using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views.Settings;
using StrataTheme.Controls;

namespace Lumi.Views;

public partial class SettingsView : UserControl
{
    private Control?[] _pages = [];
    private Control? _searchResultsHeader;
    private TextBlock? _searchCountText;
    private TextBlock? _searchQueryText;
    private StackPanel? _noResultsPanel;
    private TextBlock? _noResultsQueryText;
    private ScrollViewer? _mainScrollViewer;
    private Button? _clearSearchButton;
    private Button? _noResultsClearButton;
    private Button? _hotkeyRecorderButton;
    private Slider? _uiScaleSlider;
    private StrataSetting? _debugTransparencySetting;
    private TextBlock? _debugTransparencyValue;
    private StrataSetting? _debugFpsOverlaySetting;
    private ToggleSwitch? _debugFpsOverlayToggle;
#if DEBUG
    private TopLevel? _topLevel;
#endif
    private bool _isRecordingHotkey;
    private bool _hotkeyRecordingCooldown;
    private Interactive? _hotkeyEventSource;

    // Cookie import dialog
    private StrataDialog? _cookieImportDialog;
    private StackPanel? _cookieProfilePicker;
    private StackPanel? _cookieImportProgress;
    private TextBlock? _cookieImportStatusText;
    private StackPanel? _cookieImportActions;
    private Button? _cookieImportButton;
    private Button? _cookieImportCancelButton;
    private BrowserCookieService.BrowserProfile? _selectedCookieProfile;
    private bool _isCookieImportInProgress;

    // Search opens what holds a match and remembers how each was, so clearing the search puts it back.
    private readonly Dictionary<Expander, bool> _expandedBeforeSearch = new();
    private bool _allPagesLaidOut;

    // Search hides what doesn't match above any visibility a control has of its own (platform
    // availability, phone setup progress), then lets go so those bindings rule again.
    private readonly Dictionary<Control, IDisposable> _searchHidden = new();

    public bool IsRecordingHotkey => _isRecordingHotkey;

