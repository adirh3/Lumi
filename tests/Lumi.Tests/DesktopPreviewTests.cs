using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class DesktopPreviewTests
{
    [Fact]
    public void ScreenshotBytesAreExcludedFromDiagnosticJson()
    {
        var frame = CreateFrame();
        var update = CreateUpdate(Guid.NewGuid(), frame: frame);
        var json = JsonSerializer.Serialize(update, new JsonSerializerOptions
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        });

        Assert.DoesNotContain(nameof(DesktopPreviewFrame.PngBytes), json);
        Assert.DoesNotContain(Convert.ToBase64String(frame.PngBytes.Span), json);
        Assert.Contains(nameof(DesktopPreviewFrame.CapturedAt), json);
        Assert.Contains(nameof(DesktopPreviewFrame.PixelWidth), json);
    }

    [Fact]
    public async Task PassiveUpdatesKeepTheFrameAndCaptureTimeWithoutShowingOrRefreshing()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var chat = new Chat();
            using var vm = new ChatViewModel(CreateStore(), TestCopilot.Shared) { CurrentChat = chat };
            var shows = 0;
            var captures = 0;
            vm.DesktopShowRequested += _ => shows++;
            vm.DesktopPreviewRefreshAsync = () => { captures++; return Task.CompletedTask; };
            var frame = CreateFrame();
            var update = CreateUpdate(chat.Id, frame: frame);

            vm.PublishDesktopPreview(update);
            var capturedAtText = vm.DesktopPreviewCapturedAtText;
            vm.PublishDesktopPreview(update with
            {
                ActionText = "Read status",
                StatusText = "Ready",
                UpdatedAt = update.UpdatedAt.AddMinutes(5),
                NewFrame = null
            });

            Assert.True(vm.HasDesktopPreview);
            Assert.Equal(OperatingSystem.IsWindows(), vm.ShowDesktopToggle);
            Assert.Equal(OperatingSystem.IsWindows(),
                Assert.Single(vm.WorkspaceCategories, chip => chip.Category == WorkspaceCategory.Desktop).IsAvailable);
            Assert.False(vm.HasWorkspaceContent);
            Assert.Equal(!OperatingSystem.IsWindows(), vm.ShowWorkspaceEmptyState);
            Assert.False(vm.IsDesktopOpen);
            Assert.Same(frame, vm.DesktopPreviewFrame);
            Assert.Equal(capturedAtText, vm.DesktopPreviewCapturedAtText);
            Assert.Equal(frame.CapturedAt, vm.DesktopPreviewFrame!.CapturedAt);
            Assert.Equal(update.UpdatedAt.AddMinutes(5), vm.DesktopPreview!.UpdatedAt);
            Assert.Equal("Ready", vm.DesktopPreview.StatusText);
            Assert.Equal(0, shows);
            Assert.Equal(0, captures);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DelayedRefreshCannotReplaceANewerTargetOrAction()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var chat = new Chat();
            using var vm = new ChatViewModel(CreateStore(), TestCopilot.Shared) { CurrentChat = chat };
            var first = CreateUpdate(chat.Id, frame: CreateFrame());
            vm.PublishDesktopPreview(first);
            var origin = vm.DesktopPreview!;
            vm.PublishDesktopPreview(first with
            {
                WindowKey = "window:second",
                WindowTitle = "Second window",
                UpdatedAt = first.UpdatedAt.AddSeconds(1),
                NewFrame = null
            });
            var second = vm.DesktopPreview!;
            vm.PublishDesktopRefresh(origin, first with { UpdatedAt = first.UpdatedAt.AddSeconds(2) });
            Assert.Same(second, vm.DesktopPreview);

            vm.PublishDesktopPreview(second with { ActionText = "New action", UpdatedAt = second.UpdatedAt.AddSeconds(1) });
            var current = vm.DesktopPreview!;
            vm.PublishDesktopRefresh(second, second with { UpdatedAt = current.UpdatedAt.AddSeconds(1) });
            Assert.Same(current, vm.DesktopPreview);

            var refreshed = CreateFrame(current.UpdatedAt);
            vm.PublishDesktopRefresh(current, current with { NewFrame = refreshed });
            Assert.Same(refreshed, vm.DesktopPreviewFrame);
            Assert.Equal("New action", vm.DesktopPreview!.ActionText);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PublicationIsMarshaledAndQueuedOldChatUpdatesAreRejected()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var chat = new Chat();
            using var vm = new ChatViewModel(CreateStore(), TestCopilot.Shared) { CurrentChat = chat };
            var allNotificationsOnUiThread = true;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChatViewModel.DesktopPreview))
                    allNotificationsOnUiThread &= Dispatcher.UIThread.CheckAccess();
            };
            var update = CreateUpdate(chat.Id, frame: CreateFrame());

            void PublishFromWorker(DesktopPreviewUpdate value)
            {
                Exception? failure = null;
                var workerHadUiAccess = true;
                var worker = new Thread(() =>
                {
                    try
                    {
                        workerHadUiAccess = Dispatcher.UIThread.CheckAccess();
                        vm.PublishDesktopPreview(value);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                }) { IsBackground = true };
                worker.Start();
                Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
                Assert.Null(failure);
                Assert.False(workerHadUiAccess);
            }

            // Block dispatcher pumping while the real worker posts; a synchronous Task wait may
            // pump queued UI work and cannot establish that the publication is still pending.
            using (Dispatcher.UIThread.DisableProcessing())
            {
                PublishFromWorker(update);
                Assert.False(vm.HasDesktopPreview);
            }
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.HasDesktopPreview);

            using (Dispatcher.UIThread.DisableProcessing())
            {
                PublishFromWorker(update with { UpdatedAt = update.UpdatedAt.AddMinutes(1) });
                vm.CurrentChat = new Chat();
                Assert.False(vm.HasDesktopPreview);
            }
            Dispatcher.UIThread.RunJobs();

            Assert.True(allNotificationsOnUiThread);
            Assert.False(vm.HasDesktopPreview);
            Assert.Null(vm.DesktopPreviewFrame);
            Assert.False(vm.IsDesktopOpen);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChangingTargetsClearsTheScreenshotUntilAMatchingFrameArrives()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var chat = new Chat();
            using var vm = new ChatViewModel(CreateStore(), TestCopilot.Shared) { CurrentChat = chat };
            var first = CreateUpdate(chat.Id, frame: CreateFrame());
            vm.PublishDesktopPreview(first);
            var second = first with
            {
                WindowKey = "window:second",
                WindowTitle = "Second window",
                UpdatedAt = first.UpdatedAt.AddMinutes(1),
                NewFrame = null
            };
            vm.PublishDesktopPreview(second);
            Assert.Equal("Second window", vm.DesktopPreviewWindowTitle);
            Assert.Null(vm.DesktopPreviewFrame);

            vm.PublishDesktopPreview(first);
            vm.PublishDesktopPreview(second with { ChatId = Guid.NewGuid(), NewFrame = first.NewFrame });
            Assert.Equal("window:second", vm.DesktopPreview!.WindowKey);
            Assert.Null(vm.DesktopPreviewFrame);

            var secondFrame = CreateFrame(second.UpdatedAt);
            vm.PublishDesktopPreview(second with { NewFrame = secondFrame });
            Assert.Same(secondFrame, vm.DesktopPreviewFrame);

            vm.PublishDesktopPreview(second with
            {
                UpdatedAt = second.UpdatedAt.AddMinutes(1),
                NewFrame = first.NewFrame
            });
            Assert.Same(secondFrame, vm.DesktopPreviewFrame);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DismissalKeepsMetadataButDoesNotReopenOrRefreshOnUpdate()
    {
        var session = HeadlessTestSession.Start();
        try
        {
            await session.Dispatch(async () =>
            {
                var chat = new Chat();
                using var vm = new ChatViewModel(CreateStore(), TestCopilot.Shared) { CurrentChat = chat };
                var shown = new List<Guid>();
                var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var captureFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var secondCaptureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var captures = 0;
                vm.DesktopShowRequested += shown.Add;
                vm.DesktopPreviewRefreshAsync = async () =>
                {
                    captures++;
                    Assert.True(vm.IsDesktopOpen);
                    Assert.True(vm.IsRefreshingDesktopPreview);
                    Assert.Contains(chat.Id, shown);
                    captureStarted.TrySetResult();
                    if (captures == 2)
                        secondCaptureStarted.TrySetResult();
                    await captureFinished.Task;
                };
                var update = CreateUpdate(chat.Id);
                vm.PublishDesktopPreview(update);
                vm.RequestShowDesktop();
                await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                vm.CloseDesktopPreview();
                shown.Clear();
                var frame = CreateFrame(update.UpdatedAt.AddMinutes(1));
                vm.PublishDesktopPreview(update with { NewFrame = frame, UpdatedAt = frame.CapturedAt });
                captureFinished.SetResult();
                await Task.Yield();

                Assert.False(vm.IsDesktopOpen);
                Assert.False(vm.IsRefreshingDesktopPreview);
                Assert.True(vm.HasDesktopPreview);
                Assert.Same(frame, vm.DesktopPreviewFrame);
                Assert.Empty(shown);
                Assert.Equal(1, captures);

                vm.RequestShowDesktop();
                await secondCaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(vm.IsDesktopOpen);
                Assert.Equal(2, captures);
                vm.CloseDesktopPreview();
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Run(session.Dispose);
        }
    }

    [Fact]
    public async Task RefreshReportsFailureButStaleCompletionCannotAffectAnotherChat()
    {
        var session = HeadlessTestSession.Start();
        try
        {
            await session.Dispatch(async () =>
            {
                using var vm = new ChatViewModel(CreateStore(), TestCopilot.Shared) { CurrentChat = new Chat() };
                var frame = CreateFrame();
                vm.PublishDesktopPreview(CreateUpdate(vm.CurrentChat.Id, frame: frame));
                vm.IsDesktopOpen = true;
                vm.DesktopPreviewRefreshAsync = () => Task.FromException(new InvalidOperationException("Window closed"));
                await vm.RefreshDesktopPreviewCommand.ExecuteAsync(null);
                Assert.Equal("Window closed", vm.DesktopPreviewError);
                Assert.Same(frame, vm.DesktopPreviewFrame);
                Assert.False(vm.IsRefreshingDesktopPreview);

                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                vm.DesktopPreviewRefreshAsync = () =>
                {
                    started.SetResult();
                    return pending.Task;
                };
                var refresh = vm.RefreshDesktopPreviewCommand.ExecuteAsync(null);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                vm.CurrentChat = new Chat();
                pending.SetException(new InvalidOperationException("Old target failed"));
                await refresh;

                Assert.False(vm.IsDesktopOpen);
                Assert.False(vm.IsRefreshingDesktopPreview);
                Assert.Null(vm.DesktopPreviewError);
                Assert.Null(vm.DesktopPreview);
                Assert.Null(vm.DesktopPreviewRefreshAsync);
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Run(session.Dispose);
        }
    }

    [Fact]
    public async Task ClearAndDisposalReleasePreviewStateAndIgnoreLaterPublications()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var chat = new Chat();
            using var vm = new ChatViewModel(CreateStore(), TestCopilot.Shared) { CurrentChat = chat };
            var update = CreateUpdate(chat.Id, frame: CreateFrame());
            vm.PublishDesktopPreview(update);
            vm.IsDesktopOpen = true;
            vm.DesktopPreviewRefreshAsync = () => Task.CompletedTask;
            vm.ClearChat();
            Assert.False(vm.IsDesktopOpen);
            Assert.False(vm.HasDesktopPreview);
            Assert.Null(vm.DesktopPreviewFrame);
            Assert.Null(vm.DesktopPreviewRefreshAsync);

            vm.CurrentChat = chat;
            vm.PublishDesktopPreview(update);
            vm.DesktopPreviewRefreshAsync = () => Task.CompletedTask;
            vm.Dispose();
            vm.PublishDesktopPreview(update);
            Assert.False(vm.HasDesktopPreview);
            Assert.Null(vm.DesktopPreviewFrame);
            Assert.Null(vm.DesktopPreviewRefreshAsync);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CachedHostChoicesAreIndependentAndExplicitCloseClearsThem()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var vm = new ChatViewModel(CreateStore(), TestCopilot.Shared)
            {
                CurrentChat = new Chat(),
                IsDesktopOpen = true,
                DesktopPreviewRefreshAsync = () => Task.CompletedTask
            };
            using var first = new DesktopPreviewHostState(vm);
            using var second = new DesktopPreviewHostState(vm);
            first.IsOpen = false;
            Assert.True(second.IsOpen);

            first.IsOpen = true;
            vm.CloseDesktopPreview();
            Assert.False(first.IsOpen);
            Assert.False(second.IsOpen);

            first.Dispose();
            vm.RequestShowDesktop();
            Assert.False(first.IsOpen);
            Assert.True(second.IsOpen);
            vm.CloseDesktopPreview();
        }, CancellationToken.None);
    }

    internal static DataStore CreateStore(params Chat[] chats) => new(new AppData
    {
        Settings = new UserSettings
        {
            IsOnboarded = true,
            AutoSaveChats = false,
            EnableMemoryAutoSave = false,
            ShowAmbientPresence = false,
            ShowAnimations = false
        },
        Chats = [.. chats]
    });

    internal static DesktopPreviewUpdate CreateUpdate(Guid chatId, DesktopPreviewFrame? frame = null)
        => new(chatId, "window:first", "First window", "example", "Inspect window", "Ready",
            new DateTimeOffset(2026, 9, 27, 9, 0, 0, TimeSpan.Zero), frame);

    internal static DesktopPreviewFrame CreateFrame(DateTimeOffset? capturedAt = null)
        => new(Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGPQrf31HwAFIwKkBumdFgAAAABJRU5ErkJggg=="),
            1, 1, capturedAt ?? new DateTimeOffset(2026, 9, 27, 8, 59, 0, TimeSpan.Zero));
}
