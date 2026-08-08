using Avalonia.Threading;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.Services;
using Lumi.Services.Remote;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class RemotePreviewThreadingTests
{
    [Fact]
    public async Task ColdChatUsesPersistedPreviewWithoutLoadingTranscript()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var chat = new Chat
            {
                Title = "Cold",
                MessageCount = 12_000,
                Preview = "Persisted bounded preview",
                UpdatedAt = DateTimeOffset.Now
            };
            var dataStore = new DataStore(new AppData { Chats = [chat] });
            using var main = new MainViewModel(dataStore, TestCopilot.Shared, new UpdateService());
            WaitForDesktopInitialization(main);

            var projected = RemoteProjector.BuildChatPage(
                    dataStore,
                    main,
                    offset: 0,
                    limit: RemoteProtocol.ChatPageSize,
                    query: null,
                    projectId: null)
                .Groups.SelectMany(group => group.Chats)
                .Single();

            Assert.Empty(chat.Messages);
            Assert.Equal("Persisted bounded preview", projected.Preview);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LargeChatIndexesAreProjectedAsBoundedSearchablePages()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var chats = Enumerable.Range(0, 7_500)
                .Select(index => new Chat
                {
                    Id = Guid.NewGuid(),
                    Title = index == 7_499 ? "Needle at the end" : $"Chat {index:D4}",
                    Preview = $"Preview {index:D4}",
                    UpdatedAt = DateTimeOffset.Now.AddMinutes(-index)
                })
                .ToList();
            var dataStore = new DataStore(new AppData { Chats = chats });
            using var main = new MainViewModel(dataStore, TestCopilot.Shared, new UpdateService());
            WaitForDesktopInitialization(main);

            var first = RemoteProjector.BuildChatPage(
                dataStore,
                main,
                offset: 0,
                limit: RemoteProtocol.ChatPageSize,
                query: null,
                projectId: null);
            var search = RemoteProjector.BuildChatPage(
                dataStore,
                main,
                offset: 0,
                limit: RemoteProtocol.ChatPageSize,
                query: "Needle at the end",
                projectId: null);

            Assert.Equal(7_500, first.TotalCount);
            Assert.True(first.HasMore);
            Assert.Equal(RemoteProtocol.ChatPageSize, first.Groups.Sum(group => group.Chats.Count));
            Assert.Equal("Needle at the end", Assert.Single(search.Groups.SelectMany(group => group.Chats)).Title);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChatContentDeletionRefreshesBoundCollectionsOnTheUiThread()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var chat = new Chat
            {
                Id = Guid.NewGuid(),
                Title = "Delete me",
                UpdatedAt = DateTimeOffset.Now
            };
            var dataStore = new DataStore(new AppData { Chats = [chat] });
            using var main = new MainViewModel(dataStore, TestCopilot.Shared, new UpdateService());
            WaitForDesktopInitialization(main);
            var handler = typeof(MainViewModel).GetMethod(
                "OnDataStoreChatContentChanged",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(handler);
            var collectionChangedOnUiThread = false;
            main.ChatGroups.CollectionChanged += (_, _) =>
                collectionChangedOnUiThread = Dispatcher.UIThread.CheckAccess();

            dataStore.Data.Chats.Remove(chat);
            await Task.Run(() => handler!.Invoke(main, [chat.Id]));
            Dispatcher.UIThread.RunJobs();

            Assert.True(collectionChangedOnUiThread);
        }, CancellationToken.None);
    }

    private static void WaitForDesktopInitialization(MainViewModel main)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (main.IsConnecting && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }

        Assert.False(main.IsConnecting, "The shared test Copilot service did not finish initializing.");
    }
}
