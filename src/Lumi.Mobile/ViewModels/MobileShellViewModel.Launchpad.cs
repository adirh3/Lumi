using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Remote.Protocol;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.ViewModels;

/// <summary>What a new chat starts with: one of the user's usual setups, Just chat, or a place picked from Other.</summary>
public sealed partial class ChatStartSetup(RemoteChatSetup setup) : ObservableObject
{
    // The desktop's tile: the project's initial on a tint of its hue (the hue the chat's mark will
    // wear once it starts), otherwise the agent's glyph or a sparkle on a quiet tile.
    private readonly ChatIdentityMark? _projectMark = setup.ProjectId is { } projectId
        ? new ChatIdentityMark(setup.Glyph, ChatIdentityMark.HueFor(projectId))
        : null;

    internal RemoteChatSetup Source { get; } = setup;
    public string Id => Source.Id;
    public Guid? ProjectId => Source.ProjectId;
    public Guid? AgentId => Source.AgentId;
    public string? Model => Source.Model;
    public string Title => Source.Title;
    public string Description => Source.Description;
    public string MarkGlyph => string.IsNullOrWhiteSpace(Source.Glyph) ? "✦" : Source.Glyph;
    public bool HasProjectMark => _projectMark is not null;
    public IBrush? MarkBackground => _projectMark?.Background;
    public IBrush? MarkForeground => _projectMark?.Foreground;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Source.Description);

    /// <summary>What its capsule says beside its name: the agent working in the project, or the model.</summary>
    public string Detail => Source.Detail ?? "";

    /// <summary>Another setup in the row has its name, so it always says what tells it apart.</summary>
    public bool IsVariant => Source.IsVariant;

    /// <summary>The user kept it on new chats, rather than Lumi suggesting it from use.</summary>
    public bool IsKept => Source.IsKept;

    public bool IsWorktree => Source.UseWorktree;
    public bool CanUseWorktree => Source.CanUseWorktree;

    /// <summary>As on the desktop, a setup says what it starts with while it's the chat's, or always when it's a variant.</summary>
    public bool ShowsDetail => Detail.Length > 0 && (IsSelected || IsVariant);

    /// <summary>Its name as a screen reader says it: a variant's with what tells it apart from the others.</summary>
    public string AccessibleName => IsVariant && Detail.Length > 0 ? $"{Title} · {Detail}" : Title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsDetail))]
    private bool _isSelected;

    /// <summary>It is the chat's setup on a PC with the launchpad, so its ⋯ opens its sheet.</summary>
    [ObservableProperty] private bool _showsMore;

    /// <summary>Whether the PC would show this setup the same way, so a refreshed row can keep this one.</summary>
    internal bool LooksLike(RemoteChatSetup other) =>
        Source.Id == other.Id
        && Source.Title == other.Title
        && Source.Description == other.Description
        && Source.Glyph == other.Glyph
        && Source.Detail == other.Detail
        && Source.IsVariant == other.IsVariant
        && Source.IsKept == other.IsKept
        && Source.CanUseWorktree == other.CanUseWorktree
        && Source.Effort == other.Effort;
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

/// <summary>A project or agent in Other's sheet, and how a new chat there starts: the way the user last worked there.</summary>
public sealed class LaunchpadPlace(RemoteLaunchpadPlace place)
{
    internal RemoteLaunchpadPlace Source { get; } = place;
    public Guid Id => Source.Id;
    public bool IsProject => Source.IsProject;
    public string Title => Source.Title;

    /// <summary>When the user last worked there ("40m ago").</summary>
    public string Hint => Source.Hint ?? "";
    public bool HasHint => !string.IsNullOrEmpty(Source.Hint);

    /// <summary>One of the user's kept setups is in this place.</summary>
    public bool IsKept => Source.IsKept;

