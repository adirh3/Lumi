using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.Services;
using Lumi.Services.Remote;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// A finished reply takes Lumi's follow-up suggestions to the phone: it waits a moment for them, goes
/// with them, and goes without them once the wait is over or none are coming.
/// </summary>
[Collection("Headless UI")]
public sealed class PhoneNotificationSuggestionTests
{
    private static readonly Guid ChatId = Guid.Parse("3f2a1b4c-5d6e-4f70-8a9b-0c1d2e3f4a5b");

    [Fact]
    public Task AFinishedReplyWaitsForItsSuggestionsAndTakesThemAlong() => RunAsync((rig, _) =>
    {
        rig.Events.PublishAttention(new ChatAttention(ChatId, ChatAttentionKind.Reply, "Done."));
        Assert.Empty(rig.Center.Delivered);
        Assert.Equal(1, rig.Center.WaitingCount);

        // One too long to show whole is left out rather than cut, since it is sent as written.
        var tooLong = new string('x', RemoteProtocol.MobileNotificationSuggestionLimit + 1);
        rig.Events.PublishSuggestions(ChatId, ["Run the tests", "  ", "Show me the diff", "Run the tests", tooLong, "Ship it", "More"]);

        var reply = Assert.Single(rig.Center.Delivered);
        Assert.Equal(["Run the tests", "Show me the diff", "Ship it"], reply.Suggestions);
    });

    [Fact]
    public Task WithoutSuggestionsInTimeAReplyGoesAsItIs() => RunAsync((rig, time) =>
    {
        rig.Events.PublishAttention(new ChatAttention(ChatId, ChatAttentionKind.Reply, "Done."));
        time.Advance(PhoneNotificationCenter.SuggestionWait - TimeSpan.FromSeconds(1));
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(rig.Center.Delivered);

        time.Advance(TimeSpan.FromSeconds(1));
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(Assert.Single(rig.Center.Delivered).Suggestions);

        // Suggestions written after it went leave it as the phone shows it.
        rig.Events.PublishSuggestions(ChatId, ["Too late"]);
        Assert.Empty(Assert.Single(rig.Center.Delivered).Suggestions);
    });

    [Fact]
    public Task AReplyGoesAtOnceWhenNoSuggestionsAreComing() => RunAsync((rig, _) =>
    {
        rig.Events.PublishAttention(new ChatAttention(ChatId, ChatAttentionKind.Reply, "Done."));
        rig.Events.PublishSuggestions(ChatId, []);
        Assert.Single(rig.Center.Delivered);

        // Questions never wait for suggestions.
        rig.Events.PublishAttention(new ChatAttention(ChatId, ChatAttentionKind.Question, "Which city?", "question-1"));
        Assert.Equal(["Done.", "Which city?"], rig.Center.Delivered.Select(static item => item.Text).Order());

        // With suggestions turned off, replies don't wait either.
        rig.Data.Settings.SuggestFollowUps = false;
        rig.Events.PublishAttention(new ChatAttention(ChatId, ChatAttentionKind.Reply, "Done again."));
        Assert.Equal(["Done again.", "Which city?"], rig.Center.Delivered.Select(static item => item.Text).Order());
    });

    [Fact]
    public Task SuggestionsJoinAReplyWaitingForTheUserToLeave() => RunAsync((rig, _) =>
    {
        rig.Presence.State = new DesktopPresenceState(TimeSpan.Zero, false);
        rig.Events.PublishAttention(new ChatAttention(ChatId, ChatAttentionKind.Reply, "Done."));
        rig.Events.PublishSuggestions(ChatId, ["Run the tests"]);
        // A later settle for the chat, such as another idle with nothing new to suggest, leaves them.
        rig.Events.PublishSuggestions(ChatId, []);
        Assert.Empty(rig.Center.Delivered);

        rig.Presence.State = Away;
        rig.Center.Evaluate();
        Assert.Equal(["Run the tests"], Assert.Single(rig.Center.Delivered).Suggestions);
    });

    private static readonly DesktopPresenceState Away = new(TimeSpan.FromHours(1), false);

    private sealed record Rig(PhoneNotificationCenter Center, ChatEventHub Events, AppData Data, FakePresence Presence);

    private sealed class FakePresence : IDesktopPresenceProbe
    {
        public DesktopPresenceState State { get; set; } = Away;

        public DesktopPresenceState Read() => State;
    }

    private static async Task RunAsync(Action<Rig, ManualTimeProvider> body)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var data = new AppData
            {
                Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false },
                Chats = [new Chat { Id = ChatId, Title = "Trip plan", UpdatedAt = DateTimeOffset.Now }]
            };
            var dataStore = new DataStore(data);
            using var main = new MainViewModel(dataStore, TestCopilot.Shared, new UpdateService(), initializeCopilotOnStartup: false);
            var time = new ManualTimeProvider();
            var presence = new FakePresence();
            using var center = new PhoneNotificationCenter(dataStore, main, () => null, presence, time, () => false);
            center.Attach("run-1");
            body(new Rig(center, main.ChatEvents, data, presence), time);
        }, CancellationToken.None);
    }
}
