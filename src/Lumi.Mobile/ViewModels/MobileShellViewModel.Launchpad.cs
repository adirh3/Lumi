using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Remote.Protocol;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.ViewModels;

/// <summary>
/// A starter's badge, as the desktop draws it: the line icon the user gave it, else its project's initial on a tint of the
/// project's hue (the hue the chat's mark will wear once it starts), its agent's emoji, or a sparkle.
/// </summary>
public sealed class LaunchpadBadge
{
    private readonly ChatIdentityMark? _projectMark;

    private LaunchpadBadge(string? glyph, Guid? projectId, string? iconKey)
    {
        IconKey = string.IsNullOrWhiteSpace(iconKey) ? null : iconKey;
        Glyph = string.IsNullOrWhiteSpace(glyph) ? "✦" : glyph;
        _projectMark = IconKey is null && projectId is { } id ? new ChatIdentityMark(Glyph, ChatIdentityMark.HueFor(id)) : null;
    }

    /// <summary>The icon's key (<see cref="RemoteNewChatExperience.StarterIcons"/>), drawn instead of the place's badge.</summary>
    public string? IconKey { get; }
    public bool HasIcon => IconKey is not null;

    /// <summary>Its project's initial, its agent's emoji or a sparkle.</summary>
    public string Glyph { get; }
    public bool HasProjectMark => _projectMark is not null;

    /// <summary>An agent's emoji or the sparkle, on the quiet tile.</summary>
    public bool HasGlyph => !HasIcon && !HasProjectMark;
    public IBrush? MarkBackground => _projectMark?.Background;
    public IBrush? MarkForeground => _projectMark?.Foreground;

    internal static LaunchpadBadge Of(string? glyph, Guid? projectId, string? iconKey = null) => new(glyph, projectId, iconKey);
}

/// <summary>
/// A starter on the new chat: one the user kept (under its name, maybe with an icon and a chosen model), or a place Lumi
/// learned they start in, Just chat among them; from a PC before the launchpad, one of its usual setups. A tap starts the
/// draft there.
/// </summary>
public sealed partial class ChatStartSetup(RemoteChatSetup setup) : ObservableObject
{
    internal RemoteChatSetup Source { get; } = setup;
    public string Id => Source.Id;
    public Guid? ProjectId => Source.ProjectId;
    public Guid? AgentId => Source.AgentId;

    /// <summary>The model applying it starts with: for one on the default model, the default model now.</summary>
    public string? Model => Source.Model;

    /// <summary>Its name: the one the user gave it, else its place's.</summary>
    public string Title => Source.Title;

    /// <summary>Its place's name ("Lumi", "Just chat"), which a name of its own replaces.</summary>
    public string PlaceTitle => string.IsNullOrWhiteSpace(Source.PlaceTitle) ? Source.Title : Source.PlaceTitle;

    /// <summary>Everything it starts with, in words: "Lumi · Worktree · Default model".</summary>
    public string Description => Source.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Source.Description);

    /// <summary>Its badge: the icon the user gave it, else its place's.</summary>
    public LaunchpadBadge Badge { get; } = LaunchpadBadge.Of(setup.Glyph, setup.ProjectId, setup.Icon);

    /// <summary>Its place's badge, whatever icon it wears.</summary>
    public LaunchpadBadge PlaceBadge { get; } = LaunchpadBadge.Of(setup.Glyph, setup.ProjectId);

    /// <summary>It starts with the user's default model, whatever that is when it's used, so it is named back without one.</summary>
    public bool IsDefaultModel => Source.IsDefaultModel;

    /// <summary>The user kept it, rather than Lumi learning it from where they start.</summary>
    public bool IsKept => Source.IsKept;

    /// <summary>Lumi can stop suggesting it: a learned place other than Just chat.</summary>
    public bool CanHide => Source.CanHide;

    public bool IsWorktree => Source.UseWorktree;
    public bool CanUseWorktree => Source.CanUseWorktree;

    /// <summary>Another starter has its name and stays in the project's folder, so a branch follows its name.</summary>
    public bool TellsWorktree => Source.TellsWorktree;

    /// <summary>What else tells it from another starter of its name: its agent, model or effort.</summary>
    public string Detail => Source.Detail ?? "";
    public bool HasDetail => Detail.Length > 0;

    /// <summary>What its sheet says under its name: how often the user starts there, or that its model was retired.</summary>
    public string Note => Source.Note ?? "";
    public bool HasNote => Note.Length > 0;

    /// <summary>Its name as a screen reader says it, with what tells it from another of its name.</summary>
    public string AccessibleName => string.Join(" · ",
        new[] { Title, TellsWorktree ? Strings.Get("NewWorktree") : "", Detail }.Where(static part => part.Length > 0));

    internal bool IsJustChat => ProjectId is null && AgentId is null;

    /// <summary>It is the draft's starter, lit on the row.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>It is the draft's starter on a PC with the launchpad, so its ⋯ opens its sheet.</summary>
    [ObservableProperty] private bool _showsMore;

    /// <summary>It has room on the row; the others gather into its stack.</summary>
    [ObservableProperty] private bool _isInRow = true;

    /// <summary>
    /// How closely it fits the draft, as the desktop's planner decides: -1 when it starts elsewhere, or its chosen model or
    /// effort isn't the draft's; otherwise how much it pins down (a chosen model counts more than a chosen effort), so the
    /// most specific starter is lit. One on the default model fits whatever model the draft has.
    /// </summary>
    internal int MatchScore(Guid? projectId, Guid? agentId, bool useWorktree, string? model, string? quality)
    {
        var hasModel = !IsDefaultModel && !string.IsNullOrEmpty(Source.Model);
        var hasEffort = !IsDefaultModel && !string.IsNullOrEmpty(Source.Quality);
        if (ProjectId != projectId
            || AgentId != agentId
            || (ProjectId is not null && IsWorktree) != useWorktree
            || (hasModel && !string.Equals(Source.Model, model, StringComparison.Ordinal))
            || (hasEffort && !string.Equals(Source.Quality, quality, StringComparison.Ordinal)))
        {
            return -1;
        }

        return (hasModel ? 2 : 0) + (hasEffort ? 1 : 0);
    }

    /// <summary>Whether the PC would show this starter the same way, so a refreshed row can keep this one.</summary>
    internal bool LooksLike(RemoteChatSetup other) =>
        (Source.Id, Source.Title, Source.PlaceTitle, Source.Description, Source.Glyph, Source.Icon, Source.ProjectId,
            Source.ProjectName, Source.AgentId, Source.AgentName, Source.AgentGlyph, Source.UseWorktree)
        == (other.Id, other.Title, other.PlaceTitle, other.Description, other.Glyph, other.Icon, other.ProjectId,
            other.ProjectName, other.AgentId, other.AgentName, other.AgentGlyph, other.UseWorktree)
        && (Source.Model, Source.Quality, Source.Effort, Source.IsDefaultModel, Source.TellsWorktree, Source.Detail,
            Source.IsKept, Source.CanHide, Source.Note, Source.CanUseWorktree)
        == (other.Model, other.Quality, other.Effort, other.IsDefaultModel, other.TellsWorktree, other.Detail,
            other.IsKept, other.CanHide, other.Note, other.CanUseWorktree);
}

/// <summary>A tab of the launchpad: For you, or a kind of task.</summary>
public sealed class LaunchpadTab(string id, string title)
{
    public string Id { get; } = id;
    public string Title { get; } = title;
}

/// <summary>One run of a starter's label: plain words, or a blank the user fills in before sending.</summary>
/// <param name="SpaceBefore">The template has a space before this part; parts are laid out side by side.</param>
public sealed record LaunchpadTemplatePart(string Text, bool IsBlank, bool SpaceBefore);

/// <summary>
/// A conversation starter. Picking it puts its prompt in the composer with its first blank selected; nothing is sent.
/// </summary>
public sealed class LaunchpadStarter
{
    public LaunchpadStarter(string? id, string iconKey, string label, string prompt, string? reason, bool isFromBrief, string? glyph = null)
    {
        Id = id;
        IconKey = iconKey;
        Label = label;
        Prompt = prompt;
        Reason = reason;
        IsFromBrief = isFromBrief;
        Glyph = glyph;
        Parts = LaunchpadTemplate.Parse(label);
        Blanks = LaunchpadTemplate.Blanks(prompt);
    }

    /// <summary>Its catalog id, which the PC learns the user's habits by; null for a plan from a brief.</summary>
    public string? Id { get; }

    /// <summary>Its line icon (<see cref="RemoteProtocol.LaunchpadIcons"/>).</summary>
    public string IconKey { get; }

