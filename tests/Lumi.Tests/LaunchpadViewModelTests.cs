using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class LaunchpadViewModelTests
{
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
    public async Task PickUp_LeadsWithWhatNeedsTheUser_AndFollowsLiveChanges()
    {
        using var session = HeadlessTestSession.Start();
        List<string>? titles = null;
        string? waitingLabel = null, waitingMeta = null, brief = null, automations = null;
        Guid? revealed = null;
        Guid replyId = Guid.Empty;
        LaunchpadChatState? tripStateAfterStart = null;
        string? briefAfterStart = null;
        var liveAfterStart = false;

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
            var trip = new Chat { Title = "Trip", MessageCount = 2, UpdatedAt = now.AddMinutes(-5), Preview = "Booked." };
            var watcher = new Chat { Title = "Watcher", MessageCount = 9, UpdatedAt = now };
            var reminded = new Chat { Title = "Release prep", MessageCount = 3, UpdatedAt = now.AddMinutes(-2) };
            replyId = reply.Id;

            var data = NewData();
            data.Chats.AddRange([waiting, reply, trip, watcher, reminded]);
            data.BackgroundJobs.Add(new BackgroundJob
            {
                ChatId = watcher.Id,
                Name = "Nightly check",
                IsEnabled = true,
                NextRunAt = now.AddMinutes(90),
            });
            // A one-off reminder that already ran leaves its chat an ordinary conversation.
            data.BackgroundJobs.Add(new BackgroundJob { ChatId = reminded.Id, Name = "Reminder", IsEnabled = false });

            using var surface = NewSurface(data);
            surface.RevealChatRequested += id => revealed = id;
            var launchpad = surface.Launchpad;
            launchpad.Activate();
            try
            {
                titles = launchpad.PickUpItems.Select(item => item.Title).ToList();
                waitingLabel = launchpad.PickUpItems[0].StateLabel;
                waitingMeta = launchpad.PickUpItems[0].Meta;
                brief = launchpad.Brief;
                automations = launchpad.AutomationSummary;

                launchpad.PickUpItems[1].OpenCommand.Execute(null);

                trip.IsRunning = true;
                Dispatcher.UIThread.RunJobs();
                tripStateAfterStart = launchpad.PickUpItems.Single(item => item.Title == "Trip").State;
                briefAfterStart = launchpad.Brief;
                liveAfterStart = launchpad.HasLiveWork;
            }
            finally
            {
                launchpad.Deactivate();
            }
        }, CancellationToken.None);

        // The active automation's chat stays out of the list until it has something unread.
        Assert.Equal(["Release notes", "Budget", "Release prep", "Trip"], titles);
        Assert.Equal("Needs your answer", waitingLabel);
        Assert.Equal("Which branch should I ship?", waitingMeta);
        Assert.Equal("1 chat needs your answer · 1 new reply", brief);
        Assert.StartsWith("1 automation active · next: Nightly check, ", automations, StringComparison.Ordinal);
        Assert.Equal(replyId, revealed);

        Assert.Equal(LaunchpadChatState.Working, tripStateAfterStart);
        Assert.Equal("1 chat needs your answer · 1 new reply · 1 chat working", briefAfterStart);
        Assert.True(liveAfterStart);
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
                cardVisible = view.FindControl<Border>("LaunchpadActivity")!.IsEffectivelyVisible;
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

    private static ChatViewModel NewSurface(AppData data)
    {
        var surface = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
        surface.UpdateModelCapabilities(
        [
            new ModelInfo { Id = "gpt-5.4", SupportedReasoningEfforts = ["low", "medium", "high"], DefaultReasoningEffort = "medium" },
            new ModelInfo { Id = "claude-opus-5", SupportedReasoningEfforts = ["low", "medium", "high", "max"], DefaultReasoningEffort = "high" },
        ]);
        surface.ApplyAvailableModels(["gpt-5.4", "claude-opus-5"], "gpt-5.4");
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
}
