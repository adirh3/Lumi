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

/// <summary>
/// How a starter shows where it starts: the line icon the user gave it, else its project's initial on the project's hue,
/// its agent's emoji, or a glyph (an agent without an emoji, or Just chat's sparkle).
/// </summary>
public sealed class LaunchpadBadge
{
    private readonly LaunchpadProjectBadge? _project;

    private LaunchpadBadge(LaunchpadProjectBadge? project, string? glyphText, Geometry? icon, Geometry? lineIcon)
    {
        _project = project;
        GlyphText = glyphText;
        Icon = icon;
        LineIcon = lineIcon;
    }

    public bool HasInitial => _project is not null;
    public string Initial => _project?.Initial ?? "";
    public IBrush? InitialForeground => _project?.Foreground;
    public IBrush? InitialBackground => _project?.Background;
    public string? GlyphText { get; }
    public bool HasGlyphText => !string.IsNullOrEmpty(GlyphText);

    /// <summary>A filled glyph: an agent's, or Just chat's sparkle.</summary>
    public Geometry? Icon { get; }
    public bool HasIcon => Icon is not null;

    /// <summary>The line icon the user chose for the starter, drawn as Strata's line icons are.</summary>
    public Geometry? LineIcon { get; }
    public bool HasLineIcon => LineIcon is not null;

    /// <summary>What it shows, for telling whether a refreshed starter looks the same.</summary>
    internal (Guid?, string, string?, Geometry?, Geometry?) Look => (_project?.ProjectId, Initial, GlyphText, Icon, LineIcon);

    internal static LaunchpadBadge For(Project? project, LumiAgent? agent, string? iconKey = null)
    {
        if (iconKey is not null)
            return new LaunchpadBadge(null, null, null, LaunchpadStarterCatalog.IconOfKey(iconKey));
        if (project is not null)
            return new LaunchpadBadge(LaunchpadProjectBadge.For(project), null, null, null);
        if (!string.IsNullOrWhiteSpace(agent?.IconGlyph))
            return new LaunchpadBadge(null, agent!.IconGlyph, null, null);
        return new LaunchpadBadge(null, null, agent is not null ? WorkspaceIcons.Agents : WorkspaceIcons.Sparkle, null);
    }
}

/// <summary>
/// A starter on the new chat: one click starts the draft there, and its card (a right-click, or its ⋯) keeps, renames,
/// retunes or dismisses it. The row shows its name and, when another starter shares it, what tells the two apart.
/// </summary>
public sealed partial class LaunchpadSetupItem : ObservableObject
{
    private readonly Action<LaunchpadSetupItem> _select;
    private readonly Action<LaunchpadSetupItem> _edit;

    internal LaunchpadSetupItem(
        LaunchpadSetupChoice choice,
        LaunchpadBadge badge,
        bool tellsWorktree,
        string tellText,
        bool canUseWorktree,
        Action<LaunchpadSetupItem> select,
        Action<LaunchpadSetupItem> edit)
    {
        Choice = choice;
        Badge = badge;
        TellsWorktree = tellsWorktree;
        TellText = tellText;
        CanUseWorktree = canUseWorktree;
        _select = select;
        _edit = edit;
    }

    internal LaunchpadSetupChoice Choice { get; private set; }
    internal LaunchpadSetupSpec Spec => Choice.Spec;

    /// <summary>
    /// What an edit of its name, icon or checkout changes it from: a kept starter's own recipe, with a model this computer
    /// no longer offers (which <see cref="Spec"/> starts without), else what it starts with.
    /// </summary>
    internal LaunchpadSetupSpec Recipe => Pin is { } pin ? LaunchpadSetupKeeper.SpecOf(pin) : Spec;

    /// <summary>The kept starter this is, which its card edits; null for one learned from the user's chats.</summary>
    internal LaunchpadSetupPin? Pin => Choice.Pin;

    /// <summary>Which starter it is through refreshes: the very kept starter, or the place Lumi learned.</summary>
    internal object Identity => (object?)Choice.Pin ?? Choice.Spec;

    /// <summary>Its name: the user's for a kept one they named, else its place's.</summary>
    public string Title => Choice.Title;

    /// <summary>Its place's name, which a name of its own replaces.</summary>
    public string PlaceTitle => Choice.PlaceTitle;

    /// <summary>Everything it starts with ("Lumi · Worktree · Default model"): its tooltip.</summary>
    public string Meta => Choice.Meta;

    /// <summary>How many recent chats started in its place.</summary>
    public int Uses => Choice.Uses;

    public LaunchpadBadge Badge { get; private set; }

    /// <summary>The user kept it, so it stays on the new chat whatever they use most.</summary>
    public bool IsKept => Choice.IsPinned;

    public bool IsJustChat => Spec.IsJustChat;

    /// <summary>It starts in a worktree: a branch on its badge's corner.</summary>
    public bool IsWorktree => Spec.UseWorktree;

    /// <summary>Another starter has its name and stays in the project's folder, so this one says ⑂ after its name.</summary>
    public bool TellsWorktree { get; private set; }

    /// <summary>What else tells it from another starter of its name (its agent, model or effort), after its name.</summary>
    public string TellText { get; private set; }

    public bool HasTellText => TellText.Length > 0;

    /// <summary>Its project is a git repository, so it can start in a worktree.</summary>
    public bool CanUseWorktree { get; private set; }

    /// <summary>Lumi can stop suggesting it: a learned place, other than Just chat, which is always there.</summary>
    public bool CanHide => !IsKept && !IsJustChat;

    /// <summary>What the row shows of it, for telling whether a refreshed starter looks the same.</summary>
    internal object Look => (Choice, Badge.Look, TellsWorktree, TellText, CanUseWorktree);

    /// <summary>Takes what a refreshed starter of the same identity shows, keeping this capsule on the row.</summary>
    internal void Update(LaunchpadSetupItem refreshed)
    {
        Choice = refreshed.Choice;
        Badge = refreshed.Badge;
        TellsWorktree = refreshed.TellsWorktree;
        TellText = refreshed.TellText;
        CanUseWorktree = refreshed.CanUseWorktree;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>It is the draft's starter, lit on the row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private bool _isActive;

    /// <summary>True when clicking the applied starter restores the draft it replaced.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private bool _canUndo;

    /// <summary>It has room on the row; the others gather into its stack.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverflowed))]
    private bool _isInRow = true;

    public bool IsOverflowed => !IsInRow;

    /// <summary>The first learned starter the row shows after kept ones, which a divider sets apart.</summary>
    [ObservableProperty] private bool _hasDividerBefore;

    public string ToolTip => IsActive
        ? CanUndo ? Loc.Launchpad_SetupActiveTooltip : Loc.Launchpad_SetupAppliedTooltip
        : Meta;

    /// <summary>Its name as a screen reader says it, with what tells it apart.</summary>
    public string AccessibleName => string.Join(" · ",
        new[] { Title, TellsWorktree ? Loc.Git_Worktree : "", TellText }.Where(static part => part.Length > 0));

    [RelayCommand]
    private void Select() => _select(this);

    /// <summary>Opens its card.</summary>
    [RelayCommand]
    private void Edit() => _edit(this);
}

/// <summary>The new starter card's "+", which a full row's stack folds into its shelf after the starters it holds.</summary>
public sealed class LaunchpadNewStarterTile
{
    public static LaunchpadNewStarterTile Instance { get; } = new();

