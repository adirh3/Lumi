using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Appearance;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;

namespace Lumi;

public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private IActivatableLifetime? _activationLifetime;
    private MainWindow? _mainWindow;
    private GlobalHotkeyService? _hotkeyService;
    private DataStore? _dataStore;
    private CopilotService? _copilotService;
    private UpdateService? _updateService;
    private BackgroundJobService? _backgroundJobService;
    private ChatSurfaceRegistry? _chatSurfaceRegistry;
    private ChatSessionStore? _chatSessionStore;
    private GlobalSearchService? _globalSearchService;
    private ProjectGitSyncService? _projectGitSyncService;
    private Lumi.Services.Context.ContextCuratorService? _contextCuratorService;
    private Lumi.Services.Remote.LumiRemoteServer? _remoteServer;
    private readonly List<MainWindow> _windows = [];
    private int _secondaryWindowSequence;
    private MainViewModel? _mainViewModel;
    private readonly Dictionary<Guid, ChatWindow> _chatWindows = [];
    private readonly HashSet<ChatWindow> _openChatWindows = [];
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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime crashDesktop
            && Program.PendingCrashReport is { } crashReport)
        {
            InitializeCrashReporter(crashDesktop, crashReport);
            base.OnFrameworkInitializationCompleted();
            return;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dataStore = new DataStore();
            _dataStore = dataStore;

            Loc.Load(dataStore.Data.Settings.Language);
            Styles.Add(CreateModelPickerTextStyle());
            Styles.Add(CreateConnectorPickerTextStyle());
            Styles.Add(CreateComposerPickerTextStyles());
            var settings = dataStore.Data.Settings;
            if (settings.FollowSystemTheme && SystemTheme.PrefersDark(this) is { } systemPrefersDark)
                settings.IsDarkTheme = systemPrefersDark;
            RequestedThemeVariant = settings.IsDarkTheme
                ? ThemeVariant.Dark
                : ThemeVariant.Light;
            AccentPalettes.Apply(Resources, settings.AccentColor);

            if (dataStore.Data.Settings.StartMinimized)
            {
                InitializeDesktop(dataStore);
            }
            else
            {
                var startupWindow = new StartupWindow();
                if (Loc.IsRightToLeft)
                    startupWindow.FlowDirection = Avalonia.Media.FlowDirection.RightToLeft;
                desktop.MainWindow = startupWindow;

                // Let the loading window render before constructing the main UI.
                startupWindow.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    if (!startupWindow.IsVisible)
                        return;

                    InitializeDesktop(dataStore);
                    var window = _mainWindow
                        ?? throw new InvalidOperationException("Lumi's main window was not initialized.");
                    window.Show();
                    Dispatcher.UIThread.Post(startupWindow.Close, DispatcherPriority.Background);
                }, DispatcherPriority.Background);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Localized labels for every model picker, in every window. Applied once the saved language is
    /// loaded: App.axaml is parsed before that, so markup there would always resolve English.
    /// </summary>
    internal static Style CreateModelPickerTextStyle() => new(selector => selector.OfType<StrataTheme.Controls.StrataModelPicker>())
    {
        Setters =
        {
            new Setter(StrataTheme.Controls.StrataModelPicker.SearchWatermarkProperty, Loc.ModelPicker_SearchWatermark),
            new Setter(StrataTheme.Controls.StrataModelPicker.NoResultsTextProperty, Loc.ModelPicker_NoResults),
            new Setter(StrataTheme.Controls.StrataModelPicker.ModelsLabelProperty, Loc.ModelPicker_Models),
            new Setter(StrataTheme.Controls.StrataModelPicker.PinnedLabelProperty, Loc.ModelPicker_Pinned),
            new Setter(StrataTheme.Controls.StrataModelPicker.ReasoningLabelProperty, Loc.ModelPicker_Reasoning),
            new Setter(StrataTheme.Controls.StrataModelPicker.ContextLabelProperty, Loc.ModelPicker_Context)
        }
    };

    /// <summary>
    /// Localized labels for the connector list in every chat composer, applied like the model picker's.
    /// The list's name, tooltip and error labels come from the composer, which each chat view sets.
    /// </summary>
    internal static Style CreateConnectorPickerTextStyle() => new(selector => selector.OfType<StrataConnectorPicker>())
    {
        Setters =
        {
            new Setter(StrataConnectorPicker.SearchWatermarkProperty, Loc.Mcp_SearchPlaceholder),
            new Setter(StrataConnectorPicker.AttentionLabelProperty, Loc.ConnectorPicker_NeedsAttention),
            new Setter(StrataConnectorPicker.SummaryFormatProperty, Loc.ConnectorPicker_Summary),
            new Setter(StrataConnectorPicker.TurnAllOnLabelProperty, Loc.ConnectorPicker_TurnAllOn),
            new Setter(StrataConnectorPicker.TurnAllOffLabelProperty, Loc.ConnectorPicker_TurnAllOff),
            new Setter(StrataConnectorPicker.OffLabelProperty, Loc.ConnectorPicker_Off),
            new Setter(StrataConnectorPicker.BrowseLabelProperty, Loc.ConnectorPicker_FindMore),
            new Setter(StrataConnectorPicker.ManageLabelProperty, Loc.ConnectorPicker_Manage),
            new Setter(StrataConnectorPicker.NoResultsFormatProperty, Loc.ConnectorPicker_NoResults),
            new Setter(StrataConnectorPicker.SearchBrowseFormatProperty, Loc.ConnectorPicker_SearchDiscover),
            new Setter(StrataConnectorPicker.EmptyTitleProperty, Loc.ConnectorPicker_EmptyTitle),
            new Setter(StrataConnectorPicker.EmptyTextProperty, Loc.ConnectorPicker_EmptyText),
            new Setter(StrataConnectorPicker.EmptyActionLabelProperty, Loc.ConnectorPicker_EmptyAction),
            new Setter(StrataConnectorPicker.LoadingTextProperty, Loc.ConnectorPicker_Loading),
            new Setter(StrataConnectorPicker.LabelProperty, Loc.Composer_Connectors),
        }
    };

    /// <summary>
    /// Localized words for the composer's + menu, its dictation button and its Skills, Chats and Agents
    /// lists in every chat window, applied like the connector list's. Each list is told apart by its class
    /// in the composer template.
    /// </summary>
    internal static Styles CreateComposerPickerTextStyles() =>
    [
        new Style(selector => selector.OfType<StrataChatComposer>())
        {
            Setters =
            {
                new Setter(StrataChatComposer.AddButtonToolTipProperty, Loc.Composer_Add),
                new Setter(StrataChatComposer.AddFilesLabelProperty, Loc.Composer_AddFiles),
                new Setter(StrataChatComposer.AddRecentChatsLabelProperty, Loc.Composer_ChatsRecent),
                new Setter(StrataChatComposer.AddAllChatsLabelProperty, Loc.Composer_AddAllChats),
                new Setter(StrataChatComposer.AddSkillsLabelProperty, Loc.Composer_Skills),
                new Setter(StrataChatComposer.AddAgentsLabelProperty, Loc.Composer_AddAgents),
                new Setter(StrataChatComposer.AddConnectorsLabelProperty, Loc.Composer_Connectors),
                new Setter(StrataChatComposer.AddConnectorsCountFormatProperty, Loc.Composer_ConnectorsOn),
                new Setter(StrataChatComposer.AddProjectLabelProperty, Loc.Composer_AddProject),
                new Setter(StrataChatComposer.AddMenuHintProperty, Loc.Composer_AddHint),
                new Setter(StrataChatComposer.DictateLabelProperty, Loc.Composer_Dictate),
                new Setter(StrataChatComposer.StopDictatingLabelProperty, Loc.Composer_StopDictating),
                new Setter(StrataChatComposer.CancelDictationLabelProperty, Loc.Composer_DiscardDictation),
                new Setter(StrataChatComposer.VoiceLanguageLabelProperty, Loc.Composer_DictationLanguage),
                new Setter(StrataChatComposer.MoreVoiceLanguagesLabelProperty, Loc.Composer_MoreLanguages),
            }
        },
        new Style(selector => selector.OfType<StrataComposerPicker>().Class("skills"))
        {
            Setters =
            {
                new Setter(StrataComposerPicker.LabelProperty, Loc.Composer_Skills),
                new Setter(StrataComposerPicker.ButtonToolTipProperty, Loc.Composer_SkillsTooltip),
                new Setter(StrataComposerPicker.SearchWatermarkProperty, Loc.Composer_SkillsSearch),
                new Setter(StrataComposerPicker.SelectedLabelProperty, Loc.Composer_SkillsSelected),
                new Setter(StrataComposerPicker.ItemsLabelProperty, Loc.Composer_SkillsAll),
                new Setter(StrataComposerPicker.EmptyTitleProperty, Loc.Composer_SkillsEmptyTitle),
                new Setter(StrataComposerPicker.EmptyTextProperty, Loc.Composer_SkillsEmptyText),
                new Setter(StrataComposerPicker.NoResultsFormatProperty, Loc.Composer_SkillsNoResults),
                new Setter(StrataComposerPicker.HintTextProperty, Loc.Composer_SkillsHint),
                new Setter(StrataComposerPicker.ManageLabelProperty, Loc.Composer_SkillsManage),
            }
        },
        new Style(selector => selector.OfType<StrataComposerPicker>().Class("chats"))
        {
            Setters =
            {
                new Setter(StrataComposerPicker.LabelProperty, Loc.Composer_Chats),
                new Setter(StrataComposerPicker.SearchWatermarkProperty, Loc.Composer_ChatsSearch),
                new Setter(StrataComposerPicker.SelectedLabelProperty, Loc.Composer_ChatsSelected),
                new Setter(StrataComposerPicker.ItemsLabelProperty, Loc.Composer_ChatsRecent),
                new Setter(StrataComposerPicker.EmptyTitleProperty, Loc.Composer_ChatsEmptyTitle),
                new Setter(StrataComposerPicker.EmptyTextProperty, Loc.Composer_ChatsEmptyText),
                new Setter(StrataComposerPicker.NoResultsFormatProperty, Loc.Composer_ChatsNoResults),
                new Setter(StrataComposerPicker.HintTextProperty, Loc.Composer_ChatsHint),
            }
        },
        new Style(selector => selector.OfType<StrataComposerPicker>().Class("agents"))
        {
            Setters =
            {
                new Setter(StrataComposerPicker.LabelProperty, Loc.Composer_AddAgents),
                new Setter(StrataComposerPicker.SearchWatermarkProperty, Loc.Composer_AgentsSearch),
                new Setter(StrataComposerPicker.SelectedLabelProperty, Loc.Composer_AgentsSelected),
                new Setter(StrataComposerPicker.ItemsLabelProperty, Loc.Composer_AgentsAll),
                new Setter(StrataComposerPicker.EmptyTitleProperty, Loc.Composer_AgentsEmptyTitle),
                new Setter(StrataComposerPicker.EmptyTextProperty, Loc.Composer_AgentsEmptyText),
                new Setter(StrataComposerPicker.NoResultsFormatProperty, Loc.Composer_AgentsNoResults),
                new Setter(StrataComposerPicker.HintTextProperty, Loc.Composer_AgentsHint),
            }
        },
    ];

    private void InitializeDesktop(DataStore dataStore)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var copilotService = new CopilotService();
            copilotService.SetSettingsProvider(
                () => dataStore.Data.Settings, Lumi.Services.Byok.SecureKeyStoreFactory.Instance);
            _copilotService = copilotService;

            // Warm the login-shell PATH probe off the UI thread so GUI-launched macOS/Linux builds
            // resolve Homebrew/nvm tools without the first PATH consumer ever blocking. No-op on Windows.
            UnixShellPath.Prewarm();
            var updateService = new UpdateService();
            _updateService = updateService;
            var updateShutdownCoordinator = new UpdateShutdownCoordinator();
            updateService.Initialize();
            _chatSurfaceRegistry = new ChatSurfaceRegistry();
            _globalSearchService = new GlobalSearchService(
                () => dataStore.Data,
                dataStore.GetChatIndexSnapshot,
                releaseChatSnapshot: dataStore.EvictChatSearchSnapshot,
                chatContentVersionProvider: dataStore.GetChatIndexVersion);
            _chatSessionStore = new ChatSessionStore(dataStore, copilotService, _chatSurfaceRegistry, _globalSearchService, Lumi.Services.Byok.SecureKeyStoreFactory.Instance);
            _projectGitSyncService = new ProjectGitSyncService(dataStore);
            _contextCuratorService = new Lumi.Services.Context.ContextCuratorService(dataStore, copilotService, _globalSearchService);
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

            void DisposeUiState(bool hideWindows)
            {
                var mainWindows = _windows.ToList();

                if (hideWindows)
                {
                    foreach (var window in mainWindows)
                        window.Hide();
                }

                foreach (var chatWindow in _openChatWindows.ToList())
                    chatWindow.Close();
                _openChatWindows.Clear();
                _chatWindows.Clear();
                updateService.Dispose();
                var viewModels = mainWindows
                    .Select(static window => window.DataContext)
                    .OfType<MainViewModel>()
                    .ToList();
                if (_mainViewModel is not null)
                    viewModels.Add(_mainViewModel);

                foreach (var windowVm in viewModels.Distinct())
                {
                    windowVm.Dispose();
                }
                _chatSessionStore?.Dispose();
                _chatSurfaceRegistry?.Dispose();
            }

            async Task DisposeServicesAsync()
            {
                try
                {
                    if (_projectGitSyncService is not null)
                        await _projectGitSyncService.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"[Shutdown] Failed to stop project git sync: {ex.Message}");
                }

                try
                {
                    if (_contextCuratorService is not null)
                        await _contextCuratorService.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"[Shutdown] Failed to stop the daily briefs: {ex.Message}");
                }

                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await dataStore.SaveAsync(cts.Token);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"[Shutdown] Failed to save app data: {ex.Message}");
                }

                try
                {
                    await copilotService.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"[Shutdown] Failed to stop Copilot: {ex.Message}");
                }

                try
                {
                    await McpProxyRuntime.Shared.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"[Shutdown] Failed to stop MCP processes: {ex.Message}");
                }

                try
                {
                    if (_remoteServer is not null)
                        await _remoteServer.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"[Shutdown] Failed to stop the mobile listener: {ex.Message}");
                }