    /// <summary>Its mark, and what picking it starts with.</summary>
    public ChatStartSetup Setup { get; } = new(place.Setup);
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

/// <summary>A model a setup can start with, in its sheet.</summary>
public sealed class LaunchpadModelChoice(string modelId, string title, bool isCurrent)
{
    public string ModelId { get; } = modelId;
    public string Title { get; } = title;
    public bool IsCurrent { get; } = isCurrent;
}

/// <summary>
/// The new chat's launchpad, as the PC projects it (<see cref="RemoteNewChatExperience"/>): the greeting and the line
/// under it, For you and a tab per kind of task with their starters, and the setups row on the composer (Just chat, the
/// user's setups, Other and Keep). A setup's sheet keeps it, changes what it starts with, or stops it being suggested;
/// a PC from before the launchpad still gets its three setups and starters.
/// </summary>
public sealed partial class MobileShellViewModel
{
    internal const string ForYouTabId = "for-you";

    private RemoteNewChatExperience? _newChatExperience;
    private IReadOnlyList<LaunchpadPlace> _launchpadPlaces = [];
    private string? _desktopGreeting;

    /// <summary>The chat list's project when a setup was first applied to the draft.</summary>
    private Guid? _activeProjectBeforeSetup;

    /// <summary>Asks the view to begin a starter's fill-in on the composer: its blanks, in order.</summary>
    internal event Action<IReadOnlyList<string>>? StarterApplied;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartSetups))]
    [NotifyPropertyChangedFor(nameof(ShowsSetupRow))]
    private IReadOnlyList<ChatStartSetup> _startSetups = [];

    public bool HasStartSetups => StartSetups.Count > 0;

    /// <summary>The setups row: the launchpad's (Just chat and Other at least), or an older PC's setups.</summary>
    public bool ShowsSetupRow => HasLaunchpad || HasStartSetups;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Greeting))]
    [NotifyPropertyChangedFor(nameof(GreetingParts))]
    private bool _hasDesktopNewChat;

    /// <summary>The line under the greeting: the project brief's opening sentence, or the invitation.</summary>
    [ObservableProperty] private string _newChatBrief = "";
    [ObservableProperty] private string _startSetupsTitle = "";

    /// <summary>The PC projects the launchpad: its tabs, Just chat, Other and the setups' sheet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLaunchpadTabs))]
    [NotifyPropertyChangedFor(nameof(ShowsSetupRow))]
    private bool _hasLaunchpad;

    [ObservableProperty] private RemoteLaunchpadLabels _launchpadLabels = new();

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

    /// <summary>Just chat: no project, no agent, the user's default model.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasJustChat))]
    private ChatStartSetup? _justChatSetup;

    public bool HasJustChat => JustChatSetup is not null;

    /// <summary>The model a plain chat starts with: the draft's while Just chat is the chat's setup.</summary>
    [ObservableProperty] private string _justChatDetail = "";

    [ObservableProperty] private bool _isJustChatActive;

    /// <summary>The draft is a setup the row doesn't hold, so Other shows it and offers to keep it.</summary>
    [ObservableProperty] private bool _isOtherActive;

    /// <summary>
    /// Which capsule is the chat's: <see cref="JustChatKey"/>, <see cref="OtherKey"/>, a setup's id, or null; the view
    /// glides the row to it.
    /// </summary>
    [ObservableProperty] private string? _activeSetupKey;

    internal const string JustChatKey = "just-chat";
    internal const string OtherKey = "other";
    [ObservableProperty] private string _otherTitle = "";
    [ObservableProperty] private string _otherDetail = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOtherMark))]
    private ChatIdentityMark _otherMark = ChatIdentityMark.Empty;

    public bool HasOtherMark => !ReferenceEquals(OtherMark, ChatIdentityMark.Empty);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(KeepDraftCommand))]
    private bool _canKeepDraft;

    public bool HasOtherPlaces => _launchpadPlaces.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanDragDrawer))]
    private bool _isOtherSheetOpen;

    [ObservableProperty] private string _otherQuery = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOtherProjects))]
    [NotifyPropertyChangedFor(nameof(HasNoOtherMatch))]
    private IReadOnlyList<LaunchpadPlace> _otherProjects = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOtherAgents))]
    [NotifyPropertyChangedFor(nameof(HasNoOtherMatch))]
    private IReadOnlyList<LaunchpadPlace> _otherAgents = [];

    public bool HasOtherProjects => OtherProjects.Count > 0;
    public bool HasOtherAgents => OtherAgents.Count > 0;
    public bool HasNoOtherMatch => OtherProjects.Count == 0 && OtherAgents.Count == 0;

    /// <summary>The setup whose sheet is open: keep it, change its checkout or model, or stop it being suggested.</summary>
    private ChatStartSetup? _setupSheetTarget;

    [ObservableProperty] private string _setupSheetTitle = "";
    [ObservableProperty] private string _setupSheetDescription = "";
    [ObservableProperty] private bool _setupSheetIsKept;
    [ObservableProperty] private bool _setupSheetIsWorktree;
    [ObservableProperty] private bool _setupSheetCanUseWorktree;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanDragDrawer))]
    private bool _isSetupSheetOpen;

    [ObservableProperty] private IReadOnlyList<LaunchpadModelChoice> _setupSheetModels = [];

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

    private bool IsLaunchpadSheetOpen => IsOtherSheetOpen || IsSetupSheetOpen || IsNeedsYouSheetOpen;

    internal void ApplyNewChatExperience(RemoteNewChatExperience? experience)
    {
        _newChatExperience = experience;
        _desktopGreeting = experience?.Greeting;
        HasDesktopNewChat = experience is not null;
        HasLaunchpad = experience is { Tabs.Count: > 0 };
        StartSetupsTitle = experience?.SetupsTitle ?? Strings.Get("UsualSetups");
        LaunchpadLabels = experience?.Labels ?? new RemoteLaunchpadLabels();

        // The launchpad's whole row, which scrolls; an older PC's three.
        List<RemoteChatSetup> setups = experience is null ? [] : HasLaunchpad ? experience.Setups : experience.Setups.Take(3).ToList();
        if (StartSetups.Count != setups.Count || StartSetups.Where((setup, index) => !setup.LooksLike(setups[index])).Any())
            StartSetups = setups.Select(setup => new ChatStartSetup(setup)).ToArray();
        if (experience?.JustChat is not { } justChat)
            JustChatSetup = null;
        else if (JustChatSetup is null || !JustChatSetup.LooksLike(justChat) || JustChatSetup.Source.Model != justChat.Model)
            JustChatSetup = new ChatStartSetup(justChat);

        _launchpadPlaces = experience?.Places.Select(place => new LaunchpadPlace(place)).ToArray() ?? [];
        OnPropertyChanged(nameof(HasOtherPlaces));
        FilterOtherPlaces();

        NeedsYouLabel = experience?.NeedsYou ?? "";
        NeedsYouChats = experience?.NeedsYouChats.Select(chat => new LaunchpadChat(chat)).ToArray() ?? [];
        if (!HasNeedsYou)
            IsNeedsYouSheetOpen = false;
        if (!HasLaunchpad)
        {
            IsOtherSheetOpen = false;
            IsSetupSheetOpen = false;
        }
        else if (_setupSheetTarget is { } target && !StartSetups.Any(setup => setup.Id == target.Id))
        {
            IsSetupSheetOpen = false;
        }

        Chat.ApplyNewChatStarters(experience?.Starters);
        RefreshLaunchpadTabs();
        RefreshLaunchpadScope();
        RefreshSetupSelection();
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

    private void RefreshSetupSelection()
    {
        var projectId = Guid.TryParse(Chat.ProjectValue, out var parsedProject) ? parsedProject : (Guid?)null;
        var agentId = Guid.TryParse(Chat.AgentValue, out var parsedAgent) ? parsedAgent : (Guid?)null;
        var isDraft = !Chat.HasChat;

        // One setup is the chat's: the one it is exactly, else one that leaves the model or effort to the chat.
        var active = !isDraft ? null
            : StartSetups.FirstOrDefault(setup => Matches(setup.Source, exactly: true))
              ?? StartSetups.FirstOrDefault(setup => Matches(setup.Source, exactly: false));
        foreach (var setup in StartSetups)
        {
            setup.IsSelected = ReferenceEquals(setup, active);
            setup.ShowsMore = HasLaunchpad && setup.IsSelected;
        }

        // Just chat is the plain draft no setup claims: a setup on another model, without project or agent, is that setup.
        IsJustChatActive = HasJustChat && isDraft && active is null && projectId is null && agentId is null;
        if (JustChatSetup is { } justChat)
        {
            justChat.IsSelected = IsJustChatActive;
            JustChatDetail = IsJustChatActive ? Chat.ModelDisplayName : justChat.Detail;
        }

        IsOtherActive = HasLaunchpad && isDraft && !IsJustChatActive && active is null;
        OtherTitle = IsOtherActive
            ? (!string.IsNullOrWhiteSpace(Chat.ProjectName) ? Chat.ProjectName! : Chat.AgentName ?? LaunchpadLabels.Other)
            : LaunchpadLabels.Other;
        OtherDetail = IsOtherActive ? Chat.ModelDisplayName : "";
        OtherMark = IsOtherActive
            ? ChatIdentityMark.Create(projectId, Chat.ProjectName, Chat.AgentName, Chat.AgentGlyph) ?? ChatIdentityMark.Empty
            : ChatIdentityMark.Empty;
        CanKeepDraft = IsOtherActive && !_keepingDraft && (projectId is not null || agentId is not null);
        ActiveSetupKey = active?.Id ?? (IsJustChatActive ? JustChatKey : IsOtherActive ? OtherKey : null);

        bool Matches(RemoteChatSetup source, bool exactly) =>
            source.ProjectId == projectId
            && source.AgentId == agentId
            && source.UseWorktree == Chat.UseWorktree
            && (source.Model is null && !exactly || source.Model == Chat.Model)
            && (source.Quality is null && !exactly || source.Quality == Chat.Quality);
    }

    private bool CanUseStartSetup() => !Chat.HasChat && !Chat.IsBusy && !Chat.IsLoading && Chat.CanChangeProjectSelection;

    /// <summary>
    /// Applies a setup to the draft (its project, agent, checkout, model and effort; the prompt and attachments stay);
    /// tapping the applied one again puts back the draft it replaced, as on the desktop.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseStartSetup))]
    private void UseStartSetup(ChatStartSetup? setup)
    {
        if (!CanUseStartSetup())
        {
            Chat.ErrorText = Strings.Get("SetupsBeforeFirstMessage");
            return;
        }
        if (setup is null || !(StartSetups.Contains(setup) || ReferenceEquals(setup, JustChatSetup)))
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

    // ── Other ────────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenOtherSheet()
    {
        if (!HasLaunchpad || !CanUseStartSetup())
            return;

        OtherQuery = "";
        FilterOtherPlaces();
        IsOtherSheetOpen = true;
    }

    partial void OnOtherQueryChanged(string value) => FilterOtherPlaces();

    private void FilterOtherPlaces()
    {
        var query = OtherQuery.Trim();
        bool Matches(LaunchpadPlace place) => query.Length == 0 || place.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        OtherProjects = _launchpadPlaces.Where(place => place.IsProject && Matches(place)).ToArray();
        OtherAgents = _launchpadPlaces.Where(place => !place.IsProject && Matches(place)).ToArray();
    }

    /// <summary>Starts the new chat in a project or with an agent the way the user last worked there.</summary>
    [RelayCommand]
    private void PickPlace(LaunchpadPlace? place)
    {
        IsOtherSheetOpen = false;
        if (place is null || !CanUseStartSetup())
            return;

        ApplySetup(place.Setup);
    }

    private bool _keepingDraft;

    /// <summary>Keeps the draft's setup, exactly as the composer has it, as the first of the user's setups.</summary>
    [RelayCommand(CanExecute = nameof(CanKeepDraft))]
    private async Task KeepDraftAsync()
    {
        _keepingDraft = true;
        CanKeepDraft = false;
        try
        {
            await SendLaunchpadCommandAsync(new RemoteCommand(RemoteProtocol.Actions.KeepSetup)
                .With("projectId", Chat.ProjectValue)
                .With("agentId", Chat.AgentValue)
                .With("worktree", Chat.UseWorktree ? "true" : "false")
                .With("model", Chat.Model)
                .With("quality", Chat.Quality)
                .With("kept", "true"));
        }
        finally
        {
            _keepingDraft = false;
            RefreshSetupSelection();
        }
    }

    // ── A setup's sheet ──────────────────────────────────────────────────────────────────────

    /// <summary>A setup's menu, as the desktop's ⋯ opens it: held, or from the chat's setup.</summary>
    [RelayCommand]
    private void OpenSetupSheet(ChatStartSetup? setup)
    {
        if (!HasLaunchpad || setup is null || !StartSetups.Contains(setup))
            return;

        _setupSheetTarget = setup;
        SetupSheetTitle = setup.Title;
        SetupSheetDescription = setup.Description;
        SetupSheetIsKept = setup.IsKept;
        SetupSheetIsWorktree = setup.IsWorktree;
        SetupSheetCanUseWorktree = setup.CanUseWorktree;
        SetupSheetModels = Chat.AvailableModels
            .Select(model => new LaunchpadModelChoice(model, Chat.DisplayModelName(model) ?? model, model == setup.Model))
            .ToArray();
        IsSetupSheetOpen = true;
    }

    [RelayCommand]
    private Task ToggleSetupKeptAsync() => SetupSheetCommandAsync(setup =>
        SetupCommand(RemoteProtocol.Actions.KeepSetup, setup).With("kept", setup.IsKept ? "false" : "true"));

    [RelayCommand]
    private Task ToggleSetupWorktreeAsync() => SetupSheetCommandAsync(setup =>
        SetupCommand(RemoteProtocol.Actions.RetuneSetup, setup).With("toWorktree", setup.IsWorktree ? "false" : "true"));

    [RelayCommand]
    private Task PickSetupModelAsync(LaunchpadModelChoice? choice) => choice is null || choice.IsCurrent
        ? Task.CompletedTask
        : SetupSheetCommandAsync(setup => SetupCommand(RemoteProtocol.Actions.RetuneSetup, setup).With("toModel", choice.ModelId));

    [RelayCommand]
    private Task HideSetupAsync() => SetupSheetCommandAsync(setup => SetupCommand(RemoteProtocol.Actions.HideSetup, setup));

    /// <summary>Closes the sheet and asks the PC; its next new chat brings the row as it now is.</summary>
    private Task SetupSheetCommandAsync(Func<ChatStartSetup, RemoteCommand> build)
    {
        IsSetupSheetOpen = false;
        return _setupSheetTarget is { } setup ? SendLaunchpadCommandAsync(build(setup)) : Task.CompletedTask;
    }

    /// <summary>A setup named whole, as the row holds it.</summary>
    private static RemoteCommand SetupCommand(string action, ChatStartSetup setup) => new RemoteCommand(action)
        .With("projectId", setup.ProjectId?.ToString())
        .With("agentId", setup.AgentId?.ToString())
        .With("worktree", setup.IsWorktree ? "true" : "false")
        .With("model", setup.Model)
        .With("effort", setup.Source.Effort);

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
        IsOtherSheetOpen = false;
        IsSetupSheetOpen = false;
        IsNeedsYouSheetOpen = false;
    }

    /// <summary>Closes the topmost launchpad sheet for Back; true when one was open.</summary>
    private bool DismissLaunchpadSheet()
    {
        if (!IsLaunchpadSheetOpen)
            return false;

        CloseLaunchpadSheets();
        return true;
    }

    private async Task SendLaunchpadCommandAsync(RemoteCommand command, bool reportFailure = true)
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
    }
}
