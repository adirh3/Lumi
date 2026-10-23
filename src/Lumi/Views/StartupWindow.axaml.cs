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

    /// <summary>Says what Lumi is doing instead of starting, such as recovering chats.</summary>
    public void ShowStatus(string text)
    {
        if (this.FindControl<TextBlock>("StartupStatus") is { } status)
            status.Text = text;
    }
}
