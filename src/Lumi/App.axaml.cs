using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;

namespace Lumi;

public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private Window? _mainWindow;
    private GlobalHotkeyService? _hotkeyService;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dataStore = new DataStore();

            // Initialize localization before creating any UI
            Loc.Load(dataStore.Data.Settings.Language);

            var copilotService = new CopilotService();
            var vm = new MainViewModel(dataStore, copilotService);

            // Apply saved theme before showing the window
            RequestedThemeVariant = dataStore.Data.Settings.IsDarkTheme
                ? ThemeVariant.Dark
                : ThemeVariant.Light;

            // Apply saved density
            MainWindow.ApplyDensityStatic(dataStore.Data.Settings.IsCompactDensity);

            // Sync launch-at-startup registry
            MainWindow.ApplyLaunchAtStartup(dataStore.Data.Settings.LaunchAtStartup);

            var window = new MainWindow { DataContext = vm };
            _mainWindow = window;

            // Apply RTL for right-to-left languages
            if (Loc.IsRightToLeft)
                window.FlowDirection = Avalonia.Media.FlowDirection.RightToLeft;

            // Apply StartMinimized
            if (dataStore.Data.Settings.StartMinimized)
                window.WindowState = Avalonia.Controls.WindowState.Minimized;

            // Set up tray icon if enabled
            if (dataStore.Data.Settings.MinimizeToTray)
                SetupTrayIcon(true);

            // Set up global hotkey
            _hotkeyService = new GlobalHotkeyService();
            _hotkeyService.HotkeyPressed += () => Dispatcher.UIThread.Post(ToggleMainWindow);
            window.Opened += (_, _) =>
            {
                _hotkeyService.Attach(window);
                if (!string.IsNullOrWhiteSpace(dataStore.Data.Settings.GlobalHotkey))
                    _hotkeyService.Register(dataStore.Data.Settings.GlobalHotkey);
            };

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Create or remove the system tray icon.</summary>
    public void SetupTrayIcon(bool enable)
    {
        if (enable && _trayIcon is null)
        {
            var uri = new Uri("avares://Lumi/Assets/lumi-icon.png");
            var icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(uri));

            var showItem = new NativeMenuItem(Loc.Tray_Show);
            showItem.Click += (_, _) => ShowMainWindow();

            var exitItem = new NativeMenuItem(Loc.Tray_Exit);
            exitItem.Click += (_, _) =>
            {
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.Shutdown();
            };

            var menu = new NativeMenu();
            menu.Items.Add(showItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exitItem);

            _trayIcon = new TrayIcon
            {
                Icon = icon,
                ToolTipText = Loc.App_Name,
                Menu = menu,
                IsVisible = true
            };
            _trayIcon.Clicked += (_, _) => ShowMainWindow();

            var icons = new TrayIcons { _trayIcon };
            TrayIcon.SetIcons(this, icons);
        }
        else if (!enable && _trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
            TrayIcon.SetIcons(this, new TrayIcons());

            // Ensure window is visible when disabling tray
            ShowMainWindow();
        }
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.Show();
        _mainWindow.ShowInTaskbar = true;
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    /// <summary>Toggle window visibility. Called by the global hotkey.</summary>
    public void ToggleMainWindow()
    {
        if (_mainWindow is null) return;

        // If visible, focused, and not minimized → hide/minimize
        if (_mainWindow.IsVisible
            && _mainWindow.WindowState != WindowState.Minimized
            && _mainWindow.IsActive)
        {
            if (_mainWindow.DataContext is ViewModels.MainViewModel vm && vm.SettingsVM.MinimizeToTray)
            {
                _mainWindow.ShowInTaskbar = false;
                _mainWindow.Hide();
            }
            else
            {
                _mainWindow.WindowState = WindowState.Minimized;
            }
        }
        else
        {
            ShowMainWindow();
        }
    }

    /// <summary>Update the global hotkey registration. Called from SettingsViewModel.</summary>
    public void UpdateGlobalHotkey(string hotkeyString)
    {
        if (_hotkeyService is null) return;
        if (string.IsNullOrWhiteSpace(hotkeyString))
            _hotkeyService.Unregister();
        else
            _hotkeyService.Register(hotkeyString);
    }
}
