using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;

namespace Lumi;

public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private GlobalHotkeyService? _hotkeyService;
    private DataStore? _dataStore;
    private CopilotService? _copilotService;
    private UpdateService? _updateService;
    private BackgroundJobService? _backgroundJobService;
    private ChatSurfaceRegistry? _chatSurfaceRegistry;
    private ChatSessionStore? _chatSessionStore;
    private GlobalSearchService? _globalSearchService;
    private readonly List<MainWindow> _windows = [];
    private int _secondaryWindowSequence;
    private MainViewModel? _mainViewModel;
    private readonly Dictionary<Guid, ChatWindow> _chatWindows = [];
    private bool _isShuttingDown;
#if DEBUG
    private LumiDebugBridge? _debugBridge;
#endif

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dataStore = new DataStore();
            _dataStore = dataStore;

            // Initialize localization before creating any UI
            Loc.Load(dataStore.Data.Settings.Language);

            var copilotService = new CopilotService();
            _copilotService = copilotService;
            var updateService = new UpdateService();
            _updateService = updateService;
            updateService.Initialize();
            _chatSurfaceRegistry = new ChatSurfaceRegistry();
            _globalSearchService = new GlobalSearchService(
                () => dataStore.Data,
                dataStore.GetChatSearchSnapshot,
                releaseChatSnapshot: dataStore.EvictChatSearchSnapshot,
                chatFileTimestampProvider: dataStore.GetChatFileTimestamp);
            _chatSessionStore = new ChatSessionStore(dataStore, copilotService, _chatSurfaceRegistry, _globalSearchService);
            var vm = CreateMainViewModel(
                forceOnboarding: Program.ForceOnboarding,
                startBackgroundJobs: true
#if DEBUG
                , openAgentDebugHarness: Program.OpenAgentDebugHarness,
                skipOnboarding: Program.SkipOnboarding
#endif
            );
            _backgroundJobService = vm.BackgroundJobService;
            _mainViewModel = vm;

            // Save data and dispose CopilotService on app shutdown.
            // The window is already hidden at this point so nothing blocks the user.
            // Task.Run avoids deadlocking the UI thread if _writeLock is held by
            // an in-flight fire-and-forget save that needs the dispatcher to complete.
            desktop.ShutdownRequested += (_, _) =>
            {
                _isShuttingDown = true;
                foreach (var chatWindow in _chatWindows.Values.ToList())
                    chatWindow.Close();
                _chatWindows.Clear();
                updateService.Dispose();
                var viewModels = _windows
                    .Select(static window => window.DataContext)
                    .OfType<MainViewModel>();
                if (_mainWindow?.DataContext is MainViewModel primaryVm)
                    viewModels = viewModels.Append(primaryVm);

                foreach (var windowVm in viewModels.Distinct())
                {
                    windowVm.Dispose();
                }
                _chatSessionStore?.Dispose();
                _chatSurfaceRegistry?.Dispose();

                Task.Run(async () =>
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await dataStore.SaveAsync(cts.Token);
                    }
                    catch { }

                    try
                    {
                        await copilotService.DisposeAsync();
                    }
                    catch { }

                    try
                    {
                        await McpProxyRuntime.Shared.DisposeAsync();
                    }
                    catch { }

#if DEBUG
                    try
                    {
                        if (_debugBridge is not null)
                            await _debugBridge.DisposeAsync();
                    }
                    catch { }
#endif
                }).GetAwaiter().GetResult();
            };

            // Apply saved theme before showing the window
            RequestedThemeVariant = dataStore.Data.Settings.IsDarkTheme
                ? ThemeVariant.Dark
                : ThemeVariant.Light;

            // Apply saved density
            MainWindow.ApplyDensityStatic(dataStore.Data.Settings.IsCompactDensity);

            var window = CreateWindow(vm, isPrimary: true);
            _mainWindow = window;

#if DEBUG
            _debugBridge = new LumiDebugBridge(dataStore, vm);
            _debugBridge.Start();