#if DEBUG
                try
                {
                    if (_debugBridge is not null)
                        await _debugBridge.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"[Shutdown] Failed to stop the debug bridge: {ex.Message}");
                }
#endif

                // Last, once nothing else saves: queued chat writes reach the database before exit.
                try
                {
                    dataStore.CloseChatStorage();
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"[Shutdown] Failed to close the chat database: {ex.Message}");
                }
            }

            void CleanupForShutdown(bool isUpdateRestart)
            {
                if (_isShuttingDown)
                    return;

                PrepareForShutdown(isUpdateRestart);
                _isShuttingDown = true;
                if (isUpdateRestart)
                {
                    updateShutdownCoordinator.Run(
                        () => DisposeUiState(hideWindows: true),
                        DisposeServicesAsync);
                    return;
                }

                DisposeUiState(hideWindows: false);
                Task.Run(DisposeServicesAsync).GetAwaiter().GetResult();
            }

            // Task.Run avoids deadlocking the UI thread if _writeLock is held by
            // an in-flight fire-and-forget save that needs the dispatcher to complete.
            desktop.ShutdownRequested += (_, _) => CleanupForShutdown(isUpdateRestart: false);
            desktop.Exit += (_, _) => PrepareForShutdown();
            updateService.RestartRequested += () => Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    CleanupForShutdown(isUpdateRestart: true);
                }
                finally
                {
                    desktop.Shutdown();
                }
            });

            // Apply saved density
            MainWindow.ApplyDensityStatic(dataStore.Data.Settings.IsCompactDensity);
            UiScaleService.Apply(dataStore.Data.Settings.UiScalePercent);

            // "Theme: System" follows the OS between light and dark while Lumi runs.
            if (PlatformSettings is { } platformSettings)
                platformSettings.ColorValuesChanged += OnPlatformColorValuesChanged;

            var window = CreateWindow(vm, isPrimary: true);
            SetMainWindow(window);
            Program.SingleInstance?.SetActivationHandler(HandleActivationRequest);
            _activationLifetime = this.TryGetFeature<IActivatableLifetime>();
            if (_activationLifetime is not null)
                _activationLifetime.Activated += OnApplicationActivated;

