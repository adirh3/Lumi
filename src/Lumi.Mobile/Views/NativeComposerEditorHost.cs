using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Platform;
using Lumi.Mobile.Services;
using StrataTheme.Controls;

namespace Lumi.Mobile.Views;

public sealed class NativeComposerEditorHost
    : NativeControlHost, IStrataComposerEditor
{
    internal const double MinimumEditorHeight = 48;
    internal const double MaximumEditorHeight = 176;

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<NativeComposerEditorHost, string>(
            nameof(Text),
            "",
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> PlaceholderProperty =
        AvaloniaProperty.Register<NativeComposerEditorHost, string>(
            nameof(Placeholder),
            "");

    private INativeComposerEditorFactory? _factory;
    private double _contentHeight = MinimumEditorHeight;

    public NativeComposerEditorHost()
    {
        MinHeight = MinimumEditorHeight;
        MaxHeight = MaximumEditorHeight;
        Height = MinimumEditorHeight;
        Transitions =
        [
            new DoubleTransition
            {
                Property = HeightProperty,
                Duration = TimeSpan.FromMilliseconds(220),
                Easing = new CubicEaseOut()
            }
        ];
    }

    static NativeComposerEditorHost()
    {
        TextProperty.Changed.AddClassHandler<NativeComposerEditorHost>(
            (host, change) => host.Factory.ApplyText(
                host,
                change.GetNewValue<string>() ?? ""));
        PlaceholderProperty.Changed.AddClassHandler<NativeComposerEditorHost>(
            (host, change) => host.Factory.ApplyPlaceholder(
                host,
                change.GetNewValue<string>() ?? ""));
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    internal bool IsInputFocused { get; private set; }
    internal event Action<bool>? InputFocusChanged;

    internal void SetInputFocusFromNative(bool focused)
    {
        IsInputFocused = focused;
        InputFocusChanged?.Invoke(focused);
    }

    internal INativeComposerEditorFactory Factory =>
        _factory ?? MobilePlatformServices.NativeComposerEditorFactory;

    internal void SetTextFromNative(string value) =>
        SetCurrentValue(TextProperty, value);

    internal void SetContentHeightFromNative(double height)
    {
        if (!double.IsFinite(height))
            throw new ArgumentOutOfRangeException(nameof(height));
        var bounded = Math.Clamp(Math.Ceiling(height), MinimumEditorHeight, MaximumEditorHeight);
        if (Math.Abs(_contentHeight - bounded) < 1)
            return;
        _contentHeight = bounded;
        Height = bounded;
    }

    public int CaretIndex => Factory.GetCaretIndex(this);

    public void FocusAt(int caretIndex) => Factory.FocusAt(this, caretIndex);

    public void FocusAtEnd() => Factory.FocusAtEnd(this);

    internal void Blur() => Factory.Blur(this);

    protected override IPlatformHandle CreateNativeControlCore(
        IPlatformHandle parent)
    {
        _factory = MobilePlatformServices.NativeComposerEditorFactory;
        return _factory.Create(this, parent);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _factory?.Destroy(this, control);
        _factory = null;
        base.DestroyNativeControlCore(control);
    }
}
