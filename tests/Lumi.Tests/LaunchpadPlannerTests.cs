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
    public void NeedsYou_ListsQuestionsThenUnreadReplies_AndLeavesWorkInProgressOut()
    {
        var recent = NewChat("Recent", Now.AddMinutes(-5));
        var working = NewChat("Working", Now.AddHours(-6));
        working.IsRunning = true;
        var background = NewChat("Background", Now.AddHours(-1));
        background.IsSessionActive = true;
        var reply = NewChat("Reply", Now.AddHours(-2));
        reply.HasUnreadMessages = true;
        var olderReply = NewChat("Older reply", Now.AddHours(-4));
        olderReply.HasUnreadMessages = true;
        var waiting = NewChat("Waiting", Now.AddDays(-1));
        waiting.IsAwaitingInput = true;

        var picked = LaunchpadPlanner.SelectNeedsYou([recent, working, background, reply, olderReply, waiting], 2, out var total);

        Assert.Equal(3, total);
        Assert.Equal(["Waiting", "Reply"], picked.Select(entry => entry.Chat.Title));
        Assert.Equal([LaunchpadChatState.Waiting, LaunchpadChatState.Reply], picked.Select(entry => entry.State));
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

    [Fact]
    public void Starters_CanBeFoundAgainByTheirIds()
    {
        for (var hour = 0; hour < 24; hour += 3)
        {
            foreach (var starter in LaunchpadPlanner.SelectStarters(hour, hasHistory: true))
            {
                Assert.NotNull(starter.Id);
                Assert.Equal(starter, LaunchpadStarterCatalog.Find(starter.Id!));
            }
        }

        foreach (var category in LaunchpadStarterCatalog.Order)
        {
            var starters = LaunchpadStarterCatalog.In(category);
            // Six fill a tab's three rows of two tiles.
            Assert.Equal(6, starters.Count);
            Assert.All(starters, starter =>
            {
                Assert.Equal(category, starter.CategoryId);
                Assert.Equal(starter, LaunchpadStarterCatalog.Find(starter.Id!));
            });
        }

        Assert.Equal(8, LaunchpadStarterCatalog.Order.Distinct().Count());
        Assert.Null(LaunchpadStarterCatalog.Find("no.such.starter"));
    }

    [Theory]
    [InlineData("he")]
    [InlineData("es")]
    [InlineData("pt-BR")]
    [InlineData("zh-Hans")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("ar")]
    public void StarterTemplates_KeepTheirBlanksInEveryLanguage(string language)
    {
        var english = LaunchpadStarterCatalog.TemplateKeys.ToDictionary(key => key, key => LaunchpadTemplate.Blanks(Loc.Get(key)).Count);
        try
        {
            Loc.Load(language);
            foreach (var key in LaunchpadStarterCatalog.TemplateKeys)
            {
                var template = Loc.Get(key);
                Assert.Equal(english[key], LaunchpadTemplate.Blanks(template).Count);
                // Brackets only ever open and close a blank.
                Assert.Equal(english[key], template.Count(ch => ch == '['));
                Assert.Equal(english[key], template.Count(ch => ch == ']'));
            }
        }
        finally
        {
            Loc.Load("en");
        }
    }

    [Fact]
    public void Template_SplitsIntoWordsAndBlanks()
    {
        Assert.Equal(
            [
                new LaunchpadTemplatePart("Help me choose a", false, false),
                new LaunchpadTemplatePart("product", true, true),
                new LaunchpadTemplatePart("under", false, true),
                new LaunchpadTemplatePart("budget", true, true),
            ],
            LaunchpadTemplate.Parse("Help me choose a [product] under [budget]"));
        Assert.Equal(
            [
                new LaunchpadTemplatePart("What's new in", false, false),
                new LaunchpadTemplatePart("topic", true, true),
                new LaunchpadTemplatePart("?", false, false),
            ],
            LaunchpadTemplate.Parse("What's new in [topic]?"));
        Assert.Equal(["[product]", "[budget]"], LaunchpadTemplate.Blanks("Help me choose a [product] under [budget]"));
        Assert.Empty(LaunchpadTemplate.Blanks("Review my changes"));
        Assert.Equal([new LaunchpadTemplatePart("Review my changes", false, false)], LaunchpadTemplate.Parse("Review my changes"));
    }

    [Fact]
    public void ForYou_LeadsWithTheBriefsPlans_ThenHabits_ThenTheHour_ThenFillsUp()
    {
        var timeOfDay = LaunchpadPlanner.SelectStarters(20, hasHistory: true);
        var trip = LaunchpadStarterCatalog.Find("plan.trip")!;
        var recap = timeOfDay[0];
        var fillers = LaunchpadStarterCatalog.Fillers(isCodingProject: false);

        var project = LaunchpadPlanner.SelectForYou(
            ["Publish 1.0.2", " ", "Continue the benchmark", "A third plan", "A fourth plan"], "From Lumi's brief", [trip], timeOfDay, fillers);
        Assert.Equal(["Publish 1.0.2", "Continue the benchmark", "A third plan", trip.Prompt, timeOfDay[0].Prompt, timeOfDay[1].Prompt],
            project.Select(starter => starter.Prompt));
        Assert.All(project.Take(3), starter =>
        {
            Assert.Null(starter.Id);
            Assert.Equal(LaunchpadPlanner.BriefStarterGlyph, starter.Glyph);
            Assert.Equal("From Lumi's brief", starter.Reason);
        });

        // A habit that is also the hour's starter shows once.
        var personal = LaunchpadPlanner.SelectForYou([], null, [recap, trip], timeOfDay, fillers);
        Assert.Equal([recap.Id, trip.Id, timeOfDay[1].Id, timeOfDay[2].Id, fillers[1].Id, fillers[2].Id], personal.Select(starter => starter.Id));

        // With nothing of the user's own, the hour's starters lead (as on the phone) and well-tried ones fill the list.
        var plain = LaunchpadPlanner.SelectForYou([], null, [], timeOfDay, fillers);
        Assert.Equal(LaunchpadPlanner.ForYouCount, plain.Count);
        Assert.Equal(timeOfDay, plain.Take(timeOfDay.Count));
        Assert.Equal(["code.review", "code.explain"],
            LaunchpadPlanner.SelectForYou([], null, [], [], LaunchpadStarterCatalog.Fillers(isCodingProject: true)).Take(2).Select(s => s.Id));
        // In a coding project the work comes before the hour: after the brief's plans and the user's habits, the code.
        var coding = LaunchpadStarterCatalog.Fillers(isCodingProject: true);
        Assert.Equal(["Publish 1.0.2", trip.Prompt, .. coding.Take(4).Select(starter => starter.Prompt)],
            LaunchpadPlanner.SelectForYou(["Publish 1.0.2"], "From Lumi's brief", [trip], timeOfDay, coding, workFirst: true)
                .Select(starter => starter.Prompt));
        // Either list of well-tried starters fills For you on its own, so its grid never has a gap.
        Assert.All([false, true], coding => Assert.Equal(LaunchpadPlanner.ForYouCount,
            LaunchpadPlanner.SelectForYou([], null, [], [], LaunchpadStarterCatalog.Fillers(coding)).Count));
    }

    [Fact]
    public void ForYou_SaysWhyAStarterSuitsTheHour_OnlyForTheDayItself()
    {
        Loc.Load("en");
        Assert.Equal("This morning", LaunchpadPlanner.TimeOfDayReason("day.plan", 9));
        Assert.Equal("This afternoon", LaunchpadPlanner.TimeOfDayReason("day.focus", 14));
        Assert.Equal("This evening", LaunchpadPlanner.TimeOfDayReason("day.recap", 19));
        Assert.Equal("Tonight", LaunchpadPlanner.TimeOfDayReason("day.tomorrow", 23));
        // Researching or writing a document suits any hour.
        Assert.Null(LaunchpadPlanner.TimeOfDayReason("day.research", 9));
        Assert.Null(LaunchpadPlanner.TimeOfDayReason("plan.trip", 9));
    }

    [Fact]
    public void Setups_KeptComeFirst_EveryVariantIncluded_AndKeptOrHiddenPlacesAreNotLearnedAgain()
    {
        var lumi = new LaunchpadSetupSpec(Lumi, null, true, "gpt-6-sol", "high");
        var lumiLearned = new LaunchpadSetupSpec(Lumi, null, true, "gpt-6-luna", null);
        var search = new LaunchpadSetupSpec(Search, null, false, "gpt-6-luna", null);
        var planner = new LaunchpadSetupSpec(null, Guid.NewGuid(), false, "gpt-6-luna", null);
        var plainSol = new LaunchpadSetupSpec(null, null, false, "gpt-6-sol", null);

        var merged = LaunchpadPlanner.MergeSetups([lumi], [lumiLearned, search, planner, plainSol], [search.Context], limit: 3);

        Assert.Equal([(lumi, 0), (planner, -1), (plainSol, -1)], merged);

        // One project kept on several models and checkouts: every one shows, each a setup of its own, and a setup kept
        // twice shows once.
        var lumiOpus = lumi with { ModelId = "claude-opus-5.5" };
        var lumiLocal = lumi with { UseWorktree = false };
        Assert.Equal([(lumi, 0), (lumiOpus, 1), (lumiLocal, 3)],
            LaunchpadPlanner.MergeSetups([lumi, lumiOpus, lumi, lumiLocal], [lumiLearned, search], [], 3));

        // Kept setups never count against the learned ones' limit, up to the most the row shows.
        var many = Enumerable.Range(0, LaunchpadPlanner.MaxKeptSetups + 2)
            .Select(index => lumi with { ModelId = $"model-{index}" }).ToList();
        Assert.Equal(many.Take(LaunchpadPlanner.MaxKeptSetups).Select((spec, index) => (spec, index)),
            LaunchpadPlanner.MergeSetups(many, [search, planner], [], 3));

        // A setup the user keeps shows even where they asked not to see what they use.
        Assert.Equal([(lumi, 0), (search, -1)], LaunchpadPlanner.MergeSetups([lumi], [lumiLearned, search], [lumi.Context], 3));
    }

    [Theory]
    [InlineData("Lumi 1.0.1 is live; 1.0.2 is prepared but not published. Show HN moved to Monday.", "Lumi 1.0.1 is live; 1.0.2 is prepared but not published.")]
    [InlineData("**Mobile smoothness** work is active.\n\nSaved workflows remain exploratory.", "Mobile smoothness work is active.")]
    [InlineData("Lumi 1.0 ships next week", "Lumi 1.0 ships next week")]
    [InlineData("项目正在准备 1.0 版本。下周发布。", "项目正在准备 1.0 版本。")]
    [InlineData("", "")]
    public void BriefHeadline_IsTheBriefsOpeningSentence(string brief, string expected)
    {
        Assert.Equal(expected, LaunchpadPlanner.BriefHeadline(brief));
    }

    [Fact]
    public void BriefHeadline_TrimsALongSentenceAtAWord()
    {
        var headline = LaunchpadPlanner.BriefHeadline(string.Join(' ', Enumerable.Repeat("release", 40)) + ".");

        Assert.True(headline.Length <= LaunchpadPlanner.BriefHeadlineMaxLength);
        Assert.EndsWith("release…", headline, StringComparison.Ordinal);
    }

    [Fact]
    public void Picks_BecomeHabits_InThePlaceTheyWereMade()
    {
        var uses = new List<StarterUse>();
        LaunchpadStarterUse.Record(uses, "money.budget", null, Now.AddDays(-3));
        LaunchpadStarterUse.Record(uses, "money.budget", null, Now.AddDays(-1));
        LaunchpadStarterUse.Record(uses, "learn.news", null, Now.AddDays(-1));
        LaunchpadStarterUse.Record(uses, "code.review", Lumi, Now.AddDays(-2));
        LaunchpadStarterUse.Record(uses, "code.review", Lumi, Now);

        // Picked twice lately is a habit; once is not yet.
        Assert.Equal(["money.budget"], LaunchpadStarterUse.Habits(uses, null, Now));
        Assert.Equal(["code.review"], LaunchpadStarterUse.Habits(uses, Lumi, Now));
        Assert.Empty(LaunchpadStarterUse.Habits(uses, Search, Now));

        // A habit dropped for months fades.
        Assert.Empty(LaunchpadStarterUse.Habits(uses, null, Now.AddDays(120)));
    }

    [Fact]
    public void Picks_KeepABoundedRecord()
    {
        var uses = new List<StarterUse>();
        for (var index = 0; index <= LaunchpadStarterUse.MaxRecords; index++)
            LaunchpadStarterUse.Record(uses, "plan.trip", Guid.NewGuid(), Now.AddMinutes(index));

        Assert.Equal(LaunchpadStarterUse.MaxRecords, uses.Count);
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
