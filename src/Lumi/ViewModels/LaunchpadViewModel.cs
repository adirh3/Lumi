using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

/// <summary>A project's identity on the launchpad: its initial on a tint of its own hue.</summary>
public sealed record LaunchpadProjectBadge(Guid ProjectId, string Initial, IBrush Foreground, IBrush Background)
{
    // Mid-tone hues read on both the dark and the light island, like chat tag colors do. Amber is
    // left out: on the launchpad it means "waiting on you".
    private static readonly Color[] Hues =
    [
        Color.Parse("#818CF8"),
        Color.Parse("#34D399"),
        Color.Parse("#F472B6"),
        Color.Parse("#38BDF8"),
        Color.Parse("#A78BFA"),
        Color.Parse("#2DD4BF"),
    ];

    private static int HueIndex(Guid projectId) => (int)((uint)projectId.GetHashCode() % (uint)Hues.Length);

    internal static LaunchpadProjectBadge For(Project project)
    {
        var hue = Hues[HueIndex(project.Id)];
        return new LaunchpadProjectBadge(
            project.Id,
            InitialOf(project.Name),
            new SolidColorBrush(hue).ToImmutable(),
            new SolidColorBrush(Color.FromArgb(0x2E, hue.R, hue.G, hue.B)).ToImmutable());
    }

    internal static string InitialOf(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return "•";

        var first = System.Globalization.StringInfo.GetNextTextElement(trimmed);
        return first.ToUpper(Loc.Culture);
    }
}

/// <summary>A chat on the launchpad's "pick up where you left off" list, updated in place while shown.</summary>
public sealed partial class LaunchpadChatItem : ObservableObject
{
    private readonly Action<LaunchpadChatItem> _open;

    internal LaunchpadChatItem(Chat chat, Action<LaunchpadChatItem> open)
    {
        Chat = chat;
        _open = open;
    }

    public Chat Chat { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _projectName = "";
    [ObservableProperty] private string _stateLabel = "";
    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private string _timeLabel = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProjectBadge), nameof(StateIcon), nameof(HasStateIcon),
        nameof(BadgeInitial), nameof(BadgeForeground), nameof(BadgeBackground))]
    private LaunchpadProjectBadge? _projectBadge;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting), nameof(IsReply), nameof(IsWorking), nameof(IsBackground), nameof(IsRecent),
        nameof(ShowProjectBadge), nameof(StateIcon), nameof(HasStateIcon))]
    private LaunchpadChatState _state = LaunchpadChatState.Recent;

    public bool IsWaiting => State == LaunchpadChatState.Waiting;
    public bool IsReply => State == LaunchpadChatState.Reply;
    public bool IsWorking => State == LaunchpadChatState.Working;
    public bool IsBackground => State == LaunchpadChatState.Background;
    public bool IsRecent => State == LaunchpadChatState.Recent;
    public bool HasStateLabel => StateLabel.Length > 0;

    /// <summary>An idle chat shows where it lives; a live one shows what it is doing instead.</summary>
    public bool ShowProjectBadge => IsRecent && ProjectBadge is not null;

    public string BadgeInitial => ProjectBadge?.Initial ?? "";
    public IBrush? BadgeForeground => ProjectBadge?.Foreground;
    public IBrush? BadgeBackground => ProjectBadge?.Background;

    public bool HasStateIcon => StateIcon is not null;

    /// <summary>The quiet second line: the chat's project and what was last said.</summary>
    public string Meta => ProjectName.Length > 0 && Preview.Length > 0
        ? ProjectName + " · " + Preview
        : ProjectName + Preview;

    public bool HasSecondLine => HasStateLabel || Meta.Length > 0;

    /// <summary>Glyph for a question or a project-less idle chat; the other states draw a badge, dot or ring.</summary>
    public Geometry? StateIcon => State switch
    {
        LaunchpadChatState.Waiting => WorkspaceIcons.Question,
        LaunchpadChatState.Recent when ProjectBadge is null => WorkspaceIcons.Messages,
        _ => null,
    };

    partial void OnProjectNameChanged(string value) => OnSecondLineChanged();
    partial void OnStateLabelChanged(string value)
    {
        OnPropertyChanged(nameof(HasStateLabel));
        OnSecondLineChanged();
    }

    partial void OnPreviewChanged(string value) => OnSecondLineChanged();

    private void OnSecondLineChanged()
    {
        OnPropertyChanged(nameof(Meta));
        OnPropertyChanged(nameof(HasSecondLine));
    }

    [RelayCommand]
    private void Open() => _open(this);
}

