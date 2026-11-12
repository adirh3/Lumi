using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.ViewModels;

/// <summary>What a chat is doing that the launchpad cares about, most urgent first.</summary>
public enum LaunchpadChatState
{
    Waiting,
    Reply,
    Working,
    Background,
}

internal enum LaunchpadDayPart
{
    Morning,
    Afternoon,
    Evening,
    Night,
}

/// <summary>
/// How a new chat starts: where (project and local/worktree checkout), as whom (Lumi agent) and with which model and
/// reasoning effort. A starter's null model is the user's default model, whatever that is when it's used, and its null
/// effort the model's default; a draft's are what the composer has.
/// </summary>
internal sealed record LaunchpadSetupSpec(
    Guid? ProjectId,
    Guid? AgentId,
    bool UseWorktree,
    string? ModelId,
    string? Effort)
{
    /// <summary>The place: where and as whom it starts, ignoring the model, which is what Lumi learns starters by.</summary>
    internal (Guid? ProjectId, Guid? AgentId, bool UseWorktree) Context => (ProjectId, AgentId, UseWorktree);

    /// <summary>A plain chat: no project, no agent.</summary>
    internal bool IsJustChat => ProjectId is null && AgentId is null;

    /// <summary>Just chat on the default model, the starter that is always there.</summary>
    internal static LaunchpadSetupSpec JustChat { get; } = new(null, null, false, null, null);

    /// <summary>The place alone, on the default model: the starter Lumi learns from where the user starts chats.</summary>
    internal LaunchpadSetupSpec Place => this with { ModelId = null, Effort = null };
}

/// <summary>The personal greeting, split around the user's name so the name can carry its own emphasis.</summary>
internal readonly record struct LaunchpadGreeting(string Lead, string Name, string Trail);

/// <summary>A conversation starter. Its label may hold blanks ("[where?]") that the user fills in before sending.</summary>
internal sealed record LaunchpadStarterSpec(string Glyph, string Label, string Prompt)
{
    /// <summary>The starter's stable id, so picking it is remembered; null for one written from a project's brief.</summary>
    public string? Id { get; init; }

    /// <summary>The tab it belongs to (<see cref="LaunchpadStarterCatalog"/>), if any.</summary>
    public string? CategoryId { get; init; }

    /// <summary>Why For you offers it ("From Lumi's brief", "This morning"); null where it needs no reason.</summary>
    public string? Reason { get; init; }
}

/// <summary>A starter on the new chat: what it starts, its name, and whether the user kept it or Lumi learned it.</summary>
/// <param name="Title">Its name: the one the user gave it, else its place's.</param>
/// <param name="PlaceTitle">Its place's name ("Lumi", "Just chat"), which a starter without a name of its own shows.</param>
/// <param name="Meta">Everything it starts with, in words: "Lumi · Worktree · Default model".</param>
internal sealed record LaunchpadSetupChoice(LaunchpadSetupSpec Spec, string Title, string PlaceTitle, string Meta)
{
    /// <summary>The kept starter it shows, which its editor edits; null for one learned from the user's chats.</summary>
    public LaunchpadSetupPin? Pin { get; init; }

    /// <summary>The user kept it: it stays on the new chat until they remove it.</summary>
    public bool IsPinned => Pin is not null;

    /// <summary>How many of the user's recent chats started in its place (<see cref="LaunchpadPlanner.SetupWindowDays"/>); 0 for a kept one.</summary>
    public int Uses { get; init; }

    /// <summary>The icon the user gave it (<see cref="LaunchpadPlanner.StarterIcons"/>); null shows its place's badge.</summary>
    public string? Icon => Pin?.Icon;

    /// <summary>It was kept on a model this computer no longer offers, so it starts with the default model instead.</summary>
    public string? RetiredModelId { get; init; }
}