    private void OnMobileTransportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid choices)
            return;

        var stacked = e.NewSize.Width < 500;
        if (choices.ColumnDefinitions.Count == (stacked ? 1 : 3))
            return;

        choices.Classes.Set("stacked", stacked);
        choices.ColumnDefinitions = ColumnDefinitions.Parse(stacked ? "*" : "*,*,*");
        choices.RowDefinitions = RowDefinitions.Parse(stacked ? "Auto,Auto,Auto" : "Auto");
        for (var index = 0; index < choices.Children.Count; index++)
        {
            Grid.SetColumn(choices.Children[index], stacked ? 0 : index);
            Grid.SetRow(choices.Children[index], stacked ? index : 0);
        }
    }

    private async void OnByokApiKeyModeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel
            && sender is ComboBox { SelectedItem: ByokApiKeyModeOption option })
        {
            await viewModel.ChangeByokApiKeyModeCommand.ExecuteAsync(option);
        }
    }

    // Page header elements for search mode styling
    private (TextBlock Title, TextBlock Description)[] _pageHeaders = [];

    public SettingsView()
    {
        InitializeComponent();

        // Handle StrataSetting.Reverted events bubbling up from any page
        AddHandler(StrataSetting.RevertedEvent, OnSettingReverted);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _pages =
        [
            this.FindControl<Control>("PageProfile"),
            this.FindControl<Control>("PageGeneral"),
            this.FindControl<Control>("PageMobile"),
            this.FindControl<Control>("PageAppearance"),
            this.FindControl<Control>("PageChat"),
            this.FindControl<Control>("PageAI"),
            this.FindControl<Control>("PagePrivacy"),
            this.FindControl<Control>("PageAbout"),
        ];

        _searchResultsHeader = this.FindControl<Control>("SearchResultsHeader");
        _searchCountText = this.FindControl<TextBlock>("SearchCountText");
        _searchQueryText = this.FindControl<TextBlock>("SearchQueryText");
        _noResultsPanel = this.FindControl<StackPanel>("NoResultsPanel");
        _noResultsQueryText = this.FindControl<TextBlock>("NoResultsQueryText");
        _mainScrollViewer = this.FindControl<ScrollViewer>("MainScrollViewer");
        _clearSearchButton = this.FindControl<Button>("ClearSearchButton");
        _noResultsClearButton = this.FindControl<Button>("NoResultsClearButton");

        if (_clearSearchButton is not null)
            _clearSearchButton.Click += (_, _) => ClearSearch();
        if (_noResultsClearButton is not null)
            _noResultsClearButton.Click += (_, _) => ClearSearch();

        _hotkeyRecorderButton = this.FindControl<Button>("HotkeyRecorderButton");
        if (_hotkeyRecorderButton is not null)
        {
            _hotkeyRecorderButton.Focusable = false;
            _hotkeyRecorderButton.Click += OnHotkeyRecorderButtonClick;
        }

        _uiScaleSlider = this.FindControl<Slider>("UiScaleSlider");
        if (_uiScaleSlider is not null)
        {
            _uiScaleSlider.AddHandler(
                InputElement.PointerReleasedEvent,
                OnUiScaleSliderPointerReleased,
                RoutingStrategies.Bubble,
                handledEventsToo: true);
            _uiScaleSlider.AddHandler(
                InputElement.KeyUpEvent,
                OnUiScaleSliderKeyUp,
                RoutingStrategies.Bubble,
                handledEventsToo: true);
            _uiScaleSlider.LostFocus += OnUiScaleSliderLostFocus;
        }

        _debugTransparencySetting = this.FindControl<StrataSetting>("DebugTransparencySetting");
        _debugTransparencyValue = this.FindControl<TextBlock>("DebugTransparencyValue");
        _debugFpsOverlaySetting = this.FindControl<StrataSetting>("DebugFpsOverlaySetting");
        _debugFpsOverlayToggle = this.FindControl<ToggleSwitch>("DebugFpsOverlayToggle");

        // Cookie import dialog
        _cookieImportDialog = this.FindControl<StrataDialog>("CookieImportDialog");
        _cookieProfilePicker = this.FindControl<StackPanel>("CookieProfilePicker");
        _cookieImportProgress = this.FindControl<StackPanel>("CookieImportProgress");
        _cookieImportStatusText = this.FindControl<TextBlock>("CookieImportStatusText");
        _cookieImportActions = this.FindControl<StackPanel>("CookieImportActions");
        _cookieImportButton = this.FindControl<Button>("CookieImportButton");
        _cookieImportCancelButton = this.FindControl<Button>("CookieImportCancelButton");

#if !WINDOWS
        if (_cookieImportDialog?.DialogContent is Control dialogContent)
        {
            foreach (var hint in dialogContent.GetLogicalDescendants().OfType<TextBlock>())
            {
                if (hint.Text == "• The selected browser will be briefly closed")
                    hint.Text = "• Close the selected browser first if its cookie database is locked";
                else if (hint.Text == "• Windows may ask for your password to authorize")
                    hint.Text = "• Your system may ask to unlock its credential store";
                else if (OperatingSystem.IsLinux() && hint.Text == "• Your cookies will be copied into Lumi's browser")
                    hint.Text = Loc.Browser_LinuxTabSessionHint;
            }
        }
#endif

        if (_cookieImportButton is not null)
            _cookieImportButton.Click += OnCookieImportClick;
        if (_cookieImportCancelButton is not null)
            _cookieImportCancelButton.Click += (_, _) => CloseCookieDialog();

        // Each page opens with a header: its tile, then the title and lede.
        var headers = new List<(TextBlock, TextBlock)>();
        foreach (var page in _pages)
        {
            if (page is StackPanel { Children.Count: > 0 } sp
                && sp.Children[0] is Grid header && header.Classes.Contains("st-header"))
            {
                var texts = header.GetLogicalDescendants().OfType<TextBlock>().ToList();
                var title = texts.FirstOrDefault(static text => text.Classes.Contains("mg-page-title"));
                var lede = texts.FirstOrDefault(static text => text.Classes.Contains("mg-page-lede"));
                if (title is not null && lede is not null)
                    headers.Add((title, lede));
            }
        }
        _pageHeaders = headers.ToArray();

        InitializeDebugTransparencyInfo();
        InitializeDebugFpsOverlay();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

#if DEBUG
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
            _topLevel.PropertyChanged += OnTopLevelPropertyChanged;
        UpdateDebugTransparencyInfo();
#endif
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // Settings is built the first time it opens, which can be for a search typed on Memories. Its
        // rows know their bound choices and visibility only once they are in the window.
        if (DataContext is SettingsViewModel { IsSearching: true } vm)
            ApplySearch(vm.SearchQuery);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
#if DEBUG
        if (_topLevel is not null)
            _topLevel.PropertyChanged -= OnTopLevelPropertyChanged;
        _topLevel = null;
#endif

        base.OnDetachedFromVisualTree(e);
    }

    private void InitializeDebugTransparencyInfo()
    {
#if DEBUG
        if (_debugTransparencySetting is not null)
            _debugTransparencySetting.IsVisible = true;
        UpdateDebugTransparencyInfo();
#else
        if (_debugTransparencySetting is not null)
            _debugTransparencySetting.IsVisible = false;
#endif
    }

    private void InitializeDebugFpsOverlay()
    {
#if DEBUG
        if (_debugFpsOverlaySetting is not null)
            _debugFpsOverlaySetting.IsVisible = true;
        if (_debugFpsOverlayToggle is not null)
            _debugFpsOverlayToggle.IsCheckedChanged += OnDebugFpsOverlayToggled;
#else
        if (_debugFpsOverlaySetting is not null)
            _debugFpsOverlaySetting.IsVisible = false;
#endif
    }

    private void OnDebugFpsOverlayToggled(object? sender, RoutedEventArgs e)
    {
#if DEBUG
        var topLevel = _topLevel ?? TopLevel.GetTopLevel(this);
        if (topLevel is not Window window) return;
        var enabled = _debugFpsOverlayToggle?.IsChecked == true;
        window.RendererDiagnostics.DebugOverlays = enabled
            ? Avalonia.Rendering.RendererDebugOverlays.Fps
            : Avalonia.Rendering.RendererDebugOverlays.None;
#endif
    }