#endif

            // Apply RTL for right-to-left languages
            if (Loc.IsRightToLeft)
                window.FlowDirection = Avalonia.Media.FlowDirection.RightToLeft;

            // Apply StartMinimized
            if (dataStore.Data.Settings.StartMinimized)
                window.WindowState = Avalonia.Controls.WindowState.Minimized;

            var launchAtStartup = dataStore.Data.Settings.LaunchAtStartup;
            var minimizeToTray = dataStore.Data.Settings.MinimizeToTray;
            var globalHotkey = dataStore.Data.Settings.GlobalHotkey;

            window.Opened += (_, _) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    // Defer non-critical setup until first frame is shown.
                    MainWindow.ApplyLaunchAtStartup(launchAtStartup);

                    if (minimizeToTray)
                        SetupTrayIcon(true);

                    _hotkeyService ??= new GlobalHotkeyService();
                    _hotkeyService.HotkeyPressed -= OnGlobalHotkeyPressed;
                    _hotkeyService.HotkeyPressed += OnGlobalHotkeyPressed;
                    _hotkeyService.Attach(window);

                    if (!string.IsNullOrWhiteSpace(globalHotkey))
                        _hotkeyService.Register(globalHotkey);

                    // Start checking for updates in background
                    updateService.StartPeriodicChecks();
                }, DispatcherPriority.Background);

#if DEBUG
                if (Program.UiHarnessOptions is { Enabled: true } uiHarnessOptions)
                    StartUiResponsivenessHarness(desktop, vm, dataStore, uiHarnessOptions);

                if (Program.AnimationLifecycleLeakReproEnabled)
                    AnimationLifecycleLeakRepro.Start(desktop);
#endif
            };

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

#if DEBUG
    private void StartUiResponsivenessHarness(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainViewModel vm,
        DataStore dataStore,
        UiPerf.UiHarnessOptions options)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Let the first frame and lazy view realization settle before measuring.
                await Task.Delay(900);
                var harness = new UiPerf.UiResponsivenessHarness(
                    vm,
                    dataStore,
                    options,
                    requestShutdown: exitCode => Dispatcher.UIThread.Post(() =>
                    {
                        try { Environment.ExitCode = exitCode; desktop.Shutdown(exitCode); }
                        catch { /* already shutting down */ }
                    }));
                await harness.RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ui-perf] Harness host failed: " + ex);
                Dispatcher.UIThread.Post(() =>
                {
                    try { Environment.ExitCode = 1; desktop.Shutdown(1); }
                    catch { /* already shutting down */ }
                });
            }
        });
    }
