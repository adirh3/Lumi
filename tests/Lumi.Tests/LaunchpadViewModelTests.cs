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
                // The same starters in the same order: Just chat (the 1,700 plain chats), then Lumi with Coding Lumi.
                Assert.Equal(launchpad.Setups.Select(item => item.Title), projected.Setups.Select(setup => setup.Title));
                var desktopSetup = launchpad.Setups.Single(item => !item.IsJustChat);
                var mobileSetup = projected.Setups.Single(setup => setup.ProjectId is not null);
                Assert.Equal(desktopSetup.Title, mobileSetup.Title);
                Assert.Equal(desktopSetup.Meta, mobileSetup.Description);
                Assert.Equal(desktopSetup.Spec.ProjectId, mobileSetup.ProjectId);
                Assert.Equal(desktopSetup.Spec.AgentId, mobileSetup.AgentId);
                // A place starts on the default model: the phone names it back without one, and applies the default.
                Assert.Null(desktopSetup.Spec.ModelId);
                Assert.True(mobileSetup.IsDefaultModel);
                Assert.Equal(data.Settings.PreferredModel, mobileSetup.Model);
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
                Assert.Equal(desktopSetup.TellText, mobileSetup.Detail ?? "");
                Assert.Equal(desktopSetup.TellsWorktree, mobileSetup.TellsWorktree);
                Assert.Equal(desktopSetup.IsKept, mobileSetup.IsKept);
                Assert.Equal(desktopSetup.CanHide, mobileSetup.CanHide);
                Assert.Equal(desktopSetup.CanUseWorktree, mobileSetup.CanUseWorktree);
                Assert.Equal(LaunchpadStarterEditor.UsesTextOf(desktopSetup.Choice), mobileSetup.Note);
                var justChat = projected.Setups.Single(setup => setup.ProjectId is null && setup.AgentId is null);
                Assert.Equal(Loc.Launchpad_JustChat, justChat.Title);
                Assert.False(justChat.CanHide);
                Assert.True(justChat.IsDefaultModel);
                Assert.Equal(ChatViewModel.FormatModelDisplay(data.Settings.PreferredModel), projected.DefaultModel);
                Assert.Equal(LaunchpadPlanner.StarterIcons, projected.StarterIcons);
                Assert.All(projected.StarterIcons, icon => Assert.Contains(icon, RemoteProtocol.LaunchpadIcons));
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
                Assert.Equal(Loc.Launchpad_NewStarter, projected.Labels.NewStarter);
                Assert.Equal(1, projected.NeedsYouCount);
                Assert.Equal(Loc.Launchpad_NeedsYouOne, projected.NeedsYou);
                var waiting = Assert.Single(projected.NeedsYouChats);
                Assert.Equal("Default chat 0", waiting.Title);
                Assert.False(waiting.IsWaiting);
                Assert.Equal(Loc.Launchpad_StateReply, waiting.StateLabel);

                var json = JsonSerializer.Serialize(projected, RemoteJsonContext.Default.RemoteNewChatExperience);
                Assert.DoesNotContain(first.WorktreePath!, json);
                var roundtrip = JsonSerializer.Deserialize(json, RemoteJsonContext.Default.RemoteNewChatExperience)!;
                Assert.Equal(projected.Setups.Select(setup => setup.Id), roundtrip.Setups.Select(setup => setup.Id));
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
    public async Task Starter_StartsItsPlaceOnTheDefaultModel_AndClickingItAgainRestoresTheDraft()
    {
        using var session = HeadlessTestSession.Start();
        string? title = null, meta = null;
        bool activeBefore = true, justChatLitBefore = false, activeAfterApply = false, activeAfterUndo = true;
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
            // The user worked there on Opus; the place still starts on the default model, so a new default carries it.
            data.Chats.Add(UsedChat(project, agent, daysAgo: 1));
            data.Chats.Add(UsedChat(project, agent, daysAgo: 2));

            using var surface = NewSurface(data);
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                var setup = launchpad.Setups.Single(item => !item.IsJustChat);
                title = setup.Title;
                meta = setup.Meta;
                activeBefore = setup.IsActive;
                justChatLitBefore = launchpad.ActiveSetup?.IsJustChat == true;

                setup.SelectCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                activeAfterApply = setup.IsActive;
                modelAfterApply = surface.SelectedModel;
                qualityAfterApply = surface.SelectedQuality;
                projectAfterApply = surface.SelectedProjectName;
                agentAfterApply = surface.ActiveAgent?.Name;

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

        Assert.Equal("Lumi", title);
        Assert.Equal("Lumi · Coding Lumi · Default model", meta);
        Assert.False(activeBefore);
        Assert.True(justChatLitBefore);

        Assert.True(activeAfterApply);
        Assert.Equal("gpt-5.4", modelAfterApply);
        Assert.Equal("High", qualityAfterApply);
        Assert.Equal("Lumi", projectAfterApply);
        Assert.Equal("Coding Lumi", agentAfterApply);

        Assert.False(activeAfterUndo);
        Assert.Equal("gpt-5.4", modelAfterUndo);
        Assert.Null(projectAfterUndo);
        Assert.Null(agentAfterUndo);

        // A starter shapes one chat; it is not a change of the user's defaults.
        Assert.Equal("gpt-5.4", defaultModel);
        Assert.Equal("high", defaultEffort);
    }

    [Fact]
    public async Task AKeptStarter_StartsWithItsOwnModel_AndARetiredModelFallsBackToTheDefault()
    {
        using var session = HeadlessTestSession.Start();
        List<string>? row = null, rowRetired = null;
        bool lineIcon = false;
        (string? Model, string? Quality, string? Lit) applied = default;
        string? litOnAnotherModel = null, retiredModel = "unset", retiredNote = null;
        (string? Model, string? Effort, string? Name, string? Icon) keptThroughEdits = default;

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var lumi = new Project { Name = "Lumi" };
            var data = NewData();
            data.Projects.Add(lumi);
            data.Chats.AddRange([UsedChat(lumi, null, 1), UsedChat(lumi, null, 2)]);
            data.PinnedSetups.Add(new LaunchpadSetupPin
            {
                ProjectId = lumi.Id, ModelId = "claude-opus-5", Effort = "max", Name = "UI dev", Icon = "layers",
            });
            using var surface = NewSurface(data);
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                row = Titles();
                var uiDev = launchpad.Setups[0];
                lineIcon = uiDev.Badge.HasLineIcon && !uiDev.Badge.HasInitial;
                uiDev.SelectCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                applied = (surface.SelectedModel, surface.SelectedQuality, launchpad.ActiveSetup?.Title);

                // Lumi on another model is the learned place's, which starts on any model.
                surface.SelectedModel = "gpt-5.4";
                Dispatcher.UIThread.RunJobs();
                litOnAnotherModel = launchpad.ActiveSetup?.Title;
            }
            finally
            {
                launchpad.Deactivate();
            }

            // Kept on a model this computer no longer offers, it starts with the default model, and its card says so. It
            // is then exactly the learned place, which it stands in for.
            data.PinnedSetups[0].ModelId = "retired-model";
            launchpad.Activate();
            try
            {
                var uiDev = launchpad.Setups.Single(item => item.Title == "UI dev");
                retiredModel = uiDev.Spec.ModelId;
                uiDev.EditCommand.Execute(null);
                retiredNote = launchpad.Editor.RetiredModelNote;
                rowRetired = Titles();

                // Renamed and given another icon meanwhile, it keeps that model for when it's back.
                var editor = launchpad.Editor;
                editor.Name = "UI work";
                editor.CommitNameCommand.Execute(null);
                editor.Icons.Single(icon => icon.Key == "bug").SelectCommand.Execute(null);
                var pin = data.PinnedSetups[0];
                keptThroughEdits = (pin.ModelId, pin.Effort, pin.Name, pin.Icon);
            }
            finally
            {
                launchpad.Deactivate();
            }

            List<string> Titles() => launchpad.Setups.Select(static setup => setup.Title).ToList();
        }, CancellationToken.None);

        Assert.Equal(["UI dev", "Lumi", "Just chat"], row);
        Assert.True(lineIcon);
        Assert.Equal(("claude-opus-5", "Max", "UI dev"), applied);
        Assert.Equal("Lumi", litOnAnotherModel);
        Assert.Null(retiredModel);
        Assert.Contains("no longer available", retiredNote, StringComparison.Ordinal);
        Assert.Equal(["UI dev", "Just chat"], rowRetired);
        Assert.Equal(("retired-model", "max", "UI work", "bug"), keptThroughEdits);
    }

    [Fact]
    public async Task AStartersCard_ShowsWhatChanged_WhenTheRowRefreshesItsStarterWhereItStands()
    {
        using var session = HeadlessTestSession.Start();
        string? noteBefore = null;
        (bool HasNote, bool Announced) afterPick = (true, false);

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var docs = new Project { Name = "Docs" };
            var data = NewData();
            data.Projects.Add(docs);
            // Kept in a project without recent chats, so it stands in for no learned place, on a model no longer offered.
            data.PinnedSetups.Add(new LaunchpadSetupPin
            {
                ProjectId = docs.Id, ModelId = "retired-model", Effort = "max", Name = "Writing",
            });
            using var surface = NewSurface(data);
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                launchpad.Setups.Single(item => item.Title == "Writing").EditCommand.Execute(null);
                var editor = launchpad.Editor;
                noteBefore = editor.RetiredModelNote;

                // Another model picked in its card changes it where it stands on the row; the card tells its view at once
                // that the fallback is over.
                List<string?> heard = [];
                editor.PropertyChanged += (_, e) => heard.Add(e.PropertyName);
                editor.Models.Single(model => model.ModelId == "claude-opus-5").SelectCommand.Execute(null);
                afterPick = (editor.HasRetiredModelNote,
                    heard.Any(name => string.IsNullOrEmpty(name) || name == nameof(LaunchpadStarterEditor.HasRetiredModelNote)));
            }
            finally
            {
                launchpad.Deactivate();
            }
        }, CancellationToken.None);

        Assert.Contains("no longer available", noteBefore, StringComparison.Ordinal);
        Assert.Equal((false, true), afterPick);
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
                launchpad.Setups.Single(item => !item.IsJustChat).SelectCommand.Execute(null);
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
    public async Task Setups_RefreshProjectAndAgentEditsAfterAnIndexSave()
    {
        using var session = HeadlessTestSession.Start();
        string? renamedTitle = null, renamedMeta = null;
        Guid? remainingProject = Guid.Empty, remainingAgent = Guid.Empty;
        var remainingUses = 0;

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
                renamedTitle = launchpad.Setups.Single(item => !item.IsJustChat).Title;
                renamedMeta = launchpad.Setups.Single(item => !item.IsJustChat).Meta;

                data.Projects.Clear();
                data.Agents.Clear();
                await Task.Run(() => store.SaveAsync());
                Dispatcher.UIThread.RunJobs();
                // The chats now start nowhere in particular: they are Just chat's.
                var remaining = Assert.Single(launchpad.Setups);
                remainingProject = remaining.Spec.ProjectId;
                remainingAgent = remaining.Spec.AgentId;
                remainingUses = remaining.Uses;
            }
            finally
            {
                launchpad.Deactivate();
            }
        }, CancellationToken.None);

        Assert.Equal("Lumi App", renamedTitle);
        Assert.StartsWith("Lumi App · Developer · ", renamedMeta, StringComparison.Ordinal);
        Assert.Null(remainingProject);
        Assert.Null(remainingAgent);
        Assert.Equal(2, remainingUses);
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
    public async Task AStartersCard_KeepsATypedName_ListsModelsAsThePicker_AndTunesTheirEfforts()
    {
        using var session = HeadlessTestSession.Start();
        List<string?>? models = null;
        List<(string Title, bool Current)>? fusionEfforts = null, opusEfforts = null;
        (string? Model, string? Effort, string? Name) afterFusion = default, afterMax = default, afterOpus = default, afterOpusMax = default;
        bool rowsKept = false;
        string? fieldAfterModel = null;
        var lumi = new Project { Name = "Lumi" };

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var data = NewData();
            data.Projects.Add(lumi);
            var research = new LaunchpadSetupPin { ProjectId = lumi.Id, Name = "Research" };
            data.PinnedSetups.Add(research);
            using var surface = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            List<ModelInfo> catalog =
            [
                new ModelInfo { Id = "gpt-5.4", SupportedReasoningEfforts = ["low", "medium", "high"], DefaultReasoningEffort = "medium" },
                new ModelInfo { Id = "claude-opus-5", SupportedReasoningEfforts = ["low", "medium", "high", "max"], DefaultReasoningEffort = "high" },
                new ModelInfo { Id = "auto", Name = "Auto" },
                new ModelInfo { Id = "auto:balance", Name = "Copilot Fusion Standard" },
                new ModelInfo { Id = "auto:hydrafusion", Name = "Copilot Fusion High" },
                new ModelInfo { Id = "auto:hydrafusion-max", Name = "Copilot Fusion Max" },
            ];
            surface.UpdateModelCapabilities(catalog);
            surface.ApplyAvailableModels(catalog.Select(static model => model.Id).ToList(), "gpt-5.4");
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                launchpad.Setups.Single(setup => setup.Title == "Research").EditCommand.Execute(null);
                var editor = launchpad.Editor;

                // As the composer's picker lists them: Copilot Fusion once for its tiers, and no Auto beside it.
                models = editor.Models.Select(static choice => choice.ModelId).ToList();

                // Keeping a typed name (as focus leaving the field does) leaves the rows a click may be landing on.
                var rows = editor.Models;
                editor.Name = "Research notes";
                editor.CommitNameCommand.Execute(null);
                rowsKept = ReferenceEquals(rows, editor.Models);

                // A name typed but not kept yet goes along with a model picked meanwhile.
                editor.Name = "Deep research";
                editor.Models.Single(choice => choice.ModelId == "auto:balance").SelectCommand.Execute(null);
                afterFusion = (research.ModelId, research.Effort, research.Name);
                fieldAfterModel = editor.Name;
                fusionEfforts = editor.Qualities.Select(static quality => (quality.Title, quality.IsCurrent)).ToList();

                // Fusion's tiers are its efforts; any other model keeps its own, which the card tunes the same way.
                editor.Qualities.Single(quality => quality.Title == "Max").SelectCommand.Execute(null);
                afterMax = (research.ModelId, research.Effort, research.Name);
                editor.Models.Single(choice => choice.ModelId == "claude-opus-5").SelectCommand.Execute(null);
                afterOpus = (research.ModelId, research.Effort, research.Name);
                opusEfforts = editor.Qualities.Select(static quality => (quality.Title, quality.IsCurrent)).ToList();
                editor.Qualities.Single(quality => quality.Title == "Max").SelectCommand.Execute(null);
                afterOpusMax = (research.ModelId, research.Effort, research.Name);
            }
            finally
            {
                launchpad.Deactivate();
            }
        }, CancellationToken.None);

        Assert.Equal([null, "auto:balance", "gpt-5.4", "claude-opus-5"], models);
        Assert.True(rowsKept);
        Assert.Equal(("auto:balance", null, "Deep research"), afterFusion);
        Assert.Equal("Deep research", fieldAfterModel);
        Assert.Equal([("Standard", true), ("High", false), ("Max", false)], fusionEfforts);
        Assert.Equal(("auto:hydrafusion-max", null, "Deep research"), afterMax);
        Assert.Equal(("claude-opus-5", "high", "Deep research"), afterOpus);
        Assert.Equal([("Low", false), ("Medium", false), ("High", true), ("Max", false)], opusEfforts);
        Assert.Equal(("claude-opus-5", "max", "Deep research"), afterOpusMax);
    }

    [Fact]
    public async Task AStartersCard_KeepsRenamesRetunesOrDismissesIt()
    {
        using var session = HeadlessTestSession.Start();
        List<string>? learned = null, afterKeep = null, afterRename = null, afterOwnModel = null, afterDefault = null;
        List<string>? afterHide = null, afterRemove = null;
        (bool Learned, string Heading, string Uses, bool CanHide) card = default;
        (bool Follows, bool Kept) editor = default;
        List<(string? Model, bool Current)>? models = null;
        bool renamedInPlace = false, iconShown = false, editorClosedAfterHide = false, justChatCanHide = true;
        string? keptName = null, keptIcon = null;
        Guid?[]? hiddenProjects = null;
        var lumi = new Project { Name = "Lumi" };
        var search = new Project { Name = "Fluent Search" };

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var data = NewData();
            data.Projects.AddRange([lumi, search]);
            data.Chats.AddRange([UsedChat(lumi, null, 1), UsedChat(lumi, null, 2), UsedChat(search, null, 3), UsedChat(search, null, 4)]);
            using var surface = NewSurface(data);
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                learned = Row();

                // A learned starter's card: how often the user starts there, that it follows the default model.
                launchpad.Setups.Single(setup => setup.Title == "Fluent Search").EditCommand.Execute(null);
                card = (launchpad.Editor.IsLearned, launchpad.Editor.Heading, launchpad.Editor.UsesText, launchpad.Editor.CanHide);

                // Rename keeps it, first of the user's, and the card follows it there.
                launchpad.Editor.RenameCommand.Execute(null);
                afterKeep = Row();
                var kept = launchpad.Setups[0];
                editor = (ReferenceEquals(launchpad.Editor.Target, kept), launchpad.Editor.IsKept);

                // A name of its own: the same capsule takes it.
                launchpad.Editor.Name = "Search work";
                launchpad.Editor.CommitNameCommand.Execute(null);
                renamedInPlace = ReferenceEquals(launchpad.Setups[0], kept);
                afterRename = Row();

                // A model of its own leaves the place it was made from to be suggested again; back on the default model it
                // stands in for that place once more.
                models = launchpad.Editor.Models.Select(static choice => (choice.ModelId, choice.IsCurrent)).ToList();
                launchpad.Editor.Models.Single(choice => choice.ModelId == "claude-opus-5").SelectCommand.Execute(null);
                afterOwnModel = Row();
                launchpad.Editor.Icons.Single(choice => choice.Key == "bug").SelectCommand.Execute(null);
                iconShown = launchpad.Setups[0].Badge.HasLineIcon;
                launchpad.Editor.Models[0].SelectCommand.Execute(null);
                afterDefault = Row();
                keptName = data.PinnedSetups.Single().Name;
                keptIcon = data.PinnedSetups.Single().Icon;

                // Not suggested any more, Lumi leaves the row, and its card closes.
                launchpad.Setups.Single(setup => setup.Title == "Lumi").EditCommand.Execute(null);
                launchpad.Editor.HideCommand.Execute(null);
                editorClosedAfterHide = !launchpad.Editor.IsOpen;
                afterHide = Row();
                hiddenProjects = data.HiddenSetups.Select(static pin => pin.ProjectId).ToArray();

                // Just chat is always there.
                launchpad.Setups.Single(setup => setup.IsJustChat).EditCommand.Execute(null);
                justChatCanHide = launchpad.Editor.CanHide;

                // Removed, the kept starter gives way to the place it was made from.
                launchpad.Setups[0].EditCommand.Execute(null);
                launchpad.Editor.RemoveCommand.Execute(null);
                afterRemove = Row();
            }
            finally
            {
                launchpad.Deactivate();
            }

            List<string> Row() => launchpad.Setups.Select(setup => (setup.IsKept ? "*" : "") + setup.Title).ToList();
        }, CancellationToken.None);

        Assert.Equal(["Lumi", "Fluent Search", "Just chat"], learned);
        Assert.Equal((true, "Fluent Search", "2 of your chats in the last 30 days.", true), card);
        Assert.Equal(["*Fluent Search", "Lumi", "Just chat"], afterKeep);
        Assert.Equal((true, true), editor);
        Assert.True(renamedInPlace);
        Assert.Equal(["*Search work", "Lumi", "Just chat"], afterRename);
        Assert.Equal([(null, true), ("gpt-5.4", false), ("claude-opus-5", false)], models);
        Assert.Equal(["*Search work", "Lumi", "Fluent Search", "Just chat"], afterOwnModel);
        Assert.True(iconShown);
        Assert.Equal(["*Search work", "Lumi", "Just chat"], afterDefault);
        Assert.Equal(("Search work", "bug"), (keptName, keptIcon));
        Assert.True(editorClosedAfterHide);
        Assert.Equal(["*Search work", "Just chat"], afterHide);
        Assert.Equal([lumi.Id], hiddenProjects);
        Assert.False(justChatCanHide);
        Assert.Equal(["Fluent Search", "Just chat"], afterRemove);
    }

    [Fact]
    public async Task TheDraftsOwnSetup_ShowsWhenNoStarterFitsIt_AndTheNewStarterCardStartsFromIt()
    {
        using var session = HeadlessTestSession.Start();
        (bool Dashed, string? Title, string Tooltip) dashed = default;
        (bool Dashed, string? Lit, bool Kept, string? Model) afterPin = default;
        (bool IsNew, bool Form, string Name, string Place, string? Model) opened = default;
        string? litOnAnotherModel = null, litAfterAdd = null;
        List<(string? Name, string? Model)>? kept = null;
        List<string>? row = null;
        var openAfterAdd = true;
        var lumi = new Project { Name = "Lumi" };
        var search = new Project { Name = "Fluent Search" };
        var strata = new Project { Name = "Strata" };

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var data = NewData();
            data.Projects.AddRange([lumi, search, strata]);
            data.Chats.AddRange([UsedChat(lumi, null, 1), UsedChat(lumi, null, 2), UsedChat(search, null, 3), UsedChat(search, null, 4)]);
            using var surface = NewSurface(data, extraModel: "gpt-5.5");
            var launchpad = surface.Launchpad;
            var editor = launchpad.Editor;
            launchpad.Activate();
            try
            {
                // Strata, picked in the composer, has no starter: the row shows where the chat starts, and a pin.
                surface.SetProjectId(strata.Id);
                Dispatcher.UIThread.RunJobs();
                dashed = (launchpad.HasUnsavedDraft, launchpad.DraftPlace?.Title, launchpad.KeepDraftTooltip);

                launchpad.KeepDraftCommand.Execute(null);
                afterPin = (launchpad.HasUnsavedDraft, launchpad.ActiveSetup?.Title, launchpad.ActiveSetup?.IsKept == true,
                    data.PinnedSetups.Single().ModelId);

                // Picking a model for this chat makes it the default too, yet the new starter card starts on it as the
                // draft's own, under no name of its own until one is typed.
                surface.SelectedModel = "gpt-5.5";
                Dispatcher.UIThread.RunJobs();
                litOnAnotherModel = launchpad.ActiveSetup?.Title;
                launchpad.OpenNewStarterCommand.Execute(null);
                opened = (editor.IsNew, editor.ShowsForm, editor.Name, editor.NamePlaceholder,
                    editor.Models.Single(static model => model.IsCurrent).ModelId);
                editor.Name = "Strata fast";
                editor.AddCommand.Execute(null);
                openAfterAdd = editor.IsOpen;
                kept = data.PinnedSetups.Select(static pin => (pin.Name, pin.ModelId)).ToList();
                litAfterAdd = launchpad.ActiveSetup?.Title;
                row = launchpad.Setups.Select(static setup => setup.Title).ToList();
            }
            finally
            {
                launchpad.Deactivate();
            }
        }, CancellationToken.None);

        Assert.Equal((true, "Strata", "Keep Strata · Default model on new chats"), dashed);
        Assert.Equal((false, "Strata", true, (string?)null), afterPin);
        Assert.Equal("Strata", litOnAnotherModel);
        Assert.Equal((true, true, "", "Strata", "gpt-5.5"), opened);
        Assert.False(openAfterAdd);
        // Added beside the kept Strata, never renaming it.
        Assert.Equal([(null, null), ("Strata fast", "gpt-5.5")], kept);
        Assert.Equal("Strata fast", litAfterAdd);
        Assert.Equal(["Strata", "Strata fast", "Lumi", "Fluent Search", "Just chat"], row);
    }

    [Fact]
    public async Task NamesakeStarters_SayWhatTellsThemApart()
    {
        using var session = HeadlessTestSession.Start();
        List<(string Title, bool Worktree, string Tell, bool Kept)>? row = null;

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var lumi = new Project { Name = "Lumi" };
            var data = NewData();
            data.Projects.Add(lumi);
            data.Chats.AddRange([UsedChat(lumi, null, 1), UsedChat(lumi, null, 2), InWorktree(UsedChat(lumi, null, 3)), InWorktree(UsedChat(lumi, null, 4))]);
            data.PinnedSetups.Add(new LaunchpadSetupPin { ProjectId = lumi.Id, ModelId = "claude-opus-5" });
            data.PinnedSetups.Add(new LaunchpadSetupPin { ProjectId = lumi.Id, ModelId = "gpt-5.5" });
            using var surface = NewSurface(data, extraModel: "gpt-5.5");
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                row = launchpad.Setups.Select(static setup => (setup.Title, setup.TellsWorktree, setup.TellText, setup.IsKept)).ToList();
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

        // The kept ones say their models, the worktree one says ⑂, and the plain place says nothing.
        Assert.Equal(
            [
                ("Lumi", false, ChatViewModel.FormatModelDisplay("claude-opus-5")!, true),
                ("Lumi", false, ChatViewModel.FormatModelDisplay("gpt-5.5")!, true),
                ("Lumi", false, "", false),
                ("Lumi", true, "", false),
                ("Just chat", false, "", false),
            ],
            row);
    }

    [Fact]
    public async Task TheNewStarterCard_MakesAStarterAnywhere_WithItsOwnCheckoutAndModel_AndLeavesTheDraftAlone()
    {
        using var session = HeadlessTestSession.Start();
        // A folder Lumi takes for a git repository, so Strata can start in a worktree.
        var repo = Path.Combine(Path.GetTempPath(), $"lumi-launchpad-new-starter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        try
        {
            List<string>? projects = null, agents = null, filtered = null;
            List<bool>? currentAtFirst = null;
            bool dismissedKeepsNothing = false, othersAfterFilter = true, openAfterAdd = true, noMatch = false;
            (string Place, bool CanUseWorktree, bool Worktree, bool Choosing) afterPlace = default;
            (string? Model, string? Effort, bool HasQualities) afterModel = default;
            (string? Project, string? Model, string? Default) draftBefore = default, draftAfter = default;
            LaunchpadSetupPin? added = null;
            var strata = new Project { Name = "Strata", WorkingDirectory = repo };

            await session.Dispatch(() =>
            {
                Loc.Load("en");
                var lumi = new Project { Name = "Lumi" };
                var agent = new LumiAgent { Name = "Coding Lumi", IconGlyph = "⚡" };
                var data = NewData();
                data.Projects.AddRange([lumi, strata]);
                data.Agents.Add(agent);
                data.Chats.AddRange([UsedChat(lumi, agent, 1), UsedChat(lumi, agent, 2), UsedChat(strata, null, 3)]);
                using var surface = NewSurface(data);
                var launchpad = surface.Launchpad;
                var editor = launchpad.Editor;
                launchpad.Activate();
                try
                {
                    draftBefore = (surface.SelectedProjectName, surface.SelectedModel, data.Settings.PreferredModel);

                    // Dismissed, the card keeps nothing.
                    launchpad.OpenNewStarterCommand.Execute(null);
                    editor.Name = "Never mind";
                    editor.Close();
                    dismissedKeepsNothing = data.PinnedSetups.Count == 0;

                    // A plain new chat's card starts in Just chat. Its places list every project, most recently used
                    // first, then every agent, its own checked.
                    launchpad.OpenNewStarterCommand.Execute(null);
                    editor.ChoosePlaceCommand.Execute(null);
                    currentAtFirst = [editor.JustChatPlace!.IsCurrent, .. editor.PlaceProjects.Select(static place => place.IsCurrent)];
                    projects = editor.PlaceProjects.Select(static place => place.Title).ToList();
                    agents = editor.PlaceAgents.Select(static place => place.Title).ToList();
                    editor.PlaceQuery = "str";
                    filtered = editor.PlaceProjects.Select(static place => place.Title).ToList();
                    othersAfterFilter = editor.HasJustChatPlace || editor.HasPlaceAgents;

                    // Enter picks Strata, a git project, which can start in a worktree; the starter gets a model and
                    // effort of its own, an icon and a name.
                    editor.SelectFirstPlace();
                    afterPlace = (editor.NamePlaceholder, editor.CanUseWorktree, editor.IsWorktree, editor.IsChoosingPlace);
                    editor.ToggleWorktreeCommand.Execute(null);
                    editor.Models.Single(static model => model.ModelId == "claude-opus-5").SelectCommand.Execute(null);
                    editor.Qualities.Single(static quality => quality.Title == ModelSelectionHelper.EffortToDisplay("max"))
                        .SelectCommand.Execute(null);
                    afterModel = (editor.Target!.Spec.ModelId, editor.Target.Spec.Effort, editor.HasQualities);
                    editor.Icons.Single(static icon => icon.Key == "layers").SelectCommand.Execute(null);
                    editor.Name = "UI dev";
                    editor.AddCommand.Execute(null);
                    openAfterAdd = editor.IsOpen;
                    added = data.PinnedSetups.SingleOrDefault();
                    draftAfter = (surface.SelectedProjectName, surface.SelectedModel, data.Settings.PreferredModel);

                    launchpad.OpenNewStarterCommand.Execute(null);
                    editor.ChoosePlaceCommand.Execute(null);
                    editor.PlaceQuery = "nothing like this";
                    noMatch = editor.HasNoPlaceMatch;
                    editor.Close();
                }
                finally
                {
                    launchpad.Deactivate();
                }
            }, CancellationToken.None);

            Assert.True(dismissedKeepsNothing);
            Assert.Equal([true, false, false], currentAtFirst);
            Assert.Equal(["Lumi", "Strata"], projects);
            Assert.Equal(["Coding Lumi"], agents);
            Assert.Equal(["Strata"], filtered);
            Assert.False(othersAfterFilter);
            Assert.Equal(("Strata", true, false, false), afterPlace);
            Assert.Equal(("claude-opus-5", "max", true), afterModel);
            Assert.False(openAfterAdd);
            Assert.NotNull(added);
            Assert.Equal((strata.Id, (Guid?)null, true, "claude-opus-5", "max", "UI dev", "layers"),
                (added.ProjectId, added.AgentId, added.UseWorktree, added.ModelId, added.Effort, added.Name, added.Icon));
            // Making a starter is not starting there: the draft and the default model stay as they were.
            Assert.Equal(draftBefore, draftAfter);
            Assert.True(noMatch);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task JustChat_IsAlwaysAStarter_ThatStartsOnTheDefaultModelWhateverTheUserPicks()
    {
        using var session = HeadlessTestSession.Start();
        (int Uses, bool CanHide, bool Lit) atFirst = default;
        (string? Project, string? Model, bool Lit) back = default;
        bool litOnAnotherModel = false;

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var lumi = new Project { Name = "Lumi" };
            var data = NewData();
            data.Projects.Add(lumi);
            // Plain chats on Opus while new chats start on GPT 5.4: still Just chat, which follows the default model.
            data.Chats.AddRange([OnOpus(PlainChat(1)), OnOpus(PlainChat(2)), UsedChat(lumi, null, 3), UsedChat(lumi, null, 4)]);
            using var surface = NewSurface(data);
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                var justChat = launchpad.Setups.Single(setup => setup.IsJustChat);
                atFirst = (justChat.Uses, justChat.CanHide, justChat.IsActive);

                launchpad.Setups.Single(setup => setup.Title == "Lumi").SelectCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                justChat.SelectCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                back = (surface.SelectedProjectName, surface.SelectedModel, justChat.IsActive);

                surface.SelectedModel = "claude-opus-5";
                Dispatcher.UIThread.RunJobs();
                litOnAnotherModel = justChat.IsActive;
            }
            finally
            {
                launchpad.Deactivate();
            }
        }, CancellationToken.None);

        Assert.Equal((2, false, true), atFirst);
        Assert.Equal(((string?)null, "gpt-5.4", true), back);
        Assert.True(litOnAnotherModel);

        static Chat OnOpus(Chat chat)
        {
            chat.LastModelUsed = "claude-opus-5";
            chat.LastReasoningEffortUsed = "max";
            return chat;
        }
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

                launchpad.Setups.Single(item => !item.IsJustChat).SelectCommand.Execute(null);
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
                              && view.FindControl<ItemsControl>("LaunchpadSetupTiles")!.IsEffectivelyVisible
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