    /// <summary>The emoji an older PC sends instead of an icon.</summary>
    public string? Glyph { get; }
    public bool HasGlyph => !string.IsNullOrWhiteSpace(Glyph);

    public string Label { get; }
    public string Prompt { get; }

    /// <summary>Why For you offers it ("From Lumi's brief"), when it says.</summary>
    public string? Reason { get; }

    /// <summary>A plan from the project's brief, which wears Lumi's accent among the starters.</summary>
    public bool IsFromBrief { get; }

    /// <summary>The label as words and blanks, for its capsule.</summary>
    public IReadOnlyList<LaunchpadTemplatePart> Parts { get; }

    /// <summary>The prompt's blanks, brackets included, which the composer selects in turn.</summary>
    public IReadOnlyList<string> Blanks { get; }

    /// <summary>The label with its blanks spoken as they read ("Plan a trip to where?").</summary>
    public string AccessibleName => string.Join(" ", Parts.Select(static part => part.Text));

    internal bool LooksLike(LaunchpadStarter other) =>
        Id == other.Id && IconKey == other.IconKey && Glyph == other.Glyph && Label == other.Label
        && Prompt == other.Prompt && Reason == other.Reason && IsFromBrief == other.IsFromBrief;
}

/// <summary>
/// Starter templates, as the desktop reads them: a blank is a short bracketed label ("Plan a trip to [where?]"). The
/// tile shows it as a dotted blank; the composer receives the template as it is and selects each blank in turn.
/// </summary>
internal static partial class LaunchpadTemplate
{
    [GeneratedRegex(@"\[[^\[\]\r\n]{1,40}\]")]
    private static partial Regex Blank();

    /// <summary>The template's blanks as they appear in it, brackets included, in order and without repeats.</summary>
    internal static IReadOnlyList<string> Blanks(string template)
        => Blank().Matches(template).Select(static match => match.Value).Distinct(StringComparer.Ordinal).ToList();

    internal static IReadOnlyList<LaunchpadTemplatePart> Parse(string template)
    {
        var parts = new List<LaunchpadTemplatePart>();
        var position = 0;
        foreach (Match match in Blank().Matches(template))
        {
            AddText(template[position..match.Index]);
            var spaceBefore = parts.Count > 0 && match.Index > 0 && char.IsWhiteSpace(template[match.Index - 1]);
            parts.Add(new LaunchpadTemplatePart(match.Value[1..^1].Trim(), IsBlank: true, spaceBefore));
            position = match.Index + match.Length;
        }

        AddText(template[position..]);
        return parts;

        void AddText(string text)
        {
            var trimmed = text.Trim();
            if (trimmed.Length > 0)
                parts.Add(new LaunchpadTemplatePart(trimmed, IsBlank: false, parts.Count > 0 && char.IsWhiteSpace(text[0])));
        }
    }

    /// <summary>The words as a starter's label shows them: its parts, spaced where the template spaces them.</summary>
    internal static string Displayed(IReadOnlyList<LaunchpadTemplatePart> parts)
        => string.Concat(parts.Select(static (part, index) => index > 0 && part.SpaceBefore ? " " + part.Text : part.Text));
}

/// <summary>
/// A place a new starter can start in, in the new starter sheet's places: Just chat, a project (with the checkout the user
/// last worked there in) or an agent. A tap starts the new starter there; its own place is checked.
/// </summary>
public sealed partial class LaunchpadPlace(RemoteLaunchpadPlace place, bool isJustChat = false) : ObservableObject
{
    internal RemoteLaunchpadPlace Source { get; } = place;
    public Guid Id => Source.Id;
    public bool IsProject => !IsJustChat && Source.IsProject;
    public bool IsJustChat { get; } = isJustChat;
    public string Title => Source.Title;

    /// <summary>When the user last worked there ("40m ago").</summary>
    public string Hint => Source.Hint ?? "";
    public bool HasHint => !string.IsNullOrEmpty(Source.Hint);

    /// <summary>It is where the new starter starts.</summary>
    [ObservableProperty] private bool _isCurrent;

    /// <summary>Its badge, and how a starter there starts.</summary>
    public ChatStartSetup Setup { get; } = new(place.Setup);

    internal Guid? ProjectId => IsProject ? Id : null;
    internal Guid? AgentId => IsJustChat || Source.IsProject ? null : Id;
}

/// <summary>A chat waiting on the user, in the needs-you sheet.</summary>
public sealed class LaunchpadChat(RemoteLaunchpadChat chat)
{
    public Guid ChatId => chat.ChatId;
    public string Title => chat.Title;

    /// <summary>It asks the user something; otherwise a reply waits to be read.</summary>
    public bool IsWaiting => chat.IsWaiting;
    public bool IsReply => !chat.IsWaiting;
    public string StateLabel => chat.StateLabel;
    public string Meta => chat.Meta;
    public bool HasMeta => chat.Meta.Length > 0;
    public string TimeLabel => chat.TimeLabel;
}

/// <summary>A model a kept starter can start with, in its sheet: the default model, or a chosen one.</summary>
public sealed class LaunchpadModelChoice(string? modelId, string title, string hint, bool isCurrent)
{
    /// <summary>The model, or null for the user's default, whatever that is when the starter is used.</summary>
    public string? ModelId { get; } = modelId;
    public string Title { get; } = title;

    /// <summary>For the default model, what it is now.</summary>
    public string Hint { get; } = hint;
    public bool HasHint => Hint.Length > 0;
    public bool IsCurrent { get; } = isCurrent;
}

/// <summary>An effort a kept starter's own model works at, in its sheet, as the model picker names it (a Fusion tier among them).</summary>
public sealed class LaunchpadQualityChoice(string title, bool isCurrent)
{
    public string Title { get; } = title;
    public bool IsCurrent { get; } = isCurrent;
}

/// <summary>An icon a kept starter can wear, in its sheet: its place's badge, or a line icon.</summary>
public sealed class LaunchpadIconChoice(string? key, LaunchpadBadge badge, bool isCurrent, string accessibleName)
{
    /// <summary>The icon's key (<see cref="RemoteNewChatExperience.StarterIcons"/>), or null for the place's badge.</summary>
    public string? Key { get; } = key;
    public LaunchpadBadge Badge { get; } = badge;
    public bool IsCurrent { get; } = isCurrent;
    public string AccessibleName { get; } = accessibleName;
}

/// <summary>
/// Which starters the setups row shows, as the desktop's planner fits its own: all of them when they fit beside the new
/// starter "+"; otherwise, beside the stack, as many as fit in order. The lit one (<c>keepIndex</c>) joins them in the place
/// of learned starters from the row's end, never of kept ones, which hold their places whatever is lit; without such room
/// it stays in the stack, which then wears its light. Widths include each capsule's margins; <c>fixedWidth</c> is what the
/// row always shows besides them (the draft's dashed chip, while no starter fits the draft).
/// </summary>
internal static class LaunchpadRowFit
{
    internal static bool[] Fit(
        IReadOnlyList<double> widths,
        IReadOnlyList<bool> isKept,
        double available,
        double fixedWidth,
        double endWidth,
        double stackWidth,
        int keepIndex)
    {
        var count = widths.Count;
        var shown = new bool[count];
        Array.Fill(shown, true);
        if (Width() + fixedWidth + endWidth <= available)
            return shown;

        Array.Fill(shown, false);
        var budget = available - fixedWidth - stackWidth;
        for (var index = 0; index < count; index++)
        {
            shown[index] = true;
            if (Width() <= budget)
                continue;

            shown[index] = false;
            break;
        }

        if (keepIndex >= 0 && keepIndex < count && !shown[keepIndex])
        {
            var fitted = (bool[])shown.Clone();
            shown[keepIndex] = true;
            for (var index = count - 1; index >= 0 && Width() > budget; index--)
            {
                if (index != keepIndex && !isKept[index])
                    shown[index] = false;
            }

            if (Width() > budget)
                Array.Copy(fitted, shown, count);
        }

        return shown;

        double Width()
        {
            var total = 0d;
            for (var index = 0; index < count; index++)
            {
                if (shown[index])
                    total += widths[index];
            }

            return total;
        }
    }
}

