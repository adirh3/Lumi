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

/// <summary>What a chat on the launchpad's "pick up" list is doing, most urgent first.</summary>
public enum LaunchpadChatState
{
    Waiting,
    Reply,
    Working,
    Background,
    Recent,
}

internal enum LaunchpadDayPart
{
    Morning,
    Afternoon,
    Evening,
    Night,
}

/// <summary>
/// A new-chat configuration learned from how the user actually starts chats: where (project and
/// local/worktree checkout), as whom (Lumi agent) and with which model and reasoning effort.
/// </summary>
internal sealed record LaunchpadSetupSpec(
    Guid? ProjectId,
    Guid? AgentId,
    bool UseWorktree,
    string? ModelId,
    string? Effort)
{
    /// <summary>Where and as whom the setup works, ignoring the model — the axis setups are diversified on.</summary>
    internal (Guid? ProjectId, Guid? AgentId, bool UseWorktree) Context => (ProjectId, AgentId, UseWorktree);

    internal bool IsEmpty => ProjectId is null && AgentId is null && ModelId is null;
}

/// <summary>The personal greeting, split around the user's name so the name can carry its own emphasis.</summary>
internal readonly record struct LaunchpadGreeting(string Lead, string Name, string Trail);

internal sealed record LaunchpadStarterSpec(string Glyph, string Label, string Prompt);

/// <summary>
/// The launchpad's decisions, kept free of UI and store state so they are cheap and testable:
/// which chats deserve attention, which setups the user reaches for, and which starters fit the hour.
/// </summary>
internal static class LaunchpadPlanner
{
    internal const int MaxPickUpItems = 4;
    internal const int MaxSetups = 3;

    /// <summary>A configuration must recur before it is offered as one of the user's setups.</summary>
    private const int MinSetupUses = 2;

    internal const int PreviewMaxLength = 140;

    private static readonly TimeSpan SetupWindow = TimeSpan.FromDays(30);

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
    /// Chats to pick up, most urgent first: waiting on an answer, then unread replies, then work in
    /// progress. Remaining room goes to the most recent conversations, leaving out chats an active
    /// automation keeps writing to (they surface when they have something unread).
    /// </summary>
    internal static List<(Chat Chat, LaunchpadChatState State)> SelectPickUp(
        IEnumerable<Chat> chats,
        IReadOnlySet<Guid> automationChatIds,
        int limit)
    {
        var all = chats as IReadOnlyCollection<Chat> ?? chats.ToList();
        var picked = all
            .Select(chat => (Chat: chat, State: ClassifyActive(chat)))
            .Where(static entry => entry.State is not null)
            .OrderBy(static entry => entry.State!.Value)
            .ThenByDescending(static entry => entry.Chat.UpdatedAt)
            .Take(limit)
            .Select(static entry => (entry.Chat, entry.State!.Value))
            .ToList();

        if (picked.Count >= limit)
            return picked;

        var pickedIds = picked.Select(static entry => entry.Chat.Id).ToHashSet();
        picked.AddRange(all
            .Where(chat => !pickedIds.Contains(chat.Id)
                           && !automationChatIds.Contains(chat.Id)
                           && HasContent(chat))
            .OrderByDescending(static chat => chat.UpdatedAt)
            .Take(limit - picked.Count)
            .Select(static chat => (chat, LaunchpadChatState.Recent)));
        return picked;
    }

    /// <summary>
    /// The configurations the user keeps starting chats with, ranked by recency-weighted use.
    /// The first pass keeps one setup per place (project, agent, checkout) so the row covers the
    /// different kinds of work instead of three model variants of the same one.
    /// <paramref name="resolveModel"/> maps a chat's last model and effort to what a new chat would
    /// use today: no model when it is unavailable, and only an effort the model supports.
    /// </summary>
    internal static List<LaunchpadSetupSpec> SelectSetups(
        IEnumerable<Chat> chats,
        IReadOnlySet<Guid> automationChatIds,
        DateTimeOffset now,
        Func<Guid, bool> projectExists,
        Func<Guid, bool> agentExists,
        Func<string?, string?, (string? Model, string? Effort)> resolveModel,
        LaunchpadSetupSpec? plainDefault,
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

            var spec = ToSetupSpec(chat, projectExists, agentExists, resolveModel);
            if (spec.IsEmpty)
                continue;

            var weight = Math.Exp(-age.TotalDays / SetupDecayDays);
            votes[spec] = votes.TryGetValue(spec, out var vote)
                ? (vote.Score + weight, vote.Uses + 1)
                : (weight, 1);
        }

