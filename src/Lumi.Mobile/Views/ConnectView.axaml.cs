using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Lumi.Mobile.Views;

public partial class ConnectView : UserControl
{
    public ConnectView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
