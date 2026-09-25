using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Lumi.Presence;
using Lumi.Services;
using Lumi.ViewModels;

namespace Lumi.Views;

/// <summary>
/// The chat surface shared by the main window and detached chat windows: the chat island plus the
/// Workspace panel beside it. The panel's pages, sizing and motion live in
/// <see cref="WorkspacePanelController"/>, recreated whenever the view is pointed at another chat.
/// </summary>
public partial class ChatWorkspaceView : UserControl, IDisposable
{
    public static readonly StyledProperty<bool> ShowInternalTitleProperty =
        AvaloniaProperty.Register<ChatWorkspaceView, bool>(nameof(ShowInternalTitle), true);

    public static readonly StyledProperty<bool> UseChatIslandChromeProperty =
        AvaloniaProperty.Register<ChatWorkspaceView, bool>(nameof(UseChatIslandChrome), true);

    public static readonly StyledProperty<bool> IsPresenceEnabledProperty =
        AvaloniaProperty.Register<ChatWorkspaceView, bool>(nameof(IsPresenceEnabled), true);

    public static readonly StyledProperty<Thickness> WorkspacePanelMarginProperty =
        AvaloniaProperty.Register<ChatWorkspaceView, Thickness>(nameof(WorkspacePanelMargin), new Thickness(0));

    private WorkspacePanelController? _workspace;
    private ChatView? _chatView;
    private Border? _chatIsland;
    private Border? _chatIslandHighlight;
    private Border? _workspacePanel;
    private Control? _workspaceHeaderBar;
    private DataStore? _dataStore;
    private DataStore? _attachedDataStore;
    private ChatViewModel? _attachedChatViewModel;

    // The decoupled ambient-glow layer: a single self-contained controller owns the visual and
    // observes already-public view-model state. No production view pushes to it.
    private PresenceController? _presenceController;

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

    public bool IsPresenceEnabled
    {
        get => GetValue(IsPresenceEnabledProperty);
        set => SetValue(IsPresenceEnabledProperty, value);
    }

    /// <summary>Insets the Workspace panel (the detached window keeps it off the window edges).</summary>
    public Thickness WorkspacePanelMargin
    {
        get => GetValue(WorkspacePanelMarginProperty);
        set => SetValue(WorkspacePanelMarginProperty, value);
    }

    public DataStore? DataStore
    {
        get => _dataStore;
        set
        {
            if (ReferenceEquals(_dataStore, value))
                return;

            _dataStore = value;
            ReconnectWorkspace();
        }
    }

    public Action? EnsureChatVisible { get; set; }

    public Func<Guid, bool>? CanShowBrowserPanel { get; set; }

    public ChatView? ChatView => _chatView;

    public WorkspacePage WorkspacePage => _workspace?.Page ?? WorkspacePage.Overview;

    public bool IsWorkspaceOpen => _workspace?.IsOpen == true;

    public bool IsBrowserOpen => _workspace?.IsBrowserOpen == true;

    public void FocusComposer() => _chatView?.FocusComposer();

    public void ShowCurrentBrowserController() => _workspace?.ShowCurrentBrowserController();

    /// <summary>Closes any open page (chat switch, leaving the chat); the overview follows the user's preference.</summary>
    public void CloseWorkspacePages() => _workspace?.ClosePages();

    public void Dispose()
    {
        DisposeWorkspace();
        GC.SuppressFinalize(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ReconnectWorkspace();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ReconnectWorkspace();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DisposeWorkspace();
        DisposePresence();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == UseChatIslandChromeProperty)
            ApplyChatIslandChrome();
        else if (change.Property == IsPresenceEnabledProperty)
            _presenceController?.SetEnabled(change.GetNewValue<bool>());
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _chatView = this.FindControl<ChatView>("PageChat");
        _chatIsland = this.FindControl<Border>("ChatIsland");
        _chatIslandHighlight = this.FindControl<Border>("ChatIslandHighlight");
        _workspacePanel = this.FindControl<Border>("WorkspacePanel");
        _workspaceHeaderBar = this.FindControl<Control>("WorkspaceHeaderBar");

        SizeChanged += (_, _) => _workspace?.RefreshVisibility();
        _workspacePanel?.AddHandler(KeyDownEvent, OnWorkspaceKeyDown, RoutingStrategies.Bubble);

        AutomationProperties.SetName(this, "Page 0 Chat content grid");
        AutomationProperties.SetHelpText(this, "Stable Lumi chat workspace landmark for coding agents and MCP diagnostics.");
        if (_chatView is not null)
        {
            AutomationProperties.SetName(_chatView, "Page 0 Chat view");
            AutomationProperties.SetHelpText(_chatView, "Stable Lumi chat view landmark for coding agents and MCP diagnostics.");
        }

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

    private void OnWorkspaceBackClick(object? sender, RoutedEventArgs e) => _workspace?.GoBack();

    private void OnWorkspaceCloseClick(object? sender, RoutedEventArgs e) => _workspace?.Close();

    private void OnRefreshFilePreviewClick(object? sender, RoutedEventArgs e) => _workspace?.RefreshFilePreview();

    private void OnOpenFilePreviewClick(object? sender, RoutedEventArgs e) => _workspace?.OpenFilePreviewExternally();

    /// <summary>Escape steps back: out of a page, else from one kind back to everything.</summary>
    private void OnWorkspaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled || _workspace is null)
            return;

