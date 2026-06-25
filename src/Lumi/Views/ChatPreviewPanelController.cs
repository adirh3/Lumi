using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;

namespace Lumi.Views;

internal sealed class ChatPreviewPanelController : IDisposable
{
    private const double PreviewOffsetX = 40.0;
    private static readonly TimeSpan ShowDuration = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan HideDuration = TimeSpan.FromMilliseconds(200);
    private static readonly FontFamily MonoFontFamily = new("Cascadia Code, Consolas, monospace");

    private readonly Control _resourceScope;
    private readonly DataStore _dataStore;
    private readonly ChatViewModel _viewModel;
    private readonly Grid _contentGrid;
    private readonly Control _primaryPane;
    private readonly GridSplitter? _splitter;
    private readonly Border _browserPanel;
    private readonly ContentControl _browserHost;
    private readonly Border _diffPanel;
    private readonly ContentControl _diffHost;
    private readonly TextBlock _diffTitleText;
    private readonly Border _planPanel;
    private readonly Border _skillPanel;
    private readonly Action? _ensureChatVisible;
    private readonly Func<Guid, bool>? _canShowBrowserPanel;
    private BrowserView? _browserView;
    private DiffView? _diffView;
    private List<GitFileChangeViewModel>? _lastGitChangesList;
    private CancellationTokenSource? _browserAnimCts;
    private CancellationTokenSource? _previewAnimCts;
    private bool _isDisposed;

    public ChatPreviewPanelController(
        Control resourceScope,
        DataStore dataStore,
        ChatViewModel viewModel,
        Grid contentGrid,
        Control primaryPane,
        GridSplitter? splitter,
        Border browserPanel,
        ContentControl browserHost,
        Border diffPanel,
        ContentControl diffHost,
        TextBlock diffTitleText,
        Border planPanel,
        Border skillPanel,
        Action? ensureChatVisible = null,
        Func<Guid, bool>? canShowBrowserPanel = null)
    {
        _resourceScope = resourceScope;
        _dataStore = dataStore;
        _viewModel = viewModel;
        _contentGrid = contentGrid;
        _primaryPane = primaryPane;
        _splitter = splitter;
        _browserPanel = browserPanel;
        _browserHost = browserHost;
        _diffPanel = diffPanel;
        _diffHost = diffHost;
        _diffTitleText = diffTitleText;
        _planPanel = planPanel;
        _skillPanel = skillPanel;
        _ensureChatVisible = ensureChatVisible;
        _canShowBrowserPanel = canShowBrowserPanel;
        WireViewModel();
    }