/// <summary>One of the user's usual chat setups, applied to the draft with a click.</summary>
public sealed partial class LaunchpadSetupItem : ObservableObject
{
    private readonly LaunchpadProjectBadge? _badge;
    private readonly Action<LaunchpadSetupItem> _select;

    internal LaunchpadSetupItem(
        LaunchpadSetupSpec spec,
        string title,
        string meta,
        LaunchpadProjectBadge? badge,
        Geometry? icon,
        string? glyphText,
        Action<LaunchpadSetupItem> select)
    {
        Spec = spec;
        Title = title;
        Meta = meta;
        _badge = badge;
        Icon = icon;
        GlyphText = glyphText;
        _select = select;
    }

    internal LaunchpadSetupSpec Spec { get; }

    public string Title { get; }
    public string Meta { get; }
    public Geometry? Icon { get; }
    public string? GlyphText { get; }
    public bool HasBadge => _badge is not null;
    public string BadgeInitial => _badge?.Initial ?? "";
    public IBrush? BadgeForeground => _badge?.Foreground;
    public IBrush? BadgeBackground => _badge?.Background;
    public bool HasIcon => Icon is not null;
    public bool HasGlyphText => !string.IsNullOrEmpty(GlyphText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private bool _isActive;

    /// <summary>True when clicking the applied setup restores the draft it replaced.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private bool _canUndo;

    public string ToolTip => IsActive
        ? CanUndo ? Loc.Launchpad_SetupActiveTooltip : Loc.Launchpad_SetupAppliedTooltip
        : string.Format(Loc.Culture, Loc.Launchpad_SetupTooltip, Meta.Length > 0 ? $"{Title} · {Meta}" : Title);

    [RelayCommand]
    private void Select() => _select(this);
}

/// <summary>A conversation starter: sends its prompt as the first message of the new chat.</summary>
public sealed partial class LaunchpadStarterItem : ObservableObject
{
    private readonly Action<LaunchpadStarterItem> _send;

    internal LaunchpadStarterItem(LaunchpadStarterSpec spec, Action<LaunchpadStarterItem> send)
    {
        Spec = spec;
        _send = send;
    }

    internal LaunchpadStarterSpec Spec { get; }
    public string Glyph => Spec.Glyph;
    public string Label => Spec.Label;
    public string Prompt => Spec.Prompt;

    [RelayCommand]
    private void Send() => _send(this);
}

/// <summary>
/// Lumi's launchpad: the new-chat surface. It greets the user for the time of day, says what is
/// waiting on them across every chat, lists the chats worth picking up, offers the setups they
/// usually start work with, and suggests starters that fit the hour.
/// <para>It only observes the store while a chat view is in the new-chat state (<see cref="Activate"/> /
/// <see cref="Deactivate"/>), so cached drafts and open chats hold no subscriptions.</para>
/// </summary>
public sealed partial class LaunchpadViewModel : ObservableObject
{
    private static readonly TimeSpan ClockInterval = TimeSpan.FromSeconds(60);

    private readonly ChatViewModel _owner;
    private readonly DataStore _dataStore;
    private readonly ChatEventHub _chatEvents;
    private readonly HashSet<Chat> _watchedChats = [];
    private DispatcherTimer? _clock;
    private int _activeViews;
    private bool _refreshQueued;
    private bool _setupsStale = true;
    private LaunchpadDayPart? _starterDayPart;
    private bool _starterHistory;

