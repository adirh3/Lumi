using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
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

    /// <summary>The project's own hue, shared by every surface that shows its identity.</summary>
    internal static Color HueFor(Guid projectId) => Hues[HueIndex(projectId)];

    internal static LaunchpadProjectBadge For(Project project)
    {
        var hue = HueFor(project.Id);
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

/// <summary>A chat waiting on the user, in the launchpad's "needs you" list; updated in place while shown.</summary>
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
    [NotifyPropertyChangedFor(nameof(IsWaiting), nameof(IsReply))]
    private LaunchpadChatState _state = LaunchpadChatState.Waiting;

    /// <summary>A question waits for the user's answer; otherwise a reply waits to be read.</summary>
    public bool IsWaiting => State == LaunchpadChatState.Waiting;
    public bool IsReply => State == LaunchpadChatState.Reply;

    /// <summary>The quiet second line: the chat's project and what it asks or said last.</summary>
    public string Meta => ProjectName.Length > 0 && Preview.Length > 0
        ? ProjectName + " · " + Preview
        : ProjectName + Preview;

    partial void OnProjectNameChanged(string value) => OnPropertyChanged(nameof(Meta));
    partial void OnPreviewChanged(string value) => OnPropertyChanged(nameof(Meta));

    [RelayCommand]
    private void Open() => _open(this);
}

/// <summary>One of the user's usual chat setups, applied to the draft with a click and adjusted from its menu.</summary>
public sealed partial class LaunchpadSetupItem : ObservableObject
{
    private readonly LaunchpadProjectBadge? _badge;
    private readonly LaunchpadSetupActions _actions;

    internal LaunchpadSetupItem(
        LaunchpadSetupChoice choice,
        string detail,
        bool isVariant,
        LaunchpadProjectBadge? badge,
        Geometry? icon,
        string? glyphText,
        bool canUseWorktree,
        IReadOnlyList<LaunchpadModelChoice> modelChoices,
        LaunchpadSetupActions actions)
    {
        Choice = choice;
        Spec = choice.Spec;
        Title = choice.Title;
        Meta = choice.Meta;
        Pin = choice.Pin;
        Detail = detail;
        IsVariant = isVariant;
        _badge = badge;
        Icon = icon;
        GlyphText = glyphText;
        CanUseWorktree = canUseWorktree;
        ModelChoices = modelChoices;
        _actions = actions;
    }

    internal LaunchpadSetupChoice Choice { get; }

    internal LaunchpadSetupSpec Spec { get; }

    /// <summary>The kept setup this is, which its menu edits; null for one learned from the user's chats.</summary>
    internal LaunchpadSetupPin? Pin { get; }

    public string Title { get; }

    /// <summary>Everything the setup brings (agent, checkout, model, effort): the tooltip and the phone's description.</summary>
    public string Meta { get; }

    /// <summary>
    /// What it starts with, beside its name while it's the chat's setup or always when it's a variant: the agent working
    /// in the project, or the model (with the effort, when that's all that tells two variants apart).
    /// </summary>
    public string Detail { get; }

    /// <summary>The row holds another setup with this name (Lumi on two models, say), so this one says what it starts with.</summary>
    public bool IsVariant { get; }

    public bool IsWorktree => Spec.UseWorktree;
    public Geometry? Icon { get; }
    public string? GlyphText { get; }
    public bool HasBadge => _badge is not null;
    public string BadgeInitial => _badge?.Initial ?? "";
    public IBrush? BadgeForeground => _badge?.Foreground;
    public IBrush? BadgeBackground => _badge?.Background;
    public bool HasIcon => Icon is not null;
    public bool HasGlyphText => !string.IsNullOrEmpty(GlyphText);

    /// <summary>The user kept it, so it stays on the new chat whatever they use most.</summary>
    public bool IsPinned => Pin is not null;

    /// <summary>Its project is a git repository, so it can start in a worktree.</summary>
    public bool CanUseWorktree { get; }

    /// <summary>The models it can start with, its own checked.</summary>
    public IReadOnlyList<LaunchpadModelChoice> ModelChoices { get; }

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
    private void Select() => _actions.Select(this);

    /// <summary>Keeps it on the new chat, or lets it go back to being suggested by use.</summary>
    [RelayCommand]
    private void ToggleKeep() => _actions.ToggleKeep(this);

    [RelayCommand]
    private void ToggleWorktree() => _actions.SetWorktree(this, !Spec.UseWorktree);

    /// <summary>Takes it off the new chat; once nothing is kept in its place, that place isn't suggested either.</summary>
    [RelayCommand]
    private void Hide() => _actions.Hide(this);

    internal void StartWith(string modelId) => _actions.SetModel(this, modelId);
}

/// <summary>What a setup's chip and menu do, kept by the launchpad that made it.</summary>
internal sealed record LaunchpadSetupActions(
    Action<LaunchpadSetupItem> Select,
    Action<LaunchpadSetupItem> ToggleKeep,
    Action<LaunchpadSetupItem> Hide,
    Action<LaunchpadSetupItem, string> SetModel,
    Action<LaunchpadSetupItem, bool> SetWorktree);

/// <summary>A model a setup can start with, in its menu.</summary>
public sealed class LaunchpadModelChoice
{
    internal LaunchpadModelChoice(string modelId, string title, bool isCurrent, Action select)
    {
        ModelId = modelId;
        Title = title;
        IsCurrent = isCurrent;
        SelectCommand = new RelayCommand(select);
    }

    public string ModelId { get; }
    public string Title { get; }
    public bool IsCurrent { get; }
    public IRelayCommand SelectCommand { get; }
}

/// <summary>One of the launchpad's tabs: For you, or a kind of thing to start.</summary>
public sealed class LaunchpadCategoryItem(string id, string title)
{
    public string Id { get; } = id;
    public string Title { get; } = title;
}

/// <summary>
/// A conversation starter. Picking it puts its prompt in the composer, where the user fills in its blanks and sends
/// it; nothing is sent on a click.
/// </summary>
public sealed partial class LaunchpadStarterItem : ObservableObject
{
    private readonly Action<LaunchpadStarterItem> _use;

    internal LaunchpadStarterItem(LaunchpadStarterSpec spec, Action<LaunchpadStarterItem> use)
    {
        Spec = spec;
        _use = use;
        Parts = LaunchpadTemplate.Parse(spec.Label);
        Blanks = LaunchpadTemplate.Blanks(spec.Prompt);
        Icon = LaunchpadStarterCatalog.IconOf(spec.Id);
    }

