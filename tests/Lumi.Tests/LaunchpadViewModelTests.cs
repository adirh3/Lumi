using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.Services;
using Lumi.Services.Remote;
using Lumi.ViewModels;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class LaunchpadViewModelTests
{
    [Fact]
    public async Task RemoteNewChatProjectsTheSameDesktopChoicesFromTheWholeIndex()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var project = new Project { Name = "Lumi" };
            var agent = new LumiAgent { Name = "Coding Lumi", IconGlyph = "L" };
            var oneOffProject = new Project { Name = "One-off" };
            var data = NewData();
            data.Projects.AddRange([project, oneOffProject]);
            data.Agents.Add(agent);
            var first = UsedChat(project, agent, 3);
            var second = UsedChat(project, agent, 5);
            first.WorktreePath = second.WorktreePath = @"C:\not-projected-worktree";
            data.Chats.AddRange([first, second, UsedChat(oneOffProject, null, 1)]);
            data.Chats.AddRange(Enumerable.Range(0, 1700).Select(index => new Chat
            {
                Title = $"Default chat {index}",
                MessageCount = 2,
                LastModelUsed = data.Settings.PreferredModel,
                LastReasoningEffortUsed = data.Settings.ReasoningEffort,
                HasUnreadMessages = index == 0,
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now
            }));
            using var surface = NewSurface(data);
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                var projected = RemoteProjector.BuildSettings(
                    new DataStore(data), surface.AvailableModels.ToArray(), surface).NewChat;
                Assert.NotNull(projected);
                var desktopSetup = Assert.Single(launchpad.Setups);
                var mobileSetup = Assert.Single(projected.Setups);
                Assert.Equal(desktopSetup.Title, mobileSetup.Title);
                Assert.Equal(desktopSetup.Meta, mobileSetup.Description);
                Assert.Equal(desktopSetup.Spec.ProjectId, mobileSetup.ProjectId);
                Assert.Equal(desktopSetup.Spec.AgentId, mobileSetup.AgentId);
                Assert.Equal(desktopSetup.Spec.ModelId, mobileSetup.Model);
                Assert.Equal(surface.NormalizeReasoningEffortFor(mobileSetup.Model, mobileSetup.Quality),
                    desktopSetup.Spec.Effort);
                Assert.Equal(desktopSetup.Spec.UseWorktree, mobileSetup.UseWorktree);
                // Without plans or habits, For you leads with the hour's starters, which the phone offers too.
                Assert.Equal(LaunchpadStarterCatalog.ForYou, launchpad.SelectedCategory?.Id);
                Assert.Equal(LaunchpadPlanner.ForYouCount, launchpad.Suggestions.Count);
                Assert.Equal(launchpad.Suggestions.Take(projected.Starters.Count).Select(item => (item.Glyph, item.Label, item.Prompt)),
                    projected.Starters.Select(item => (item.Glyph, item.Label, item.Prompt)));
                Assert.Equal(launchpad.GreetingLead + launchpad.GreetingName + launchpad.GreetingTrail,
                    projected.Greeting);

                // The launchpad itself, as the phone draws it: the row's details, Just chat, the tabs, For you for
                // each place, Other's places, the launchpad's words and what waits on the user.
                Assert.Equal(desktopSetup.Detail, mobileSetup.Detail);
                Assert.Equal(desktopSetup.IsVariant, mobileSetup.IsVariant);
                Assert.Equal(desktopSetup.IsPinned, mobileSetup.IsKept);
                Assert.Equal(desktopSetup.Spec.Effort, mobileSetup.Effort);
                Assert.Equal(desktopSetup.CanUseWorktree, mobileSetup.CanUseWorktree);
                var justChat = Assert.IsType<RemoteChatSetup>(projected.JustChat);
                Assert.Equal(Loc.Launchpad_JustChat, justChat.Title);
                Assert.Null(justChat.ProjectId);
                Assert.Null(justChat.AgentId);
                Assert.Equal(launchpad.JustChatMeta, justChat.Description);
                Assert.Equal(LaunchpadStarterCatalog.Order, projected.Tabs.Select(tab => tab.Id));
                Assert.All(projected.Tabs, tab =>
                {
                    Assert.Equal(LaunchpadStarterCatalog.Title(tab.Id), tab.Title);
                    Assert.Equal(LaunchpadStarterCatalog.In(tab.Id).Select(starter => (starter.Id, starter.Label, starter.Prompt)),
                        tab.Starters.Select(starter => (starter.Id, starter.Label, starter.Prompt)));
                });
                var noProject = projected.Scopes[0];
                Assert.Null(noProject.ProjectId);
                Assert.Equal(launchpad.Subtitle, noProject.Subtitle);
                Assert.Equal(launchpad.Suggestions.Select(item => (item.Spec.Id, item.Label, item.Prompt)),
                    noProject.ForYou.Select(item => (item.Id, item.Label, item.Prompt)));
                Assert.Equal(new[] { project.Id, oneOffProject.Id }.Order(),
                    projected.Scopes.Skip(1).Select(scope => scope.ProjectId!.Value).Order());
                Assert.All(projected.Scopes, scope => Assert.Equal(LaunchpadPlanner.ForYouCount, scope.ForYou.Count));
                Assert.All(projected.Tabs.SelectMany(tab => tab.Starters).Concat(projected.Scopes.SelectMany(scope => scope.ForYou)),
                    starter => Assert.Contains(starter.Icon, RemoteProtocol.LaunchpadIcons));
                Assert.Equal(new[] { project.Id, oneOffProject.Id, agent.Id }.Order(), projected.Places.Select(place => place.Id).Order());
                var lumiPlace = projected.Places.Single(place => place.Id == project.Id);
                Assert.True(lumiPlace.IsProject);
                Assert.Equal(project.Id, lumiPlace.Setup.ProjectId);
                Assert.False(string.IsNullOrEmpty(lumiPlace.Hint));
                Assert.False(projected.Places.Single(place => place.Id == agent.Id).IsProject);
                Assert.Equal(Loc.Launchpad_CategoryForYou, projected.Labels!.ForYou);
                Assert.Equal(Loc.Launchpad_SetupStartsWith, projected.Labels.StartsWith);
                Assert.Equal(1, projected.NeedsYouCount);
                Assert.Equal(Loc.Launchpad_NeedsYouOne, projected.NeedsYou);
                var waiting = Assert.Single(projected.NeedsYouChats);
                Assert.Equal("Default chat 0", waiting.Title);
                Assert.False(waiting.IsWaiting);
                Assert.Equal(Loc.Launchpad_StateReply, waiting.StateLabel);

                var json = JsonSerializer.Serialize(projected, RemoteJsonContext.Default.RemoteNewChatExperience);
                Assert.DoesNotContain(first.WorktreePath!, json);
                var roundtrip = JsonSerializer.Deserialize(json, RemoteJsonContext.Default.RemoteNewChatExperience)!;
                Assert.Equal(mobileSetup.Id, Assert.Single(roundtrip.Setups).Id);
                Assert.Equal(projected.Tabs.Sum(tab => tab.Starters.Count), roundtrip.Tabs.Sum(tab => tab.Starters.Count));
                Assert.Equal(projected.Places.Count, roundtrip.Places.Count);
                // The whole launchpad stays small however many chats there are.
                Assert.True(json.Length < 30_000, $"The new chat is {json.Length} characters.");
                Assert.Equal(1703, data.Chats.Count);
            }
            finally
            {
                launchpad.Deactivate();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public void EveryStarterIcon_IsOneThePhoneDraws()
    {
        Loc.Load("en");
        string?[] ids =
        [
            null,
            .. LaunchpadStarterCatalog.Order.SelectMany(LaunchpadStarterCatalog.In).Select(static starter => starter.Id),
            .. Enumerable.Range(0, 24)
                .SelectMany(static hour => LaunchpadPlanner.SelectStarters(hour, hasHistory: true)
                    .Concat(LaunchpadPlanner.SelectStarters(hour, hasHistory: false)))
                .Select(static starter => starter.Id)
                .Distinct(),
        ];

        Assert.Equal(1 + 48 + 7, ids.Length);
        Assert.All(ids, id => Assert.Contains(LaunchpadStarterCatalog.IconKeyOf(id), RemoteProtocol.LaunchpadIcons));
    }

    [Fact]
    public async Task Setup_ConfiguresTheDraft_AndClickingItAgainRestoresTheDraft()
    {
        using var session = HeadlessTestSession.Start();
        string? setupTitle = null;
        string? setupMeta = null;
        bool activeBefore = true, activeAfterApply = false, activeAfterUndo = true;
        string? modelAfterApply = null, qualityAfterApply = null, projectAfterApply = null, agentAfterApply = null;
        string? modelAfterUndo = null, projectAfterUndo = null, agentAfterUndo = null;
        string? defaultModel = null, defaultEffort = null;

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var project = new Project { Name = "Lumi" };
            var agent = new LumiAgent { Name = "Coding Lumi", IconGlyph = "⚡" };
            var data = NewData();
            data.Projects.Add(project);
            data.Agents.Add(agent);
            data.Chats.Add(UsedChat(project, agent, daysAgo: 1));
            data.Chats.Add(UsedChat(project, agent, daysAgo: 2));

            using var surface = NewSurface(data);
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                var setup = Assert.Single(launchpad.Setups);
                setupTitle = setup.Title;
                setupMeta = setup.Meta;
                activeBefore = setup.IsActive;

                setup.SelectCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                activeAfterApply = setup.IsActive;
                modelAfterApply = surface.SelectedModel;
                qualityAfterApply = surface.SelectedQuality;
                projectAfterApply = surface.SelectedProjectName;
                agentAfterApply = surface.ActiveAgent?.Name;

                // Tuning the applied setup's model is still about this chat, not the defaults.
                surface.SelectedQuality = "High";
                surface.SelectedQuality = "Max";

                setup.SelectCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                activeAfterUndo = setup.IsActive;
                modelAfterUndo = surface.SelectedModel;
                projectAfterUndo = surface.SelectedProjectName;
                agentAfterUndo = surface.ActiveAgent?.Name;

                defaultModel = data.Settings.PreferredModel;
                defaultEffort = data.Settings.ReasoningEffort;
            }
            finally
            {
                launchpad.Deactivate();
            }
        }, CancellationToken.None);

        Assert.Equal("Lumi", setupTitle);
        Assert.Equal("Coding Lumi · Claude Opus 5 · Max", setupMeta);
        Assert.False(activeBefore);

        Assert.True(activeAfterApply);
        Assert.Equal("claude-opus-5", modelAfterApply);
        Assert.Equal("Max", qualityAfterApply);
        Assert.Equal("Lumi", projectAfterApply);
        Assert.Equal("Coding Lumi", agentAfterApply);

        Assert.False(activeAfterUndo);
        Assert.Equal("gpt-5.4", modelAfterUndo);
        Assert.Null(projectAfterUndo);
        Assert.Null(agentAfterUndo);

        // A setup shapes one chat; it is not a change of the user's defaults.
        Assert.Equal("gpt-5.4", defaultModel);
        Assert.Equal("high", defaultEffort);
    }

    [Fact]
    public async Task Setup_ModelTuningDoesNotPublishOrSaveNewDefaults()
    {
        using var session = HeadlessTestSession.Start();
        int defaultsPublished = 0;
        string? defaultModel = null, defaultEffort = null, defaultTier = null;
        string? selectedEffort = null, selectedTier = null;

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var project = new Project { Name = "Lumi" };
            var data = NewData();
            data.Projects.Add(project);
            data.Chats.Add(UsedChat(project, null, 1));
            data.Chats.Add(UsedChat(project, null, 2));

            using var surface = NewSurface(data);
            surface.UpdateModelCapabilities(
                [new ModelInfo { Id = "claude-opus-5", SupportedReasoningEfforts = ["low", "high", "max"] }],
                new HashSet<string> { "claude-opus-5" },
                merge: true);
            surface.DefaultModelSelectionChanged += (_, _, _) => defaultsPublished++;
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                launchpad.Setups.Single().SelectCommand.Execute(null);
                surface.SelectedQuality = Loc.Quality_Low;
                surface.SelectedContextWindowTier = Loc.ContextWindow_Long;
                selectedEffort = surface.SelectedQuality;
                selectedTier = surface.SelectedContextWindowTier;
                defaultModel = data.Settings.PreferredModel;
                defaultEffort = data.Settings.ReasoningEffort;
                defaultTier = data.Settings.ContextWindowTier;
            }
            finally
            {
                launchpad.Deactivate();
            }
        }, CancellationToken.None);

        Assert.Equal(Loc.Quality_Low, selectedEffort);
        Assert.Equal(Loc.ContextWindow_Long, selectedTier);
        Assert.Equal(0, defaultsPublished);
        Assert.Equal("gpt-5.4", defaultModel);
        Assert.Equal("high", defaultEffort);
        Assert.Equal(ModelContextWindowTiers.Default, defaultTier);
    }

    [Fact]
    public async Task Setup_ModelWithoutEffortsIsGroupedAndCanBeUndone()
    {
        using var session = HeadlessTestSession.Start();
        bool activeAfterApply = false, activeAfterUndo = true;
        string? setupEffort = "unexpected", qualityAfterApply = "unexpected", restoredModel = null;

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var project = new Project { Name = "Docs" };
            var data = NewData();
            data.Settings.ReasoningEffort = "medium";
            data.Projects.Add(project);
            var first = UsedChat(project, null, 1);
            var second = UsedChat(project, null, 2);
            first.LastModelUsed = second.LastModelUsed = "claude-sonnet-4.5";
            first.LastReasoningEffortUsed = "high";
            second.LastReasoningEffortUsed = "medium";
            data.Chats.AddRange([first, second]);

            using var surface = NewSurface(data);
            surface.UpdateModelCapabilities([new ModelInfo { Id = "claude-sonnet-4.5" }], merge: true);
            surface.ApplyAvailableModels(["gpt-5.4", "claude-opus-5", "claude-sonnet-4.5"], "gpt-5.4");
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                var setup = launchpad.Setups.Single();
                setupEffort = setup.Spec.Effort;
                setup.SelectCommand.Execute(null);
                activeAfterApply = setup.IsActive;
                qualityAfterApply = surface.SelectedQuality;
                setup.SelectCommand.Execute(null);
                activeAfterUndo = setup.IsActive;
                restoredModel = surface.SelectedModel;
            }
            finally
            {
                launchpad.Deactivate();
            }
        }, CancellationToken.None);

        Assert.Null(setupEffort);
        Assert.Null(qualityAfterApply);
        Assert.True(activeAfterApply);
        Assert.False(activeAfterUndo);
        Assert.Equal("gpt-5.4", restoredModel);
    }

    [Fact]
    public async Task Setups_RefreshProjectAndAgentEditsAfterAnIndexSave()
    {
        using var session = HeadlessTestSession.Start();
        string? renamedTitle = null, renamedMeta = null;
        Guid? remainingProject = Guid.Empty, remainingAgent = Guid.Empty;

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var project = new Project { Name = "Lumi" };
            var agent = new LumiAgent { Name = "Coding Lumi" };
            var data = NewData();
            data.Projects.Add(project);
            data.Agents.Add(agent);
            data.Chats.Add(UsedChat(project, agent, 1));
            data.Chats.Add(UsedChat(project, agent, 2));
            var store = new DataStore(data);
            using var surface = new ChatViewModel(store, TestCopilot.Shared);
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                project.Name = "Lumi App";
                agent.Name = "Developer";
                await Task.Run(() => store.SaveAsync());
                Dispatcher.UIThread.RunJobs();
                renamedTitle = launchpad.Setups.Single().Title;
                renamedMeta = launchpad.Setups.Single().Meta;

                data.Projects.Clear();
                data.Agents.Clear();
                await Task.Run(() => store.SaveAsync());
                Dispatcher.UIThread.RunJobs();
                remainingProject = launchpad.Setups.Single().Spec.ProjectId;
                remainingAgent = launchpad.Setups.Single().Spec.AgentId;
            }
            finally
            {
                launchpad.Deactivate();
            }
        }, CancellationToken.None);

        Assert.Equal("Lumi App", renamedTitle);
        Assert.StartsWith("Developer · ", renamedMeta, StringComparison.Ordinal);
        Assert.Null(remainingProject);
        Assert.Null(remainingAgent);
    }

    [Fact]
    public async Task Setup_UndoRestoresTheSelectedExistingWorktree()
    {
        using var session = HeadlessTestSession.Start();
        var root = Path.Combine(Path.GetTempPath(), $"lumi-launchpad-worktree-{Guid.NewGuid():N}");
        var repo = Path.Combine(root, "repo");
        var worktree = Path.Combine(root, "existing");
        Directory.CreateDirectory(repo);
        try
        {
            RunGit(repo, "init", "--quiet");
            RunGit(repo, "-c", "user.name=Lumi Tests", "-c", "user.email=test@example.com",
                "commit", "--quiet", "--allow-empty", "-m", "initial");
            RunGit(repo, "worktree", "add", "--quiet", "-b", "existing-work", worktree);

            string? clearedPath = "unexpected", restoredPath = null, restoredBranch = null;
            bool restoredMode = false;
            await session.Dispatch(async () =>
            {
                Loc.Load("en");
                var project = new Project { Name = "Lumi", WorkingDirectory = repo };
                var data = NewData();
                data.Projects.Add(project);
                using var surface = NewSurface(data);
                surface.SetProjectId(project.Id);
                await surface.RefreshCodingProjectState();
                await surface.SelectExistingWorktreeCommand.ExecuteAsync(worktree);
                Dispatcher.UIThread.RunJobs();

                var localSetup = new LaunchpadSetupSpec(project.Id, null, false, "claude-opus-5", "max");
                await surface.ToggleLaunchpadSetupAsync(localSetup, isActive: false);
                clearedPath = surface.WorktreePath;
                await surface.ToggleLaunchpadSetupAsync(localSetup, isActive: true);
                Dispatcher.UIThread.RunJobs();
                restoredPath = surface.WorktreePath;
                restoredMode = surface.IsWorktreeMode;
                restoredBranch = surface.GitBranch;
            }, CancellationToken.None);

            Assert.Null(clearedPath);
            Assert.Equal(worktree, restoredPath);
            Assert.True(restoredMode);
            Assert.Equal("existing-work", restoredBranch);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NeedsYou_CountsWhatWaitsOnTheUser_AndFollowsLiveChanges()
    {
        using var session = HeadlessTestSession.Start();
        List<string>? titles = null, titlesAfterReply = null;
        string? label = null, waitingState = null, waitingMeta = null, labelAfterReply = null;
        bool hasNeedsYou = false, openingRaised = false;
        Guid? revealed = null;
        Guid replyId = Guid.Empty;

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var now = DateTimeOffset.Now;
            var waiting = new Chat { Title = "Release notes", MessageCount = 3, UpdatedAt = now.AddHours(-2), IsAwaitingInput = true };
            waiting.Messages.Add(new ChatMessage
            {
                Role = "tool",
                ToolName = "ask_question",
                ToolStatus = "InProgress",
                QuestionText = "Which **branch** should I ship?",
            });
            var reply = new Chat { Title = "Budget", MessageCount = 4, UpdatedAt = now.AddMinutes(-30), HasUnreadMessages = true, Preview = "Here's the summary." };
            var trip = new Chat { Title = "Trip", MessageCount = 2, UpdatedAt = now.AddMinutes(-5), Preview = "Booked.", IsRunning = true };
            replyId = reply.Id;

            var data = NewData();
            data.Chats.AddRange([waiting, reply, trip]);

            using var surface = NewSurface(data);
            surface.RevealChatRequested += id => revealed = id;
            var launchpad = surface.Launchpad;
            launchpad.ChatOpening += () => openingRaised = true;
            launchpad.Activate();
            try
            {
                titles = launchpad.NeedsYouItems.Select(item => item.Title).ToList();
                label = launchpad.NeedsYouLabel;
                hasNeedsYou = launchpad.HasNeedsYou;
                waitingState = launchpad.NeedsYouItems[0].StateLabel;
                waitingMeta = launchpad.NeedsYouItems[0].Meta;

                launchpad.NeedsYouItems[1].OpenCommand.Execute(null);

                // A chat that was only working finishes with a reply the user hasn't read.
                trip.IsRunning = false;
                trip.HasUnreadMessages = true;
                Dispatcher.UIThread.RunJobs();
                titlesAfterReply = launchpad.NeedsYouItems.Select(item => item.Title).ToList();
                labelAfterReply = launchpad.NeedsYouLabel;
            }
            finally
            {
                launchpad.Deactivate();
            }
        }, CancellationToken.None);

        // Work in progress isn't something to answer: the new chat leaves it to the sidebar.
        Assert.Equal(["Release notes", "Budget"], titles);
        Assert.True(hasNeedsYou);
        Assert.Equal("2 chats need you", label);
        Assert.Equal("Needs your answer", waitingState);
        Assert.Equal("Which branch should I ship?", waitingMeta);
        Assert.True(openingRaised);
        Assert.Equal(replyId, revealed);
        Assert.Equal(["Release notes", "Trip", "Budget"], titlesAfterReply);
        Assert.Equal("3 chats need you", labelAfterReply);
    }

    [Fact]
    public async Task Starter_GoesIntoTheComposerWithItsBlanks_AndPicksShapeTheTabsAndForYou()
    {
        using var session = HeadlessTestSession.Start();
        List<string>? tabs = null, planStarters = null, tabsLater = null, forYouLater = null;
        string? prompt = null, selectedAfterReactivating = null;
        var fillIns = new List<IReadOnlyList<string>>();
        StarterUse? remembered = null;

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var data = NewData();
            using var surface = NewSurface(data);
            surface.ComposerFillInRequested += fillIns.Add;
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                tabs = launchpad.Categories.Select(category => category.Id).ToList();
                launchpad.SelectedCategory = launchpad.Categories.Single(category => category.Id == LaunchpadStarterCatalog.Plan);
                planStarters = launchpad.Suggestions.Select(item => item.Prompt).ToList();

                var trip = launchpad.Suggestions.Single(item => item.Spec.Id == "plan.trip");
                trip.UseCommand.Execute(null);
                prompt = surface.PromptText;
                remembered = data.StarterUses.Single();

                // Two budgets make a habit, which For you then leads with.
                launchpad.SelectedCategory = launchpad.Categories.Single(category => category.Id == LaunchpadStarterCatalog.Money);
                launchpad.Suggestions.Single(item => item.Spec.Id == "money.budget").UseCommand.Execute(null);
                launchpad.Suggestions.Single(item => item.Spec.Id == "money.budget").UseCommand.Execute(null);
            }
            finally
            {
                launchpad.Deactivate();
            }

            // The next new chat starts on For you; the tabs stay where they were.
            launchpad.Activate();
            try
            {
                selectedAfterReactivating = launchpad.SelectedCategory?.Id;
                tabsLater = launchpad.Categories.Select(category => category.Id).ToList();
                forYouLater = launchpad.Suggestions.Select(item => item.Spec.Id ?? item.Prompt).ToList();
            }
            finally
            {
                launchpad.Deactivate();
            }
        }, CancellationToken.None);

        Assert.Equal([LaunchpadStarterCatalog.ForYou, .. LaunchpadStarterCatalog.Order], tabs);
        Assert.Equal(6, planStarters!.Count);
        Assert.Equal("Plan a trip to [where?]", prompt);
        Assert.Equal(["[where?]"], fillIns[0]);
        // A starter without blanks goes in whole, the caret at its end.
        Assert.Equal(3, fillIns.Count);
        Assert.Empty(fillIns[1]);
        Assert.Equal(("plan.trip", (Guid?)null, 1), (remembered!.Id, remembered.ProjectId, remembered.Count));

        Assert.Equal(LaunchpadStarterCatalog.ForYou, selectedAfterReactivating);
        Assert.Equal(tabs, tabsLater);
        Assert.Equal("money.budget", forYouLater![0]);
        Assert.Equal(LaunchpadPlanner.ForYouCount, forYouLater.Count);
    }

    [Fact]
    public async Task Setups_CanBeKeptRetunedOrHidden_AndPinnedFromOther()
    {
        using var session = HeadlessTestSession.Start();
        List<string>? learned = null, afterKeep = null, afterHide = null, afterOtherPin = null;
        (string? Model, bool Pinned) retuned = default;
        var strataPinnedInOther = false;
        Guid?[]? pinnedProjects = null, hiddenProjects = null;
        var lumi = new Project { Name = "Lumi" };
        var search = new Project { Name = "Fluent Search" };
        var strata = new Project { Name = "Strata" };

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var data = NewData();
            data.Projects.AddRange([lumi, search, strata]);
            data.Chats.AddRange([UsedChat(lumi, null, 1), UsedChat(lumi, null, 2), UsedChat(search, null, 3), UsedChat(search, null, 4)]);
            using var surface = NewSurface(data);
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                learned = launchpad.Setups.Select(setup => setup.Title).ToList();

                // Kept, Fluent Search leads the row whatever is used most.
                launchpad.Setups.Single(setup => setup.Title == "Fluent Search").ToggleKeepCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                afterKeep = launchpad.Setups.Select(setup => (setup.IsPinned ? "*" : "") + setup.Title).ToList();

                // Starting Lumi on another model keeps it, with that model.
                launchpad.Setups.Single(setup => setup.Title == "Lumi").StartWith("gpt-5.4");
                Dispatcher.UIThread.RunJobs();
                var retunedLumi = launchpad.Setups.Single(setup => setup.Title == "Lumi");
                retuned = (retunedLumi.Spec.ModelId, retunedLumi.IsPinned);

                // Not suggested any more, Fluent Search leaves the row and isn't learned back.
                launchpad.Setups.Single(setup => setup.Title == "Fluent Search").HideCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                afterHide = launchpad.Setups.Select(setup => setup.Title).ToList();

                // Strata, never used yet, is pinned from Other and joins the row.
                launchpad.OpenOtherCommand.Execute(null);
                var strataRow = launchpad.OtherProjects.Single(item => item.Title == "Strata");
                strataRow.TogglePinCommand.Execute(null);
                strataPinnedInOther = strataRow.IsPinned;
                Dispatcher.UIThread.RunJobs();
                afterOtherPin = launchpad.Setups.Select(setup => setup.Title).ToList();
                pinnedProjects = data.PinnedSetups.Select(pin => pin.ProjectId).ToArray();
                hiddenProjects = data.HiddenSetups.Select(pin => pin.ProjectId).ToArray();
            }
            finally
            {
                launchpad.Deactivate();
            }
        }, CancellationToken.None);

        Assert.Equal(["Lumi", "Fluent Search"], learned);
        Assert.Equal(["*Fluent Search", "Lumi"], afterKeep);
        Assert.Equal(("gpt-5.4", true), retuned);
        Assert.Equal(["Lumi"], afterHide);
        Assert.True(strataPinnedInOther);
        Assert.Equal(["Lumi", "Strata"], afterOtherPin);
        Assert.Equal([lumi.Id, strata.Id], pinnedProjects);
        Assert.Equal([search.Id], hiddenProjects);
    }

    [Fact]
    public async Task PinningAProjectFromOther_KeepsEveryVariantTheRowShowsThere()
    {
        using var session = HeadlessTestSession.Start();
        List<string>? before = null, after = null;
        var lumi = new Project { Name = "Lumi" };

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var data = NewData();
            data.Projects.Add(lumi);
            data.Chats.AddRange([UsedChat(lumi, null, 1), UsedChat(lumi, null, 2), OnModel(UsedChat(lumi, null, 3)), OnModel(UsedChat(lumi, null, 4))]);
            using var surface = NewSurface(data);
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                before = Row();
                launchpad.OpenOtherCommand.Execute(null);
                launchpad.OtherProjects.Single().TogglePinCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                after = Row();
            }
            finally
            {
                launchpad.Deactivate();
            }

            List<string> Row() => launchpad.Setups.Select(setup => (setup.IsPinned ? "*" : "") + setup.Spec.ModelId).ToList();
        }, CancellationToken.None);

        // Lumi on Opus and on GPT 5.4: pinning Lumi keeps both, rather than dropping the one it wasn't last used on.
        Assert.Equal(["claude-opus-5", "gpt-5.4"], before);
        Assert.Equal(["*claude-opus-5", "*gpt-5.4"], after);

        static Chat OnModel(Chat chat)
        {
            chat.LastModelUsed = "gpt-5.4";
            chat.LastReasoningEffortUsed = "high";
            return chat;
        }
    }

    [Fact]
    public async Task AnAgentLastUsedInAProject_PinsAndUnpinsFromOther()
    {
        using var session = HeadlessTestSession.Start();
        bool pinnedAtFirst = true, pinned = false, pinnedOnReopen = false, pinnedAfterUnpin = true;
        int keptWhilePinned = -1, keptAfterUnpin = -1;
        var lumi = new Project { Name = "Lumi" };
        var agent = new LumiAgent { Name = "Coding Lumi", IconGlyph = "⚡" };

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var data = NewData();
            data.Projects.Add(lumi);
            data.Agents.Add(agent);
            data.Chats.AddRange([UsedChat(lumi, agent, 1), UsedChat(lumi, agent, 2)]);
            using var surface = NewSurface(data);
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                launchpad.OpenOtherCommand.Execute(null);
                var row = launchpad.OtherAgents.Single();
                pinnedAtFirst = row.IsPinned;
                row.TogglePinCommand.Execute(null);
                pinned = row.IsPinned;
                keptWhilePinned = data.PinnedSetups.Count;

                // Its pin keeps the project it was last used in, and still reads as the agent's.
                launchpad.OpenOtherCommand.Execute(null);
                row = launchpad.OtherAgents.Single();
                pinnedOnReopen = row.IsPinned;
                row.TogglePinCommand.Execute(null);
                pinnedAfterUnpin = row.IsPinned;
                keptAfterUnpin = data.PinnedSetups.Count;
            }
            finally
            {
                launchpad.Deactivate();
            }
        }, CancellationToken.None);

        Assert.False(pinnedAtFirst);
        Assert.True(pinned);
        Assert.Equal(1, keptWhilePinned);
        Assert.True(pinnedOnReopen);
        Assert.False(pinnedAfterUnpin);
        Assert.Equal(0, keptAfterUnpin);
    }

    [Fact]
    public async Task JustChat_GoesBackToTheDefaultModelFromASetupOnAnother()
    {
        using var session = HeadlessTestSession.Start();
        (bool JustChat, bool Setup) onSetup = default, back = default;
        string? modelBack = null;

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var data = NewData();
            // Chats outside projects on Opus, while new chats start on GPT 5.4: a setup of its own.
            data.Chats.AddRange([OnOpus(PlainChat(1)), OnOpus(PlainChat(2))]);
            using var surface = NewSurface(data);
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                var setup = launchpad.Setups.Single();
                setup.SelectCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                onSetup = (launchpad.IsJustChatActive, setup.IsActive);

                launchpad.SelectJustChatCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                back = (launchpad.IsJustChatActive, launchpad.Setups.Single().IsActive);
                modelBack = surface.SelectedModel;
            }
            finally
            {
                launchpad.Deactivate();
            }
        }, CancellationToken.None);

        Assert.Equal((false, true), onSetup);
        Assert.Equal((true, false), back);
        Assert.Equal("gpt-5.4", modelBack);

        static Chat OnOpus(Chat chat)
        {
            chat.LastModelUsed = "claude-opus-5";
            chat.LastReasoningEffortUsed = "max";
            return chat;
        }
    }

    [Fact]
    public async Task KeepingTheDraft_MakesTheComposersSetupTheFirstChip_AndTryingAModelLeavesTheRowAlone()
    {
        using var session = HeadlessTestSession.Start();
        List<string>? rowBefore = null, rowWhileTrying = null, rowAfterKeep = null, rowLetGo = null, rowNextTime = null;
        (bool CanKeep, string Title, string Tooltip) offered = default;
        (bool CanKeep, bool Active, bool Pinned) afterKeep = default;
        List<(Guid? Project, bool Worktree, string? Model)>? kept = null;
        var lumi = new Project { Name = "Lumi" };
        var search = new Project { Name = "Fluent Search" };

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = NewData();
            data.Projects.AddRange([lumi, search]);
            data.Chats.AddRange([UsedChat(lumi, null, 1), UsedChat(lumi, null, 2), UsedChat(search, null, 3), UsedChat(search, null, 4)]);
            // Plain chats on the default model: Just chat already starts them, so they are no setup of their own.
            data.Chats.AddRange([PlainChat(daysAgo: 5), PlainChat(daysAgo: 6)]);
            var store = new DataStore(data);
            using var surface = new ChatViewModel(store, TestCopilot.Shared);
            surface.UpdateModelCapabilities(
            [
                new ModelInfo { Id = "gpt-5.4", SupportedReasoningEfforts = ["low", "medium", "high"], DefaultReasoningEffort = "medium" },
                new ModelInfo { Id = "gpt-5.5", SupportedReasoningEfforts = ["low", "medium", "high"], DefaultReasoningEffort = "medium" },
                new ModelInfo { Id = "claude-opus-5", SupportedReasoningEfforts = ["low", "medium", "high", "max"], DefaultReasoningEffort = "high" },
            ]);
            surface.ApplyAvailableModels(["gpt-5.4", "gpt-5.5", "claude-opus-5"], "gpt-5.4");
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                rowBefore = Row();

                // Lumi's setup, then another model for it in the composer, which also becomes the default: the Other
                // chip shows the setup and offers to keep it, and the row stays as it was.
                launchpad.Setups.Single(setup => setup.Title == "Lumi").SelectCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                surface.SelectedModel = "gpt-5.5";
                await Task.Run(() => store.SaveAsync());
                Dispatcher.UIThread.RunJobs();
                rowWhileTrying = Row();
                offered = (launchpad.CanKeepDraft, launchpad.OtherTitle, launchpad.KeepDraftTooltip);

                // Keeping it adds it first and keeps the Opus setup the row showed for Lumi beside it.
                launchpad.KeepDraftCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                rowAfterKeep = Row();
                afterKeep = (launchpad.CanKeepDraft, launchpad.Setups[0].IsActive, launchpad.Setups[0].IsPinned);
                kept = data.PinnedSetups.Select(static pin => (pin.ProjectId, pin.UseWorktree, pin.ModelId)).ToList();

                // Letting the Opus one go leaves the new one, and Lumi isn't learned again while it is kept.
                launchpad.Setups.Single(setup => setup.Title == "Lumi" && setup.Spec.ModelId == "claude-opus-5")
                    .ToggleKeepCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                rowLetGo = Row();
            }
            finally
            {
                launchpad.Deactivate();
            }

            // Next time Just chat starts on the new default, so the old default's plain chats are a setup of their own.
            launchpad.Activate();
            try
            {
                rowNextTime = Row();
            }
            finally
            {
                launchpad.Deactivate();
            }

            List<string> Row() => launchpad.Setups
                .Select(setup => $"{(setup.IsPinned ? "*" : "")}{(setup.Spec.ProjectId is null ? "Just" : setup.Title)}:{setup.Spec.ModelId}")
                .ToList();
        }, CancellationToken.None);

        Assert.Equal(["Lumi:claude-opus-5", "Fluent Search:claude-opus-5"], rowBefore);
        Assert.Equal(rowBefore, rowWhileTrying);
        Assert.True(offered.CanKeep);
        Assert.Equal("Lumi", offered.Title);
        Assert.StartsWith("Keep Lumi · ", offered.Tooltip, StringComparison.Ordinal);
        Assert.EndsWith(" on new chats", offered.Tooltip, StringComparison.Ordinal);
        Assert.Equal(["*Lumi:gpt-5.5", "*Lumi:claude-opus-5", "Fluent Search:claude-opus-5"], rowAfterKeep);
        Assert.Equal((false, true, true), afterKeep);
        Assert.Equal([(lumi.Id, false, "gpt-5.5"), (lumi.Id, false, "claude-opus-5")], kept);
        Assert.Equal(["*Lumi:gpt-5.5", "Fluent Search:claude-opus-5"], rowLetGo);
        Assert.Equal(["*Lumi:gpt-5.5", "Fluent Search:claude-opus-5", "Just:gpt-5.4"], rowNextTime);
    }

    [Fact]
    public async Task OneProject_CanBeKeptOnSeveralModels_EachSayingWhatItStartsWith()
    {
        using var session = HeadlessTestSession.Start();
        List<string>? rowBefore = null, rowTwoModels = null, rowRetuned = null, rowOneLeft = null, rowNoneLeft = null;
        List<bool>? activeTwoModels = null;
        var lumi = new Project { Name = "Lumi" };
        var search = new Project { Name = "Fluent Search" };

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var data = NewData();
            data.Projects.AddRange([lumi, search]);
            data.Chats.AddRange([UsedChat(lumi, null, 1), UsedChat(lumi, null, 2), UsedChat(search, null, 3), UsedChat(search, null, 4)]);
            using var surface = NewSurface(data, extraModel: "gpt-5.5");
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                rowBefore = Row();

                // Keep Lumi as it's used (Opus), then start it on GPT 5.5 in the composer and keep that too: Lumi now
                // has two setups, each saying which model it starts with, and only the chat's is active.
                launchpad.Setups.Single(setup => setup.Title == "Lumi").ToggleKeepCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                launchpad.Setups.Single(setup => setup.Title == "Lumi").SelectCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                surface.SelectedModel = "gpt-5.5";
                Dispatcher.UIThread.RunJobs();
                launchpad.KeepDraftCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                rowTwoModels = Row();
                activeTwoModels = launchpad.Setups.Select(static setup => setup.IsActive).ToList();

                // Starting the GPT one on Opus too keeps both: they now differ only in effort, which they say.
                launchpad.Setups.First(setup => setup.Spec.ModelId == "gpt-5.5").StartWith("claude-opus-5");
                Dispatcher.UIThread.RunJobs();
                rowRetuned = Row();

                // Removing one leaves the other; removing the last stops Lumi being suggested at all.
                launchpad.Setups.First(setup => setup.Title == "Lumi").HideCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                rowOneLeft = Row();
                launchpad.Setups.Single(setup => setup.Title == "Lumi").HideCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                rowNoneLeft = Row();
            }
            finally
            {
                launchpad.Deactivate();
            }

            List<string> Row() => launchpad.Setups
                .Select(setup => $"{(setup.IsPinned ? "*" : "")}{setup.Title}{(setup.IsVariant ? "|" + setup.Detail : "")}")
                .ToList();
        }, CancellationToken.None);

        var gpt = ChatViewModel.FormatModelDisplay("gpt-5.5");
        var opus = ChatViewModel.FormatModelDisplay("claude-opus-5");
        var max = ModelSelectionHelper.EffortToDisplay("max");
        Assert.Equal(["Lumi", "Fluent Search"], rowBefore);
        Assert.Equal([$"*Lumi|{gpt}", $"*Lumi|{opus}", "Fluent Search"], rowTwoModels);
        Assert.Equal([true, false, false], activeTwoModels);
        Assert.Equal(3, rowRetuned!.Count);
        Assert.All(rowRetuned.Take(2), entry => Assert.StartsWith($"*Lumi|{opus} · ", entry, StringComparison.Ordinal));
        Assert.Contains($"*Lumi|{opus} · {max}", rowRetuned);
        Assert.NotEqual(rowRetuned[0], rowRetuned[1]);
        Assert.Equal("Fluent Search", rowRetuned[2]);
        Assert.Equal(["*Lumi", "Fluent Search"], rowOneLeft);
        Assert.Equal(["Fluent Search"], rowNoneLeft);
    }

    [Fact]
    public async Task KeepingAnotherSetupForAProject_KeepsTheOneTheRowShowed()
    {
        using var session = HeadlessTestSession.Start();
        List<string>? before = null, after = null;

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var lumi = new Project { Name = "Lumi" };
            var data = NewData();
            data.Projects.Add(lumi);
            data.Chats.AddRange([UsedChat(lumi, null, 1), UsedChat(lumi, null, 2)]);
            using var surface = NewSurface(data, extraModel: "gpt-5.5");
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                before = Row();

                // Start Lumi on GPT 5.5 and keep that: the Opus setup the row showed stays beside it, kept now too, since
                // keeping a project stops Lumi learning setups for it.
                launchpad.Setups.Single().SelectCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                surface.SelectedModel = "gpt-5.5";
                Dispatcher.UIThread.RunJobs();
                launchpad.KeepDraftCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                after = Row();
            }
            finally
            {
                launchpad.Deactivate();
            }

            List<string> Row() => launchpad.Setups
                .Select(setup => $"{(setup.IsPinned ? "*" : "")}{setup.Title}|{setup.Spec.ModelId}")
                .ToList();
        }, CancellationToken.None);

        Assert.Equal(["Lumi|claude-opus-5"], before);
        Assert.Equal(["*Lumi|gpt-5.5", "*Lumi|claude-opus-5"], after);
    }

    [Fact]
    public async Task OneProjectInAWorktreeAndInItsFolder_SaysWhichIsWhich()
    {
        using var session = HeadlessTestSession.Start();
        List<(string Title, string Detail, bool Worktree)>? row = null;

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var lumi = new Project { Name = "Lumi" };
            var data = NewData();
            data.Projects.Add(lumi);
            data.Chats.AddRange([UsedChat(lumi, null, 1), UsedChat(lumi, null, 2), InWorktree(UsedChat(lumi, null, 3)), InWorktree(UsedChat(lumi, null, 4))]);
            using var surface = NewSurface(data);
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                row = launchpad.Setups.Select(static setup => (setup.Title, setup.Detail, setup.Spec.UseWorktree)).ToList();
            }
            finally
            {
                launchpad.Deactivate();
            }

            static Chat InWorktree(Chat chat)
            {
                chat.WorktreePath = @"C:\worktrees\" + chat.Title;
                return chat;
            }
        }, CancellationToken.None);

        // Both start on the same model, so the worktree is what tells them apart.
        var opus = ChatViewModel.FormatModelDisplay("claude-opus-5")!;
        Assert.Equal([("Lumi", opus, false), ("Lumi", $"Worktree · {opus}", true)], row);
    }

    [Fact]
    public async Task JustChatAndOther_StartWhereTheUserChooses()
    {
        using var session = HeadlessTestSession.Start();
        bool plainAtFirst = false, plainWithSetup = true, plainAgain = false, otherPicked = false;
        string? plainMeta = null, projectAfterJustChat = "unset", projectAfterOther = null, modelAfterOther = null;
        List<string>? otherProjects = null, otherAgents = null, filtered = null;
        bool agentsAfterFilter = true, noMatch = false;
        (bool Active, string Title, bool HasBadge, bool JustChat) otherChip = default, otherChipAtFirst = default;

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var lumi = new Project { Name = "Lumi" };
            var strata = new Project { Name = "Strata" };
            var agent = new LumiAgent { Name = "Coding Lumi", IconGlyph = "⚡" };
            var data = NewData();
            data.Projects.AddRange([lumi, strata]);
            data.Agents.Add(agent);
            data.Chats.Add(UsedChat(lumi, agent, daysAgo: 1));
            data.Chats.Add(UsedChat(lumi, agent, daysAgo: 2));
            var strataChat = UsedChat(strata, null, daysAgo: 3);
            strataChat.LastModelUsed = "gpt-5.4";
            strataChat.LastReasoningEffortUsed = "low";
            data.Chats.Add(strataChat);

            using var surface = NewSurface(data);
            var launchpad = surface.Launchpad;
            launchpad.OtherPicked += () => otherPicked = true;
            launchpad.Activate();
            try
            {
                plainAtFirst = launchpad.IsJustChatActive;
                plainMeta = launchpad.JustChatMeta;
                otherChipAtFirst = (launchpad.IsOtherActive, launchpad.OtherTitle, launchpad.HasOtherBadge, launchpad.IsJustChatActive);

                launchpad.Setups.Single().SelectCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                plainWithSetup = launchpad.IsJustChatActive;

                launchpad.SelectJustChatCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                plainAgain = launchpad.IsJustChatActive;
                projectAfterJustChat = surface.SelectedProjectName;

                launchpad.OpenOtherCommand.Execute(null);
                otherProjects = launchpad.OtherProjects.Select(item => item.Title).ToList();
                otherAgents = launchpad.OtherAgents.Select(item => item.Title).ToList();
                launchpad.OtherQuery = "str";
                filtered = launchpad.OtherProjects.Select(item => item.Title).ToList();
                agentsAfterFilter = launchpad.HasOtherAgents;

                launchpad.SelectFirstOther();
                Dispatcher.UIThread.RunJobs();
                projectAfterOther = surface.SelectedProjectName;
                modelAfterOther = surface.SelectedModel;
                otherChip = (launchpad.IsOtherActive, launchpad.OtherTitle, launchpad.HasOtherBadge, launchpad.IsJustChatActive);

                launchpad.OtherQuery = "nothing like this";
                noMatch = launchpad.HasNoOtherMatch;
            }
            finally
            {
                launchpad.Deactivate();
            }
        }, CancellationToken.None);

        Assert.True(plainAtFirst);
        Assert.Equal(ChatViewModel.FormatModelDisplay("gpt-5.4"), plainMeta);
        Assert.False(plainWithSetup);
        Assert.True(plainAgain);
        Assert.Null(projectAfterJustChat);

        // Most recently used first; every agent after the projects.
        Assert.Equal(["Lumi", "Strata"], otherProjects);
        Assert.Equal(["Coding Lumi"], otherAgents);
        Assert.Equal(["Strata"], filtered);
        Assert.False(agentsAfterFilter);
        Assert.True(otherPicked);
        // It starts the way the user last worked in Strata, and the Other chip says so.
        Assert.Equal("Strata", projectAfterOther);
        Assert.Equal("gpt-5.4", modelAfterOther);
        Assert.Equal((false, "Other", false, true), otherChipAtFirst);
        Assert.Equal((true, "Strata", true, false), otherChip);
        Assert.True(noMatch);
    }

    [Fact]
    public async Task AProjectsBrief_LeadsItsNewChat()
    {
        using var session = HeadlessTestSession.Start();
        string? subtitleOutside = null, subtitleInProject = null, firstStarter = null, subtitleBriefsOff = null;
        List<string>? tabsInProject = null;

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var lumi = new Project { Name = "Lumi" };
            var data = NewData();
            data.Projects.Add(lumi);
            data.Chats.Add(UsedChat(lumi, null, daysAgo: 1));
            data.Chats.Add(UsedChat(lumi, null, daysAgo: 2));
            data.Briefs.Add(new ContextBrief
            {
                ProjectId = lumi.Id,
                Content = "Lumi 1.0.1 is live; 1.0.2 is prepared but not published. Mobile smoothness work is active.",
                Starters = ["Publish 1.0.2", "Continue the mobile smoothness work"],
            });

            using var surface = NewSurface(data);
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                subtitleOutside = launchpad.Subtitle;

                launchpad.Setups.Single().SelectCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                subtitleInProject = launchpad.Subtitle;
                firstStarter = launchpad.Suggestions.First().Prompt;
                tabsInProject = launchpad.Categories.Select(category => category.Id).ToList();

                data.Settings.EnableDailyBriefs = false;
                launchpad.Deactivate();
                launchpad.Activate();
                subtitleBriefsOff = launchpad.Subtitle;
            }
            finally
            {
                launchpad.Deactivate();
            }
        }, CancellationToken.None);

        Assert.Equal("What would you like to start?", subtitleOutside);
        Assert.Equal("Lumi 1.0.1 is live; 1.0.2 is prepared but not published.", subtitleInProject);
        Assert.Equal("Publish 1.0.2", firstStarter);
        // The tabs never move: a project changes For you, not where the kinds of task are.
        Assert.Equal([LaunchpadStarterCatalog.ForYou, .. LaunchpadStarterCatalog.Order], tabsInProject);
        Assert.Equal("What would you like to start?", subtitleBriefsOff);
    }

    [Fact]
    public async Task ChatView_RunsTheLaunchpadOnlyWhileTheNewChatIsOnScreen()
    {
        using var session = HeadlessTestSession.Start();
        bool activeOnWelcome = false, cardVisible = false, setupsVisible = false;
        bool activeInChat = true, activeBackOnWelcome = false, activeAfterClose = true;
        double foldedSetupsHeight = -1, composerOffset = -1, expectedComposerOffset = -2;
        string? greeting = null;

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var project = new Project { Name = "Lumi" };
            var data = NewData();
            data.Settings.UserName = "Adir";
            data.Projects.Add(project);
            data.Chats.Add(UsedChat(project, null, daysAgo: 1));
            data.Chats.Add(UsedChat(project, null, daysAgo: 2));
            var opened = new Chat { Title = "Opened", MessageCount = 1 };
            opened.Messages.Add(new ChatMessage { Role = "user", Content = "Hi" });
            data.Chats.Add(opened);

            using var surface = NewSurface(data);
            var view = new ChatView { DataContext = surface };
            var window = new Window { Width = 1100, Height = 900, Content = view };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                activeOnWelcome = surface.Launchpad.IsActive;
                cardVisible = view.FindControl<ItemsControl>("WelcomeSuggestions")!.IsEffectivelyVisible
                              && view.FindControl<ListBox>("LaunchpadCategories")!.ItemCount == 9
                              && view.FindControl<Button>("LaunchpadJustChat")!.IsEffectivelyVisible
                              && !view.FindControl<Button>("LaunchpadNeedsYouButton")!.IsVisible;
                var setups = view.FindControl<StackPanel>("LaunchpadSetups")!;
                setupsVisible = setups.IsEffectivelyVisible;
                greeting = surface.Launchpad.GreetingLead + surface.Launchpad.GreetingName + surface.Launchpad.GreetingTrail;

                surface.CurrentChat = opened;
                var deadline = DateTime.UtcNow.AddSeconds(3);
                do
                {
                    Thread.Sleep(16);
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                }
                while (setups.Bounds.Height > 0 && DateTime.UtcNow < deadline);

                activeInChat = surface.Launchpad.IsActive;

                // Once folded, the setups row must take no room at all above the open chat's composer.
                var container = view.FindControl<StackPanel>("ComposerContainer")!;
                var composer = view.FindControl<Control>("Composer")!;
                foldedSetupsHeight = setups.Bounds.Height;
                composerOffset = composer.Bounds.Top;
                expectedComposerOffset = container.Children
                    .Where(child => child != setups && child != composer && child.IsVisible)
                    .Sum(child => child.Margin.Top + child.Bounds.Height + child.Margin.Bottom)
                    + composer.Margin.Top;

                surface.CurrentChat = null;
                Dispatcher.UIThread.RunJobs();
                activeBackOnWelcome = surface.Launchpad.IsActive;
            }
            finally
            {
                window.Close();
            }

            Dispatcher.UIThread.RunJobs();
            activeAfterClose = surface.Launchpad.IsActive;
        }, CancellationToken.None);

        Assert.True(activeOnWelcome);
        Assert.True(cardVisible);
        Assert.True(setupsVisible);
        Assert.Contains("Adir", greeting, StringComparison.Ordinal);
        Assert.False(activeInChat);
        Assert.Equal(0, foldedSetupsHeight);
        Assert.Equal(expectedComposerOffset, composerOffset, precision: 3);
        Assert.True(activeBackOnWelcome);
        Assert.False(activeAfterClose);
    }

    private static AppData NewData() => new()
    {
        Settings = new UserSettings
        {
            PreferredModel = "gpt-5.4",
            ReasoningEffort = "high",
            AutoSaveChats = false,
            EnableMemoryAutoSave = false,
        },
    };

    private static ChatViewModel NewSurface(AppData data, string? extraModel = null)
    {
        var surface = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
        List<ModelInfo> capabilities =
        [
            new ModelInfo { Id = "gpt-5.4", SupportedReasoningEfforts = ["low", "medium", "high"], DefaultReasoningEffort = "medium" },
            new ModelInfo { Id = "claude-opus-5", SupportedReasoningEfforts = ["low", "medium", "high", "max"], DefaultReasoningEffort = "high" },
        ];
        if (extraModel is not null)
            capabilities.Add(new ModelInfo { Id = extraModel, SupportedReasoningEfforts = ["low", "medium", "high"], DefaultReasoningEffort = "medium" });
        surface.UpdateModelCapabilities(capabilities);
        surface.ApplyAvailableModels(capabilities.Select(static model => model.Id).ToList(), "gpt-5.4");
        return surface;
    }

    private static Chat UsedChat(Project project, LumiAgent? agent, int daysAgo) => new()
    {
        Title = $"{project.Name} work {daysAgo}",
        ProjectId = project.Id,
        AgentId = agent?.Id,
        LastModelUsed = "claude-opus-5",
        LastReasoningEffortUsed = "max",
        MessageCount = 6,
        CreatedAt = DateTimeOffset.Now.AddDays(-daysAgo),
        UpdatedAt = DateTimeOffset.Now.AddDays(-daysAgo),
    };

    /// <summary>A chat outside projects on the default model (<see cref="NewData"/>).</summary>
    private static Chat PlainChat(int daysAgo) => new()
    {
        Title = $"Chat {daysAgo}",
        LastModelUsed = "gpt-5.4",
        LastReasoningEffortUsed = "high",
        MessageCount = 6,
        CreatedAt = DateTimeOffset.Now.AddDays(-daysAgo),
        UpdatedAt = DateTimeOffset.Now.AddDays(-daysAgo),
    };

    private static void RunGit(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(directory, ".lumi-test-global-gitconfig");
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, error.Result);
    }
}