    public bool IsBrowserOpen => _browserPanel.IsVisible;
    public bool IsDiffOpen => _diffPanel.IsVisible;
    public bool IsPlanOpen => _planPanel.IsVisible;
    public bool IsSkillOpen => _skillPanel.IsVisible;

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        UnwireViewModel();
        _browserView?.ClearBrowserService();
        DisposeCancellationTokenSource(ref _browserAnimCts);
        DisposeCancellationTokenSource(ref _previewAnimCts);
    }

    private void WireViewModel()
    {
        _viewModel.BrowserShowRequested += OnBrowserShowRequested;
        _viewModel.BrowserHideRequested += OnBrowserHideRequested;
        _viewModel.DiffShowRequested += OnDiffShowRequested;
        _viewModel.DiffHideRequested += OnDiffHideRequested;
        _viewModel.GitChangesShowRequested += OnGitChangesShowRequested;
        _viewModel.PlanShowRequested += OnPlanShowRequested;
        _viewModel.PlanHideRequested += OnPlanHideRequested;
        _viewModel.SkillShowRequested += OnSkillShowRequested;
        _viewModel.SkillHideRequested += OnSkillHideRequested;
    }

    private void UnwireViewModel()
    {
        _viewModel.BrowserShowRequested -= OnBrowserShowRequested;
        _viewModel.BrowserHideRequested -= OnBrowserHideRequested;
        _viewModel.DiffShowRequested -= OnDiffShowRequested;
        _viewModel.DiffHideRequested -= OnDiffHideRequested;
        _viewModel.GitChangesShowRequested -= OnGitChangesShowRequested;
        _viewModel.PlanShowRequested -= OnPlanShowRequested;
        _viewModel.PlanHideRequested -= OnPlanHideRequested;
        _viewModel.SkillShowRequested -= OnSkillShowRequested;
        _viewModel.SkillHideRequested -= OnSkillHideRequested;
    }

    private void PostIfActive(Action action)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isDisposed)
                action();
        });
    }

    private void OnBrowserShowRequested(Guid chatId) => PostIfActive(() =>
    {
        if (_canShowBrowserPanel?.Invoke(chatId) != false)
            ShowBrowserPanel(chatId);
    });

    private void OnBrowserHideRequested() => PostIfActive(HideBrowserPanel);
    private void OnDiffShowRequested(FileChangeItem item) => PostIfActive(() => ShowDiffPanel(item));
    private void OnDiffHideRequested() => PostIfActive(HideDiffPanel);
    private void OnGitChangesShowRequested(List<GitFileChangeViewModel> files) => PostIfActive(() => ShowGitChangesPanel(files));
    private void OnPlanShowRequested() => PostIfActive(ShowPlanPanel);
    private void OnPlanHideRequested() => PostIfActive(HidePlanPanel);
    private void OnSkillShowRequested() => PostIfActive(ShowSkillPanel);
    private void OnSkillHideRequested() => PostIfActive(HideSkillPanel);

    public void ShowCurrentBrowserController()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ShowCurrentBrowserController, DispatcherPriority.Loaded);
            return;
        }

        _browserView?.ShowCurrentController();
        Dispatcher.UIThread.Post(() => _browserView?.RefreshBounds(), DispatcherPriority.Loaded);
    }

    public void ShowBrowserPanel(Guid chatId)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowBrowserPanel(chatId));
            return;
        }

        _ = ShowBrowserPanelAsync(chatId);
    }

    public void HideBrowserPanel()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(HideBrowserPanel);
            return;
        }

        _ = HideBrowserPanelAsync();
    }

    public void ShowDiffPanel(FileChangeItem fileChange)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowDiffPanel(fileChange));
            return;
        }

        EnsureDiffViewLoaded();
        ResetDiffTitle();
        _diffTitleText.Text = Path.GetFileName(fileChange.FilePath);
        _diffView?.SetFileChangeDiff(fileChange);
        _ = ShowDiffPanelAnimatedAsync();
    }

    public void HideDiffPanel()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(HideDiffPanel);
            return;
        }

        _ = HidePreviewPanelAsync(_diffPanel, () => _viewModel.IsDiffOpen = false);
    }

    public void ShowGitChangesPanel(List<GitFileChangeViewModel> files)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowGitChangesPanel(files));
            return;
        }

        _lastGitChangesList = files;
        ResetDiffTitle();

        var tertiaryBrush = GetThemeBrush("Brush.TextTertiary", Brushes.Gray);
        var listPanel = new StackPanel { Spacing = 2, Margin = new Thickness(8, 4) };
        foreach (var file in files)
        {
            var row = CreateGitDiffRow(file, listPanel.Children.Count, tertiaryBrush);
            row.Click += (_, _) => ShowGitFileDiffWithBackNav(file);
            listPanel.Children.Add(row);
        }

        _diffTitleText.Text = $"Changes ({files.Count})";
        _diffHost.Content = new ScrollViewer
        {
            Content = listPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        _ = ShowDiffPanelAnimatedAsync();
    }

    public void ShowPlanPanel()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ShowPlanPanel);
            return;
        }

        _ = ShowPlanPanelAsync();
    }

    public void HidePlanPanel()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(HidePlanPanel);
            return;
        }

        _ = HidePreviewPanelAsync(_planPanel, () => _viewModel.IsPlanOpen = false);
    }

    public void ShowSkillPanel()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ShowSkillPanel);
            return;
        }

        _ = ShowSkillPanelAsync();
    }

    public void HideSkillPanel()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(HideSkillPanel);
            return;
        }

        _ = HidePreviewPanelAsync(_skillPanel, () => _viewModel.IsSkillOpen = false);
    }

    private async Task ShowBrowserPanelAsync(Guid chatId)
    {
        var browserService = _viewModel.GetBrowserServiceForChat(chatId);
        if (browserService is null)
            return;

        HidePreviewPanelsExcept(_browserPanel);
        EnsureBrowserViewLoaded(browserService);

        if (_browserPanel.IsVisible)
        {
            _viewModel.IsBrowserOpen = true;
            _browserView?.RefreshBounds();
            if (browserService.Controller is not null)
                browserService.Controller.IsVisible = true;
            return;
        }

        _ensureChatVisible?.Invoke();
        EnsureSplitLayout(_browserPanel);

        _browserPanel.RenderTransform = new TranslateTransform(PreviewOffsetX, 0);
        _browserPanel.Opacity = 0;
        _browserPanel.IsVisible = true;
        if (_splitter is not null)
            _splitter.IsVisible = true;

        var ct = ReplaceCancellationTokenSource(ref _browserAnimCts).Token;
        try
        {
            await CreatePreviewAnimation(PreviewOffsetX, 0, 0, 1, ShowDuration, new CubicEaseOut())
                .RunAsync(_browserPanel, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
            return;

        _browserPanel.Opacity = 1;
        _browserPanel.RenderTransform = null;
        _viewModel.IsBrowserOpen = true;

        if (browserService.Controller is not null)
            browserService.Controller.IsVisible = true;
        Dispatcher.UIThread.Post(() => _browserView?.RefreshBounds(), DispatcherPriority.Loaded);
    }

    private async Task HideBrowserPanelAsync()
    {
        if (!_browserPanel.IsVisible)
            return;

        _browserView?.ClearBrowserService();

        var ct = ReplaceCancellationTokenSource(ref _browserAnimCts).Token;
        _browserPanel.RenderTransform = new TranslateTransform(0, 0);

        try
        {
            await CreatePreviewAnimation(0, PreviewOffsetX, 1, 0, HideDuration, new CubicEaseIn())
                .RunAsync(_browserPanel, ct);
        }
        catch (OperationCanceledException)
        {
        }

        _browserPanel.IsVisible = false;
        _browserPanel.Opacity = 1;
        _browserPanel.RenderTransform = null;
        _viewModel.IsBrowserOpen = false;
        CollapseSplitLayoutIfIdle();
    }

    private async Task ShowDiffPanelAnimatedAsync()
    {
        HidePreviewPanelsExcept(_diffPanel);
        _ensureChatVisible?.Invoke();
        EnsureSplitLayout(_diffPanel);

        _diffPanel.RenderTransform = new TranslateTransform(PreviewOffsetX, 0);
        _diffPanel.Opacity = 0;
        _diffPanel.IsVisible = true;
        if (_splitter is not null)
            _splitter.IsVisible = true;

        var ct = ReplaceCancellationTokenSource(ref _previewAnimCts).Token;
        try
        {
            await CreatePreviewAnimation(PreviewOffsetX, 0, 0, 1, ShowDuration, new CubicEaseOut())
                .RunAsync(_diffPanel, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
            return;

        _diffPanel.Opacity = 1;
        _diffPanel.RenderTransform = null;
        _viewModel.IsDiffOpen = true;
    }

    private async Task ShowPlanPanelAsync()
    {
        HidePreviewPanelsExcept(_planPanel);
        _ensureChatVisible?.Invoke();
        EnsureSplitLayout(_planPanel);

        _planPanel.RenderTransform = new TranslateTransform(PreviewOffsetX, 0);
        _planPanel.Opacity = 0;
        _planPanel.IsVisible = true;
        if (_splitter is not null)
            _splitter.IsVisible = true;

        var ct = ReplaceCancellationTokenSource(ref _previewAnimCts).Token;
        try
        {
            await CreatePreviewAnimation(PreviewOffsetX, 0, 0, 1, ShowDuration, new CubicEaseOut())
                .RunAsync(_planPanel, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
            return;

        _planPanel.Opacity = 1;
        _planPanel.RenderTransform = null;
        _viewModel.IsPlanOpen = true;
    }

    private async Task ShowSkillPanelAsync()
    {
        HidePreviewPanelsExcept(_skillPanel);
        _ensureChatVisible?.Invoke();
        EnsureSplitLayout(_skillPanel);

        _skillPanel.RenderTransform = new TranslateTransform(PreviewOffsetX, 0);
        _skillPanel.Opacity = 0;
        _skillPanel.IsVisible = true;
        if (_splitter is not null)
            _splitter.IsVisible = true;

        var ct = ReplaceCancellationTokenSource(ref _previewAnimCts).Token;
        try
        {
            await CreatePreviewAnimation(PreviewOffsetX, 0, 0, 1, ShowDuration, new CubicEaseOut())
                .RunAsync(_skillPanel, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
            return;

        _skillPanel.Opacity = 1;
        _skillPanel.RenderTransform = null;
        _viewModel.IsSkillOpen = true;
    }

    private async Task HidePreviewPanelAsync(Border panel, Action markClosed)
    {
        if (!panel.IsVisible)
            return;

        var ct = ReplaceCancellationTokenSource(ref _previewAnimCts).Token;
        panel.RenderTransform = new TranslateTransform(0, 0);

        try
        {
            await CreatePreviewAnimation(0, PreviewOffsetX, 1, 0, HideDuration, new CubicEaseIn())
                .RunAsync(panel, ct);
        }
        catch (OperationCanceledException)
        {
        }

        panel.IsVisible = false;
        panel.Opacity = 1;
        panel.RenderTransform = null;
        markClosed();
        CollapseSplitLayoutIfIdle();
    }

    private void EnsureSplitLayout(Control previewPanel)
    {
        var defs = _contentGrid.ColumnDefinitions;
        while (defs.Count < 3)
            defs.Add(new ColumnDefinition());

        defs[0].Width = new GridLength(1, GridUnitType.Star);
        defs[1].Width = GridLength.Auto;
        defs[2].Width = new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(_primaryPane, 0);
        Grid.SetColumn(previewPanel, 2);
    }

    private void CollapseSplitLayoutIfIdle()
    {
        if (_browserPanel.IsVisible || _diffPanel.IsVisible || _planPanel.IsVisible || _skillPanel.IsVisible)
            return;

        var defs = _contentGrid.ColumnDefinitions;
        while (defs.Count < 3)
            defs.Add(new ColumnDefinition());

        defs[0].Width = new GridLength(1, GridUnitType.Star);
        defs[1].Width = new GridLength(0);
        defs[2].Width = new GridLength(0);
        Grid.SetColumn(_primaryPane, 0);
        if (_splitter is not null)
            _splitter.IsVisible = false;
    }

    private void HidePreviewPanelsExcept(Border keep)
    {
        if (!ReferenceEquals(keep, _browserPanel))
        {
            _browserView?.ClearBrowserService();
            HideImmediately(_browserPanel);
            _viewModel.IsBrowserOpen = false;
        }

        if (!ReferenceEquals(keep, _diffPanel))
        {
            HideImmediately(_diffPanel);
            _viewModel.IsDiffOpen = false;
        }

        if (!ReferenceEquals(keep, _planPanel))
        {
            HideImmediately(_planPanel);
            _viewModel.IsPlanOpen = false;
        }

        if (!ReferenceEquals(keep, _skillPanel))
        {
            HideImmediately(_skillPanel);
            _viewModel.IsSkillOpen = false;
        }
    }

    private static void HideImmediately(Border panel)
    {
        panel.IsVisible = false;
        panel.Opacity = 1;
        panel.RenderTransform = null;
    }

    private void EnsureBrowserViewLoaded(BrowserService browserService)
    {
        _browserView ??= new BrowserView();
        if (_browserHost.Content != _browserView)
            _browserHost.Content = _browserView;

        var isDark = Application.Current?.RequestedThemeVariant == ThemeVariant.Dark;
        browserService.SetTheme(isDark);
        _browserView.SetBrowserService(browserService, _dataStore);
    }

    private void EnsureDiffViewLoaded()
    {
        _diffView ??= new DiffView();
        if (_diffHost.Content != _diffView)
            _diffHost.Content = _diffView;
    }

    private void ResetDiffTitle()
    {
        _diffTitleText.PointerPressed -= OnDiffBreadcrumbClick;
        _diffTitleText.Cursor = null;
        _diffTitleText.Inlines?.Clear();
        _diffTitleText.Inlines = null;
    }

    private Button CreateGitDiffRow(GitFileChangeViewModel file, int rowIndex, IBrush tertiaryBrush)
    {
        var kindColor = file.Kind switch
        {
            GitChangeKind.Added or GitChangeKind.Untracked => new SolidColorBrush(Color.FromRgb(63, 185, 80)),
            GitChangeKind.Deleted => new SolidColorBrush(Color.FromRgb(248, 81, 73)),
            GitChangeKind.Renamed => new SolidColorBrush(Color.FromRgb(88, 166, 255)),
            _ => new SolidColorBrush(Color.FromRgb(210, 153, 34))
        };

        var row = new Button
        {
            Name = CreateGitDiffRowName(file.FileName, rowIndex),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 8),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = CreateGitDiffRowContent(file, kindColor, tertiaryBrush),
        };
        AutomationProperties.SetName(row, $"Open diff for {file.FileName}");
        row.Classes.Add("subtle");
        return row;
    }

    private static DockPanel CreateGitDiffRowContent(GitFileChangeViewModel file, SolidColorBrush kindColor, IBrush tertiaryBrush)
    {
        var content = new DockPanel();

        var badge = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(kindColor.Color, 0.15),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock
            {
                Text = file.KindIcon,
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                Foreground = kindColor,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        DockPanel.SetDock(badge, Dock.Left);
        content.Children.Add(badge);

        var textStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = file.FileName,
            FontSize = 12,
            FontWeight = FontWeight.Medium,
        });
        if (!string.IsNullOrEmpty(file.Directory))
        {
            textStack.Children.Add(new TextBlock
            {
                Text = file.Directory,
                FontSize = 10,
                Foreground = tertiaryBrush,
            });
        }
        content.Children.Add(textStack);

        if (file.HasStats)
            content.Children.Insert(1, CreateStatsPanel(file));

        return content;
    }

    private static StackPanel CreateStatsPanel(GitFileChangeViewModel file)
    {
        var statsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        DockPanel.SetDock(statsPanel, Dock.Right);

        if (file.LinesAdded > 0)
        {
            statsPanel.Children.Add(new TextBlock
            {
                Text = $"+{file.LinesAdded}",
                FontSize = 11,
                FontFamily = MonoFontFamily,
                Foreground = new SolidColorBrush(Color.FromRgb(63, 185, 80)),
            });
        }

        if (file.LinesRemoved > 0)
        {
            statsPanel.Children.Add(new TextBlock
            {
                Text = $"-{file.LinesRemoved}",
                FontSize = 11,
                FontFamily = MonoFontFamily,
                Foreground = new SolidColorBrush(Color.FromRgb(248, 81, 73)),
            });
        }

        return statsPanel;
    }

    private static string CreateGitDiffRowName(string fileName, int rowIndex)
    {
        var safeFileName = new string(fileName.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        return $"GitDiffRow_{rowIndex}_{safeFileName}";
    }

    private void ShowGitFileDiffWithBackNav(GitFileChangeViewModel file)
    {
        var diffView = new DiffView();
        _diffHost.Content = diffView;

        ResetDiffTitle();
        _diffTitleText.Text = null;
        var inlines = _diffTitleText.Inlines ??= new InlineCollection();

        var accentBrush = GetThemeBrush("Brush.AccentDefault", Brushes.DodgerBlue);
        var tertiaryBrush = GetThemeBrush("Brush.TextTertiary", Brushes.Gray);
        inlines.Add(new Run("Changes") { Foreground = accentBrush });
        inlines.Add(new Run("  >  ") { Foreground = tertiaryBrush });
        inlines.Add(new Run(file.FileName));

        _diffTitleText.Cursor = new Cursor(StandardCursorType.Hand);
        _diffTitleText.PointerPressed += OnDiffBreadcrumbClick;

        if (file.Change.Kind is GitChangeKind.Added or GitChangeKind.Untracked)
        {
            _ = ShowAddedGitFileDiffAsync(file.Change.FullPath, diffView);
        }
        else
        {
            _ = LoadGitUnifiedDiffAsync(file.Change, diffView);
        }
    }

    private void OnDiffBreadcrumbClick(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (_lastGitChangesList is not null)
            ShowGitChangesPanel(_lastGitChangesList);
    }

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

    private static async Task LoadGitUnifiedDiffAsync(GitFileChange change, DiffView diffView)
    {
        var repoDir = Path.GetDirectoryName(change.FullPath) ?? "";
        var diff = await GitService.GetFileDiffAsync(repoDir, Path.GetFileName(change.FullPath)).ConfigureAwait(false)
            ?? await GitService.GetFileDiffAsync(repoDir, change.RelativePath).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() => diffView.SetUnifiedDiffText(change.FullPath, diff));
    }

    private IBrush GetThemeBrush(string resourceKey, IBrush fallback)
    {
        return _resourceScope.TryFindResource(resourceKey, _resourceScope.ActualThemeVariant, out var resource) && resource is IBrush brush
            ? brush
            : fallback;
    }

    private static Animation CreatePreviewAnimation(
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
