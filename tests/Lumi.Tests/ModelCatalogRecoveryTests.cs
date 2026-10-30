using System.Reflection;
using Avalonia.Threading;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Remote;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ModelCatalogRecoveryTests
{
    [Fact]
    public async Task SuccessfulRefreshAfterTimeoutRecoversDesktopAndPhoneWithoutResettingSelections()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            await using var connection = new TestCopilot.SimulatedConnection();
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    PreferredModel = "gpt-6-astra",
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            };
            var store = new DataStore(data);
            using var surfaces = new ChatSessionStore(store, TestCopilot.Shared, new ChatSurfaceRegistry());
            using var main = new MainViewModel(
                store, TestCopilot.Shared, new UpdateService(), chatSessionStore: surfaces,
                startBackgroundJobs: false, initializeCopilotOnStartup: false);
            var otherSurface = surfaces.AcquireDraft(null);
            otherSurface.CurrentChat = new Chat
            {
                Messages = [new ChatMessage { Role = "user", Content = "Existing chat" }]
            };
            otherSurface.SelectedModel = "gpt-6-sol";
            var requests = 0;
            var published = 0;
            var models = new List<ModelInfo>
            {
                new() { Id = "gpt-6-astra" },
                new() { Id = "gpt-6-sol" },
                new() { Id = "claude-sonnet-5.5" }
            };
            var cache = new ModelCatalogCache(
                _ => ++requests == 1
                    ? Task.FromException<ModelCatalogSnapshot>(new TimeoutException("Model catalog request timed out"))
                    : Task.FromResult(new ModelCatalogSnapshot(models, ModelCatalogCache.Empty)));
            cache.Changed += snapshot =>
            {
                published++;
                Publish(main, snapshot);
            };

            var failure = await Assert.ThrowsAsync<TimeoutException>(() => cache.GetModelsAsync());
            main.ConnectionStatus = string.Format(Loc.Status_ConnectionFailed, failure.Message);
            Assert.False(main.IsConnected);
            Assert.DoesNotContain("claude-sonnet-5.5", main.ChatVM.AvailableModels);

            Assert.True(await cache.RefreshAsync());
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

            Assert.True(main.IsConnected);
            Assert.Equal(Loc.Status_Connected, main.ConnectionStatus);
            var expected = models.Select(model => model.Id).ToArray();
            Assert.Equal(expected, main.ChatVM.AvailableModels);
            Assert.Equal(expected, otherSurface.AvailableModels);
            Assert.Equal(expected, main.SettingsVM.AvailableModels);
            Assert.Equal("gpt-6-astra", main.ChatVM.SelectedModel);
            Assert.Equal("gpt-6-sol", otherSurface.SelectedModel);
            Assert.Equal("gpt-6-astra", main.SettingsVM.PreferredModel);
            Assert.Equal("gpt-6-astra", data.Settings.PreferredModel);
            Assert.Equal("auto", data.Settings.BackgroundModel);

            var phone = RemoteProjector.BuildSnapshot(store, main, main.ChatVM.AvailableModels.ToArray());
            Assert.True(phone.IsConnected);
            Assert.Equal(Loc.Status_Connected, phone.ConnectionStatus);
            Assert.Equal(expected, phone.Settings.AvailableModels);

            Assert.False(await cache.RefreshAsync(force: true));
            Assert.Equal(1, published);
            Assert.Equal("gpt-6-sol", otherSurface.SelectedModel);
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedRefreshDoesNotOverrideInitializationOrDisposal(bool dispose)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            await using var connection = new TestCopilot.SimulatedConnection();
            using var main = new MainViewModel(
                new DataStore(new AppData()), TestCopilot.Shared, new UpdateService(),
                startBackgroundJobs: false, initializeCopilotOnStartup: false);
            Publish(main, new ModelCatalogSnapshot(
                [new ModelInfo { Id = "claude-sonnet-5.5" }], ModelCatalogCache.Empty));
            if (dispose)
                main.Dispose();
            else
                typeof(MainViewModel).GetField("_isRefreshingCopilotState", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(main, true);

            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            Assert.False(main.IsConnected);
            Assert.DoesNotContain("claude-sonnet-5.5", main.ChatVM.AvailableModels);
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(ConnectionState.Disconnected, false)]
    [InlineData(ConnectionState.Error, false)]
    [InlineData(ConnectionState.Connected, true)]
    public async Task QueuedRefreshRejectsConnectionLossOrGenerationChange(
        ConnectionState state, bool advanceGeneration)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            await using var connection = new TestCopilot.SimulatedConnection();
            using var main = new MainViewModel(
                new DataStore(new AppData()), TestCopilot.Shared, new UpdateService(),
                startBackgroundJobs: false, initializeCopilotOnStartup: false);
            main.ConnectionStatus = "Catalog request timed out";
            var generation = TestCopilot.Shared.ConnectionGeneration;
            Publish(main, new ModelCatalogSnapshot(
                [new ModelInfo { Id = "claude-sonnet-5.5" }], ModelCatalogCache.Empty));
            connection.SetState(state);
            if (advanceGeneration)
                connection.AdvanceGeneration();
            else
                Assert.Equal(generation, TestCopilot.Shared.ConnectionGeneration);

            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

            Assert.False(main.IsConnected);
            Assert.Equal("Catalog request timed out", main.ConnectionStatus);
            Assert.DoesNotContain("claude-sonnet-5.5", main.ChatVM.AvailableModels);
            Assert.DoesNotContain("claude-sonnet-5.5", main.SettingsVM.AvailableModels);
        }, CancellationToken.None);
    }

    private static void Publish(MainViewModel main, ModelCatalogSnapshot snapshot)
        => typeof(MainViewModel).GetMethod("OnModelCatalogChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(main, [snapshot]);
}
