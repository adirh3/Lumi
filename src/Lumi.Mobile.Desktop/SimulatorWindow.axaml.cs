using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Lumi.Mobile.Layout;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;

namespace Lumi.Mobile.Desktop;

/// <summary>
/// Development host. Renders the real mobile shell at exact device-independent pixel sizes so every
/// supported form factor — bar phone, flip cover screen, folded and unfolded foldables, tablets —
/// can be inspected and driven without an emulator.
/// </summary>
public partial class SimulatorWindow : Window
{
    private ComboBox? _devicePicker;
    private CheckBox? _showBezel;
    private TextBlock? _readout;
    private LayoutTransformControl? _scaleHost;
    private Border? _frame;
    private Border? _screen;
    private MobileShellView? _shell;

    public SimulatorWindow()
    {
        InitializeComponent();

        _devicePicker = this.FindControl<ComboBox>("DevicePicker");
        _showBezel = this.FindControl<CheckBox>("ShowBezel");
        _readout = this.FindControl<TextBlock>("LayoutReadout");
        _scaleHost = this.FindControl<LayoutTransformControl>("DeviceScaleHost");
        _frame = this.FindControl<Border>("DeviceFrame");
        _screen = this.FindControl<Border>("DeviceScreen");
        _shell = this.FindControl<MobileShellView>("Shell");

        if (_devicePicker is not null)
        {
            _devicePicker.ItemsSource = SimulatedDevice.All;
            _devicePicker.SelectionChanged += (_, _) => ApplyDevice();
        }

        if (_showBezel is not null)
        {
            _showBezel.IsCheckedChanged += (_, _) => ApplyBezel();
        }

        Opened += (_, _) =>
        {
            ApplyDevice();
            ApplyBezel();
        };
    }

    /// <summary>Selects a device by name; used by the CLI switch and by automated tests.</summary>
    public void SelectDevice(string name)
    {
        if (_devicePicker is null || string.IsNullOrWhiteSpace(name))
            return;

        var index = IndexOf(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            index = IndexOf(d => d.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
            _devicePicker.SelectedIndex = index;
        else
            Console.Error.WriteLine(
                $"Unknown device '{name}'. Known devices: {string.Join(", ", SimulatedDevice.All.Select(d => d.Name))}");
    }

    private static int IndexOf(Func<SimulatedDevice, bool> predicate)
    {
        for (var i = 0; i < SimulatedDevice.All.Count; i++)
        {
            if (predicate(SimulatedDevice.All[i]))
                return i;
        }

        return -1;
    }

    public SimulatedDevice CurrentDevice =>
        _devicePicker?.SelectedItem as SimulatedDevice ?? SimulatedDevice.All[2];

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void ApplyDevice()
    {
        if (_scaleHost is null || _frame is null || _screen is null || _shell is null)
            return;

        var device = CurrentDevice;

        _screen.Width = device.Width;
        _screen.Height = device.Height;

        _shell.Posture = device.Posture;
        _shell.HingeSize = device.HingeSize;
        _shell.HingePosition = device.HingePosition;

        // Fit the WHOLE device into the working area. Clamping only the host window while leaving the
        // emulated screen at 1:1 clipped 96-108dp from the bottom on a scaled laptop display, so QA
        // could not see the composer, keyboard edge, or lower sheets. LayoutTransform preserves the
        // device's logical DIP size (and therefore its responsive layout) while scaling only how the
        // simulator presents it.
        var hostScreen = Screens.ScreenFromWindow(this);
        var hostScaling = hostScreen?.Scaling is > 0 and var scaling ? scaling : RenderScaling;
        // Screen.WorkingArea is in physical pixels; Window.Width/Height are DIPs. Comparing them
        // directly overestimated the available height by the monitor scale (1.2x here), so the OS
        // clipped the bottom even though the simulator believed it fit.
        var maxWidth = hostScreen is null ? 1920 : hostScreen.WorkingArea.Width / hostScaling;
        var maxHeight = hostScreen is null ? 1080 : hostScreen.WorkingArea.Height / hostScaling;
        const double frameChrome = 24;       // DeviceFrame padding + border.
        const double horizontalHostChrome = 72;
        const double verticalHostChrome = 150; // Window title + simulator bar + host padding.

        var frameWidth = device.Width + frameChrome;
        var frameHeight = device.Height + frameChrome;
        var availableWidth = Math.Max(1, maxWidth - horizontalHostChrome);
        var availableHeight = Math.Max(1, maxHeight - verticalHostChrome);
        var scale = Math.Min(1, Math.Min(availableWidth / frameWidth, availableHeight / frameHeight));

        _scaleHost.LayoutTransform = new ScaleTransform(scale, scale);
        Width = Math.Min(frameWidth * scale + horizontalHostChrome, maxWidth);
        var minimumHostHeight = Math.Min(560, maxHeight);
        Height = Math.Clamp(frameHeight * scale + verticalHostChrome, minimumHostHeight, maxHeight);

        UpdateReadout();
        _shell.LayoutUpdated -= OnShellLayoutUpdated;
        _shell.LayoutUpdated += OnShellLayoutUpdated;
    }

    private void OnShellLayoutUpdated(object? sender, EventArgs e) => UpdateReadout();

    private void ApplyBezel()
    {
        if (_frame is null)
            return;

        var show = _showBezel?.IsChecked == true;
        _frame.Padding = new Thickness(show ? 10 : 0);
        _frame.BorderThickness = new Thickness(show ? 2 : 0);
        _frame.CornerRadius = new CornerRadius(show ? 36 : 0);
    }

    private void UpdateReadout()
    {
        if (_readout is null || _shell?.DataContext is not MobileShellViewModel shell)
            return;

        var layout = shell.Layout;
        var hinge = layout.HingeSize > 0 ? $" · hinge {layout.HingeSize:0.#} dp" : "";

        _readout.Text =
            $"{layout.WidthClass} · " +
            $"{(shell.CanDockDrawer ? "docked drawer" : "overlay drawer")}{hinge}";
    }
}