#if DEBUG
            _debugBridge = new LumiDebugBridge(dataStore, vm);
            _debugBridge.Start();
#endif

            // Mobile companion listener. Opt-in, and every device still has to pair.
            _remoteServer = new Lumi.Services.Remote.LumiRemoteServer(dataStore, vm);
            vm.SettingsVM.AttachRemoteServer(_remoteServer);
            _ = InitializeRemoteServerAsync(_remoteServer);

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
#if DEBUG
                if (Program.BrowserNativeHarnessEnabled)
                {
                    BrowserNativeHarness.Start(desktop, vm, dataStore,
                        Program.BrowserNativeHarnessKeepOpen, Program.BrowserNativeHarnessOutput);
                    return;
                }
#endif
                Dispatcher.UIThread.Post(() =>
                {
                    // Defer non-critical setup until first frame is shown.
                    MainWindow.ApplyLaunchAtStartup(launchAtStartup);

                    if (minimizeToTray)
                        SetupTrayIcon(true);

                    AttachGlobalHotkey(window, globalHotkey);

                    // Start checking for updates in background
                    updateService.StartPeriodicChecks();
                    _projectGitSyncService?.Start();
                    // Test and debug profiles review their chats only on request (Update now on the Memories page),
                    // so seeded or copied chats never start background AI work on their own.
                    if (!DataStore.UsesIsolatedProfile)
                        _contextCuratorService?.Start();
                }, DispatcherPriority.Background);

