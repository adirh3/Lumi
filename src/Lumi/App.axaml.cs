using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;

namespace Lumi;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dataStore = new DataStore();
            var copilotService = new CopilotService();
            var vm = new MainViewModel(dataStore, copilotService);

            // Apply saved theme before showing the window
            RequestedThemeVariant = dataStore.Data.Settings.IsDarkTheme
                ? ThemeVariant.Dark
                : ThemeVariant.Light;

            desktop.MainWindow = new MainWindow { DataContext = vm };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
