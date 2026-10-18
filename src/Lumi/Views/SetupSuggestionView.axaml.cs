using Avalonia;
using Avalonia.Controls;

namespace Lumi.Views;

/// <summary>
/// A well-known setup as one compact row. Where it is a suggestion (the new-chat screen, welcome) it can be
/// dismissed; in Discover, which lists every setup, <see cref="CanDismiss"/> is off.
/// </summary>
public partial class SetupSuggestionView : UserControl
{
    public static readonly StyledProperty<bool> CanDismissProperty =
        AvaloniaProperty.Register<SetupSuggestionView, bool>(nameof(CanDismiss), defaultValue: true);

    public bool CanDismiss
    {
        get => GetValue(CanDismissProperty);
        set => SetValue(CanDismissProperty, value);
    }

    public SetupSuggestionView()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CanDismissProperty && SetupSuggestionDismiss is { } dismiss)
            dismiss.IsVisible = CanDismiss;
    }
}