#if DEBUG
                if (Program.MemoryHarnessOptions is { Enabled: true } memoryHarnessOptions)
                    StartMemoryStressHarness(desktop, vm, dataStore, memoryHarnessOptions);
                else if (Program.UiHarnessOptions is { Enabled: true } uiHarnessOptions)
                    StartUiResponsivenessHarness(desktop, vm, dataStore, uiHarnessOptions);
                else if (Program.HistoryHarnessOptions is { Enabled: true } historyHarnessOptions)
                    StartHistoryScaleHarness(desktop, vm, dataStore, historyHarnessOptions);

                if (Program.AnimationLifecycleLeakReproEnabled)
                    AnimationLifecycleLeakRepro.Start(desktop);
#endif
            };
            // Keep the system-rendered bundle icon; the runtime PNG is only an unbundled fallback.
            if (AppIcon.ShouldUseRuntimeDockIcon(OperatingSystem.IsMacOS(), Environment.ProcessPath))
            {
                var iconBytes = AppIcon.TryReadPngBytes();
                if (iconBytes is not null)
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (OperatingSystem.IsMacOS())
                            MacOsNative.TrySetDockIcon(iconBytes);
                    }, DispatcherPriority.Background);
            }

        }
    }

    private static async Task InitializeRemoteServerAsync(Lumi.Services.Remote.LumiRemoteServer server)
    {
        try
        {
            await server.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Remote] Failed to initialize the mobile listener: {ex.Message}");
        }
    }

    private void InitializeCrashReporter(
        IClassicDesktopStyleApplicationLifetime desktop,
        CrashReportData crashReport)
    {
        Loc.Load(crashReport.Language);
        RequestedThemeVariant = crashReport.IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;

        var window = new CrashReportWindow(crashReport);
        if (Loc.IsRightToLeft)
            window.FlowDirection = Avalonia.Media.FlowDirection.RightToLeft;

        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        desktop.MainWindow = window;
    }

    internal bool TryShowCrashReportFallback(CrashReportData crashReport, string launchError)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return false;

        foreach (var window in _windows.ToList())
            window.Hide();
        foreach (var window in _chatWindows.Values.ToList())
            window.Hide();

        var crashWindow = new CrashReportWindow(crashReport, launchError);
        if (Loc.IsRightToLeft)
            crashWindow.FlowDirection = Avalonia.Media.FlowDirection.RightToLeft;

        crashWindow.Closed += (_, _) => Environment.Exit(1);
        desktop.MainWindow = crashWindow;
        crashWindow.Show();
        crashWindow.Activate();
        return true;
    }