/// <summary>A project or agent of the new starter's places, when the user last worked there, and how a chat there started then.</summary>
internal sealed record LaunchpadPlace(Guid Id, bool IsProject, string Title, string Hint, LaunchpadSetupSpec Setup)
{
    /// <summary>Where a new starter there starts: the place alone, on the default model; a project in its last chat's checkout.</summary>
    internal LaunchpadSetupSpec StarterSetup => IsProject
        ? new LaunchpadSetupSpec(Id, null, Setup.UseWorktree, null, null)
        : new LaunchpadSetupSpec(null, Id, false, null, null);
}

/// <summary>
/// The launchpad's decisions, kept free of UI and store state so they are cheap and testable:
/// which chats need the user, which setups the user reaches for, and which starters fit the moment.
/// </summary>
internal static partial class LaunchpadPlanner
{
    internal const int MaxNeedsYouItems = 6;

    /// <summary>
    /// The most places Lumi suggests from use, besides Just chat, which is always there. Kept starters never count
    /// against it; what doesn't fit the row gathers into its stack.
    /// </summary>
    internal const int MaxLearnedSetups = 6;

    /// <summary>The most kept starters the new chat shows, in the user's order.</summary>
    internal const int MaxKeptSetups = 16;

    /// <summary>The longest name a starter keeps.</summary>
    internal const int MaxStarterNameLength = 40;

    /// <summary>
    /// The icons a kept starter can wear instead of its place's badge, each a key both ends draw
    /// (<see cref="Lumi.Remote.Protocol.RemoteProtocol.LaunchpadIcons"/>).
    /// </summary>
    internal static readonly IReadOnlyList<string> StarterIcons =
        ["layers", "bug", "lightbulb", "search", "book", "pencil", "beaker", "cube", "target", "chat"];

    /// <summary>
    /// For you holds this many starters (two lines of capsules); a project's brief gives all the plans its curator
    /// keeps (<see cref="MaxBriefStarters"/>), the user's habits at most <see cref="MaxHabitStarters"/>.
    /// </summary>
    internal const int ForYouCount = 6;
    internal const int MaxBriefStarters = Services.Context.ContextCuratorScope.MaxStarters;
    private const int MaxHabitStarters = 2;

    /// <summary>The glyph of a starter written from a project's brief.</summary>
    internal const string BriefStarterGlyph = "🧭";

    /// <summary>The longest brief headline shown under the greeting.</summary>
    internal const int BriefHeadlineMaxLength = 120;

    /// <summary>A place must recur before it is offered as one of the user's starters.</summary>
    private const int MinSetupUses = 2;

    internal const int PreviewMaxLength = 140;

    /// <summary>How far back Lumi looks for where the user starts chats.</summary>
    internal const int SetupWindowDays = 30;

    private static readonly TimeSpan SetupWindow = TimeSpan.FromDays(SetupWindowDays);

    /// <summary>Recent habits outweigh old ones: a chat's vote halves roughly every ten days.</summary>
    private const double SetupDecayDays = 14;

