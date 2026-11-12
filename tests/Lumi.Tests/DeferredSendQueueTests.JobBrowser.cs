using Avalonia.Threading;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed partial class DeferredSendQueueTests
{
    [Fact]
    [Trait("Category", "CoreQA")]
    public async Task BackgroundJobSendKeepsBrowserAfterAcknowledgementAndReleasesItOnSessionIdle()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            await using var connection = new TestCopilot.SimulatedConnection();
            using var host = DeferredSendHost.Create();
            using var rpc = new AbortRpc();
            host.AttachSession(rpc, subscribe: true);
            var interactive = host.ViewModel.GetOrCreateBrowserService(host.Chat.Id);

            await host.ViewModel.SendBackgroundJobMessageAsync(new BackgroundJob
            {
                ChatId = host.Chat.Id,
                Name = "Browser review",
                Prompt = "Review the fixture."
            }, "scheduled").WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, rpc.SendCount);
            var job = host.ViewModel.GetOrCreateBrowserService(host.Chat.Id);
            Assert.NotSame(interactive, job);
            Assert.Single(job.Tabs);

            rpc.Emit(new UserMessageEvent
            {
                Data = new UserMessageData { Content = host.Chat.Messages[0].Content }
            });
            rpc.Emit(new AssistantTurnStartEvent { Data = new AssistantTurnStartData { TurnId = "job-turn" } });
            rpc.Emit(new AssistantTurnEndEvent { Data = new AssistantTurnEndData { TurnId = "job-turn" } });
            Dispatcher.UIThread.RunJobs();
            Assert.Single(job.Tabs);

            rpc.Emit(new SessionIdleEvent { Data = new SessionIdleData() });
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(job.Tabs);
            Assert.Same(interactive, host.ViewModel.GetBrowserServiceForChat(host.Chat.Id));
            Assert.Single(interactive.Tabs);
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "CoreQA")]
    public async Task BackgroundJobInvalidatedAfterAcceptanceReleasesItsBrowserWithoutAnIdleEvent()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            await using var connection = new TestCopilot.SimulatedConnection();
            using var host = DeferredSendHost.Create();
            using var rpc = new AbortRpc();
            host.AttachSession(rpc, subscribe: true);
            var interactive = host.ViewModel.GetOrCreateBrowserService(host.Chat.Id);
            BrowserService? job = null;

            await Assert.ThrowsAsync<BackgroundJobDeliveryInvalidatedException>(() =>
                host.ViewModel.SendBackgroundJobMessageAsync(new BackgroundJob
                {
                    ChatId = host.Chat.Id,
                    Name = "Invalidated review",
                    Prompt = "Do not send."
                }, "scheduled", validateDelivery: () =>
                {
                    var current = host.ViewModel.GetBrowserServiceForChat(host.Chat.Id);
                    if (!ReferenceEquals(current, interactive))
                    {
                        job = current;
                        throw new BackgroundJobDeliveryInvalidatedException();
                    }
                }));

            Assert.NotNull(job);
            Assert.Empty(job.Tabs);
            Assert.Equal(0, rpc.SendCount);
            Assert.Empty(host.Chat.Messages);
            Assert.Same(interactive, host.ViewModel.GetBrowserServiceForChat(host.Chat.Id));
        }, CancellationToken.None);
    }
}