    internal LaunchpadStarterSpec Spec { get; }

    /// <summary>What keeps a starter's row when the list is rebuilt: its id (or its words, for one from a brief) and why it is here.</summary>
    internal string Key => KeyOf(Spec);

    internal static string KeyOf(LaunchpadStarterSpec spec) => $"{spec.Id ?? spec.Prompt}\u001F{spec.Reason}";

    /// <summary>The emoji the phone shows; the desktop row shows <see cref="Icon"/>.</summary>
    public string Glyph => Spec.Glyph;
    public Geometry Icon { get; }
    public string Label => Spec.Label;
    public string Prompt => Spec.Prompt;

    /// <summary>Why For you offers it, shown while the pointer rests on it; empty in the other tabs.</summary>
    public string Reason => Spec.Reason ?? "";
    public bool HasReason => !string.IsNullOrEmpty(Spec.Reason);

    /// <summary>A plan from the project's brief, which wears Lumi's accent among the starters.</summary>
    public bool IsFromBrief => Spec.Id is null;

    /// <summary>The label as words and blanks, for the capsule.</summary>
    public IReadOnlyList<LaunchpadTemplatePart> Parts { get; }

    /// <summary>The prompt's blanks, brackets included, which the composer selects in turn.</summary>
    public IReadOnlyList<string> Blanks { get; }

    /// <summary>The accessible name: the label with its blanks spoken as they read ("Plan a trip to where?").</summary>
    public string AccessibleName => string.Join(" ", Parts.Select(static part => part.Text));

    [RelayCommand]
    private void Use() => _use(this);
}

/// <summary>A project or agent the launchpad's Other list can start a chat in or with, and pin to the new chat.</summary>
public sealed partial class LaunchpadOtherItem : ObservableObject
{
    private readonly LaunchpadProjectBadge? _badge;
    private readonly Action<LaunchpadOtherItem> _select;
    private readonly Action<LaunchpadOtherItem> _togglePin;

    internal LaunchpadOtherItem(Guid id, bool isProject, string title, string meta, LaunchpadProjectBadge? badge,
        string? glyphText, bool isPinned, Action<LaunchpadOtherItem> select, Action<LaunchpadOtherItem> togglePin)
    {
        Id = id;
        IsProject = isProject;
        Title = title;
        Meta = meta;
        _badge = badge;
        GlyphText = glyphText;
        _isPinned = isPinned;
        _select = select;
        _togglePin = togglePin;
    }

    internal Guid Id { get; }
    internal bool IsProject { get; }

    public string Title { get; }
    public string Meta { get; }
    public bool HasMeta => Meta.Length > 0;
    public string? GlyphText { get; }
    public bool HasBadge => _badge is not null;
    public string BadgeInitial => _badge?.Initial ?? "";
    public IBrush? BadgeForeground => _badge?.Foreground;
    public IBrush? BadgeBackground => _badge?.Background;
    public bool HasGlyphText => !HasBadge && !string.IsNullOrEmpty(GlyphText);
    public bool HasIcon => !HasBadge && !HasGlyphText;
    public Geometry Icon => WorkspaceIcons.Agents;

    /// <summary>It has a chip on the new chat, which its pin keeps there.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinTooltip))]
    private bool _isPinned;

    public string PinTooltip => IsPinned ? Loc.Launchpad_OtherUnpin : Loc.Launchpad_OtherPin;

    [RelayCommand]
    private void Select() => _select(this);

    [RelayCommand]
    private void TogglePin() => _togglePin(this);
}

