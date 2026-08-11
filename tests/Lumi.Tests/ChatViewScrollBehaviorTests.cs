using System;
using System.Collections.Generic;
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

                shell.JumpToLatest();
                await PumpAsync();

                Assert.True(scrollViewer.Extent.Height > scrollViewer.Viewport.Height);

                var bottomOffset = scrollViewer.Offset.Y;
                scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Max(0, bottomOffset - 220));
                await PumpAsync();

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
                shell.JumpToLatest();
                await PumpAsync();

                shell.PreserveViewport();
                var middleOffset = Math.Max(0, (scrollViewer.Extent.Height - scrollViewer.Viewport.Height) * 0.55);
                scrollViewer.Offset = scrollViewer.Offset.WithY(middleOffset);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);

                scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Max(0, scrollViewer.Offset.Y - 180));
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);

                var anchor = CaptureVisibleTurnAnchor(view, scrollViewer);
                Assert.NotNull(anchor);
                Assert.NotNull(scrollViewer.CurrentAnchor);

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

                var task = Assert.IsAssignableFrom<Task<TranscriptTurnControl?>>(method.Invoke(view, [targetTurn]));
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
            var view = new ChatView();
            var window = new Window
            {
                Width = 1100,
                Height = 820,
                Content = view,
            };

            window.Show();
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var scrollViewerField = typeof(ChatView).GetField("_transcriptScrollViewer", flags);
                var dragField = typeof(ChatView).GetField("_isTranscriptScrollbarDragging", flags);
                var directionField = typeof(ChatView).GetField("_pendingTranscriptPagingDirection", flags);
                Assert.NotNull(scrollViewerField);
                Assert.NotNull(dragField);
                Assert.NotNull(directionField);

                await WaitUntilAsync(() => scrollViewerField.GetValue(view) is ScrollViewer);
                var scrollViewer = Assert.IsType<ScrollViewer>(scrollViewerField.GetValue(view));
                var wheelPoint = GetCenterPoint(window, scrollViewer);

                dragField.SetValue(view, true);
                try
                {
                    window.MouseWheel(wheelPoint, new Vector(0, -1), RawInputModifiers.None);
                    Assert.Equal(
                        TranscriptPagingDirection.TowardNewer,
                        Assert.IsType<TranscriptPagingDirection>(directionField.GetValue(view)));

                    window.MouseWheel(wheelPoint, new Vector(0, 1), RawInputModifiers.None);
                    Assert.Equal(
                        TranscriptPagingDirection.TowardOlder,
                        Assert.IsType<TranscriptPagingDirection>(directionField.GetValue(view)));
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
