using System.Reflection;
using System.Runtime.ExceptionServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Mobile.Tests;

[Collection("Headless mobile UI")]
public sealed class ObserverPruningTests
{
    [Fact]
    public async Task ChatDetailView_RemovedAndReplacedRowsStopRequestingTranscriptFollow()
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;

        await session.Dispatch(async () =>
        {
            MobileShellViewModel? shell = null;
            Window? window = null;
            try
            {
                shell = new MobileShellViewModel(store: session.NewStore(), post: action => action());
                shell.Chat.Reset(Guid.NewGuid(), "Observer pruning");

                var removed = Assistant("assistant-1", "initial");
                var turn = new TranscriptTurnViewModel("turn-1");
                turn.Items.Add(removed);
                shell.Chat.Turns.Add(turn);

                var view = new ChatDetailView { DataContext = shell };
                window = new Window { Width = 412, Height = 892, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                removed.Text = "live update";
                Assert.True(IsFollowQueued(view), "an active assistant row must still drive transcript follow");
                Dispatcher.UIThread.RunJobs();

                turn.Items.Remove(removed);
                Dispatcher.UIThread.RunJobs();
                removed.Text = "stale update";
                Assert.False(IsFollowQueued(view), "an individually removed row must be unsubscribed");

                var replaced = Assistant("assistant-2", "replacement source");
                turn.Items.Add(replaced);
                Dispatcher.UIThread.RunJobs();

                var replacement = Reasoning("reasoning-1", "replacement target");
                turn.Items[0] = replacement;
                Dispatcher.UIThread.RunJobs();
                replaced.Text = "stale replacement";
                Assert.False(IsFollowQueued(view), "the old side of a collection replacement must be unsubscribed");

                replacement.Text = "live reasoning";
                Assert.True(IsFollowQueued(view), "the new side of a collection replacement must be observed");
                Dispatcher.UIThread.RunJobs();

                shell.Chat.Turns.Clear();
                Dispatcher.UIThread.RunJobs();
                replacement.Text = "stale after reset";
                Assert.False(IsFollowQueued(view), "a collection reset must release rows from removed turns");
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window?.Close();
                if (shell is not null)
                    await shell.DisposeAsync();
            }
        }, CancellationToken.None);

        failure?.Throw();
    }

    [Fact]
    public async Task MobilePresenceController_PrunesRemovedItemsAndDetachesThePreviousShell()
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;

        await session.Dispatch(async () =>
        {
            MobileShellViewModel? first = null;
            MobileShellViewModel? second = null;
            try
            {
                first = LiveShell(session, "First");
                var host = new Panel();
                using var controller = new MobilePresenceController(host);
                controller.Attach(first);
                Assert.Equal(PresenceState.Idle, controller.Visual.State);

                var question = Question("question-1");
                var turn = new TranscriptTurnViewModel("turn-1");
                turn.Items.Add(question);
                first.Chat.Turns.Add(turn);
                Assert.Equal(PresenceState.Attention, controller.Visual.State);

                turn.Items.Remove(question);
                first.Chat.IsStreaming = true;
                first.Chat.IsStreaming = false;
                Assert.Equal(PresenceState.Idle, controller.Visual.State);

                // Re-adding the exact same object only reacts if the removed-item set was pruned.
                turn.Items.Add(question);
                Assert.Equal(PresenceState.Attention, controller.Visual.State);

                turn.Items[turn.Items.IndexOf(question)] = Assistant("replacement", "Done");
                first.Chat.IsStreaming = true;
                first.Chat.IsStreaming = false;
                Assert.Equal(PresenceState.Idle, controller.Visual.State);

                turn.Items.Add(question);
                Assert.Equal(
                    PresenceState.Attention,
                    controller.Visual.State);

                first.Chat.Turns.Clear();
                first.Chat.IsStreaming = true;
                first.Chat.IsStreaming = false;
                Assert.Equal(PresenceState.Idle, controller.Visual.State);

                first.Chat.Turns.Add(turn);
                Assert.Equal(
                    PresenceState.Attention,
                    controller.Visual.State);

                second = LiveShell(session, "Second");
                controller.Attach(second);
                Assert.Equal(PresenceState.Idle, controller.Visual.State);

                turn.Items.Add(Question("stale-shell-question"));
                Assert.Equal(
                    PresenceState.Idle,
                    controller.Visual.State);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                if (first is not null)
                    await first.DisposeAsync();
                if (second is not null)
                    await second.DisposeAsync();
            }
        }, CancellationToken.None);

        failure?.Throw();
    }

    private static MobileShellViewModel LiveShell(HeadlessMobileSession session, string title)
    {
        var shell = new MobileShellViewModel(store: session.NewStore(), post: action => action())
        {
            IsConnected = true,
            IsHostReady = true
        };
        shell.Chat.Reset(Guid.NewGuid(), title);
        return shell;
    }

    private static AssistantItemViewModel Assistant(string id, string text) =>
        new(new RemoteTranscriptItem
        {
            Id = id,
            Kind = RemoteProtocol.ItemKinds.Assistant,
            Text = text
        });

    private static ReasoningItemViewModel Reasoning(string id, string text) =>
        new(new RemoteTranscriptItem
        {
            Id = id,
            Kind = RemoteProtocol.ItemKinds.Reasoning,
            Text = text
        });

    private static QuestionItemViewModel Question(string id) =>
        new(new RemoteTranscriptItem
        {
            Id = id,
            Kind = RemoteProtocol.ItemKinds.Question,
            Question = new RemoteQuestion
            {
                QuestionId = id,
                Text = "Choose",
                Options = ["A", "B"]
            }
        });

    private static bool IsFollowQueued(ChatDetailView view) =>
        (bool)(typeof(ChatDetailView)
            .GetField("_followQueued", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(view)
            ?? throw new InvalidOperationException("ChatDetailView follow queue field was not found."));
}
