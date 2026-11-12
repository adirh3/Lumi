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
    public void Setups_LearnPlacesNeverModels_RankedByRecentUse()
    {
        var chats = new List<Chat>
        {
            // One place on two models is one habit: the model is never part of it.
            SetupChat(Lumi, null, worktree: true, "gpt-6", "max", daysAgo: 1),
            SetupChat(Lumi, null, worktree: true, "gpt-6", "max", daysAgo: 3),
            SetupChat(Lumi, null, worktree: true, "claude", "high", daysAgo: 2),
            SetupChat(Search, CodingAgent, worktree: false, "claude", "high", daysAgo: 2),
            SetupChat(Search, CodingAgent, worktree: false, "gpt-6", null, daysAgo: 5),
            // Used once: not a habit yet.
            SetupChat(Search, null, worktree: true, "gpt-6", "max", daysAgo: 1),
            SetupChat(null, null, worktree: false, "default-model", "high", daysAgo: 6),
            SetupChat(null, null, worktree: false, "other-model", null, daysAgo: 7),
            // Outside the window.
            SetupChat(Search, null, worktree: false, "old-model", null, daysAgo: 40),
            SetupChat(Search, null, worktree: false, "old-model", null, daysAgo: 45),
        };

        Assert.Equal(
            [
                (new LaunchpadSetupSpec(Lumi, null, true, null, null), 3),
                (new LaunchpadSetupSpec(Search, CodingAgent, false, null, null), 2),
                (LaunchpadSetupSpec.JustChat, 2),
            ],
            SelectSetups(chats));
    }

    [Fact]
    public void Setups_AlwaysOfferJustChat_AndCountADeletedProjectsChatsAsPlain()
    {
        var removedProject = Guid.NewGuid();
        var chats = new List<Chat>
        {
            SetupChat(Lumi, null, worktree: false, "gpt-6", null, daysAgo: 1),
            SetupChat(Lumi, null, worktree: false, "gpt-6", null, daysAgo: 2),
            // A project that's gone leaves a plain chat, and no worktree to start in.
            SetupChat(removedProject, null, worktree: true, "gpt-6", null, daysAgo: 3),
        };

        Assert.Equal(
            [(new LaunchpadSetupSpec(Lumi, null, false, null, null), 2), (LaunchpadSetupSpec.JustChat, 1)],
            SelectSetups(chats));
        Assert.Equal([(LaunchpadSetupSpec.JustChat, 0)], SelectSetups([]));
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
            limit: 3);

        Assert.Equal([(LaunchpadSetupSpec.JustChat, 0)], setups);
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
    public void Setups_KeptComeFirst_AnIdenticalKeptStarterStandsInForItsPlace_AndJustChatIsAlwaysOffered()
    {
        var uiDev = new LaunchpadSetupSpec(Lumi, null, true, "claude-opus-5.5", "high");
        var fixes = new LaunchpadSetupSpec(Lumi, null, true, null, null);
        var lumiWorktree = new LaunchpadSetupSpec(Lumi, null, true, null, null);
        var lumiLocal = new LaunchpadSetupSpec(Lumi, null, false, null, null);
        var search = new LaunchpadSetupSpec(Search, null, false, null, null);
        var planner = new LaunchpadSetupSpec(null, CodingAgent, false, null, null);
        var learned = new List<(LaunchpadSetupSpec, int)>
        {
            (lumiWorktree, 174), (lumiLocal, 28), (LaunchpadSetupSpec.JustChat, 21), (search, 10), (planner, 4),
        };

        // "Fixes" is the Lumi worktree place renamed, so it shows in that place's stead; UI dev, on its own model, leaves
        // the place alone. A dismissed place stays out. Every kept starter shows, even two that start the same way (a
        // starter kept on a model since retired starts as another does), so none of them ever goes missing.
        Assert.Equal(
            [(uiDev, 0, 0), (fixes, 1, 0), (uiDev, 2, 0), (lumiLocal, -1, 28), (LaunchpadSetupSpec.JustChat, -1, 21), (planner, -1, 4)],
            LaunchpadPlanner.MergeSetups([uiDev, fixes, uiDev], learned, [search.Context], limit: 6));

        // Learned places stop at the limit, but Just chat is never left out, nor dismissed.
        Assert.Equal(
            [(lumiWorktree, -1, 174), (LaunchpadSetupSpec.JustChat, -1, 21)],
            LaunchpadPlanner.MergeSetups([], learned, [LaunchpadSetupSpec.JustChat.Context], limit: 1));

        // Kept starters never count against the learned ones' limit, up to the most the row keeps.
        var many = Enumerable.Range(0, LaunchpadPlanner.MaxKeptSetups + 2)
            .Select(index => uiDev with { ModelId = $"model-{index}" }).ToList();
        Assert.Equal(
            [.. many.Take(LaunchpadPlanner.MaxKeptSetups).Select((spec, index) => (spec, index, 0)), (lumiWorktree, -1, 174)],
            LaunchpadPlanner.MergeSetups(many, learned.Take(1).ToList(), [], limit: 1)
                .Where(entry => !entry.Spec.IsJustChat).ToList());
    }

    [Fact]
    public void TheDraftsStarter_IsTheMostSpecificOneThatFitsIt()
    {
        var uiDev = new LaunchpadSetupSpec(Lumi, null, true, "claude-opus-5.5", "high");
        var opusAnyEffort = new LaunchpadSetupSpec(Lumi, null, true, "claude-opus-5.5", null);
        var fixes = new LaunchpadSetupSpec(Lumi, null, true, null, null);
        var lumiLocal = new LaunchpadSetupSpec(Lumi, null, false, null, null);
        List<LaunchpadSetupSpec> row = [fixes, opusAnyEffort, uiDev, lumiLocal, LaunchpadSetupSpec.JustChat];

        // A chosen model and effort beat a chosen model alone, which beats the default model, wherever they sit.
        Assert.Equal(2, LaunchpadPlanner.IndexOfMatch(row, new LaunchpadSetupSpec(Lumi, null, true, "claude-opus-5.5", "high")));
        Assert.Equal(1, LaunchpadPlanner.IndexOfMatch(row, new LaunchpadSetupSpec(Lumi, null, true, "claude-opus-5.5", "low")));
        // A starter on the default model fits whatever model the draft has.
        Assert.Equal(0, LaunchpadPlanner.IndexOfMatch(row, new LaunchpadSetupSpec(Lumi, null, true, "gpt-6.1-sol", "high")));
        Assert.Equal(3, LaunchpadPlanner.IndexOfMatch(row, new LaunchpadSetupSpec(Lumi, null, false, "claude-opus-5.5", "high")));
        Assert.Equal(4, LaunchpadPlanner.IndexOfMatch(row, new LaunchpadSetupSpec(null, null, false, "gpt-6.1-terra", null)));
        // Somewhere the row has no starter for, or nothing to match.
        Assert.Equal(-1, LaunchpadPlanner.IndexOfMatch(row, new LaunchpadSetupSpec(Search, null, false, "gpt-6.1-sol", null)));
        Assert.Equal(-1, LaunchpadPlanner.IndexOfMatch(row, null));
        // On a tie the first in the row wins.
        Assert.Equal(0, LaunchpadPlanner.IndexOfMatch([fixes, fixes], new LaunchpadSetupSpec(Lumi, null, true, "x", null)));
    }

    [Fact]
    public void TheRow_ShowsWhatFitsInOrder_KeepsTheDraftsStarterInSight_AndDividesKeptFromLearned()
    {
        double[] widths = [100, 100, 100, 100];
        bool[] kept = [true, true, false, false];

        // Everything fits beside the + button: all of it shows.
        Assert.All(LaunchpadPlanner.FitRow(widths, kept, 455, 0, 46, 80, 9, -1), Assert.True);

        // Otherwise what fits beside the stack, in order; the divider counts once a learned starter follows a kept one.
        Assert.Equal([true, true, false, false], LaunchpadPlanner.FitRow(widths, kept, 380, 0, 46, 80, 9, -1));
        Assert.Equal([true, true, true, false], LaunchpadPlanner.FitRow(widths, kept, 389, 0, 46, 80, 9, -1));
        Assert.Equal([true, true, false, false], LaunchpadPlanner.FitRow(widths, kept, 380, 0, 46, 80, 9, 1));

        // The draft's own chip, when no starter fits it, takes its room first.
        Assert.Equal([true, false, false, false], LaunchpadPlanner.FitRow(widths, kept, 380, 120, 46, 80, 9, -1));
    }

    [Fact]
    public void TheDraftsStarter_TakesOnlyALearnedStartersPlace_KeptStartersHoldTheirs()
    {
        double[] widths = [100, 100, 100, 100, 100];
        bool[] oneKept = [true, false, false, false, false];

        // A learned starter the draft started from comes in from the stack in the place of the last learned one shown.
        Assert.Equal([true, true, true, false, false], LaunchpadPlanner.FitRow(widths, oneKept, 400, 0, 46, 80, 9, -1));
        Assert.Equal([true, true, false, false, true], LaunchpadPlanner.FitRow(widths, oneKept, 400, 0, 46, 80, 9, 4));

        // With the row's room all taken by kept starters it stays in the stack (which wears its light): no kept starter
        // ever leaves the row for it, whether it was learned or kept further on.
        bool[] twoKept = [true, true, false, false];
        Assert.Equal([true, true, false, false], LaunchpadPlanner.FitRow([100, 100, 100, 100], twoKept, 380, 0, 46, 80, 9, 3));
        bool[] threeKept = [true, true, true, false];
        Assert.Equal([true, true, false, false], LaunchpadPlanner.FitRow([100, 100, 100, 100], threeKept, 300, 0, 46, 80, 9, 2));
    }

    [Fact]
    public void Keeper_KeepsARecipeOnce_AddsNamedStartersBesideIt_AndLetsThemGo()
    {
        var data = KeeperData();
        var place = new LaunchpadSetupSpec(Lumi, null, true, null, null);

        var kept = LaunchpadSetupKeeper.Keep(data, [], place);
        Assert.Same(kept, Assert.Single(data.PinnedSetups));
        Assert.Null(kept.Name);
        Assert.Same(kept, LaunchpadSetupKeeper.Keep(data, [], place));

        // A starter made in the new starter card joins the kept one with its recipe and never renames it; asking again
        // adds nothing. A name that is just the place's isn't stored, nor are spaces or an icon Lumi doesn't draw.
        var fixes = LaunchpadSetupKeeper.Add(data, place, "  Fixes  ", "bug");
        Assert.Equal([kept, fixes], data.PinnedSetups);
        Assert.Equal(((string?)null, (string?)null, "Fixes", "bug"), (kept.Name, kept.Icon, fixes.Name, fixes.Icon));
        Assert.Same(fixes, LaunchpadSetupKeeper.Add(data, place, "Fixes", "bug"));
        Assert.Same(kept, LaunchpadSetupKeeper.Add(data, place, "Lumi", "not-an-icon"));
        Assert.Equal(2, data.PinnedSetups.Count);

        Assert.True(LaunchpadSetupKeeper.StopKeeping(data, [], place));
        Assert.Equal([fixes], data.PinnedSetups);
        Assert.True(LaunchpadSetupKeeper.StopKeeping(data, fixes));
        Assert.Empty(data.PinnedSetups);
        Assert.False(LaunchpadSetupKeeper.StopKeeping(data, [], place));
    }

    [Fact]
    public void Keeper_EditsAKeptStarterInPlace_KeepsALearnedOne_AndNeverLosesANamedOne()
    {
        var data = KeeperData();
        var place = new LaunchpadSetupSpec(Lumi, null, true, null, null);
        var uiDev = LaunchpadSetupKeeper.Add(data, place with { ModelId = "claude-opus-5.5" }, "UI dev", null);
        var fixes = LaunchpadSetupKeeper.Add(data, place, "Fixes", null);
        List<LaunchpadSetupChoice> row =
        [
            Choice(place with { ModelId = "claude-opus-5.5" }, uiDev),
            Choice(place, fixes),
            Choice(new LaunchpadSetupSpec(Lumi, null, false, null, null), pin: null),
        ];

        // A chosen model on the kept "Fixes" changes it where it stands, name and all.
        var edited = LaunchpadSetupKeeper.Edit(data, row, fixes, place with { ModelId = "gpt-6.1-sol", Effort = "high" });
        Assert.Same(fixes, edited);
        Assert.Equal([uiDev, fixes], data.PinnedSetups);
        Assert.Equal(("gpt-6.1-sol", "high", "Fixes"), (fixes.ModelId, fixes.Effort, fixes.Name));

        // Renaming a learned place keeps it, after the user's others.
        var research = Assert.IsType<LaunchpadSetupPin>(LaunchpadSetupKeeper.Edit(data, row, null, row[2].Spec, name: "Research"));
        Assert.Equal([uiDev, fixes, research], data.PinnedSetups);
        Assert.Equal(("Research", false, null), (research.Name, research.UseWorktree, research.ModelId));

        // Retuning "Fixes" into UI dev's recipe leaves both: each has a name the user gave it.
        LaunchpadSetupKeeper.Edit(data, row, fixes, place with { ModelId = "claude-opus-5.5" });
        Assert.Equal([uiDev, fixes, research], data.PinnedSetups);
        Assert.Equal(("claude-opus-5.5", "Fixes", "UI dev"), (fixes.ModelId, fixes.Name, uiDev.Name));
    }

    [Fact]
    public void Keeper_AnEditMergesOnlyTwinsThatWouldLookTheSame_AndNeverBringsBackOneLetGo()
    {
        // Adir's profile: "Research" (Lumi on the default model, named) and two bare starters kept on Lumi with models.
        var data = KeeperData();
        var lumi = new LaunchpadSetupSpec(Lumi, null, false, null, null);
        var onSol = lumi with { ModelId = "gpt-6.1-sol", Effort = "high" };
        var research = LaunchpadSetupKeeper.Add(data, lumi, "Research", null);
        var opus = LaunchpadSetupKeeper.Keep(data, [], lumi with { ModelId = "claude-opus-5.5", Effort = "high" });
        var sol = LaunchpadSetupKeeper.Keep(data, [], onSol);

        // Opus back on the default model now starts as "Research" does: both stay, Research keeps its name.
        LaunchpadSetupKeeper.Edit(data, [], opus, lumi);
        Assert.Equal([research, opus, sol], data.PinnedSetups);

        // Renaming Research beside that bare twin, giving it an icon, or moving it onto Sol's recipe never costs a bare
        // starter: Research's name tells them apart, as the new starter card adds named ones beside their recipe's others.
        LaunchpadSetupKeeper.Edit(data, [], research, lumi, name: "Deep research", icon: "search");
        LaunchpadSetupKeeper.Edit(data, [], research, onSol);
        Assert.Equal([research, opus, sol], data.PinnedSetups);
        Assert.Equal(("Deep research", "search", "gpt-6.1-sol"), (research.Name, research.Icon, research.ModelId));

        // Opus moved onto Sol's recipe too: two bare starters that would look the same become one.
        LaunchpadSetupKeeper.Edit(data, [], opus, onSol);
        Assert.Equal([research, opus], data.PinnedSetups);

        // A starter let go meanwhile (on the phone, say) is not kept again by an edit from a card still showing it.
        Assert.True(LaunchpadSetupKeeper.StopKeeping(data, opus));
        Assert.Null(LaunchpadSetupKeeper.Edit(data, [], opus, lumi, name: "Again"));
        Assert.Equal([research], data.PinnedSetups);
    }

    [Fact]
    public void Keeper_DismissesPlacesButNeverJustChat()
    {
        var data = KeeperData();
        var search = new LaunchpadSetupSpec(Search, null, false, "gpt-6.1-sol", null);

        Assert.True(LaunchpadSetupKeeper.Hide(data, search));
        Assert.False(LaunchpadSetupKeeper.Hide(data, search with { ModelId = null }));
        var hidden = Assert.Single(data.HiddenSetups);
        Assert.Equal(((Guid?)Search, (string?)null), (hidden.ProjectId, hidden.ModelId));
        Assert.False(LaunchpadSetupKeeper.Hide(data, LaunchpadSetupSpec.JustChat with { ModelId = "gpt-6.1-sol" }));
        Assert.Single(data.HiddenSetups);
    }

    private static AppData KeeperData() => new()
    {
        Projects = [new Project { Id = Lumi, Name = "Lumi" }, new Project { Id = Search, Name = "Fluent Search" }],
    };

    private static LaunchpadSetupChoice Choice(LaunchpadSetupSpec spec, LaunchpadSetupPin? pin)
        => new(spec, pin?.Name ?? "Lumi", "Lumi", "") { Pin = pin };

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

    private static List<(LaunchpadSetupSpec Spec, int Uses)> SelectSetups(IEnumerable<Chat> chats)
        => LaunchpadPlanner.SelectSetups(
            chats,
            new HashSet<Guid>(),
            Now,
            projectId => projectId == Lumi || projectId == Search,
            agentId => agentId == CodingAgent,
            LaunchpadPlanner.MaxLearnedSetups);

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
