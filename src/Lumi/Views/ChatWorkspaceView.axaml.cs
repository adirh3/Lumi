using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.Presence;
using Lumi.Services;
using Lumi.ViewModels;
using StrataTheme.Controls;

namespace Lumi.Views;

public partial class ChatWorkspaceView : UserControl, IDisposable
{
    public static readonly StyledProperty<bool> ShowInternalTitleProperty =
        AvaloniaProperty.Register<ChatWorkspaceView, bool>(nameof(ShowInternalTitle), true);

    public static readonly StyledProperty<bool> UseChatIslandChromeProperty =
        AvaloniaProperty.Register<ChatWorkspaceView, bool>(nameof(UseChatIslandChrome), true);

    public static readonly StyledProperty<Thickness> PreviewIslandMarginProperty =
        AvaloniaProperty.Register<ChatWorkspaceView, Thickness>(nameof(PreviewIslandMargin), new Thickness(0));

    private ChatPreviewPanelController? _previewPanel;
    private ChatView? _chatView;
    private Border? _chatIsland;
    private Border? _chatIslandHighlight;
    private DataStore? _dataStore;
    private DataStore? _attachedDataStore;
    private ChatViewModel? _attachedChatViewModel;

    // The decoupled ambient-glow layer: a single self-contained controller owns the visual and
    // observes already-public view-model state. No production view pushes to it.
    private PresenceController? _presenceController;

    private const double RailMinHostWidth = 1040;
    private Border? _workspaceRail;
    private ChatViewModel? _railViewModel;

    // Open/close slide+fade animation state.
    private const double RailSlideOffset = 34;
    private static readonly TimeSpan RailShowDuration = TimeSpan.FromMilliseconds(290);
    private static readonly TimeSpan RailHideDuration = TimeSpan.FromMilliseconds(190);

    private CancellationTokenSource? _railAnimCts;
    private bool? _railShown;

    public ChatWorkspaceView()
    {
        InitializeComponent();
    }

    public bool ShowInternalTitle
    {
        get => GetValue(ShowInternalTitleProperty);
        set => SetValue(ShowInternalTitleProperty, value);
    }

    public bool UseChatIslandChrome
    {
        get => GetValue(UseChatIslandChromeProperty);
        set => SetValue(UseChatIslandChromeProperty, value);
    }

    public Thickness PreviewIslandMargin
    {
        get => GetValue(PreviewIslandMarginProperty);
        set => SetValue(PreviewIslandMarginProperty, value);
    }

    public DataStore? DataStore
    {
        get => _dataStore;
        set
        {
            if (ReferenceEquals(_dataStore, value))
                return;

            _dataStore = value;
            ReconnectPreviewPanel();
        }
    }

    public Action? EnsureChatVisible { get; set; }

    public Func<Guid, bool>? CanShowBrowserPanel { get; set; }

    public ChatView? ChatView => _chatView;

    public bool IsBrowserOpen => _previewPanel?.IsBrowserOpen == true;

    public bool IsDiffOpen => _previewPanel?.IsDiffOpen == true;

    public bool IsPlanOpen => _previewPanel?.IsPlanOpen == true;

    public void FocusComposer() => _chatView?.FocusComposer();

    public void ShowBrowserPanel(Guid chatId) => _previewPanel?.ShowBrowserPanel(chatId);

    public void HideBrowserPanel() => _previewPanel?.HideBrowserPanel();

    public void ShowCurrentBrowserController() => _previewPanel?.ShowCurrentBrowserController();

    public void ShowDiffPanel(FileChangeItem fileChange) => _previewPanel?.ShowDiffPanel(fileChange);

    public void HideDiffPanel() => _previewPanel?.HideDiffPanel();

    public void ShowGitChangesPanel(List<GitFileChangeViewModel> files) => _previewPanel?.ShowGitChangesPanel(files);

    public void ShowPlanPanel() => _previewPanel?.ShowPlanPanel();

    public void HidePlanPanel() => _previewPanel?.HidePlanPanel();

    public bool IsSkillOpen => _previewPanel?.IsSkillOpen == true;

    public void ShowSkillPanel() => _previewPanel?.ShowSkillPanel();

    public void HideSkillPanel() => _previewPanel?.HideSkillPanel();

    public void Dispose()
    {
        DisposePreviewPanel();
        GC.SuppressFinalize(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ReconnectPreviewPanel();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ReconnectPreviewPanel();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DisposePreviewPanel();
        DisposePresence();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == UseChatIslandChromeProperty)
            ApplyChatIslandChrome();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _chatView = this.FindControl<ChatView>("PageChat");
        _chatIsland = this.FindControl<Border>("ChatIsland");
        _chatIslandHighlight = this.FindControl<Border>("ChatIslandHighlight");
        _workspaceRail = this.FindControl<Border>("WorkspaceRail");

        SizeChanged += OnHostSizeChanged;

        AutomationProperties.SetName(this, "Page 0 Chat content grid");
        AutomationProperties.SetHelpText(this, "Stable Lumi chat workspace landmark for coding agents and MCP diagnostics.");
        if (_chatView is not null)
        {
            AutomationProperties.SetName(_chatView, "Page 0 Chat view");
            AutomationProperties.SetHelpText(_chatView, "Stable Lumi chat view landmark for coding agents and MCP diagnostics.");
        }

        if (this.FindControl<Button>("CloseBrowserButton") is { } closeBrowserButton)
            closeBrowserButton.Click += (_, _) => HideBrowserPanel();
        if (this.FindControl<Button>("CloseDiffButton") is { } closeDiffButton)
            closeDiffButton.Click += (_, _) => HideDiffPanel();
        if (this.FindControl<Button>("ClosePlanButton") is { } closePlanButton)
            closePlanButton.Click += (_, _) => HidePlanPanel();
        if (this.FindControl<Button>("CloseSkillButton") is { } closeSkillButton)
            closeSkillButton.Click += (_, _) => HideSkillPanel();

        ApplyChatIslandChrome();
    }