        if (_workspace.Page != WorkspacePage.Overview)
            _workspace.GoBack();
        else if (DataContext is ChatViewModel { WorkspaceCategory: not WorkspaceCategory.All } viewModel)
            viewModel.WorkspaceCategory = WorkspaceCategory.All;
        else
            return;

        e.Handled = true;
    }

    private void ReconnectWorkspace()
    {
        var chatViewModel = DataContext as ChatViewModel;

        if (ReferenceEquals(_attachedDataStore, _dataStore) &&
            ReferenceEquals(_attachedChatViewModel, chatViewModel))
        {
            return;
        }

        DisposeWorkspace();

        if (_dataStore is null || chatViewModel is null)
            return;

        var contentGrid = this.FindControl<Grid>("WorkspaceGrid")
            ?? throw new InvalidOperationException("Chat workspace is missing WorkspaceGrid.");
        var parts = new WorkspacePanelParts(
            contentGrid,
            this.FindControl<Control>("ChatIsland")
                ?? throw new InvalidOperationException("Chat workspace is missing ChatIsland."),
            this.FindControl<GridSplitter>("WorkspaceSplitter"),
            _workspacePanel ?? throw new InvalidOperationException("Chat workspace is missing WorkspacePanel."),
            this.FindControl<Control>("WorkspaceOverview")
                ?? throw new InvalidOperationException("Chat workspace is missing WorkspaceOverview."),
            this.FindControl<ContentControl>("WorkspacePageHost")
                ?? throw new InvalidOperationException("Chat workspace is missing WorkspacePageHost."));

        _workspace = new WorkspacePanelController(
            this,
            _dataStore,
            chatViewModel,
            parts,
            ensureChatVisible: () => EnsureChatVisible?.Invoke(),
            canShowBrowserPanel: chatId => CanShowBrowserPanel?.Invoke(chatId) != false);

        if (_workspaceHeaderBar is not null)
            _workspaceHeaderBar.DataContext = _workspace.Header;

        _attachedDataStore = _dataStore;
        _attachedChatViewModel = chatViewModel;

        // Persistent ambient field: created ONCE for the lifetime of this (reused) workspace grid and
        // RE-POINTED at the new surface on a chat switch. ChatWorkspaceView is a single named element in
        // the shell whose DataContext is rebound when ChatVM swaps (each chat owns its own surface), so
        // the grid — and the glow living in it — survive the swap. Disposing + recreating the controller
        // here (as the workspace panel is) would destroy the welcome glow and spawn a fresh one
        // already at the chat state, so the field could never visibly GLIDE from the hero down to the
        // composer — it would just appear there. Keeping the SAME field and repointing it is what lets
        // the presence travel from welcome to an opened chat.
        if (_presenceController is null)
        {
            _presenceController = new PresenceController(contentGrid, IsPresenceEnabled);
            _presenceController.Attach(chatViewModel);
        }
        else
        {
            _presenceController.Repoint(chatViewModel);
        }
    }

    private void DisposeWorkspace()
    {
        // The presence controller is intentionally NOT disposed here: it persists across chat-surface
        // swaps (this method runs on every DataContext rebind) and is torn down only when the view
        // genuinely leaves the visual tree — see DisposePresence / OnDetachedFromVisualTree.
        _workspace?.Dispose();
        _workspace = null;
        if (_workspaceHeaderBar is not null)
            _workspaceHeaderBar.DataContext = null;
        _attachedDataStore = null;
        _attachedChatViewModel = null;
    }

    private void DisposePresence()
    {
        _presenceController?.Dispose();
        _presenceController = null;
    }
}
