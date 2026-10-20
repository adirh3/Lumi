using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Lumi.Services;

namespace Lumi.Views;

public partial class StartupWindow : Window
{
    public StartupWindow()
    {
        AvaloniaXamlLoader.Load(this);
        AppIcon.ApplyWindowIcon(this);
    }
}