    private LaunchpadNewStarterTile()
    {
    }
}

/// <summary>A model a kept starter can start with, in its card: the default model, or a chosen one.</summary>
public sealed class LaunchpadModelChoice
{
    internal LaunchpadModelChoice(string? modelId, string title, string hint, bool isCurrent, Action select)
    {
        ModelId = modelId;
        Title = title;
        Hint = hint;
        IsCurrent = isCurrent;
        SelectCommand = new RelayCommand(select);
    }

    /// <summary>The model, or null for the user's default, whatever that is when the starter is used.</summary>
    public string? ModelId { get; }
    public string Title { get; }

    /// <summary>For the default model, what it is now.</summary>
    public string Hint { get; }
    public bool HasHint => Hint.Length > 0;
    public bool IsCurrent { get; }
    public IRelayCommand SelectCommand { get; }

    /// <summary>What the card shows of it, so a rebuilt card can keep the rows it already shows.</summary>
    internal object Look => (ModelId, Title, Hint, IsCurrent);
}

/// <summary>
/// An effort a kept starter's own model can work at, in its card, as the composer's picker offers them: a reasoning
/// effort, or a Copilot Fusion tier.
/// </summary>
public sealed class LaunchpadQualityChoice
{
    internal LaunchpadQualityChoice(string title, bool isCurrent, Action select)
    {
        Title = title;
        IsCurrent = isCurrent;
        SelectCommand = new RelayCommand(select);
    }

    public string Title { get; }
    public bool IsCurrent { get; }
    public IRelayCommand SelectCommand { get; }

    internal object Look => (Title, IsCurrent);
}

/// <summary>An icon a kept starter can wear, in its card: its place's badge, or a line icon.</summary>
public sealed class LaunchpadIconChoice
{
    internal LaunchpadIconChoice(string? key, LaunchpadBadge badge, bool isCurrent, Action select)
    {
        Key = key;
        Badge = badge;
        IsCurrent = isCurrent;
        SelectCommand = new RelayCommand(select);
    }

    /// <summary>The icon's key (<see cref="LaunchpadPlanner.StarterIcons"/>), or null for the place's badge.</summary>
    public string? Key { get; }
    public LaunchpadBadge Badge { get; }
    public bool IsCurrent { get; }
    public IRelayCommand SelectCommand { get; }

    internal object Look => (Key, Badge.Look, IsCurrent);
}

/// <summary>
/// A starter's card, where starters are made and changed. A learned starter's says how often the user starts there and
/// that it starts with the default model, and offers Keep, Rename and Don't suggest; a kept one's edits its name, icon,
/// checkout and model, or removes it. The new starter card (<see cref="IsNew"/>) is that form for a starter not kept yet,
/// filled from the draft: its place is chosen from a list too, and nothing is kept until Add. The card follows its starter
/// through the row's refreshes, and a learned starter it keeps or renames becomes the kept one.
/// </summary>
public sealed partial class LaunchpadStarterEditor : ObservableObject
{
    private readonly LaunchpadViewModel _owner;
    private List<LaunchpadPlaceItem> _projectPlaces = [];
    private List<LaunchpadPlaceItem> _agentPlaces = [];

