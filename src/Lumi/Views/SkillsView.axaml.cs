using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Lumi.Views;

public partial class SkillsView : UserControl
{
    public SkillsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
