using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Animation;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

/// <summary>The Workspace: overview sections, plan at a glance, and one panel that hosts every page.</summary>
[Collection("Headless UI")]
public sealed class WorkspaceTests
{
    // ── Sections ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void SectionPreviewsTheFirstRowsUntilTheOverviewNarrowsToIt()
    {
        Loc.Load("en");
        var section = new WorkspaceSection<string>(WorkspaceCategory.Files, 3, static (item, query) => item.Contains(query, StringComparison.OrdinalIgnoreCase));
        WorkspaceCategory? requested = null;
        section.ShowAllRequested = category => requested = category;

        section.SetItems(["alpha", "beta", "gamma", "delta", "epsilon"]);

        Assert.Equal(["alpha", "beta", "gamma"], section.Items);
        Assert.Equal(5, section.TotalCount);
        Assert.Equal("5", section.CountLabel);
        Assert.True(section.HasMore);
        Assert.Equal("Show all 5", section.ShowAllLabel);

        // "Show all" asks the overview to narrow to this kind…
        section.ShowAllCommand.Execute(null);
        Assert.Equal(WorkspaceCategory.Files, requested);

        // …which lists every row.
        section.SetFocus(WorkspaceCategory.Files);
        Assert.Equal(5, section.Items.Count);
        Assert.False(section.HasMore);
        Assert.True(section.IsShown);
        Assert.True(section.IsFocused);

        // Narrowed to another kind, the section steps aside.
        section.SetFocus(WorkspaceCategory.Links);
        Assert.False(section.IsShown);
        Assert.False(section.IsFocused);
        Assert.Equal(["alpha", "beta", "gamma"], section.Items);

        section.SetFocus(WorkspaceCategory.All);
        Assert.True(section.IsShown);
        Assert.True(section.HasMore);
    }

    [Fact]
    public void SectionSearchListsEveryMatchAndHidesWhenNothingMatches()
    {
        Loc.Load("en");
        var section = new WorkspaceSection<string>(WorkspaceCategory.Files, 2, static (item, query) => item.Contains(query, StringComparison.OrdinalIgnoreCase));
        section.SetItems(["report.docx", "report-v2.docx", "report-final.docx", "notes.md"]);

        section.SetQuery("report");

        Assert.Equal(3, section.Items.Count); // searching ignores the preview limit
        Assert.False(section.HasMore);
        Assert.Equal("3", section.CountLabel);
        Assert.Equal(4, section.TotalCount);

        section.SetQuery("missing");

        Assert.False(section.HasItems);
        Assert.Empty(section.Items);

        section.SetQuery("");

        Assert.Equal(["report.docx", "report-v2.docx"], section.Items);
    }

    // ── Plan at a glance ─────────────────────────────────────────────────────────────

    [Fact]
    public void PlanSummaryReadsTitleTaskProgressAndNextSteps()
    {
        var summary = WorkspacePlanSummary.Parse("""
            # Plan: Ship the workspace ##

            Rebuild the side panel around one overview.

            ```text
            - [ ] not a task, it's inside a fence
            ```

            ## Todos
            - [x] Design the overview
            - [X] Build the **panel** controller
            - [ ] Wire [the header](https://example.com) toggle
            - [ ] Test on Linux
            - [ ] Ship C#
            """);

        Assert.Equal("Plan: Ship the workspace", summary.Title);
        Assert.Equal("Rebuild the side panel around one overview.", summary.Summary);
        Assert.True(summary.HasTasks);
        Assert.Equal(2, summary.CompletedCount);
        Assert.Equal(5, summary.TaskCount);
        Assert.Equal(
            ["Wire the header toggle", "Test on Linux", "Ship C#"],
            summary.UpcomingSteps(3).Select(static step => step.Text));
        Assert.All(summary.UpcomingSteps(3), static step => Assert.True(step.IsTask));
    }

    [Fact]
    public void PlanSummaryFallsBackToBulletsWithoutTasks()
    {
        var summary = WorkspacePlanSummary.Parse("# Debug plan\n\n- Render every item.\n1. Keep it local.\n");

        Assert.Equal("Debug plan", summary.Title);
        Assert.False(summary.HasTasks);
        Assert.Equal(["Render every item.", "Keep it local."], summary.UpcomingSteps(3).Select(static step => step.Text));
        Assert.All(summary.Steps, static step => Assert.True(step.IsBullet));
        Assert.Same(WorkspacePlanSummary.Empty, WorkspacePlanSummary.Parse("  "));
    }

