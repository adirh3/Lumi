using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Lumi.Mobile.Views;

public partial class MobileSettingsView : UserControl
{
    public MobileSettingsView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
