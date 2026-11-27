using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Lumi.Views;

internal sealed class WindowDisplayLossRecovery : IDisposable
{
    private readonly Window _window;
    private readonly Screens? _screens;
    private readonly Func<IReadOnlyList<Display>> _connectedScreens;
    private readonly Func<IntPtr> _windowScreen;
    private readonly nint _hwnd;
    private readonly DispatcherTimer _settle = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(300)
    };
    private IntPtr _host;
    private bool _hasMultipleDisplays;
    private IntPtr _lostHost;

    internal readonly record struct Display(IntPtr Handle, PixelRect WorkArea, bool IsPrimary);

    public WindowDisplayLossRecovery(Window window, Screens screens)
        : this(window,
            () => screens.All.Select(screen => new Display(ScreenHandle(screen),
                screen.WorkingArea, screen.IsPrimary)).ToArray(),
            () => ScreenHandle(screens.ScreenFromWindow(window)))
    {
        _screens = screens;
        screens.Changed += OnScreensChanged;
    }

    internal WindowDisplayLossRecovery(Window window, Func<IReadOnlyList<Display>> connectedScreens, Func<IntPtr> windowScreen)
    {
        _window = window;
        _connectedScreens = connectedScreens;
        _windowScreen = windowScreen;
        _hasMultipleDisplays = connectedScreens().Count > 1;
        _host = windowScreen();
        _settle.Tick += OnSettled;
        window.PositionChanged += OnPositionChanged;
        window.Resized += OnResized;
        window.PropertyChanged += OnWindowPropertyChanged;
        window.Closed += OnClosed;
        if (OperatingSystem.IsWindows() && window.TryGetPlatformHandle() is { HandleDescriptor: "HWND", Handle: var hwnd })
        {
            _hwnd = hwnd;
            Win32Properties.AddWndProcHookCallback(window, OnNativeMessage);
        }
    }

    private void OnScreensChanged(object? sender, EventArgs e) => DisplayConfigurationChanged();

    private static IntPtr ScreenHandle(Screen? screen) => screen?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    internal void DisplayConfigurationChanged()
    {
        ObserveTopology(_connectedScreens());
        if (_window.IsVisible)
            WaitForPlacement();
    }

    private void ObserveTopology(IReadOnlyList<Display> screens)
    {
        // The driver can briefly report no displays while switching configurations.
        if (screens.Count == 0)
            return;

        if (_lostHost != IntPtr.Zero && screens.Any(screen => screen.Handle == _lostHost))
            _lostHost = IntPtr.Zero;
        if (_lostHost == IntPtr.Zero && _host != IntPtr.Zero
            && !screens.Any(screen => screen.Handle == _host))
            _lostHost = _host;
        _hasMultipleDisplays = screens.Count > 1;
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e) => OnGeometryChanged();

    private void OnResized(object? sender, WindowResizedEventArgs e)
    {
        if (e.Reason == WindowResizeReason.User)
            OnUserMoveOrResize();
        OnGeometryChanged();
    }

    private nint OnNativeMessage(nint hwnd, uint message, nint wParam, nint lParam, ref bool handled)
    {
        // WM_MOVING is an interactive drag. WM_MOVE also occurs when Windows relocates
        // the window after undocking, and must not cancel that recovery.
        if (message == 0x0216)
            OnUserMoveOrResize();
        return 0;
    }

    private void OnUserMoveOrResize()
    {
        if (_lostHost != IntPtr.Zero)
        {
            _lostHost = IntPtr.Zero;
            _host = _windowScreen();
        }
    }

    private void OnGeometryChanged()
    {
        // Windows can move the HWND to its new display before the screen-change notification.
        if (_window.IsVisible && _window.WindowState != WindowState.Minimized
            && (_lostHost != IntPtr.Zero || (_hasMultipleDisplays && _windowScreen() != _host)))
            WaitForPlacement();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty && !_window.IsVisible)
        {
            _settle.Stop();
        }
        else if (_window.IsVisible && (e.Property == Visual.IsVisibleProperty || e.Property == Window.WindowStateProperty))
        {
            WaitForPlacement();
        }
    }

    private void WaitForPlacement()
    {
        _settle.Stop();
        _settle.Start();
    }

    private void OnSettled(object? sender, EventArgs e)
    {
        _settle.Stop();
        var screens = _connectedScreens();
        if (!_window.IsVisible || screens.Count == 0)
            return;
        ObserveTopology(screens);

        // Keep a pending check until normal, but otherwise update the host even when minimized.
        if (_lostHost != IntPtr.Zero && _window.WindowState != WindowState.Normal)
            return;

        var host = _windowScreen();
        if (host != IntPtr.Zero || _window.WindowState != WindowState.Minimized)
            _host = host;
        if (_lostHost == IntPtr.Zero)
            return;

        _lostHost = IntPtr.Zero;
        var target = host == IntPtr.Zero ? default : screens.FirstOrDefault(screen => screen.Handle == host);
        if (target.WorkArea == default)
            target = screens.FirstOrDefault(screen => screen.IsPrimary, screens[0]);
        FitNormalWindow(target.WorkArea, screens);
        _host = _windowScreen();
    }

    private void FitNormalWindow(PixelRect workArea, IReadOnlyList<Display> screens)
    {
        var scaling = _window.DesktopScaling;
        var client = _window.ClientSize;
        if (workArea.Width <= 0 || workArea.Height <= 0 || !double.IsFinite(scaling) || scaling <= 0
            || client.Width <= 0 || client.Height <= 0)
            return;

        // Test actual client pixels: rounding back up from fractional DIPs can invent a
        // clipped pixel, and the frame includes invisible resize margins.
        PixelRect clientBounds;
        if (OperatingSystem.IsWindows() && _hwnd != 0)
        {
            if (!WindowChromeInterop.TryGetBounds(_hwnd, out _, out clientBounds))
                return;
        }
        else
            clientBounds = new PixelRect(_window.PointToScreen(default), PixelSize.FromSize(client, scaling));
        if (WindowPlacement.IsFullyVisible(clientBounds, screens.Select(screen => screen.WorkArea)))
            return;

        var frame = _window.FrameSize ?? client;
        var frameWidth = Math.Max(0, frame.Width - client.Width);
        var frameHeight = Math.Max(0, frame.Height - client.Height);
        var width = Math.Min(client.Width, Math.Max(_window.MinWidth, workArea.Width / scaling - frameWidth));
        var height = Math.Min(client.Height, Math.Max(_window.MinHeight, workArea.Height / scaling - frameHeight));
        var frameSize = PixelSize.FromSize(new Size(width + frameWidth, height + frameHeight), scaling);
        var position = _window.Position;
        var fit = WindowPlacement.FitPosition(position, frameSize, workArea);
        if (width < client.Width)
            _window.Width = width;
        if (height < client.Height)
            _window.Height = height;
        if (fit != position)
            _window.Position = fit;
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        _settle.Stop();
        _settle.Tick -= OnSettled;
        if (_screens is not null)
            _screens.Changed -= OnScreensChanged;
        if (_hwnd != 0)
            Win32Properties.RemoveWndProcHookCallback(_window, OnNativeMessage);
        _window.PositionChanged -= OnPositionChanged;
        _window.Resized -= OnResized;
        _window.PropertyChanged -= OnWindowPropertyChanged;
        _window.Closed -= OnClosed;
    }
}