    [Fact]
    public void ChatPlanFeedsTheOverviewCard()
    {
        Loc.Load("en");
        using var chatVm = new ChatViewModel(new DataStore(CreateAppData()), TestCopilot.Shared);

        Assert.False(chatVm.ShowWorkspacePlan);

        chatVm.HasPlan = true;
        chatVm.PlanContent = "# Launch\n\n- [x] Draft\n- [ ] Review\n- [ ] Publish";

        Assert.True(chatVm.ShowWorkspacePlan);
        Assert.True(chatVm.HasWorkspaceContent);
        Assert.Equal("Launch", chatVm.PlanTitle);
        Assert.Equal("1 of 3 done", chatVm.PlanProgressLabel);
        Assert.True(chatVm.HasPlanProgress);
        Assert.Equal(100d / 3, chatVm.PlanProgress, 3);
        Assert.Equal(["Review", "Publish"], chatVm.PlanSteps.Select(static step => step.Text));

        chatVm.WorkspaceSearchText = "publish";
        Assert.True(chatVm.ShowWorkspacePlan);
        chatVm.WorkspaceSearchText = "unrelated";
        Assert.False(chatVm.ShowWorkspacePlan);
        Assert.True(chatVm.WorkspaceSearchHasNoMatches);
    }

    // ── Icons ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WorkspaceGlyphsKeepTheFullTwentyPixelFrameAndCoverEveryKind()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            var glyphs = typeof(WorkspaceIcons).GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(static property => property.PropertyType == typeof(Geometry))
                .ToDictionary(static property => property.Name, static property => (Geometry)property.GetValue(null)!);

            // Framed to the whole grid, every glyph renders at one optical size in any icon box.
            Assert.NotEmpty(glyphs);
            Assert.All(glyphs, static glyph => Assert.Equal(new Rect(0, 0, 20, 20), glyph.Value.Bounds));