    private void ApplyChatIslandChrome()
    {
        if (_chatIsland is null)
            return;

        _chatIsland.Classes.Set("workspace-island", UseChatIslandChrome);
        _chatIsland.Classes.Set("workspace-flat", !UseChatIslandChrome);

        if (_chatIslandHighlight is not null)
            _chatIslandHighlight.IsVisible = UseChatIslandChrome;
    }

    private void ReconnectPreviewPanel()
    {
        var chatViewModel = DataContext as ChatViewModel;

        if (ReferenceEquals(_attachedDataStore, _dataStore) &&
            ReferenceEquals(_attachedChatViewModel, chatViewModel))
        {
            return;
        }

        DisposePreviewPanel();

        if (_dataStore is null || chatViewModel is null)
            return;

        var contentGrid = this.FindControl<Grid>("WorkspaceGrid")
            ?? throw new InvalidOperationException("Chat workspace is missing WorkspaceGrid.");
        var chatIsland = this.FindControl<Control>("ChatIsland")
            ?? throw new InvalidOperationException("Chat workspace is missing ChatIsland.");
        var previewSplitter = this.FindControl<GridSplitter>("PreviewSplitter");
        var browserPanel = this.FindControl<Border>("BrowserIsland")
            ?? throw new InvalidOperationException("Chat workspace is missing BrowserIsland.");
        var browserHost = this.FindControl<ContentControl>("BrowserHost")
            ?? throw new InvalidOperationException("Chat workspace is missing BrowserHost.");
        var diffPanel = this.FindControl<Border>("DiffIsland")
            ?? throw new InvalidOperationException("Chat workspace is missing DiffIsland.");
        var diffHost = this.FindControl<ContentControl>("DiffHost")
            ?? throw new InvalidOperationException("Chat workspace is missing DiffHost.");
        var diffFileNameText = this.FindControl<TextBlock>("DiffFileNameText")
            ?? throw new InvalidOperationException("Chat workspace is missing DiffFileNameText.");
        var planPanel = this.FindControl<Border>("PlanIsland")
            ?? throw new InvalidOperationException("Chat workspace is missing PlanIsland.");
        var skillPanel = this.FindControl<Border>("SkillIsland")
            ?? throw new InvalidOperationException("Chat workspace is missing SkillIsland.");

        _previewPanel = new ChatPreviewPanelController(
            this,
            _dataStore,
            chatViewModel,
            contentGrid,
            chatIsland,
            previewSplitter,
            browserPanel,
            browserHost,
            diffPanel,
            diffHost,
            diffFileNameText,
            planPanel,
            skillPanel,
            ensureChatVisible: () => EnsureChatVisible?.Invoke(),
            canShowBrowserPanel: chatId => CanShowBrowserPanel?.Invoke(chatId) != false);

        _attachedDataStore = _dataStore;
        _attachedChatViewModel = chatViewModel;

        // Persistent ambient field: created ONCE for the lifetime of this (reused) workspace grid and
        // RE-POINTED at the new surface on a chat switch. ChatWorkspaceView is a single named element in
        // the shell whose DataContext is rebound when ChatVM swaps (each chat owns its own surface), so
        // the grid — and the glow living in it — survive the swap. Disposing + recreating the controller
        // here (as the preview panel below does) would destroy the welcome glow and spawn a fresh one
        // already at the chat state, so the field could never visibly GLIDE from the hero down to the
        // composer — it would just appear there. Keeping the SAME field and repointing it is what lets
        // the presence travel from welcome to an opened chat.
        if (_presenceController is null)
        {
            _presenceController = new PresenceController(contentGrid);
            _presenceController.Attach(chatViewModel);
        }
        else
        {
            _presenceController.Repoint(chatViewModel);
        }

        AttachWorkspaceRail(chatViewModel);
    }

    private void OnHostSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateRailVisibility();

    private void AttachWorkspaceRail(ChatViewModel viewModel)
    {
        if (!ReferenceEquals(_railViewModel, viewModel))
        {
            if (_railViewModel is not null)
            {
                _railViewModel.WorkspaceContentChanged -= OnWorkspaceContentChanged;
                _railViewModel.WorkspacePanelPreferenceChanged -= OnWorkspacePanelPreferenceChanged;
            }

            _railViewModel = viewModel;
            _railViewModel.WorkspaceContentChanged += OnWorkspaceContentChanged;
            _railViewModel.WorkspacePanelPreferenceChanged += OnWorkspacePanelPreferenceChanged;
        }

        UpdateRailVisibility();
    }

