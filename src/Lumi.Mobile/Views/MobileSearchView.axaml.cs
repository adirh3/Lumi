using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Lumi.Mobile.Views;

public partial class MobileSearchView : UserControl
{
    public MobileSearchView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Focus the field the moment the page appears so the keyboard is already up. Opening a search
    /// screen and then making the user tap the field is a wasted step on a phone.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (IsVisible)
            QueueSearchFocus();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
            QueueSearchFocus();
    }

    private void QueueSearchFocus()
    {
        // IsVisible flips before the page has completed its layout/focus-scope transition. A direct
        // Focus() at that point reports success but is immediately displaced by the control that
        // opened the page, leaving Android with no focused field and therefore no keyboard.
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsEffectivelyVisible || this.FindControl<TextBox>("SearchField") is not { } field)
                return;

            field.Focus(NavigationMethod.Unspecified);
            field.CaretIndex = field.Text?.Length ?? 0;
        }, DispatcherPriority.Input);
    }
}