#if DEBUG
    private void StartMemoryStressHarness(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainViewModel vm,
        DataStore dataStore,
        MemoryDiagnostics.MemoryHarnessOptions options)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1200);
                var sessionStore = _chatSessionStore
                    ?? throw new InvalidOperationException("The chat session store is not initialized.");
                var harness = new MemoryDiagnostics.MemoryStressHarness(
                    vm,
                    dataStore,
                    sessionStore,
                    options,
                    snapshotDetachedWindows: () => _openChatWindows.ToArray(),
                    isDetachedWindowTracked: window =>
                        _openChatWindows.Contains(window)
                        || _chatWindows.Values.Any(tracked => ReferenceEquals(tracked, window)),
                    requestShutdown: exitCode => Dispatcher.UIThread.Post(() =>
                    {
                        try { Environment.ExitCode = exitCode; desktop.Shutdown(exitCode); }
                        catch { /* already shutting down */ }
                    }));
                await harness.RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[memory] Harness host failed: " + ex);
                Dispatcher.UIThread.Post(() =>
                {
                    try { Environment.ExitCode = 1; desktop.Shutdown(1); }
                    catch { /* already shutting down */ }
                });
            }
        });
    }

    private void StartHistoryScaleHarness(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainViewModel vm,
        DataStore dataStore,
        MemoryDiagnostics.HistoryHarnessOptions options)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1200);
                var harness = new MemoryDiagnostics.HistoryScaleHarness(
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
                Console.WriteLine("[history] Harness host failed: " + ex);
                Dispatcher.UIThread.Post(() =>
                {
                    try { Environment.ExitCode = 1; desktop.Shutdown(1); }
                    catch { /* already shutting down */ }
                });
            }
        });
    }

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
            _globalSearchService,
            _projectGitSyncService,
            Lumi.Services.Byok.SecureKeyStoreFactory.Instance
#if DEBUG
            , openAgentDebugHarness,
            skipOnboarding
