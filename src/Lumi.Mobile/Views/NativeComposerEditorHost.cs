using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Platform;
using Lumi.Mobile.Services;
using StrataTheme.Controls;

namespace Lumi.Mobile.Views;

public sealed class NativeComposerEditorHost
    : NativeControlHost, IStrataComposerEditor
{
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

    internal INativeComposerEditorFactory Factory =>
        _factory ?? MobilePlatformServices.NativeComposerEditorFactory;

    internal void SetTextFromNative(string value) =>
        SetCurrentValue(TextProperty, value);

    public int CaretIndex => Factory.GetCaretIndex(this);

    public void FocusAt(int caretIndex) => Factory.FocusAt(this, caretIndex);

    public void FocusAtEnd() => Factory.FocusAtEnd(this);

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