    private static readonly Regex MarkdownLink = new(@"\[([^\]]+)\]\((?:[^)\s]+)\)", RegexOptions.Compiled);
    // Stored previews are one line, so only a heading or quote marker that opens the text is markup: "a > b" is prose.
    private static readonly Regex LeadingMarker = new(@"^\s*(#{1,6}|>)\s+", RegexOptions.Compiled);
    private static readonly Regex Emphasis = new(@"\*\*|`+|(?<![\w*])\*(?=\S)|(?<=\S)\*(?![\w*])", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex VisualFence = new(
        @"```(card|chart|confidence|comparison|mermaid)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static LaunchpadDayPart DayPartOf(int hour) => hour switch
    {
        >= 5 and < 12 => LaunchpadDayPart.Morning,
        >= 12 and < 17 => LaunchpadDayPart.Afternoon,
        >= 17 and < 22 => LaunchpadDayPart.Evening,
        _ => LaunchpadDayPart.Night,
    };

    internal static LaunchpadGreeting BuildGreeting(string? userName, int hour)
    {
        var name = userName?.Trim() ?? "";
        var (withName, withoutName) = DayPartOf(hour) switch
        {
            LaunchpadDayPart.Morning => (Loc.Launchpad_GreetingMorning, Loc.Launchpad_GreetingMorningNoName),
            LaunchpadDayPart.Afternoon => (Loc.Launchpad_GreetingAfternoon, Loc.Launchpad_GreetingAfternoonNoName),
            LaunchpadDayPart.Evening => (Loc.Launchpad_GreetingEvening, Loc.Launchpad_GreetingEveningNoName),
            _ => (Loc.Launchpad_GreetingNight, Loc.Launchpad_GreetingNightNoName),
        };

        if (name.Length == 0)
            return new LaunchpadGreeting(withoutName, "", "");

        const string placeholder = "{0}";
        var index = withName.IndexOf(placeholder, StringComparison.Ordinal);
        return index < 0
            ? new LaunchpadGreeting(withName, "", "")
            : new LaunchpadGreeting(withName[..index], name, withName[(index + placeholder.Length)..]);
    }

    /// <summary>One calm line under the greeting: what needs the user, or that nothing does.</summary>
    internal static string BuildBrief(int waiting, int replies, int working, bool hasHistory)
    {
        var parts = new List<string>(3);
        if (waiting > 0)
            parts.Add(waiting == 1 ? Loc.Launchpad_BriefWaitingOne : Format(Loc.Launchpad_BriefWaitingMany, waiting));
        if (replies > 0)
            parts.Add(replies == 1 ? Loc.Launchpad_BriefRepliesOne : Format(Loc.Launchpad_BriefRepliesMany, replies));
        if (working > 0)
            parts.Add(working == 1 ? Loc.Launchpad_BriefWorkingOne : Format(Loc.Launchpad_BriefWorkingMany, working));

        if (parts.Count > 0)
            return string.Join(" · ", parts);

        return hasHistory ? Loc.Launchpad_BriefQuiet : Loc.Chat_WelcomeSubtitle;
    }

    internal static bool HasContent(Chat chat) => chat.MessageCount > 0 || chat.Messages.Count > 0;

    /// <summary>The live state that makes a chat worth surfacing, or null when it is simply idle.</summary>
    internal static LaunchpadChatState? ClassifyActive(Chat chat)
    {
        if (chat.IsAwaitingInput)
            return LaunchpadChatState.Waiting;
        if (chat.HasUnreadMessages)
            return LaunchpadChatState.Reply;
        if (chat.IsRunning)
            return LaunchpadChatState.Working;
        if (chat.IsSessionActive)
            return LaunchpadChatState.Background;
        return null;
    }

    /// <summary>
    /// The chats waiting on the user, most urgent first: a question to answer, then a reply not read yet. Chats that
    /// are only working are left out: the new chat is about starting something, and the sidebar shows them.
    /// </summary>
    internal static List<(Chat Chat, LaunchpadChatState State)> SelectNeedsYou(IEnumerable<Chat> chats, int limit, out int total)
    {
        var waiting = chats
            .Select(chat => (Chat: chat, State: ClassifyActive(chat)))
            .Where(static entry => entry.State is LaunchpadChatState.Waiting or LaunchpadChatState.Reply)
            .OrderBy(static entry => entry.State!.Value)
            .ThenByDescending(static entry => entry.Chat.UpdatedAt)
            .Select(static entry => (entry.Chat, entry.State!.Value))
            .ToList();
        total = waiting.Count;
        return waiting.Count > limit ? waiting.GetRange(0, limit) : waiting;
    }

    /// <summary>
    /// The places the user keeps starting chats in (project, agent and checkout, never the model), ranked by
    /// recency-weighted use, each with how many recent chats started there. They start with the default model, so a
    /// new default carries them along. Just chat is always among them: where its use ranks it, else last.
    /// </summary>
    internal static List<(LaunchpadSetupSpec Spec, int Uses)> SelectSetups(
        IEnumerable<Chat> chats,
        IReadOnlySet<Guid> automationChatIds,
        DateTimeOffset now,
        Func<Guid, bool> projectExists,
        Func<Guid, bool> agentExists,
        int limit)
    {
        var votes = new Dictionary<LaunchpadSetupSpec, (double Score, int Uses)>();
        foreach (var chat in chats)
        {
            if (automationChatIds.Contains(chat.Id)
                || !HasContent(chat)
                || !string.IsNullOrWhiteSpace(chat.SdkAgentName))
            {
                continue;
            }

            var age = now - chat.CreatedAt;
            if (age < TimeSpan.Zero)
                age = TimeSpan.Zero;
            if (age > SetupWindow)
                continue;

            var spec = ToSetupSpec(chat, projectExists, agentExists);
            var weight = Math.Exp(-age.TotalDays / SetupDecayDays);
            votes[spec] = votes.TryGetValue(spec, out var vote)
                ? (vote.Score + weight, vote.Uses + 1)
                : (weight, 1);
        }

        var result = votes
            .Where(static entry => entry.Value.Uses >= MinSetupUses)
            .OrderByDescending(static entry => entry.Value.Score)
            .ThenByDescending(static entry => entry.Value.Uses)
            .Take(limit)
            .Select(static entry => (entry.Key, entry.Value.Uses))
            .ToList();
        if (!result.Exists(static entry => entry.Key.IsJustChat))
            result.Add((LaunchpadSetupSpec.JustChat, votes.GetValueOrDefault(LaunchpadSetupSpec.JustChat).Uses));
        return result;
    }

    /// <summary>A chat's place in starter terms: the project and agent it worked in (if they still exist) and its checkout.</summary>
    internal static LaunchpadSetupSpec ToSetupSpec(Chat chat, Func<Guid, bool> projectExists, Func<Guid, bool> agentExists)
    {
        var projectId = chat.ProjectId is { } project && projectExists(project) ? project : (Guid?)null;
        var agentId = chat.AgentId is { } agent && agentExists(agent) ? agent : (Guid?)null;
        return new LaunchpadSetupSpec(
            projectId,
            agentId,
            projectId is not null && !string.IsNullOrWhiteSpace(chat.WorktreePath),
            null,
            null);
    }

    /// <summary>Three conversation starters that suit the time of day and whether there is history to draw on.</summary>
    internal static List<LaunchpadStarterSpec> SelectStarters(int hour, bool hasHistory)
    {
        var planDay = TimeOfDayStarter(PlanDayId)!;
        var research = TimeOfDayStarter(ResearchId)!;
        var document = TimeOfDayStarter(DocumentId)!;
        var planTomorrow = TimeOfDayStarter(PlanTomorrowId)!;
        var catchUp = TimeOfDayStarter(CatchUpId)!;
        var focus = TimeOfDayStarter(FocusId)!;
        var recap = TimeOfDayStarter(RecapId)!;

        return DayPartOf(hour) switch
        {
            LaunchpadDayPart.Morning => hasHistory ? [planDay, catchUp, research] : [planDay, research, document],
            LaunchpadDayPart.Afternoon => hasHistory ? [focus, research, document] : [research, document, planDay],
            LaunchpadDayPart.Evening => hasHistory ? [recap, planTomorrow, research] : [planTomorrow, research, document],
            _ => hasHistory ? [recap, planTomorrow, document] : [planTomorrow, research, document],
        };
    }

    private const string PlanDayId = "day.plan";
    private const string ResearchId = "day.research";
    private const string DocumentId = "day.document";
    private const string PlanTomorrowId = "day.tomorrow";
    private const string CatchUpId = "day.catch-up";
    private const string FocusId = "day.focus";
    private const string RecapId = "day.recap";

    /// <summary>A time-of-day starter by id, in the current language.</summary>
    internal static LaunchpadStarterSpec? TimeOfDayStarter(string id) => id switch
    {
        PlanDayId => new("☀️", Loc.Chat_SuggestionA, Loc.Chat_SuggestionA) { Id = id, CategoryId = LaunchpadStarterCatalog.Plan },
        ResearchId => new("🔍", Loc.Chat_SuggestionB, Loc.Chat_SuggestionB) { Id = id, CategoryId = LaunchpadStarterCatalog.Learn },
        DocumentId => new("📄", Loc.Chat_SuggestionC, Loc.Chat_SuggestionC) { Id = id, CategoryId = LaunchpadStarterCatalog.Documents },
        PlanTomorrowId => new("🗓️", Loc.Launchpad_StarterPlanTomorrow, Loc.Launchpad_StarterPlanTomorrowPrompt)
            { Id = id, CategoryId = LaunchpadStarterCatalog.Plan },
        CatchUpId => new("📬", Loc.Launchpad_StarterCatchUp, Loc.Launchpad_StarterCatchUpPrompt) { Id = id },
        FocusId => new("🎯", Loc.Launchpad_StarterFocus, Loc.Launchpad_StarterFocusPrompt) { Id = id },
        RecapId => new("🌙", Loc.Launchpad_StarterRecap, Loc.Launchpad_StarterRecapPrompt) { Id = id },
        _ => null,
    };

    /// <summary>
    /// For you: in a project, the plans its brief turned into starters come first; then the starters the user keeps
    /// picking in this place; then what suits the time of day; then well-tried ones, so the list is always full. In a
    /// coding project (<paramref name="workFirst"/>) the work comes before the hour, which is about the user's day rather
    /// than the code. Never the same starter twice.
    /// </summary>
    internal static List<LaunchpadStarterSpec> SelectForYou(
        IReadOnlyList<string> briefStarters,
        string? briefReason,
        IReadOnlyList<LaunchpadStarterSpec> habits,
        IReadOnlyList<LaunchpadStarterSpec> timeOfDay,
        IReadOnlyList<LaunchpadStarterSpec> fillers,
        bool workFirst = false)
    {
        var result = new List<LaunchpadStarterSpec>(ForYouCount);
        foreach (var text in briefStarters.Where(static text => !string.IsNullOrWhiteSpace(text)).Take(MaxBriefStarters))
            Add(new LaunchpadStarterSpec(BriefStarterGlyph, text.Trim(), text.Trim()) { Reason = briefReason });
        foreach (var habit in habits.Take(MaxHabitStarters))
            Add(habit);
        foreach (var starter in workFirst ? fillers.Concat(timeOfDay) : timeOfDay.Concat(fillers))
            Add(starter);
        return result;

        void Add(LaunchpadStarterSpec starter)
        {
            if (result.Count < ForYouCount
                && !result.Any(existing => (existing.Id is not null && existing.Id == starter.Id)
                                           || string.Equals(existing.Prompt, starter.Prompt, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(starter);
            }
        }
    }

    /// <summary>
    /// Why a time-of-day starter is offered now ("This morning"): only the ones about the day itself, since researching
    /// or writing suits any hour.
    /// </summary>
    internal static string? TimeOfDayReason(string? id, int hour)
    {
        if (id is not (PlanDayId or CatchUpId or FocusId or RecapId or PlanTomorrowId))
            return null;

        return DayPartOf(hour) switch
        {
            LaunchpadDayPart.Morning => Loc.Launchpad_ReasonMorning,
            LaunchpadDayPart.Afternoon => Loc.Launchpad_ReasonAfternoon,
            LaunchpadDayPart.Evening => Loc.Launchpad_ReasonEvening,
            _ => Loc.Launchpad_ReasonNight,
        };
    }

    /// <summary>
    /// The starters the new chat offers: every one the user kept, in their order (two may start the same way, under their
    /// own names); then the places Lumi learned, while they number fewer than <paramref name="limit"/>, leaving out places
    /// the user asked not to see and any a kept starter repeats exactly (a renamed place stands in for it). Just chat is
    /// never left out, short of a kept starter that is exactly it. Each says which kept starter it is (its index in
    /// <paramref name="kept"/>), or -1 for a learned one, and how often its place was used.
    /// </summary>
    internal static List<(LaunchpadSetupSpec Spec, int PinIndex, int Uses)> MergeSetups(
        IReadOnlyList<LaunchpadSetupSpec> kept,
        IReadOnlyList<(LaunchpadSetupSpec Spec, int Uses)> learned,
        IReadOnlyCollection<(Guid? ProjectId, Guid? AgentId, bool UseWorktree)> hidden,
        int limit)
    {
        var result = new List<(LaunchpadSetupSpec Spec, int PinIndex, int Uses)>(kept.Count + limit + 1);
        for (var index = 0; index < kept.Count && result.Count < MaxKeptSetups; index++)
            result.Add((kept[index], index, 0));

        var learnedCount = 0;
        foreach (var (spec, uses) in learned)
        {
            if (result.Exists(entry => entry.Spec == spec))
                continue;
            if (!spec.IsJustChat && (learnedCount >= limit || hidden.Contains(spec.Context)))
                continue;

            result.Add((spec, -1, uses));
            if (!spec.IsJustChat)
                learnedCount++;
        }

        return result;
    }

    /// <summary>
    /// How closely a starter fits a draft: -1 when it starts elsewhere or with another model or effort; otherwise how
    /// much it pins down (a chosen model counts more than a chosen effort), so the most specific starter wins.
    /// </summary>
    internal static int MatchScore(LaunchpadSetupSpec starter, LaunchpadSetupSpec draft)
    {
        if (starter.Context != draft.Context
            || (starter.ModelId is not null && !string.Equals(starter.ModelId, draft.ModelId, StringComparison.Ordinal))
            || (starter.Effort is not null && !string.Equals(starter.Effort, draft.Effort, StringComparison.Ordinal)))
        {
            return -1;
        }

        return (starter.ModelId is null ? 0 : 2) + (starter.Effort is null ? 0 : 1);
    }

    /// <summary>The draft's starter: the most specific one that fits it, the first in the row on a tie; -1 for none.</summary>
    internal static int IndexOfMatch(IReadOnlyList<LaunchpadSetupSpec> row, LaunchpadSetupSpec? draft)
    {
        if (draft is null)
            return -1;

        var best = -1;
        var bestScore = -1;
        for (var index = 0; index < row.Count; index++)
        {
            var score = MatchScore(row[index], draft);
            if (score > bestScore)
            {
                best = index;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>
    /// Which starters the row shows within <paramref name="available"/> pixels; the rest gather into its stack. All of them
    /// when they fit beside <paramref name="endWidth"/> (the new starter button); otherwise, beside the stack, as many as
    /// fit in order. The draft's own (<paramref name="keepIndex"/>) joins them in the place of learned starters from the
    /// row's end, never of kept ones, which hold their places whatever is lit; without such room it stays in the stack,
    /// which then wears its light. A divider (<paramref name="dividerWidth"/>) separates kept starters from learned ones
    /// while both show. <paramref name="fixedWidth"/> is what the row always shows besides them (the draft's chip, when it
    /// has no starter).
    /// </summary>
    internal static bool[] FitRow(
        IReadOnlyList<double> widths,
        IReadOnlyList<bool> isKept,
        double available,
        double fixedWidth,
        double endWidth,
        double stackWidth,
        double dividerWidth,
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
            bool anyKept = false, anyLearned = false;
            for (var index = 0; index < count; index++)
            {
                if (!shown[index])
                    continue;

                total += widths[index];
                anyKept |= isKept[index];
                anyLearned |= !isKept[index];
            }

            return anyKept && anyLearned ? total + dividerWidth : total;
        }
    }

    [GeneratedRegex(@"(?<=[.!?…])\s+|(?<=[。！？])")]
    private static partial Regex SentenceEnd();

    /// <summary>
    /// The brief's opening sentence, as one calm line under the greeting: where the project stands now. Briefs open with
    /// what the project is going through (<see cref="Services.Context.ContextCuratorPrompts"/>).
    /// </summary>
    internal static string BriefHeadline(string? brief)
    {
        if (string.IsNullOrWhiteSpace(brief))
            return "";

        var paragraph = brief.Split('\n').Select(static line => line.Trim()).FirstOrDefault(static line => line.Length > 0) ?? "";
        var text = StripMarkdown(paragraph);
        var end = SentenceEnd().Match(text);
        var sentence = end.Success && end.Index > 0 ? text[..end.Index].Trim() : text;
        if (sentence.Length <= BriefHeadlineMaxLength)
            return sentence;

        var cut = sentence.LastIndexOf(' ', BriefHeadlineMaxLength - 1);
        return (cut > BriefHeadlineMaxLength / 2 ? sentence[..cut] : sentence[..(BriefHeadlineMaxLength - 1)]).TrimEnd(',', ';', ':', ' ') + "…";
    }

    /// <summary>
    /// Flattens a persisted chat preview into one calm line: Lumi's visual blocks (a result card's
    /// JSON) become their headline, and markdown markup is dropped.
    /// </summary>
    internal static string CleanPreview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var value = text;
        var fence = VisualFence.Match(value);
        if (fence.Success)
        {
            // A short lead-in ("Done:") says less than the card's own headline.
            var lead = StripMarkdown(value[..fence.Index]);
            value = lead.Length >= 8
                ? lead
                : ReadCardHeadline(value[fence.Index..]) ?? lead;
        }

        return Truncate(StripMarkdown(value.Replace("```", " ")), PreviewMaxLength);
    }

    /// <summary>Formats when an automation runs next, relative to today. The Jobs page shows next runs the same way.</summary>
    internal static string FormatRunTime(DateTimeOffset at, DateTimeOffset now)
    {
        var culture = CultureInfo.CurrentCulture;
        var local = at.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var time = local.ToString("t", culture);
        if (local.Date == today)
            return time;
        if (local.Date == today.AddDays(1))
            return Format(Loc.Launchpad_TomorrowAt, time);
        if (local.Date < today.AddDays(7))
            return local.ToString("ddd", culture) + " " + time;
        return local.ToString("MMM d", culture);
    }

    /// <summary>
    /// Reads a visual block's "header" (or "summary") string. Stored previews are cut short, so the
    /// block is usually incomplete JSON; this reads the value it can see instead of parsing the block.
    /// </summary>
    private static string? ReadCardHeadline(string block)
    {
        foreach (var key in new[] { "header", "summary" })
        {
            var marker = block.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (marker < 0)
                continue;

            var colon = block.IndexOf(':', marker);
            var open = colon < 0 ? -1 : block.IndexOf('"', colon + 1);
            if (open < 0)
                continue;

            var raw = new StringBuilder();
            var closed = false;
            for (var index = open + 1; index < block.Length; index++)
            {
                var ch = block[index];
                if (ch == '\\' && index + 1 < block.Length)
                {
                    raw.Append(ch).Append(block[++index]);
                    continue;
                }

                if (ch == '"')
                {
                    closed = true;
                    break;
                }

                raw.Append(ch);
            }

            var headline = Unescape(raw.ToString());
            if (string.IsNullOrWhiteSpace(headline))
                continue;

            return closed ? headline : headline.TrimEnd() + "…";
        }

        return null;
    }

    private static string Unescape(string json)
    {
        try
        {
            return JsonSerializer.Deserialize("\"" + json + "\"", AppDataJsonContext.Default.String) ?? json;
        }
        catch (JsonException)
        {
            return json.Replace("\\\"", "\"", StringComparison.Ordinal);
        }
    }

    private static string StripMarkdown(string text)
    {
        var value = MarkdownLink.Replace(text, "$1");
        value = LeadingMarker.Replace(value, "");
        value = Emphasis.Replace(value, "");
        return Whitespace.Replace(value, " ").Trim();
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..(maxLength - 1)].TrimEnd() + "…";

    private static string Format(string format, params object[] args)
        => string.Format(CultureInfo.CurrentCulture, format, args);
}