/// <summary>
/// The new chat's launchpad, as the PC projects it (<see cref="RemoteNewChatExperience"/>): the greeting and the line
/// under it, For you and a tab per kind of task with their starters, and the starters row on the composer. The row holds
/// the starters the user kept, by their names, then the places Lumi learned they start in, Just chat among them. The most
/// specific one that fits the draft is lit; those the screen has no room for gather into a stack whose speed dial rises
/// above it; and a draft no starter fits shows where it starts, dashed, with a pin that keeps it. Holding a starter opens
/// its sheet (keep, rename or stop suggesting a learned one; the name, icon, checkout and model of a kept one), and the new
/// starter sheet is that sheet for a starter not kept yet, filled from the draft, its place chosen from a list too. A PC
/// from before the launchpad still gets its three setups and starters.
/// </summary>
public sealed partial class MobileShellViewModel
{
    internal const string ForYouTabId = "for-you";

    /// <summary>What the starter sheet and the dashed chip show before they first have a starter, so their bindings always do.</summary>
    private static readonly ChatStartSetup NoStarter = new(new RemoteChatSetup());

    /// <summary>
    /// The starter the new starter sheet makes, as it has it so far: its place and checkout, its model as a picker row
    /// (null: the default model) and its effort as the picker names it, and its icon (null: its place's badge).
    /// </summary>
    private sealed record PendingStarter(Guid? ProjectId, Guid? AgentId, bool UseWorktree, string? Model, string? Quality, string? Icon);

    private RemoteNewChatExperience? _newChatExperience;
    private IReadOnlyList<LaunchpadPlace> _launchpadPlaces = [];
    private IReadOnlyList<string> _starterIcons = [];
    private string? _desktopGreeting;
    private PendingStarter _newStarter = new(null, null, false, null, null, null);

    /// <summary>The chat list's project when a setup was first applied to the draft.</summary>
    private Guid? _activeProjectBeforeSetup;

    /// <summary>Asks the view to begin a starter's fill-in on the composer: its blanks, in order.</summary>
    internal event Action<IReadOnlyList<string>>? StarterApplied;

