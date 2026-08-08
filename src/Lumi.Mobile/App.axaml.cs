using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;

namespace Lumi.Mobile;

/// <summary>
/// The shared application object. Both the desktop test head and the phone heads use it: the only
/// difference is which lifetime the platform hands us.
/// </summary>
public partial class App : Application
{
    private MobileShellViewModel? _shell;

    /// <summary>Set by a head before <see cref="Avalonia.Application.Initialize"/> to inject a preconfigured shell (tests, simulators).</summary>
    public static Func<MobileShellViewModel>? ShellFactory { get; set; }

    /// <summary>Lets a desktop head supply its own window chrome (the form-factor simulator uses this).</summary>
    public static Func<Window>? DesktopWindowFactory { get; set; }

    /// <summary>The live shell, available to heads that need to drive it (form-factor simulator, tests).</summary>
    public MobileShellViewModel? Shell => _shell;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        _shell = ShellFactory?.Invoke() ?? new MobileShellViewModel();
        ApplyTheme(_shell.Theme);
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MobileShellViewModel.Theme))
                ApplyTheme(_shell.Theme);
        };

        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                var window = DesktopWindowFactory?.Invoke() ?? new Window
                {
                    Title = "Lumi",
                    Width = 390,
                    Height = 844,
                    Content = new MobileShellView()
                };
                window.DataContext = _shell;
                desktop.MainWindow = window;
                desktop.ShutdownRequested += (_, _) => _ = _shell.DisposeAsync();
                break;

            case ISingleViewApplicationLifetime singleView:
                singleView.MainView = new MobileShellView { DataContext = _shell };
                break;
        }

        _ = _shell.StartAsync();

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Maps the user's preference onto Avalonia's variant.
    ///
    /// <para><see cref="ThemeVariant.Default"/> is how you follow the OS: Avalonia resolves it
    /// against the platform's own setting and re-resolves when that changes, so "System" needs no
    /// polling and no platform-specific code.</para>
    ///
    /// <para>Static and public so it can be verified without standing up the application lifetime —
    /// the headless test harness deliberately skips initialization, so the instance wiring that
    /// calls this is not exercised there.</para>
    /// </summary>
    public static ThemeVariant VariantFor(MobileShellViewModel.ThemePreference preference) =>
        preference switch
        {
            MobileShellViewModel.ThemePreference.Light => ThemeVariant.Light,
            MobileShellViewModel.ThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

    private void ApplyTheme(MobileShellViewModel.ThemePreference preference) =>
        RequestedThemeVariant = VariantFor(preference);
}