    internal LaunchpadStarterEditor(LaunchpadViewModel owner) => _owner = owner;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen), nameof(IsKept), nameof(IsLearned), nameof(ShowsForm), nameof(Heading),
        nameof(UsesText), nameof(HasUsesText), nameof(CanHide), nameof(CanUseWorktree), nameof(IsWorktree),
        nameof(NamePlaceholder), nameof(PlaceAccessibleName), nameof(PlaceBadge), nameof(Badge), nameof(RetiredModelNote),
        nameof(HasRetiredModelNote))]
    private LaunchpadSetupItem? _target;

    /// <summary>The card makes a new starter: <see cref="Target"/> is the one Add keeps, as the card has it so far.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLearned), nameof(ShowsForm), nameof(CanUseWorktree))]
    private bool _isNew;

    /// <summary>The new starter's places show in place of its form, to choose where it starts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsForm))]
    private bool _isChoosingPlace;

    /// <summary>The name being typed; a kept starter keeps it on Enter, when the field loses focus, or as the card closes.</summary>
    [ObservableProperty] private string _name = "";

    [ObservableProperty] private IReadOnlyList<LaunchpadModelChoice> _models = [];
    [ObservableProperty] private IReadOnlyList<LaunchpadIconChoice> _icons = [];

    /// <summary>The efforts its own model offers (none on the default model, which brings the default's).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQualities))]
    private IReadOnlyList<LaunchpadQualityChoice> _qualities = [];

    public bool HasQualities => Qualities.Count > 1;

    /// <summary>The name the card last showed, so a refresh keeps one the user has typed since.</summary>
    private string _shownName = "";

    /// <summary>The icons show under the name while the user picks one.</summary>
    [ObservableProperty] private bool _isChoosingIcon;

    /// <summary>What the new starter's places are filtered by.</summary>
    [ObservableProperty] private string _placeQuery = "";

    /// <summary>Just chat, first among the places while the search allows it.</summary>
    [ObservableProperty] private LaunchpadPlaceItem? _justChatPlace;
    [ObservableProperty] private bool _hasJustChatPlace;
    [ObservableProperty] private bool _hasPlaceProjects;
    [ObservableProperty] private bool _hasPlaceAgents;
    [ObservableProperty] private bool _hasNoPlaceMatch;

    public ObservableCollection<LaunchpadPlaceItem> PlaceProjects { get; } = [];
    public ObservableCollection<LaunchpadPlaceItem> PlaceAgents { get; } = [];

    public bool IsOpen => Target is not null;
    public bool IsKept => Target?.IsKept == true;
    public bool IsLearned => Target is { IsKept: false } && !IsNew;

    /// <summary>The form a kept starter's card and the new starter card share: its name, icon, place, checkout and model.</summary>
    public bool ShowsForm => (IsKept || IsNew) && !IsChoosingPlace;

    /// <summary>A learned starter's place in words: "Lumi, in a worktree".</summary>
    public string Heading => Target is null ? ""
        : Target.IsWorktree ? string.Format(Loc.Culture, Loc.Launchpad_StarterInWorktree, Target.PlaceTitle)
        : Target.PlaceTitle;

    /// <summary>"174 of your chats in the last 30 days."</summary>
    public string UsesText => Target is { } target ? UsesTextOf(target.Choice) : "";

    public bool HasUsesText => UsesText.Length > 0;
    public bool CanHide => Target?.CanHide == true;
    public bool CanUseWorktree => (IsKept || IsNew) && Target is { CanUseWorktree: true };
    public bool IsWorktree => Target?.IsWorktree == true;
    public string NamePlaceholder => Target?.PlaceTitle ?? "";

    /// <summary>A new starter's place as a screen reader says its row: "Starts in, Lumi".</summary>
    public string PlaceAccessibleName => $"{Loc.Launchpad_StarterStartsIn}, {NamePlaceholder}";

    /// <summary>Where it starts, as its place's badge shows it, in the card's "Starts in" row.</summary>
    public LaunchpadBadge? PlaceBadge { get; private set; }

    /// <summary>Its own badge, on the card's icon button: the icon it wears, else its place's.</summary>
    public LaunchpadBadge? Badge => Target?.Badge;

    /// <summary>It was kept on a model this computer no longer offers, so it starts with the default model.</summary>
    public string RetiredModelNote => Target is { } target ? RetiredNoteOf(target.Choice) : "";

    public bool HasRetiredModelNote => RetiredModelNote.Length > 0;

    /// <summary>How often the user starts in a learned starter's place, in words; empty for a kept one.</summary>
    internal static string UsesTextOf(LaunchpadSetupChoice choice) => choice is { IsPinned: false, Uses: > 0 }
        ? string.Format(Loc.Culture, Loc.Launchpad_StarterUses, choice.Uses, LaunchpadPlanner.SetupWindowDays)
        : "";

    /// <summary>That a kept starter's model is no longer offered, so it starts with the default model; empty otherwise.</summary>
    internal static string RetiredNoteOf(LaunchpadSetupChoice choice) => choice.RetiredModelId is { } retired
        ? string.Format(Loc.Culture, Loc.Launchpad_StarterModelRetired, ChatViewModel.FormatModelDisplay(retired) ?? retired)
        : "";

    /// <summary>Raised when the card should give its name field keyboard focus (after Rename).</summary>
    public event Action? NameFocusRequested;

    /// <summary>
    /// Shows a starter, or the same one afresh: a new starter (<paramref name="isNew"/>) is the same one however it changes,
    /// until <paramref name="fresh"/> starts the card on another. The name field keeps a name the user has typed since it
    /// last showed one, and a list that shows the same choices keeps its rows, so a click that moved focus out of the name
    /// (which keeps the name and so rebuilds the card) still lands on its row.
    /// </summary>
    internal void Show(LaunchpadSetupItem target, LaunchpadBadge placeBadge, IReadOnlyList<LaunchpadModelChoice> models,
        IReadOnlyList<LaunchpadIconChoice> icons, IReadOnlyList<LaunchpadQualityChoice> qualities, bool isNew = false,
        bool fresh = false)
    {
        var same = !fresh && (ReferenceEquals(Target, target) || (isNew && IsNew));
        var refreshedInPlace = ReferenceEquals(Target, target);
        if (fresh)
        {
            IsChoosingIcon = false;
            PlaceQuery = "";
        }

        if (fresh || !isNew)
            IsChoosingPlace = false;
        PlaceBadge = placeBadge;
        IsNew = isNew;
        Target = target;
        // The row refreshes a starter in place, which assigning it again doesn't announce: everything the card reads from
        // it (its checkout, badge, retired model, uses) may have changed.
        if (refreshedInPlace)
            OnPropertyChanged(string.Empty);
        var title = target.IsKept ? target.Title : "";
        if (!same || string.Equals(Name.Trim(), _shownName, StringComparison.Ordinal))
            Name = title;
        _shownName = title;
        if (!same || !models.Select(static model => model.Look).SequenceEqual(Models.Select(static model => model.Look)))
            Models = models;
        if (!same || !icons.Select(static icon => icon.Look).SequenceEqual(Icons.Select(static icon => icon.Look)))
            Icons = icons;
        if (!same || !qualities.Select(static quality => quality.Look).SequenceEqual(Qualities.Select(static quality => quality.Look)))
            Qualities = qualities;
    }

    /// <summary>Gives the new starter's place list its places: Just chat, the projects and the agents.</summary>
    internal void ShowPlaces(LaunchpadPlaceItem justChat, List<LaunchpadPlaceItem> projects, List<LaunchpadPlaceItem> agents)
    {
        JustChatPlace = justChat;
        _projectPlaces = projects;
        _agentPlaces = agents;
        FilterPlaces();
    }

    internal void Close()
    {
        Target = null;
        IsNew = false;
        IsChoosingPlace = false;
        IsChoosingIcon = false;
        PlaceQuery = "";
        Models = [];
        Icons = [];
        Qualities = [];
        JustChatPlace = null;
        _projectPlaces = [];
        _agentPlaces = [];
        FilterPlaces();
    }

    internal void RequestNameFocus() => NameFocusRequested?.Invoke();

    partial void OnPlaceQueryChanged(string value) => FilterPlaces();

    private void FilterPlaces()
    {
        var query = PlaceQuery.Trim();
        Fill(PlaceProjects, _projectPlaces);
        Fill(PlaceAgents, _agentPlaces);
        HasJustChatPlace = JustChatPlace is { } justChat && Matches(justChat);
        HasPlaceProjects = PlaceProjects.Count > 0;
        HasPlaceAgents = PlaceAgents.Count > 0;
        HasNoPlaceMatch = JustChatPlace is not null && !HasJustChatPlace && !HasPlaceProjects && !HasPlaceAgents;

        bool Matches(LaunchpadPlaceItem item)
            => query.Length == 0 || item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase);

        void Fill(ObservableCollection<LaunchpadPlaceItem> target, List<LaunchpadPlaceItem> source)
        {
            target.Clear();
            foreach (var item in source)
            {
                if (Matches(item))
                    target.Add(item);
            }
        }
    }

    /// <summary>Picks the first place the search shows, as Enter in it does.</summary>
    internal void SelectFirstPlace()
        => (HasJustChatPlace ? JustChatPlace : PlaceProjects.FirstOrDefault() ?? PlaceAgents.FirstOrDefault())
            ?.SelectCommand.Execute(null);

    [RelayCommand]
    private void Keep()
    {
        if (Target is { } target)
            _owner.KeepStarter(target, focusName: false);
    }

    /// <summary>Keeps a learned starter, and gives its name field focus.</summary>
    [RelayCommand]
    private void Rename()
    {
        if (Target is { } target)
            _owner.KeepStarter(target, focusName: true);
    }

    [RelayCommand]
    private void Hide()
    {
        if (Target is { } target)
            _owner.HideStarter(target);
    }

    [RelayCommand]
    private void Remove()
    {
        if (Target is { } target)
            _owner.RemoveStarter(target);
    }

    [RelayCommand]
    private void ToggleWorktree()
    {
        if (Target is not { } target)
            return;

        var changed = target.Recipe with { UseWorktree = !target.IsWorktree };
        if (IsNew)
            _owner.ChangeNewStarter(changed);
        else
            _owner.EditStarter(target, changed);
    }

    [RelayCommand]
    private void ToggleIcons() => IsChoosingIcon = !IsChoosingIcon;

    /// <summary>Opens the new starter's places, its own marked, in place of its form.</summary>
    [RelayCommand]
    private void ChoosePlace()
    {
        if (!IsNew)
            return;

        _owner.ShowNewStarterPlaces();
        PlaceQuery = "";
        IsChoosingPlace = true;
    }

    /// <summary>Back to the new starter's form, where it starts unchanged.</summary>
    [RelayCommand]
    private void BackToForm() => IsChoosingPlace = false;

    /// <summary>Keeps the new starter as the card has it.</summary>
    [RelayCommand]
    private void Add()
    {
        if (IsNew)
            _owner.AddNewStarter();
    }

    /// <summary>Keeps the typed name, if it changed.</summary>
    [RelayCommand]
    private void CommitName()
    {
        if (Target is { IsKept: true } target && !string.Equals(Name.Trim(), target.Title, StringComparison.Ordinal))
            _owner.EditStarter(target, target.Recipe, name: Name);
    }
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