        var ranked = votes
            .Where(entry => entry.Value.Uses >= MinSetupUses && entry.Key != plainDefault)
            .OrderByDescending(static entry => entry.Value.Score)
            .ThenByDescending(static entry => entry.Value.Uses)
            .Select(static entry => entry.Key)
            .ToList();

        var result = new List<LaunchpadSetupSpec>(limit);
        var places = new HashSet<(Guid?, Guid?, bool)>();
        foreach (var spec in ranked)
        {
            if (result.Count == limit)
                break;
            if (places.Add(spec.Context))
                result.Add(spec);
        }

        foreach (var spec in ranked)
        {
            if (result.Count == limit)
                break;
            if (!result.Contains(spec))
                result.Add(spec);
        }

        return result;
    }

    private static LaunchpadSetupSpec ToSetupSpec(
        Chat chat,
        Func<Guid, bool> projectExists,
        Func<Guid, bool> agentExists,
        Func<string?, string?, (string? Model, string? Effort)> resolveModel)
    {
        var projectId = chat.ProjectId is { } project && projectExists(project) ? project : (Guid?)null;
        var agentId = chat.AgentId is { } agent && agentExists(agent) ? agent : (Guid?)null;
        var (model, effort) = resolveModel(chat.LastModelUsed, chat.LastReasoningEffortUsed);
        return new LaunchpadSetupSpec(
            projectId,
            agentId,
            projectId is not null && !string.IsNullOrWhiteSpace(chat.WorktreePath),
            model,
            model is null ? null : effort);
    }

    /// <summary>Three conversation starters that suit the time of day and whether there is history to draw on.</summary>
    internal static List<LaunchpadStarterSpec> SelectStarters(int hour, bool hasHistory)
    {
        var planDay = new LaunchpadStarterSpec("☀️", Loc.Chat_SuggestionA, Loc.Chat_SuggestionA);
        var research = new LaunchpadStarterSpec("🔍", Loc.Chat_SuggestionB, Loc.Chat_SuggestionB);
        var document = new LaunchpadStarterSpec("📄", Loc.Chat_SuggestionC, Loc.Chat_SuggestionC);
        var planTomorrow = new LaunchpadStarterSpec("🗓️", Loc.Launchpad_StarterPlanTomorrow, Loc.Launchpad_StarterPlanTomorrowPrompt);
        var catchUp = new LaunchpadStarterSpec("📬", Loc.Launchpad_StarterCatchUp, Loc.Launchpad_StarterCatchUpPrompt);
        var focus = new LaunchpadStarterSpec("🎯", Loc.Launchpad_StarterFocus, Loc.Launchpad_StarterFocusPrompt);
        var recap = new LaunchpadStarterSpec("🌙", Loc.Launchpad_StarterRecap, Loc.Launchpad_StarterRecapPrompt);

        return DayPartOf(hour) switch
        {
            LaunchpadDayPart.Morning => hasHistory ? [planDay, catchUp, research] : [planDay, research, document],
            LaunchpadDayPart.Afternoon => hasHistory ? [focus, research, document] : [research, document, planDay],
            LaunchpadDayPart.Evening => hasHistory ? [recap, planTomorrow, research] : [planTomorrow, research, document],
            _ => hasHistory ? [recap, planTomorrow, document] : [planTomorrow, research, document],
        };
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

    /// <summary>Formats when an automation runs next, relative to today.</summary>
    private static string FormatRunTime(DateTimeOffset at, DateTimeOffset now)
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

    internal static string BuildAutomationSummary(IReadOnlyCollection<BackgroundJob> jobs, DateTimeOffset now, out bool hasAutomations)
    {
        var active = jobs.Where(static job => job.IsEnabled).ToList();
        hasAutomations = active.Count > 0;
        if (!hasAutomations)
            return "";

        var count = active.Count == 1
            ? Loc.Launchpad_AutomationsOne
            : Format(Loc.Launchpad_AutomationsMany, active.Count);
        var next = active
            .Where(job => job.NextRunAt is { } at && at > now)
            .OrderBy(static job => job.NextRunAt)
            .FirstOrDefault();

        return next?.NextRunAt is { } nextRun
            ? count + " · " + Format(Loc.Launchpad_AutomationsNext, next.Name, FormatRunTime(nextRun, now))
            : count;
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