#if DEBUG
    private void OnTopLevelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TopLevel.ActualTransparencyLevelProperty)
            UpdateDebugTransparencyInfo();
    }
#endif

    private void UpdateDebugTransparencyInfo()
    {
#if DEBUG
        if (_debugTransparencyValue is null) return;

        var topLevel = _topLevel ?? TopLevel.GetTopLevel(this);
        if (topLevel is not Window window)
        {
            _debugTransparencyValue.Text = "Unavailable";
            return;
        }

        var hints = window.TransparencyLevelHint;
        var hintText = hints is { Count: > 0 }
            ? string.Join(", ", hints.Select(static x => x.ToString()))
            : "None";

        _debugTransparencyValue.Text = $"Actual: {window.ActualTransparencyLevel} | Hint: {hintText}";
#endif
    }

    private void OnSettingReverted(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not StrataSetting setting) return;
        if (DataContext is not SettingsViewModel vm) return;

        var header = setting.Header;
        if (_revertActions.TryGetValue(header ?? "", out var action))
            action(vm);
    }

    private static readonly Dictionary<string, Action<SettingsViewModel>> _revertActions = new()
    {
        [Loc.Setting_LaunchAtStartup] = vm => vm.RevertLaunchAtStartupCommand.Execute(null),
        [Loc.Setting_StartMinimized] = vm => vm.RevertStartMinimizedCommand.Execute(null),
        [Loc.Setting_MinimizeToTray] = vm => vm.RevertMinimizeToTrayCommand.Execute(null),
        [Loc.Setting_GlobalHotkey] = vm => vm.RevertGlobalHotkeyCommand.Execute(null),
        [Loc.Setting_EnableNotifications] = vm => vm.RevertNotificationsEnabledCommand.Execute(null),
        [Loc.Setting_OfferCopiedCapabilities] = vm => vm.RevertOfferCopiedCapabilitiesCommand.Execute(null),
        [Loc.Setting_Theme] = vm => vm.RevertThemeCommand.Execute(null),
        [Loc.Setting_AccentColor] = vm => vm.RevertAccentColorCommand.Execute(null),
        [Loc.Setting_WindowMaterial] = vm => vm.RevertWindowMaterialCommand.Execute(null),
        [Loc.Setting_Density] = vm => vm.RevertIsCompactDensityCommand.Execute(null),
        [Loc.Setting_FontSize] = vm => vm.RevertUiScaleCommand.Execute(null),
        [Loc.Setting_AmbientPresence] = vm => vm.RevertShowAmbientPresenceCommand.Execute(null),
        [Loc.Setting_PresenceBrightness] = vm => vm.RevertPresenceBrightnessCommand.Execute(null),
        [Loc.Setting_AnimatePresenceWhileWorking] = vm => vm.RevertAnimatePresenceWhileWorkingCommand.Execute(null),
        [Loc.Setting_ShowAnimations] = vm => vm.RevertShowAnimationsCommand.Execute(null),
        [Loc.Setting_SendKey] = vm => vm.RevertSendWithEnterCommand.Execute(null),
        [Loc.Setting_ShowTimestamps] = vm => vm.RevertShowTimestampsCommand.Execute(null),
        [Loc.Setting_ShowToolCalls] = vm => vm.RevertShowToolCallsCommand.Execute(null),
        [Loc.Setting_ShowReasoning] = vm => vm.RevertShowReasoningCommand.Execute(null),
        [Loc.Setting_ExpandReasoningWhileStreaming] = vm => vm.RevertExpandReasoningWhileStreamingCommand.Execute(null),
        [Loc.Setting_SuggestFollowUps] = vm => vm.RevertSuggestFollowUpsCommand.Execute(null),
        [Loc.Setting_AutoGenerateTitles] = vm => vm.RevertAutoGenerateTitlesCommand.Execute(null),
        [Loc.Setting_GlobalCustomInstructions] = vm => vm.RevertGlobalCustomInstructionsCommand.Execute(null),
        [Loc.Setting_PreferredModel] = vm => vm.RevertDefaultModelSelectionCommand.Execute(null),
        [Loc.Setting_BackgroundModel] = vm => vm.RevertBackgroundModelCommand.Execute(null),
        [Loc.Setting_UseMcpProxy] = vm => vm.RevertUseMcpProxyCommand.Execute(null),
        [Loc.Setting_UseLazyMcpInitialization] = vm => vm.RevertUseLazyMcpInitializationCommand.Execute(null),
        [Loc.Setting_McpToolTimeout] = vm => vm.RevertMcpToolTimeoutCommand.Execute(null),
        [Loc.Setting_AutoSaveMemories] = vm => vm.RevertEnableMemoryAutoSaveCommand.Execute(null),
        [Loc.Setting_AutoMaintainMemories] = vm => vm.RevertEnableMemoryAutoMaintenanceCommand.Execute(null),
        [Loc.Setting_AutoSaveChats] = vm => vm.RevertAutoSaveChatsCommand.Execute(null),
    };

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is SettingsViewModel vm)
        {
            ShowPage(vm.SelectedPageIndex);

            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SettingsViewModel.SelectedPageIndex))
                {
                    ShowPage(vm.SelectedPageIndex);
                    UpdateDebugTransparencyInfo();
                }
                else if (args.PropertyName == nameof(SettingsViewModel.SearchQuery))
                    ApplySearch(vm.SearchQuery);
                else if (args.PropertyName == nameof(SettingsViewModel.GlobalHotkey))
                    UpdateHotkeyButtonText();
                else if (args.PropertyName == nameof(SettingsViewModel.IsMobileSetupReady))
                    RevealMobileSetup();
            };

            UpdateHotkeyButtonText();

            vm.CookieImportDialogRequested += () =>
                Dispatcher.UIThread.Post(OpenCookieDialog);
        }
    }

    private void OnUiScaleSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
        => CommitUiScaleSliderPreview();

    private void OnUiScaleSliderKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down
            or Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            CommitUiScaleSliderPreview();
        }
    }

    private void OnUiScaleSliderLostFocus(object? sender, RoutedEventArgs e)
        => CommitUiScaleSliderPreview();

    private void CommitUiScaleSliderPreview()
    {
        if (_uiScaleSlider is null || DataContext is not SettingsViewModel vm)
            return;

        vm.UiScaleLevelIndex = (int)Math.Round(_uiScaleSlider.Value);
    }

    public void ShowPage(int index)
    {
        // Don't switch pages while search is active
        if (DataContext is SettingsViewModel vm && !string.IsNullOrWhiteSpace(vm.SearchQuery))
            return;

        for (int i = 0; i < _pages.Length; i++)
        {
            if (_pages[i] is not null)
                _pages[i]!.IsVisible = i == index;
        }

        _mainScrollViewer?.ScrollToHome();
        if (index == 2)
            RevealMobileSetup();
    }

    private void RevealMobileSetup()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsEffectivelyVisible
                && DataContext is SettingsViewModel
                    { SelectedPageIndex: 2, UseDevTunnelForMobile: true, IsMobileSetupReady: true } vm
                && string.IsNullOrWhiteSpace(vm.SearchQuery))
                this.FindControl<Border>("MobileSetupPanel")?.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private void ApplySearch(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            // Restore normal paged mode
            RestoreAllVisibility();
            _searchResultsHeader!.IsVisible = false;
            _noResultsPanel!.IsVisible = false;
            _mainScrollViewer!.IsVisible = true;

            if (DataContext is SettingsViewModel vm)
            {
                vm.SearchResultSummary = "";
                ShowPage(vm.SelectedPageIndex);
            }
            return;
        }

        var terms = query!.Trim();
        int matchCount = 0;
        var openedBySearch = new HashSet<Expander>();

        // Start from each control's own visibility, so a query that narrows doesn't remember the last one.
        ReleaseSearchVisibility();
        LayOutAllPagesOnce();

        for (int i = 0; i < _pages.Length; i++)
        {
            if (_pages[i] is not StackPanel pageStack) continue;

            pageStack.Classes.Set("searching", true);
            bool pageHasResults = false;

            foreach (var child in pageStack.Children)
            {
                if (child is Grid header && header.Classes.Contains("st-header"))
                    continue;

                if (child is not StrataSettingGroup group)
                {
                    // Previews, heroes and stats belong to browsing a page; the few that answer a search say so.
                    if (child.IsVisible && SettingsSearch.Matches(terms, SettingsChrome.GetSearchText(child)))
                    {
                        matchCount++;
                        pageHasResults = true;
                    }
                    else
                    {
                        HideForSearch(child);
                    }
                    continue;
                }

                // Cards this computer can't use stay out of results.
                if (!group.IsVisible)
                    continue;

                // If the card itself matches, show all its settings
                bool groupHeaderMatches = SettingsSearch.Matches(terms, group.Header, group.Description);
                bool groupHasResults = false;
                var settings = SettingsIn(group).ToList();
                // A card without rows of its own, or the AI accounts' providers, is one result; a provider
                // match doesn't make the card's rows matches.
                if ((groupHeaderMatches && settings.Count == 0)
                    || (group.Items.OfType<Controls.EasyConnectView>().Any() && MatchesAiAccount(terms)))
                {
                    groupHasResults = true;
                    matchCount++;
                }

                foreach (var item in settings)
                {
                    item.IsHighlighted = false;
                    if (!item.IsVisible)
                        continue;

                    bool matches = groupHeaderMatches || MatchesSetting(item, terms);
                    item.IsHighlighted = matches;
                    if (matches) { groupHasResults = true; matchCount++; }
                    else HideForSearch(item);
                }

                foreach (var disclosure in DisclosuresIn(group))
                {
                    if (!disclosure.IsVisible || !MatchesDisclosure(disclosure, terms))
                        continue;

                    OpenForSearch(disclosure, openedBySearch);
                    groupHasResults = true;
                    matchCount++;
                }

                if (!groupHasResults)
                {
                    HideForSearch(child);
                    continue;
                }

                pageHasResults = true;
            }

            pageStack.IsVisible = pageHasResults;

            // Hide page descriptions during search — titles act as section dividers
            if (i < _pageHeaders.Length)
                _pageHeaders[i].Description.IsVisible = false;
        }

        // What a narrower query no longer needs open closes again.
        CloseOpenedBySearch(openedBySearch);

        var resultWord = matchCount == 1 ? Loc.Search_Result : Loc.Search_Results;

        if (matchCount > 0)
        {
            _searchResultsHeader!.IsVisible = true;
            _searchCountText!.Text = matchCount.ToString(Loc.Culture);
            _searchQueryText!.Text = string.Format(Loc.Search_ResultsFor, resultWord, terms);
            _noResultsPanel!.IsVisible = false;
            _mainScrollViewer!.IsVisible = true;
            _mainScrollViewer.ScrollToHome();
        }
        else
        {
            _searchResultsHeader!.IsVisible = false;
            _noResultsPanel!.IsVisible = true;
            _noResultsQueryText!.Text = string.Format(Loc.Search_NoResultsFor, terms);
            _mainScrollViewer!.IsVisible = false;
        }

        if (DataContext is SettingsViewModel vmSearch)
            vmSearch.SearchResultSummary = matchCount > 0
                ? $"{matchCount} {resultWord}"
                : Loc.Search_NoResults;
    }

    private void RestoreAllVisibility()
    {
        ReleaseSearchVisibility();

        for (int i = 0; i < _pages.Length; i++)
        {
            if (_pages[i] is not StackPanel pageStack) continue;

            pageStack.Classes.Set("searching", false);
            foreach (var group in pageStack.Children.OfType<StrataSettingGroup>())
            {
                foreach (var item in SettingsIn(group))
                    item.IsHighlighted = false;
            }

            // Restore page header descriptions
            if (i < _pageHeaders.Length)
                _pageHeaders[i].Description.IsVisible = true;
        }
        CloseOpenedBySearch();
    }

    private void OpenForSearch(Expander expander, HashSet<Expander> opened)
    {
        _expandedBeforeSearch.TryAdd(expander, expander.IsExpanded);
        expander.IsExpanded = true;
        opened.Add(expander);
    }

    private void CloseOpenedBySearch(HashSet<Expander>? keepOpen = null)
    {
        foreach (var (expander, wasExpanded) in _expandedBeforeSearch.ToList())
        {
            if (keepOpen?.Contains(expander) == true)
                continue;

            expander.IsExpanded = wasExpanded;
            _expandedBeforeSearch.Remove(expander);
        }
    }

    // A card's rows can sit inside a panel of its own (the AI accounts' privacy row), not only directly in it.
    private static IEnumerable<StrataSetting> SettingsIn(StrataSettingGroup group)
    {
        foreach (var item in group.Items.OfType<Control>())
        {
            if (item is StrataSetting setting)
            {
                yield return setting;
                continue;
            }

            foreach (var nested in item.GetLogicalDescendants().OfType<StrataSetting>())
                yield return nested;
        }
    }

    // The parts of a card that open to show a match: expanders outside its rows that say what finds them.
    private static IEnumerable<Expander> DisclosuresIn(StrataSettingGroup group)
        => group.Items.OfType<Control>()
            .Where(static item => item is not StrataSetting)
            .SelectMany(static item => item.GetSelfAndLogicalDescendants().OfType<Expander>())
            .Where(static expander => SettingsChrome.GetSearchText(expander) is not null
                                      && expander.FindLogicalAncestorOfType<StrataSetting>() is null);

    private static bool MatchesDisclosure(Expander disclosure, string query)
    {
        var names = new List<string?> { SettingsChrome.GetSearchText(disclosure) };
        var descriptions = new List<string?>();
        CollectOptionText(disclosure.Header, names, descriptions);
        return SettingsSearch.Matches(query, names, descriptions);
    }

    private void HideForSearch(Control control)
    {
        if (!_searchHidden.ContainsKey(control))
            _searchHidden[control] = control.Bind(Visual.IsVisibleProperty, new ConstantObservable<bool>(false), BindingPriority.Animation);
    }

    // A page that hasn't been shown isn't laid out, so its rows aren't in the window and their bindings
    // (text, choices, visibility) haven't run. The first search lays every page out once.
    private void LayOutAllPagesOnce()
    {
        if (_allPagesLaidOut || !IsEffectivelyVisible)
            return;

        foreach (var page in _pages)
        {
            if (page is not null)
                page.IsVisible = true;
        }
        UpdateLayout();
        _allPagesLaidOut = true;
    }

    private void ReleaseSearchVisibility()
    {
        foreach (var binding in _searchHidden.Values)
            binding.Dispose();
        _searchHidden.Clear();
    }

    private sealed class ConstantObservable<T>(T value) : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer)
        {
            observer.OnNext(value);
            return NoopDisposable.Instance;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public void Dispose()
        {
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None
            && DataContext is SettingsViewModel { IsConfirmingDanger: true } vm)
        {
            vm.CancelDangerActionCommand.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void ClearSearch()
    {
        if (DataContext is SettingsViewModel vm)
            vm.SearchQuery = "";
    }

    private static bool MatchesSetting(StrataSetting setting, string query)
    {
        var names = new List<string?> { setting.Header, SettingsChrome.GetSearchText(setting) };
        var descriptions = new List<string?> { setting.Description };
        CollectOptionText(setting.SettingContent, names, descriptions);
        return SettingsSearch.Matches(query, names, descriptions);
    }

    // A row is also found by what it offers: "ocean" finds the accent colour, "compact" the density.
    private static void CollectOptionText(object? content, List<string?> names, List<string?> descriptions)
    {
        switch (content)
        {
            case string text:
                names.Add(text);
                break;
            case TextBlock block:
                names.Add(block.Text);
                break;
            case SettingsChoice choice:
                names.Add(choice.Label);
                descriptions.Add(choice.Description);
                break;
            case AccentOption accent:
                names.Add(accent.Name);
                break;
            case KeyboardShortcutItem shortcut:
                descriptions.Add(shortcut.Description);
                break;
            case ItemsControl items:
                foreach (var item in items.Items)
                    CollectOptionText(item, names, descriptions);
                break;
            case ContentControl control:
                CollectOptionText(control.Content, names, descriptions);
                break;
            case Decorator decorator:
                CollectOptionText(decorator.Child, names, descriptions);
                break;
            case Panel panel:
                foreach (var child in panel.Children)
                    CollectOptionText(child, names, descriptions);
                break;
        }
    }

    private static bool MatchesAiAccount(string query)
        => Lumi.Services.Byok.ProviderCatalog.All.Any(profile =>
            SettingsSearch.Matches(query, [profile.Name], [profile.Description, profile.Billing]));

    private void OnHotkeyRecorderButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_isRecordingHotkey)
            return;

        StartHotkeyRecording();
    }

    // ── Hotkey recording ──

    private void StartHotkeyRecording()
    {
        if (_hotkeyRecorderButton is null || _hotkeyRecordingCooldown) return;
        _isRecordingHotkey = true;
        _hotkeyRecorderButton.Content = Loc.Setting_GlobalHotkeyRecording;
        _hotkeyRecorderButton.Classes.Add("accent");
        _hotkeyRecorderButton.Focusable = false;

        // Capture from top-level so recording works regardless of focused child control.
        _hotkeyEventSource = TopLevel.GetTopLevel(this) as Interactive ?? this;
        _hotkeyEventSource.AddHandler(KeyDownEvent, OnHotkeyKeyDown, RoutingStrategies.Tunnel);
        _hotkeyEventSource.AddHandler(KeyUpEvent, OnHotkeyKeyUp, RoutingStrategies.Tunnel);
        _hotkeyEventSource.AddHandler(PointerPressedEvent, OnHotkeyPointerPressed, RoutingStrategies.Tunnel);

        // Move focus off the button so Space release cannot trigger Click.
        Focusable = true;
        Focus();
    }

    private void StopHotkeyRecording()
    {
        _isRecordingHotkey = false;
        var eventSource = _hotkeyEventSource ?? this;
        eventSource.RemoveHandler(KeyDownEvent, OnHotkeyKeyDown);
        eventSource.RemoveHandler(KeyUpEvent, OnHotkeyKeyUp);
        eventSource.RemoveHandler(PointerPressedEvent, OnHotkeyPointerPressed);
        _hotkeyEventSource = null;
        _hotkeyRecorderButton?.Classes.Remove("accent");

        // Prevent the pending KeyUp (e.g. Space release) from re-triggering
        // the button Click → StartHotkeyRecording on the same frame
        _hotkeyRecordingCooldown = true;
        Dispatcher.UIThread.Post(() =>
        {
            _hotkeyRecordingCooldown = false;
        }, DispatcherPriority.Background);

        UpdateHotkeyButtonText();
    }

    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isRecordingHotkey || _hotkeyRecorderButton is null) return;

        e.Handled = true;

        // Pure modifier press → show live preview ("Ctrl+…", "Ctrl+Alt+…")
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            // e.KeyModifiers may not yet include the just-pressed key, so merge it
            var mods = e.KeyModifiers | (e.Key switch
            {
                Key.LeftCtrl or Key.RightCtrl => KeyModifiers.Control,
                Key.LeftShift or Key.RightShift => KeyModifiers.Shift,
                Key.LeftAlt or Key.RightAlt => KeyModifiers.Alt,
                Key.LWin or Key.RWin => KeyModifiers.Meta,
                _ => KeyModifiers.None
            });
            _hotkeyRecorderButton.Content = BuildModifierPreview(mods);
            return;
        }

        // Escape cancels recording
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            StopHotkeyRecording();
            return;
        }

        // Delete/Backspace without modifiers clears the hotkey
        if ((e.Key == Key.Delete || e.Key == Key.Back) && e.KeyModifiers == KeyModifiers.None)
        {
            if (DataContext is SettingsViewModel vm)
                vm.GlobalHotkey = "";
            StopHotkeyRecording();
            return;
        }

        // Require at least one modifier
        if (e.KeyModifiers == KeyModifiers.None) return;

        var parts = new List<string>();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");

        parts.Add(FormatKeyName(e.Key));

        var hotkey = string.Join("+", parts);

        if (GlobalHotkeyService.TryParseHotkey(hotkey, out _, out _))
        {
            if (DataContext is SettingsViewModel vm)
                vm.GlobalHotkey = hotkey;
        }

        StopHotkeyRecording();
    }

    private static string BuildModifierPreview(KeyModifiers mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        return parts.Count > 0 ? string.Join("+", parts) + "+\u2026" : Loc.Setting_GlobalHotkeyRecording;
    }

    private void OnHotkeyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isRecordingHotkey)
            StopHotkeyRecording();
    }

    /// <summary>Suppress KeyUp during recording so the button doesn't fire Click on Space release.</summary>
    private void OnHotkeyKeyUp(object? sender, KeyEventArgs e)
    {
        if (_isRecordingHotkey)
            e.Handled = true;
    }

    private void UpdateHotkeyButtonText()
    {
        if (_hotkeyRecorderButton is null) return;
        if (DataContext is SettingsViewModel vm && !string.IsNullOrWhiteSpace(vm.GlobalHotkey))
            _hotkeyRecorderButton.Content = vm.GlobalHotkey;
        else
            _hotkeyRecorderButton.Content = Loc.Setting_GlobalHotkeyNotSet;
    }

    private static string FormatKeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.F1 and <= Key.F12 => key.ToString(),
        Key.Space => "Space",
        Key.Return => "Enter",
        Key.Tab => "Tab",
        Key.Escape => "Escape",
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Insert => "Insert",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.OemTilde => "OemTilde",
        Key.OemMinus => "OemMinus",
        Key.OemPlus => "OemPlus",
        Key.OemPeriod => "OemPeriod",
        Key.OemComma => "OemComma",
        _ => key.ToString()
    };

    // ── Cookie import dialog ──

    private void OpenCookieDialog()
    {
        if (_cookieImportDialog is null || _cookieProfilePicker is null) return;

        var browsers = BrowserCookieService.GetInstalledBrowsers();
        var profilesByBrowser = browsers
            .Select(b => (Browser: b, Profiles: BrowserCookieService.GetProfiles(b)))
            .Where(x => x.Profiles.Count > 0)
            .ToList();

        if (profilesByBrowser.Count == 0) return;

        _cookieProfilePicker.Children.Clear();
        _selectedCookieProfile = null;
        _isCookieImportInProgress = false;

        if (_cookieImportActions is not null) _cookieImportActions.IsVisible = true;
        if (_cookieImportProgress is not null) _cookieImportProgress.IsVisible = false;
        if (_cookieImportStatusText is not null) _cookieImportStatusText.Text = "Preparing…";

        foreach (var (browser, profiles) in profilesByBrowser)
        {
            foreach (var profile in profiles)
            {
                var rb = new RadioButton
                {
                    Content = $"{browser.Name} — {profile.Name}",
                    GroupName = "CookieProfile",
                    Padding = new Thickness(8, 8),
                    Tag = profile,
                };
                rb.IsCheckedChanged += (_, _) =>
                {
                    if (rb.IsChecked == true)
                        _selectedCookieProfile = (BrowserCookieService.BrowserProfile)rb.Tag!;
                };

                if (_selectedCookieProfile is null ||
                    (browser.Name.Contains("Edge", StringComparison.OrdinalIgnoreCase)
                     && _selectedCookieProfile.Browser.Name != browser.Name))
                {
                    rb.IsChecked = true;
                    _selectedCookieProfile = profile;
                }

                _cookieProfilePicker.Children.Add(rb);
            }
        }

        _cookieImportDialog.IsDialogOpen = true;
    }

    private void CloseCookieDialog()
    {
        if (_cookieImportDialog is not null)
            _cookieImportDialog.IsDialogOpen = false;
    }

    private async void OnCookieImportClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedCookieProfile is null || _isCookieImportInProgress) return;
        if (DataContext is not SettingsViewModel vm) return;

        _isCookieImportInProgress = true;

        if (_cookieImportActions is not null) _cookieImportActions.IsVisible = false;
        if (_cookieImportProgress is not null) _cookieImportProgress.IsVisible = true;
        if (_cookieImportStatusText is not null) _cookieImportStatusText.Text = "Importing cookies…";

        try
        {
            var count = await vm.BrowserService
                .ImportCookiesAsync(_selectedCookieProfile)
                .WaitAsync(TimeSpan.FromSeconds(45));

            if (count <= 0)
            {
                if (_cookieImportStatusText is not null)
                    _cookieImportStatusText.Text = "No cookies imported. Close the selected browser and try again.";
                await Task.Delay(2200);
                if (_cookieImportActions is not null) _cookieImportActions.IsVisible = true;
                if (_cookieImportProgress is not null) _cookieImportProgress.IsVisible = false;
                return;
            }

            if (_cookieImportStatusText is not null)
                _cookieImportStatusText.Text = $"Imported {count:N0} cookies!";
            await Task.Delay(1000);

            vm.MarkCookiesImported();
            CloseCookieDialog();
        }
        catch (TimeoutException)
        {
            if (_cookieImportStatusText is not null)
                _cookieImportStatusText.Text = "Cookie import timed out. Close the browser and try again.";
            await Task.Delay(2200);
            if (_cookieImportActions is not null) _cookieImportActions.IsVisible = true;
            if (_cookieImportProgress is not null) _cookieImportProgress.IsVisible = false;
        }
        catch (Exception ex)
        {
            if (_cookieImportStatusText is not null)
                _cookieImportStatusText.Text = $"Error: {ex.Message}";
            await Task.Delay(2000);
            if (_cookieImportActions is not null) _cookieImportActions.IsVisible = true;
            if (_cookieImportProgress is not null) _cookieImportProgress.IsVisible = false;
        }
        finally
        {
            _isCookieImportInProgress = false;
        }
    }
}