/// <summary>
/// A place a new starter can start in, in its card's list: Just chat, a project or an agent. A project brings the
/// checkout the user last worked there in; the new starter's own place is marked.
/// </summary>
public sealed partial class LaunchpadPlaceItem : ObservableObject
{
    private readonly Action<LaunchpadPlaceItem> _select;

    internal LaunchpadPlaceItem(LaunchpadSetupSpec setup, string title, string hint, LaunchpadBadge badge,
        Action<LaunchpadPlaceItem> select)
    {
        Setup = setup;
        Title = title;
        Meta = hint;
        Badge = badge;
        _select = select;
    }

    /// <summary>Where a starter there starts: the place alone, on the default model, with its checkout.</summary>
    internal LaunchpadSetupSpec Setup { get; }

    public string Title { get; }

    /// <summary>When the user last worked there ("40m ago").</summary>
    public string Meta { get; }
    public bool HasMeta => Meta.Length > 0;
    public LaunchpadBadge Badge { get; }

    /// <summary>It is where the new starter starts.</summary>
    [ObservableProperty] private bool _isCurrent;

    [RelayCommand]
    private void Select() => _select(this);
}

/// <summary>
/// Lumi's launchpad: the new-chat surface. It greets the user, says how many chats wait on them, and offers what to
/// start: For you (the project's plans from its brief, the user's habits, what suits the hour) and a tab per kind of
/// task, each with starters whose blanks the user fills in the composer. On the composer it offers where to start: the
/// starters the user kept, in their order and by their names, then the places Lumi learned they start in (Just chat
/// among them), each on the default model unless the user chose another. The draft's starter is lit; one that doesn't
/// fit the row gathers into its stack, and the new starter card makes another, filled from the draft.
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

    /// <summary>
    /// The default model when the new chat opened. Picking a model for a new chat also makes it the default, so a model
    /// picked since is the draft's own choice, which keeping the draft keeps.
    /// </summary>
    private string? _defaultModelAtOpen;

    /// <summary>The kept starter the card follows once the row is rebuilt (one it just kept), and whether its name takes focus.</summary>
    private LaunchpadSetupPin? _editorPin;
    private bool _focusEditorName;

    /// <summary>What the card's starter looked like when the card showed it, so a change shows it afresh.</summary>
    private object? _editorLook;

    /// <summary>The starter the new starter card makes, as it has it so far, and the icon it gives it (null: its place's).</summary>
    private LaunchpadSetupSpec _newStarter = LaunchpadSetupSpec.JustChat;
    private string? _newStarterIcon;

    internal LaunchpadViewModel(ChatViewModel owner, DataStore dataStore, ChatEventHub chatEvents)
    {
        _owner = owner;
        _dataStore = dataStore;
        _chatEvents = chatEvents;
        Editor = new LaunchpadStarterEditor(this);
    }

    public ObservableCollection<LaunchpadChatItem> NeedsYouItems { get; } = [];

    /// <summary>The starters: the kept ones in the user's order, then the places Lumi learned, Just chat among them.</summary>
    public ObservableCollection<LaunchpadSetupItem> Setups { get; } = [];
    public ObservableCollection<LaunchpadCategoryItem> Categories { get; } = [];
    public ObservableCollection<LaunchpadStarterItem> Suggestions { get; } = [];

    /// <summary>What the stack's shelf holds while it's open: the starters the row has no room for, then the new starter "+".</summary>
    public ObservableCollection<object> ShelfItems { get; } = [];

    /// <summary>The card a starter opens, which the new starter card is too.</summary>
    public LaunchpadStarterEditor Editor { get; }

    [ObservableProperty] private string _greetingLead = "";
    [ObservableProperty] private string _greetingName = "";
    [ObservableProperty] private string _greetingTrail = "";

    /// <summary>One line under the greeting: where the project stands (its brief's opening), or the invitation.</summary>
    [ObservableProperty] private string _subtitle = "";

    [ObservableProperty] private string _needsYouLabel = "";
    [ObservableProperty] private bool _hasNeedsYou;
    [ObservableProperty] private LaunchpadCategoryItem? _selectedCategory;

    /// <summary>The draft's starter, lit on the row (or, without room beside the kept starters, in the stack).</summary>
    [ObservableProperty] private LaunchpadSetupItem? _activeSetup;

    /// <summary>
    /// Where the draft starts, as a starter shows it: the dashed chip on the row while no starter fits the draft
    /// (<see cref="HasUnsavedDraft"/>).
    /// </summary>
    [ObservableProperty] private LaunchpadSetupItem? _draftPlace;

    /// <summary>No starter fits the draft (a project picked in the composer, say), so the row shows where it starts, with a pin.</summary>
    [ObservableProperty] private bool _hasUnsavedDraft;

    /// <summary>What the dashed chip's pin keeps, in words.</summary>
    [ObservableProperty] private string _keepDraftTooltip = "";

    /// <summary>How many starters the row has no room for, gathered into its stack, and the first three's badges.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverflow), nameof(OverflowLabel), nameof(OverflowAccessibleName))]
    private int _overflowCount;

    [ObservableProperty] private IReadOnlyList<LaunchpadBadge> _overflowPreview = [];

    /// <summary>The draft's starter is in the stack (the row's room went to kept starters), so the stack wears its light.</summary>
    [ObservableProperty] private bool _isOverflowActive;

    public bool HasOverflow => OverflowCount > 0;
    public string OverflowLabel => "+" + OverflowCount.ToString(Loc.Culture);
    public string OverflowAccessibleName => OverflowCount == 1
        ? Loc.Launchpad_StarterStackOne
        : string.Format(Loc.Culture, Loc.Launchpad_StarterStack, OverflowCount);

    /// <summary>Raised before the launchpad opens a chat, so the view can close what it has open.</summary>
    public event Action? ChatOpening;

    /// <summary>Raised when a starter's card should open beside it.</summary>
    public event Action<LaunchpadSetupItem>? EditorRequested;

    /// <summary>Raised when the starters changed (added, removed, renamed), so the view fits the row again.</summary>
    public event Action? RowChanged;

    internal bool IsActive => _activeViews > 0;

    /// <summary>Starts observing chats and the clock for a view that is now in the new-chat state.</summary>
    public void Activate()
    {
        if (_activeViews++ > 0)
            return;

        _setupsStale = true;
        _defaultModelAtOpen = _dataStore.Data.Settings.PreferredModel;
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
        Editor.Close();
        ShelfItems.Clear();
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

    /// <summary>
    /// Opens the new starter card on the draft as a starter would keep it: its place and checkout, and its model when the
    /// user picked one since the new chat opened, else the default model; no name or icon of its own yet.
    /// </summary>
    [RelayCommand]
    private void OpenNewStarter()
    {
        _newStarter = _owner.GetLaunchpadDraftSetup() is { } draft ? RecipeOf(draft) : LaunchpadSetupSpec.JustChat;
        _newStarterIcon = null;
        ShowNewStarter(fresh: true);
    }

    /// <summary>Shows the new starter in its card as it is so far; <paramref name="fresh"/> starts the card on it.</summary>
    private void ShowNewStarter(bool fresh = false)
    {
        var data = _dataStore.Data;
        var projects = data.Projects.ToArray().ToDictionary(static project => project.Id);
        var agents = data.Agents.ToArray().ToDictionary(static agent => agent.Id);
        var project = _newStarter.ProjectId is { } projectId ? projects.GetValueOrDefault(projectId) : null;
        var agent = _newStarter.AgentId is { } agentId ? agents.GetValueOrDefault(agentId) : null;
        var canUseWorktree = CanUseWorktree(project);

        // A place deleted meanwhile is no place to start, and only a git project starts in a worktree.
        _newStarter = _newStarter with
        {
            ProjectId = project?.Id,
            AgentId = agent?.Id,
            UseWorktree = _newStarter.UseWorktree && canUseWorktree,
        };
        var placeBadge = LaunchpadBadge.For(project, agent);
        var item = new LaunchpadSetupItem(DescribeSetup(_newStarter, projects, agents),
            LaunchpadBadge.For(project, agent, _newStarterIcon), false, "", canUseWorktree, static _ => { }, static _ => { });
        Editor.Show(item, placeBadge,
            ModelChoices(() => _newStarter, ChangeNewStarter),
            IconChoices(placeBadge, _newStarterIcon, icon =>
            {
                _newStarterIcon = icon;
                ShowNewStarter();
            }),
            QualityChoices(() => _newStarter, ChangeNewStarter),
            isNew: true,
            fresh: fresh);
    }

    /// <summary>Changes what the new starter starts with, in its card; nothing is kept until Add.</summary>
    internal void ChangeNewStarter(LaunchpadSetupSpec changed)
    {
        if (!Editor.IsNew)
            return;

        _newStarter = changed;
        ShowNewStarter();
    }

    /// <summary>
    /// Fills the new starter's places: Just chat, then every project, most recently used first, then every agent. A
    /// project brings the checkout the user last worked there in; its own place is marked.
    /// </summary>
    internal void ShowNewStarterPlaces()
    {
        var data = _dataStore.Data;
        var projects = data.Projects.ToArray().ToDictionary(static project => project.Id);
        var agents = data.Agents.ToArray().ToDictionary(static agent => agent.Id);
        var places = BuildPlaces(_dataStore, int.MaxValue, int.MaxValue);
        var justChat = Place(LaunchpadSetupSpec.JustChat, Loc.Launchpad_JustChat, "", LaunchpadBadge.For(null, null));
        var projectPlaces = places.Where(static place => place.IsProject)
            .Select(place => Place(place.StarterSetup, place.Title, place.Hint, LaunchpadBadge.For(projects.GetValueOrDefault(place.Id), null)))
            .ToList();
        var agentPlaces = places.Where(static place => !place.IsProject)
            .Select(place => Place(place.StarterSetup, place.Title, place.Hint, LaunchpadBadge.For(null, agents.GetValueOrDefault(place.Id))))
            .ToList();
        Editor.ShowPlaces(justChat, projectPlaces, agentPlaces);

        LaunchpadPlaceItem Place(LaunchpadSetupSpec setup, string title, string hint, LaunchpadBadge badge)
            => new(setup, title, hint, badge, ChooseNewStarterPlace)
            {
                IsCurrent = setup.ProjectId == _newStarter.ProjectId && setup.AgentId == _newStarter.AgentId,
            };
    }

    /// <summary>Starts the new starter in another place, with that place's checkout; its own place leaves it as it is.</summary>
    private void ChooseNewStarterPlace(LaunchpadPlaceItem place)
    {
        if (!Editor.IsNew)
            return;

        if (!place.IsCurrent)
        {
            _newStarter = _newStarter with
            {
                ProjectId = place.Setup.ProjectId,
                AgentId = place.Setup.AgentId,
                UseWorktree = place.Setup.UseWorktree,
            };
        }

        Editor.IsChoosingPlace = false;
        ShowNewStarter();
    }

    /// <summary>Keeps the new starter card's starter after the user's others, under the name and icon it gave it.</summary>
    internal void AddNewStarter()
    {
        if (!Editor.IsNew)
            return;

        LaunchpadSetupKeeper.Add(_dataStore.Data, _newStarter, Editor.Name, _newStarterIcon);
        Editor.Close();
        SaveStarters();
    }

    /// <summary>A chat that shows how the user starts in its place: the user's own, with something in it.</summary>
    private static bool CanTeachSetup(Chat chat, HashSet<Guid> automationChatIds)
        => LaunchpadPlanner.HasContent(chat)
           && string.IsNullOrWhiteSpace(chat.SdkAgentName)
           && !automationChatIds.Contains(chat.Id);

    private static LaunchpadSetupSpec SetupFrom(AppData data, Guid id, bool isProject, Chat? latest)
    {
        if (latest is null)
            return isProject ? new(id, null, false, null, null) : new(null, id, false, null, null);

        var projects = data.Projects.Select(static project => project.Id).ToHashSet();
        var agents = data.Agents.Select(static agent => agent.Id).ToHashSet();
        var spec = LaunchpadPlanner.ToSetupSpec(latest, projects.Contains, agents.Contains);
        return isProject ? spec with { ProjectId = id } : spec with { AgentId = id };
    }

    /// <summary>
    /// The new starter's places for the desktop and the phone: the projects, most recently used first, then the agents,
    /// each with when the user last worked there and how a chat there started then (<see cref="SetupFrom"/>: its
    /// checkout), found in one pass over the chats.
    /// </summary>
    internal static List<LaunchpadPlace> BuildPlaces(DataStore dataStore, int maxProjects, int maxAgents)
    {
        var data = dataStore.Data;
        var automationChatIds = AutomationChatIds(dataStore);
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

        var places = new List<LaunchpadPlace>();
        foreach (var project in data.Projects.ToArray()
                     .OrderByDescending(project => projectActivity.GetValueOrDefault(project.Id))
                     .ThenBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
                     .Take(maxProjects))
        {
            places.Add(new LaunchpadPlace(project.Id, IsProject: true, project.Name, HintOf(projectActivity, project.Id),
                SetupFrom(data, project.Id, isProject: true, projectLatest.GetValueOrDefault(project.Id))));
        }

        foreach (var agent in data.Agents.ToArray()
                     .OrderByDescending(agent => agentActivity.GetValueOrDefault(agent.Id))
                     .ThenBy(agent => agent.Name, StringComparer.CurrentCultureIgnoreCase)
                     .Take(maxAgents))
        {
            places.Add(new LaunchpadPlace(agent.Id, IsProject: false, agent.Name, HintOf(agentActivity, agent.Id),
                SetupFrom(data, agent.Id, isProject: false, agentLatest.GetValueOrDefault(agent.Id))));
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

    /// <summary>The row's starters, as the keeper finds them.</summary>
    private List<LaunchpadSetupChoice> Row() => Setups.Select(static setup => setup.Choice).ToList();

    /// <summary>Opens a starter's card beside it.</summary>
    private void OpenEditor(LaunchpadSetupItem item)
    {
        ShowEditor(item);
        EditorRequested?.Invoke(item);
    }

    private void ShowEditor(LaunchpadSetupItem item)
    {
        var data = _dataStore.Data;
        var project = item.Spec.ProjectId is { } projectId ? data.Projects.ToArray().FirstOrDefault(p => p.Id == projectId) : null;
        var agent = item.Spec.AgentId is { } agentId ? data.Agents.ToArray().FirstOrDefault(a => a.Id == agentId) : null;
        var placeBadge = LaunchpadBadge.For(project, agent);
        if (item.IsKept)
        {
            // Every choice reads the starter as it is when clicked, so the card can keep its rows across a refresh.
            Editor.Show(item, placeBadge,
                ModelChoices(() => item.Spec, changed => EditStarter(item, changed)),
                IconChoices(placeBadge, item.Choice.Icon, icon => EditStarter(item, item.Recipe, icon: icon ?? "")),
                QualityChoices(() => item.Spec, changed => EditStarter(item, changed)));
        }
        else
        {
            Editor.Show(item, placeBadge, [], [], []);
        }

        _editorLook = item.Look;
    }

    /// <summary>
    /// The models a starter's card offers, after the default model, as the composer's picker lists them: Copilot Fusion
    /// once, its tiers being its efforts. Picking one changes the starter (as <paramref name="current"/> reads it when
    /// clicked) to that model, at the effort it had where the model offers it.
    /// </summary>
    private List<LaunchpadModelChoice> ModelChoices(Func<LaunchpadSetupSpec> current, Action<LaunchpadSetupSpec> change)
    {
        var model = current().ModelId;
        List<LaunchpadModelChoice> models =
        [
            new(null, Loc.Launchpad_StarterDefaultModel, ChatViewModel.FormatModelDisplay(PlainSetup().ModelId) ?? "",
                model is null, () =>
                {
                    if (current() is { ModelId: not null } spec)
                        change(spec.Place);
                }),
        ];
        foreach (var option in _owner.CreateModelOptions(_owner.AvailableModels))
        {
            models.Add(new LaunchpadModelChoice(option.Id, option.DisplayName, "", IsModelOf(option, model), () =>
            {
                var spec = current();
                if (!IsModelOf(option, spec.ModelId))
                    change(spec with { ModelId = option.Id, Effort = _owner.ResolveReasoningEffortForModel(spec.Effort, option.Id) });
            }));
        }

        return models;

        static bool IsModelOf(ModelOption option, string? model) => model is not null
            && (string.Equals(option.Id, model, StringComparison.Ordinal) || option.SelectionAliases.Contains(model, StringComparer.Ordinal));
    }

    /// <summary>The efforts a starter's own model offers in its card (Copilot Fusion's tiers among them); none on the default model.</summary>
    private List<LaunchpadQualityChoice> QualityChoices(Func<LaunchpadSetupSpec> current, Action<LaunchpadSetupSpec> change)
    {
        var spec = current();
        if (spec.ModelId is not { } model || _owner.GetQualityLevelsFor(model) is not { Length: > 1 } levels)
            return [];

        var selected = _owner.GetAutoTierQualityFor(model) ?? (spec.Effort is { } effort ? ModelSelectionHelper.EffortToDisplay(effort) : null);
        return levels.Select(level => new LaunchpadQualityChoice(level, level == selected, () =>
        {
            if (current() is { ModelId: { } chosen } now)
            {
                var (tuned, tunedEffort) = _owner.ResolveModelQuality(chosen, level);
                change(now with { ModelId = tuned, Effort = tunedEffort });
            }
        })).ToList();
    }

    /// <summary>The icons a starter can wear in its card: its place's badge (null), then the line icons.</summary>
    private static List<LaunchpadIconChoice> IconChoices(LaunchpadBadge placeBadge, string? icon, Action<string?> change)
    {
        List<LaunchpadIconChoice> icons = [new(null, placeBadge, icon is null, () => change(null))];
        icons.AddRange(LaunchpadPlanner.StarterIcons.Select(key => new LaunchpadIconChoice(
            key, LaunchpadBadge.For(null, null, key), key == icon, () => change(key))));
        return icons;
    }

    /// <summary>Keeps a learned starter on the new chat; Rename then gives its name field focus.</summary>
    internal void KeepStarter(LaunchpadSetupItem item, bool focusName)
    {
        _editorPin = item.Pin ?? LaunchpadSetupKeeper.Keep(_dataStore.Data, Row(), item.Spec);
        _focusEditorName = focusName;
        SaveStarters();
    }

    /// <summary>
    /// Changes a starter from now on (what it starts with, its name or its icon): a learned one is kept so the change
    /// holds. A name typed in its card goes along, so picking a model mid-rename keeps it. The card follows the starter.
    /// </summary>
    internal void EditStarter(LaunchpadSetupItem item, LaunchpadSetupSpec changed, string? name = null, string? icon = null)
    {
        if (name is null && item.IsKept && ReferenceEquals(Editor.Target, item)
            && !string.Equals(Editor.Name.Trim(), item.Title, StringComparison.Ordinal))
        {
            name = Editor.Name;
        }

        _editorPin = LaunchpadSetupKeeper.Edit(_dataStore.Data, Row(), item.Pin, changed, name, icon);
        SaveStarters();
    }

    /// <summary>Lets a kept starter go; its place comes back as a learned one if the user still starts there.</summary>
    internal void RemoveStarter(LaunchpadSetupItem item)
    {
        Editor.Close();
        if (item.Pin is { } pin)
            LaunchpadSetupKeeper.StopKeeping(_dataStore.Data, pin);
        SaveStarters();
    }

    /// <summary>Stops suggesting a learned place.</summary>
    internal void HideStarter(LaunchpadSetupItem item)
    {
        Editor.Close();
        LaunchpadSetupKeeper.Hide(_dataStore.Data, item.Spec);
        SaveStarters();
    }

    /// <summary>Keeps the draft's setup, the dashed chip's, as the last of the user's starters.</summary>
    [RelayCommand]
    private void KeepDraft()
    {
        if (DraftRecipe() is not { } recipe)
            return;

        LaunchpadSetupKeeper.Keep(_dataStore.Data, Row(), recipe);
        SaveStarters();
    }

    /// <summary>
    /// The draft as a starter keeps it (<see cref="RecipeOf"/>); null without a draft a starter can keep (a Copilot
    /// agent's, say).
    /// </summary>
    private LaunchpadSetupSpec? DraftRecipe()
        => _owner.HasLaunchpadIncompatibleAgent || _owner.GetLaunchpadDraftSetup() is not { } draft ? null : RecipeOf(draft);

    /// <summary>
    /// A draft as a starter keeps it: its place, with its model only when the user picked another since the new chat
    /// opened, else the default model.
    /// </summary>
    private LaunchpadSetupSpec RecipeOf(LaunchpadSetupSpec draft)
        => draft.ModelId is null || string.Equals(draft.ModelId, _defaultModelAtOpen, StringComparison.Ordinal)
            ? draft.Place
            : draft;

    /// <summary>Saves the user's starters and rebuilds the row at once, so the card follows the starter it edits.</summary>
    private void SaveStarters()
    {
        _setupsStale = true;
        if (IsActive)
            Refresh();
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
        var data = _dataStore.Data;
        var projects = data.Projects.ToArray().ToDictionary(static project => project.Id);
        var agents = data.Agents.ToArray().ToDictionary(static agent => agent.Id);
        var choices = BuildSetupChoices(_dataStore, _owner, now);
        var items = choices.Select(choice => CreateSetupItem(choice, choices, projects, agents)).ToList();
        var changed = false;
        if (items.Select(static item => item.Identity).SequenceEqual(Setups.Select(static item => item.Identity)))
        {
            // The same starters in the same order keep their capsules, so nothing under the pointer is replaced.
            for (var index = 0; index < items.Count; index++)
            {
                if (Equals(items[index].Look, Setups[index].Look))
                    continue;

                Setups[index].Update(items[index]);
                changed = true;
            }
        }
        else
        {
            Setups.Clear();
            foreach (var item in items)
                Setups.Add(item);
            changed = true;
        }

        RetargetEditor();
        if (changed)
            RowChanged?.Invoke();
    }

    /// <summary>
    /// Keeps the open card on its starter once the row is refreshed: the kept starter it just made or edited, else the
    /// same one, shown afresh when it changed. It closes once its starter is gone. A new starter's card shows it afresh,
    /// as a model or a place may have come or gone.
    /// </summary>
    private void RetargetEditor()
    {
        var pin = _editorPin;
        _editorPin = null;
        var focusName = _focusEditorName;
        _focusEditorName = false;
        if (Editor.IsNew)
        {
            ShowNewStarter();
            return;
        }

        if (Editor.Target is not { } target)
            return;

        pin ??= target.Pin;
        var next = pin is not null
            ? Setups.FirstOrDefault(setup => ReferenceEquals(setup.Pin, pin))
            : Setups.FirstOrDefault(setup => !setup.IsKept && setup.Spec == target.Spec);
        if (next is null)
        {
            Editor.Close();
            return;
        }

        if (!ReferenceEquals(next, target) || !Equals(next.Look, _editorLook))
            ShowEditor(next);
        if (focusName)
            Editor.RequestNameFocus();
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

    /// <summary>What a plain chat starts with: no project, no agent, and the user's default model.</summary>
    internal static LaunchpadSetupSpec PlainSetupFor(DataStore dataStore, ChatViewModel owner)
        => PlainSetup(dataStore, CreateModelResolver(owner));

    /// <summary>What a starter starts the draft with: the default model, and its effort, for one on the default model.</summary>
    internal static LaunchpadSetupSpec Resolve(LaunchpadSetupSpec spec, LaunchpadSetupSpec plain)
        => spec.ModelId is null ? spec with { ModelId = plain.ModelId, Effort = plain.Effort } : spec;

    private LaunchpadSetupSpec Resolve(LaunchpadSetupSpec spec) => spec.ModelId is null ? Resolve(spec, PlainSetup()) : spec;

    private static HashSet<Guid> AutomationChatIds(DataStore dataStore) => dataStore.SnapshotBackgroundJobs()
        .Where(static job => job.IsEnabled).Select(static job => job.ChatId).ToHashSet();

    /// <summary>
    /// The starters the new chat offers, the phone's included: every one the user kept, in their order, then the places
    /// they keep starting in, leaving out places they asked not to see and those a kept starter is exactly. A kept
    /// starter whose model this computer no longer offers starts with the default model, and says so in its card.
    /// </summary>
    internal static List<LaunchpadSetupChoice> BuildSetupChoices(DataStore dataStore, ChatViewModel owner, DateTimeOffset now)
    {
        var data = dataStore.Data;
        var projects = data.Projects.ToArray().ToDictionary(static project => project.Id);
        var agents = data.Agents.ToArray().ToDictionary(static agent => agent.Id);
        var resolveModel = CreateModelResolver(owner);
        var kept = data.PinnedSetups.ToArray()
            .Where(pin => (pin.ProjectId is not { } projectId || projects.ContainsKey(projectId))
                          && (pin.AgentId is not { } agentId || agents.ContainsKey(agentId)))
            .Select(Kept)
            .ToList();
        var hidden = data.HiddenSetups.ToArray().Select(static pin => (pin.ProjectId, pin.AgentId, pin.UseWorktree)).ToList();
        var learned = LaunchpadPlanner.SelectSetups(
            data.Chats.ToArray(),
            AutomationChatIds(dataStore),
            now,
            projects.ContainsKey,
            agents.ContainsKey,
            LaunchpadPlanner.MaxLearnedSetups + kept.Count + hidden.Count);

        return LaunchpadPlanner.MergeSetups(kept.Select(static entry => entry.Spec).ToList(), learned, hidden,
                LaunchpadPlanner.MaxLearnedSetups)
            .Select(entry =>
            {
                var pin = entry.PinIndex >= 0 ? kept[entry.PinIndex].Pin : null;
                return DescribeSetup(entry.Spec, projects, agents, pin?.Name) with
                {
                    Pin = pin,
                    Uses = entry.Uses,
                    RetiredModelId = entry.PinIndex >= 0 ? kept[entry.PinIndex].Retired : null,
                };
            })
            .ToList();

        // A kept starter on a model this computer no longer offers starts with the default model.
        (LaunchpadSetupPin Pin, LaunchpadSetupSpec Spec, string? Retired) Kept(LaunchpadSetupPin pin)
        {
            var place = new LaunchpadSetupSpec(pin.ProjectId, pin.AgentId, pin.UseWorktree && pin.ProjectId is not null, null, null);
            if (pin.ModelId is null)
                return (pin, place, null);

            var (model, effort) = resolveModel(pin.ModelId, pin.Effort);
            return model is null ? (pin, place, pin.ModelId) : (pin, place with { ModelId = model, Effort = effort }, null);
        }
    }

    /// <summary>A place's name: its project's, else its agent's, else Just chat.</summary>
    internal static string PlaceTitle(Project? project, LumiAgent? agent) => project?.Name ?? agent?.Name ?? Loc.Launchpad_JustChat;

    /// <summary>A starter in words: its name (the given one, else its place's) and everything it starts with.</summary>
    internal static LaunchpadSetupChoice DescribeSetup(
        LaunchpadSetupSpec spec,
        IReadOnlyDictionary<Guid, Project> projects,
        IReadOnlyDictionary<Guid, LumiAgent> agents,
        string? name = null)
    {
        var project = spec.ProjectId is { } projectId ? projects.GetValueOrDefault(projectId) : null;
        var agent = spec.AgentId is { } agentId ? agents.GetValueOrDefault(agentId) : null;
        var place = PlaceTitle(project, agent);
        var meta = new List<string>(5) { place };
        if (project is not null && agent is not null)
            meta.Add(agent.Name);
        if (spec.UseWorktree)
            meta.Add(Loc.Git_Worktree);
        meta.Add(ChatViewModel.FormatModelDisplay(spec.ModelId) ?? Loc.Launchpad_StarterDefaultModel);
        if (spec.Effort is { } effort)
            meta.Add(ModelSelectionHelper.EffortToDisplay(effort));
        return new LaunchpadSetupChoice(spec, string.IsNullOrWhiteSpace(name) ? place : name, place, string.Join(" · ", meta));
    }

    private LaunchpadSetupItem CreateSetupItem(
        LaunchpadSetupChoice choice,
        IReadOnlyList<LaunchpadSetupChoice> row,
        IReadOnlyDictionary<Guid, Project> projects,
        IReadOnlyDictionary<Guid, LumiAgent> agents)
    {
        var project = choice.Spec.ProjectId is { } projectId ? projects.GetValueOrDefault(projectId) : null;
        var agent = choice.Spec.AgentId is { } agentId ? agents.GetValueOrDefault(agentId) : null;
        var (tellsWorktree, tellText) = DescribeTell(choice, row, agents);
        return new LaunchpadSetupItem(choice, LaunchpadBadge.For(project, agent, choice.Icon), tellsWorktree, tellText,
            CanUseWorktree(project), SelectSetup, OpenEditor);
    }

    /// <summary>A project whose folder is a git repository can start in a worktree.</summary>
    internal static bool CanUseWorktree(Project? project)
        => project is { WorkingDirectory: { Length: > 0 } directory } && GitService.IsGitRepo(directory);

    /// <summary>
    /// What tells a starter from others of its name on the row (Lumi in a worktree and in its folder, say): ⑂ when it
    /// starts in a worktree and another doesn't, and its agent, model or effort where another's differs. Nothing while no
    /// other starter shares its name.
    /// </summary>
    internal static (bool Worktree, string Text) DescribeTell(
        LaunchpadSetupChoice choice,
        IReadOnlyList<LaunchpadSetupChoice> row,
        IReadOnlyDictionary<Guid, LumiAgent> agents)
    {
        var spec = choice.Spec;
        var siblings = row.Where(other => !ReferenceEquals(other, choice)
                                          && string.Equals(other.Title, choice.Title, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        if (siblings.Count == 0)
            return (false, "");

        var parts = new List<string>(3);
        if (spec.AgentId is { } agentId && siblings.Any(other => other.Spec.AgentId != agentId)
            && agents.TryGetValue(agentId, out var agent))
        {
            parts.Add(agent.Name);
        }

        if (spec.ModelId is { } model && siblings.Any(other => !string.Equals(other.Spec.ModelId, model, StringComparison.Ordinal)))
            parts.Add(ChatViewModel.FormatModelDisplay(model) ?? model);
        if (spec.Effort is { } effort && siblings.Any(other => other.Spec with { Effort = effort } == spec))
            parts.Add(ModelSelectionHelper.EffortToDisplay(effort));
        return (spec.UseWorktree && siblings.Any(other => !other.Spec.UseWorktree), string.Join(" · ", parts));
    }

    /// <summary>
    /// Lights the draft's starter: the most specific one that fits it (a chosen model over the default one). Without one,
    /// the row shows where the draft starts, which its pin keeps.
    /// </summary>
    private void RefreshSetupActivity()
    {
        var draft = _owner.HasLaunchpadIncompatibleAgent ? null : _owner.GetLaunchpadDraftSetup();
        var canUndo = _owner.IsLaunchpadSetupApplied;
        var lit = LaunchpadPlanner.IndexOfMatch(Setups.Select(static setup => setup.Spec).ToList(), draft);
        for (var index = 0; index < Setups.Count; index++)
        {
            Setups[index].IsActive = index == lit;
            Setups[index].CanUndo = canUndo;
        }

        ActiveSetup = lit >= 0 ? Setups[lit] : null;
        IsOverflowActive = ActiveSetup is { IsInRow: false };
        HasUnsavedDraft = draft is not null && lit < 0;
        if (draft is null || DraftRecipe() is not { } recipe)
        {
            DraftPlace = null;
            KeepDraftTooltip = "";
            return;
        }

        var data = _dataStore.Data;
        var projects = data.Projects.ToArray().ToDictionary(static project => project.Id);
        var agents = data.Agents.ToArray().ToDictionary(static agent => agent.Id);
        var place = DescribeSetup(draft.Place, projects, agents);
        if (DraftPlace is not { } shown || shown.Choice != place)
        {
            var project = draft.ProjectId is { } projectId ? projects.GetValueOrDefault(projectId) : null;
            var agent = draft.AgentId is { } agentId ? agents.GetValueOrDefault(agentId) : null;
            DraftPlace = new LaunchpadSetupItem(place, LaunchpadBadge.For(project, agent), false, "", false,
                _ => _owner.RequestComposerFocus(), _ => { });
        }

        KeepDraftTooltip = string.Format(Loc.Culture, Loc.Launchpad_KeepSetupTooltip, DescribeSetup(recipe, projects, agents).Meta);
    }

    /// <summary>
    /// Shows on the row the starters the view found room for (<see cref="LaunchpadPlanner.FitRow"/>) and gathers the rest
    /// into its stack; a divider sets the learned starters the row shows apart from the kept ones before them.
    /// </summary>
    internal void ApplyRowFit(IReadOnlyList<bool> shown)
    {
        if (shown.Count != Setups.Count)
            return;

        var keptShown = false;
        var dividerPlaced = false;
        var overflow = 0;
        var preview = new List<LaunchpadBadge>(3);
        for (var index = 0; index < Setups.Count; index++)
        {
            var setup = Setups[index];
            setup.IsInRow = shown[index];
            var divider = shown[index] && !setup.IsKept && keptShown && !dividerPlaced;
            setup.HasDividerBefore = divider;
            dividerPlaced |= divider;
            keptShown |= shown[index] && setup.IsKept;
            if (shown[index])
                continue;

            overflow++;
            if (preview.Count < 3)
                preview.Add(setup.Badge);
        }

        OverflowCount = overflow;
        if (!preview.Select(static badge => badge.Look).SequenceEqual(OverflowPreview.Select(static badge => badge.Look)))
            OverflowPreview = preview;
        IsOverflowActive = Setups.Any(static setup => setup.IsActive && !setup.IsInRow);
    }

    /// <summary>Fills the stack's shelf as it opens: the starters the row has no room for, in order, then the new starter "+".</summary>
    internal void OpenShelf()
    {
        ShelfItems.Clear();
        foreach (var setup in Setups)
        {
            if (!setup.IsInRow)
                ShelfItems.Add(setup);
        }

        ShelfItems.Add(LaunchpadNewStarterTile.Instance);
    }

    private async void SelectSetup(LaunchpadSetupItem setup)
    {
        try
        {
            await _owner.ToggleLaunchpadSetupAsync(Resolve(setup.Spec), setup.IsActive);
        }
        catch (Exception ex)
        {
            // Applying reads git state for the worktree toggle; a failure leaves the draft as far as it got.
            System.Diagnostics.Trace.TraceWarning($"[Launchpad] Applying a starter failed: {ex}");
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
