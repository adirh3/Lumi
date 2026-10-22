using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Context;
using Lumi.ViewModels;
using Xunit;
using ChatMessage = Lumi.Models.ChatMessage;

namespace Lumi.Tests;

public sealed class ContextCuratorTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(3);

    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, Offset);

    // ───────────── schedule ─────────────

    [Fact]
    public void ADayStartsAtFiveInTheMorning()
    {
        Assert.Equal(At(22, 5), ContextCuratorService.LatestDayStart(At(22, 8, 57)));
        Assert.Equal(At(22, 5), ContextCuratorService.LatestDayStart(At(22, 5)));
        Assert.Equal(At(21, 5), ContextCuratorService.LatestDayStart(At(22, 4, 59)));
    }

    [Fact]
    public void ACatchUpReviewsOneDayPerStep()
    {
        Assert.Equal(
            [(At(19, 22), At(20, 5)), (At(20, 5), At(21, 5)), (At(21, 5), At(22, 5)), (At(22, 5), At(22, 8, 57))],
            ContextCuratorService.SplitIntoSteps(At(19, 22), At(22, 8, 57)));
        Assert.Equal([(At(21, 5), At(22, 5))], ContextCuratorService.SplitIntoSteps(At(21, 5), At(22, 5)));
        Assert.Equal([(At(22, 6), At(22, 9))], ContextCuratorService.SplitIntoSteps(At(22, 6), At(22, 9)));
    }

    [Fact]
    public async Task TheNightlyRunReviewsYesterdayForActiveProjectsThenAcrossProjects()
    {
        var lumi = new Project { Name = "Lumi" };
        var finance = new Project { Name = "Finance" };
        var release = ChatIn(lumi, "Release",
            (At(21, 14), "user", "Let's ship 1.0 next week"),
            (At(21, 14, 5), "assistant", "I'll prepare the release notes."),
            (At(22, 7), "user", "Is the build green?"));
        var outside = ChatIn(null, "Dinner plans", (At(21, 20), "user", "Book a table for Friday"));
        var data = new AppData
        {
            Projects = [lumi, finance],
            Chats = [release, outside],
            Briefs =
            [
                new ContextBrief { ProjectId = lumi.Id, CuratedThrough = At(21, 5) },
                new ContextBrief { ProjectId = finance.Id, CuratedThrough = At(21, 5) },
                new ContextBrief { ProjectId = Guid.NewGuid(), Content = "A deleted project", CuratedThrough = At(21, 5) },
                new ContextBrief { CuratedThrough = At(21, 5) },
            ],
        };
        var reviews = new List<ContextCuratorScope>();
        string? globalPrompt = null;
        var curator = CreateCurator(new DataStore(data), () => At(22, 8), scope =>
        {
            lock (reviews)
                reviews.Add(scope);
            if (scope.Project is not null)
                scope.UpdateBrief("## Now\n- Shipping 1.0 next week (\"Release\", Oct 21)");
            else
                globalPrompt = scope.BuildPrompt();
            return Task.CompletedTask;
        });

        Assert.True(curator.IsDue(At(22, 8)));
        Assert.True(await curator.RunAsync(everything: false, CancellationToken.None));

        Assert.Equal(["Lumi", "All projects"], reviews.Select(scope => scope.Name));
        var project = reviews[0];
        Assert.Equal((At(21, 5), At(22, 5)), (project.From, project.To));
        var reviewed = Assert.Single(project.Chats);
        Assert.Equal(2, reviewed.Window.Count); // this morning's message waits for tonight
        Assert.Equal("Dinner plans", Assert.Single(reviews[1].Chats).Chat.Title);
        Assert.Contains("### Lumi (", globalPrompt);
        Assert.Contains("updated since your last run", globalPrompt);

        Assert.Equal(3, data.Briefs.Count); // the deleted project's brief is gone
        Assert.All(data.Briefs, brief => Assert.Equal(At(22, 5), brief.CuratedThrough));
        Assert.Equal("", data.Briefs.Single(brief => brief.ProjectId == finance.Id).Content);
        Assert.False(curator.IsDue(At(22, 23)));
        Assert.True(curator.IsDue(At(23, 5)));
    }

    [Fact]
    public async Task TheFirstRunReviewsTheLastThreeDaysADayAtATime()
    {
        var lumi = new Project { Name = "Lumi" };
        var chat = ChatIn(lumi, "Release",
            (At(18, 12), "user", "Plan the 1.0 launch"),
            (At(19, 12), "user", "Draft the changelog"),
            (At(20, 12), "user", "Fix the installer"),
            (At(21, 12), "user", "Tag the release"));
        var data = new AppData { Projects = [lumi], Chats = [chat] };
        var reviews = new List<ContextCuratorScope>();
        var curator = CreateCurator(new DataStore(data), () => At(22, 8), scope =>
        {
            lock (reviews)
                reviews.Add(scope);
            return Task.CompletedTask;
        });

        Assert.True(curator.IsDue(At(22, 8)));
        Assert.True(await curator.RunAsync(everything: false, CancellationToken.None));

        var days = reviews.Where(scope => scope.Project is not null).ToList();
        Assert.Equal([(At(19, 5), At(20, 5)), (At(20, 5), At(21, 5)), (At(21, 5), At(22, 5))], days.Select(scope => (scope.From, scope.To)));
        Assert.Single(days[0].Chats[0].Before); // what came before the lookback is earlier history, not new activity
        Assert.Equal(2, data.Briefs.Count);
    }

    [Fact]
    public async Task AFailedScopeKeepsItsPlaceAndIsRetriedAloneHoursLater()
    {
        var lumi = new Project { Name = "Lumi" };
        var blast = new Project { Name = "Blast" };
        var data = new AppData
        {
            Settings = new UserSettings { UserName = "Adir" },
            Projects = [lumi, blast],
            Chats =
            [
                ChatIn(lumi, "Release", (At(21, 14), "user", "Ship 1.0")),
                ChatIn(blast, "Theme", (At(21, 15), "user", "Always use a dark theme, everywhere")),
            ],
            Briefs =
            [
                new ContextBrief { ProjectId = lumi.Id, CuratedThrough = At(21, 5) },
                new ContextBrief { ProjectId = blast.Id, CuratedThrough = At(21, 5) },
                new ContextBrief { CuratedThrough = At(21, 5) },
            ],
        };
        var clock = At(22, 8);
        var offline = true;
        var reviews = new List<string>();
        string? globalPrompt = null;
        var curator = CreateCurator(new DataStore(data), () => clock, scope =>
        {
            lock (reviews)
                reviews.Add(scope.Name);
            if (scope.Project is null)
            {
                globalPrompt = scope.BuildPrompt();
                return Task.CompletedTask;
            }

            if (scope.Name == "Blast")
            {
                if (offline)
                    throw new InvalidOperationException("offline");
                scope.ManageMemories("create", null, "Prefers dark themes", "Adir wants dark themes in every app.",
                    "Preferences", "global", "\"Theme\", Oct 21: 'Always use a dark theme, everywhere'", null);
            }

            scope.UpdateBrief("## Now\n- Working on it (\"Release\", Oct 21)");
            return Task.CompletedTask;
        });

        Assert.False(await curator.RunAsync(everything: false, CancellationToken.None));
        Assert.True(curator.LastRunFailed);
        Assert.Equal("All projects", reviews[^1]);
        Assert.Equal(At(21, 5), data.Briefs.Single(brief => brief.ProjectId == blast.Id).CuratedThrough);
        Assert.Equal(At(22, 5), data.Briefs.Single(brief => brief.ProjectId == lumi.Id).CuratedThrough);
        Assert.False(curator.IsDue(At(22, 10)));
        Assert.True(curator.IsDue(At(22, 11, 1)));

        clock = At(22, 11, 1);
        offline = false;
        reviews.Clear();
        Assert.True(await curator.RunAsync(everything: false, CancellationToken.None));

        // Only the failed project is reviewed again; its proposal still reaches the all-projects curator.
        Assert.Equal(["Blast", "All projects"], reviews);
        Assert.Contains("Prefers dark themes", globalPrompt);
        Assert.False(curator.LastRunFailed);
        Assert.All(data.Briefs, brief => Assert.Equal(At(22, 5), brief.CuratedThrough));
        Assert.False(curator.IsDue(At(22, 23)));
    }

    [Fact]
    public async Task ProposalsWaitForTheAllProjectsReviewWhenItFails()
    {
        var blast = new Project { Name = "Blast" };
        var data = new AppData
        {
            Settings = new UserSettings { UserName = "Adir" },
            Projects = [blast],
            Chats =
            [
                ChatIn(blast, "Theme", (At(21, 15), "user", "Always use a dark theme, everywhere")),
                ChatIn(null, "Groceries", (At(22, 10), "user", "Add milk to the list")),
            ],
            Briefs =
            [
                new ContextBrief { ProjectId = blast.Id, CuratedThrough = At(21, 5) },
                new ContextBrief { CuratedThrough = At(21, 5) },
            ],
        };
        var clock = At(22, 8);
        var offline = true;
        string? globalPrompt = null;
        var curator = CreateCurator(new DataStore(data), () => clock, scope =>
        {
            if (scope.Project is not null)
            {
                scope.ManageMemories("create", null, "Prefers dark themes", "Adir wants dark themes in every app.",
                    "Preferences", "global", "\"Theme\", Oct 21: 'Always use a dark theme, everywhere'", null);
                scope.UpdateBrief("## Now\n- Dark theme everywhere (\"Theme\", Oct 21)");
                return Task.CompletedTask;
            }

            if (offline)
                throw new InvalidOperationException("offline");
            globalPrompt = scope.BuildPrompt();
            return Task.CompletedTask;
        });

        Assert.False(await curator.RunAsync(everything: false, CancellationToken.None));
        Assert.Equal(At(22, 5), data.Briefs.Single(brief => brief.ProjectId == blast.Id).CuratedThrough);

        clock = At(22, 11, 1);
        offline = false;
        Assert.True(await curator.RunAsync(everything: false, CancellationToken.None));

        // The retry has no chats of its own to review, but still decides the proposal and reads the changed brief.
        Assert.Contains("Prefers dark themes", globalPrompt);
        Assert.Contains("### Blast (", globalPrompt);
        Assert.Contains("updated since your last run", globalPrompt);
        Assert.All(data.Briefs, brief => Assert.Equal(At(22, 5), brief.CuratedThrough));

        // Decided proposals aren't brought up again: this review is for this morning's chat outside projects.
        globalPrompt = null;
        Assert.True(await curator.RunNowAsync());
        Assert.NotNull(globalPrompt);
        Assert.Contains("Groceries", globalPrompt);
        Assert.DoesNotContain("Prefers dark themes", globalPrompt);
        Assert.DoesNotContain("updated since your last run", globalPrompt);
    }

    [Fact]
    public async Task ProposalsLeftForAFailedFinalReviewAreRetriedHoursLater()
    {
        var blast = new Project { Name = "Blast" };
        var data = new AppData
        {
            Settings = new UserSettings { UserName = "Adir" },
            Projects = [blast],
            Chats = [ChatIn(blast, "Theme", (At(21, 15), "user", "Always use a dark theme, everywhere"))],
            Briefs =
            [
                new ContextBrief { ProjectId = blast.Id, CuratedThrough = At(21, 5) },
                new ContextBrief { CuratedThrough = At(22, 5) }, // the all-projects review already covered yesterday
            ],
        };
        var clock = At(22, 8);
        var offline = true;
        string? globalPrompt = null;
        var curator = CreateCurator(new DataStore(data), () => clock, scope =>
        {
            if (scope.Project is not null)
            {
                scope.ManageMemories("create", null, "Prefers dark themes", "Adir wants dark themes in every app.",
                    "Preferences", "global", "\"Theme\", Oct 21: 'Always use a dark theme, everywhere'", null);
                return Task.CompletedTask;
            }

            if (offline)
                throw new InvalidOperationException("offline");
            globalPrompt = scope.BuildPrompt();
            return Task.CompletedTask;
        });

        Assert.False(await curator.RunAsync(everything: false, CancellationToken.None));
        Assert.All(data.Briefs, brief => Assert.Equal(At(22, 5), brief.CuratedThrough));
        Assert.False(curator.IsDue(At(22, 10)));
        Assert.True(curator.IsDue(At(22, 11, 1)));

        clock = At(22, 11, 1);
        offline = false;
        Assert.True(await curator.RunAsync(everything: false, CancellationToken.None));
        Assert.Contains("Prefers dark themes", globalPrompt);
        Assert.Equal(At(22, 11, 1), curator.LastRunAt);
        Assert.False(curator.IsDue(At(22, 23)));
    }

    [Fact]
    public async Task UpdateNowReviewsUpToTheMomentAndOnlyWhenTurnedOn()
    {
        var lumi = new Project { Name = "Lumi" };
        var data = new AppData
        {
            Projects = [lumi],
            Chats = [ChatIn(lumi, "Release", (At(22, 13), "user", "The build is green"))],
            Briefs =
            [
                new ContextBrief { ProjectId = lumi.Id, CuratedThrough = At(22, 5) },
                new ContextBrief { CuratedThrough = At(22, 5) },
            ],
        };
        var reviews = new List<ContextCuratorScope>();
        var curator = CreateCurator(new DataStore(data), () => At(22, 14), scope =>
        {
            lock (reviews)
                reviews.Add(scope);
            return Task.CompletedTask;
        });

        Assert.False(curator.IsDue(At(22, 14)));
        data.Settings.EnableDailyBriefs = false;
        Assert.False(await curator.RunNowAsync());
        Assert.Empty(reviews);

        data.Settings.EnableDailyBriefs = true;
        Assert.True(await curator.RunNowAsync());
        var review = Assert.Single(reviews);
        Assert.Equal((At(22, 5), At(22, 14)), (review.From, review.To));
        Assert.All(data.Briefs, brief => Assert.Equal(At(22, 14), brief.CuratedThrough));
    }

    // ───────────── a curator's tools ─────────────

    [Fact]
    public void AProjectCuratorWritesItsProjectsMemoriesAndOnlyProposesGlobalOnes()
    {
        var lumi = new Project { Name = "Lumi" };
        var color = new Memory { Key = "Favorite color", Content = "Adir's favorite color is blue.", Category = "Preferences" };
        var policy = new Memory
        {
            Key = "Release policy", Content = "Lumi releases from main.", Category = "Project",
            Scope = MemoryScopes.Project, ProjectId = lumi.Id,
        };
        var store = new DataStore(new AppData { Projects = [lumi], Memories = [color, policy] });
        var proposals = new List<CuratorProposal>();
        var scope = CreateScope(store, lumi, proposals);
        const string evidence = "\"Release\", Oct 21: 'we ship weekly'";

        Assert.StartsWith("Created", scope.ManageMemories("create", null, "Ship cadence", "Lumi ships a release every week.", "Project", null, evidence, null));
        var created = Assert.Single(store.Data.Memories, memory => memory.Key == "Ship cadence");
        Assert.Equal(lumi.Id, created.ProjectId);
        Assert.Equal(MemoryScopes.Project, created.Scope);
        Assert.Equal(MemorySources.Curator, created.Source);

        Assert.Contains("evidence is required", scope.ManageMemories("create", null, "Unproven", "Something.", "Project", null, null, null));
        Assert.Contains("Too long", scope.ManageMemories("create", null, "Rambling", new string('x', 500), "Project", null, evidence, null));

        Assert.StartsWith("Proposed", scope.ManageMemories("create", null, "Works weekends", "Adir often works on weekends.", "Work", "global", evidence, null));
        Assert.Equal("Works weekends", Assert.Single(proposals).Key);
        Assert.DoesNotContain(store.Data.Memories, memory => memory.Key == "Works weekends");

        Assert.Contains("global memory", scope.ManageMemories("update", color.Id.ToString("N")[..6], null, "Green.", null, null, evidence, null));
        Assert.Equal("Adir's favorite color is blue.", color.Content);

        Assert.Contains("existed", scope.ManageMemories("create", null, "Release policy", "Lumi releases from a release branch.", "Project", null, evidence, null));
        Assert.Equal("Lumi releases from a release branch.", policy.Content);
        Assert.Equal(MemorySources.Curator, policy.Source);

        Assert.Contains("reason is required", scope.ManageMemories("delete", policy.Id.ToString("N")[..6], null, null, null, null, null, null));
        Assert.StartsWith("Deleted", scope.ManageMemories("delete", policy.Id.ToString("N")[..6], null, null, null, null, null, "obsolete"));
        Assert.DoesNotContain(policy, store.Data.Memories);
        Assert.Equal(3, scope.MemoryChanges);
    }

    [Fact]
    public void TheAllProjectsCuratorLeavesProjectMemoriesToTheirProjects()
    {
        var lumi = new Project { Name = "Lumi" };
        var policy = new Memory
        {
            Key = "Release policy", Content = "Lumi releases from main.", Category = "Project",
            Scope = MemoryScopes.Project, ProjectId = lumi.Id,
        };
        var store = new DataStore(new AppData { Projects = [lumi], Memories = [policy] });
        var scope = CreateScope(store, project: null);

        Assert.Contains("project memory", scope.ManageMemories("delete", policy.Id.ToString("N")[..6], null, null, null, null, null, "obsolete"));
        Assert.StartsWith("Created", scope.ManageMemories("create", null, "Favorite color", "Adir's favorite color is blue.", "Preferences", null, "\"Theme\", Oct 21", null));
        Assert.Null(Assert.Single(store.Data.Memories, memory => memory.Key == "Favorite color").ProjectId);
    }

    [Fact]
    public void WithoutAutomaticMemoriesACuratorOnlyWritesItsBrief()
    {
        var store = new DataStore(new AppData());
        var scope = CreateScope(store, project: null, canWriteMemories: false);

        Assert.Equal(["read_chat", "search_chats", "update_brief"], scope.BuildTools().Select(tool => tool.Name));
        Assert.Contains("turned off", scope.ManageMemories("create", null, "Key", "Content.", null, null, "evidence", null));
    }

    [Fact]
    public void UpdateBriefKeepsTheLimitAndReplacesTheUsersEdits()
    {
        var lumi = new Project { Name = "Lumi" };
        var store = new DataStore(new AppData { Projects = [lumi] });
        var scope = CreateScope(store, lumi);
        scope.Brief.Content = "Edited by hand";
        scope.Brief.EditedByUser = true;

        Assert.StartsWith("Too long", scope.UpdateBrief(new string('x', ContextCuratorPrompts.ProjectBriefLimit + 1)));
        Assert.Equal("Edited by hand", scope.Brief.Content);
        Assert.False(scope.BriefChanged);

        Assert.Equal("Brief saved.", scope.UpdateBrief("## Now\n- Shipping 1.0 next week (\"Release\", Oct 21)"));
        Assert.False(scope.Brief.EditedByUser);
        Assert.True(scope.BriefChanged);
        Assert.Contains("don't end with a source", scope.UpdateBrief("## Now\n- Shipping 1.0 next week"));
    }

    [Fact]
    public void TheCuratorKeepsWhatTheOwnerChangesInTheBriefWhileItWorks()
    {
        var lumi = new Project { Name = "Lumi" };
        var store = new DataStore(new AppData { Projects = [lumi] });
        var scope = CreateScope(store, lumi);
        scope.Brief.Content = "## Now\n- Shipping 1.0 (\"Release\", Oct 20)";
        scope.BuildPrompt();

        // Saved on the Memories page while the session reads the chats.
        scope.Brief.Content = "## Now\n- Shipping 1.0 on Monday (as of Oct 21)";
        scope.Brief.EditedByUser = true;
        var answer = scope.UpdateBrief("## Now\n- Shipping 1.0 next week (\"Release\", Oct 21)");

        Assert.StartsWith("Not saved: Adir edited this brief while you were working.", answer);
        Assert.EndsWith("Shipping 1.0 on Monday (as of Oct 21)", answer);
        Assert.Equal("## Now\n- Shipping 1.0 on Monday (as of Oct 21)", scope.Brief.Content);
        Assert.True(scope.Brief.EditedByUser);
        Assert.False(scope.BriefChanged);

        Assert.Equal("Brief saved.", scope.UpdateBrief("## Now\n- Shipping 1.0 on Monday (\"Release\", Oct 21)"));
        Assert.True(scope.BriefChanged);

        scope.Brief.Content = "";
        Assert.StartsWith("Not saved: Adir cleared this brief", scope.UpdateBrief("## Now\n- Shipping 1.0 on Monday (\"Release\", Oct 21)"));
        Assert.Equal("", scope.Brief.Content);
    }

    [Fact]
    public async Task ReadChatReadsEachPartOnceAndStaysInsideTheProject()
    {
        var lumi = new Project { Name = "Lumi" };
        var finance = new Project { Name = "Finance" };
        var release = ChatIn(lumi, "Release",
            (At(21, 14), "user", "Let's ship 1.0 next week"),
            (At(21, 14, 5), "assistant", "I'll prepare the release notes."));
        var budget = ChatIn(finance, "Budget", (At(21, 16), "user", "Track the October budget"));
        var store = new DataStore(new AppData { Projects = [lumi, finance], Chats = [release, budget] });
        var window = new CuratorChat(release, release.Messages, []);
        var scope = CreateScope(store, lumi, chats: [window]);

        var text = await scope.ReadChatAsync(window.ShortId, 1, null, CancellationToken.None);
        Assert.Contains("\"Release\", since your last run: part 1 of 1", text);
        Assert.Contains("Adir: Let's ship 1.0 next week", text);
        Assert.Contains("Lumi: I'll prepare the release notes.", text);
        Assert.Contains("already read part 1", await scope.ReadChatAsync(window.ShortId, 1, null, CancellationToken.None));
        Assert.StartsWith("Unknown chat", await scope.ReadChatAsync(budget.Id.ToString(), 1, null, CancellationToken.None));
    }

    [Fact]
    public void TheDigestTellsTheOwnersWordsFromJobsAndManagers()
    {
        var owner = new ChatMessage { Role = "user", Content = "Ship it" };
        var job = new ChatMessage { Role = "user", Author = "Lumi Job - Daily report", Content = new string('j', 600), Timestamp = At(22, 9) };
        var manager = new ChatMessage { Role = "user", Author = "Lumi Manager · Release", Content = "Status?" };

        Assert.True(ChatDigest.IsOwnerMessage(owner));
        Assert.False(ChatDigest.IsOwnerMessage(job));
        Assert.True(ChatDigest.IsAutomated(manager));

        var turn = Assert.Single(ChatDigest.SplitTurns([job]));
        var rendered = ChatDigest.RenderTurn(turn, "Adir");
        Assert.Contains("Job prompt (Lumi Job - Daily report)", rendered);
        Assert.DoesNotContain(new string('j', 401), rendered);
    }

    // ───────────── briefs on the Memories page ─────────────

    [Fact]
    public void TheMemoriesPageShowsBriefsAndKeepsTheUsersEdits()
    {
        var lumi = new Project { Name = "Lumi" };
        var quiet = new Project { Name = "Quiet" };
        var projectBrief = new ContextBrief
        {
            ProjectId = lumi.Id,
            Content = "## Now\n- Shipping 1.0 next week (\"Release\", Oct 21)\n- Testing nightly builds (\"QA\", Oct 21)\n\n## Decided\n- Weekly releases (\"Release\", Oct 20)",
        };
        var data = new AppData
        {
            Projects = [lumi, quiet],
            Briefs =
            [
                projectBrief,
                new ContextBrief { ProjectId = quiet.Id },
                new ContextBrief { Content = "## Projects\n- Lumi: shipping 1.0 (as of Oct 21)", UpdatedAt = DateTimeOffset.Now.AddDays(-2) },
            ],
        };
        var briefs = new BriefsViewModel(new DataStore(data), curator: null);

        Assert.Equal(2, briefs.Cards.Count);
        Assert.True(briefs.Cards[0].IsGlobal);
        Assert.Equal("Lumi", briefs.Cards[1].Title);
        Assert.Equal("Shipping 1.0 next week\nTesting nightly builds", briefs.Cards[1].Summary);
        Assert.False(briefs.CanUpdateNow);

        briefs.Cards[1].OpenCommand.Execute(null);
        Assert.True(briefs.IsOpen);
        Assert.False(briefs.HasUnsavedChanges);
        briefs.EditContent += "\n- Launch on Monday (as of Oct 22)";
        Assert.True(briefs.ShowSaveBar);
        briefs.SaveCommand.Execute(null);
        Assert.True(projectBrief.EditedByUser);
        Assert.EndsWith("Launch on Monday (as of Oct 22)", projectBrief.Content);
        Assert.False(briefs.HasUnsavedChanges);

        briefs.RequestClearCommand.Execute(null);
        Assert.False(briefs.ShowSaveBar);
        briefs.ConfirmClearCommand.Execute(null);
        Assert.Equal("", projectBrief.Content);
        Assert.False(briefs.IsOpen);
        Assert.Single(briefs.Cards);
    }

    [Fact]
    public void BriefsAreAGroupOfTheMemoriesListWithTheirOwnChip()
    {
        string[] topics = ["Shipping the installer", "Fixing the theme toggle", "Writing release notes", "Isolate Skia's share of the GPU memory gap", "Reviewing pull requests"];
        var projects = topics.Select((_, i) => new Project { Name = $"Project {i + 1}" }).ToList();
        var data = new AppData
        {
            Projects = projects,
            Briefs =
            [
                .. projects.Select((project, i) => new ContextBrief
                {
                    ProjectId = project.Id,
                    Content = $"## Now\n- {topics[i]} (\"Work\", Oct 21)",
                    UpdatedAt = DateTimeOffset.Now.AddHours(-i),
                }),
                new ContextBrief { Content = "## Projects\n- Project 1: shipping (as of Oct 21)" },
            ],
            Memories = [new Memory { Key = "Favorite color", Content = "Adir's favorite color is blue.", Category = "Preferences" }],
        };
        var page = new MemoriesViewModel(new DataStore(data));

        // All: the brief across projects and the three most recently updated, then Show all.
        Assert.True(page.Briefs.ShowGroup);
        Assert.Equal([Loc.Briefs_GlobalTitle, "Project 1", "Project 2", "Project 3"], page.Briefs.VisibleCards.Select(card => card.Title));
        Assert.True(page.Briefs.CanToggleShowAll);
        page.Briefs.ToggleShowAllCommand.Execute(null);
        Assert.Equal(6, page.Briefs.VisibleCards.Count);
        page.Briefs.ToggleShowAllCommand.Execute(null);

        // The chip shows every brief and no memories; a new memory doesn't take it for a category.
        var chip = Assert.Single(page.CategoryFilters, option => option.Key == MemoriesViewModel.BriefsFilter);
        Assert.Equal(6, chip.Count);
        chip.SelectCommand.Execute(null);
        Assert.Empty(page.MemoryGroups);
        Assert.Equal(6, page.Briefs.VisibleCards.Count);
        Assert.False(page.Briefs.CanToggleShowAll);
        Assert.False(page.ShowNoResults);
        page.NewMemoryCommand.Execute(null);
        Assert.Equal(MemoriesViewModel.DefaultCategory, page.EditCategory);
        page.CloseDetailCommand.Execute(null);

        // A category hides them; search finds them by their text.
        page.CategoryFilters.Single(option => option.Key == "Preferences").SelectCommand.Execute(null);
        Assert.False(page.Briefs.ShowGroup);
        page.CategoryFilters[0].SelectCommand.Execute(null);
        page.SearchQuery = "pull requests";
        Assert.Equal(["Project 5"], page.Briefs.VisibleCards.Select(card => card.Title));
        Assert.Empty(page.MemoryGroups);
        Assert.False(page.ShowNoResults);
        page.SearchQuery = "SQLite"; // close to "Isolate", but no brief says it
        Assert.False(page.Briefs.ShowGroup);
        page.SearchQuery = "zebra crossing";
        Assert.False(page.Briefs.ShowGroup);
        Assert.True(page.ShowNoResults);

        // Turned off, the group and its chip go away.
        page.SearchQuery = "";
        data.Settings.EnableDailyBriefs = false;
        page.RefreshFromStore();
        Assert.False(page.Briefs.ShowGroup);
        Assert.DoesNotContain(page.CategoryFilters, option => option.Key == MemoriesViewModel.BriefsFilter);
    }

    [Fact]
    public void AProjectsPageLinksToItsBrief()
    {
        var lumi = new Project { Name = "Lumi" };
        var memory = new Memory { Key = "Favorite color", Content = "Adir's favorite color is blue.", Category = "Preferences" };
        var data = new AppData
        {
            Projects = [lumi],
            Memories = [memory],
            Briefs = [new ContextBrief { ProjectId = lumi.Id, Content = "## Now\n- Shipping 1.0 next week (\"Release\", Oct 21)\n- Testing nightly builds (\"QA\", Oct 21)" }],
        };
        var store = new DataStore(data);
        var projectsPage = new ProjectsViewModel(store);
        var memoriesPage = new MemoriesViewModel(store);
        Guid? requested = null;
        projectsPage.OpenBriefRequested += id => requested = id;

        projectsPage.OpenById(lumi.Id);
        Assert.True(projectsPage.HasProjectBrief);
        Assert.Equal("Shipping 1.0 next week", projectsPage.ProjectBriefSummary);
        projectsPage.OpenBriefCommand.Execute(null);
        Assert.Equal(lumi.Id, requested);

        // It opens in Memories in place of a memory left open there.
        memoriesPage.EditMemoryCommand.Execute(memory);
        memoriesPage.OpenBrief(lumi.Id);
        Assert.False(memoriesPage.IsEditing);
        Assert.Same(data.Briefs[0], memoriesPage.Briefs.SelectedBrief);

        data.Settings.EnableDailyBriefs = false;
        projectsPage.RefreshBriefFacts();
        Assert.False(projectsPage.HasProjectBrief);
    }

    [Fact]
    public void BriefsWithoutMemoriesCanBeSearched_AndTurnedOffLeaveNoFalseNoResults()
    {
        var lumi = new Project { Name = "Lumi" };
        var data = new AppData
        {
            Projects = [lumi],
            Briefs = [new ContextBrief { ProjectId = lumi.Id, Content = "## Now\n- Shipping 1.0 next week (\"Release\", Oct 21)" }],
        };
        var page = new MemoriesViewModel(new DataStore(data));

        Assert.False(page.HasAnyMemories);
        Assert.True(page.HasAnyContent);
        Assert.True(page.Briefs.ShowGroup);
        Assert.False(page.ShowNoResults);

        data.Settings.EnableDailyBriefs = false;
        page.RefreshFromStore();
        Assert.False(page.HasAnyContent);
        Assert.False(page.ShowNoResults);
        Assert.True(page.ShowEmptyState);
    }

    [Fact]
    public void ABriefOfADeletedProjectClosesAndCannotBeReopened()
    {
        var lumi = new Project { Name = "Lumi" };
        var data = new AppData
        {
            Projects = [lumi],
            Briefs = [new ContextBrief { ProjectId = lumi.Id, Content = "## Now\n- Shipping 1.0 next week (\"Release\", Oct 21)" }],
        };
        var briefs = new BriefsViewModel(new DataStore(data), curator: null);
        briefs.OpenFor(lumi.Id);
        var key = Assert.IsType<string>(briefs.NavigationKey);
        Assert.True(briefs.CanRestoreNavigation(key));

        data.Projects.Clear();
        briefs.Refresh();
        Assert.False(briefs.IsOpen);
        briefs.OpenFor(lumi.Id);
        Assert.False(briefs.IsOpen);
        Assert.False(briefs.CanRestoreNavigation(key));
    }

    [Fact]
    public void EditingACuratorMemoryKeepsItTheCurators()
    {
        var curated = new Memory { Key = "Release branch", Content = "Blast is currently on branch version/1.1.0.0.", Category = "Work", Source = MemorySources.Curator };
        var saved = new Memory { Key = "Favorite color", Content = "Adir's favorite color is blue.", Category = "Preferences", Source = "auto" };
        var page = new MemoriesViewModel(new DataStore(new AppData { Memories = [curated, saved] }));

        page.EditMemoryCommand.Execute(curated);
        page.EditContent = "Blast is currently on branch version/1.2.0.0.";
        page.SaveMemoryCommand.Execute(null);
        Assert.Equal(MemorySources.Curator, curated.Source);

        page.EditMemoryCommand.Execute(saved);
        page.EditContent = "Adir's favorite color is green.";
        page.SaveMemoryCommand.Execute(null);
        Assert.Equal("manual", saved.Source);
    }

    // ───────────── helpers ─────────────

    private static ContextCuratorService CreateCurator(DataStore store, Func<DateTimeOffset> now, Func<ContextCuratorScope, Task> review)
        => new(store, copilot: null, new ChatHistoryService(store, searchService: null), now, static () => true,
            (scope, _) => review(scope), checkInterval: TimeSpan.FromHours(1), startupDelay: TimeSpan.FromHours(1));

    private static ContextCuratorScope CreateScope(
        DataStore store,
        Project? project,
        List<CuratorProposal>? proposals = null,
        bool canWriteMemories = true,
        IReadOnlyList<CuratorChat>? chats = null)
    {
        var brief = new ContextBrief { ProjectId = project?.Id };
        store.Data.Briefs.Add(brief);
        return new ContextCuratorScope(store, new ChatHistoryService(store, searchService: null), project, brief, At(21, 5), At(22, 5),
            chats ?? [], proposals ?? [], new HashSet<Guid>(), "Adir", canWriteMemories);
    }

    private static Chat ChatIn(Project? project, string title, params (DateTimeOffset At, string Role, string Text)[] messages)
    {
        var chat = new Chat { Title = title, ProjectId = project?.Id, CreatedAt = messages[0].At, UpdatedAt = messages[^1].At };
        foreach (var (at, role, text) in messages)
            chat.Messages.Add(new ChatMessage { Role = role, Content = text, Timestamp = at });
        chat.MessageCount = chat.Messages.Count;
        return chat;
    }
}
