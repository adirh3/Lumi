using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Lumi.Localization;
using Lumi.Models;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class LaunchpadPlannerTests
{
    // Local time, so "tomorrow" means the same thing to the planner and the test on any machine.
    private static readonly DateTimeOffset Now = new(
        new DateTime(2026, 9, 29, 20, 0, 0),
        TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 29, 20, 0, 0)));
    private static readonly Guid Lumi = Guid.NewGuid();
    private static readonly Guid Search = Guid.NewGuid();
    private static readonly Guid CodingAgent = Guid.NewGuid();

    public LaunchpadPlannerTests() => Loc.Load("en");

    [Theory]
    [InlineData(8, "Good morning, ", "")]
    [InlineData(14, "Good afternoon, ", "")]
    [InlineData(19, "Good evening, ", "")]
    [InlineData(2, "Working late, ", "?")]
    public void Greeting_SplitsAroundTheNameForEachPartOfTheDay(int hour, string lead, string trail)
    {
        var greeting = LaunchpadPlanner.BuildGreeting(" Adir ", hour);

        Assert.Equal(lead, greeting.Lead);
        Assert.Equal("Adir", greeting.Name);
        Assert.Equal(trail, greeting.Trail);
    }

    [Fact]
    public void Greeting_WithoutANameIsJustTheGreeting()
    {
        var greeting = LaunchpadPlanner.BuildGreeting(null, 9);

        Assert.Equal("Good morning", greeting.Lead);
        Assert.Equal("", greeting.Name);
        Assert.Equal("", greeting.Trail);
    }

    [Fact]
    public void Brief_NamesWhatNeedsTheUserMostUrgentFirst()
    {
        Assert.Equal(
            "2 chats need your answers · 1 new reply · 3 chats working",
            LaunchpadPlanner.BuildBrief(waiting: 2, replies: 1, working: 3, hasHistory: true));
        Assert.Equal("1 chat working", LaunchpadPlanner.BuildBrief(0, 0, 1, hasHistory: true));
    }

    [Fact]
    public void Brief_IsCalmWhenNothingIsPending()
    {
        Assert.Equal(Loc.Launchpad_BriefQuiet, LaunchpadPlanner.BuildBrief(0, 0, 0, hasHistory: true));
        Assert.Equal(Loc.Chat_WelcomeSubtitle, LaunchpadPlanner.BuildBrief(0, 0, 0, hasHistory: false));
    }

    [Fact]
    public void PickUp_OrdersByUrgencyThenFillsWithRecentChats()
    {
        var recent = NewChat("Recent", Now.AddMinutes(-5));
        var older = NewChat("Older", Now.AddHours(-3));
        var working = NewChat("Working", Now.AddHours(-6));
        working.IsRunning = true;
        var reply = NewChat("Reply", Now.AddHours(-2));
        reply.HasUnreadMessages = true;
        var waiting = NewChat("Waiting", Now.AddDays(-1));
        waiting.IsAwaitingInput = true;

        var picked = LaunchpadPlanner.SelectPickUp([recent, older, working, reply, waiting], new HashSet<Guid>(), 4);

        Assert.Equal(["Waiting", "Reply", "Working", "Recent"], picked.Select(entry => entry.Chat.Title));
        Assert.Equal(
            [LaunchpadChatState.Waiting, LaunchpadChatState.Reply, LaunchpadChatState.Working, LaunchpadChatState.Recent],
            picked.Select(entry => entry.State));
    }

    [Fact]
    public void PickUp_LeavesOutEmptyChatsAndQuietAutomationChats()
    {
        var empty = NewChat("Empty", Now, messageCount: 0);
        var automation = NewChat("Watcher", Now.AddMinutes(-1));
        var unreadAutomation = NewChat("Digest", Now.AddHours(-9));
        unreadAutomation.HasUnreadMessages = true;
        var conversation = NewChat("Conversation", Now.AddHours(-1));

        var picked = LaunchpadPlanner.SelectPickUp(
            [empty, automation, unreadAutomation, conversation],
            new HashSet<Guid> { automation.Id, unreadAutomation.Id },
            4);

        Assert.Equal(["Digest", "Conversation"], picked.Select(entry => entry.Chat.Title));
    }

    [Fact]
    public void Setups_OfferRecurringConfigurationsAndSkipTheDefault()
    {
        var chats = new List<Chat>
        {
            SetupChat(Lumi, null, worktree: true, "gpt-6", "max", daysAgo: 1),
            SetupChat(Lumi, null, worktree: true, "gpt-6", "max", daysAgo: 3),
            SetupChat(Lumi, null, worktree: true, "gpt-6", "max", daysAgo: 4),
            SetupChat(Search, CodingAgent, worktree: false, "claude", "high", daysAgo: 2),
            SetupChat(Search, CodingAgent, worktree: false, "claude", "high", daysAgo: 5),
            // Used once: not a habit yet.
            SetupChat(Search, null, worktree: true, "gpt-6", "max", daysAgo: 1),
            // The plain default is what a new chat already is.
            SetupChat(null, null, worktree: false, "default-model", "high", daysAgo: 1),
            SetupChat(null, null, worktree: false, "default-model", "high", daysAgo: 2),
            // Outside the window.
            SetupChat(null, null, worktree: false, "old-model", null, daysAgo: 40),
            SetupChat(null, null, worktree: false, "old-model", null, daysAgo: 45),
        };

        var setups = SelectSetups(chats, plainDefault: new LaunchpadSetupSpec(null, null, false, "default-model", "high"));

        Assert.Equal(
            [
                new LaunchpadSetupSpec(Lumi, null, true, "gpt-6", "max"),
                new LaunchpadSetupSpec(Search, CodingAgent, false, "claude", "high"),
            ],
            setups);
    }

    [Fact]
    public void Setups_CoverDifferentPlacesBeforeRepeatingOne()
    {
        var chats = new List<Chat>();
        for (var day = 1; day <= 6; day++)
            chats.Add(SetupChat(Lumi, null, worktree: true, "gpt-6", "max", day));
        for (var day = 1; day <= 5; day++)
            chats.Add(SetupChat(Lumi, null, worktree: true, "claude", "max", day));
        chats.Add(SetupChat(Search, null, worktree: false, "gpt-6", "high", daysAgo: 10));
        chats.Add(SetupChat(Search, null, worktree: false, "gpt-6", "high", daysAgo: 12));

        var setups = SelectSetups(chats, plainDefault: null);

        Assert.Equal(
            [
                new LaunchpadSetupSpec(Lumi, null, true, "gpt-6", "max"),
                new LaunchpadSetupSpec(Search, null, false, "gpt-6", "high"),
                new LaunchpadSetupSpec(Lumi, null, true, "claude", "max"),
            ],
            setups);
    }

    [Fact]
    public void Setups_ForgetDeletedProjectsAndUnavailableModels()
    {
        var removedProject = Guid.NewGuid();
        var chats = new List<Chat>
        {
            SetupChat(removedProject, null, worktree: true, "retired-model", "max", daysAgo: 1),
            SetupChat(removedProject, null, worktree: true, "retired-model", "max", daysAgo: 2),
            SetupChat(Lumi, null, worktree: false, "retired-model", "max", daysAgo: 1),
            SetupChat(Lumi, null, worktree: false, "retired-model", "max", daysAgo: 2),
        };

        var setups = SelectSetups(
            chats,
            plainDefault: null,
            resolveModel: (model, effort) => model == "retired-model" ? (null, null) : (model, effort));

        // A setup without its project or model is only a place, and a place-less, model-less setup is nothing.
        Assert.Equal([new LaunchpadSetupSpec(Lumi, null, false, null, null)], setups);
    }

    [Fact]
    public void Setups_KeepOneHabitWhenStoredEffortsDriftOnAModelWithoutEfforts()
    {
        var chats = new List<Chat>
        {
            SetupChat(Lumi, null, worktree: false, "no-effort-model", "high", daysAgo: 1),
            SetupChat(Lumi, null, worktree: false, "no-effort-model", "medium", daysAgo: 2),
        };

        var setups = SelectSetups(
            chats,
            plainDefault: null,
            resolveModel: (model, effort) => (model, model == "no-effort-model" ? null : effort));

        Assert.Equal([new LaunchpadSetupSpec(Lumi, null, false, "no-effort-model", null)], setups);
    }

    [Fact]
    public void Setups_IgnoreAutomationChats()
    {
        var first = SetupChat(Lumi, null, worktree: true, "gpt-6", "max", daysAgo: 1);
        var second = SetupChat(Lumi, null, worktree: true, "gpt-6", "max", daysAgo: 2);

        var setups = LaunchpadPlanner.SelectSetups(
            [first, second],
            new HashSet<Guid> { first.Id },
            Now,
            _ => true,
            _ => true,
            (model, effort) => (model, effort),
            plainDefault: null,
            limit: 3);

        Assert.Empty(setups);
    }

    [Theory]
    [InlineData(8, true, "Catch me up")]
    [InlineData(15, true, "What should I focus on?")]
    [InlineData(20, true, "Recap my day")]
    [InlineData(20, false, "Plan tomorrow")]
    public void Starters_FitTheTimeOfDay(int hour, bool hasHistory, string expectedLabel)
    {
        var starters = LaunchpadPlanner.SelectStarters(hour, hasHistory);

        Assert.Equal(3, starters.Count);
        Assert.Contains(starters, starter => starter.Label == expectedLabel);
        Assert.All(starters, starter => Assert.False(string.IsNullOrWhiteSpace(starter.Prompt)));
    }

    [Fact]
    public void Starters_WithoutHistoryNeverPromiseAReviewOfPastChats()
    {
        for (var hour = 0; hour < 24; hour++)
        {
            var labels = LaunchpadPlanner.SelectStarters(hour, hasHistory: false).Select(starter => starter.Label);
            Assert.DoesNotContain(Loc.Launchpad_StarterCatchUp, labels);
            Assert.DoesNotContain(Loc.Launchpad_StarterRecap, labels);
            Assert.DoesNotContain(Loc.Launchpad_StarterFocus, labels);
        }
    }

    [Theory]
    [InlineData("**Pushed** to `main` — [open PR](https://github.com/x/y/pull/1)", "Pushed to main — open PR")]
    [InlineData("## Done — all green", "Done — all green")]
    [InlineData("> Quoted note", "Quoted note")]
    [InlineData("Open Settings > Privacy > Location", "Open Settings > Privacy > Location")]
    [InlineData("Tracked in bug # 42", "Tracked in bug # 42")]
    [InlineData("```card {\"header\":\"Daily status \\u2014 Sep 29\",\"summary\":\"x\"} ```", "Daily status — Sep 29")]
    [InlineData("Here's the digest. ```card {\"header\":\"Ignored\"} ```", "Here's the digest.")]
    [InlineData("", "")]
    public void Preview_ReadsAsOneCalmLine(string raw, string expected)
    {
        Assert.Equal(expected, LaunchpadPlanner.CleanPreview(raw));
    }

    [Fact]
    public void Preview_OfATruncatedCardStillReadsItsHeadline()
    {
        var preview = LaunchpadPlanner.CleanPreview("```card {\"header\":\"Pushed to main, and CI is gre");

        Assert.Equal("Pushed to main, and CI is gre…", preview);
    }

    [Fact]
    public void Preview_IsTruncatedToOneLine()
    {
        var preview = LaunchpadPlanner.CleanPreview(new string('a', 400));

        Assert.Equal(LaunchpadPlanner.PreviewMaxLength, preview.Length);
        Assert.EndsWith("…", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void Automations_SummarizeActiveJobsAndTheNextRun()
    {
        var jobs = new List<BackgroundJob>
        {
            new() { Name = "Morning brief", IsEnabled = true, NextRunAt = Now.AddHours(13) },
            new() { Name = "Watcher", IsEnabled = true },
            new() { Name = "Paused", IsEnabled = false, NextRunAt = Now.AddMinutes(5) },
        };

        var summary = LaunchpadPlanner.BuildAutomationSummary(jobs, Now, out var hasAutomations);

        var tomorrow = Now.AddHours(13).ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        Assert.True(hasAutomations);
        Assert.Equal($"2 automations active · next: Morning brief, tomorrow {tomorrow}", summary);
    }

    [Fact]
    public void Automations_AreHiddenWhenNoneAreEnabled()
    {
        var summary = LaunchpadPlanner.BuildAutomationSummary(
            [new BackgroundJob { Name = "Paused", IsEnabled = false }],
            Now,
            out var hasAutomations);

        Assert.False(hasAutomations);
        Assert.Equal("", summary);
    }

    private static List<LaunchpadSetupSpec> SelectSetups(
        IEnumerable<Chat> chats,
        LaunchpadSetupSpec? plainDefault,
        Func<string?, string?, (string? Model, string? Effort)>? resolveModel = null)
        => LaunchpadPlanner.SelectSetups(
            chats,
            new HashSet<Guid>(),
            Now,
            projectId => projectId == Lumi || projectId == Search,
            agentId => agentId == CodingAgent,
            resolveModel ?? ((model, effort) => (model, effort)),
            plainDefault,
            LaunchpadPlanner.MaxSetups);

    private static Chat NewChat(string title, DateTimeOffset updatedAt, int messageCount = 2)
        => new() { Title = title, UpdatedAt = updatedAt, CreatedAt = updatedAt, MessageCount = messageCount };

    private static Chat SetupChat(Guid? projectId, Guid? agentId, bool worktree, string model, string? effort, int daysAgo)
        => new()
        {
            ProjectId = projectId,
            AgentId = agentId,
            WorktreePath = worktree ? @"C:\worktrees\wt" : null,
            LastModelUsed = model,
            LastReasoningEffortUsed = effort,
            CreatedAt = Now.AddDays(-daysAgo),
            UpdatedAt = Now.AddDays(-daysAgo),
            MessageCount = 4,
        };
}
