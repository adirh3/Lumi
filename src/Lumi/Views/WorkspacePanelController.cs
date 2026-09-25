using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Services;
using Lumi.ViewModels;
using StrataTheme.Controls;

namespace Lumi.Views;

/// <summary>The controls a <see cref="WorkspacePanelController"/> drives inside its host view.</summary>
internal readonly record struct WorkspacePanelParts(
    Grid Grid,
    Control ChatPane,
    GridSplitter? Splitter,
    Control Panel,
    Control Overview,
    ContentControl PageHost);

/// <summary>
/// Drives the chat's Workspace — the one panel beside the chat. It hosts the at-a-glance overview
/// and every focused page (plan, agents, git changes, diffs, skills, file previews, the browser),
/// turns the view-model's show/hide requests into navigation with a back history, sizes the panel
/// (a compact rail for the overview, a split view for pages), plays its motion, and owns the
/// lifecycle of pages backed by native windows.
/// <para>A new page is a <see cref="WorkspacePage"/> value, a Show method that calls
/// <see cref="Navigate"/>, and a header line in <see cref="RefreshHeader"/>.</para>
/// </summary>
internal sealed class WorkspacePanelController : IDisposable
{
    private const double CompactDefaultWidth = 372;
    private const double CompactMinWidth = 300;
    private const double PanelMinWidth = 280;
    private const double ChatMinWidth = 220;
    private const double AutoOpenMinHostWidth = 1040;
    private const double PanelSlideOffset = 34;
    private const double PageSlideOffset = 14;
    private static readonly TimeSpan PanelShowDuration = TimeSpan.FromMilliseconds(290);
    private static readonly TimeSpan PanelHideDuration = TimeSpan.FromMilliseconds(190);
    private static readonly TimeSpan PageDuration = TimeSpan.FromMilliseconds(200);

    private enum PanelLayout { Hidden, Compact, Wide }

    private readonly Control _host;
    private readonly DataStore _dataStore;
    private readonly ChatViewModel _vm;
    private readonly WorkspacePanelParts _parts;
    private readonly Action? _ensureChatVisible;
    private readonly Func<Guid, bool>? _canShowBrowserPanel;

    // Navigation. _page is the logical page; the visuals follow it immediately while the panel is
    // open, or once a closing panel has finished sliding away (so its content never swaps mid-exit).
    private WorkspacePage _page = WorkspacePage.Overview;
    private Control? _pageContent;
    private readonly List<WorkspacePage> _history = [];
    private bool _navigatingBack;

    // Opened only to show a page, while the user keeps the workspace closed. Closing such a page
    // closes the panel again instead of revealing the overview.
    private bool _transientOpen;

    // Panel presentation.
    private bool? _panelShown;
    private Task<bool> _panelShownTask = Task.FromResult(true);
    private PanelLayout _layout = PanelLayout.Hidden;
    private double _compactWidth = CompactDefaultWidth;
    private double _appliedCompactWidth = double.NaN;
    private GridLength _wideChatWidth = new(1, GridUnitType.Star);
    private GridLength _widePanelWidth = new(1, GridUnitType.Star);
    private CancellationTokenSource? _panelAnimCts;
    private CancellationTokenSource? _pageAnimCts;
    private bool _isDisposed;

    // Pages, created on first use and reused.
    private Control? _planPage;
    private StrataMarkdown? _planMarkdown;
    private Control? _skillPage;
    private StrataMarkdown? _skillMarkdown;
    private SubagentRunView? _agentsPage;
    private DiffView? _diffView;
    private FileChangeItem? _diffItem;
    private GitChangesView? _gitPage;
    private GitChangesViewModel? _gitChanges;
    private GitFileChangeViewModel? _gitFile;
    private double _gitScrollOffset;
    private FilePreviewView? _fileView;
    private string? _filePath;
    private BrowserView? _browserView;

    public WorkspacePanelController(
        Control host,
        DataStore dataStore,
        ChatViewModel viewModel,
        WorkspacePanelParts parts,
        Action? ensureChatVisible = null,
        Func<Guid, bool>? canShowBrowserPanel = null)
    {
        _host = host;
        _dataStore = dataStore;
        _vm = viewModel;
        _parts = parts;
        _ensureChatVisible = ensureChatVisible;
        _canShowBrowserPanel = canShowBrowserPanel;

        WireViewModel();
        SyncPageContent();
        _ = ApplyPanelState();
    }

    public WorkspacePage Page => _page;

    /// <summary>What this view's header shows (the view binds its header to it).</summary>
    public WorkspaceHeader Header { get; } = new();

    public bool IsOpen => _panelShown == true;

