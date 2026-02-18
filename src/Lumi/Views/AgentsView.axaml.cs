using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Lumi.Views;

public partial class AgentsView : UserControl
{
    public AgentsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
