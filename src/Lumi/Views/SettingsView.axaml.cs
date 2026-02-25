using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Services;
using Lumi.ViewModels;
using StrataTheme.Controls;

namespace Lumi.Views;

public partial class SettingsView : UserControl
{
    private Control?[] _pages = [];
    private ComboBox? _settingsSexCombo;
    private Control? _searchResultsHeader;
    private TextBlock? _searchCountText;
    private TextBlock? _searchQueryText;
    private StackPanel? _noResultsPanel;
    private TextBlock? _noResultsQueryText;
    private ScrollViewer? _mainScrollViewer;
    private Button? _clearSearchButton;
    private Button? _noResultsClearButton;
    private Button? _signInButton;
    private Button? _hotkeyRecorderButton;
    private StrataSetting? _debugTransparencySetting;
    private TextBlock? _debugTransparencyValue;
    private TopLevel? _topLevel;
    private bool _isRecordingHotkey;
    private bool _hotkeyRecordingCooldown;
    private Interactive? _hotkeyEventSource;

    public bool IsRecordingHotkey => _isRecordingHotkey;

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

        _signInButton = this.FindControl<Button>("SignInButton");
        if (_signInButton is not null)
            _signInButton.Content = Loc.Button_SignIn;

        _hotkeyRecorderButton = this.FindControl<Button>("HotkeyRecorderButton");
        if (_hotkeyRecorderButton is not null)
        {
            _hotkeyRecorderButton.Focusable = false;
            _hotkeyRecorderButton.Click += OnHotkeyRecorderButtonClick;
        }

        _debugTransparencySetting = this.FindControl<StrataSetting>("DebugTransparencySetting");
        _debugTransparencyValue = this.FindControl<TextBlock>("DebugTransparencyValue");

        _settingsSexCombo = this.FindControl<ComboBox>("SettingsSexCombo");
        if (_settingsSexCombo is not null)
        {
            _settingsSexCombo.ItemsSource = new[]
            {
                Loc.Onboarding_SexMale,
                Loc.Onboarding_SexFemale,
                Loc.Onboarding_SexPreferNot,
            };
        }

        // Extract page header elements (title + description TextBlocks)
        var headers = new List<(TextBlock, TextBlock)>();
        foreach (var page in _pages)
        {
            if (page is StackPanel sp && sp.Children.Count > 0
                && sp.Children[0] is StackPanel header && header.Children.Count >= 2
                && header.Children[0] is TextBlock title
                && header.Children[1] is TextBlock desc)
            {
                headers.Add((title, desc));
            }
        }
        _pageHeaders = headers.ToArray();

        InitializeDebugTransparencyInfo();
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
        [Loc.Setting_DarkMode] = vm => vm.RevertIsDarkThemeCommand.Execute(null),
        [Loc.Setting_CompactDensity] = vm => vm.RevertIsCompactDensityCommand.Execute(null),
        [Loc.Setting_FontSize] = vm => vm.RevertFontSizeCommand.Execute(null),
        [Loc.Setting_ShowAnimations] = vm => vm.RevertShowAnimationsCommand.Execute(null),
        [Loc.Setting_SendWithEnter] = vm => vm.RevertSendWithEnterCommand.Execute(null),
        [Loc.Setting_ShowTimestamps] = vm => vm.RevertShowTimestampsCommand.Execute(null),
        [Loc.Setting_ShowToolCalls] = vm => vm.RevertShowToolCallsCommand.Execute(null),
        [Loc.Setting_ShowReasoning] = vm => vm.RevertShowReasoningCommand.Execute(null),
        [Loc.Setting_AutoGenerateTitles] = vm => vm.RevertAutoGenerateTitlesCommand.Execute(null),
        [Loc.Setting_MaxContextMessages] = vm => vm.RevertMaxContextMessagesCommand.Execute(null),
        [Loc.Setting_PreferredModel] = vm => vm.RevertPreferredModelCommand.Execute(null),
        [Loc.Setting_AutoSaveMemories] = vm => vm.RevertEnableMemoryAutoSaveCommand.Execute(null),
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
                else if (args.PropertyName == nameof(SettingsViewModel.IsSigningIn))
                    UpdateSignInButton(vm);
                else if (args.PropertyName == nameof(SettingsViewModel.GlobalHotkey))
                    UpdateHotkeyButtonText();
            };

            UpdateSignInButton(vm);
            UpdateHotkeyButtonText();
        }
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

        for (int i = 0; i < _pages.Length; i++)
        {
            if (_pages[i] is not StackPanel pageStack) continue;

            bool pageHasResults = false;

            foreach (var child in pageStack.Children)
            {
                if (child is StrataSettingGroup group)
                {
                    // If the group header itself matches, show all its settings
                    bool groupHeaderMatches = (group.Header ?? "").Contains(terms, StringComparison.OrdinalIgnoreCase);
                    bool groupHasResults = false;

                    foreach (var item in group.Items.OfType<StrataSetting>())
                    {
                        bool matches = groupHeaderMatches || MatchesSetting(item, terms);
                        item.IsHighlighted = matches;
                        item.IsVisible = matches;
                        if (matches) { groupHasResults = true; matchCount++; }
                    }

                    group.IsVisible = groupHasResults;
                    if (groupHasResults) pageHasResults = true;
                }
            }

            pageStack.IsVisible = pageHasResults;

            // Hide page descriptions during search — titles act as section dividers
            if (i < _pageHeaders.Length)
                _pageHeaders[i].Description.IsVisible = false;
        }

        var resultWord = matchCount == 1 ? Loc.Search_Result : Loc.Search_Results;

        if (matchCount > 0)
        {
            _searchResultsHeader!.IsVisible = true;
            _searchCountText!.Text = matchCount.ToString();
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
        for (int i = 0; i < _pages.Length; i++)
        {
            if (_pages[i] is not StackPanel pageStack) continue;

            foreach (var child in pageStack.Children)
            {
                if (child is StrataSettingGroup group)
                {
                    group.IsVisible = true;
                    foreach (var item in group.Items.OfType<StrataSetting>())
                    {
                        item.IsHighlighted = false;
                        item.IsVisible = true;
                    }
                }
            }

            // Restore page header descriptions
            if (i < _pageHeaders.Length)
                _pageHeaders[i].Description.IsVisible = true;
        }
    }

    private void ClearSearch()
    {
        if (DataContext is SettingsViewModel vm)
            vm.SearchQuery = "";
    }

    private static bool MatchesSetting(StrataSetting setting, string query)
    {
        var header = setting.Header ?? "";
        var desc = setting.Description ?? "";
        return header.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               desc.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateSignInButton(SettingsViewModel vm)
    {
        if (_signInButton is not null)
            _signInButton.Content = vm.IsSigningIn ? Loc.Button_SigningIn : Loc.Button_SignIn;
    }

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
}