    public bool IsBrowserOpen => IsOpen && _page == WorkspacePage.Browser;

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        UnwireViewModel();
        DisposeCancellationTokenSource(ref _panelAnimCts);
        StopPageEntrance();

        // Hide the native browser, but leave the surface's IsBrowserOpen alone: returning to this chat
        // restores an open browser from it (RestoreBrowserPanelForActiveChat).
        _browserView?.ClearBrowserService();
        if (_page == WorkspacePage.FilePreview)
        {
            _vm.IsFilePreviewOpen = false;
            _vm.PreviewFilePath = null;
        }

        _fileView?.Dispose();
        _fileView = null;
        if (_gitChanges is not null)
            _gitChanges.FileActivated = null;

        _parts.PageHost.Content = null;
        _parts.PageHost.IsVisible = false;
        _parts.Overview.IsVisible = true;
        ResetPanelVisual(_parts.Panel);
        _parts.Panel.IsVisible = false;
        ApplyLayout(PanelLayout.Hidden);
    }

    // ── Public navigation ────────────────────────────────────────────────────────────

    public void ShowPlan()
    {
        _planPage ??= CreateMarkdownPage("WorkspacePlanMarkdown", out _planMarkdown);
        _planMarkdown!.Markdown = _vm.PlanContent;
        Navigate(WorkspacePage.Plan, _planPage);
    }

    public void ShowSkill()
    {
        _skillPage ??= CreateMarkdownPage("WorkspaceSkillMarkdown", out _skillMarkdown);
        _skillMarkdown!.Markdown = _vm.SkillPreviewContent;
        Navigate(WorkspacePage.Skill, _skillPage);
    }

    public void ShowAgents()
        => Navigate(WorkspacePage.Agents, _agentsPage ??= new SubagentRunView { DataContext = _vm });

    public void ShowDiff(FileChangeItem fileChange)
    {
        _diffItem = fileChange;
        _diffView ??= new DiffView();
        _diffView.SetFileChangeDiff(fileChange);
        Navigate(WorkspacePage.Diff, _diffView);
    }

    public void ShowGitChanges(GitChangesViewModel changes)
    {
        if (_gitChanges is not null && !ReferenceEquals(_gitChanges, changes))
            _gitChanges.FileActivated = null;

        _gitChanges = changes;
        changes.FileActivated = OpenGitFile;
        changes.ClearSelection();
        _gitPage = new GitChangesView { DataContext = changes };
        _gitFile = null;
        _gitScrollOffset = 0;
        Navigate(WorkspacePage.GitChanges, _gitPage);

        if (changes.FindFile(changes.InitialFilePath) is { } file)
            OpenGitFile(file);
    }

    public void ShowBrowser(Guid chatId)
    {
        if (_canShowBrowserPanel?.Invoke(chatId) != false)
            _ = ShowBrowserAsync(chatId);
    }

    /// <summary>Back: up one level inside a page (a git file, an agent run), else the previous page.</summary>
    public void GoBack()
    {
        if (_page == WorkspacePage.GitChanges && _gitFile is not null)
        {
            ShowGitList(restoreScroll: true);
            return;
        }

        if (_page == WorkspacePage.Agents && _vm.SelectedSubagentRun is not null)
        {
            _vm.BackToSubagentIndexCommand.Execute(null);
            return;
        }

        if (!TryShowPrevious())
            ShowOverview();
    }

    /// <summary>The user dismissed the workspace (close button or header toggle).</summary>
    public void Close()
    {
        // Closing the workspace the user keeps open remembers that choice; closing a panel that only
        // opened to show a page leaves the preference untouched.
        if (ResolvePreferenceVisible())
            _vm.SaveWorkspacePanelPreference(false);

        HidePanel();
    }

    public void Toggle()
    {
        if (IsOpen)
        {
            Close();
            return;
        }

        _vm.SaveWorkspacePanelPreference(true);
        _transientOpen = false;
        _ = ApplyPanelState();
    }

    /// <summary>Returns to the overview and forgets transient pages (chat switch, leaving the chat).</summary>
    public void ClosePages() => HidePanel();

    /// <summary>Re-resolves visibility after the content, the host size or the preference changed.</summary>
    public void RefreshVisibility() => _ = ApplyPanelState();

    public void ShowCurrentBrowserController()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ShowCurrentBrowserController, DispatcherPriority.Loaded);
            return;
        }

        if (!IsBrowserOpen)
            return;

        _browserView?.ShowCurrentController();
        Dispatcher.UIThread.Post(() => _browserView?.RefreshBounds(), DispatcherPriority.Loaded);
    }

    public void RefreshFilePreview()
    {
        if (_page == WorkspacePage.FilePreview && _filePath is { } path)
            _vm.OpenFilePreview(path);
    }

    public void OpenFilePreviewExternally() => _fileView?.OpenInDefaultApp();

    // ── View-model requests ──────────────────────────────────────────────────────────

    private void WireViewModel()
    {
        _vm.BrowserShowRequested += OnBrowserShowRequested;
        _vm.BrowserHideRequested += OnBrowserHideRequested;
        _vm.DiffShowRequested += OnDiffShowRequested;
        _vm.DiffHideRequested += OnDiffHideRequested;
        _vm.GitChangesShowRequested += OnGitChangesShowRequested;
        _vm.PlanShowRequested += OnPlanShowRequested;
        _vm.PlanHideRequested += OnPlanHideRequested;
        _vm.SkillShowRequested += OnSkillShowRequested;
        _vm.SkillHideRequested += OnSkillHideRequested;
        _vm.SubagentRunShowRequested += OnSubagentRunShowRequested;
        _vm.SubagentRunHideRequested += OnSubagentRunHideRequested;
        _vm.FilePreviewShowRequested += OnFilePreviewShowRequested;
        _vm.FilePreviewHideRequested += OnFilePreviewHideRequested;
        _vm.WorkspaceToggleRequested += Toggle;
        _vm.WorkspaceContentChanged += RefreshVisibility;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void UnwireViewModel()
    {
        _vm.BrowserShowRequested -= OnBrowserShowRequested;
        _vm.BrowserHideRequested -= OnBrowserHideRequested;
        _vm.DiffShowRequested -= OnDiffShowRequested;
        _vm.DiffHideRequested -= OnDiffHideRequested;
        _vm.GitChangesShowRequested -= OnGitChangesShowRequested;
        _vm.PlanShowRequested -= OnPlanShowRequested;
        _vm.PlanHideRequested -= OnPlanHideRequested;
        _vm.SkillShowRequested -= OnSkillShowRequested;
        _vm.SkillHideRequested -= OnSkillHideRequested;
        _vm.SubagentRunShowRequested -= OnSubagentRunShowRequested;
        _vm.SubagentRunHideRequested -= OnSubagentRunHideRequested;
        _vm.FilePreviewShowRequested -= OnFilePreviewShowRequested;
        _vm.FilePreviewHideRequested -= OnFilePreviewHideRequested;
        _vm.WorkspaceToggleRequested -= Toggle;
        _vm.WorkspaceContentChanged -= RefreshVisibility;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void PostIfActive(Action action)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isDisposed)
                action();
        });
    }

    private void OnBrowserShowRequested(Guid chatId) => PostIfActive(() => ShowBrowser(chatId));
    private void OnBrowserHideRequested() => PostIfActive(() => ClosePage(WorkspacePage.Browser));
    private void OnDiffShowRequested(FileChangeItem item) => PostIfActive(() => ShowDiff(item));
    private void OnGitChangesShowRequested(GitChangesViewModel changes) => PostIfActive(() => ShowGitChanges(changes));
    private void OnPlanShowRequested() => PostIfActive(ShowPlan);
    private void OnPlanHideRequested() => PostIfActive(() => ClosePage(WorkspacePage.Plan));
    private void OnSkillShowRequested() => PostIfActive(ShowSkill);
    private void OnSkillHideRequested() => PostIfActive(() => ClosePage(WorkspacePage.Skill));
    private void OnSubagentRunShowRequested() => PostIfActive(ShowAgents);
    private void OnSubagentRunHideRequested() => PostIfActive(() => ClosePage(WorkspacePage.Agents));

    private void OnDiffHideRequested() => PostIfActive(() =>
    {
        ClosePage(WorkspacePage.Diff);
        ClosePage(WorkspacePage.GitChanges);
    });

    private void OnFilePreviewShowRequested(string filePath)
    {
        var chatId = _vm.CurrentChat?.Id;
        PostIfActive(() =>
        {
            if (_vm.IsFilePreviewOpen && _vm.PreviewFilePath == filePath && _vm.CurrentChat?.Id == chatId)
                _ = ShowFilePreviewAsync(filePath);
        });
    }

    private void OnFilePreviewHideRequested()
    {
        if (Dispatcher.UIThread.CheckAccess())
            ClosePage(WorkspacePage.FilePreview);
        else
            PostIfActive(() => ClosePage(WorkspacePage.FilePreview));
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChatViewModel.PlanContent) when _planMarkdown is not null:
                _planMarkdown.Markdown = _vm.PlanContent;
                break;
            case nameof(ChatViewModel.SkillPreviewContent) when _skillMarkdown is not null:
                _skillMarkdown.Markdown = _vm.SkillPreviewContent;
                break;
            case nameof(ChatViewModel.SelectedSubagentRun) or nameof(ChatViewModel.SubagentRunsSummary)
                when _page == WorkspacePage.Agents:
            case nameof(ChatViewModel.PlanTitle) or nameof(ChatViewModel.PlanProgressLabel)
                when _page == WorkspacePage.Plan:
            case nameof(ChatViewModel.SkillPreviewTitle) when _page == WorkspacePage.Skill:
                RefreshHeader();
                break;
        }
    }

    // ── Navigation core ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Shows <paramref name="page"/> in the panel, opening the panel when needed. Returns a task that
    /// completes (true) once the panel is fully on screen — native pages attach only then, because
    /// native windows cannot ride the slide.
    /// </summary>
    private Task<bool> Navigate(WorkspacePage page, Control content)
    {
        var previous = _page;
        var wasOpen = IsOpen;
        if (previous != page)
        {
            LeavePage(previous);
            if (wasOpen && !_navigatingBack)
            {
                _history.Remove(page);
                _history.Add(previous);
            }
        }

        if (!wasOpen)
        {
            _history.Clear();
            _transientOpen = !ResolvePreferenceVisible();
        }

        // A file preview is the only page that keeps the view-model's preview request open.
        if (page != WorkspacePage.FilePreview && _vm.IsFilePreviewOpen)
        {
            _vm.IsFilePreviewOpen = false;
            _vm.PreviewFilePath = null;
        }

        _page = page;
        _pageContent = content;
        _ensureChatVisible?.Invoke();
        var shown = ApplyPanelState();

        if (wasOpen && previous != page && !IsNativePage(page))
            PlayPageEntrance(_parts.PageHost, back: _navigatingBack);

        return shown;
    }

    private void ShowOverview()
    {
        var wasPage = _page != WorkspacePage.Overview;
        ResetToOverview();

        if (wasPage && IsOpen)
            PlayPageEntrance(_parts.Overview, back: true);
    }

    /// <summary>A page's content went away (plan deleted, browser closed…): step back past it.</summary>
    private void ClosePage(WorkspacePage page)
    {
        _history.Remove(page);
        if (_page != page)
            return;

        if (TryShowPrevious())
            return;

        if (_transientOpen)
            HidePanel();
        else
            ShowOverview();
    }

    private bool TryShowPrevious()
    {
        while (_history.Count > 0)
        {
            var previous = _history[^1];
            _history.RemoveAt(_history.Count - 1);
            if (previous == WorkspacePage.Overview)
            {
                ShowOverview();
                return true;
            }

            _navigatingBack = true;
            try
            {
                if (TryReshow(previous))
                    return true;
            }
            finally
            {
                _navigatingBack = false;
            }
        }

        return false;
    }

    /// <summary>Re-opens a page from its retained state, if it still has something to show.</summary>
    private bool TryReshow(WorkspacePage page)
    {
        switch (page)
        {
            case WorkspacePage.Plan when _vm.HasPlan:
                ShowPlan();
                return true;
            case WorkspacePage.Skill when _vm.SkillPreviewContent is not null:
                ShowSkill();
                return true;
            case WorkspacePage.Agents when _vm.HasSubagentRuns:
                ShowAgents();
                return true;
            case WorkspacePage.Diff when _diffItem is not null:
                ShowDiff(_diffItem);
                return true;
            case WorkspacePage.GitChanges when _gitPage is not null:
                _gitFile = null;
                Navigate(WorkspacePage.GitChanges, _gitPage);
                RestoreGitScroll();
                return true;
            case WorkspacePage.FilePreview when _filePath is { } path && File.Exists(path):
                _vm.PreviewFilePath = path;
                _vm.IsFilePreviewOpen = true;
                _ = ShowFilePreviewAsync(path);
                return true;
            case WorkspacePage.Browser when _vm.CurrentChat is { } chat && _vm.GetBrowserServiceForChat(chat.Id) is not null:
                _ = ShowBrowserAsync(chat.Id);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Drops every page and closes the panel, unless the user keeps the workspace open (then the
    /// overview stays). A closing panel keeps its content while it slides away, then resets.
    /// </summary>
    private void HidePanel()
    {
        _transientOpen = false;
        ResetToOverview();
    }

    private void ResetToOverview()
    {
        LeavePage(_page);
        _history.Clear();
        _page = WorkspacePage.Overview;
        _pageContent = null;
        _ = ApplyPanelState();
    }

    /// <summary>Releases what a page holds while it is not on screen.</summary>
    private void LeavePage(WorkspacePage page)
    {
        StopPageEntrance();
        switch (page)
        {
            case WorkspacePage.Browser:
                _browserView?.ClearBrowserService();
                _vm.IsBrowserOpen = false;
                break;
            case WorkspacePage.FilePreview:
                _fileView?.Clear();
                _vm.IsFilePreviewOpen = false;
                _vm.PreviewFilePath = null;
                break;
            case WorkspacePage.GitChanges:
                if (_gitFile is null && _gitPage is not null)
                    _gitScrollOffset = _gitPage.ScrollOffset;
                _gitFile = null;
                break;
        }
    }

    private static bool IsNativePage(WorkspacePage page)
        => page is WorkspacePage.Browser or WorkspacePage.FilePreview;

    /// <summary>Puts the current page's content on screen.</summary>
    private void SyncPageContent()
    {
        var isOverview = _page == WorkspacePage.Overview;
        _parts.Overview.IsVisible = isOverview;
        _parts.PageHost.IsVisible = !isOverview;
        var content = isOverview ? null : _pageContent;
        if (!ReferenceEquals(_parts.PageHost.Content, content))
            _parts.PageHost.Content = content;
    }

    /// <summary>Mirrors the current page to the view-model and refreshes this view's header.</summary>
    private void MirrorPage()
    {
        _vm.WorkspacePage = _page;
        RefreshHeader();
    }

    private void RefreshHeader()
    {
        var (title, subtitle) = _page switch
        {
            WorkspacePage.Plan => (_vm.PlanTitle, _vm.PlanProgressLabel),
            WorkspacePage.Agents => _vm.SelectedSubagentRun is { } run
                ? (run.DisplayName, (string?)Loc.Workspace_Agents)
                : (Loc.Workspace_Agents, _vm.SubagentRunsSummary),
            WorkspacePage.GitChanges => _gitFile is { } file
                ? (file.FileName, (string?)(file.Change.SubmoduleName is { Length: > 0 } submodule
                    ? $"{Loc.Workspace_GitChanges} · {submodule}"
                    : Loc.Workspace_GitChanges))
                : (Loc.Workspace_GitChanges, _gitChanges?.BranchLabel),
            WorkspacePage.Diff => (_diffItem?.FileName ?? Loc.Diff_Title, _diffItem?.ActionLabel),
            WorkspacePage.Skill => (_vm.SkillPreviewTitle ?? Loc.Workspace_Skill, (string?)Loc.Workspace_Skill),
            WorkspacePage.FilePreview => (Path.GetFileName(_filePath) ?? Loc.Preview_Title, (string?)Loc.Preview_Title),
            WorkspacePage.Browser => (Loc.Browser_Title, (string?)null),
            _ => (Loc.Workspace_Title, (string?)null),
        };

        Header.Page = _page;
        Header.Title = string.IsNullOrWhiteSpace(title) ? Loc.Workspace_Title : title;
        Header.Subtitle = subtitle;
    }

    // ── Pages ────────────────────────────────────────────────────────────────────────

    private Control CreateMarkdownPage(string name, out StrataMarkdown markdown)
    {
        // Gutters live on the content, not the ScrollViewer: a Margin constrains the measure, so
        // long lines wrap inside the panel instead of running under its edge.
        markdown = new StrataMarkdown
        {
            Name = name,
            IsInline = true,
            Margin = new Thickness(16, 12, 16, 18),
        };

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = markdown,
        };
    }

    private void OpenGitFile(GitFileChangeViewModel file)
    {
        if (_gitChanges is null || _page != WorkspacePage.GitChanges)
            return;

        if (_gitFile is null && _gitPage is not null)
            _gitScrollOffset = _gitPage.ScrollOffset;

        _gitChanges.Select(file);
        _gitFile = file;
        var diffView = new DiffView();
        _parts.PageHost.Content = diffView;
        _pageContent = diffView;
        RefreshHeader();
        if (_panelShownTask.IsCompleted)
            PlayPageEntrance(_parts.PageHost, back: false);

        if (file.Kind == GitChangeKind.Submodule)
            _ = ShowSubmodulePointerDiffAsync(file.Change, diffView);
        else if (file.Change.Kind is GitChangeKind.Added or GitChangeKind.Untracked)
            _ = ShowAddedGitFileDiffAsync(file.Change.FullPath, diffView);
        else
            _ = LoadGitUnifiedDiffAsync(file.Change, diffView);
    }

    /// <summary>Back from a file diff to the grouped list, where the reader left off.</summary>
    private void ShowGitList(bool restoreScroll)
    {
        if (_gitPage is null)
            return;

        _gitFile = null;
        _parts.PageHost.Content = _gitPage;
        _pageContent = _gitPage;
        RefreshHeader();
        PlayPageEntrance(_parts.PageHost, back: true);
        if (restoreScroll)
            RestoreGitScroll();
    }

    private void RestoreGitScroll()
    {
        var offset = _gitScrollOffset;
        // The list is re-attached this frame; apply the offset once layout has run.
        Dispatcher.UIThread.Post(() =>
        {
            if (_gitPage is not null && _gitFile is null)
                _gitPage.ScrollOffset = offset;
        }, DispatcherPriority.Loaded);
    }

    private async Task ShowFilePreviewAsync(string filePath, string? error = null)
    {
        // HTML previews render in the browser page, where scripts and styles work.
        if (error is null && OperatingSystem.IsWindows() && FilePreviewContent.IsHtmlFile(filePath)
            && File.Exists(filePath) && _vm.CurrentChat is { } chat)
        {
            var browserService = _vm.GetOrCreateBrowserService(chat.Id);
            _vm.HasUsedBrowser = true;
            try
            {
                if (await ShowBrowserAsync(chat.Id))
                    await browserService.NavigateAsync(new Uri(Path.GetFullPath(filePath)).AbsoluteUri);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or InvalidOperationException or COMException
#if WINDOWS
                                       or Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException
#endif
                                       )
            {
                if (!_isDisposed && _vm.CurrentChat?.Id == chat.Id && _page == WorkspacePage.Browser)
                {
                    _vm.PreviewFilePath = filePath;
                    _vm.IsFilePreviewOpen = true;
                    // The preview replaces the browser that failed; Back must not return to it.
                    var fallback = ShowFilePreviewAsync(filePath, ex.Message);
                    _history.Remove(WorkspacePage.Browser);
                    await fallback;
                }
            }

            return;
        }

        _fileView ??= new FilePreviewView();
        var alreadyShown = IsOpen && _page == WorkspacePage.FilePreview;
        _filePath = filePath;
        var shown = Navigate(WorkspacePage.FilePreview, _fileView);
        _vm.IsFilePreviewOpen = true;
        _vm.PreviewFilePath = filePath;

        if (!alreadyShown && !await shown)
            return;
        if (_isDisposed || _page != WorkspacePage.FilePreview || _filePath != filePath)
            return;

        if (error is not null)
            _fileView.ShowUnavailable(filePath, error);
        else
            await _fileView.ShowFileAsync(filePath);
    }

    private async Task<bool> ShowBrowserAsync(Guid chatId)
    {
        var browserService = _vm.GetBrowserServiceForChat(chatId);
        if (browserService is null)
            return false;

        _browserView ??= new BrowserView();
        var alreadyShown = IsBrowserOpen;
        var shown = Navigate(WorkspacePage.Browser, _browserView);

        var isDark = Application.Current?.RequestedThemeVariant == ThemeVariant.Dark;
        browserService.SetTheme(isDark);
        _browserView.SetBrowserService(browserService, _dataStore);

        if (alreadyShown)
        {
            _vm.IsBrowserOpen = true;
            _browserView.RefreshBounds();
            browserService.SetControllerVisible(true);
            return true;
        }

        if (!await shown || _isDisposed || _page != WorkspacePage.Browser)
            return false;

        _vm.IsBrowserOpen = true;

        // The page may have just widened the panel: place the native view once layout has run so it
        // never flashes at its previous bounds.
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        if (_isDisposed || _page != WorkspacePage.Browser)
            return false;

        _browserView.RefreshBounds();
        browserService.SetControllerVisible(true);
        return true;
    }

    // ── Panel visibility & layout ────────────────────────────────────────────────────

    /// <summary>The overview's own visibility: the saved choice, or "auto" when unset — show it once the
    /// chat has content and the window is wide enough to keep a comfortable reading column.</summary>
    private bool ResolvePreferenceVisible()
        => _dataStore.Data.Settings.WorkspacePanelOpen
           ?? (_vm.HasWorkspaceContent && _host.Bounds.Width >= AutoOpenMinHostWidth);

    private bool WantsOpen()
        => _page != WorkspacePage.Overview || _transientOpen || ResolvePreferenceVisible();

    private Task<bool> ApplyPanelState()
    {
        if (_isDisposed)
            return Task.FromResult(false);

        var open = WantsOpen();
        _vm.IsWorkspacePanelOpen = open;
        MirrorPage();
        if (open)
        {
            SyncPageContent();
            ApplyLayout(_page == WorkspacePage.Overview ? PanelLayout.Compact : PanelLayout.Wide);
        }

        return AnimatePanel(open);
    }

    private Task<bool> AnimatePanel(bool open)
    {
        if (_panelShown == open)
            return open ? _panelShownTask : Task.FromResult(true);

        // Before the host has a size the auto preference can't be judged; resolve once it does, so
        // the panel doesn't slide in on startup just because layout arrived after the chat.
        if (_panelShown is null && !open && _host.Bounds.Width <= 0)
        {
            ApplyLayout(PanelLayout.Hidden);
            SyncPageContent();
            return Task.FromResult(true);
        }

        var firstResolve = _panelShown is null;
        _panelShown = open;

        // The first resolution (a fresh chat surface) applies instantly: switching chats never animates.
        if (firstResolve)
        {
            DisposeCancellationTokenSource(ref _panelAnimCts);
            ResetPanelVisual(_parts.Panel);
            _parts.Panel.IsVisible = open;
            if (!open)
            {
                ApplyLayout(PanelLayout.Hidden);
                SyncPageContent();
            }

            _panelShownTask = Task.FromResult(true);
            return _panelShownTask;
        }

        if (open)
        {
            _panelShownTask = SlideInAsync();
            return _panelShownTask;
        }

        return SlideOutAsync();
    }

    private async Task<bool> SlideInAsync()
    {
        var panel = _parts.Panel;
        var token = ReplaceCancellationTokenSource(ref _panelAnimCts).Token;
        panel.RenderTransform = new TranslateTransform(PanelSlideOffset, 0);
        panel.Opacity = 0;
        panel.IsVisible = true;

        try
        {
            await CreateSlideFade(PanelSlideOffset, 0, 0, 1, PanelShowDuration, new CubicEaseOut())
                .RunAsync(panel, token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (token.IsCancellationRequested || _isDisposed)
            return false;

        ResetPanelVisual(panel);
        return true;
    }

    private async Task<bool> SlideOutAsync()
    {
        var panel = _parts.Panel;
        var token = ReplaceCancellationTokenSource(ref _panelAnimCts).Token;
        panel.RenderTransform = new TranslateTransform(0, 0);

        try
        {
            await CreateSlideFade(0, PanelSlideOffset, 1, 0, PanelHideDuration, new CubicEaseIn())
                .RunAsync(panel, token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (token.IsCancellationRequested || _isDisposed)
            return false;

        panel.IsVisible = false;
        ResetPanelVisual(panel);
        ApplyLayout(PanelLayout.Hidden);
        SyncPageContent();
        return true;
    }

    /// <summary>
    /// Sizes the panel's column: a compact rail beside the chat for the overview, an even split for
    /// pages. Each keeps the width the user drags it to while this chat is shown; the rail also
    /// re-fits as the window resizes so it never crowds the conversation.
    /// </summary>
    private void ApplyLayout(PanelLayout layout)
    {
        var defs = _parts.Grid.ColumnDefinitions;
        while (defs.Count < 3)
            defs.Add(new ColumnDefinition());

        if (_layout == layout)
        {
            if (layout == PanelLayout.Compact)
                FitCompactWidth(defs);
            return;
        }

        RememberUserWidths(defs);
        _layout = layout;

        switch (layout)
        {
            case PanelLayout.Compact:
                defs[0].Width = new GridLength(1, GridUnitType.Star);
                defs[1].Width = GridLength.Auto;
                FitCompactWidth(defs);
                break;
            case PanelLayout.Wide:
                defs[0].Width = _wideChatWidth;
                defs[1].Width = GridLength.Auto;
                defs[2].Width = _widePanelWidth;
                break;
            default:
                defs[0].Width = new GridLength(1, GridUnitType.Star);
                defs[1].Width = new GridLength(0);
                defs[2].Width = new GridLength(0);
                break;
        }

        // The splitter honours column bounds, so dragging can never crush the chat or the panel, nor
        // take the rail past the range it is fitted to (see FitCompactWidth).
        defs[0].MinWidth = layout == PanelLayout.Hidden ? 0 : ChatMinWidth;
        defs[2].MinWidth = layout switch
        {
            PanelLayout.Hidden => 0,
            PanelLayout.Compact => CompactMinWidth,
            _ => PanelMinWidth,
        };
        if (layout != PanelLayout.Compact)
            defs[2].MaxWidth = double.PositiveInfinity;
        Grid.SetColumn(_parts.ChatPane, 0);
        Grid.SetColumn(_parts.Panel, 2);
        if (_parts.Splitter is { } splitter)
            splitter.IsVisible = layout != PanelLayout.Hidden;
    }

    private void FitCompactWidth(ColumnDefinitions defs)
    {
        RememberUserWidths(defs);
        var max = CompactMaxWidth();
        defs[2].MaxWidth = max;
        var width = Math.Clamp(_compactWidth, CompactMinWidth, max);
        if (defs[2].Width is { IsAbsolute: true } current && Math.Abs(current.Value - width) < 0.5)
            return;

        defs[2].Width = new GridLength(width);
        _appliedCompactWidth = width;
    }

    /// <summary>Keeps widths the user dragged with the splitter (not the ones this controller fitted).</summary>
    private void RememberUserWidths(ColumnDefinitions defs)
    {
        if (_layout == PanelLayout.Compact
            && defs[2].Width is { IsAbsolute: true, Value: > 0 } compact
            && Math.Abs(compact.Value - _appliedCompactWidth) >= 0.5)
        {
            _compactWidth = compact.Value;
            _appliedCompactWidth = compact.Value;
        }
        else if (_layout == PanelLayout.Wide && defs[0].Width.IsStar && defs[2].Width.IsStar)
        {
            _wideChatWidth = defs[0].Width;
            _widePanelWidth = defs[2].Width;
        }
    }

    /// <summary>The overview is a companion: it never takes more room than the conversation beside it.</summary>
    private double CompactMaxWidth()
    {
        var hostWidth = _host.Bounds.Width;
        return hostWidth > 0 ? Math.Max(CompactMinWidth, hostWidth * 0.45) : double.PositiveInfinity;
    }

    private static void ResetPanelVisual(Control panel)
    {
        panel.Opacity = 1;
        panel.RenderTransform = null;
    }

    private void PlayPageEntrance(Control target, bool back)
    {
        StopPageEntrance();
        var token = ReplaceCancellationTokenSource(ref _pageAnimCts).Token;
        var from = back ? -PageSlideOffset : PageSlideOffset;
        target.RenderTransform = new TranslateTransform(from, 0);
        target.Opacity = 0;
        _ = RunPageEntranceAsync(target, from, token);
    }

    /// <summary>Cancels a running page entrance and restores both surfaces to rest.</summary>
    private void StopPageEntrance()
    {
        DisposeCancellationTokenSource(ref _pageAnimCts);
        ResetPanelVisual(_parts.Overview);
        ResetPanelVisual(_parts.PageHost);
    }

    private static async Task RunPageEntranceAsync(Control target, double from, CancellationToken token)
    {
        try
        {
            await CreateSlideFade(from, 0, 0, 1, PageDuration, new CubicEaseOut()).RunAsync(target, token);
        }
        catch (OperationCanceledException)
        {
            return; // whoever cancelled restores the surface
        }

        if (token.IsCancellationRequested)
            return;

        ResetPanelVisual(target);
    }

    // ── Git diffs ────────────────────────────────────────────────────────────────────

    private static async Task ShowAddedGitFileDiffAsync(string filePath, DiffView diffView)
    {
        var content = string.Empty;
        try
        {
            if (File.Exists(filePath))
                content = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        Dispatcher.UIThread.Post(() => diffView.SetSnapshotDiff(filePath, string.Empty, content));
    }

    /// <summary>Renders the commit-log summary for a submodule whose recorded pointer moved.</summary>
    private static async Task ShowSubmodulePointerDiffAsync(GitFileChange change, DiffView diffView)
    {
        var log = await GitService.GetSubmoduleCommitLogAsync(change.RepoRoot, change.RepoRelativePath)
            .ConfigureAwait(false);
        Dispatcher.UIThread.Post(() => diffView.SetSnapshotDiff(change.RelativePath, string.Empty, log ?? ""));
    }

    private static async Task LoadGitUnifiedDiffAsync(GitFileChange change, DiffView diffView)
    {
        var diff = await GitService.GetFileDiffAsync(change.RepoRoot, change.RepoRelativePath).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() => diffView.SetUnifiedDiffText(change.FullPath, diff));
    }

    // ── Motion helpers ───────────────────────────────────────────────────────────────

    private static Animation CreateSlideFade(
        double fromX,
        double toX,
        double fromOpacity,
        double toOpacity,
        TimeSpan duration,
        Easing easing)
    {
        return new Animation
        {
            Duration = duration,
            Easing = easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, fromOpacity),
                        new Setter(TranslateTransform.XProperty, fromX),
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, toOpacity),
                        new Setter(TranslateTransform.XProperty, toX),
                    }
                },
            }
        };
    }

    private static CancellationTokenSource ReplaceCancellationTokenSource(ref CancellationTokenSource? source)
    {
        DisposeCancellationTokenSource(ref source);
        source = new CancellationTokenSource();
        return source;
    }

    private static void DisposeCancellationTokenSource(ref CancellationTokenSource? source)
    {
        var previous = source;
        source = null;
        if (previous is null)
            return;

        try { previous.Cancel(); }
        catch (ObjectDisposedException) { }
        previous.Dispose();
    }
}