/// <summary>
/// Lumi's launchpad: the new-chat surface. It greets the user, says how many chats wait on them, and offers what to
/// start: For you (the project's plans from its brief, the user's habits, what suits the hour) and a tab per kind of
/// task, each with starters whose blanks the user fills in the composer. Above the composer it offers where to start:
/// Just chat, the setups the user keeps starting with, and every other project and agent.
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
    private bool _hasHistory;
    private string _setupModelsKey = "";
    private LaunchpadSetupSpec? _plainDefaultAtOpen;
    private readonly LaunchpadSetupActions _setupActions;

    private List<LaunchpadOtherItem> _otherProjects = [];
    private List<LaunchpadOtherItem> _otherAgents = [];

    internal LaunchpadViewModel(ChatViewModel owner, DataStore dataStore, ChatEventHub chatEvents)
    {
        _owner = owner;
        _dataStore = dataStore;
        _chatEvents = chatEvents;
        _setupActions = new LaunchpadSetupActions(SelectSetup, ToggleKeepSetup, HideSetup, SetSetupModel, SetSetupWorktree);
    }

    public ObservableCollection<LaunchpadChatItem> NeedsYouItems { get; } = [];
    public ObservableCollection<LaunchpadSetupItem> Setups { get; } = [];
    public ObservableCollection<LaunchpadCategoryItem> Categories { get; } = [];
    public ObservableCollection<LaunchpadStarterItem> Suggestions { get; } = [];
    public ObservableCollection<LaunchpadOtherItem> OtherProjects { get; } = [];
    public ObservableCollection<LaunchpadOtherItem> OtherAgents { get; } = [];

    [ObservableProperty] private string _greetingLead = "";
    [ObservableProperty] private string _greetingName = "";
    [ObservableProperty] private string _greetingTrail = "";

    /// <summary>One line under the greeting: where the project stands (its brief's opening), or the invitation.</summary>
    [ObservableProperty] private string _subtitle = "";

    [ObservableProperty] private string _needsYouLabel = "";
    [ObservableProperty] private bool _hasNeedsYou;
    [ObservableProperty] private bool _hasSetups;
    [ObservableProperty] private LaunchpadCategoryItem? _selectedCategory;

    /// <summary>The draft is a plain chat: no project and no agent.</summary>
    [ObservableProperty] private bool _isJustChatActive;
    [ObservableProperty] private string _justChatMeta = "";

    /// <summary>The setup the draft starts with, when the row holds it, which the row keeps in sight as it scrolls.</summary>
    [ObservableProperty] private LaunchpadSetupItem? _activeSetup;

    /// <summary>
    /// The draft starts somewhere the row has no chip for (picked from Other, or in the composer): the Other chip shows
    /// that place, so the row always says where the chat starts.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOtherBadge), nameof(HasOtherGlyph), nameof(ShowOtherAddIcon), nameof(CanKeepDraft))]
    private bool _isOtherActive;
    [ObservableProperty] private string _otherTitle = Loc.Launchpad_Other;
    [ObservableProperty] private string _otherMeta = "";

    /// <summary>
    /// The draft is a setup the row doesn't hold (set up in the composer, or picked from Other), so the Other chip offers
    /// to keep it: one click makes it the first of the user's setups, model, checkout and all.
    /// </summary>
    public bool CanKeepDraft => IsOtherActive;

    /// <summary>The setup Keep would keep, in words, for its tooltip and accessible name.</summary>
    [ObservableProperty] private string _keepDraftTooltip = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOtherBadge), nameof(HasOtherGlyph), nameof(ShowOtherAddIcon),
        nameof(OtherBadgeInitial), nameof(OtherBadgeForeground), nameof(OtherBadgeBackground))]
    private LaunchpadProjectBadge? _otherBadge;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOtherGlyph), nameof(ShowOtherAddIcon))]
    private string? _otherGlyph;

    public bool HasOtherBadge => IsOtherActive && OtherBadge is not null;
    public bool HasOtherGlyph => IsOtherActive && OtherBadge is null && !string.IsNullOrEmpty(OtherGlyph);
    public bool ShowOtherAddIcon => !HasOtherBadge && !HasOtherGlyph;
    public bool HasOtherMeta => OtherMeta.Length > 0;
    public string OtherBadgeInitial => OtherBadge?.Initial ?? "";
    public IBrush? OtherBadgeForeground => OtherBadge?.Foreground;
    public IBrush? OtherBadgeBackground => OtherBadge?.Background;

    partial void OnOtherMetaChanged(string value) => OnPropertyChanged(nameof(HasOtherMeta));

    [ObservableProperty] private string _otherQuery = "";
    [ObservableProperty] private bool _hasOtherProjects;
    [ObservableProperty] private bool _hasOtherAgents;
    [ObservableProperty] private bool _hasNoOtherMatch;

    /// <summary>Raised before the launchpad opens a chat, so the view can close what it has open.</summary>
    public event Action? ChatOpening;

    /// <summary>Raised when a project or agent is picked from the Other list.</summary>
    public event Action? OtherPicked;

    internal bool IsActive => _activeViews > 0;

    /// <summary>Starts observing chats and the clock for a view that is now in the new-chat state.</summary>
    public void Activate()
    {
        if (_activeViews++ > 0)
            return;

        _setupsStale = true;
        _plainDefaultAtOpen = null;
        // A new chat starts on For you.
        SelectedCategory = null;
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

    partial void OnSelectedCategoryChanged(LaunchpadCategoryItem? value)
    {
        // The launchpad always shows one tab.
        if (value is null && Categories.Count > 0 && IsActive)
        {
            SelectedCategory = Categories[0];
            return;
        }

        RefreshSuggestions();
    }

    partial void OnOtherQueryChanged(string value) => FilterOther();

    /// <summary>Plain chat again: no project, no agent, the default model. Already plain, it only returns to the composer.</summary>
    [RelayCommand]
    private async Task SelectJustChat()
    {
        if (IsJustChatActive)
        {
            _owner.RequestComposerFocus();
            return;
        }

        try
        {
            await _owner.ToggleLaunchpadSetupAsync(PlainSetup(), isActive: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"[Launchpad] Starting a plain chat failed: {ex}");
        }
    }

    /// <summary>Fills the Other list as it opens: every project, most recently used first, then every agent.</summary>
    [RelayCommand]
    private void OpenOther()
    {
        var data = _dataStore.Data;
        var chats = data.Chats.ToArray();
        var projectActivity = new Dictionary<Guid, DateTimeOffset>();
        var agentActivity = new Dictionary<Guid, DateTimeOffset>();
        foreach (var chat in chats)
        {
            if (chat.ProjectId is { } projectId && (!projectActivity.TryGetValue(projectId, out var seen) || chat.UpdatedAt > seen))
                projectActivity[projectId] = chat.UpdatedAt;
            if (chat.AgentId is { } agentId && (!agentActivity.TryGetValue(agentId, out var used) || chat.UpdatedAt > used))
                agentActivity[agentId] = chat.UpdatedAt;
        }

        var pins = data.PinnedSetups.ToArray();
        _otherProjects = data.Projects.ToArray()
            .OrderByDescending(project => projectActivity.GetValueOrDefault(project.Id))
            .ThenBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(project => new LaunchpadOtherItem(project.Id, isProject: true, project.Name,
                projectActivity.TryGetValue(project.Id, out var at) ? LibraryViewModel.FormatRelativeTime(at) : "",
                LaunchpadProjectBadge.For(project), glyphText: null,
                pins.Any(pin => IsKeptAt(pin, project.Id, isProject: true)), SelectOther, TogglePinOther))
            .ToList();
        _otherAgents = data.Agents.ToArray()
            .OrderByDescending(agent => agentActivity.GetValueOrDefault(agent.Id))
            .ThenBy(agent => agent.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(agent => new LaunchpadOtherItem(agent.Id, isProject: false, agent.Name,
                agentActivity.TryGetValue(agent.Id, out var at) ? LibraryViewModel.FormatRelativeTime(at) : "",
                badge: null, string.IsNullOrWhiteSpace(agent.IconGlyph) ? null : agent.IconGlyph,
                pins.Any(pin => IsKeptAt(pin, agent.Id, isProject: false)), SelectOther, TogglePinOther))
            .ToList();

        if (OtherQuery.Length > 0)
            OtherQuery = "";
        else
            FilterOther();
    }

    private void FilterOther()
    {
        var query = OtherQuery.Trim();
        Fill(OtherProjects, _otherProjects);
        Fill(OtherAgents, _otherAgents);
        HasOtherProjects = OtherProjects.Count > 0;
        HasOtherAgents = OtherAgents.Count > 0;
        HasNoOtherMatch = !HasOtherProjects && !HasOtherAgents;

        void Fill(ObservableCollection<LaunchpadOtherItem> target, List<LaunchpadOtherItem> source)
        {
            target.Clear();
            foreach (var item in source)
            {
                if (query.Length == 0 || item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                    target.Add(item);
            }
        }
    }

    /// <summary>Picks the first match, as Enter in the Other list's search does.</summary>
    internal void SelectFirstOther() => (OtherProjects.FirstOrDefault() ?? OtherAgents.FirstOrDefault())?.SelectCommand.Execute(null);

    private async void SelectOther(LaunchpadOtherItem item)
    {
        OtherPicked?.Invoke();
        try
        {
            await _owner.ToggleLaunchpadSetupAsync(SetupFor(item), isActive: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"[Launchpad] Starting in {item.Title} failed: {ex}");
        }
    }

    /// <summary>
    /// A project or agent picked from the Other list starts the way the user last worked there: that chat's agent or
    /// project, checkout and model. Never used yet, it keeps the draft's model.
    /// </summary>
    private LaunchpadSetupSpec SetupFor(LaunchpadOtherItem item) => SetupFor(_dataStore, _owner, item.Id, item.IsProject);

    internal static LaunchpadSetupSpec SetupFor(DataStore dataStore, ChatViewModel owner, Guid id, bool isProject)
    {
        var automationChatIds = AutomationChatIds(dataStore);
        var latest = dataStore.Data.Chats.ToArray()
            .Where(chat => CanTeachSetup(chat, automationChatIds) && (isProject ? chat.ProjectId == id : chat.AgentId == id))
            .MaxBy(static chat => chat.UpdatedAt);
        return SetupFrom(dataStore.Data, id, isProject, latest, CreateModelResolver(owner));
    }

    /// <summary>A chat that shows how the user starts in its place: the user's own, with something in it.</summary>
    private static bool CanTeachSetup(Chat chat, HashSet<Guid> automationChatIds)
        => LaunchpadPlanner.HasContent(chat)
           && string.IsNullOrWhiteSpace(chat.SdkAgentName)
           && !automationChatIds.Contains(chat.Id);

    private static LaunchpadSetupSpec SetupFrom(
        AppData data,
        Guid id,
        bool isProject,
        Chat? latest,
        Func<string?, string?, (string? Model, string? Effort)> resolveModel)
    {
        if (latest is null)
            return isProject ? new(id, null, false, null, null) : new(null, id, false, null, null);

        var projects = data.Projects.Select(static project => project.Id).ToHashSet();
        var agents = data.Agents.Select(static agent => agent.Id).ToHashSet();
        var spec = LaunchpadPlanner.ToSetupSpec(latest, projects.Contains, agents.Contains, resolveModel);
        return isProject ? spec with { ProjectId = id } : spec with { AgentId = id };
    }

    /// <summary>
    /// Other's list for the phone: the projects, most recently used first, then the agents, each with when the user last
    /// worked there and how a chat there starts (<see cref="SetupFor(DataStore, ChatViewModel, Guid, bool)"/>), found in
    /// one pass over the chats.
    /// </summary>
    internal static List<LaunchpadPlace> BuildPlaces(DataStore dataStore, ChatViewModel owner, int maxProjects, int maxAgents)
    {
        var data = dataStore.Data;
        var automationChatIds = AutomationChatIds(dataStore);
        var resolveModel = CreateModelResolver(owner);
        var projectActivity = new Dictionary<Guid, DateTimeOffset>();
        var agentActivity = new Dictionary<Guid, DateTimeOffset>();
        var projectLatest = new Dictionary<Guid, Chat>();
        var agentLatest = new Dictionary<Guid, Chat>();
        foreach (var chat in data.Chats.ToArray())
        {
            var teaches = CanTeachSetup(chat, automationChatIds);
            if (chat.ProjectId is { } projectId)
                Note(projectActivity, projectLatest, projectId, chat, teaches);
            if (chat.AgentId is { } agentId)
                Note(agentActivity, agentLatest, agentId, chat, teaches);
        }

        var pins = data.PinnedSetups.ToArray();
        var places = new List<LaunchpadPlace>();
        foreach (var project in data.Projects.ToArray()
                     .OrderByDescending(project => projectActivity.GetValueOrDefault(project.Id))
                     .ThenBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
                     .Take(maxProjects))
        {
            places.Add(new LaunchpadPlace(project.Id, IsProject: true, project.Name, HintOf(projectActivity, project.Id),
                pins.Any(pin => IsKeptAt(pin, project.Id, isProject: true)),
                SetupFrom(data, project.Id, isProject: true, projectLatest.GetValueOrDefault(project.Id), resolveModel)));
        }

        foreach (var agent in data.Agents.ToArray()
                     .OrderByDescending(agent => agentActivity.GetValueOrDefault(agent.Id))
                     .ThenBy(agent => agent.Name, StringComparer.CurrentCultureIgnoreCase)
                     .Take(maxAgents))
        {
            places.Add(new LaunchpadPlace(agent.Id, IsProject: false, agent.Name, HintOf(agentActivity, agent.Id),
                pins.Any(pin => IsKeptAt(pin, agent.Id, isProject: false)),
                SetupFrom(data, agent.Id, isProject: false, agentLatest.GetValueOrDefault(agent.Id), resolveModel)));
        }

        return places;

        static void Note(Dictionary<Guid, DateTimeOffset> activity, Dictionary<Guid, Chat> latest, Guid id, Chat chat, bool teaches)
        {
            if (!activity.TryGetValue(id, out var seen) || chat.UpdatedAt > seen)
                activity[id] = chat.UpdatedAt;
            if (teaches && (!latest.TryGetValue(id, out var known) || chat.UpdatedAt > known.UpdatedAt))
                latest[id] = chat;
        }

        static string HintOf(Dictionary<Guid, DateTimeOffset> activity, Guid id)
            => activity.TryGetValue(id, out var at) ? LibraryViewModel.FormatRelativeTime(at) : "";
    }

    /// <summary>
    /// Pins a project or agent from the Other list to the new chat: it joins the end of the row, starting the way the
    /// user last worked there, and the variants the row shows there stay. Pinned already, it lets go of every setup kept
    /// there. A place is pinned while any kept setup is in it: an agent's, in whatever project it was last used.
    /// </summary>
    private void TogglePinOther(LaunchpadOtherItem item)
    {
        var data = _dataStore.Data;
        var removed = data.PinnedSetups.RemoveAll(pin => IsKeptAt(pin, item.Id, item.IsProject));
        if (removed == 0)
            LaunchpadSetupKeeper.Keep(data, ShownRow(), SetupFor(item), joinsTheEnd: true);

        item.IsPinned = removed == 0;
        SaveSetupPins();
    }

    /// <summary>Whether a kept setup is in a project or agent of the Other list.</summary>
    internal static bool IsKeptAt(LaunchpadSetupPin pin, Guid id, bool isProject)
        => isProject ? pin.ProjectId == id : pin.AgentId == id;

    /// <summary>Keeps a learned setup on the new chat, or lets a kept one go back to being suggested by use.</summary>
    private void ToggleKeepSetup(LaunchpadSetupItem setup)
    {
        if (setup.IsPinned)
            LaunchpadSetupKeeper.StopKeeping(_dataStore.Data, ShownRow(), setup.Spec);
        else
            LaunchpadSetupKeeper.Keep(_dataStore.Data, ShownRow(), setup.Spec);
        SaveSetupPins();
    }

    /// <summary>
    /// Takes the setup off the new chat. Once nothing is kept in its place, the place isn't suggested by use either, so
    /// the setup doesn't come straight back as a learned one.
    /// </summary>
    private void HideSetup(LaunchpadSetupItem setup)
    {
        LaunchpadSetupKeeper.Hide(_dataStore.Data, setup.Pin, setup.Spec);
        SaveSetupPins();
    }

    /// <summary>Starts the setup on another model from now on, which keeps it so the choice holds.</summary>
    private void SetSetupModel(LaunchpadSetupItem setup, string modelId)
        => KeepAs(setup, setup.Spec with { ModelId = modelId, Effort = _owner.ResolveReasoningEffortForModel(setup.Spec.Effort, modelId) });

    private void SetSetupWorktree(LaunchpadSetupItem setup, bool useWorktree)
        => KeepAs(setup, setup.Spec with { UseWorktree = useWorktree });

    /// <summary>
    /// Keeps the changed setup in place of the one the user adjusted (a learned one is kept from now on). Its other kept
    /// variants stay, and so do the learned ones the row shows there; one the change made identical to it goes, since a
    /// setup is kept once.
    /// </summary>
    private void KeepAs(LaunchpadSetupItem setup, LaunchpadSetupSpec changed)
    {
        LaunchpadSetupKeeper.Retune(_dataStore.Data, ShownRow(), setup.Pin, setup.Spec, changed);
        SaveSetupPins();
    }

    /// <summary>
    /// Keeps the setup the Other chip shows, exactly as the composer has it, as the first of the user's setups. Setups
    /// kept for the same project stay, so one project can be kept on several models and checkouts; a place the user
    /// dismissed comes back.
    /// </summary>
    [RelayCommand]
    private void KeepDraft()
    {
        if (!IsOtherActive
            || _owner.HasLaunchpadIncompatibleAgent
            || _owner.GetLaunchpadDraftSetup() is not { } draft
            || draft is { ProjectId: null, AgentId: null })
        {
            return;
        }

        LaunchpadSetupKeeper.Keep(_dataStore.Data, ShownRow(), draft);
        SaveSetupPins();
    }

    /// <summary>The setups the row shows, as the keeper finds them.</summary>
    private List<LaunchpadSetupChoice> ShownRow() => Setups.Select(static setup => setup.Choice).ToList();

    private void SaveSetupPins()
    {
        _setupsStale = true;
        QueueRefresh();
        _ = _dataStore.SaveAsync();
    }

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

        var greeting = LaunchpadPlanner.BuildGreeting(_dataStore.Data.Settings.UserName, now.Hour);
        GreetingLead = greeting.Lead;
        GreetingName = greeting.Name;
        GreetingTrail = greeting.Trail;
        _hasHistory = chats.Any(LaunchpadPlanner.HasContent);

        var needsYou = LaunchpadPlanner.SelectNeedsYou(chats, LaunchpadPlanner.MaxNeedsYouItems, out var waiting);
        SyncNeedsYou(needsYou);
        NeedsYouLabel = waiting == 1 ? Loc.Launchpad_NeedsYouOne : string.Format(Loc.Culture, Loc.Launchpad_NeedsYouMany, waiting);
        HasNeedsYou = waiting > 0;

        RefreshScope();
        RefreshSuggestions();
        if (_setupsStale)
        {
            _setupsStale = false;
            RefreshSetups(now);
        }

        RefreshSetupActivity();

        // A chat can ask something or finish a reply while the launchpad is up.
        WatchChats(chats);
    }

    /// <summary>Where the new chat starts (a project, or none): its subtitle. The tabs never move.</summary>
    private void RefreshScope()
    {
        Subtitle = SubtitleFor(_dataStore.Data, _owner.LaunchpadProject);
        if (Categories.Count > 0)
            return;

        foreach (var id in (string[])[LaunchpadStarterCatalog.ForYou, .. LaunchpadStarterCatalog.Order])
            Categories.Add(new LaunchpadCategoryItem(id, LaunchpadStarterCatalog.Title(id)));
        SelectedCategory ??= Categories[0];
    }

    /// <summary>The line under the greeting where a new chat starts: its project brief's opening, or the invitation.</summary>
    internal static string SubtitleFor(AppData data, Project? project)
    {
        var headline = LaunchpadPlanner.BriefHeadline(BriefOf(data, project)?.Content);
        return headline.Length > 0 ? headline : Loc.Launchpad_Subtitle;
    }

    private static ContextBrief? BriefOf(AppData data, Project? project)
        => project is not null && data.Settings.EnableDailyBriefs
            ? data.Briefs.ToArray().FirstOrDefault(brief => brief.ProjectId == project.Id)
            : null;

    private void RefreshSuggestions()
    {
        if (!IsActive)
            return;

        var category = SelectedCategory?.Id ?? LaunchpadStarterCatalog.ForYou;
        var specs = category == LaunchpadStarterCatalog.ForYou ? SelectForYou() : LaunchpadStarterCatalog.In(category);
        SyncSuggestions(specs);
    }

    private List<LaunchpadStarterSpec> SelectForYou()
        => SelectForYou(_dataStore.Data, _owner.LaunchpadProject, _owner.IsCodingProject, _hasHistory, DateTimeOffset.Now);

    /// <summary>
    /// For you where a new chat starts (a project, or none), each with why it is here: the project's plans from its
    /// brief, the user's habits in this place, what suits the hour, then well-tried starters for the place. In a coding
    /// project the work comes before the hour.
    /// </summary>
    internal static List<LaunchpadStarterSpec> SelectForYou(
        AppData data,
        Project? project,
        bool isCodingProject,
        bool hasHistory,
        DateTimeOffset now)
    {
        var isCoding = project is not null && isCodingProject;
        var habitReason = Loc.Launchpad_ReasonHabit;
        var habits = LaunchpadStarterUse.Habits(data.StarterUses.ToArray(), project?.Id, now)
            .Select(LaunchpadStarterCatalog.Find)
            .OfType<LaunchpadStarterSpec>()
            .Select(habit => habit with { Reason = habitReason })
            .ToList();
        var timeOfDay = LaunchpadPlanner.SelectStarters(now.Hour, hasHistory)
            .Select(starter => starter with { Reason = LaunchpadPlanner.TimeOfDayReason(starter.Id, now.Hour) })
            .ToList();
        return LaunchpadPlanner.SelectForYou(
            BriefOf(data, project)?.Starters.ToArray() ?? [],
            project is null ? null : string.Format(Loc.Culture, Loc.Launchpad_ReasonBrief, project.Name),
            habits,
            timeOfDay,
            LaunchpadStarterCatalog.Fillers(isCoding),
            workFirst: isCoding);
    }

    /// <summary>Keeps the rows that stay, so a refresh that changes nothing moves nothing.</summary>
    private void SyncSuggestions(List<LaunchpadStarterSpec> specs)
    {
        if (specs.Select(LaunchpadStarterItem.KeyOf).SequenceEqual(Suggestions.Select(static item => item.Key)))
            return;

        for (var index = 0; index < specs.Count; index++)
        {
            var key = LaunchpadStarterItem.KeyOf(specs[index]);
            var existing = -1;
            for (var probe = index; probe < Suggestions.Count; probe++)
            {
                if (Suggestions[probe].Key == key)
                {
                    existing = probe;
                    break;
                }
            }

            if (existing < 0)
                Suggestions.Insert(index, new LaunchpadStarterItem(specs[index], UseStarter));
            else if (existing != index)
                Suggestions.Move(existing, index);
        }

        while (Suggestions.Count > specs.Count)
            Suggestions.RemoveAt(Suggestions.Count - 1);
    }

    /// <summary>Puts the starter in the composer, its first blank selected, and remembers the pick for this place.</summary>
    private void UseStarter(LaunchpadStarterItem starter)
    {
        if (starter.Spec.Id is { } id)
            LaunchpadStarterUse.Record(_dataStore.Data.StarterUses, id, _owner.LaunchpadProject?.Id, DateTimeOffset.Now);

        _owner.ComposeFromLaunchpad(starter.Prompt, starter.Blanks);
    }

    private void SyncNeedsYou(List<(Chat Chat, LaunchpadChatState State)> desired)
    {
        var projects = _dataStore.Data.Projects;
        for (var index = 0; index < desired.Count; index++)
        {
            var (chat, state) = desired[index];
            var existing = -1;
            for (var probe = index; probe < NeedsYouItems.Count; probe++)
            {
                if (ReferenceEquals(NeedsYouItems[probe].Chat, chat))
                {
                    existing = probe;
                    break;
                }
            }

            LaunchpadChatItem item;
            if (existing == index)
            {
                item = NeedsYouItems[index];
            }
            else if (existing > index)
            {
                item = NeedsYouItems[existing];
                NeedsYouItems.Move(existing, index);
            }
            else
            {
                item = new LaunchpadChatItem(chat, OpenChat);
                NeedsYouItems.Insert(index, item);
            }

            item.Title = string.IsNullOrWhiteSpace(chat.Title) ? Loc.Library_UntitledChat : chat.Title;
            item.ProjectName = chat.ProjectId is { } projectId
                ? projects.FirstOrDefault(candidate => candidate.Id == projectId)?.Name ?? ""
                : "";
            item.State = state;
            item.StateLabel = state == LaunchpadChatState.Waiting ? Loc.Launchpad_StateWaiting : Loc.Launchpad_StateReply;
            item.Preview = state == LaunchpadChatState.Waiting && FindPendingQuestion(chat) is { } question
                ? LaunchpadPlanner.CleanPreview(question)
                : LaunchpadPlanner.CleanPreview(chat.Preview);
            item.TimeLabel = LibraryViewModel.FormatRelativeTime(chat.UpdatedAt);
        }

        while (NeedsYouItems.Count > desired.Count)
            NeedsYouItems.RemoveAt(NeedsYouItems.Count - 1);
    }

    /// <summary>The question a waiting chat is blocked on, so the row can ask it directly.</summary>
    internal static string? FindPendingQuestion(Chat chat)
    {
        for (var index = chat.Messages.Count - 1; index >= 0; index--)
        {
            var message = chat.Messages[index];
            if (message.ToolName is "ask_question" or "ask_input"
                && message.ToolStatus == "InProgress"
                && !string.IsNullOrWhiteSpace(message.QuestionText))
            {
                return message.QuestionText;
            }
        }

        return null;
    }

    private void RefreshSetups(DateTimeOffset now)
    {
        var projects = _dataStore.Data.Projects.ToDictionary(static project => project.Id);
        var agents = _dataStore.Data.Agents.ToDictionary(static agent => agent.Id);
        var models = _owner.AvailableModels.ToList();
        var modelsKey = string.Join('\n', models);
        // Picking a model on a new chat also makes it the default, which Just chat follows; the setups keep the default
        // the launchpad opened with, so trying a model (to keep a setup with it, say) doesn't reshuffle the row.
        _plainDefaultAtOpen ??= PlainSetup();
        var choices = BuildSetupChoices(_dataStore, _owner, now, _plainDefaultAtOpen);
        var items = choices.Select(choice => CreateSetupItem(choice, choices, projects, agents, models)).ToList();
        if (modelsKey == _setupModelsKey && items.Select(Describe).SequenceEqual(Setups.Select(Describe)))
            return;

        _setupModelsKey = modelsKey;
        Setups.Clear();
        foreach (var item in items)
            Setups.Add(item);
        HasSetups = Setups.Count > 0;

        // The kept setup is compared by reference: the menu edits that very one.
        static (LaunchpadSetupSpec, string, string, string, bool, LaunchpadSetupPin?, bool) Describe(LaunchpadSetupItem item)
            => (item.Spec, item.Title, item.Meta, item.Detail, item.IsVariant, item.Pin, item.CanUseWorktree);
    }

    /// <summary>What a new chat would run today for a chat's last model and effort: nothing unavailable or unsupported.</summary>
    private static Func<string?, string?, (string? Model, string? Effort)> CreateModelResolver(ChatViewModel owner)
    {
        var models = owner.AvailableModels.ToHashSet(StringComparer.Ordinal);
        var catalogKnown = owner.IsModelCatalogKnown;
        return (model, effort) => string.IsNullOrWhiteSpace(model) || (catalogKnown && !models.Contains(model))
            ? (null, null)
            : (model, owner.ResolveReasoningEffortForModel(effort, model));
    }

    /// <summary>A plain chat: no project, no agent, and the user's default model.</summary>
    private static LaunchpadSetupSpec PlainSetup(DataStore dataStore, Func<string?, string?, (string? Model, string? Effort)> resolve)
    {
        var settings = dataStore.Data.Settings;
        var (model, effort) = resolve(settings.PreferredModel, settings.ReasoningEffort);
        return new LaunchpadSetupSpec(null, null, false, model, effort);
    }

    private LaunchpadSetupSpec PlainSetup() => PlainSetup(_dataStore, CreateModelResolver(_owner));

    /// <summary>What Just chat starts with: no project, no agent, and the user's default model.</summary>
    internal static LaunchpadSetupSpec PlainSetupFor(DataStore dataStore, ChatViewModel owner)
        => PlainSetup(dataStore, CreateModelResolver(owner));

    private static HashSet<Guid> AutomationChatIds(DataStore dataStore) => dataStore.SnapshotBackgroundJobs()
        .Where(static job => job.IsEnabled).Select(static job => job.ChatId).ToHashSet();

    /// <summary>
    /// The setups the new chat offers, the phone's included: every one the user kept, in their order (one project on
    /// several models included), then the ones they keep starting with, leaving out places they asked not to see.
    /// <paramref name="plainDefault"/> is what Just chat starts with (the user's default model unless given), which no
    /// learned setup repeats.
    /// </summary>
    internal static List<LaunchpadSetupChoice> BuildSetupChoices(
        DataStore dataStore,
        ChatViewModel owner,
        DateTimeOffset now,
        LaunchpadSetupSpec? plainDefault = null)
    {
        var data = dataStore.Data;
        var automationChatIds = dataStore.SnapshotBackgroundJobs()
            .Where(static job => job.IsEnabled).Select(static job => job.ChatId).ToHashSet();
        var projects = data.Projects.ToDictionary(static project => project.Id);
        var agents = data.Agents.ToDictionary(static agent => agent.Id);
        var resolveModel = CreateModelResolver(owner);
        var kept = data.PinnedSetups.ToArray()
            .Where(pin => (pin.ProjectId is not { } projectId || projects.ContainsKey(projectId))
                          && (pin.AgentId is not { } agentId || agents.ContainsKey(agentId)))
            .Select(pin =>
            {
                var (model, effort) = resolveModel(pin.ModelId, pin.Effort);
                return (Pin: pin, Spec: new LaunchpadSetupSpec(
                    pin.ProjectId, pin.AgentId, pin.UseWorktree && pin.ProjectId is not null, model, effort));
            })
            .Where(static entry => !entry.Spec.IsEmpty)
            .ToList();
        var pinned = kept.Select(static entry => entry.Spec).ToList();
        var hidden = data.HiddenSetups.ToArray().Select(static pin => (pin.ProjectId, pin.AgentId, pin.UseWorktree)).ToList();
        var learned = LaunchpadPlanner.SelectSetups(
            data.Chats,
            automationChatIds,
            now,
            projects.ContainsKey,
            agents.ContainsKey,
            resolveModel,
            plainDefault: plainDefault ?? PlainSetup(dataStore, resolveModel),
            LaunchpadPlanner.MaxSetups + pinned.Count + hidden.Count);

        return LaunchpadPlanner.MergeSetups(pinned, learned, hidden, LaunchpadPlanner.MaxSetups)
            .Select(entry => DescribeSetup(entry.Spec, projects, agents) with
            {
                Pin = entry.PinIndex >= 0 ? kept[entry.PinIndex].Pin : null,
            })
            .ToList();
    }

    private static LaunchpadSetupChoice DescribeSetup(
        LaunchpadSetupSpec spec,
        IReadOnlyDictionary<Guid, Project> projects,
        IReadOnlyDictionary<Guid, LumiAgent> agents)
    {
        var project = spec.ProjectId is { } projectId && projects.TryGetValue(projectId, out var p) ? p : null;
        var agent = spec.AgentId is { } agentId && agents.TryGetValue(agentId, out var a) ? a : null;
        var model = ChatViewModel.FormatModelDisplay(spec.ModelId);

        string title;
        var meta = new List<string>(4);
        if (project is not null)
        {
            title = project.Name;
            if (agent is not null)
                meta.Add(agent.Name);
        }
        else if (agent is not null)
        {
            title = agent.Name;
        }
        else
        {
            title = model ?? Loc.Launchpad_SetupNoProject;
        }

        if (spec.UseWorktree)
            meta.Add(Loc.Git_Worktree);
        if (model is not null && !string.Equals(model, title, StringComparison.Ordinal))
            meta.Add(model);
        if (spec.Effort is { } effort)
            meta.Add(ModelSelectionHelper.EffortToDisplay(effort));
        if (project is null)
            meta.Add(Loc.Launchpad_SetupNoProject);

        return new LaunchpadSetupChoice(spec, title, string.Join(" · ", meta));
    }

    private LaunchpadSetupItem CreateSetupItem(
        LaunchpadSetupChoice choice,
        IReadOnlyList<LaunchpadSetupChoice> row,
        IReadOnlyDictionary<Guid, Project> projects,
        IReadOnlyDictionary<Guid, LumiAgent> agents,
        IReadOnlyList<string> models)
    {
        var spec = choice.Spec;
        var project = spec.ProjectId is { } projectId ? projects.GetValueOrDefault(projectId) : null;
        var agent = spec.AgentId is { } agentId ? agents.GetValueOrDefault(agentId) : null;
        var badge = project is null ? null : LaunchpadProjectBadge.For(project);
        var glyph = project is null && !string.IsNullOrWhiteSpace(agent?.IconGlyph) ? agent.IconGlyph : null;
        var icon = project is not null || glyph is not null
            ? null
            : agent is not null ? WorkspaceIcons.Agents : WorkspaceIcons.Sparkle;
        var (detail, isVariant) = DescribeDetail(choice, row, projects, agents);
        var canUseWorktree = CanUseWorktree(project);
        LaunchpadSetupItem? item = null;
        var modelChoices = models
            .Select(id => new LaunchpadModelChoice(id, ChatViewModel.FormatModelDisplay(id) ?? id,
                string.Equals(id, spec.ModelId, StringComparison.Ordinal), () => SetSetupModel(item!, id)))
            .ToList();
        item = new LaunchpadSetupItem(choice, detail, isVariant, badge, icon, glyph, canUseWorktree, modelChoices, _setupActions);
        return item;
    }

    /// <summary>A project whose folder is a git repository can start in a worktree.</summary>
    internal static bool CanUseWorktree(Project? project)
        => project is { WorkingDirectory: { Length: > 0 } directory } && GitService.IsGitRepo(directory);

    /// <summary>
    /// What a setup's capsule says beside its name: the agent working in the project, or the model; the rest is in the
    /// tooltip. A variant (another setup in the row has its name) says all that tells it apart, so Lumi on two models, or
    /// in a worktree and in its folder, reads as two setups.
    /// </summary>
    internal static (string Detail, bool IsVariant) DescribeDetail(
        LaunchpadSetupChoice choice,
        IReadOnlyList<LaunchpadSetupChoice> row,
        IReadOnlyDictionary<Guid, Project> projects,
        IReadOnlyDictionary<Guid, LumiAgent> agents)
    {
        var spec = choice.Spec;
        var project = spec.ProjectId is { } projectId ? projects.GetValueOrDefault(projectId) : null;
        var agent = spec.AgentId is { } agentId ? agents.GetValueOrDefault(agentId) : null;
        var model = ChatViewModel.FormatModelDisplay(spec.ModelId);
        var modelDetail = model is not null && !string.Equals(model, choice.Title, StringComparison.Ordinal) ? model : null;
        var siblings = row.Where(other => !ReferenceEquals(other, choice)
                                          && string.Equals(other.Title, choice.Title, StringComparison.Ordinal)).ToList();
        if (siblings.Count == 0)
        {
            var detail = project is not null && agent is not null
                ? agent.Name
                : modelDetail ?? (project is null && agent is null ? Loc.Launchpad_SetupNoProject : "");
            return (detail, false);
        }

        var parts = new List<string>(4);
        if (project is not null && agent is not null)
            parts.Add(agent.Name);
        if (spec.UseWorktree && siblings.Any(other => !other.Spec.UseWorktree))
            parts.Add(Loc.Git_Worktree);
        if (modelDetail is not null)
            parts.Add(modelDetail);
        var onlyEffortDiffers = siblings.Any(other => other.Spec with { Effort = spec.Effort } == spec);
        if ((onlyEffortDiffers || parts.Count == 0) && spec.Effort is { } effort)
            parts.Add(ModelSelectionHelper.EffortToDisplay(effort));
        return (string.Join(" · ", parts), true);
    }

    private void RefreshSetupActivity()
    {
        var draft = _owner.HasLaunchpadIncompatibleAgent ? null : _owner.GetLaunchpadDraftSetup();
        var canUndo = _owner.IsLaunchpadSetupApplied;
        // One setup is the chat's: the one it is exactly, else one that leaves the model or effort to the chat.
        var active = draft is null
            ? null
            : Setups.FirstOrDefault(setup => setup.Spec == draft) ?? Setups.FirstOrDefault(setup => Matches(setup.Spec, draft));
        foreach (var setup in Setups)
        {
            setup.IsActive = ReferenceEquals(setup, active);
            setup.CanUndo = canUndo;
        }

        ActiveSetup = active;
        // Just chat is the plain draft no setup claims: a setup on another model, without project or agent, is that setup.
        IsJustChatActive = active is null && draft is { ProjectId: null, AgentId: null };
        JustChatMeta = ChatViewModel.FormatModelDisplay(IsJustChatActive ? draft!.ModelId : PlainSetup().ModelId) ?? "";

        IsOtherActive = draft is not null && !IsJustChatActive && active is null;
        var project = IsOtherActive ? _owner.LaunchpadProject : null;
        var agent = IsOtherActive ? _owner.ActiveAgent : null;
        OtherTitle = project?.Name ?? agent?.Name ?? Loc.Launchpad_Other;
        OtherMeta = IsOtherActive ? ChatViewModel.FormatModelDisplay(draft!.ModelId) ?? "" : "";
        KeepDraftTooltip = IsOtherActive ? string.Format(Loc.Culture, Loc.Launchpad_KeepSetupTooltip, DescribeDraft(draft!)) : "";
        if (project is null)
            OtherBadge = null;
        else if (OtherBadge is not { } badge || badge.ProjectId != project.Id || badge.Initial != LaunchpadProjectBadge.InitialOf(project.Name))
            OtherBadge = LaunchpadProjectBadge.For(project);
        OtherGlyph = project is null ? agent?.IconGlyph : null;
    }

    private static bool Matches(LaunchpadSetupSpec setup, LaunchpadSetupSpec draft)
        => setup.ProjectId == draft.ProjectId
           && setup.AgentId == draft.AgentId
           && setup.UseWorktree == draft.UseWorktree
           && (setup.ModelId is null || string.Equals(setup.ModelId, draft.ModelId, StringComparison.Ordinal))
           && (setup.Effort is null || string.Equals(setup.Effort, draft.Effort, StringComparison.Ordinal));

    /// <summary>A draft in a setup's words: "Lumi · Worktree · Claude Opus 5.5 · High".</summary>
    private string DescribeDraft(LaunchpadSetupSpec draft)
    {
        var data = _dataStore.Data;
        var choice = DescribeSetup(
            draft,
            data.Projects.ToArray().ToDictionary(static project => project.Id),
            data.Agents.ToArray().ToDictionary(static agent => agent.Id));
        return choice.Meta.Length > 0 ? $"{choice.Title} · {choice.Meta}" : choice.Title;
    }

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

    private void OpenChat(LaunchpadChatItem item)
    {
        ChatOpening?.Invoke();
        _owner.RequestRevealChat(item.Chat.Id);
    }

    private void OnChatEventPublished(ChatLifecycleEvent chatEvent) => QueueRefreshFromAnyThread();

    /// <summary>
    /// Chats, projects, agents, briefs and automations all persist through the index, so a save means something
    /// shown here may have changed, the setups included. Raised on any thread.
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
        if (e.PropertyName is nameof(Chat.HasUnreadMessages)
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
            case nameof(ChatViewModel.SelectedAgentName):
                RefreshSetupActivity();
                break;
            // Another place: its brief, its For you and the order of its tabs.
            case nameof(ChatViewModel.SelectedProjectName):
                RefreshSetupActivity();
                QueueRefresh();
                break;
            case nameof(ChatViewModel.IsCodingProject):
                QueueRefresh();
                break;
            case nameof(ChatViewModel.ModelCatalogVersion):
                _setupsStale = true;
                _plainDefaultAtOpen = null;
                QueueRefresh();
                break;
        }
    }

    private void OnAvailableModelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _setupsStale = true;
        _plainDefaultAtOpen = null;
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