            Assert.All(Enum.GetValues<WorkspacePage>(), static page => Assert.NotNull(WorkspaceIcons.For(page)));
            Assert.All(Enum.GetValues<WorkspaceCategory>(), static category => Assert.NotNull(WorkspaceIcons.For(category)));
            Assert.All(Enum.GetValues<WorkspaceActivityKind>(), static kind => Assert.NotNull(WorkspaceIcons.For(kind)));
        }, CancellationToken.None);
    }

    [Fact]
    public void GitRefreshKeepsTheLastChangesUntilGitAnswers()
    {
        Loc.Load("en");
        using var chatVm = new ChatViewModel(new DataStore(CreateAppData()), TestCopilot.Shared);
        chatVm.IsCodingProject = true;
        chatVm.GitChangedFileCount = 1;
        chatVm.GitChangedFiles.Add(GitFile("docs/a.md"));
        chatVm.SelectWorkspaceCategoryCommand.Execute(WorkspaceCategory.Changes);
        Assert.Equal(WorkspaceCategory.Changes, chatVm.WorkspaceCategory);

        // A refresh empties the live list while git runs (the order RefreshCodingProjectState uses).
        chatVm.IsRefreshingGitStatus = true;
        chatVm.GitChangedFileCount = 0;
        chatVm.GitChangedFiles.Clear();

        Assert.True(chatVm.ShowWorkspaceGit);
        Assert.Equal(WorkspaceCategory.Changes, chatVm.WorkspaceCategory);
        Assert.Single(chatVm.WorkspaceGitFiles.Items);

        // Git answers; the workspace follows once the refresh settles.
        chatVm.GitChangedFileCount = 2;
        chatVm.GitChangedFiles.Add(GitFile("docs/a.md"));
        chatVm.GitChangedFiles.Add(GitFile("docs/b.md"));
        Assert.Single(chatVm.WorkspaceGitFiles.Items);
        chatVm.IsRefreshingGitStatus = false;

        Assert.Equal(2, chatVm.WorkspaceGitFiles.TotalCount);
        Assert.Equal(WorkspaceCategory.Changes, chatVm.WorkspaceCategory);

        static GitFileChangeViewModel GitFile(string path) => new(new GitFileChange
        {
            RelativePath = path,
            FullPath = Path.Combine(Path.GetTempPath(), path),
            Kind = GitChangeKind.Modified,
            StatusCode = "M",
            RepoRoot = Path.GetTempPath(),
            RepoRelativePath = path,
        });
    }

    // ── Category bar: every kind one click away ──────────────────────────────────────

    [Fact]
    public void CategoryBarNarrowsTheOverviewToOneKind()
    {
        Loc.Load("en");
        using var chatVm = new ChatViewModel(new DataStore(CreateAppData()), TestCopilot.Shared);
        chatVm.HasPlan = true;
        chatVm.PlanContent = "# Plan\n\n- [ ] Ship";
        for (var i = 1; i <= 5; i++)
            chatVm.Messages.Add(new ChatMessageViewModel(new ChatMessage { Role = "user", Content = $"Ask {i}" }));
        chatVm.Messages.Add(new ChatMessageViewModel(new ChatMessage
        {
            Role = "assistant",
            Content = "Read [the guide](https://example.com/guide).",
        }));
        chatVm.RebuildTranscript();

        // Only kinds with content get a chip, with their counts.
        Assert.Equal(
            [WorkspaceCategory.All, WorkspaceCategory.Plan, WorkspaceCategory.Links, WorkspaceCategory.Messages],
            chatVm.WorkspaceCategories.Where(static chip => chip.IsAvailable).Select(static chip => chip.Category));
        Assert.Equal("5", Chip(WorkspaceCategory.Messages).CountLabel);
        Assert.True(Chip(WorkspaceCategory.All).IsSelected);
        Assert.Equal(3, chatVm.WorkspaceMessages.Items.Count); // a preview beside everything else

        chatVm.SelectWorkspaceCategoryCommand.Execute(WorkspaceCategory.Messages);

        Assert.True(Chip(WorkspaceCategory.Messages).IsSelected);
        Assert.False(Chip(WorkspaceCategory.All).IsSelected);
        Assert.Equal(5, chatVm.WorkspaceMessages.Items.Count); // every ask
        Assert.True(chatVm.WorkspaceMessages.IsShown);
        Assert.False(chatVm.WorkspaceLinks.IsShown);
        Assert.False(chatVm.ShowWorkspacePlan);

        // The selected chip again shows everything.
        chatVm.SelectWorkspaceCategoryCommand.Execute(WorkspaceCategory.Messages);
        Assert.Equal(WorkspaceCategory.All, chatVm.WorkspaceCategory);
        Assert.True(chatVm.ShowWorkspacePlan);
        Assert.True(chatVm.WorkspaceLinks.IsShown);

        // The plan is one thing: its chip opens the plan page and leaves the overview as it was.
        var planRequests = 0;
        chatVm.PlanShowRequested += () => planRequests++;
        chatVm.SelectWorkspaceCategoryCommand.Execute(WorkspaceCategory.Plan);
        Assert.Equal(1, planRequests);
        Assert.Equal(WorkspaceCategory.All, chatVm.WorkspaceCategory);

        // "Show all" under a preview narrows to that kind.
        chatVm.WorkspaceMessages.ShowAllCommand.Execute(null);
        Assert.Equal(WorkspaceCategory.Messages, chatVm.WorkspaceCategory);

        // Search counts what matches, within the kind on screen.
        chatVm.WorkspaceSearchText = "Ask 2";
        Assert.Equal("1", Chip(WorkspaceCategory.Messages).CountLabel);
        Assert.Single(chatVm.WorkspaceMessages.Items);
        chatVm.WorkspaceSearchText = "guide";
        Assert.True(chatVm.WorkspaceSearchHasNoMatches); // the guide is a link, not a message
        chatVm.WorkspaceSearchText = "";

        // A kind that empties hands the overview back to everything.
        chatVm.Messages.Clear();
        Assert.Equal(WorkspaceCategory.All, chatVm.WorkspaceCategory);
        Assert.False(Chip(WorkspaceCategory.Messages).IsAvailable);

        WorkspaceCategoryChip Chip(WorkspaceCategory category)
            => chatVm.WorkspaceCategories.Single(chip => chip.Category == category);
    }

    [Fact]
    public async Task CategoryBarStaysOnScreenAndEscapeReturnsToEverything()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = CreateAppData();
            data.Settings.WorkspacePanelOpen = true;
            var dataStore = new DataStore(data);
            using var chatVm = new ChatViewModel(dataStore, TestCopilot.Shared);
            // No links in this transcript: rendered links use Avalonia's shared static underline, which
            // belongs to whichever per-test headless session touched it first.
            for (var i = 1; i <= 6; i++)
                chatVm.Messages.Add(new ChatMessageViewModel(new ChatMessage { Role = "user", Content = $"Ask {i}" }));
            chatVm.Messages.Add(new ChatMessageViewModel(new ChatMessage
            {
                Role = "user",
                Content = "Use a skill",
                ActiveSkills = [new SkillReference { Name = "Code Helper" }],
            }));
            chatVm.RebuildTranscript();
            var workspace = new ChatWorkspaceView { DataContext = chatVm, DataStore = dataStore };
            var window = new Window { Width = 1400, Height = 900, Content = workspace };

            window.Show();
            try
            {
                await PumpAsync();
                var overview = workspace.FindControl<WorkspaceOverview>("WorkspaceOverview")!;
                var bar = overview.FindControl<ItemsControl>("WsCategoryBar")!;
                var scroller = overview.FindControl<ScrollViewer>("WsScroller")!;
                var chips = bar.GetVisualDescendants().OfType<Button>().Where(static chip => chip.IsVisible).ToList();

                // The bar sits above the scrolling content, so no kind is ever scrolled out of reach.
                Assert.DoesNotContain(scroller, bar.GetVisualAncestors());
                Assert.Equal(["All", "Skills", "Messages"], chips.Select(static chip => ((WorkspaceCategoryChip)chip.DataContext!).Title));

                var skillsChip = chips.Single(static chip => ((WorkspaceCategoryChip)chip.DataContext!).Category == WorkspaceCategory.Skills);
                skillsChip.Command!.Execute(skillsChip.CommandParameter);
                await PumpAsync();

                Assert.True(skillsChip.Classes.Contains("selected"));
                var skills = overview.FindControl<WorkspaceSectionView>("WsSkills")!;
                Assert.True(skills.IsVisible);
                Assert.False(overview.FindControl<WorkspaceSectionView>("WsMessages")!.IsVisible);
                // The chosen kind rises into place instead of popping in.
                Assert.Contains(skills.Transitions!, static transition => transition is DoubleTransition { Property.Name: "Opacity" });

                workspace.FindControl<Border>("WorkspacePanel")!.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Escape,
                });

                Assert.Equal(WorkspaceCategory.All, chatVm.WorkspaceCategory);
                Assert.True(workspace.IsWorkspaceOpen); // Escape stepped back, it didn't close anything

                // With animations off, switching kinds is instant.
                dataStore.Data.Settings.ShowAnimations = false;
                var messages = overview.FindControl<WorkspaceSectionView>("WsMessages")!;
                messages.Transitions = null;
                chatVm.SelectWorkspaceCategoryCommand.Execute(WorkspaceCategory.Messages);
                Assert.Null(messages.Transitions);
            }
            finally
            {
                window.Close();
                workspace.Dispose();
            }
        }, CancellationToken.None);
    }

    // ── One panel, every page ────────────────────────────────────────────────────────

    [Fact]
    public async Task PagesOpenInTheWorkspaceAndBackReturnsWhereTheUserWas()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            // "Auto" preference on a window too narrow to auto-open: the workspace starts closed.
            var dataStore = new DataStore(CreateAppData());
            using var chatVm = new ChatViewModel(dataStore, TestCopilot.Shared);
            chatVm.HasPlan = true;
            // No checked tasks: they render Avalonia's shared static strikethrough, which belongs to
            // whichever per-test headless session touched it first.
            chatVm.PlanContent = "# Ship the workspace\n\n- [ ] Design\n- [ ] Build\n- [ ] Test";
            var workspace = new ChatWorkspaceView { DataContext = chatVm, DataStore = dataStore };
            var window = new Window { Width = 1000, Height = 900, Content = workspace };

            window.Show();
            try
            {
                await PumpAsync();
                var grid = workspace.FindControl<Grid>("WorkspaceGrid")!;
                Assert.False(workspace.IsWorkspaceOpen);

                // A page opens the workspace even while the user keeps it closed, as a split view.
                chatVm.OpenWorkspacePlanCommand.Execute(null);
                await PumpAsync();

                Assert.True(workspace.IsWorkspaceOpen);
                Assert.True(chatVm.IsWorkspacePanelOpen);
                Assert.Equal(WorkspacePage.Plan, chatVm.WorkspacePage);
                Assert.Equal("Ship the workspace", HeaderTitle(workspace));
                Assert.Equal("0 of 3 done", HeaderSubtitle(workspace));
                Assert.True(grid.ColumnDefinitions[2].Width.IsStar);
                Assert.True(double.IsPositiveInfinity(grid.ColumnDefinitions[2].MaxWidth));
                Assert.IsType<ScrollViewer>(workspace.FindControl<ContentControl>("WorkspacePageHost")!.Content);

                // A diff opened on top returns to the plan, then to the overview.
                chatVm.ShowDiff(new FileChangeItem(Path.Combine(Path.GetTempPath(), "notes.md"), isCreate: true));
                await PumpAsync();
                Assert.Equal(WorkspacePage.Diff, workspace.WorkspacePage);
                Assert.Equal("notes.md", HeaderTitle(workspace));

                ClickBack(workspace);
                Assert.Equal(WorkspacePage.Plan, workspace.WorkspacePage);

                ClickBack(workspace);
                Assert.Equal(WorkspacePage.Overview, workspace.WorkspacePage);
                Assert.True(workspace.IsWorkspaceOpen);
                Assert.Equal("Workspace", HeaderTitle(workspace));
                Assert.False(workspace.FindControl<Button>("WorkspaceBackButton")!.IsVisible);
                Assert.True(grid.ColumnDefinitions[2].Width.IsAbsolute); // the overview is a compact rail
                // The splitter can only drag the rail within the range it is fitted to (300 px to 45%).
                Assert.Equal(300, grid.ColumnDefinitions[2].MinWidth);
                Assert.Equal(450, grid.ColumnDefinitions[2].MaxWidth);
                Assert.True(workspace.FindControl<WorkspaceOverview>("WorkspaceOverview")!.IsVisible);

                // Closing a panel that only opened for a page leaves the saved preference alone.
                ClickClose(workspace);
                Assert.False(workspace.IsWorkspaceOpen);
                Assert.False(chatVm.IsWorkspacePanelOpen);
                Assert.Null(dataStore.Data.Settings.WorkspacePanelOpen);
            }
            finally
            {
                window.Close();
                workspace.Dispose();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SwitchingChatsMidTransitionLeavesTheOverviewVisible()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = CreateAppData();
            data.Settings.WorkspacePanelOpen = true;
            var dataStore = new DataStore(data);
            using var chatVm = new ChatViewModel(dataStore, TestCopilot.Shared);
            using var otherChatVm = new ChatViewModel(dataStore, TestCopilot.Shared);
            chatVm.HasPlan = true;
            chatVm.PlanContent = "# Plan";
            var workspace = new ChatWorkspaceView { DataContext = chatVm, DataStore = dataStore };
            var window = new Window { Width = 1400, Height = 900, Content = workspace };

            window.Show();
            try
            {
                await PumpAsync();
                chatVm.OpenWorkspacePlanCommand.Execute(null);
                await PumpAsync();
                var overview = workspace.FindControl<WorkspaceOverview>("WorkspaceOverview")!;

                ClickBack(workspace); // the overview starts sliding back in…
                Assert.True(overview.Opacity < 1);
                workspace.DataContext = otherChatVm; // …and the user opens another chat

                Assert.Equal(1, overview.Opacity);
                Assert.Null(overview.RenderTransform);
            }
            finally
            {
                window.Close();
                workspace.Dispose();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ClosingATransientPageClosesTheWorkspaceAgain()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = CreateAppData();
            data.Settings.WorkspacePanelOpen = false;
            var dataStore = new DataStore(data);
            using var chatVm = new ChatViewModel(dataStore, TestCopilot.Shared);
            chatVm.HasPlan = true;
            chatVm.PlanContent = "# Plan";
            var workspace = new ChatWorkspaceView { DataContext = chatVm, DataStore = dataStore };
            var window = new Window { Width = 1400, Height = 900, Content = workspace };

            window.Show();
            try
            {
                await PumpAsync();
                chatVm.OpenWorkspacePlanCommand.Execute(null);
                await PumpAsync();
                Assert.True(workspace.IsWorkspaceOpen);

                // The plan went away (ClearChat raises every page's hide request).
                chatVm.ClearChat();
                await PumpAsync();

                Assert.False(workspace.IsWorkspaceOpen);
                Assert.Equal(WorkspacePage.Overview, chatVm.WorkspacePage);
            }
            finally
            {
                window.Close();
                workspace.Dispose();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task HeaderToggleOpensTheOverviewAndRemembersTheChoice()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var dataStore = new DataStore(CreateAppData());
            using var chatVm = new ChatViewModel(dataStore, TestCopilot.Shared);
            var workspace = new ChatWorkspaceView { DataContext = chatVm, DataStore = dataStore };
            var window = new Window { Width = 1400, Height = 900, Content = workspace };

            window.Show();
            try
            {
                await PumpAsync();
                Assert.False(workspace.IsWorkspaceOpen); // auto: nothing to show yet

                chatVm.ToggleWorkspacePanelCommand.Execute(null);
                Assert.True(workspace.IsWorkspaceOpen);
                Assert.Equal(WorkspacePage.Overview, chatVm.WorkspacePage);
                Assert.True(dataStore.Data.Settings.WorkspacePanelOpen);
                Assert.True(chatVm.ShowWorkspaceEmptyState);

                chatVm.ToggleWorkspacePanelCommand.Execute(null);
                Assert.False(workspace.IsWorkspaceOpen);
                Assert.False(dataStore.Data.Settings.WorkspacePanelOpen);
            }
            finally
            {
                window.Close();
                workspace.Dispose();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task GitChangesCanOpenStraightIntoOneFileAndBackToTheList()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lumi-workspace-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(path, "# New file\n");
        var session = HeadlessTestSession.Start();
        try
        {
            await session.Dispatch(async () =>
            {
                Loc.Load("en");
                var dataStore = new DataStore(CreateAppData());
                using var chatVm = new ChatViewModel(dataStore, TestCopilot.Shared);
                var pageHost = new ContentControl();
                using var controller = new WorkspacePanelController(
                    new Border(), dataStore, chatVm,
                    new WorkspacePanelParts(new Grid(), new Border(), null, new Border(), new Border(), pageHost));
                var changes = new GitChangesViewModel(
                    [new GitFileChange
                    {
                        RelativePath = "docs/new.md",
                        FullPath = path,
                        Kind = GitChangeKind.Added,
                        StatusCode = "A",
                        RepoRoot = Path.GetTempPath(),
                        RepoRelativePath = "docs/new.md",
                        LinesAdded = 1,
                    }],
                    Path.GetTempPath(),
                    "feature/workspace",
                    isWorktree: false)
                {
                    InitialFilePath = path,
                };

                controller.ShowGitChanges(changes);
                await PumpAsync();

                Assert.Equal(WorkspacePage.GitChanges, controller.Page);
                Assert.True(controller.IsOpen);
                Assert.Equal("new.md", controller.Header.Title);
                Assert.Equal("Git changes", controller.Header.Subtitle);
                Assert.IsType<DiffView>(pageHost.Content);

                controller.GoBack();

                Assert.Equal(WorkspacePage.GitChanges, controller.Page);
                Assert.Equal("Git changes", controller.Header.Title);
                Assert.Equal("feature/workspace", controller.Header.Subtitle);
                Assert.IsType<GitChangesView>(pageHost.Content);

                controller.GoBack();

                Assert.Equal(WorkspacePage.Overview, controller.Page);
                Assert.Null(pageHost.Content);
            }, CancellationToken.None);
        }
        finally
        {
            // The headless dispatcher can complete the awaiting test inline on its own thread, so its
            // blocking Dispose must run elsewhere or it waits for itself.
            await Task.Run(session.Dispose);
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TwoWindowsOnOneChatKeepTheirOwnPageHeaders()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = CreateAppData();
            data.Settings.WorkspacePanelOpen = true;
            var dataStore = new DataStore(data);
            using var chatVm = new ChatViewModel(dataStore, TestCopilot.Shared);
            chatVm.HasPlan = true;
            chatVm.PlanContent = "# Ship it\n\n- [ ] Build";
            var first = new ChatWorkspaceView { DataContext = chatVm, DataStore = dataStore };
            var second = new ChatWorkspaceView { DataContext = chatVm, DataStore = dataStore };
            var firstWindow = new Window { Width = 1400, Height = 900, Content = first };
            var secondWindow = new Window { Width = 1400, Height = 900, Content = second };

            firstWindow.Show();
            secondWindow.Show();
            try
            {
                await PumpAsync();
                chatVm.OpenWorkspacePlanCommand.Execute(null);
                await PumpAsync();
                Assert.Equal("Ship it", HeaderTitle(first));
                Assert.Equal("Ship it", HeaderTitle(second));

                // Going back in one window leaves the other window's page and header alone, even when
                // the other window re-applies its state (a resize).
                ClickBack(first);
                secondWindow.Width = 1300;
                await PumpAsync();

                Assert.Equal(WorkspacePage.Overview, first.WorkspacePage);
                Assert.Equal("Workspace", HeaderTitle(first));
                Assert.False(first.FindControl<Button>("WorkspaceBackButton")!.IsVisible);
                Assert.Equal(WorkspacePage.Plan, second.WorkspacePage);
                Assert.Equal("Ship it", HeaderTitle(second));
                Assert.True(second.FindControl<Button>("WorkspaceBackButton")!.IsVisible);
            }
            finally
            {
                firstWindow.Close();
                secondWindow.Close();
                first.Dispose();
                second.Dispose();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public void EditsListEachFileOnceEvenWhenItsPathCasingChanges()
    {
        Loc.Load("en");
        var dataStore = new DataStore(CreateAppData());
        using var chatVm = new ChatViewModel(dataStore, TestCopilot.Shared);
        var root = Path.Combine(Path.GetTempPath(), "LumiWorkspaceCase");

        AddEdit(Path.Combine(root, "Src", "Auth.cs"));
        AddEdit(Path.Combine(root, "Src", "Other.cs"));
        AddEdit(Path.Combine(root, "src", "auth.cs"));
        chatVm.RebuildTranscript();

        Assert.Equal(2, chatVm.WorkspaceEdits.TotalCount);
        Assert.Collection(
            chatVm.WorkspaceEdits.Items,
            latest => Assert.Equal("auth.cs", latest.FileName), // the latest touch leads, in its latest spelling
            other => Assert.Equal("Other.cs", other.FileName));

        void AddEdit(string path)
        {
            chatVm.Messages.Add(new ChatMessageViewModel(new ChatMessage { Role = "user", Content = "Edit it" }));
            chatVm.Messages.Add(new ChatMessageViewModel(new ChatMessage
            {
                Role = "tool",
                ToolName = "edit",
                ToolStatus = "Completed",
                Content = System.Text.Json.JsonSerializer.Serialize(new { filePath = path, oldString = "old", newString = "new" }),
            }));
            chatVm.Messages.Add(new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "Done" }));
        }
    }

    [Fact]
    public async Task ChatSwitchClosesPagesButKeepsTheOverviewPreference()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = CreateAppData();
            data.Settings.WorkspacePanelOpen = true;
            var dataStore = new DataStore(data);
            using var chatVm = new ChatViewModel(dataStore, TestCopilot.Shared);
            chatVm.HasPlan = true;
            chatVm.PlanContent = "# Plan";
            var workspace = new ChatWorkspaceView { DataContext = chatVm, DataStore = dataStore };
            var window = new Window { Width = 1400, Height = 900, Content = workspace };

            window.Show();
            try
            {
                await PumpAsync();
                Assert.True(workspace.IsWorkspaceOpen);
                chatVm.OpenWorkspacePlanCommand.Execute(null);
                await PumpAsync();
                Assert.Equal(WorkspacePage.Plan, workspace.WorkspacePage);

                workspace.CloseWorkspacePages();

                Assert.Equal(WorkspacePage.Overview, workspace.WorkspacePage);
                Assert.True(workspace.IsWorkspaceOpen);
            }
            finally
            {
                window.Close();
                workspace.Dispose();
            }
        }, CancellationToken.None);
    }

    private static void ClickBack(ChatWorkspaceView workspace)
        => workspace.FindControl<Button>("WorkspaceBackButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void ClickClose(ChatWorkspaceView workspace)
        => workspace.FindControl<Button>("WorkspaceCloseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static string? HeaderTitle(ChatWorkspaceView workspace)
        => workspace.FindControl<TextBlock>("WorkspaceTitleText")!.Text;

    private static string? HeaderSubtitle(ChatWorkspaceView workspace)
        => workspace.FindControl<TextBlock>("WorkspaceSubtitleText")!.Text;

    private static AppData CreateAppData() => new()
    {
        Settings = new UserSettings
        {
            AutoSaveChats = false,
            EnableMemoryAutoSave = false,
        },
    };

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
