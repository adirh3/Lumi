using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Lumi.ViewModels;
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
        ["Launch at Startup"] = vm => vm.RevertLaunchAtStartupCommand.Execute(null),
        ["Start Minimized"] = vm => vm.RevertStartMinimizedCommand.Execute(null),
        ["Enable Notifications"] = vm => vm.RevertNotificationsEnabledCommand.Execute(null),
        ["Dark Mode"] = vm => vm.RevertIsDarkThemeCommand.Execute(null),
        ["Compact Density"] = vm => vm.RevertIsCompactDensityCommand.Execute(null),
        ["Font Size"] = vm => vm.RevertFontSizeCommand.Execute(null),
        ["Show Animations"] = vm => vm.RevertShowAnimationsCommand.Execute(null),
        ["Send with Enter"] = vm => vm.RevertSendWithEnterCommand.Execute(null),
        ["Show Timestamps"] = vm => vm.RevertShowTimestampsCommand.Execute(null),
        ["Show Tool Calls"] = vm => vm.RevertShowToolCallsCommand.Execute(null),
        ["Show Reasoning"] = vm => vm.RevertShowReasoningCommand.Execute(null),
        ["Auto-Generate Titles"] = vm => vm.RevertAutoGenerateTitlesCommand.Execute(null),
        ["Max Context Messages"] = vm => vm.RevertMaxContextMessagesCommand.Execute(null),
        ["Preferred Model"] = vm => vm.RevertPreferredModelCommand.Execute(null),
        ["Auto-Save Memories"] = vm => vm.RevertEnableMemoryAutoSaveCommand.Execute(null),
        ["Auto-Save Chats"] = vm => vm.RevertAutoSaveChatsCommand.Execute(null),
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
                    ShowPage(vm.SelectedPageIndex);
                else if (args.PropertyName == nameof(SettingsViewModel.SearchQuery))
                    ApplySearch(vm.SearchQuery);
            };
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

        var resultWord = matchCount == 1 ? "result" : "results";

        if (matchCount > 0)
        {
            _searchResultsHeader!.IsVisible = true;
            _searchCountText!.Text = matchCount.ToString();
            _searchQueryText!.Text = $" {resultWord} for \u201c{terms}\u201d";
            _noResultsPanel!.IsVisible = false;
            _mainScrollViewer!.IsVisible = true;
            _mainScrollViewer.ScrollToHome();
        }
        else
        {
            _searchResultsHeader!.IsVisible = false;
            _noResultsPanel!.IsVisible = true;
            _noResultsQueryText!.Text = $"No results for \u201c{terms}\u201d";
            _mainScrollViewer!.IsVisible = false;
        }

        if (DataContext is SettingsViewModel vmSearch)
            vmSearch.SearchResultSummary = matchCount > 0
                ? $"{matchCount} {resultWord}"
                : "No results";
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
}