    private void DetachWorkspaceRail()
    {
        if (_railViewModel is not null)
        {
            _railViewModel.WorkspaceContentChanged -= OnWorkspaceContentChanged;
            _railViewModel.WorkspacePanelPreferenceChanged -= OnWorkspacePanelPreferenceChanged;
            _railViewModel = null;
        }

        _railShown = null;

        if (_workspaceRail is not null)
        {
            _workspaceRail.IsVisible = false;
            _workspaceRail.Opacity = 1;
            _workspaceRail.RenderTransform = null;
        }
    }

    private void OnWorkspaceContentChanged() => UpdateRailVisibility();

    private void OnWorkspacePanelPreferenceChanged() => UpdateRailVisibility();

    /// <summary>
    /// Resolves the rail's effective visibility from the persisted tri-state preference and pushes it
    /// back onto the view-model so the toggle button reflects state. <c>true</c>/<c>false</c> are explicit
    /// user choices (work at any width); <c>null</c> is "auto" — show only when there's content and the
    /// host is wide enough that stealing ~352px still leaves a comfortable reading column.
    /// </summary>
    private void UpdateRailVisibility()
    {
        if (_workspaceRail is null || _railViewModel is null)
            return;

        var preference = _dataStore?.Data.Settings.WorkspacePanelOpen;
        var hasContent = _railViewModel.HasWorkspaceContent;
        var wideEnough = Bounds.Width >= RailMinHostWidth;

        var visible = preference switch
        {
            true => true,
            false => false,
            _ => hasContent && wideEnough,
        };

        _railViewModel.IsWorkspacePanelOpen = visible;
        ApplyRailVisibility(visible);
    }

    /// <summary>
    /// Drives the rail's open/close with a slide+fade. The very first resolution (initial load or a
    /// chat switch) is applied instantly so the panel never animates just from navigating.
    /// </summary>
    private void ApplyRailVisibility(bool visible)
    {
        if (_workspaceRail is null)
            return;

        if (_railShown == visible)
            return;

        var firstResolve = _railShown is null;
        _railShown = visible;

        if (firstResolve)
        {
            DisposeCts(ref _railAnimCts);
            _workspaceRail.Opacity = 1;
            _workspaceRail.RenderTransform = null;
            _workspaceRail.IsVisible = visible;
            return;
        }

        _ = AnimateRailAsync(visible);
    }

    private async Task AnimateRailAsync(bool visible)
    {
        if (_workspaceRail is null)
            return;

        var token = ReplaceCts(ref _railAnimCts).Token;

        if (visible)
        {
            _workspaceRail.RenderTransform = new TranslateTransform(RailSlideOffset, 0);
            _workspaceRail.Opacity = 0;
            _workspaceRail.IsVisible = true;

            try
            {
                await CreateSlideFade(RailSlideOffset, 0, 0, 1, RailShowDuration, new CubicEaseOut())
                    .RunAsync(_workspaceRail, token);
            }
            catch (OperationCanceledException) { return; }

            if (token.IsCancellationRequested)
                return;

            _workspaceRail.Opacity = 1;
            _workspaceRail.RenderTransform = null;
        }
        else
        {
            _workspaceRail.RenderTransform = new TranslateTransform(0, 0);

            try
            {
                await CreateSlideFade(0, RailSlideOffset, 1, 0, RailHideDuration, new CubicEaseIn())
                    .RunAsync(_workspaceRail, token);
            }
            catch (OperationCanceledException) { return; }

            if (token.IsCancellationRequested)
                return;

            _workspaceRail.IsVisible = false;
            _workspaceRail.Opacity = 1;
            _workspaceRail.RenderTransform = null;
        }
    }

    private static Animation CreateSlideFade(
        double fromX, double toX, double fromOpacity, double toOpacity, TimeSpan duration, Easing easing)
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

    private static CancellationTokenSource ReplaceCts(ref CancellationTokenSource? source)
    {
        DisposeCts(ref source);
        source = new CancellationTokenSource();
        return source;
    }

    private static void DisposeCts(ref CancellationTokenSource? source)
    {
        if (source is null)
            return;

        try
        {
            source.Cancel();
            source.Dispose();
        }
        catch (ObjectDisposedException) { }

        source = null;
    }

    private void DisposePreviewPanel()
    {
        DetachWorkspaceRail();
        DisposeCts(ref _railAnimCts);
        // The presence controller is intentionally NOT disposed here: it persists across chat-surface
        // swaps (this method runs on every DataContext rebind) and is torn down only when the view
        // genuinely leaves the visual tree — see DisposePresence / OnDetachedFromVisualTree.
        _previewPanel?.Dispose();
        _previewPanel = null;
        _attachedDataStore = null;
        _attachedChatViewModel = null;
    }

    private void DisposePresence()
    {
        _presenceController?.Dispose();
        _presenceController = null;
    }
}