#endif
            , initializeCopilotOnStartup: true
            , contextCuratorService: _contextCuratorService
        );
    }

    internal void ApplyUiScaleFromSettings(SettingsViewModel source, int scalePercent)
    {
        var normalizedScalePercent = UiScaleService.NormalizeScalePercent(scalePercent);
        UiScaleService.Apply(normalizedScalePercent);

        var synchronizedViewModels = new HashSet<SettingsViewModel> { source };

        void Synchronize(MainViewModel? viewModel)
        {
            if (viewModel is null || !synchronizedViewModels.Add(viewModel.SettingsVM))
                return;

            viewModel.SettingsVM.SyncUiScaleFromApplication(normalizedScalePercent);
        }

        Synchronize(_mainViewModel);
        foreach (var window in _windows)
            Synchronize(window.DataContext as MainViewModel);

        foreach (var chatWindow in _openChatWindows)
            chatWindow.ApplyUiScaleToChrome(normalizedScalePercent);
    }

    /// <summary>Shows the chosen window material on every open Lumi window.</summary>
    internal void ApplyWindowMaterial(string? material)
    {
        foreach (var window in _windows)
            WindowMaterials.Apply(window, material);
        foreach (var chatWindow in _openChatWindows)
            WindowMaterials.Apply(chatWindow, material);
    }

    private void OnPlatformColorValuesChanged(object? sender, PlatformColorValues values)
    {
        var prefersDark = values.ThemeVariant == PlatformThemeVariant.Dark;
        Dispatcher.UIThread.Post(() =>
        {
            if (_isShuttingDown || _dataStore?.Data.Settings.FollowSystemTheme != true)
                return;

            var viewModels = _windows
                .Select(static window => window.DataContext)
                .OfType<MainViewModel>()
                .Append(_mainViewModel)
                .OfType<MainViewModel>()
                .Distinct();
            foreach (var viewModel in viewModels)
                viewModel.SettingsVM.ApplySystemTheme(prefersDark);
        });
    }

    internal bool TryAdjustUiScale(int delta)
    {
        if (delta == 0 || _dataStore is null)
            return false;

        var currentScalePercent = UiScaleService.NormalizeScalePercent(_dataStore.Data.Settings.UiScalePercent);
        var nextScalePercent = UiScaleService.AdjustScalePercent(currentScalePercent, delta);
        var settingsViewModel = _mainViewModel?.SettingsVM
            ?? _windows.Select(static window => window.DataContext)
                .OfType<MainViewModel>()
                .Select(static viewModel => viewModel.SettingsVM)
                .FirstOrDefault();

        if (settingsViewModel is not null)
        {
            if (settingsViewModel.UiScalePercent == nextScalePercent)
                ApplyUiScaleFromSettings(settingsViewModel, nextScalePercent);
            else
                settingsViewModel.UiScalePercent = nextScalePercent;

            return true;
        }

        _dataStore.Data.Settings.UiScalePercent = nextScalePercent;
        UiScaleService.Apply(nextScalePercent);
        _ = _dataStore.SaveAsync();
        return true;
    }

    private MainWindow CreateWindow(MainViewModel vm, bool isPrimary)
    {
        var window = new MainWindow
        {
            DataContext = vm,
            IsPrimaryWindow = isPrimary,
            SecondaryWindowCascadeIndex = isPrimary ? 0 : ++_secondaryWindowSequence
        };
        WindowMaterials.Apply(window, _dataStore?.Data.Settings.WindowMaterial);

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

            if (ReferenceEquals(_mainWindow, window))
                PromoteReplacementMainWindow();
        };
        window.Closing += (_, _) => PrepareForWindowClosing(window);

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

    private void SetMainWindow(MainWindow? window)
    {
        _mainWindow = window;
        if (window is not null)
            window.IsPrimaryWindow = true;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = window;
    }

    private void PromoteReplacementMainWindow()
    {
        if (_isShuttingDown)
        {
            SetMainWindow(null);
            return;
        }

        var replacement = _windows.FirstOrDefault();

        if (replacement is null)
        {
            SetMainWindow(null);
            _hotkeyService?.Unregister();
            return;
        }

        PromoteMainWindow(replacement);
    }

    private void PromoteMainWindow(MainWindow window)
    {
        SetMainWindow(window);

        if (_dataStore is not null
            && window.DataContext is MainViewModel vm
            && vm.SettingsVM.MinimizeToTray != _dataStore.Data.Settings.MinimizeToTray)
        {
            vm.SettingsVM.MinimizeToTray = _dataStore.Data.Settings.MinimizeToTray;
        }

        AttachGlobalHotkey(window);
    }

    private void AttachGlobalHotkey(MainWindow window, string? hotkey = null)
    {
        _hotkeyService ??= new GlobalHotkeyService();
        _hotkeyService.HotkeyPressed -= OnGlobalHotkeyPressed;
        _hotkeyService.HotkeyPressed += OnGlobalHotkeyPressed;
        _hotkeyService.Unregister();
        _hotkeyService.Attach(window);

        hotkey ??= _dataStore?.Data.Settings.GlobalHotkey;
        if (!string.IsNullOrWhiteSpace(hotkey))
            _hotkeyService.Register(hotkey);
    }

    /// <summary>Create or remove the system tray icon.</summary>
    public void SetupTrayIcon(bool enable)
    {
        if (enable && _trayIcon is null)
        {
            var icon = CreateTrayIcon();

            var showItem = new NativeMenuItem(Loc.Tray_Show);
            showItem.Click += (_, _) => ShowMainWindow();

            var newWindowItem = new NativeMenuItem(Loc.Tray_NewWindow);
            newWindowItem.Click += (_, _) => OpenNewWindow();

            var exitItem = new NativeMenuItem(Loc.Tray_Exit);
            exitItem.Click += (_, _) =>
            {
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    PrepareForShutdown();
                    desktop.Shutdown();
                }
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

    /// <summary>Redraws the tray icon in the accent the app now shows.</summary>
    public void RefreshTrayIcon()
    {
        if (_trayIcon is not null)
            _trayIcon.Icon = CreateTrayIcon();
    }

    private WindowIcon CreateTrayIcon()
        => AppIcon.CreateTrayIcon(AccentPalettes.Find(AccentPalettes.AppliedId(Resources)), OperatingSystem.IsWindows());

    public void ShowMainWindow()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ShowMainWindow);
            return;
        }

        var window = GetOrCreateMainWindow();
        if (window is not null)
            FocusMainWindow(window);
    }

    private MainWindow? GetOrCreateMainWindow()
    {
        if (_mainWindow is not null && _windows.Contains(_mainWindow))
            return _mainWindow;

        if (_windows.FirstOrDefault() is { } existingWindow)
        {
            PromoteMainWindow(existingWindow);
            return existingWindow;
        }

        if (_mainViewModel is null || _isShuttingDown)
            return null;

        var window = CreateWindow(_mainViewModel, isPrimary: true);
        PromoteMainWindow(window);
        window.Show();
        return window;
    }

    private static void FocusMainWindow(MainWindow window)
    {
        window.Show();
        window.ShowInTaskbar = true;
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();

        if (window.DataContext is MainViewModel vm && vm.SelectedNavIndex == 0)
        {
            var chatView = window.FindControl<ChatView>("PageChat");
            Dispatcher.UIThread.Post(
                () => chatView?.FocusComposer(),
                DispatcherPriority.Input);
        }
    }

    public void OpenNewWindow(Guid? chatId = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OpenNewWindow(chatId));
            return;
        }

        var viewModel = CreateMainViewModel(
#if DEBUG
            skipOnboarding: Program.SkipOnboarding
#endif
        );
        if (_remoteServer is { } remoteServer)
            viewModel.SettingsVM.AttachRemoteServer(remoteServer);

        var window = CreateWindow(viewModel, isPrimary: false);
        if (_mainWindow is { IsVisible: true } owner)
            window.SecondaryWindowAnchorPosition = owner.Position;

        window.Show();

        window.Activate();

        if (chatId is Guid targetChatId && window.DataContext is MainViewModel vm)
            _ = vm.OpenChatByIdAsync(targetChatId);
    }

    internal void OpenChatInNewWindow(Guid chatId, MainViewModel? owner = null)
    {
        var viewModel = owner ?? _mainViewModel;
        if (viewModel?.DataStore.Data.Chats.FirstOrDefault(candidate => candidate.Id == chatId) is not { } chat)
            return;

        viewModel.OpenChatInNewWindowCommand.Execute(chat);
    }

    public void ShowMainWindow(Guid? chatId)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowMainWindow(chatId));
            return;
        }

        if (chatId is Guid existingChatId && FocusExistingWindowForChat(existingChatId))
            return;

        ShowMainWindow();

        if (chatId is not Guid targetChatId)
            return;

        if (_mainWindow?.DataContext is MainViewModel vm)
            _ = vm.OpenChatByIdAsync(targetChatId);
    }

    private void HandleActivationRequest(AppActivationRequest request)
        => Dispatcher.UIThread.Post(() => ShowMainWindow(request.ChatId));

    private void OnApplicationActivated(object? sender, ActivatedEventArgs e)
    {
        // macOS reopens an existing app through its lifetime, not a second process.
        if (e.Kind != ActivationKind.Reopen)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (!_isShuttingDown)
                ShowMainWindow();
        });
    }

    private bool FocusExistingWindowForChat(Guid chatId)
    {
        if (_chatWindows.TryGetValue(chatId, out var chatWindow))
        {
            FocusChatWindow(chatWindow);
            return true;
        }

        foreach (var window in _windows)
        {
            if (window.DataContext is not MainViewModel vm
                || vm.ChatVM.CurrentChat?.Id != chatId)
            {
                continue;
            }

            vm.SelectedNavIndex = 0;
            FocusMainWindow(window);
            return true;
        }

        return false;
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

        windowVm.McpRepairHandler = repair =>
        {
            ShowMainWindow();
            return _mainWindow?.DataContext is MainViewModel main
                ? main.StartMcpRepairChatAsync(repair)
                : Task.FromException(new InvalidOperationException(Loc.Connector_RepairStartFailed));
        };
        chatVm.McpRepairRequested += windowVm.StartMcpRepairChatAsync;

        void OnDetachedOpenAutomationsRequested()
        {
            ShowMainWindow();
            (_mainWindow?.DataContext as MainViewModel)?.ShowAutomations();
        }
        chatVm.OpenAutomationsRequested += OnDetachedOpenAutomationsRequested;

        void OnDetachedConnectorsPageRequested(ConnectorsPageRequest pageRequest)
        {
            ShowMainWindow();
            (_mainWindow?.DataContext as MainViewModel)?.ShowConnectorsPage(pageRequest);
        }
        chatVm.ConnectorsPageRequested += OnDetachedConnectorsPageRequested;

        void OnDetachedSkillsPageRequested()
        {
            ShowMainWindow();
            (_mainWindow?.DataContext as MainViewModel)?.ShowSkillsPage();
        }
        chatVm.SkillsPageRequested += OnDetachedSkillsPageRequested;

        void OnDetachedManageApprovalsRequested()
        {
            ShowMainWindow();
            (_mainWindow?.DataContext as MainViewModel)?.ShowPermissionsPage();
        }
        chatVm.ManageApprovalsRequested += OnDetachedManageApprovalsRequested;

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
        WindowMaterials.Apply(window, _dataStore.Data.Settings.WindowMaterial);
        window.Closing += (_, _) => PrepareForWindowClosing(window);
        chatVm.AddDisplayHost();
        _openChatWindows.Add(window);
        if (trackedChatId is Guid trackedId)
            _chatWindows[trackedId] = window;

        window.Closed += (_, _) =>
        {
            _openChatWindows.Remove(window);
            chatVm.RemoveDisplayHost();

            if (trackedChatId is Guid closedChatId
                && _chatWindows.TryGetValue(closedChatId, out var trackedWindow)
                && ReferenceEquals(trackedWindow, window))
                _chatWindows.Remove(closedChatId);

            chatVm.PropertyChanged -= OnDetachedCurrentChatChanged;
            chatVm.ChatUpdated -= OnDetachedChatUpdated;
            chatVm.ChatTitleChanged -= OnDetachedChatTitleChanged;
            chatVm.DefaultModelSelectionChanged -= OnDetachedDefaultModelSelectionChanged;
            chatVm.OpenChatRequested -= OnDetachedOpenChatRequested;
            chatVm.McpRepairRequested -= windowVm.StartMcpRepairChatAsync;
            chatVm.OpenAutomationsRequested -= OnDetachedOpenAutomationsRequested;
            chatVm.ConnectorsPageRequested -= OnDetachedConnectorsPageRequested;
            chatVm.SkillsPageRequested -= OnDetachedSkillsPageRequested;
            chatVm.ManageApprovalsRequested -= OnDetachedManageApprovalsRequested;
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
        if (window.WindowState == WindowState.Minimized)
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

    internal void PrepareForShutdown(bool restartExpected = false)
    {
        if (_activationLifetime is not null)
        {
            _activationLifetime.Activated -= OnApplicationActivated;
            _activationLifetime = null;
        }
        Program.SingleInstance?.StopAcceptingActivations(restartExpected);
    }

    private void PrepareForWindowClosing(Window window)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        var willShutdown = desktop.ShutdownMode switch
        {
            ShutdownMode.OnLastWindowClose => desktop.Windows.Count <= 1,
            ShutdownMode.OnMainWindowClose => ReferenceEquals(desktop.MainWindow, window),
            _ => false
        };

        if (willShutdown)
            PrepareForShutdown();
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
