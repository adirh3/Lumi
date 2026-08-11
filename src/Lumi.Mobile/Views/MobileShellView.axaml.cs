using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Lumi.Mobile.Layout;
using Lumi.Mobile.ViewModels;

namespace Lumi.Mobile.Views;

/// <summary>
/// The adaptive shell. Owns three things the shared Strata controls deliberately do not know about:
/// display size classes, OS safe-area insets, and the soft keyboard.
/// </summary>
public partial class MobileShellView : UserControl
{
    private IInsetsManager? _insets;
    private IInputPane? _inputPane;
    private TopLevel? _topLevel;
    private Thickness _safeArea;
    private double _keyboardInset;
    private double _keyboardTop = double.NaN;

    /// <summary>Posture pushed in by the host (Android hinge info, or the desktop simulator).</summary>
    public static readonly StyledProperty<FoldPosture> PostureProperty =
        AvaloniaProperty.Register<MobileShellView, FoldPosture>(nameof(Posture));

    public static readonly StyledProperty<double> HingeSizeProperty =
        AvaloniaProperty.Register<MobileShellView, double>(nameof(HingeSize));

    public static readonly StyledProperty<double> HingePositionProperty =
        AvaloniaProperty.Register<MobileShellView, double>(nameof(HingePosition));

    static MobileShellView()
    {
        PostureProperty.Changed.AddClassHandler<MobileShellView>((view, _) => view.PushLayout());
        HingeSizeProperty.Changed.AddClassHandler<MobileShellView>((view, _) => view.PushLayout());
        HingePositionProperty.Changed.AddClassHandler<MobileShellView>((view, _) => view.PushLayout());
    }

    public MobileShellView()
    {
        InitializeComponent();
    }

    public FoldPosture Posture
    {
        get => GetValue(PostureProperty);
        set => SetValue(PostureProperty, value);
    }

    public double HingeSize
    {
        get => GetValue(HingeSizeProperty);
        set => SetValue(HingeSizeProperty, value);
    }

    public double HingePosition
    {
        get => GetValue(HingePositionProperty);
        set => SetValue(HingePositionProperty, value);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is null)
            return;

        // Avalonia sets Padding on the TopLevel's content child itself when AutoSafeAreaPadding is
        // on (it defaults to true). We need the insets split across our own surfaces — the drawer
        // and scrim must run edge to edge under the bars while only the content is inset — so we
        // take ownership. Leaving the automatic one on meant two mechanisms writing the same
        // property, ours silently winning on priority, and no way to tell which value was live.
        TopLevel.SetAutoSafeAreaPadding(this, false);

        _insets = _topLevel.InsetsManager;
        if (_insets is not null)
        {
            // Draw behind the status/navigation bars and pad manually: that is what makes the
            // transcript feel like it belongs to the device rather than sitting in a letterbox.
            _insets.DisplayEdgeToEdgePreference = true;
            _insets.SafeAreaChanged += OnSafeAreaChanged;
            _safeArea = _insets.SafeAreaPadding;
        }

        _inputPane = _topLevel.InputPane;
        if (_inputPane is not null)
            _inputPane.StateChanged += OnInputPaneStateChanged;

        // RenderScaling starts at 1 and only becomes the real display density once the surface is
        // created, and the Android backend divides the raw insets by it. A safe-area report that
        // lands before that is therefore in the wrong scale, and nothing re-sends it — so re-read
        // on every scaling change, which is what Avalonia's own PageNavigationHost does.
        _topLevel.ScalingChanged += OnScalingChanged;
        _topLevel.BackRequested += OnBackRequested;