#endif

    private MainViewModel CreateMainViewModel(
        bool forceOnboarding = false,
        bool startBackgroundJobs = false
#if DEBUG
        , bool openAgentDebugHarness = false,
        bool skipOnboarding = false
#endif
        )
    {
        if (_dataStore is null || _copilotService is null || _updateService is null)
            throw new InvalidOperationException("Lumi services are not initialized.");
        if (_chatSurfaceRegistry is null)
            throw new InvalidOperationException("Lumi chat surface registry is not initialized.");
        if (_chatSessionStore is null)
            throw new InvalidOperationException("Lumi chat session store is not initialized.");

        // Secondary windows share the primary scheduler so background jobs have a single runner.
        return new MainViewModel(
            _dataStore,
            _copilotService,
            _updateService,
            forceOnboarding,
            _backgroundJobService,
            startBackgroundJobs,
            _chatSurfaceRegistry,
            _chatSessionStore,
            _globalSearchService
#if DEBUG
            , openAgentDebugHarness,
            skipOnboarding
#endif
        );
    }

    private MainWindow CreateWindow(MainViewModel vm, bool isPrimary)
    {
        var window = new MainWindow
        {
            DataContext = vm,
            IsPrimaryWindow = isPrimary,
            SecondaryWindowCascadeIndex = isPrimary ? 0 : ++_secondaryWindowSequence
        };

        if (Loc.IsRightToLeft)
            window.FlowDirection = Avalonia.Media.FlowDirection.RightToLeft;

        AttachWindowManagementHandlers(vm);

        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            if (window.DataContext is MainViewModel windowVm)
            {
                DetachWindowManagementHandlers(windowVm);
                if (!isPrimary)
                    windowVm.Dispose();
            }
        };

        _windows.Add(window);
        return window;
    }

    private void AttachWindowManagementHandlers(MainViewModel vm)
    {
        vm.OpenChatWindowRequested -= OpenChatWindow;
        vm.OpenChatWindowRequested += OpenChatWindow;
        vm.DetachedChatFocusRequested -= FocusDetachedChatWindow;
        vm.DetachedChatFocusRequested += FocusDetachedChatWindow;
        vm.ChatDeleted -= CloseChatWindow;
        vm.ChatDeleted += CloseChatWindow;
    }

    private void DetachWindowManagementHandlers(MainViewModel vm)
    {
        vm.OpenChatWindowRequested -= OpenChatWindow;
        vm.DetachedChatFocusRequested -= FocusDetachedChatWindow;
        vm.ChatDeleted -= CloseChatWindow;
    }

    private void OnGlobalHotkeyPressed()
    {
        Dispatcher.UIThread.Post(ToggleMainWindow);
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

            var newWindowItem = new NativeMenuItem(Loc.Tray_NewWindow);
            newWindowItem.Click += (_, _) => OpenNewWindow();

            var exitItem = new NativeMenuItem(Loc.Tray_Exit);
            exitItem.Click += (_, _) =>
            {
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.Shutdown();
            };

            var menu = new NativeMenu();
            menu.Items.Add(showItem);
            menu.Items.Add(newWindowItem);
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

        // Focus composer when showing via hotkey/tray and chat tab is active
        if (_mainWindow.DataContext is ViewModels.MainViewModel vm && vm.SelectedNavIndex == 0)
        {
            var chatView = _mainWindow.FindControl<Views.ChatView>("PageChat");
            Avalonia.Threading.Dispatcher.UIThread.Post(() => chatView?.FocusComposer(),
                Avalonia.Threading.DispatcherPriority.Input);
        }
    }

    public void OpenNewWindow(Guid? chatId = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OpenNewWindow(chatId));
            return;
        }

        var window = CreateWindow(CreateMainViewModel(
#if DEBUG
            skipOnboarding: Program.SkipOnboarding
#endif
        ), isPrimary: false);
        if (_mainWindow is { IsVisible: true } owner)
            window.SecondaryWindowAnchorPosition = owner.Position;

        window.Show();

        window.Activate();

        if (chatId is Guid targetChatId && window.DataContext is MainViewModel vm)
            _ = vm.OpenChatByIdAsync(targetChatId);
    }

    public void ShowMainWindow(Guid? chatId)
    {
        ShowMainWindow();

        if (chatId is not Guid targetChatId)
            return;

        if (_mainWindow?.DataContext is MainViewModel vm)
            _ = vm.OpenChatByIdAsync(targetChatId);
    }

    private void OpenChatWindow(DetachedChatWindowRequest request)
    {
        Dispatcher.UIThread.Post(() => _ = OpenChatWindowAsync(request));
    }

    private async Task OpenChatWindowAsync(DetachedChatWindowRequest request)
    {
        if (_dataStore is null || _copilotService is null)
        {
            DisposeUnopenedChatWindowRequest(request);
            return;
        }

        var chat = request.Chat;
        var initialChatId = chat?.Id;
        if (initialChatId is Guid chatId && _chatWindows.TryGetValue(chatId, out var existingWindow))
        {
            FocusChatWindow(existingWindow);
            DisposeUnopenedChatWindowRequest(request);
            return;
        }

        var windowVm = request.WindowVM;
        var chatVm = windowVm.ChatVM;

        chatVm.ChatUpdated += OnDetachedChatUpdated;
        chatVm.ChatTitleChanged += OnDetachedChatTitleChanged;
        chatVm.DefaultModelSelectionChanged += OnDetachedDefaultModelSelectionChanged;

        void OnDetachedOpenChatRequested(Guid requestedChatId) => ShowMainWindow(requestedChatId);
        chatVm.OpenChatRequested += OnDetachedOpenChatRequested;

        Guid? trackedChatId = initialChatId;
        ChatWindow? window = null;
        void TrackCurrentChat()
        {
            if (window is null || chatVm.CurrentChat is not { } currentChat || trackedChatId == currentChat.Id)
                return;

            if (trackedChatId is Guid previousChatId
                && _chatWindows.TryGetValue(previousChatId, out var trackedWindow)
                && ReferenceEquals(trackedWindow, window))
                _chatWindows.Remove(previousChatId);

            trackedChatId = currentChat.Id;
            _chatWindows[currentChat.Id] = window;
        }

        void OnDetachedCurrentChatChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(ChatViewModel.CurrentChat))
                TrackCurrentChat();
        }

        chatVm.PropertyChanged += OnDetachedCurrentChatChanged;

        window = new ChatWindow(_dataStore, windowVm);
        if (trackedChatId is Guid trackedId)
            _chatWindows[trackedId] = window;

        window.Closed += (_, _) =>
        {
            if (trackedChatId is Guid closedChatId
                && _chatWindows.TryGetValue(closedChatId, out var trackedWindow)
                && ReferenceEquals(trackedWindow, window))
                _chatWindows.Remove(closedChatId);

            chatVm.PropertyChanged -= OnDetachedCurrentChatChanged;
            chatVm.ChatUpdated -= OnDetachedChatUpdated;
            chatVm.ChatTitleChanged -= OnDetachedChatTitleChanged;
            chatVm.DefaultModelSelectionChanged -= OnDetachedDefaultModelSelectionChanged;
            chatVm.OpenChatRequested -= OnDetachedOpenChatRequested;
            windowVm.Dispose();
            request.ReleaseSurface();
        };

        if (Loc.IsRightToLeft)
            window.FlowDirection = Avalonia.Media.FlowDirection.RightToLeft;

        window.Show();

        if (chat is null)
        {
            window.FocusComposer();
            return;
        }

        try
        {
            if (chatVm.CurrentChat?.Id != chat.Id)
                await chatVm.LoadChatAsync(chat);
            window.FocusComposer();
        }
        catch (OperationCanceledException) when (_isShuttingDown)
        {
        }
    }

    private static void DisposeUnopenedChatWindowRequest(DetachedChatWindowRequest request)
    {
        request.WindowVM.Dispose();
        request.ReleaseSurface();
    }

    private void OnDetachedDefaultModelSelectionChanged(string model, string? reasoningEffort, string? contextWindowTier)
        => _mainViewModel?.SyncDefaultModelSelectionFromChatSurface(model, reasoningEffort, contextWindowTier);

    private bool FocusDetachedChatWindow(Chat chat)
    {
        if (!_chatWindows.TryGetValue(chat.Id, out var window))
            return false;

        FocusChatWindow(window);
        return true;
    }

    private static void FocusChatWindow(ChatWindow window)
    {
        window.Show();
        window.ShowInTaskbar = true;
        window.WindowState = WindowState.Normal;
        window.Activate();
        window.FocusComposer();
    }

    private void CloseChatWindow(Guid chatId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_chatWindows.TryGetValue(chatId, out var window))
                window.Close();
        });
    }

    private void OnDetachedChatUpdated()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _mainViewModel?.RefreshChatList();
            _mainViewModel?.RefreshProjectRunningState();
        });
    }

    private void OnDetachedChatTitleChanged(Guid chatId, string title)
    {
        Dispatcher.UIThread.Post(() => _mainViewModel?.RefreshChatList());
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
