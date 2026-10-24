using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Storage;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ChatViewScrollBehaviorTests
{
    [Fact]
    public async Task CurrentChatMetadataRefresh_DoesNotReenterFollowMode()
    {
        using var session = HeadlessTestSession.Start();

        await DispatchAsync(session, async () =>
        {
            var chat = CreateLongChat();
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            };
            data.Chats.Add(chat);

            var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            var view = new ChatView { DataContext = viewModel };
            var window = new Window
            {
                Width = 1100,
                Height = 820,
                Content = view,
            };

            window.Show();
            try
            {
                await PumpAsync();
                await PumpAsync();

                await viewModel.LoadChatAsync(chat);
                await WaitUntilAsync(() => view.FindControl<StrataChatShell>("ChatShell")?.TranscriptScrollViewer is not null);

                var shell = Assert.IsType<StrataChatShell>(view.FindControl<StrataChatShell>("ChatShell"));
                var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);
                var transcript = Assert.IsType<TranscriptItemsControl>(view.FindControl<TranscriptItemsControl>("Transcript"));

                // The chat-open reveal moves the transcript independently of metadata refresh.
                await WaitUntilAsync(() =>
                {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    return !viewModel.IsChatSurfaceLoading
                        && transcript.Opacity == 1
                        && (transcript.RenderTransform?.Value ?? Matrix.Identity).IsIdentity;
                }, 10_000);

                shell.JumpToLatest();
                await PumpAsync();

                Assert.True(scrollViewer.Extent.Height > scrollViewer.Viewport.Height);

                var bottomOffset = scrollViewer.Offset.Y;
                scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Max(0, bottomOffset - 220));
                await PumpAsync();
                // The turns the scroll brought into view are realized first, so only the refresh could move them.
                await Task.Delay(TranscriptItemsControl.ScrollIdleRealizationDelay + TimeSpan.FromMilliseconds(30));
                await SettleAsync(viewModel);

                Assert.False(shell.IsFollowingTail);
                var readerAnchor = CaptureVisibleTurnAnchor(view, scrollViewer);
                Assert.NotNull(readerAnchor);

                viewModel.SetProjectId(Guid.NewGuid());
                await PumpAsync();
                await PumpAsync();

                Assert.False(shell.IsFollowingTail);
                var refreshedY = GetTurnViewportY(view, scrollViewer, readerAnchor!.Value.StableId);
                Assert.NotNull(refreshedY);
                Assert.InRange(Math.Abs(refreshedY.Value - readerAnchor.Value.ViewportY), 0, 2.0);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task WorktreeToggleHighlight_ResyncsWhenChatSurfaceChanges()
    {
        using var session = HeadlessTestSession.Start();

        await DispatchAsync(session, async () =>
        {
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            };

            using var worktreeViewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared)
            {
                IsCodingProject = true
            };
            using var localViewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared)
            {
                IsCodingProject = true
            };

            var view = new ChatView { DataContext = worktreeViewModel };
            var window = new Window
            {
                Width = 1100,
                Height = 820,
                Content = view,
            };

            window.Show();
            try
            {
                await PumpAsync();
                await PumpAsync();

                var highlight = Assert.IsType<Border>(view.FindControl<Border>("WorktreeToggleHighlight"));
                var localButton = Assert.IsType<Button>(view.FindControl<Button>("LocalToggleBtn"));
                var worktreeButton = Assert.IsType<Button>(view.FindControl<Button>("WorktreeToggleBtn"));

                await WaitUntilAsync(() => localButton.Bounds.Width > 0 && worktreeButton.Bounds.Width > 0);

                worktreeViewModel.IsWorktreeMode = true;
                await WaitUntilAsync(() => IsAlignedWith(highlight, worktreeButton));

                view.DataContext = localViewModel;
                await WaitUntilAsync(() => IsAlignedWith(highlight, localButton));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CompletedAssistantTailWhileScrolledUp_RemountsWhenBusyEndsWithoutRepinning()
    {
        using var session = HeadlessTestSession.Start();

        await DispatchAsync(session, async () =>
        {
            var chat = CreateLongChat(pairCount: 28);
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            };
            data.Chats.Add(chat);

            var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            var view = new ChatView { DataContext = viewModel };
            var window = new Window
            {
                Width = 1100,
                Height = 820,
                Content = view,
            };

            window.Show();
            try
            {
                await PumpAsync();
                await PumpAsync();

                await viewModel.LoadChatAsync(chat);
                await WaitUntilAsync(() => view.FindControl<StrataChatShell>("ChatShell")?.TranscriptScrollViewer is not null);

                var shell = Assert.IsType<StrataChatShell>(view.FindControl<StrataChatShell>("ChatShell"));
                var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);
                var transcript = Assert.IsType<TranscriptItemsControl>(view.FindControl<TranscriptItemsControl>("Transcript"));

                // Measure scroll stability only after the chat-open reveal has finished moving the transcript.
                await WaitUntilAsync(() =>
                {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    return !viewModel.IsChatSurfaceLoading
                        && transcript.Opacity == 1
                        && (transcript.RenderTransform?.Value ?? Matrix.Identity).IsIdentity;
                });

                shell.JumpToLatest();
                await PumpAsync();

                Assert.True(scrollViewer.Extent.Height > scrollViewer.Viewport.Height);

                var bottomOffset = scrollViewer.Offset.Y;
                scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Max(0, bottomOffset - 220));
                await WaitUntilAsync(() => !shell.IsFollowingTail && !shell.IsPinnedToBottom);
                var readerAnchor = CaptureVisibleTurnAnchor(view, scrollViewer);
                Assert.NotNull(readerAnchor);

                viewModel.StatusText = "Generating...";
                viewModel.IsBusy = true;
                await PumpAsync();

                var assistantTurn = CreateCompletedAssistantTailTurn();
                var typingTurn = viewModel.TranscriptTurns.FirstOrDefault(static turn => turn.StableId == "turn:typing");
                var typingIndex = typingTurn is null ? -1 : viewModel.TranscriptTurns.IndexOf(typingTurn);

                if (typingIndex >= 0)
                    viewModel.TranscriptTurns.Insert(typingIndex, assistantTurn);
                else
                    viewModel.TranscriptTurns.Add(assistantTurn);

                await PumpAsync();

                viewModel.IsBusy = false;

                await WaitUntilAsync(() =>
                    viewModel.MountedTranscriptTurns.Any(turn => turn.StableId == assistantTurn.StableId));

                Assert.False(shell.IsPinnedToBottom);
                var completedY = GetTurnViewportY(view, scrollViewer, readerAnchor!.Value.StableId);
                Assert.NotNull(completedY);
                Assert.InRange(Math.Abs(completedY.Value - readerAnchor.Value.ViewportY), 0, 2.0);
                Assert.Equal(assistantTurn.StableId, viewModel.MountedTranscriptTurns[^1].StableId);
            }
            finally
            {
                viewModel.IsBusy = false;
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AssistantCompletionDuringScrollbarDrag_DoesNotMoveReaderViewport()
    {
        using var session = HeadlessTestSession.Start();

        await DispatchAsync(session, async () =>
        {
            var chat = CreateLongChat(pairCount: 36);
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            };
            data.Chats.Add(chat);

            var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            var view = new ChatView { DataContext = viewModel };
            var window = new Window
            {
                Width = 1100,
                Height = 820,
                Content = view,
            };

            window.Show();
            try
            {
                await PumpAsync();
                await PumpAsync();

                await viewModel.LoadChatAsync(chat);
                await WaitUntilAsync(() => view.FindControl<StrataChatShell>("ChatShell")?.TranscriptScrollViewer is not null);

                var shell = Assert.IsType<StrataChatShell>(view.FindControl<StrataChatShell>("ChatShell"));
                var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);

                shell.JumpToLatest();
                await PumpAsync();
                await PumpAsync();

                viewModel.StatusText = "Generating...";
                viewModel.IsBusy = true;
                await PumpAsync();

                var verticalScrollBar = scrollViewer.GetVisualDescendants()
                    .OfType<ScrollBar>()
                    .FirstOrDefault(static scrollBar => scrollBar.Orientation == Orientation.Vertical);
                Assert.NotNull(verticalScrollBar);

                var thumb = verticalScrollBar.GetVisualDescendants().OfType<Thumb>().FirstOrDefault();
                Assert.NotNull(thumb);

                var thumbCenter = GetCenterPoint(window, thumb);
                window.MouseDown(thumbCenter, MouseButton.Left, RawInputModifiers.None);
                await PumpAsync();

                var readerOffset = Math.Max(0, scrollViewer.Offset.Y - 320);
                scrollViewer.Offset = scrollViewer.Offset.WithY(readerOffset);
                await WaitUntilAsync(() => !shell.IsFollowingTail);
                readerOffset = scrollViewer.Offset.Y;

                var assistantTurn = CreateCompletedAssistantTailTurn("turn:test-drag-completed-assistant");
                var typingTurn = viewModel.TranscriptTurns.FirstOrDefault(static turn => turn.StableId == "turn:typing");
                var typingIndex = typingTurn is null ? -1 : viewModel.TranscriptTurns.IndexOf(typingTurn);
                if (typingIndex >= 0)
                    viewModel.TranscriptTurns.Insert(typingIndex, assistantTurn);
                else
                    viewModel.TranscriptTurns.Add(assistantTurn);

                viewModel.IsBusy = false;

                await WaitUntilAsync(() =>
                    viewModel.MountedTranscriptTurns.Any(turn => turn.StableId == assistantTurn.StableId));

                Assert.False(shell.IsFollowingTail);
                Assert.False(shell.IsPinnedToBottom);
                Assert.InRange(Math.Abs(scrollViewer.Offset.Y - readerOffset), 0, 2.0);

                window.MouseUp(thumbCenter, MouseButton.Left, RawInputModifiers.None);
            }
            finally
            {
                viewModel.IsBusy = false;
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task StreamingTailWhileFollowing_RemountsDuringTransientUnpin()
    {
        using var session = HeadlessTestSession.Start();

        await DispatchAsync(session, async () =>
        {
            var chat = CreateLongChat(pairCount: 28);
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            };
            data.Chats.Add(chat);

            var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            var view = new ChatView { DataContext = viewModel };
            var window = new Window
            {
                Width = 1100,
                Height = 820,
                Content = view,
            };

            window.Show();
            try
            {
                await PumpAsync();
                await PumpAsync();

                await viewModel.LoadChatAsync(chat);
                await WaitUntilAsync(() => view.FindControl<StrataChatShell>("ChatShell")?.TranscriptScrollViewer is not null);

                var shell = Assert.IsType<StrataChatShell>(view.FindControl<StrataChatShell>("ChatShell"));
                shell.JumpToLatest();
                await PumpAsync();

                Assert.True(shell.IsFollowingTail);

                // Layout growth can temporarily move the viewport outside the bottom tolerance even
                // though the shell still intends to follow. Reproduce that brief state deterministically.
                viewModel.UpdateTranscriptScrollState(
                    isFollowingTail: true,
                    isPinnedToBottom: false,
                    distanceFromBottom: 240);
                var beforeAppend = viewModel.CaptureTranscriptDiagnostics();
                Assert.False(beforeAppend.IsPinnedToBottom);

                TranscriptTurn? assistantTurn = null;
                TranscriptWindowDiagnosticsSnapshot afterAppend = default;
                for (var i = 0; i < 4 && afterAppend.TotalPageCount <= beforeAppend.TotalPageCount; i++)
                {
                    assistantTurn = CreateCompletedAssistantTailTurn($"turn:test-streaming-tail:{i}");
                    viewModel.TranscriptTurns.Add(assistantTurn);
                    afterAppend = viewModel.CaptureTranscriptDiagnostics();
                }

                await PumpAsync();

                Assert.NotNull(assistantTurn);
                Assert.True(afterAppend.TotalPageCount > beforeAppend.TotalPageCount);
                Assert.True(shell.IsFollowingTail);
                Assert.Contains(
                    viewModel.MountedTranscriptTurns,
                    turn => turn.StableId == assistantTurn.StableId);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ScrollbarThumbDrag_DoesNotMutateStableTranscriptMembership()
    {
        using var session = HeadlessTestSession.Start();

        await DispatchAsync(session, async () =>
        {
            var chat = CreateLongChat(pairCount: 36);
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            };
            data.Chats.Add(chat);

            var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            var view = new ChatView { DataContext = viewModel };
            var window = new Window
            {
                Width = 1100,
                Height = 820,
                Content = view,
            };

            window.Show();
            try
            {
                await PumpAsync();
                await PumpAsync();

                await viewModel.LoadChatAsync(chat);
                await WaitUntilAsync(() => view.FindControl<StrataChatShell>("ChatShell")?.TranscriptScrollViewer is not null);

                var shell = Assert.IsType<StrataChatShell>(view.FindControl<StrataChatShell>("ChatShell"));
                var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);

                shell.JumpToLatest();
                await PumpAsync();
                await PumpAsync();

                Assert.True(scrollViewer.Extent.Height > scrollViewer.Viewport.Height);
                Assert.Equal(viewModel.TranscriptTurns.Count, viewModel.MountedTranscriptTurns.Count);

                var mountedBefore = viewModel.MountedTranscriptTurns
                    .Select(static turn => turn.StableId)
                    .ToArray();

                var verticalScrollBar = scrollViewer.GetVisualDescendants()
                    .OfType<ScrollBar>()
                    .FirstOrDefault(static scrollBar => scrollBar.Orientation == Orientation.Vertical);
                Assert.NotNull(verticalScrollBar);

                var thumb = verticalScrollBar.GetVisualDescendants().OfType<Thumb>().FirstOrDefault();
                Assert.NotNull(thumb);

                var thumbCenter = GetCenterPoint(window, thumb);
                window.MouseDown(thumbCenter, MouseButton.Left, RawInputModifiers.None);
                await PumpAsync();

                scrollViewer.Offset = scrollViewer.Offset.WithY(0);
                await PumpAsync();
                await PumpAsync();

                Assert.Equal(
                    mountedBefore,
                    viewModel.MountedTranscriptTurns.Select(static turn => turn.StableId).ToArray());

                window.MouseUp(thumbCenter, MouseButton.Left, RawInputModifiers.None);
                await PumpAsync();
                Assert.Equal(
                    mountedBefore,
                    viewModel.MountedTranscriptTurns.Select(static turn => turn.StableId).ToArray());

                shell.JumpToLatest();
                await PumpAsync();
                await PumpAsync();

                var mountedBeforeCaptureLost = viewModel.MountedTranscriptTurns
                    .Select(static turn => turn.StableId)
                    .ToArray();
                thumb = verticalScrollBar.GetVisualDescendants().OfType<Thumb>().FirstOrDefault();
                Assert.NotNull(thumb);

                thumbCenter = GetCenterPoint(window, thumb);
                window.MouseDown(thumbCenter, MouseButton.Left, RawInputModifiers.None);
                await PumpAsync();

                scrollViewer.Offset = scrollViewer.Offset.WithY(0);
                await PumpAsync();
                await PumpAsync();

                Assert.Equal(
                    mountedBeforeCaptureLost,
                    viewModel.MountedTranscriptTurns.Select(static turn => turn.StableId).ToArray());

                thumb.RaiseEvent(new PointerCaptureLostEventArgs(thumb, null!));
                await PumpAsync();
                Assert.Equal(
                    mountedBeforeCaptureLost,
                    viewModel.MountedTranscriptTurns.Select(static turn => turn.StableId).ToArray());
                window.MouseUp(thumbCenter, MouseButton.Left, RawInputModifiers.None);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ScrollingTopToBottom_KeepsActualTailMountedAndRestoresFollowMode()
    {
        using var session = HeadlessTestSession.Start();

        await DispatchAsync(session, async () =>
        {
            var chat = CreateLongChat(pairCount: 80);
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            };
            data.Chats.Add(chat);

            var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            var view = new ChatView { DataContext = viewModel };
            var window = new Window
            {
                Width = 1100,
                Height = 820,
                Content = view,
            };

            window.Show();
            try
            {
                await PumpAsync();
                await PumpAsync();

                await viewModel.LoadChatAsync(chat);
                await WaitUntilAsync(() => view.FindControl<StrataChatShell>("ChatShell")?.TranscriptScrollViewer is not null);

                var shell = Assert.IsType<StrataChatShell>(view.FindControl<StrataChatShell>("ChatShell"));
                var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);

                shell.JumpToLatest();
                await PumpAsync();
                await PumpAsync();

                var actualTail = viewModel.TranscriptTurns[^1];
                Assert.Same(actualTail, viewModel.MountedTranscriptTurns[^1]);

                shell.PreserveViewport();
                scrollViewer.Offset = scrollViewer.Offset.WithY(0);
                await PumpAsync();
                await PumpAsync();

                Assert.False(shell.IsFollowingTail);
                Assert.Contains(actualTail, viewModel.MountedTranscriptTurns);
                Assert.Same(actualTail, viewModel.MountedTranscriptTurns[^1]);

                var wheelPoint = GetCenterPoint(window, scrollViewer);
                for (var attempt = 0; attempt < 20 && (!shell.IsFollowingTail || !shell.IsPinnedToBottom); attempt++)
                {
                    scrollViewer.Offset = scrollViewer.Offset.WithY(
                        Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height));
                    window.MouseWheel(wheelPoint, new Vector(0, -1), RawInputModifiers.None);
                    await PumpAsync();
                }

                Assert.True(
                    shell.IsFollowingTail,
                    $"Tail return did not restore follow mode. offset={scrollViewer.Offset.Y:F1}, "
                    + $"max={Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height):F1}, "
                    + $"distance={shell.CurrentDistanceFromBottom:F1}, "
                    + $"hasUnmountedTail={viewModel.HasUnmountedTranscriptTail}, "
                    + $"topSpacer={viewModel.TranscriptTopSpacerHeight:F1}, "
                    + $"bottomSpacer={viewModel.TranscriptBottomSpacerHeight:F1}");
                Assert.True(shell.IsPinnedToBottom);

                await WaitUntilAsync(() =>
                {
                    var itemsHost = view.FindControl<TranscriptItemsControl>("Transcript")?.ItemsPanelRoot;
                    return itemsHost?.GetVisualDescendants()
                        .OfType<TranscriptTurnControl>()
                        .Any(control => ReferenceEquals(control.Turn, actualTail) && control.Bounds.Height > 0) == true;
                });
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ContinuousScrollWhileTurnsRealize_PreservesVisibleAnchor()
    {
        using var session = HeadlessTestSession.Start();

        await DispatchAsync(session, async () =>
        {
            var chat = CreateLongChat(pairCount: 80);
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            };
            data.Chats.Add(chat);

            var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            var view = new ChatView { DataContext = viewModel };
            var window = new Window
            {
                Width = 1100,
                Height = 820,
                Content = view,
            };

            window.Show();
            try
            {
                await PumpAsync();
                await PumpAsync();

                await viewModel.LoadChatAsync(chat);
                await WaitUntilAsync(() => view.FindControl<StrataChatShell>("ChatShell")?.TranscriptScrollViewer is not null);

                var shell = Assert.IsType<StrataChatShell>(view.FindControl<StrataChatShell>("ChatShell"));
                var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);
                var transcript = Assert.IsType<TranscriptItemsControl>(view.FindControl<TranscriptItemsControl>("Transcript"));

                // Settle only the chat-open reveal, before scrolling queues the realization work under test.
                await WaitUntilAsync(() =>
                {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    return !viewModel.IsChatSurfaceLoading
                        && transcript.Opacity == 1
                        && (transcript.RenderTransform?.Value ?? Matrix.Identity).IsIdentity;
                }, 10_000);

                shell.JumpToLatest();
                await PumpAsync();

                shell.PreserveViewport();
                var middleOffset = Math.Max(0, (scrollViewer.Extent.Height - scrollViewer.Viewport.Height) * 0.55);
                scrollViewer.Offset = scrollViewer.Offset.WithY(middleOffset);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                transcript.RealizeCurrentViewportNow();
                await WaitUntilAsync(() => !TranscriptRealizationScheduler.Instance.HasPendingWork);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);

                // Put the top of what shows mid-screen: above it, turns whose content is not shown yet, reserving
                // estimated heights, as scrolling up brings them into view.
                var topShown = transcript.ItemsPanelRoot!.GetVisualDescendants().OfType<TranscriptTurnControl>().First(static control => control.ShowsContent);
                scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Max(0,
                    scrollViewer.Offset.Y + topShown.TranslatePoint(default, scrollViewer)!.Value.Y - scrollViewer.Viewport.Height / 2));
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);

                var anchor = CaptureShownTurnAnchor(view, scrollViewer);
                Assert.NotNull(anchor);
                Assert.NotNull(scrollViewer.CurrentAnchor);
                Assert.Contains(GetVisibleTurnControls(view, scrollViewer), control =>
                    !control.ShowsContent && control.TranslatePoint(default, scrollViewer)!.Value.Y < anchor!.Value.ViewportY);

                var realizationBefore = TranscriptRealizationScheduler.CaptureDiagnostics();
                transcript.RealizeCurrentViewportNow();
                Assert.True(TranscriptRealizationScheduler.Instance.HasPendingWork);
                await WaitUntilAsync(() => !TranscriptRealizationScheduler.Instance.HasPendingWork);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                var realizationAfter = TranscriptRealizationScheduler.CaptureDiagnostics();

                var settledY = GetTurnViewportY(view, scrollViewer, anchor!.Value.StableId);
                Assert.NotNull(settledY);
                Assert.False(shell.IsFollowingTail);
                Assert.True(realizationAfter.RealizeCount > realizationBefore.RealizeCount);
                // What showed stays where it was while the turns above it realize and take their real heights.
                Assert.InRange(Math.Abs(settledY.Value - anchor.Value.ViewportY), 0, 2.0);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task StableTranscript_MountsEveryTurnAndRealizesOnlyViewportContent()
    {
        using var session = HeadlessTestSession.Start();

        await DispatchAsync(session, async () =>
        {
            var chat = CreateLongChat(pairCount: 80);
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            };
            data.Chats.Add(chat);

            var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            var view = new ChatView { DataContext = viewModel };
            var window = new Window
            {
                Width = 1100,
                Height = 820,
                Content = view,
            };

            window.Show();
            try
            {
                await PumpAsync();
                await viewModel.LoadChatAsync(chat);
                var transcript = Assert.IsType<TranscriptItemsControl>(view.FindControl<TranscriptItemsControl>("Transcript"));
                await WaitUntilAsync(() =>
                    view.FindControl<StrataChatShell>("ChatShell")?.TranscriptScrollViewer is not null
                    && transcript.ItemsPanelRoot?.GetVisualDescendants().OfType<TranscriptTurnControl>().Any() == true);
                transcript.RealizeCurrentViewportNow();
                await PumpUntilAsync(
                    () => viewModel.MountedTranscriptTurns.Any(static turn => turn.RealizedItemsHost is not null)
                        && !TranscriptRealizationScheduler.Instance.HasPendingWork,
                    maxPumps: 20);

                Assert.Equal(viewModel.TranscriptTurns.Count, viewModel.MountedTranscriptTurns.Count);
                Assert.Equal(0, viewModel.TranscriptTopSpacerHeight);
                Assert.Equal(0, viewModel.TranscriptBottomSpacerHeight);
                var realizedAtTail = viewModel.MountedTranscriptTurns.Count(static turn => turn.RealizedItemsHost is not null);
                Assert.InRange(realizedAtTail, 1, viewModel.MountedTranscriptTurns.Count);

                var tail = viewModel.TranscriptTurns[^1];
                var shell = Assert.IsType<StrataChatShell>(view.FindControl<StrataChatShell>("ChatShell"));
                var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);
                shell.PreserveViewport();
                scrollViewer.Offset = scrollViewer.Offset.WithY(
                    Math.Max(0, (scrollViewer.Extent.Height - scrollViewer.Viewport.Height) * 0.5));

                await PumpAsync();
                transcript.RealizeCurrentViewportNow();
                await WaitUntilAsync(() => !TranscriptRealizationScheduler.Instance.HasPendingWork);
                Assert.Contains(tail, viewModel.MountedTranscriptTurns);
                var tailControl = transcript.ItemsPanelRoot?.GetVisualDescendants()
                    .OfType<TranscriptTurnControl>()
                    .FirstOrDefault(control => ReferenceEquals(control.Turn, tail));
                Assert.NotNull(tailControl);
                Assert.False(tailControl.IsViewportActive);
                Assert.Null(tailControl.Content);

                var visibleAnchor = CaptureVisibleTurnAnchor(view, scrollViewer);
                Assert.NotNull(visibleAnchor);
                var visibleTurn = viewModel.TranscriptTurns.Single(turn => turn.StableId == visibleAnchor!.Value.StableId);
                Assert.NotNull(visibleTurn.RealizedItemsHost);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task FarTurnJump_RealizesStablePlaceholderAndBringsItIntoView()
    {
        using var session = HeadlessTestSession.Start();

        await DispatchAsync(session, async () =>
        {
            var chat = CreateLongChat(pairCount: 80);
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            };
            data.Chats.Add(chat);

            var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            var view = new ChatView { DataContext = viewModel };
            var window = new Window
            {
                Width = 1100,
                Height = 820,
                Content = view,
            };

            window.Show();
            try
            {
                await PumpAsync();
                await viewModel.LoadChatAsync(chat);
                await WaitUntilAsync(() => !TranscriptRealizationScheduler.Instance.HasPendingWork);

                var targetTurn = viewModel.TranscriptTurns.First(static turn => turn.Items.Count > 0);
                Assert.Null(targetTurn.RealizedItemsHost);

                var method = typeof(ChatView).GetMethod(
                    "EnsureTurnRealizedAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(method);

                var task = Assert.IsAssignableFrom<Task<TranscriptTurnControl?>>(method.Invoke(view, [targetTurn, true]));
                var control = await task;
                Assert.NotNull(control);
                Assert.Same(targetTurn, control.Turn);
                Assert.NotNull(targetTurn.RealizedItemsHost);

                var shell = Assert.IsType<StrataChatShell>(view.FindControl<StrataChatShell>("ChatShell"));
                var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);
                var point = control.TranslatePoint(default, scrollViewer);
                Assert.NotNull(point);
                Assert.True(point.Value.Y + control.Bounds.Height >= 0);
                Assert.True(point.Value.Y <= scrollViewer.Viewport.Height);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MeasuredHeightChangeDuringPagingMutation_RefreshesCompensationBaseline()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            var view = new ChatView();
            var turn = new TranscriptTurn("turn:height-baseline");
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var mutationField = typeof(ChatView).GetField("_isApplyingTranscriptMutation", flags);
            var observedHeightsField = typeof(ChatView).GetField("_observedTurnHeights", flags);
            var subscribeMethod = typeof(ChatView).GetMethod("SubscribeToTurnHeight", flags);
            Assert.NotNull(mutationField);
            Assert.NotNull(observedHeightsField);
            Assert.NotNull(subscribeMethod);

            subscribeMethod.Invoke(view, [turn]);
            var observedHeights = Assert.IsType<Dictionary<string, double>>(observedHeightsField.GetValue(view));
            Assert.Equal(0, observedHeights[turn.StableId]);

            mutationField.SetValue(view, true);
            try
            {
                turn.MeasuredHeight = 137;
                Assert.Equal(137, observedHeights[turn.StableId]);
            }
            finally
            {
                mutationField.SetValue(view, false);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task WheelAtUnscrollableBoundary_RecordsPagingDirection()
    {
        using var session = HeadlessTestSession.Start();

        await DispatchAsync(session, async () =>
        {
            var chat = CreateLongChat(pairCount: 1);
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            };
            data.Chats.Add(chat);

            var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            var view = new ChatView { DataContext = viewModel };
            var window = new Window
            {
                Width = 1100,
                Height = 820,
                Content = view,
            };

            window.Show();
            try
            {
                await PumpAsync();
                await viewModel.LoadChatAsync(chat);
                await WaitUntilAsync(() => !viewModel.IsChatSurfaceLoading);

                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var scrollViewerField = typeof(ChatView).GetField("_transcriptScrollViewer", flags);
                var dragField = typeof(ChatView).GetField("_isTranscriptScrollbarDragging", flags);
                var directionField = typeof(ChatView).GetField("_pendingTranscriptPagingDirection", flags);
                Assert.NotNull(scrollViewerField);
                Assert.NotNull(dragField);
                Assert.NotNull(directionField);

                await WaitUntilAsync(() => scrollViewerField.GetValue(view) is ScrollViewer);
                var scrollViewer = Assert.IsType<ScrollViewer>(scrollViewerField.GetValue(view));
                Assert.True(scrollViewer.Viewport.Height > 0);
                Assert.True(scrollViewer.Extent.Height <= scrollViewer.Viewport.Height);
                Assert.Equal(0d, scrollViewer.Offset.Y);

                var message = scrollViewer.GetVisualDescendants().OfType<StrataChatMessage>().First();
                var wheelPoint = GetCenterPoint(window, message);

                dragField.SetValue(view, true);
                try
                {
                    window.MouseWheel(wheelPoint, new Vector(0, -1), RawInputModifiers.None);
                    Assert.Equal(
                        TranscriptPagingDirection.TowardNewer,
                        Assert.IsType<TranscriptPagingDirection>(directionField.GetValue(view)));
                    Assert.Equal(0d, scrollViewer.Offset.Y);

                    window.MouseWheel(wheelPoint, new Vector(0, 1), RawInputModifiers.None);
                    Assert.Equal(
                        TranscriptPagingDirection.TowardOlder,
                        Assert.IsType<TranscriptPagingDirection>(directionField.GetValue(view)));
                    Assert.Equal(0d, scrollViewer.Offset.Y);
                }
                finally
                {
                    dragField.SetValue(view, false);
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task NestedScrollOwners_DoNotDriveTranscriptPaging()
    {
        using var session = HeadlessTestSession.Start();

        await DispatchAsync(session, async () =>
        {
            var nestedSource = new Border { Height = 40 };
            var innerContent = new StackPanel
            {
                Children =
                {
                    nestedSource,
                    new Border { Height = 240 },
                }
            };
            var innerScrollViewer = new ScrollViewer
            {
                Height = 100,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                Content = innerContent,
            };
            var middleScrollViewer = new ScrollViewer
            {
                Height = 150,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                Content = new StackPanel
                {
                    Children =
                    {
                        innerScrollViewer,
                        new Border { Height = 320 },
                    }
                },
            };
            var transcriptScrollViewer = new ScrollViewer
            {
                Width = 320,
                Height = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                Content = middleScrollViewer,
            };
            var window = new Window
            {
                Width = 400,
                Height = 320,
                Content = transcriptScrollViewer,
            };

            window.Show();
            try
            {
                await PumpAsync();
                await PumpAsync();

                innerScrollViewer.Offset = innerScrollViewer.Offset.WithY(
                    Math.Max(0, innerScrollViewer.Extent.Height - innerScrollViewer.Viewport.Height));
                middleScrollViewer.Offset = middleScrollViewer.Offset.WithY(0);
                await PumpAsync();

                var view = new ChatView();
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var scrollViewerField = typeof(ChatView).GetField("_transcriptScrollViewer", flags);
                var nestedConsumptionMethod = typeof(ChatView).GetMethod("CanNestedScrollViewerConsume", flags);
                var transcriptScrollbarMethod = typeof(ChatView).GetMethod("IsTranscriptScrollbarInteraction", flags);
                Assert.NotNull(scrollViewerField);
                Assert.NotNull(nestedConsumptionMethod);
                Assert.NotNull(transcriptScrollbarMethod);

                scrollViewerField.SetValue(view, transcriptScrollViewer);

                Assert.True(Assert.IsType<bool>(nestedConsumptionMethod.Invoke(
                    view,
                    [nestedSource, TranscriptPagingDirection.TowardNewer])));

                var nestedScrollBar = innerScrollViewer.GetVisualDescendants()
                    .OfType<ScrollBar>()
                    .Single(scrollBar => scrollBar.Orientation == Orientation.Vertical);
                var transcriptScrollBar = transcriptScrollViewer.GetVisualDescendants()
                    .OfType<ScrollBar>()
                    .Single(scrollBar =>
                        scrollBar.Orientation == Orientation.Vertical
                        && ReferenceEquals(scrollBar.FindAncestorOfType<ScrollViewer>(), transcriptScrollViewer));

                Assert.False(Assert.IsType<bool>(transcriptScrollbarMethod.Invoke(view, [nestedScrollBar])));
                Assert.True(Assert.IsType<bool>(transcriptScrollbarMethod.Invoke(view, [transcriptScrollBar])));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChatSurface_UpAndDownKeysScrollTranscript()
    {
        using var session = HeadlessTestSession.Start();

        await DispatchAsync(session, async () =>
        {
            var chat = CreateLongChat(pairCount: 36);
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            };
            data.Chats.Add(chat);

            using var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            var view = new ChatView { DataContext = viewModel };
            var window = new Window
            {
                Width = 1100,
                Height = 820,
                Content = view,
            };

            window.Show();
            try
            {
                await PumpAsync();
                await viewModel.LoadChatAsync(chat);
                await WaitUntilAsync(() =>
                    view.FindControl<StrataChatShell>("ChatShell")?.TranscriptScrollViewer is not null
                    && !TranscriptRealizationScheduler.Instance.HasPendingWork);

                var shell = Assert.IsType<StrataChatShell>(view.FindControl<StrataChatShell>("ChatShell"));
                var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);
                var composer = Assert.IsType<StrataChatComposer>(view.FindControl<StrataChatComposer>("Composer"));
                var input = composer.GetVisualDescendants()
                    .OfType<TextBox>()
                    .Single(control => control.Name == "PART_Input");

                shell.JumpToLatest();
                await WaitUntilAsync(() =>
                    scrollViewer.Extent.Height - scrollViewer.Viewport.Height > 96);
                var maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
                shell.PreserveViewport();
                scrollViewer.Offset = scrollViewer.Offset.WithY(maxOffset / 2);
                input.Text = string.Empty;
                input.Focus();
                await PumpAsync();

                var beforeUp = scrollViewer.Offset.Y;
                var up = new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Up,
                    KeyModifiers = KeyModifiers.None,
                };
                input.RaiseEvent(up);
                await PumpAsync();

                Assert.True(up.Handled);
                Assert.True(scrollViewer.Offset.Y < beforeUp);

                var beforeDown = scrollViewer.Offset.Y;
                var down = new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Down,
                    KeyModifiers = KeyModifiers.None,
                };
                input.RaiseEvent(down);
                await PumpAsync();

                Assert.True(down.Handled);
                Assert.True(scrollViewer.Offset.Y > beforeDown);

                var transcriptMessage = view.GetVisualDescendants()
                    .OfType<StrataChatMessage>()
                    .First();
                transcriptMessage.Focus();
                await PumpAsync();

                var beforeTranscriptUp = scrollViewer.Offset.Y;
                var transcriptUp = new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Up,
                    KeyModifiers = KeyModifiers.None,
                };
                transcriptMessage.RaiseEvent(transcriptUp);
                await PumpAsync();

                Assert.True(transcriptUp.Handled);
                Assert.True(scrollViewer.Offset.Y < beforeTranscriptUp);

                input.Text = "draft";
                input.CaretIndex = input.Text.Length;
                input.Focus();
                var beforeDraftUp = scrollViewer.Offset.Y;
                input.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Up,
                    KeyModifiers = KeyModifiers.None,
                });
                await PumpAsync();

                Assert.InRange(Math.Abs(scrollViewer.Offset.Y - beforeDraftUp), 0, 0.5);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task UserMessageShortcut_OnlyAppliesToUnfocusedWindowTranscriptOrEmptyComposer()
    {
        using var session = HeadlessTestSession.Start();

        await DispatchAsync(session, async () =>
        {
            var chat = CreateLongChat();
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            };
            data.Chats.Add(chat);

            using var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            var view = new ChatView { DataContext = viewModel };
            var sidebarButton = new Button { Content = "Sidebar action" };
            var sidebarList = new ListBox { ItemsSource = new[] { "Chat A", "Chat B", "Chat C" } };
            var sidebar = new StackPanel { Width = 200, Children = { sidebarButton, sidebarList } };
            DockPanel.SetDock(sidebar, Dock.Left);
            var window = new Window
            {
                Width = 1300,
                Height = 820,
                Content = new DockPanel { Children = { sidebar, view } },
            };

            window.Show();
            try
            {
                await PumpAsync();
                await viewModel.LoadChatAsync(chat);
                await WaitUntilAsync(() =>
                    view.FindControl<StrataChatShell>("ChatShell")?.TranscriptScrollViewer is not null
                    && !TranscriptRealizationScheduler.Instance.HasPendingWork);

                var shell = Assert.IsType<StrataChatShell>(view.FindControl<StrataChatShell>("ChatShell"));
                var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);
                var composer = Assert.IsType<StrataChatComposer>(view.FindControl<StrataChatComposer>("Composer"));
                var input = composer.GetVisualDescendants()
                    .OfType<TextBox>()
                    .Single(control => control.Name == "PART_Input");

                shell.JumpToLatest();
                await WaitUntilAsync(() =>
                    scrollViewer.Extent.Height - scrollViewer.Viewport.Height > 96);
                var maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
                shell.PreserveViewport();
                scrollViewer.Offset = scrollViewer.Offset.WithY(maxOffset / 2);
                await PumpAsync();

                var modifiers = OperatingSystem.IsMacOS()
                    ? KeyModifiers.Meta | KeyModifiers.Alt
                    : KeyModifiers.Control;
                KeyEventArgs Shortcut(Key key) => new()
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = key,
                    KeyModifiers = modifiers,
                };

                // An unrelated focused control outside the chat keeps the keys.
                sidebarButton.Focus();
                await PumpAsync();
                var beforeButton = scrollViewer.Offset.Y;
                var fromButton = Shortcut(Key.Down);
                sidebarButton.RaiseEvent(fromButton);
                await PumpAsync();
                await PumpAsync();

                Assert.False(fromButton.Handled);
                Assert.InRange(Math.Abs(scrollViewer.Offset.Y - beforeButton), 0, 0.5);

                sidebarList.SelectedIndex = 0;
                await PumpAsync();
                var sidebarItem = Assert.IsType<ListBoxItem>(sidebarList.ContainerFromIndex(0));
                sidebarItem.Focus();
                await PumpAsync();
                var beforeListItem = scrollViewer.Offset.Y;
                sidebarItem.RaiseEvent(Shortcut(Key.Down));
                await PumpAsync();
                await PumpAsync();

                Assert.InRange(Math.Abs(scrollViewer.Offset.Y - beforeListItem), 0, 0.5);

                // With nothing focused the key is raised on the window itself.
                var beforeWindow = scrollViewer.Offset.Y;
                var fromWindow = Shortcut(Key.Down);
                window.RaiseEvent(fromWindow);
                Assert.True(fromWindow.Handled);
                await WaitUntilAsync(() => Math.Abs(scrollViewer.Offset.Y - beforeWindow) > 0.5);

                var transcriptMessage = view.GetVisualDescendants()
                    .OfType<StrataChatMessage>()
                    .First(message => message.IsEffectivelyVisible);
                transcriptMessage.Focus();
                await PumpAsync();
                var beforeTranscript = scrollViewer.Offset.Y;
                var fromTranscript = Shortcut(Key.Up);
                transcriptMessage.RaiseEvent(fromTranscript);
                Assert.True(fromTranscript.Handled);
                await WaitUntilAsync(() => Math.Abs(scrollViewer.Offset.Y - beforeTranscript) > 0.5);

                input.Text = string.Empty;
                input.Focus();
                await PumpAsync();
                var beforeComposer = scrollViewer.Offset.Y;
                var fromComposer = Shortcut(Key.Up);
                input.RaiseEvent(fromComposer);
                Assert.True(fromComposer.Handled);
                await WaitUntilAsync(() => Math.Abs(scrollViewer.Offset.Y - beforeComposer) > 0.5);

                input.Text = "draft";
                input.CaretIndex = input.Text.Length;
                await PumpAsync();
                var beforeDraft = scrollViewer.Offset.Y;
                var fromDraft = Shortcut(Key.Up);
                input.RaiseEvent(fromDraft);
                await PumpAsync();
                await PumpAsync();

                Assert.False(fromDraft.Handled);
                Assert.InRange(Math.Abs(scrollViewer.Offset.Y - beforeDraft), 0, 0.5);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task UserMessageShortcut_StepsBackThroughOlderExchangesThatAreOnlyInStorage()
    {
        using var session = HeadlessTestSession.Start();
        await WithStoredHistoryAsync(session, async (window, view, viewModel, scrollViewer) =>
        {
            // The chat opens at its newest exchange; the oldest are only in storage.
            Assert.DoesNotContain(
                viewModel.TranscriptTurns.SelectMany(static turn => turn.Items).OfType<UserMessageItem>(),
                static item => item.Content == "Question 0");

            var target = typeof(ChatView).GetField("_userNavTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var inFlight = typeof(ChatView).GetField("_userNavInFlight", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var modifiers = OperatingSystem.IsMacOS() ? KeyModifiers.Meta | KeyModifiers.Alt : KeyModifiers.Control;
            var landed = new List<(string Content, double? Y)>();
            for (var press = 0; press < 20; press++)
            {
                var shortcut = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up, KeyModifiers = modifiers };
                window.RaiseEvent(shortcut);
                Assert.True(shortcut.Handled);
                await WaitUntilAsync(() => !(bool)inFlight.GetValue(view)!, 10_000);
                if (target.GetValue(view) is not TranscriptTurn turn
                    || turn.Items.OfType<UserMessageItem>().FirstOrDefault() is not { } item)
                    break;

                await SettleAsync(viewModel);
                landed.Add((item.Content, GetTurnViewportY(view, scrollViewer, turn.StableId)));
            }

            // Each press lands on the previous question, past the job's wake, back to the first, and the
            // message stays on the landing line while the exchanges around it are built.
            Assert.NotEmpty(landed);
            var first = int.Parse(landed[0].Content["Question ".Length..]);
            var report = string.Join(", ", landed.Select(static landing => $"{landing.Content} at {landing.Y:0}"));
            Assert.True(
                Enumerable.Range(0, first + 1).Reverse().Select(static i => $"Question {i}").SequenceEqual(landed.Select(static landing => landing.Content)),
                report);
            Assert.True(landed.All(static landing => landing.Y is >= 56 and <= 72), report);
        });
    }

    [Fact]
    public async Task ScrollingUpThroughStoredHistory_KeepsWhatIsOnScreenInPlace()
    {
        using var session = HeadlessTestSession.Start();
        await WithStoredHistoryAsync(session, async (_, view, viewModel, scrollViewer) =>
        {
            // Where each built turn near the viewport is.
            Dictionary<string, double> Positions() => viewModel.TranscriptTurns
                .Where(static turn => !TranscriptHistory.IsPlaceholder(turn))
                .Select(turn => (turn.StableId, Y: GetTurnViewportY(view, scrollViewer, turn.StableId)))
                .Where(entry => entry.Y > -200 && entry.Y < scrollViewer.Viewport.Height + 200)
                .ToDictionary(static entry => entry.StableId, static entry => entry.Y!.Value);

            // Scroll up in wheel-sized steps: as older exchanges are built and swapped in above or at the
            // top of the viewport, every turn on screen moves exactly as far as the scroll did.
            var drifts = new List<string>();
            for (var step = 0; step < 80 && scrollViewer.Offset.Y > 1; step++)
            {
                var before = Positions();
                var from = scrollViewer.Offset.Y;
                scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Max(0, from - 150));
                var scrolled = from - scrollViewer.Offset.Y;
                await SettleAsync(viewModel);
                foreach (var (id, now) in Positions())
                {
                    if (before.TryGetValue(id, out var was) && Math.Abs(now - (was + scrolled)) > 2)
                        drifts.Add($"step {step}: {id} moved {now - (was + scrolled):0} px");
                }
            }

            Assert.True(scrollViewer.Offset.Y <= 1, "The scroll did not reach the top of the chat.");
            Assert.True(drifts.Count == 0, string.Join(Environment.NewLine, drifts));
        });
    }

    [Fact]
    public async Task OpeningWorkInATurnStraddlingTheTop_GrowsDownward()
    {
        using var session = HeadlessTestSession.Start();
        await DispatchAsync(session, async () =>
        {
            // Exchanges whose updates and steps fold into a "worked for" row above the answer.
            var chat = new Chat { Title = "Straddling turn" };
            for (var i = 0; i < 8; i++)
            {
                chat.Messages.Add(new ChatMessage { Role = "user", Content = $"Question {i}" });
                for (var step = 0; step < 3; step++)
                {
                    chat.Messages.Add(new ChatMessage { Role = "assistant", IsProgress = true, Content = $"Update {i}.{step}: " + new string('u', 900) });
                    chat.Messages.Add(new ChatMessage { Role = "tool", ToolName = "view", ToolCallId = $"view-{i}-{step}", ToolStatus = "Completed", Content = "{\"path\":\"README.md\"}" });
                }

                chat.Messages.Add(new ChatMessage { Role = "assistant", Content = $"Answer {i}: " + new string('a', 1600) });
            }

            var data = new AppData { Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false, ShowAnimations = false } };
            data.Chats.Add(chat);
            using var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            var view = new ChatView { DataContext = viewModel };
            var window = new Window { Width = 1100, Height = 820, Content = view };
            window.Show();
            try
            {
                await PumpAsync();
                await viewModel.LoadChatAsync(chat);
                await WaitUntilAsync(() => view.FindControl<StrataChatShell>("ChatShell")?.TranscriptScrollViewer is not null, 10_000);
                var scrollViewer = Assert.IsType<ScrollViewer>(view.FindControl<StrataChatShell>("ChatShell")!.TranscriptScrollViewer);
                await SettleAsync(viewModel);

                // A turn with folded work, shown, its top just above the viewport and its answer on screen.
                var turns = viewModel.TranscriptTurns.ToList();
                var work = turns.Where(static turn => turn.Items.OfType<TurnSummaryItem>().Any()).ElementAt(4);
                var next = turns[turns.IndexOf(work) + 1];
                var summary = work.Items.OfType<TurnSummaryItem>().First();
                Assert.False(summary.IsExpanded);
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    await WaitUntilAsync(() => GetTurnViewportY(view, scrollViewer, work.StableId) is not null);
                    var y = GetTurnViewportY(view, scrollViewer, work.StableId)!.Value;
                    scrollViewer.Offset = scrollViewer.Offset.WithY(scrollViewer.Offset.Y + y + 8);
                    // Turns show their content once scrolling rests.
                    await Task.Delay(TranscriptItemsControl.ScrollIdleRealizationDelay + TimeSpan.FromMilliseconds(30));
                    await SettleAsync(viewModel);
                }

                var top = GetTurnViewportY(view, scrollViewer, work.StableId)!.Value;
                var nextTop = GetTurnViewportY(view, scrollViewer, next.StableId)!.Value;
                Assert.InRange(top, -20, -1);
                Assert.InRange(nextTop, 0, scrollViewer.Viewport.Height);
                Assert.True(view.FindControl<TranscriptItemsControl>("Transcript")!.ItemsPanelRoot!.GetVisualDescendants()
                    .OfType<TranscriptTurnControl>().Single(control => control.Turn?.StableId == work.StableId).ShowsContent);

                // Opening its work grows the turn downward: it stays where it is and pushes the next one down.
                summary.IsExpanded = true;
                double? Grown() => GetTurnViewportY(view, scrollViewer, next.StableId) - nextTop;
                double? last = null;
                for (int frame = 0, still = 0; frame < 200 && still < 5; frame++)
                {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    await PumpAsync();
                    await Task.Delay(10);
                    var now = Grown();
                    still = now > 20 && last is { } previous && Math.Abs(now.Value - previous) < 0.5 ? still + 1 : 0;
                    last = now;
                }

                Assert.True(Grown() > 20, $"Opening the work moved the next turn {Grown():0} px.");
                Assert.InRange(GetTurnViewportY(view, scrollViewer, work.StableId)!.Value - top, -2, 2);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Opens a chat stored in SQLite at its newest exchange, so its older exchanges are only in
    /// storage: twelve questions with long answers, and a job's wake before the seventh.
    /// </summary>
    private static async Task WithStoredHistoryAsync(
        HeadlessTestSession session,
        Func<Window, ChatView, ChatViewModel, ScrollViewer, Task> test)
    {
        var directory = Path.Combine(TestProfile.Root, "stored-history", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "lumi.db");
        try
        {
            await DispatchAsync(session, async () =>
            {
                var settings = new UserSettings { AutoSaveChats = true, EnableMemoryAutoSave = false };
                var chat = new Chat { Title = "Stored history" };
                for (var i = 0; i < 12; i++)
                {
                    // A job's wake starts an exchange of its own, but is not one of the user's messages.
                    if (i == 6)
                    {
                        chat.Messages.Add(new ChatMessage { Role = "user", Author = "Lumi Job - Watcher", Content = "Background job triggered: Watcher" });
                        chat.Messages.Add(new ChatMessage { Role = "assistant", Content = "Nothing new." });
                    }

                    chat.Messages.Add(new ChatMessage { Role = "user", Content = $"Question {i}" });
                    chat.Messages.Add(new ChatMessage { Role = "assistant", Content = $"Answer {i}: " + new string('a', 2400) });
                }

                using (var writer = new ChatStore(databasePath))
                {
                    var dataStore = new DataStore(new AppData { Settings = settings }, writer);
                    dataStore.Data.Chats.Add(chat);
                    await dataStore.SaveChatAsync(chat);
                }

                using var store = new ChatStore(databasePath);
                var reopened = new DataStore(new AppData { Settings = settings }, store);
                using var viewModel = new ChatViewModel(reopened, TestCopilot.Shared);
                var view = new ChatView { DataContext = viewModel };
                var window = new Window { Width = 1100, Height = 820, Content = view };
                window.Show();
                try
                {
                    await PumpAsync();
                    await viewModel.LoadChatAsync(Assert.Single(reopened.Data.Chats));
                    await WaitUntilAsync(() => view.FindControl<StrataChatShell>("ChatShell")?.TranscriptScrollViewer is not null, 10_000);
                    var shell = Assert.IsType<StrataChatShell>(view.FindControl<StrataChatShell>("ChatShell"));
                    var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);
                    shell.JumpToLatest();
                    await WaitUntilAsync(() => scrollViewer.Extent.Height - scrollViewer.Viewport.Height > 96, 10_000);
                    await SettleAsync(viewModel);
                    Assert.True(viewModel.HasTranscriptHistory);

                    await test(window, view, viewModel, scrollViewer);
                }
                finally
                {
                    window.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    // Lets older exchanges be read, built and realized, then layout finish.
    private static async Task SettleAsync(ChatViewModel viewModel)
    {
        for (var i = 0; i < 100 && (viewModel.TranscriptHistory.HasPendingWork || TranscriptRealizationScheduler.Instance.HasPendingWork); i++)
        {
            await PumpAsync();
            await Task.Delay(10);
        }

        await PumpAsync();
    }

    [Fact]
    public async Task ClearingDataContext_DetachesMountedTurnHeightSubscriptions()
    {
        using var session = HeadlessTestSession.Start();

        await DispatchAsync(session, async () =>
        {
            var chat = CreateLongChat();
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            };
            data.Chats.Add(chat);

            var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            var view = new ChatView { DataContext = viewModel };
            var window = new Window
            {
                Width = 1100,
                Height = 820,
                Content = view,
            };

            window.Show();
            try
            {
                await PumpAsync();
                await viewModel.LoadChatAsync(chat);
                await WaitUntilAsync(() => viewModel.MountedTranscriptTurns.Count > 0);

                var mountedTurn = viewModel.MountedTranscriptTurns[0];
                view.DataContext = null;

                mountedTurn.MeasuredHeight += 20;
                await PumpAsync();
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static Chat CreateLongChat(int pairCount = 18)
    {
        var chat = new Chat { Title = "Scroll regression" };
        for (var i = 0; i < pairCount; i++)
        {
            chat.Messages.Add(new ChatMessage
            {
                Role = "user",
                Content = $"Question {i}: " + new string('q', 160)
            });
            chat.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = $"Answer {i}: " + new string('a', 280)
            });
        }

        return chat;
    }

    private static bool IsAlignedWith(Border highlight, Button target)
    {
        return Math.Abs(highlight.Margin.Left - target.Bounds.Left) < 0.5
            && Math.Abs(highlight.Width - target.Bounds.Width) < 0.5;
    }

    private static TranscriptTurn CreateCompletedAssistantTailTurn(
        string stableId = "turn:test-completed-assistant-tail")
    {
        var turn = new TranscriptTurn(stableId);
        for (var i = 0; i < 4; i++)
        {
            var message = new ChatMessage
            {
                Role = "assistant",
                Author = "Lumi",
                Content = $"Completed assistant stream segment {i}: " + new string('a', 1200),
                IsStreaming = false
            };

            turn.Items.Add(new AssistantMessageItem(new ChatMessageViewModel(message), showTimestamps: false));
        }

        return turn;
    }

    private static Point GetCenterPoint(Window window, Control target)
    {
        var topLeft = target.TranslatePoint(new Point(0, 0), window)
            ?? throw new InvalidOperationException("Target is not attached to the test window.");

        return topLeft + new Point(target.Bounds.Width / 2, target.Bounds.Height / 2);
    }

    private static (string StableId, double ViewportY)? CaptureVisibleTurnAnchor(
        ChatView view,
        ScrollViewer scrollViewer)
    {
        var itemsHost = view.FindControl<TranscriptItemsControl>("Transcript")?.ItemsPanelRoot;
        if (itemsHost is null)
            return null;

        foreach (var control in itemsHost.GetVisualDescendants().OfType<TranscriptTurnControl>())
        {
            var point = control.TranslatePoint(default, scrollViewer);
            if (point is null || point.Value.Y + control.Bounds.Height < 0)
                continue;

            return control.Turn is null
                ? null
                : (control.Turn.StableId, point.Value.Y);
        }

        return null;
    }

    // The first visible turn whose content shows: what the reader sees first, which the transcript anchors on.
    private static (string StableId, double ViewportY)? CaptureShownTurnAnchor(ChatView view, ScrollViewer scrollViewer)
    {
        foreach (var control in GetVisibleTurnControls(view, scrollViewer))
        {
            if (control.ShowsContent && control.Turn is { } turn)
                return (turn.StableId, control.TranslatePoint(default, scrollViewer)!.Value.Y);
        }

        return null;
    }

    private static double? GetTurnViewportY(
        ChatView view,
        ScrollViewer scrollViewer,
        string stableId)
    {
        var itemsHost = view.FindControl<TranscriptItemsControl>("Transcript")?.ItemsPanelRoot;
        var control = itemsHost?.GetVisualDescendants()
            .OfType<TranscriptTurnControl>()
            .FirstOrDefault(turnControl => turnControl.Turn?.StableId == stableId);
        return control?.TranslatePoint(default, scrollViewer)?.Y;
    }

    private static IReadOnlyList<TranscriptTurnControl> GetVisibleTurnControls(
        ChatView view,
        ScrollViewer scrollViewer)
    {
        var itemsHost = view.FindControl<TranscriptItemsControl>("Transcript")?.ItemsPanelRoot;
        if (itemsHost is null)
            return Array.Empty<TranscriptTurnControl>();

        return itemsHost.GetVisualDescendants()
            .OfType<TranscriptTurnControl>()
            .Where(control =>
            {
                var point = control.TranslatePoint(default, scrollViewer);
                return point is not null
                    && point.Value.Y + control.Bounds.Height >= 0
                    && point.Value.Y <= scrollViewer.Viewport.Height;
            })
            .ToArray();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await PumpAsync();
            await Task.Delay(20);
        }

        Assert.True(condition(), "Timed out waiting for the chat view to finish loading.");
    }

    private static async Task PumpUntilAsync(Func<bool> condition, int maxPumps)
    {
        for (var attempt = 0; attempt < maxPumps; attempt++)
        {
            if (condition())
                return;

            await PumpAsync();
        }

        Assert.True(condition(), "Timed out pumping the headless UI to the expected state.");
    }

    private static async Task DispatchAsync(
        HeadlessTestSession session,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        Exception? dispatchedException = null;
        await session.Dispatch(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                dispatchedException = ex;
            }
        }, cancellationToken);

        if (dispatchedException is not null)
            ExceptionDispatchInfo.Capture(dispatchedException).Throw();
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