        ApplyInsets();
        PushLayout();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_insets is not null)
            _insets.SafeAreaChanged -= OnSafeAreaChanged;

        if (_inputPane is not null)
            _inputPane.StateChanged -= OnInputPaneStateChanged;

        if (_topLevel is not null)
        {
            _topLevel.BackRequested -= OnBackRequested;
            _topLevel.ScalingChanged -= OnScalingChanged;
        }

        _insets = null;
        _inputPane = null;
        _topLevel = null;

        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        // RenderScaling is only correct once the surface exists, and ApplyInsets depends on it —
        // the very first safe-area report arrives before that, at a placeholder scale of 1.
        ApplyInsets();
        PushLayout();
    }

    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e)
    {
        _safeArea = e.SafeAreaPadding;
        ApplyInsets();
    }

    /// <summary>
    /// The single point where OS insets enter the view. Exposed so tests can stand in for the
    /// platform, which reports nothing in a headless run.
    /// </summary>
    internal void ApplyPlatformInsets(Thickness safeArea, double keyboardInset = 0)
    {
        _safeArea = safeArea;
        _keyboardInset = keyboardInset;
        var fullHeight = DataContext is MobileShellViewModel shell && shell.Layout.Height > 0
            ? shell.Layout.Height
            : _topLevel?.ClientSize.Height ?? Bounds.Height;
        _keyboardTop = keyboardInset > 0 ? Math.Max(0, fullHeight - keyboardInset) : double.NaN;
        ApplyInsets();
    }

    /// <summary>Clears transient IME geometry when the platform backgrounds the app.</summary>
    public void NotifyApplicationDeactivated()
    {
        _keyboardInset = 0;
        _keyboardTop = double.NaN;
        ApplyInsets();
        if (DataContext is MobileShellViewModel shell)
            _ = shell.NotifyApplicationDeactivatedAsync();
    }

    public void NotifyApplicationActivated()
    {
        if (DataContext is MobileShellViewModel shell)
            _ = shell.NotifyApplicationActivatedAsync();
    }

    private void OnScalingChanged(object? sender, EventArgs e)
    {
        if (_insets is not null)
            _safeArea = _insets.SafeAreaPadding;

        ApplyInsets();
    }

    private void OnInputPaneStateChanged(object? sender, InputPaneStateEventArgs e)
    {
        // Lift the composer above the keyboard.
        //
        // The rect is NOT a height measured from the bottom: it is a client-space rectangle whose
        // Top already accounts for the navigation bar (the reported height is the IME inset MINUS
        // the navbar, because the navbar is separately part of the safe area). Measuring from the
        // bottom of the client area to that Top is therefore the whole occluded strip, and matches
        // the pattern in Avalonia's own SafeAreaDemo. Treating EndRect.Height as the inset — which
        // is what this did — dropped the navigation bar's worth of padding and left the composer
        // partly under the keyboard.
        var rect = e.NewState == InputPaneState.Open ? e.EndRect : default;
        _keyboardInset = rect.Height > 0 && _topLevel is { } top
            ? Math.Max(0, top.ClientSize.Height - rect.Top)
            : 0;
        _keyboardTop = _keyboardInset > 0 ? rect.Top : double.NaN;

        ApplyInsets();
    }

    private void ApplyInsets()
    {
        // The Android InsetsManager already divides by RenderScaling — SafeAreaPadding, OccludedRect
        // and EndRect are all in device-independent units. This used to divide by RenderScaling a
        // SECOND time, which on a 2.625x device left only 38% of the real inset: the top bar rode up
        // under the status bar and the composer sat under the keyboard. Apply the values as given.
        var safe = _safeArea;

        var fullHeight = _topLevel?.ClientSize.Height
                         ?? (DataContext as MobileShellViewModel)?.Layout.Height
                         ?? Bounds.Height;
        var activeHeight = DataContext is MobileShellViewModel shellWithLayout
            ? shellWithLayout.UsableContentHeight
            : fullHeight;
        if (activeHeight <= 0)
            activeHeight = fullHeight;

        // In tabletop posture the app occupies the upper pane. An IME wholly inside the lower pane
        // does not occlude that content, and neither does the bottom system bar.
        var keyboardOverlap = double.IsNaN(_keyboardTop)
            ? Math.Min(_keyboardInset, activeHeight)
            : Math.Max(0, activeHeight - _keyboardTop);
        var reachesWindowBottom = activeHeight >= fullHeight - 0.5;
        var safeBottom = reachesWindowBottom ? safe.Bottom : 0;
        var bottom = Math.Max(safeBottom, keyboardOverlap);

        // Published rather than applied as Padding here. Padding the shell inset every surface —
        // drawer, top bar, conversation background — so the app sat in a letterbox with a dead band
        // under the status bar. The views take these insets on their own CONTENT while their
        // backgrounds run to the edges, which is what makes it look full-screen.
        var target = new Thickness(safe.Left, safe.Top, safe.Right, bottom);
        if (DataContext is MobileShellViewModel shell)
        {
            shell.SafeArea = target;
            shell.IsKeyboardOpen = keyboardOverlap > 1;
        }

        if (_lastInsets == target)
            return;

        _lastInsets = target;
        PushLayout();
    }

    private Thickness _lastInsets = new(double.NaN);

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        // The Android system back gesture must dismiss our own overlays before leaving the app.
        if (DataContext is MobileShellViewModel { CanGoBack: true } shell)
        {
            shell.GoBackCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void PushLayout()
    {
        if (DataContext is not MobileShellViewModel shell)
            return;

        // Size classes are decided on the space content actually gets, which is the window minus
        // the safe area — not the raw window. Reading Padding here was correct only while the shell
        // itself was padded; the insets now live on the view model.
        var insets = _lastInsets;
        if (double.IsNaN(insets.Left))
            insets = default;

        var width = Bounds.Width - insets.Left - insets.Right;
        // Surfaces stay edge-to-edge; safe areas are applied by their own content. Width loses side
        // cutouts for adaptive columns, but height must remain the full window or the root is
        // letterboxed by the status/navigation insets.
        var height = Bounds.Height;

        shell.UpdateLayout(width, height, Posture, HingeSize, HingePosition);
        ApplyInsets();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // A fresh view model has no insets yet; re-publish whatever the OS already told us.
        ApplyInsets();
        PushLayout();
    }
}