    /// <summary>Asks the view to focus the starter sheet's name field, once Rename has kept the starter.</summary>
    internal event Action? StarterNameFocusRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartSetups))]
    [NotifyPropertyChangedFor(nameof(ShowsSetupRow))]
    private IReadOnlyList<ChatStartSetup> _startSetups = [];

    public bool HasStartSetups => StartSetups.Count > 0;

    /// <summary>The starters row: the launchpad's (Just chat at least), or an older PC's setups.</summary>
    public bool ShowsSetupRow => HasLaunchpad || HasStartSetups;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Greeting))]
    [NotifyPropertyChangedFor(nameof(GreetingParts))]
    private bool _hasDesktopNewChat;

    /// <summary>The line under the greeting: the project brief's opening sentence, or the invitation.</summary>
    [ObservableProperty] private string _newChatBrief = "";
    [ObservableProperty] private string _startSetupsTitle = "";

    /// <summary>The PC projects the launchpad: its tabs, its starters and their sheets.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLaunchpadTabs))]
    [NotifyPropertyChangedFor(nameof(ShowsSetupRow))]
    [NotifyPropertyChangedFor(nameof(ShowsNewStarterButton))]
    private bool _hasLaunchpad;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StarterStackAccessibleName))]
    [NotifyPropertyChangedFor(nameof(DraftSetupAccessibleName))]
    [NotifyPropertyChangedFor(nameof(SetupSheetTitle))]
    [NotifyPropertyChangedFor(nameof(SetupSheetAccessibleName))]
    private RemoteLaunchpadLabels _launchpadLabels = new();

    /// <summary>What a starter on the default model starts with now ("GPT-6.1 Sol"), beside Default model in its sheet.</summary>
    [ObservableProperty] private string _defaultModelName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLaunchpadTabs))]
    private IReadOnlyList<LaunchpadTab> _launchpadTabs = [];

    public bool HasLaunchpadTabs => HasLaunchpad && LaunchpadTabs.Count > 1;

    [ObservableProperty] private LaunchpadTab? _selectedLaunchpadTab;

    /// <summary>The open tab's starters; For you follows the project the new chat starts in.</summary>
    [ObservableProperty] private IReadOnlyList<LaunchpadStarter> _launchpadStarters = [];

    /// <summary>
    /// The words of every tab's starters where the new chat starts (For you, then each tab), as their capsules show
    /// them, so the view keeps room for the tab needing the most lines and switching tabs moves nothing. Empty for an
    /// older PC, which has no tabs.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<IReadOnlyList<string>> _launchpadStarterTexts = [];

    // ── The starters row ─────────────────────────────────────────────────────────────────────

    /// <summary>The draft's starter: the most specific one that fits it, lit on the row, which always has room for it.</summary>
    [ObservableProperty] private ChatStartSetup? _activeStartSetup;

    /// <summary>No starter fits the draft (a project picked in the composer, say), so the row shows where it starts, dashed, with a pin.</summary>
    [ObservableProperty] private bool _hasUnsavedDraft;

    /// <summary>Where the draft starts, as a starter shows it: the dashed chip, and the head of the new starter sheet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DraftSetupAccessibleName))]
    private ChatStartSetup _draftSetup = NoStarter;

    /// <summary>The dashed chip as a screen reader says it: "This chat's setup: Travel".</summary>
    public string DraftSetupAccessibleName => LaunchpadLabels.DraftSetup is { Length: > 0 } label
        ? $"{label}: {DraftSetup.Title}"
        : DraftSetup.Title;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(KeepDraftCommand))]
    private bool _canKeepDraft;

    /// <summary>How many starters the row has no room for, gathered into its stack.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStarterStack))]
    [NotifyPropertyChangedFor(nameof(StarterStackLabel))]
    [NotifyPropertyChangedFor(nameof(StarterStackAccessibleName))]
    [NotifyPropertyChangedFor(nameof(ShowsNewStarterButton))]
    private int _starterStackCount;

    /// <summary>The first three of them, as the stack's overlapping badges.</summary>
    [ObservableProperty] private IReadOnlyList<LaunchpadBadge> _starterStackPreview = [];

    /// <summary>The lit starter is in the stack (the row's room went to kept starters), so the stack wears its light.</summary>
    [ObservableProperty] private bool _isStarterStackSelected;

    /// <summary>The starters the row has no room for, in its order: the speed dial, the nearest first.</summary>
    [ObservableProperty] private IReadOnlyList<ChatStartSetup> _starterDialItems = [];

    public bool HasStarterStack => StarterStackCount > 0;
    public string StarterStackLabel => "+" + StarterStackCount.ToString(CultureInfo.CurrentCulture);

    /// <summary>The stack's name: "7 more starters", or "1 more starter".</summary>
    public string StarterStackAccessibleName => StarterStackCount == 1 && LaunchpadLabels.MoreStarter is { Length: > 0 } one
        ? one
        : LaunchpadLabels.MoreStarters is { } label && label.Contains("{0}", StringComparison.Ordinal)
            ? string.Format(CultureInfo.CurrentCulture, label, StarterStackCount)
            : StarterStackLabel;

    /// <summary>Every starter fits, so the row ends with a round "+" that opens the new starter sheet.</summary>
    public bool ShowsNewStarterButton => HasLaunchpad && !HasStarterStack;

    /// <summary>The stack's speed dial: the starters the row has no room for, risen above it over a scrim.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanDragDrawer))]
    private bool _isStarterDialOpen;

    // ── The new starter's places ─────────────────────────────────────────────────────────────

    [ObservableProperty] private string _placeQuery = "";

    /// <summary>Just chat, first among the places while the search allows it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasJustChatPlace))]
    [NotifyPropertyChangedFor(nameof(HasNoPlaceMatch))]
    private LaunchpadPlace? _justChatPlace;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlaceProjects))]
    [NotifyPropertyChangedFor(nameof(HasNoPlaceMatch))]
    private IReadOnlyList<LaunchpadPlace> _placeProjects = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlaceAgents))]
    [NotifyPropertyChangedFor(nameof(HasNoPlaceMatch))]
    private IReadOnlyList<LaunchpadPlace> _placeAgents = [];

    public bool HasJustChatPlace => JustChatPlace is not null;
    public bool HasPlaceProjects => PlaceProjects.Count > 0;
    public bool HasPlaceAgents => PlaceAgents.Count > 0;
    public bool HasNoPlaceMatch => JustChatPlace is null && PlaceProjects.Count == 0 && PlaceAgents.Count == 0;

    // ── A starter's sheet, and the new starter sheet ─────────────────────────────────────────

    /// <summary>The starter whose sheet is open; null once it closes or its starter is gone.</summary>
    private ChatStartSetup? _setupSheetTarget;

    /// <summary>
    /// How to find the sheet's starter once the PC sends the row back with a change the sheet asked for: changing what it
    /// starts with names it anew on the PC.
    /// </summary>
    private Func<ChatStartSetup, bool>? _setupSheetFollow;

    /// <summary>The name the field last showed or sent, so a refresh never overwrites one the user is typing.</summary>
    private string _setupSheetShownName = "";

    /// <summary>Rename kept the starter: its name field takes focus once the sheet shows it kept.</summary>
    private bool _focusNameWhenKept;

    /// <summary>The sheet is showing its starter, so its worktree switch changes nothing on the PC.</summary>
    private bool _showingSetupSheet;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanDragDrawer))]
    private bool _isSetupSheetOpen;

    /// <summary>The starter the sheet shows: a learned one's place and how often the user starts there, or a kept one's recipe.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSetupSheetLearned))]
    [NotifyPropertyChangedFor(nameof(ShowsSetupSheetForm))]
    [NotifyPropertyChangedFor(nameof(SetupSheetAccessibleName))]
    private ChatStartSetup _setupSheetStarter = NoStarter;

    /// <summary>
    /// The sheet makes a new starter (the new starter sheet): <see cref="SetupSheetStarter"/> is the one Add keeps, as the
    /// sheet has it so far, and nothing is kept until then.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSetupSheetLearned))]
    [NotifyPropertyChangedFor(nameof(ShowsSetupSheetForm))]
    [NotifyPropertyChangedFor(nameof(SetupSheetTitle))]
    [NotifyPropertyChangedFor(nameof(SetupSheetAccessibleName))]
    private bool _isSetupSheetNew;

    /// <summary>The new starter's places show in place of its form, to choose where it starts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSetupSheetForm))]
    private bool _isChoosingStarterPlace;

    /// <summary>A learned starter's part of the sheet: where it starts, how often, and Keep, Rename and Don't suggest.</summary>
    public bool ShowsSetupSheetLearned => !IsSetupSheetNew && !SetupSheetStarter.IsKept;

    /// <summary>The form a kept starter's sheet and the new starter sheet share: its name, icon, place, checkout and model.</summary>
    public bool ShowsSetupSheetForm => (IsSetupSheetNew || SetupSheetStarter.IsKept) && !IsChoosingStarterPlace;

    /// <summary>The sheet's title: New starter while it makes one; a starter's own sheet shows its name instead.</summary>
    public string? SetupSheetTitle => IsSetupSheetNew ? LaunchpadLabels.NewStarter : null;

    public string SetupSheetAccessibleName => IsSetupSheetNew ? LaunchpadLabels.NewStarter : SetupSheetStarter.Title;

    /// <summary>A kept starter's name as it is being typed, kept on Done and as the sheet closes; a new one's, kept on Add.</summary>
    [ObservableProperty] private string _setupSheetName = "";

    /// <summary>The starter starts in a worktree.</summary>
    [ObservableProperty] private bool _setupSheetWorktree;

    [ObservableProperty] private IReadOnlyList<LaunchpadIconChoice> _setupSheetIcons = [];
    [ObservableProperty] private IReadOnlyList<LaunchpadModelChoice> _setupSheetModels = [];

    /// <summary>The efforts a kept starter's own model offers (none on the default model, which brings the default's).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSetupSheetQualities))]
    private IReadOnlyList<LaunchpadQualityChoice> _setupSheetQualities = [];

    public bool HasSetupSheetQualities => SetupSheetQualities.Count > 1;

    /// <summary>What those efforts are called: Copilot Fusion's tiers, or reasoning effort.</summary>
    [ObservableProperty] private string _setupSheetQualityName = "";

    /// <summary>A change to what the starter starts with is on its way; the sheet takes no other until the PC sends the row back.</summary>
    [ObservableProperty] private bool _isSetupSheetBusy;

    // ── Needs you ────────────────────────────────────────────────────────────────────────────

    /// <summary>"2 chats need you", while any do.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNeedsYou))]
    private string _needsYouLabel = "";

    public bool HasNeedsYou => NeedsYouLabel.Length > 0;

    [ObservableProperty] private IReadOnlyList<LaunchpadChat> _needsYouChats = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanDragDrawer))]
    private bool _isNeedsYouSheetOpen;

    /// <summary>A launchpad sheet or the speed dial is open, which Back closes first.</summary>
    private bool IsLaunchpadSurfaceOpen => IsSetupSheetOpen || IsNeedsYouSheetOpen || IsStarterDialOpen;

    internal void ApplyNewChatExperience(RemoteNewChatExperience? experience)
    {
        _newChatExperience = experience;
        _desktopGreeting = experience?.Greeting;
        HasDesktopNewChat = experience is not null;
        // A PC from before the launchpad sends no tabs, and one with the earlier launchpad no starter icons: the phone
        // shows either one's three setups and starters for the hour.
        HasLaunchpad = experience is { Tabs.Count: > 0, StarterIcons.Count: > 0 };
        StartSetupsTitle = experience?.SetupsTitle ?? Strings.Get("UsualSetups");
        LaunchpadLabels = experience?.Labels ?? new RemoteLaunchpadLabels();
        DefaultModelName = experience?.DefaultModel ?? "";
        _starterIcons = experience?.StarterIcons ?? [];

        // The launchpad's starters, which the row fits to the screen; an older PC's three. A refresh that shows the same
        // starters keeps their capsules, and a starter that stays keeps its place until the view fits the row again.
        List<RemoteChatSetup> setups = experience is null ? [] : HasLaunchpad ? experience.Setups : experience.Setups.Take(3).ToList();
        if (StartSetups.Count != setups.Count || StartSetups.Where((setup, index) => !setup.LooksLike(setups[index])).Any())
        {
            var previous = StartSetups;
            StartSetups = setups.Select(setup => new ChatStartSetup(setup)
            {
                IsInRow = previous.FirstOrDefault(old => old.Id == setup.Id)?.IsInRow ?? true
            }).ToArray();
            ApplyStarterRowFit(StartSetups.Select(static setup => setup.IsInRow).ToArray());
        }

        _launchpadPlaces = experience?.Places.Select(place => new LaunchpadPlace(place)).ToArray() ?? [];
        FilterPlaces();

        NeedsYouLabel = experience?.NeedsYou ?? "";
        NeedsYouChats = experience?.NeedsYouChats.Select(chat => new LaunchpadChat(chat)).ToArray() ?? [];
        if (!HasNeedsYou)
            IsNeedsYouSheetOpen = false;
        if (!HasLaunchpad)
        {
            _setupSheetTarget = null;
            IsSetupSheetOpen = false;
        }

        Chat.ApplyNewChatStarters(experience?.Starters);
        RefreshLaunchpadTabs();
        RefreshLaunchpadScope();
        RefreshSetupSelection();
        RetargetSetupSheet();
        OnPropertyChanged(nameof(Greeting));
        OnPropertyChanged(nameof(GreetingParts));
    }

    private void RefreshLaunchpadTabs()
    {
        var experience = _newChatExperience;
        IReadOnlyList<LaunchpadTab> tabs = HasLaunchpad && experience is not null
            ? [new LaunchpadTab(ForYouTabId, LaunchpadLabels.ForYou), .. experience.Tabs.Select(tab => new LaunchpadTab(tab.Id, tab.Title))]
            : [];
        var unchanged = tabs.Count == LaunchpadTabs.Count
                        && tabs.Zip(LaunchpadTabs).All(pair => pair.First.Id == pair.Second.Id && pair.First.Title == pair.Second.Title);
        if (!unchanged)
        {
            var selectedId = SelectedLaunchpadTab?.Id ?? ForYouTabId;
            LaunchpadTabs = tabs;
            SelectedLaunchpadTab = tabs.FirstOrDefault(tab => tab.Id == selectedId) ?? tabs.FirstOrDefault();
        }
    }

    partial void OnSelectedLaunchpadTabChanged(LaunchpadTab? value) => RefreshLaunchpadStarters();

    /// <summary>For you and the line under the greeting for the project the new chat starts in, or for none.</summary>
    private RemoteLaunchpadScope? CurrentLaunchpadScope()
    {
        if (_newChatExperience is not { Scopes.Count: > 0 } experience)
            return null;

        var projectId = Guid.TryParse(Chat.ProjectValue, out var parsed) ? parsed : (Guid?)null;
        return (projectId is null ? null : experience.Scopes.FirstOrDefault(scope => scope.ProjectId == projectId))
               ?? experience.Scopes.FirstOrDefault(static scope => scope.ProjectId is null)
               ?? experience.Scopes[0];
    }

    private void RefreshLaunchpadScope()
    {
        NewChatBrief = _newChatExperience is null
            ? Strings.Get("NewChatBrief")
            : CurrentLaunchpadScope()?.Subtitle is { Length: > 0 } subtitle ? subtitle : _newChatExperience.Brief;
        RefreshLaunchpadStarterTexts();
        RefreshLaunchpadStarters();
    }

    private void RefreshLaunchpadStarterTexts()
    {
        IReadOnlyList<IReadOnlyList<string>> texts = HasLaunchpad && _newChatExperience is { } experience
            ? [Texts(CurrentLaunchpadScope()?.ForYou ?? []), .. experience.Tabs.Select(tab => Texts(tab.Starters))]
            : [];
        if (texts.Count != LaunchpadStarterTexts.Count
            || texts.Where((set, index) => !set.SequenceEqual(LaunchpadStarterTexts[index], StringComparer.Ordinal)).Any())
        {
            LaunchpadStarterTexts = texts;
        }

        static IReadOnlyList<string> Texts(IEnumerable<RemoteLaunchpadStarter> starters)
            => starters.Select(starter => LaunchpadTemplate.Displayed(LaunchpadTemplate.Parse(starter.Label))).ToArray();
    }

    private void RefreshLaunchpadStarters()
    {
        IReadOnlyList<LaunchpadStarter> starters;
        if (!HasLaunchpad || _newChatExperience is not { } experience)
        {
            // Before the launchpad: the PC's starters for the hour, or the phone's own.
            starters = Chat.Starters.Select(starter => new LaunchpadStarter(
                null, LegacyIconKey(starter.IconKey), starter.Text, starter.EffectivePrompt, null, isFromBrief: false, starter.Glyph)).ToArray();
        }
        else if (SelectedLaunchpadTab is not { } tab || tab.Id == ForYouTabId)
        {
            starters = (CurrentLaunchpadScope()?.ForYou ?? []).Select(ToStarter).ToArray();
        }
        else
        {
            starters = (experience.Tabs.FirstOrDefault(candidate => candidate.Id == tab.Id)?.Starters ?? []).Select(ToStarter).ToArray();
        }

        if (starters.Count != LaunchpadStarters.Count || starters.Where((starter, index) => !starter.LooksLike(LaunchpadStarters[index])).Any())
            LaunchpadStarters = starters;

        static LaunchpadStarter ToStarter(RemoteLaunchpadStarter starter) => new(
            starter.Id, starter.Icon, starter.Label, starter.Prompt, starter.Reason, isFromBrief: starter.Id is null);
    }

    /// <summary>The phone's own starters name transcript icons; these are their launchpad equivalents.</summary>
    private static string LegacyIconKey(string key) => key switch
    {
        "plan" => "checklist",
        "create" => "document-plus",
        "search" or "chat" => key,
        _ => "lightbulb"
    };

    /// <summary>Puts a starter in the composer and selects its first blank; the PC hears which one, to learn the user's habits.</summary>
    [RelayCommand]
    private void UseLaunchpadStarter(LaunchpadStarter? starter)
    {
        if (starter is null || string.IsNullOrWhiteSpace(starter.Prompt))
            return;

        Chat.PromptText = starter.Prompt;
        StarterApplied?.Invoke(starter.Blanks);
        if (HasLaunchpad && starter.Id is { Length: > 0 } id)
        {
            _ = SendLaunchpadCommandAsync(new RemoteCommand(RemoteProtocol.Actions.UseStarter)
                .With("id", id)
                .With("projectId", Guid.TryParse(Chat.ProjectValue, out var projectId) ? projectId.ToString() : null),
                reportFailure: false);
        }
    }

    private static Guid? ParseId(string? value) => Guid.TryParse(value, out var id) ? id : null;

    /// <summary>
    /// Lights the draft's starter: the most specific one that fits it (a chosen model over the default one), the first in
    /// the row on a tie. Without one, the row shows where the draft starts, dashed, with a pin that keeps it.
    /// </summary>
    private void RefreshSetupSelection()
    {
        var projectId = ParseId(Chat.ProjectValue);
        var agentId = ParseId(Chat.AgentValue);
        var worktree = projectId is not null && Chat.UseWorktree;

        // A Copilot agent (one without a Lumi agent's id) is no place a starter holds: nothing is lit or offered to keep.
        var hasDraft = !Chat.HasChat && (agentId is not null || string.IsNullOrWhiteSpace(Chat.AgentName));
        ChatStartSetup? lit = null;
        var best = -1;
        if (hasDraft)
        {
            foreach (var setup in StartSetups)
            {
                var score = setup.MatchScore(projectId, agentId, worktree, Chat.Model, Chat.Quality);
                if (score > best)
                    (lit, best) = (setup, score);
            }
        }

        foreach (var setup in StartSetups)
        {
            setup.IsSelected = ReferenceEquals(setup, lit);
            setup.ShowsMore = HasLaunchpad && setup.IsSelected;
        }

        ActiveStartSetup = lit;
        IsStarterStackSelected = lit is { IsInRow: false };
        var canKeep = HasLaunchpad && hasDraft;
        HasUnsavedDraft = canKeep && lit is null;
        CanKeepDraft = HasUnsavedDraft && !_keepingDraft;
        if (canKeep)
        {
            var draft = DescribePlace("draft", projectId, agentId, worktree);
            if (!DraftSetup.LooksLike(draft))
                DraftSetup = new ChatStartSetup(draft);
        }
    }

    /// <summary>
    /// A place as a starter shows it: its name, and its project's initial, its agent's emoji or a sparkle, as the PC's
    /// places name it, else as the draft does (one the PC's list leaves out); no project or agent is Just chat.
    /// </summary>
    private RemoteChatSetup DescribePlace(string id, Guid? projectId, Guid? agentId, bool worktree)
    {
        var listedProject = projectId is { } projectKey ? _launchpadPlaces.FirstOrDefault(place => place.IsProject && place.Id == projectKey) : null;
        var listedAgent = agentId is { } agentKey ? _launchpadPlaces.FirstOrDefault(place => place.AgentId == agentKey) : null;
        var draftProject = projectId is not null && projectId == ParseId(Chat.ProjectValue);
        var draftAgent = agentId is not null && agentId == ParseId(Chat.AgentValue);
        var projectName = listedProject?.Title ?? (draftProject ? Chat.ProjectName : null);
        var agentName = listedAgent?.Title ?? (draftAgent ? Chat.AgentName : null);
        var agentGlyph = listedAgent?.Setup.Source.AgentGlyph ?? (draftAgent ? Chat.AgentGlyph : null);
        var title = projectId is not null ? projectName
            : agentId is not null ? agentName
            : StartSetups.FirstOrDefault(static setup => setup.IsJustChat)?.PlaceTitle;
        return new RemoteChatSetup
        {
            Id = id,
            Title = title ?? "",
            PlaceTitle = title,
            Glyph = projectId is not null ? ChatIdentityMark.InitialOf(projectName ?? "")
                : agentGlyph is { Length: > 0 } && agentGlyph != "◉" ? agentGlyph
                : "✦",
            ProjectId = projectId,
            ProjectName = projectName,
            AgentId = agentId,
            AgentName = agentName,
            AgentGlyph = agentGlyph,
            UseWorktree = worktree,
            CanUseWorktree = listedProject?.Setup.CanUseWorktree ?? (draftProject && Chat.CanChooseWorktree)
        };
    }

    private bool CanUseStartSetup() => !Chat.HasChat && !Chat.IsBusy && !Chat.IsLoading && Chat.CanChangeProjectSelection;

    /// <summary>
    /// Applies a starter to the draft (its project, agent, checkout, model and effort; the prompt and attachments stay);
    /// tapping the lit one again puts back the draft it replaced, as on the desktop. One from the speed dial folds it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseStartSetup))]
    private void UseStartSetup(ChatStartSetup? setup)
    {
        IsStarterDialOpen = false;
        if (!CanUseStartSetup())
        {
            Chat.ErrorText = Strings.Get("SetupsBeforeFirstMessage");
            return;
        }
        if (setup is null || !StartSetups.Contains(setup))
        {
            Chat.ErrorText = Strings.Get("SetupUnavailable");
            return;
        }

        ApplySetup(setup);
    }

    private void ApplySetup(ChatStartSetup setup)
    {
        // The project lens goes first because it also sets a blank chat's project; the setup, or the
        // draft it replaced, then has the last word.
        if (!setup.IsSelected)
        {
            if (!Chat.HasDraftBeforeSetup)
            {
                _activeProjectBeforeSetup = ActiveProjectId;
                Chat.RememberDraftBeforeSetup();
            }
            ActiveProjectId = setup.ProjectId;
            Chat.ApplyStartSetup(setup.Source);
        }
        else if (Chat.HasDraftBeforeSetup)
        {
            // A draft that simply matches a setup has nothing to put back.
            ActiveProjectId = _activeProjectBeforeSetup;
            Chat.RestoreDraftBeforeSetup();
        }

        RefreshSetupSelection();
        Chat.ErrorText = null;
    }

    /// <summary>
    /// Shows on the row the starters that fit its width (<paramref name="widths"/>, as the view measured each capsule with
    /// its margins), in order, and the lit one always; the rest gather into the stack.
    /// </summary>
    internal void FitStarterRow(IReadOnlyList<double> widths, double available, double draftWidth, double newStarterWidth, double stackWidth)
    {
        if (widths.Count != StartSetups.Count)
            return;

        var lit = -1;
        for (var index = 0; index < StartSetups.Count; index++)
        {
            if (ReferenceEquals(StartSetups[index], ActiveStartSetup))
                lit = index;
        }

        ApplyStarterRowFit(LaunchpadRowFit.Fit(
            widths,
            StartSetups.Select(static setup => setup.IsKept).ToArray(),
            available,
            HasUnsavedDraft ? draftWidth : 0,
            HasLaunchpad ? newStarterWidth : 0,
            stackWidth,
            lit));
    }

    private void ApplyStarterRowFit(IReadOnlyList<bool> shown)
    {
        var overflow = new List<ChatStartSetup>();
        for (var index = 0; index < StartSetups.Count; index++)
        {
            var setup = StartSetups[index];
            setup.IsInRow = shown[index];
            if (!setup.IsInRow)
                overflow.Add(setup);
        }

        if (!overflow.SequenceEqual(StarterDialItems))
            StarterDialItems = overflow;
        var preview = overflow.Take(3).Select(static setup => setup.Badge).ToArray();
        if (!preview.SequenceEqual(StarterStackPreview))
            StarterStackPreview = preview;
        StarterStackCount = overflow.Count;
        IsStarterStackSelected = ActiveStartSetup is { IsInRow: false };
        if (overflow.Count == 0)
            IsStarterDialOpen = false;
    }

    /// <summary>The stack: its speed dial rises with the starters the row has no room for, or folds back.</summary>
    [RelayCommand]
    private void ToggleStarterDial() => IsStarterDialOpen = !IsStarterDialOpen && HasStarterStack;

    [RelayCommand]
    private void CloseStarterDial() => IsStarterDialOpen = false;

    /// <summary>The speed dial folds once the welcome gives way (a keyboard, the first message).</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsWelcomeVisible) && IsStarterDialOpen && !IsWelcomeVisible)
            IsStarterDialOpen = false;
    }

    // ── The draft's setup ────────────────────────────────────────────────────────────────────

    private bool _keepingDraft;

    /// <summary>The dashed chip's pin: keeps the draft's setup, as the composer has it, after the user's other starters.</summary>
    [RelayCommand(CanExecute = nameof(CanKeepDraft))]
    private async Task KeepDraftAsync()
    {
        _keepingDraft = true;
        CanKeepDraft = false;
        try
        {
            await SendLaunchpadCommandAsync(DraftCommand(RemoteProtocol.Actions.KeepSetup).With("kept", "true"));
        }
        finally
        {
            _keepingDraft = false;
            RefreshSetupSelection();
        }
    }

    /// <summary>
    /// The draft named as its pickers show it: its place and checkout, its model and its effort as the model picker names
    /// it (<c>quality</c>, empty for a model without efforts, which still marks it as the draft). The PC keeps a draft on
    /// its default model on the default model.
    /// </summary>
    private RemoteCommand DraftCommand(string action)
    {
        var projectId = ParseId(Chat.ProjectValue);
        return new RemoteCommand(action)
            .With("projectId", projectId?.ToString())
            .With("agentId", ParseId(Chat.AgentValue)?.ToString())
            .With("worktree", projectId is not null && Chat.UseWorktree ? "true" : "false")
            .With("model", Chat.Model)
            .With("quality", Chat.Quality ?? "");
    }

    /// <summary>
    /// A starter named whole, as the PC holds it: its place and checkout, and its model and effort unless it starts with
    /// the default model, whatever that is when it's used; a kept one by its own id too, which picks it out from another
    /// kept the same way and holds through changes to what it starts with.
    /// </summary>
    private static RemoteCommand StarterCommand(string action, ChatStartSetup setup)
    {
        var command = new RemoteCommand(action)
            .With("projectId", setup.ProjectId?.ToString())
            .With("agentId", setup.AgentId?.ToString())
            .With("worktree", setup.IsWorktree ? "true" : "false")
            .With("id", setup.IsKept ? setup.Id : null);
        return setup.IsDefaultModel ? command : command.With("model", setup.Model).With("effort", setup.Source.Effort);
    }

    // ── The new starter sheet ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the new starter sheet (the "+", the speed dial's New starter, or the dashed chip): the starter's sheet for one
    /// not kept yet, filled from the draft (its place and checkout, and its model unless that is the PC's default), with
    /// no name yet. Nothing is kept until Add, and the draft stays as it is.
    /// </summary>
    [RelayCommand]
    private void OpenNewStarterSheet()
    {
        IsStarterDialOpen = false;
        if (!HasLaunchpad)
            return;

        // A Copilot agent has no id, so the starter starts without it.
        var projectId = ParseId(Chat.ProjectValue);
        var model = Chat.IsPreferredModel(Chat.Model) ? null : Chat.Model;
        _newStarter = new PendingStarter(
            projectId,
            ParseId(Chat.AgentValue),
            projectId is not null && Chat.UseWorktree,
            model,
            model is null ? null : Chat.Quality,
            null);
        _setupSheetFollow = null;
        _focusNameWhenKept = false;
        IsSetupSheetBusy = false;
        IsChoosingStarterPlace = false;
        IsSetupSheetNew = true;
        ShowSetupSheet(NewStarterSetup(), resetName: true);
        IsSetupSheetOpen = true;
    }

    /// <summary>The new starter as a starter shows it, so the sheet draws it as it draws a kept one; its name is the one typed.</summary>
    private ChatStartSetup NewStarterSetup()
    {
        var starter = _newStarter;
        var setup = DescribePlace("new", starter.ProjectId, starter.AgentId, starter.UseWorktree);
        setup.Title = "";
        setup.Icon = starter.Icon;
        setup.Model = starter.Model;
        setup.Quality = starter.Quality;
        setup.IsDefaultModel = starter.Model is null;
        return new ChatStartSetup(setup);
    }

    /// <summary>Changes what the new starter starts with, in its sheet; nothing is kept until Add.</summary>
    private void ChangeNewStarter(PendingStarter changed)
    {
        _newStarter = changed;
        ShowSetupSheet(NewStarterSetup(), resetName: false);
    }

    /// <summary>Keeps the new starter as the sheet has it, after the user's other starters, under the name typed.</summary>
    [RelayCommand]
    private Task AddStarterAsync()
    {
        if (!IsSetupSheetOpen || !IsSetupSheetNew)
            return Task.CompletedTask;

        var starter = _newStarter;
        var command = new RemoteCommand(RemoteProtocol.Actions.KeepSetup)
            .With("projectId", starter.ProjectId?.ToString())
            .With("agentId", starter.AgentId?.ToString())
            .With("worktree", starter.ProjectId is not null && starter.UseWorktree ? "true" : "false")
            .With("kept", "true")
            .With("name", SetupSheetName.Trim())
            .With("icon", starter.Icon ?? "");
        if (starter.Model is { } model)
            command = command.With("model", model).With("quality", starter.Quality ?? "").With("ownModel", "true");

        CloseSetupSheetWithoutSaving();
        return SendLaunchpadCommandAsync(command);
    }

    /// <summary>Opens the new starter's places, its own checked, in place of its form.</summary>
    [RelayCommand]
    private void ChooseStarterPlace()
    {
        if (!IsSetupSheetNew)
            return;

        PlaceQuery = "";
        FilterPlaces();
        IsChoosingStarterPlace = true;
    }

    /// <summary>Back to the new starter's form, where it starts unchanged.</summary>
    [RelayCommand]
    private void BackToStarterForm() => IsChoosingStarterPlace = false;

    partial void OnPlaceQueryChanged(string value) => FilterPlaces();

    /// <summary>The new starter's places the search allows: Just chat, the projects and the agents, its own checked.</summary>
    private void FilterPlaces()
    {
        var query = PlaceQuery.Trim();
        var justChatTitle = StartSetups.FirstOrDefault(static setup => setup.IsJustChat)?.PlaceTitle;
        var justChat = justChatTitle is { Length: > 0 }
            ? new LaunchpadPlace(new RemoteLaunchpadPlace
            {
                Title = justChatTitle,
                Setup = DescribePlace("just-chat", null, null, false)
            }, isJustChat: true)
            : null;
        LaunchpadPlace[] places = [.. justChat is null ? [] : new[] { justChat }, .. _launchpadPlaces];
        foreach (var place in places)
            place.IsCurrent = place.ProjectId == _newStarter.ProjectId && place.AgentId == _newStarter.AgentId;

        JustChatPlace = justChat is not null && Matches(justChat) ? justChat : null;
        PlaceProjects = _launchpadPlaces.Where(place => place.IsProject && Matches(place)).ToArray();
        PlaceAgents = _launchpadPlaces.Where(place => !place.IsProject && Matches(place)).ToArray();

        bool Matches(LaunchpadPlace place) => query.Length == 0 || place.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>Starts the new starter in another place, with that place's checkout; its own place leaves it as it is.</summary>
    [RelayCommand]
    private void PickPlace(LaunchpadPlace? place)
    {
        if (place is null || !IsSetupSheetNew)
            return;

        IsChoosingStarterPlace = false;
        if (!place.IsCurrent)
        {
            ChangeNewStarter(_newStarter with
            {
                ProjectId = place.ProjectId,
                AgentId = place.AgentId,
                UseWorktree = place.ProjectId is not null && place.Setup.IsWorktree
            });
        }
    }

    // ── A starter's sheet ────────────────────────────────────────────────────────────────────

    /// <summary>A starter's sheet, as the desktop's card: held, or the lit one's ⋯.</summary>
    [RelayCommand]
    private void OpenSetupSheet(ChatStartSetup? setup)
    {
        IsStarterDialOpen = false;
        if (!HasLaunchpad || setup is null || !StartSetups.Contains(setup))
            return;

        _setupSheetFollow = null;
        _focusNameWhenKept = false;
        IsSetupSheetBusy = false;
        IsChoosingStarterPlace = false;
        IsSetupSheetNew = false;
        ShowSetupSheet(setup, resetName: true);
        IsSetupSheetOpen = true;
    }

    /// <summary>
    /// Fills the sheet for its starter: for a kept or a new one, its name, its icons and its models, the current ones
    /// marked.
    /// </summary>
    private void ShowSetupSheet(ChatStartSetup setup, bool resetName)
    {
        // The name field shows the starter's name, unless the user has typed another since it last did.
        if (resetName || SetupSheetName.Trim() == _setupSheetShownName)
            SetupSheetName = setup.Title;
        _setupSheetShownName = setup.Title;
        _setupSheetTarget = setup;
        SetupSheetStarter = setup;
        _showingSetupSheet = true;
        try
        {
            SetupSheetWorktree = setup.IsWorktree;
        }
        finally
        {
            _showingSetupSheet = false;
        }

        if (!setup.IsKept && !IsSetupSheetNew)
        {
            SetupSheetIcons = [];
            SetupSheetModels = [];
            SetupSheetQualities = [];
            return;
        }

        var icon = setup.Badge.IconKey;
        SetupSheetIcons =
        [
            new LaunchpadIconChoice(null, setup.PlaceBadge, icon is null, setup.PlaceTitle),
            .. _starterIcons.Select(key => new LaunchpadIconChoice(key, LaunchpadBadge.Of(null, null, key), key == icon, key))
        ];

        // The models as the composer's picker lists them: Copilot Fusion once, its tiers being its efforts.
        var model = setup.IsDefaultModel ? null : setup.Model;
        SetupSheetModels =
        [
            new LaunchpadModelChoice(null, LaunchpadLabels.DefaultModel, DefaultModelName, model is null),
            .. Chat.PickerModels().Select(row => new LaunchpadModelChoice(row, Chat.PickerModelName(row), "", Chat.IsPickerModel(row, model)))
        ];
        var levels = Chat.QualityLevelsOf(model);
        var quality = Chat.TierQualityOf(model) ?? setup.Source.Quality;
        SetupSheetQualityName = Chat.EffortNameOf(model);
        SetupSheetQualities = levels.Count > 1
            ? levels.Select(level => new LaunchpadQualityChoice(level, string.Equals(level, quality, StringComparison.Ordinal))).ToArray()
            : [];
    }

    /// <summary>
    /// Keeps the open sheet on its starter once the PC sends the row again: the one a change it asked for made, else the
    /// same one, shown afresh. It closes once its starter is gone. A new starter's sheet shows it afresh, as its places and
    /// the models may have changed.
    /// </summary>
    private void RetargetSetupSheet()
    {
        if (!IsSetupSheetOpen || _setupSheetTarget is not { } target)
            return;

        if (IsSetupSheetNew)
        {
            ShowSetupSheet(NewStarterSetup(), resetName: false);
            return;
        }

        ChatStartSetup? next = null;
        if (_setupSheetFollow is { } follow)
        {
            // A name survives a change to what the starter starts with, so it tells two candidates apart.
            next = StartSetups.Where(follow).OrderBy(setup => setup.Title == _setupSheetShownName ? 0 : 1).FirstOrDefault();
            if (next is not null)
            {
                _setupSheetFollow = null;
                IsSetupSheetBusy = false;
            }
        }

        next ??= StartSetups.FirstOrDefault(setup => setup.Id == target.Id);
        if (next is null)
        {
            CloseSetupSheetWithoutSaving();
            return;
        }

        if (!ReferenceEquals(next, target))
            ShowSetupSheet(next, resetName: false);
        if (_focusNameWhenKept && next.IsKept && !IsSetupSheetBusy)
        {
            _focusNameWhenKept = false;
            StarterNameFocusRequested?.Invoke();
        }
    }

    /// <summary>Keeps a learned starter on the new chat; the sheet then shows it kept.</summary>
    [RelayCommand]
    private void KeepStarter() => KeepSetupSheetStarter(focusName: false);

    /// <summary>Keeps a learned starter and gives its name field focus.</summary>
    [RelayCommand]
    private void RenameStarter() => KeepSetupSheetStarter(focusName: true);

    private void KeepSetupSheetStarter(bool focusName)
    {
        if (_setupSheetTarget is not { IsKept: false } target || IsSetupSheetBusy)
            return;

        _focusNameWhenKept = focusName;
        SendSetupSheetCommand(
            StarterCommand(RemoteProtocol.Actions.KeepSetup, target).With("kept", "true"),
            setup => setup.IsKept && IsSameRecipe(setup, target));
    }

    /// <summary>Stops suggesting a learned starter's place.</summary>
    [RelayCommand]
    private Task HideStarterAsync()
    {
        if (_setupSheetTarget is not { CanHide: true } target)
            return Task.CompletedTask;

        CloseSetupSheetWithoutSaving();
        return SendLaunchpadCommandAsync(StarterCommand(RemoteProtocol.Actions.HideSetup, target));
    }

    /// <summary>Lets a kept starter go; its place comes back as a learned one if the user still starts there.</summary>
    [RelayCommand]
    private Task RemoveStarterAsync()
    {
        if (_setupSheetTarget is not { IsKept: true } target)
            return Task.CompletedTask;

        CloseSetupSheetWithoutSaving();
        return SendLaunchpadCommandAsync(StarterCommand(RemoteProtocol.Actions.KeepSetup, target).With("kept", "false"));
    }

    /// <summary>Gives a kept or new starter an icon, or its place's badge back.</summary>
    [RelayCommand]
    private void PickStarterIcon(LaunchpadIconChoice? choice)
    {
        if (choice is null || choice.IsCurrent)
            return;

        if (IsSetupSheetNew)
        {
            ChangeNewStarter(_newStarter with { Icon = choice.Key });
            return;
        }

        if (_setupSheetTarget is not { IsKept: true }
            || !RetuneSetupSheetStarter(command => command.With("toIcon", choice.Key ?? ""), follow: null))
        {
            return;
        }

        SetupSheetIcons = SetupSheetIcons
            .Select(icon => new LaunchpadIconChoice(icon.Key, icon.Badge, ReferenceEquals(icon, choice), icon.AccessibleName))
            .ToArray();
    }

    /// <summary>
    /// Starts a kept or new starter with another model from now on, or with the default model, whatever that is when it's
    /// used. A new one takes the effort a new chat on that model starts at.
    /// </summary>
    [RelayCommand]
    private void PickStarterModel(LaunchpadModelChoice? choice)
    {
        if (choice is null || choice.IsCurrent)
            return;

        var model = choice.ModelId;
        if (IsSetupSheetNew)
        {
            ChangeNewStarter(_newStarter with { Model = model, Quality = Chat.DefaultQualityOf(model) });
            return;
        }

        // The sheet waits for the PC's row to show its starter (by its id, which the change keeps) on that model.
        if (_setupSheetTarget is not { IsKept: true } target
            || !RetuneSetupSheetStarter(
                command => model is null ? command.With("toDefaultModel", "true") : command.With("toModel", model),
                setup => setup.Id == target.Id
                         && (model is null ? setup.IsDefaultModel : !setup.IsDefaultModel && Chat.IsPickerModel(model, setup.Model))))
        {
            return;
        }

        SetupSheetModels = SetupSheetModels
            .Select(option => new LaunchpadModelChoice(option.ModelId, option.Title, option.Hint, ReferenceEquals(option, choice)))
            .ToArray();
        var levels = Chat.QualityLevelsOf(model);
        SetupSheetQualityName = Chat.EffortNameOf(model);
        SetupSheetQualities = levels.Count > 1
            ? levels.Select(level => new LaunchpadQualityChoice(level, false)).ToArray()
            : [];
    }

    /// <summary>Works a kept or new starter's own model at another effort from now on (a Copilot Fusion tier among them).</summary>
    [RelayCommand]
    private void PickStarterQuality(LaunchpadQualityChoice? choice)
    {
        if (choice is null || choice.IsCurrent
            || SetupSheetModels.FirstOrDefault(static option => option.IsCurrent)?.ModelId is not { } model)
        {
            return;
        }

        if (IsSetupSheetNew)
        {
            ChangeNewStarter(_newStarter with { Model = Chat.ModelAtQuality(model, choice.Title), Quality = choice.Title });
            return;
        }

        if (_setupSheetTarget is not { IsKept: true } target
            || !RetuneSetupSheetStarter(
                command => command.With("toModel", model).With("toQuality", choice.Title),
                setup => setup.Id == target.Id && !setup.IsDefaultModel
                         && string.Equals(Chat.TierQualityOf(setup.Model) ?? setup.Source.Quality, choice.Title, StringComparison.Ordinal)))
        {
            return;
        }

        SetupSheetQualities = SetupSheetQualities
            .Select(option => new LaunchpadQualityChoice(option.Title, ReferenceEquals(option, choice)))
            .ToArray();
    }

    /// <summary>The worktree switch starts a kept or new starter in a worktree, or in the project's folder, from now on.</summary>
    partial void OnSetupSheetWorktreeChanged(bool value)
    {
        if (_showingSetupSheet || _setupSheetTarget is not { CanUseWorktree: true } target || value == target.IsWorktree)
            return;

        if (IsSetupSheetNew)
        {
            ChangeNewStarter(_newStarter with { UseWorktree = value });
            return;
        }

        if (!target.IsKept)
            return;

        if (!RetuneSetupSheetStarter(
                command => command.With("toWorktree", value ? "true" : "false"),
                setup => setup.Id == target.Id && setup.IsWorktree == value))
        {
            ShowSetupSheet(target, resetName: false);
        }
    }

    /// <summary>Keeps the name typed in the sheet, if it changed: on Done, and as the sheet closes.</summary>
    [RelayCommand]
    private void CommitStarterName()
    {
        if (_setupSheetTarget is not { } target || IsSetupSheetBusy || TypedName(target) is not { } name)
            return;

        _setupSheetShownName = name;
        SendSetupSheetCommand(StarterCommand(RemoteProtocol.Actions.RetuneSetup, target).With("toName", name), follow: null);
    }

    /// <summary>The name typed for a kept starter, trimmed, when it isn't the one the sheet last showed or sent.</summary>
    private string? TypedName(ChatStartSetup target)
    {
        var name = SetupSheetName.Trim();
        return target.IsKept && !string.Equals(name, _setupSheetShownName, StringComparison.Ordinal) ? name : null;
    }

    /// <summary>
    /// Asks the PC to change the sheet's starter from now on, with the name typed in the sheet if it changed. A change to
    /// what it starts with names it anew on the PC, so the sheet takes no other until the PC sends the row back and the
    /// sheet finds the starter again (<paramref name="follow"/>). False when the sheet can't ask now.
    /// </summary>
    private bool RetuneSetupSheetStarter(Func<RemoteCommand, RemoteCommand> change, Func<ChatStartSetup, bool>? follow)
    {
        if (_setupSheetTarget is not { } target || IsSetupSheetBusy)
            return false;

        var command = change(StarterCommand(RemoteProtocol.Actions.RetuneSetup, target));
        if (TypedName(target) is { } name)
        {
            command = command.With("toName", name);
            _setupSheetShownName = name;
        }

        SendSetupSheetCommand(command, follow);
        return true;
    }

    private void SendSetupSheetCommand(RemoteCommand command, Func<ChatStartSetup, bool>? follow)
    {
        if (follow is not null)
        {
            _setupSheetFollow = follow;
            IsSetupSheetBusy = true;
        }

        _ = SendAsync();

        async Task SendAsync()
        {
            if (await SendLaunchpadCommandAsync(command))
                return;

            // The PC refused: the sheet shows its starter as it still is.
            if (follow is not null && ReferenceEquals(_setupSheetFollow, follow))
            {
                _setupSheetFollow = null;
                _focusNameWhenKept = false;
                IsSetupSheetBusy = false;
            }

            if (IsSetupSheetOpen && _setupSheetTarget is { } target)
                ShowSetupSheet(target, resetName: false);
        }
    }

    private static bool IsSamePlace(ChatStartSetup setup, ChatStartSetup target, bool worktree) =>
        setup.ProjectId == target.ProjectId && setup.AgentId == target.AgentId && setup.IsWorktree == worktree;

    private static bool IsSameModel(ChatStartSetup setup, ChatStartSetup target) =>
        setup.IsDefaultModel == target.IsDefaultModel
        && (target.IsDefaultModel || (setup.Model == target.Model && setup.Source.Effort == target.Source.Effort));

    private static bool IsSameRecipe(ChatStartSetup setup, ChatStartSetup target) =>
        IsSamePlace(setup, target, target.IsWorktree) && IsSameModel(setup, target);

    private void CloseSetupSheetWithoutSaving()
    {
        _setupSheetTarget = null;
        IsSetupSheetOpen = false;
    }

    partial void OnIsSetupSheetOpenChanged(bool value)
    {
        if (value)
            return;

        CommitStarterName();
        _setupSheetTarget = null;
        _setupSheetFollow = null;
        _focusNameWhenKept = false;
        IsSetupSheetBusy = false;
        IsChoosingStarterPlace = false;
        IsSetupSheetNew = false;
    }

    // ── Needs you ────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenNeedsYou()
    {
        if (HasNeedsYou)
            IsNeedsYouSheetOpen = true;
    }

    [RelayCommand]
    private void OpenNeedsYouChat(LaunchpadChat? chat)
    {
        IsNeedsYouSheetOpen = false;
        if (chat is not null && IsPaired)
            OnChatActivated(chat.ChatId, chat.Title, null);
    }

    private void CloseLaunchpadSheets()
    {
        IsStarterDialOpen = false;
        IsSetupSheetOpen = false;
        IsNeedsYouSheetOpen = false;
    }

    /// <summary>
    /// Closes the topmost launchpad sheet, or the speed dial, for Back; true when one was open. The new starter's places
    /// go back to its form first.
    /// </summary>
    private bool DismissLaunchpadSheet()
    {
        if (!IsLaunchpadSurfaceOpen)
            return false;

        if (IsSetupSheetOpen && IsChoosingStarterPlace)
        {
            IsChoosingStarterPlace = false;
            return true;
        }

        CloseLaunchpadSheets();
        return true;
    }

    /// <summary>Asks the PC; its next snapshot brings the row as it now is. True when the PC did what was asked.</summary>
    private async Task<bool> SendLaunchpadCommandAsync(RemoteCommand command, bool reportFailure = true)
    {
        // A failure is said in the chat that asked, on the PC that answered, not in one opened meanwhile.
        var connection = Volatile.Read(ref _connectionGeneration);
        var surface = _transcriptSurfaceGeneration;
        var chatId = Chat.ChatId;
        RemoteCommandResult result;
        try
        {
            result = await SendCommandAsync(command);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Mobile] Launchpad command '{command.Action}' failed: {ex}");
            result = new RemoteCommandResult { Error = Strings.Get("PcNoAnswer") };
        }

        if (!result.Ok && reportFailure
            && connection == Volatile.Read(ref _connectionGeneration)
            && surface == _transcriptSurfaceGeneration
            && Chat.ChatId == chatId)
        {
            Chat.ErrorText = result.Error ?? Strings.Get("PcNoAnswer");
        }

        return result.Ok;
    }
}