    internal LaunchpadViewModel(ChatViewModel owner, DataStore dataStore, ChatEventHub chatEvents)
    {
        _owner = owner;
        _dataStore = dataStore;
        _chatEvents = chatEvents;
    }

    public ObservableCollection<LaunchpadChatItem> PickUpItems { get; } = [];
    public ObservableCollection<LaunchpadSetupItem> Setups { get; } = [];
    public ObservableCollection<LaunchpadStarterItem> Starters { get; } = [];

    [ObservableProperty] private string _greetingLead = "";
    [ObservableProperty] private string _greetingName = "";
    [ObservableProperty] private string _greetingTrail = "";
    [ObservableProperty] private string _brief = "";
    [ObservableProperty] private string _automationSummary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivityCard), nameof(HasPickUpAndAutomations))]
    private bool _hasPickUpItems;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivityCard), nameof(HasPickUpAndAutomations))]
    private bool _hasAutomations;

    [ObservableProperty] private bool _hasSetups;

    /// <summary>True while any chat is working for the user, which the brief marks with a live dot.</summary>
    [ObservableProperty] private bool _hasLiveWork;

    /// <summary>The card holding the pick-up list and the automations footer.</summary>
    public bool HasActivityCard => HasPickUpItems || HasAutomations;

    public bool HasPickUpAndAutomations => HasPickUpItems && HasAutomations;

    internal bool IsActive => _activeViews > 0;

    /// <summary>Starts observing chats and the clock for a view that is now in the new-chat state.</summary>
    public void Activate()
    {
        if (_activeViews++ > 0)
            return;

        _setupsStale = true;
        _chatEvents.EventPublished += OnChatEventPublished;
        _dataStore.IndexSaved += OnIndexSaved;
        _owner.PropertyChanged += OnOwnerPropertyChanged;
        _owner.AvailableModels.CollectionChanged += OnAvailableModelsChanged;
        _clock = new DispatcherTimer(ClockInterval, DispatcherPriority.Background, (_, _) => QueueRefresh());
        Refresh();
    }

    /// <summary>Stops observing once no view is in the new-chat state for this surface.</summary>
    public void Deactivate()
    {
        if (_activeViews == 0 || --_activeViews > 0)
            return;

        _chatEvents.EventPublished -= OnChatEventPublished;
        _dataStore.IndexSaved -= OnIndexSaved;
        _owner.PropertyChanged -= OnOwnerPropertyChanged;
        _owner.AvailableModels.CollectionChanged -= OnAvailableModelsChanged;
        _clock?.Stop();
        _clock = null;
        WatchChats([]);
    }

    /// <summary>Forces teardown when the owning surface is disposed, however many views were attached.</summary>
    internal void Shutdown()
    {
        if (_activeViews == 0)
            return;

        _activeViews = 1;
        Deactivate();
    }

    [RelayCommand]
    private void OpenAutomations() => _owner.RequestOpenAutomations();

    private void QueueRefresh()
    {
        if (_refreshQueued || !IsActive)
            return;

        _refreshQueued = true;
        Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        _refreshQueued = false;
        if (!IsActive)
            return;

        var now = DateTimeOffset.Now;
        var chats = _dataStore.Data.Chats;
        var jobs = _dataStore.SnapshotBackgroundJobs();
        var automationChatIds = jobs
            .Where(static job => job.IsEnabled)
            .Select(static job => job.ChatId)
            .ToHashSet();

        var greeting = LaunchpadPlanner.BuildGreeting(_dataStore.Data.Settings.UserName, now.Hour);
        GreetingLead = greeting.Lead;
        GreetingName = greeting.Name;
        GreetingTrail = greeting.Trail;

        int waiting = 0, replies = 0, working = 0;
        var hasHistory = false;
        foreach (var chat in chats)
        {
            hasHistory |= LaunchpadPlanner.HasContent(chat);
            switch (LaunchpadPlanner.ClassifyActive(chat))
            {
                case LaunchpadChatState.Waiting: waiting++; break;
                case LaunchpadChatState.Reply: replies++; break;
                case LaunchpadChatState.Working or LaunchpadChatState.Background: working++; break;
            }
        }

        Brief = LaunchpadPlanner.BuildBrief(waiting, replies, working, hasHistory);
        HasLiveWork = working > 0;

        var pickUp = LaunchpadPlanner.SelectPickUp(chats, automationChatIds, LaunchpadPlanner.MaxPickUpItems);
        SyncPickUp(pickUp);
        HasPickUpItems = PickUpItems.Count > 0;

        AutomationSummary = LaunchpadPlanner.BuildAutomationSummary(jobs, now, out var hasAutomations);
        HasAutomations = hasAutomations;

        RefreshStarters(now.Hour, hasHistory);
        if (_setupsStale)
        {
            _setupsStale = false;
            RefreshSetups(chats, automationChatIds, now);
        }

        RefreshSetupActivity();

        // Any chat can start working, finish or ask something while the launchpad is up.
        WatchChats(chats);
    }

    private void SyncPickUp(List<(Chat Chat, LaunchpadChatState State)> desired)
    {
        var projects = _dataStore.Data.Projects;
        for (var index = 0; index < desired.Count; index++)
        {
            var (chat, state) = desired[index];
            var existing = FindPickUpItem(chat, index);
            LaunchpadChatItem item;
            if (existing == index)
            {
                item = PickUpItems[index];
            }
            else if (existing > index)
            {
                item = PickUpItems[existing];
                PickUpItems.Move(existing, index);
            }
            else
            {
                item = new LaunchpadChatItem(chat, OpenChat);
                PickUpItems.Insert(index, item);
            }

            item.Title = string.IsNullOrWhiteSpace(chat.Title) ? Loc.Library_UntitledChat : chat.Title;
            var project = chat.ProjectId is { } projectId
                ? projects.FirstOrDefault(candidate => candidate.Id == projectId)
                : null;
            item.ProjectName = project?.Name ?? "";
            // Badges carry brushes, so reuse the current one unless the project or its initial changed.
            if (project is null)
                item.ProjectBadge = null;
            else if (item.ProjectBadge is not { } badge
                     || badge.ProjectId != project.Id
                     || badge.Initial != LaunchpadProjectBadge.InitialOf(project.Name))
                item.ProjectBadge = LaunchpadProjectBadge.For(project);
            item.State = state;
            item.StateLabel = state switch
            {
                LaunchpadChatState.Waiting => Loc.Launchpad_StateWaiting,
                LaunchpadChatState.Reply => Loc.Launchpad_StateReply,
                LaunchpadChatState.Working => Loc.Launchpad_StateWorking,
                LaunchpadChatState.Background => Loc.Launchpad_StateBackground,
                _ => "",
            };
            item.Preview = state == LaunchpadChatState.Waiting && FindPendingQuestion(chat) is { } question
                ? LaunchpadPlanner.CleanPreview(question)
                : LaunchpadPlanner.CleanPreview(chat.Preview);
            item.TimeLabel = LibraryViewModel.FormatRelativeTime(chat.UpdatedAt);
        }

        while (PickUpItems.Count > desired.Count)
            PickUpItems.RemoveAt(PickUpItems.Count - 1);
    }

    private int FindPickUpItem(Chat chat, int start)
    {
        for (var index = start; index < PickUpItems.Count; index++)
        {
            if (ReferenceEquals(PickUpItems[index].Chat, chat))
                return index;
        }

        return -1;
    }

    /// <summary>The question a waiting chat is blocked on, so the row can ask it directly.</summary>
    private static string? FindPendingQuestion(Chat chat)
    {
        for (var index = chat.Messages.Count - 1; index >= 0; index--)
        {
            var message = chat.Messages[index];
            if (message.ToolName == "ask_question"
                && message.ToolStatus == "InProgress"
                && !string.IsNullOrWhiteSpace(message.QuestionText))
            {
                return message.QuestionText;
            }
        }

        return null;
    }

    private void RefreshStarters(int hour, bool hasHistory)
    {
        var dayPart = LaunchpadPlanner.DayPartOf(hour);
        if (dayPart == _starterDayPart && hasHistory == _starterHistory && Starters.Count > 0)
            return;

        _starterDayPart = dayPart;
        _starterHistory = hasHistory;
        Starters.Clear();
        foreach (var spec in LaunchpadPlanner.SelectStarters(hour, hasHistory))
            Starters.Add(new LaunchpadStarterItem(spec, SendStarter));
    }

    private void RefreshSetups(IReadOnlyList<Chat> chats, IReadOnlySet<Guid> automationChatIds, DateTimeOffset now)
    {
        var data = _dataStore.Data;
        var projects = data.Projects.ToDictionary(static project => project.Id);
        var agents = data.Agents.ToDictionary(static agent => agent.Id);
        var models = _owner.AvailableModels.ToHashSet(StringComparer.Ordinal);
        var catalogKnown = _owner.IsModelCatalogKnown;

        // What a new chat would run today, so an unavailable model or an unsupported effort never shows.
        (string? Model, string? Effort) ResolveModel(string? model, string? effort)
            => string.IsNullOrWhiteSpace(model) || (catalogKnown && !models.Contains(model))
                ? (null, null)
                : (model, _owner.ResolveReasoningEffortForModel(effort, model));

        var settings = data.Settings;
        var (defaultModel, defaultEffort) = ResolveModel(settings.PreferredModel, settings.ReasoningEffort);
        var specs = LaunchpadPlanner.SelectSetups(
            chats,
            automationChatIds,
            now,
            projects.ContainsKey,
            agents.ContainsKey,
            ResolveModel,
            plainDefault: new LaunchpadSetupSpec(null, null, false, defaultModel, defaultEffort),
            LaunchpadPlanner.MaxSetups);

        // Renamed projects or agents change a tile's text without changing what it applies.
        var items = specs.Select(spec => CreateSetupItem(spec, projects, agents)).ToList();
        if (items.Select(Describe).SequenceEqual(Setups.Select(Describe)))
            return;

        Setups.Clear();
        foreach (var item in items)
            Setups.Add(item);
        HasSetups = Setups.Count > 0;

        static (LaunchpadSetupSpec, string, string) Describe(LaunchpadSetupItem item) => (item.Spec, item.Title, item.Meta);
    }

    private LaunchpadSetupItem CreateSetupItem(
        LaunchpadSetupSpec spec,
        IReadOnlyDictionary<Guid, Project> projects,
        IReadOnlyDictionary<Guid, LumiAgent> agents)
    {
        var project = spec.ProjectId is { } projectId && projects.TryGetValue(projectId, out var p) ? p : null;
        var agent = spec.AgentId is { } agentId && agents.TryGetValue(agentId, out var a) ? a : null;
        var model = ChatViewModel.FormatModelDisplay(spec.ModelId);

        string title;
        LaunchpadProjectBadge? badge = null;
        Geometry? icon = null;
        string? glyph = null;
        var meta = new List<string>(4);
        if (project is not null)
        {
            title = project.Name;
            badge = LaunchpadProjectBadge.For(project);
            if (agent is not null)
                meta.Add(agent.Name);
        }
        else if (agent is not null)
        {
            title = agent.Name;
            glyph = string.IsNullOrWhiteSpace(agent.IconGlyph) ? null : agent.IconGlyph;
            icon = glyph is null ? WorkspaceIcons.Agents : null;
        }
        else
        {
            title = model ?? Loc.Launchpad_SetupNoProject;
            icon = WorkspaceIcons.Sparkle;
        }

        if (spec.UseWorktree)
            meta.Add(Loc.Git_Worktree);
        if (model is not null && !string.Equals(model, title, StringComparison.Ordinal))
            meta.Add(model);
        if (spec.Effort is { } effort)
            meta.Add(ModelSelectionHelper.EffortToDisplay(effort));
        if (project is null)
            meta.Add(Loc.Launchpad_SetupNoProject);

        return new LaunchpadSetupItem(spec, title, string.Join(" · ", meta), badge, icon, glyph, SelectSetup);
    }

    private void RefreshSetupActivity()
    {
        var draft = _owner.HasLaunchpadIncompatibleAgent ? null : _owner.GetLaunchpadDraftSetup();
        var canUndo = _owner.IsLaunchpadSetupApplied;
        foreach (var setup in Setups)
        {
            setup.IsActive = draft is not null && Matches(setup.Spec, draft);
            setup.CanUndo = canUndo;
        }
    }

    private static bool Matches(LaunchpadSetupSpec setup, LaunchpadSetupSpec draft)
        => setup.ProjectId == draft.ProjectId
           && setup.AgentId == draft.AgentId
           && setup.UseWorktree == draft.UseWorktree
           && (setup.ModelId is null || string.Equals(setup.ModelId, draft.ModelId, StringComparison.Ordinal))
           && (setup.Effort is null || string.Equals(setup.Effort, draft.Effort, StringComparison.Ordinal));

    private async void SelectSetup(LaunchpadSetupItem setup)
    {
        try
        {
            await _owner.ToggleLaunchpadSetupAsync(setup.Spec, setup.IsActive);
        }
        catch (Exception ex)
        {
            // Applying reads git state for the worktree toggle; a failure leaves the draft as far as it got.
            System.Diagnostics.Trace.TraceWarning($"[Launchpad] Applying a setup failed: {ex}");
        }
    }

    private void SendStarter(LaunchpadStarterItem starter)
    {
        if (_owner.SelectSuggestionCommand.CanExecute(starter.Prompt))
            _owner.SelectSuggestionCommand.Execute(starter.Prompt);
    }

    private void OpenChat(LaunchpadChatItem item) => _owner.RequestRevealChat(item.Chat.Id);

    private void OnChatEventPublished(ChatLifecycleEvent chatEvent) => QueueRefreshFromAnyThread();

    /// <summary>
    /// Chats, projects, agents and automations all persist through the index, so a save means
    /// something shown here may have changed, the setups included. Raised on any thread.
    /// </summary>
    private void OnIndexSaved()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnIndexSaved, DispatcherPriority.Background);
            return;
        }

        _setupsStale = true;
        QueueRefresh();
    }

    private void OnWatchedChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Chat.IsRunning)
            or nameof(Chat.IsSessionActive)
            or nameof(Chat.HasUnreadMessages)
            or nameof(Chat.IsAwaitingInput)
            or nameof(Chat.Title))
        {
            QueueRefreshFromAnyThread();
        }
    }

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChatViewModel.SelectedModel):
            case nameof(ChatViewModel.SelectedQuality):
            case nameof(ChatViewModel.IsWorktreeMode):
            case nameof(ChatViewModel.SelectedProjectName):
            case nameof(ChatViewModel.SelectedAgentName):
                RefreshSetupActivity();
                break;
            case nameof(ChatViewModel.ModelCatalogVersion):
                _setupsStale = true;
                QueueRefresh();
                break;
        }
    }

    private void OnAvailableModelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _setupsStale = true;
        QueueRefresh();
    }

    private void QueueRefreshFromAnyThread()
    {
        if (Dispatcher.UIThread.CheckAccess())
            QueueRefresh();
        else
            Dispatcher.UIThread.Post(QueueRefresh, DispatcherPriority.Background);
    }

    private void WatchChats(IEnumerable<Chat> chats)
    {
        var next = chats.ToHashSet();
        foreach (var chat in _watchedChats.Where(chat => !next.Contains(chat)).ToList())
        {
            chat.PropertyChanged -= OnWatchedChatPropertyChanged;
            _watchedChats.Remove(chat);
        }

        foreach (var chat in next)
        {
            if (_watchedChats.Add(chat))
                chat.PropertyChanged += OnWatchedChatPropertyChanged;
        }
    }
}
