using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    public async Task LoadingTailPreview_RendersAndClearsWithOverlayLifecycle()
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
            var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            var message = new ChatMessage
            {
                Role = "assistant",
                Content = "Real recent content appears while the full transcript is prepared."
            };
            var turn = new TranscriptTurn("turn:loading-preview");
            turn.Items.Add(new AssistantMessageItem(
                new ChatMessageViewModel(message),
                showTimestamps: false));
            viewModel.LoadingTranscriptPreviewTurns =
                new ObservableCollection<TranscriptTurn> { turn };
            viewModel.HasLoadingTranscriptPreview = true;
            viewModel.IsLoadingChat = true;
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
                var preview = Assert.IsType<TranscriptItemsControl>(
                    view.FindControl<TranscriptItemsControl>("LoadingPreviewTranscript"));
                preview.RealizeCurrentViewportNow();
                TranscriptRealizationScheduler.Instance.FlushAll();
                await PumpAsync();

                Assert.True(view.FindControl<Grid>("LoadingOverlay")?.IsVisible);
                Assert.True(view.FindControl<StrataTypingIndicator>("LoadingConversationIndicator")?.IsActive);
                Assert.Contains(
                    preview.ItemsPanelRoot!.GetVisualDescendants().OfType<TranscriptTurnControl>(),
                    control => ReferenceEquals(control.Turn, turn) && control.Content is not null);

                viewModel.IsLoadingChat = false;
                viewModel.TryClearLoadingTranscriptPreview();
                await PumpAsync();

                Assert.False(viewModel.IsChatLoadingOverlayVisible);
                Assert.False(viewModel.HasLoadingTranscriptPreview);
                Assert.Empty(viewModel.LoadingTranscriptPreviewTurns);
                Assert.False(view.FindControl<Grid>("LoadingOverlay")?.IsVisible == true);
                Assert.False(view.FindControl<StrataTypingIndicator>("LoadingConversationIndicator")?.IsActive == true);
            }
            finally
            {
                window.Close();
                viewModel.Dispose();
            }
        }, CancellationToken.None);
    }

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
                await WaitUntilAsync(() => !viewModel.IsChatSurfaceLoading, timeoutMs: 5_000);

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
    public async Task InPlaceTranscriptRebuild_PreservesReaderAnchorAwayFromTail()
    {
        using var session = HeadlessTestSession.Start();

        await DispatchAsync(session, async () =>
        {
            var chat = CreateLongChat(pairCount: 48);
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
                    view.FindControl<StrataChatShell>("ChatShell")?.TranscriptScrollViewer is not null);

                var shell = Assert.IsType<StrataChatShell>(view.FindControl<StrataChatShell>("ChatShell"));
                var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);
                shell.JumpToLatest();
                await PumpAsync();

                scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Max(0, scrollViewer.Offset.Y - 260));
                await WaitUntilAsync(() => !shell.IsFollowingTail);
                var anchor = CaptureVisibleTurnAnchor(view, scrollViewer);
                Assert.NotNull(anchor);
                var firstMountedStableId = viewModel.MountedTranscriptTurns[0].StableId;

                viewModel.RebuildTranscript();
                await WaitUntilAsync(() =>
                    viewModel.MountedTranscriptTurns.Count > 0
                    && viewModel.MountedTranscriptTurns[0].StableId == firstMountedStableId);
                await PumpAsync();

                Assert.False(shell.IsFollowingTail);
                var restoredY = GetTurnViewportY(view, scrollViewer, anchor!.Value.StableId);
                Assert.NotNull(restoredY);
                Assert.InRange(Math.Abs(restoredY.Value - anchor.Value.ViewportY), 0, 2.0);
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
                await WaitUntilAsync(() => !viewModel.IsChatSurfaceLoading, timeoutMs: 5_000);

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
    public async Task LocalHeightCompensation_IsDeferredUntilScrollbarDragEnds()
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
                    view.FindControl<StrataChatShell>("ChatShell")?.TranscriptScrollViewer is not null);
                await PumpAsync();

                var shell = Assert.IsType<StrataChatShell>(view.FindControl<StrataChatShell>("ChatShell"));
                var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);
                shell.JumpToLatest();
                await PumpAsync();
                scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Max(0, scrollViewer.Offset.Y - 320));
                await WaitUntilAsync(() => !shell.IsFollowingTail);

                var aboveControl = view.FindControl<TranscriptItemsControl>("Transcript")?
                    .ItemsPanelRoot?
                    .GetVisualDescendants()
                    .OfType<TranscriptTurnControl>()
                    .FirstOrDefault(control =>
                    {
                        var point = control.TranslatePoint(default, scrollViewer);
                        return point is not null && point.Value.Y + control.Bounds.Height <= 0;
                    });
                Assert.NotNull(aboveControl);

                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var draggingField = typeof(ChatView).GetField("_isTranscriptScrollbarDragging", flags);
                var applyingField = typeof(ChatView).GetField("_isApplyingTranscriptMutation", flags);
                var deferredField = typeof(ChatView).GetField("_deferredScrollbarDragCompensation", flags);
                var scrollField = typeof(ChatView).GetField("_transcriptScrollViewer", flags);
                var viewModelField = typeof(ChatView).GetField("_subscribedVm", flags);
                var applyMethod = typeof(ChatView).GetMethod("ApplyLocalTurnHeightChange", flags);
                var endMethod = typeof(ChatView).GetMethod("EndTranscriptScrollbarDrag", flags);
                Assert.NotNull(draggingField);
                Assert.NotNull(applyingField);
                Assert.NotNull(deferredField);
                Assert.NotNull(scrollField);
                Assert.NotNull(viewModelField);
                Assert.NotNull(applyMethod);
                Assert.NotNull(endMethod);

                applyingField.SetValue(view, false);
                draggingField.SetValue(view, true);
                scrollField.SetValue(view, scrollViewer);
                viewModelField.SetValue(view, viewModel);
                shell.PreserveViewport();
                var abovePoint = aboveControl.TranslatePoint(default, scrollViewer);
                Assert.NotNull(abovePoint);
                Assert.True(abovePoint.Value.Y + aboveControl.Bounds.Height <= 0);
                Assert.False(shell.IsFollowingTail);
                var offsetBefore = scrollViewer.Offset.Y;
                applyMethod.Invoke(view, [aboveControl, aboveControl.Bounds.Height, aboveControl.Bounds.Height + 120]);

                Assert.Equal(offsetBefore, scrollViewer.Offset.Y);
                Assert.InRange(Assert.IsType<double>(deferredField.GetValue(view)), 119.5, 120.5);

                endMethod.Invoke(view, null);
                await PumpAsync();
                Assert.InRange(scrollViewer.Offset.Y - offsetBefore, 119.5, 120.5);
                Assert.Equal(0, Assert.IsType<double>(deferredField.GetValue(view)));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PlaceholderGrowthCrossingViewport_StillCompensatesFromPreviousHeight()
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
            var window = new Window { Width = 1100, Height = 820, Content = view };
            window.Show();
            try
            {
                await PumpAsync();
                await viewModel.LoadChatAsync(chat);
                await WaitUntilAsync(() =>
                    view.FindControl<StrataChatShell>("ChatShell")?.TranscriptScrollViewer is not null);

                var shell = Assert.IsType<StrataChatShell>(view.FindControl<StrataChatShell>("ChatShell"));
                var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);
                shell.JumpToLatest();
                await PumpAsync();
                scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Max(0, scrollViewer.Offset.Y - 420));
                await WaitUntilAsync(() => !shell.IsFollowingTail);

                var control = view.FindControl<TranscriptItemsControl>("Transcript")?
                    .ItemsPanelRoot?
                    .GetVisualDescendants()
                    .OfType<TranscriptTurnControl>()
                    .FirstOrDefault(candidate =>
                    {
                        var point = candidate.TranslatePoint(default, scrollViewer);
                        if (point is null)
                            return false;

                        var previousBottom = point.Value.Y + candidate.Bounds.Height;
                        return previousBottom <= 0 && previousBottom > -300;
                    });
                Assert.NotNull(control);

                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var applyingField = typeof(ChatView).GetField("_isApplyingTranscriptMutation", flags);
                var applyMethod = typeof(ChatView).GetMethod("ApplyLocalTurnHeightChange", flags);
                Assert.NotNull(applyingField);
                Assert.NotNull(applyMethod);

                var previousHeight = control.Bounds.Height;
                applyingField.SetValue(view, true);
                control.Height = previousHeight + 320;
                await PumpAsync();
                applyingField.SetValue(view, false);

                var pointAfterGrowth = control.TranslatePoint(default, scrollViewer);
                Assert.NotNull(pointAfterGrowth);
                Assert.True(pointAfterGrowth.Value.Y + previousHeight <= 0);
                Assert.True(pointAfterGrowth.Value.Y + control.Bounds.Height > 0);

                var offsetBeforeCompensation = scrollViewer.Offset.Y;
                applyMethod.Invoke(view, [control, previousHeight, control.Bounds.Height]);
                await PumpAsync();

                Assert.InRange(
                    scrollViewer.Offset.Y - offsetBeforeCompensation,
                    control.Bounds.Height - previousHeight - 1,
                    control.Bounds.Height - previousHeight + 1);
            }
            finally
            {
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
    public async Task ScrollbarThumbDrag_DefersProgressivePrependUntilRelease()
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
                Assert.True(viewModel.MountedTranscriptTurns.Count < viewModel.TranscriptTurns.Count);
                var actualTail = viewModel.TranscriptTurns[^1];
                Assert.Contains(actualTail, viewModel.MountedTranscriptTurns);

                var mountedBefore = viewModel.MountedTranscriptTurns
                    .Select(static turn => turn.StableId)
                    .ToArray();

                var dragField = typeof(ChatView)
                    .GetField("_isTranscriptScrollbarDragging", BindingFlags.Instance | BindingFlags.NonPublic);
                var endDragMethod = typeof(ChatView)
                    .GetMethod("EndTranscriptScrollbarDrag", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(dragField);
                Assert.NotNull(endDragMethod);
                dragField.SetValue(view, true);

                scrollViewer.Offset = scrollViewer.Offset.WithY(0);
                await PumpAsync();
                await PumpAsync();

                Assert.Equal(
                    mountedBefore,
                    viewModel.MountedTranscriptTurns.Select(static turn => turn.StableId).ToArray());

                endDragMethod.Invoke(view, null);
                await WaitUntilAsync(() => viewModel.MountedTranscriptTurns.Count > mountedBefore.Length);
                Assert.Contains(actualTail, viewModel.MountedTranscriptTurns);

                shell.JumpToLatest();
                await PumpAsync();
                await PumpAsync();

                var mountedBeforeCaptureLost = viewModel.MountedTranscriptTurns
                    .Select(static turn => turn.StableId)
                    .ToArray();
                dragField.SetValue(view, true);

                scrollViewer.Offset = scrollViewer.Offset.WithY(0);
                await PumpAsync();
                await PumpAsync();

                Assert.Equal(
                    mountedBeforeCaptureLost,
                    viewModel.MountedTranscriptTurns.Select(static turn => turn.StableId).ToArray());

                endDragMethod.Invoke(view, null);
                await WaitUntilAsync(() => viewModel.MountedTranscriptTurns.Count > mountedBeforeCaptureLost.Length);
                Assert.Contains(actualTail, viewModel.MountedTranscriptTurns);
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
                await WaitUntilAsync(() => !viewModel.IsChatSurfaceLoading, timeoutMs: 5_000);
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

                transcript.RealizeCurrentViewportNow();
                if (TranscriptRealizationScheduler.Instance.HasPendingWork)
                    await WaitUntilAsync(() => !TranscriptRealizationScheduler.Instance.HasPendingWork);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);

                var settledY = GetTurnViewportY(view, scrollViewer, anchor!.Value.StableId);
                Assert.NotNull(settledY);
                Assert.False(shell.IsFollowingTail);
                Assert.InRange(Math.Abs(settledY.Value - anchor.Value.ViewportY), 0, 2.0);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ProgressiveTranscript_MountsMeasuredTailAndPrependsWithoutEviction()
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
                await WaitUntilAsync(() => !viewModel.IsChatSurfaceLoading, timeoutMs: 5_000);
                transcript.RealizeCurrentViewportNow();
                await PumpUntilAsync(
                    () => viewModel.MountedTranscriptTurns.Any(static turn => turn.RealizedItemsHost is not null)
                        && !TranscriptRealizationScheduler.Instance.HasPendingWork,
                    maxPumps: 20);

                Assert.True(viewModel.MountedTranscriptTurns.Count < viewModel.TranscriptTurns.Count);
                Assert.Equal(0, viewModel.TranscriptTopSpacerHeight);
                Assert.Equal(0, viewModel.TranscriptBottomSpacerHeight);
                var realizedAtTail = viewModel.MountedTranscriptTurns.Count(static turn => turn.RealizedItemsHost is not null);
                Assert.InRange(realizedAtTail, 1, viewModel.MountedTranscriptTurns.Count);

                var tail = viewModel.TranscriptTurns[^1];
                Assert.Contains(tail, viewModel.MountedTranscriptTurns);
                var shell = Assert.IsType<StrataChatShell>(view.FindControl<StrataChatShell>("ChatShell"));
                var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);
                var mountedBeforePrepend = viewModel.MountedTranscriptTurns.Count;
                shell.PreserveViewport();
                scrollViewer.Offset = scrollViewer.Offset.WithY(0);

                await WaitUntilAsync(() => viewModel.MountedTranscriptTurns.Count > mountedBeforePrepend);
                transcript.RealizeCurrentViewportNow();
                await WaitUntilAsync(() => !TranscriptRealizationScheduler.Instance.HasPendingWork);
                Assert.Contains(tail, viewModel.MountedTranscriptTurns);
                var tailControl = transcript.ItemsPanelRoot?.GetVisualDescendants()
                    .OfType<TranscriptTurnControl>()
                    .FirstOrDefault(control => ReferenceEquals(control.Turn, tail));
                Assert.NotNull(tailControl);
                await WaitUntilAsync(() => !tailControl.IsViewportActive);
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
    public async Task SharedProgressiveSurface_PreservesOtherWindowAnchorDuringPrepend()
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

            using var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            var firstView = new ChatView { DataContext = viewModel };
            var secondView = new ChatView { DataContext = viewModel };
            var firstWindow = new Window { Width = 1100, Height = 820, Content = firstView };
            var secondWindow = new Window { Width = 980, Height = 760, Content = secondView };
            firstWindow.Show();
            secondWindow.Show();
            try
            {
                await PumpAsync();
                await viewModel.LoadChatAsync(chat);
                await WaitUntilAsync(() => !viewModel.IsChatSurfaceLoading, timeoutMs: 5_000);

                var firstShell = Assert.IsType<StrataChatShell>(firstView.FindControl<StrataChatShell>("ChatShell"));
                var secondShell = Assert.IsType<StrataChatShell>(secondView.FindControl<StrataChatShell>("ChatShell"));
                var firstScroll = Assert.IsType<ScrollViewer>(firstShell.TranscriptScrollViewer);
                var secondScroll = Assert.IsType<ScrollViewer>(secondShell.TranscriptScrollViewer);
                await WaitUntilAsync(() => firstScroll.Extent.Height > firstScroll.Viewport.Height
                    && secondScroll.Extent.Height > secondScroll.Viewport.Height);

                secondShell.PreserveViewport();
                secondScroll.Offset = secondScroll.Offset.WithY(
                    Math.Max(0, (secondScroll.Extent.Height - secondScroll.Viewport.Height) * 0.45));
                await PumpAsync();
                var secondAnchor = CaptureVisibleTurnAnchor(secondView, secondScroll);
                Assert.NotNull(secondAnchor);

                var mountedBefore = viewModel.MountedTranscriptTurns.Count;
                firstShell.PreserveViewport();
                firstScroll.Offset = firstScroll.Offset.WithY(0);
                await WaitUntilAsync(() => viewModel.MountedTranscriptTurns.Count > mountedBefore);
                await PumpAsync();

                var restoredY = GetTurnViewportY(secondView, secondScroll, secondAnchor!.Value.StableId);
                Assert.NotNull(restoredY);
                Assert.InRange(Math.Abs(restoredY.Value - secondAnchor.Value.ViewportY), 0, 2.0);
                Assert.Contains(viewModel.TranscriptTurns[^1], viewModel.MountedTranscriptTurns);
            }
            finally
            {
                firstWindow.Close();
                secondWindow.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task FarTurnJump_UsesBoundedFocusedContextWithoutExpandingProgressiveHistory()
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
                await WaitUntilAsync(() => !viewModel.IsChatSurfaceLoading, timeoutMs: 5_000);
                await WaitUntilAsync(() => !TranscriptRealizationScheduler.Instance.HasPendingWork, timeoutMs: 5_000);
                await PumpAsync();

                var shell = Assert.IsType<StrataChatShell>(view.FindControl<StrataChatShell>("ChatShell"));
                var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);
                shell.JumpToLatest();
                await PumpAsync();
                Assert.True(shell.IsFollowingTail);
                Assert.True(shell.IsPinnedToBottom);
                shell.PreserveViewport();
                Assert.False(shell.IsFollowingTail);
                Assert.InRange(shell.CurrentDistanceFromBottom, 0, 2.0);

                var targetTurn = viewModel.TranscriptTurns.First(static turn => turn.Items.Count > 0);
                Assert.Null(targetTurn.RealizedItemsHost);
                var mountedBefore = viewModel.MountedTranscriptTurns.ToArray();
                Assert.DoesNotContain(targetTurn, mountedBefore);

                var method = typeof(ChatView).GetMethod(
                    "EnsureTurnRealizedAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(method);

                var task = Assert.IsAssignableFrom<Task<TranscriptTurnControl?>>(method.Invoke(view, [targetTurn]));
                var control = await task;
                Assert.NotNull(control);
                Assert.Same(targetTurn, control.Turn);
                Assert.NotNull(targetTurn.RealizedItemsHost);
                Assert.Equal(mountedBefore, viewModel.MountedTranscriptTurns);
                Assert.DoesNotContain(targetTurn, viewModel.MountedTranscriptTurns);

                var transcript = Assert.IsType<TranscriptItemsControl>(
                    view.FindControl<TranscriptItemsControl>("Transcript"));
                var focusedTurns = Assert.IsAssignableFrom<IEnumerable<TranscriptTurn>>(transcript.ItemsSource);
                Assert.InRange(focusedTurns.Count(), 1, 5);
                Assert.Contains(targetTurn, focusedTurns);
                Assert.True(view.FindControl<Border>("FocusedHistoryBanner")?.IsVisible);

                var point = control.TranslatePoint(default, scrollViewer);
                Assert.NotNull(point);
                Assert.True(point.Value.Y + control.Bounds.Height >= 0);
                Assert.True(point.Value.Y <= scrollViewer.Viewport.Height);

                var exitMethod = typeof(ChatView).GetMethod(
                    "ExitFocusedHistoryAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(exitMethod);
                var exitTask = Assert.IsAssignableFrom<Task>(exitMethod.Invoke(view, [true]));
                await exitTask;
                await PumpAsync();

                Assert.Same(viewModel.MountedTranscriptTurns, transcript.ItemsSource);
                Assert.False(view.FindControl<Border>("FocusedHistoryBanner")?.IsVisible == true);
                Assert.Equal(mountedBefore, viewModel.MountedTranscriptTurns);
                Assert.True(shell.IsFollowingTail);
                Assert.True(shell.IsPinnedToBottom);
                Assert.InRange(shell.CurrentDistanceFromBottom, 0, 2.0);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ActiveSearch_RebindsHitsAfterInPlaceTranscriptRebuild()
    {
        using var session = HeadlessTestSession.Start();

        await DispatchAsync(session, async () =>
        {
            var chat = CreateLongChat(pairCount: 24);
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
                await WaitUntilAsync(() => !viewModel.IsChatSurfaceLoading, timeoutMs: 5_000);

                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var openSearch = typeof(ChatView).GetMethod("OpenSearch", flags);
                var executeSearch = typeof(ChatView).GetMethod("ExecuteSearch", flags);
                var hitsField = typeof(ChatView).GetField("_searchHits", flags);
                Assert.NotNull(openSearch);
                Assert.NotNull(executeSearch);
                Assert.NotNull(hitsField);

                openSearch.Invoke(view, null);
                var input = Assert.IsType<TextBox>(view.FindControl<TextBox>("SearchInput"));
                input.Text = "Question 0";
                executeSearch.Invoke(view, null);
                var hitsBefore = Assert.IsAssignableFrom<System.Collections.IList>(hitsField.GetValue(view));
                Assert.NotEmpty(hitsBefore.Cast<object>());
                var oldTurn = hitsBefore[0]!.GetType().GetProperty("Turn")!.GetValue(hitsBefore[0]);

                viewModel.RebuildTranscript();
                await PumpAsync();
                await PumpAsync();

                var hitsAfter = Assert.IsAssignableFrom<System.Collections.IList>(hitsField.GetValue(view));
                Assert.NotEmpty(hitsAfter.Cast<object>());
                var newTurns = viewModel.TranscriptTurns.ToHashSet(ReferenceEqualityComparer.Instance);
                foreach (var hit in hitsAfter.Cast<object>())
                {
                    var turn = Assert.IsType<TranscriptTurn>(
                        hit.GetType().GetProperty("Turn")!.GetValue(hit));
                    Assert.Contains(turn, newTurns);
                }

                var newTurn = hitsAfter[0]!.GetType().GetProperty("Turn")!.GetValue(hitsAfter[0]);
                Assert.NotSame(oldTurn, newTurn);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SharedMeasuredHeight_DoesNotChangeLocalPlaceholderGeometry()
    {
        using var session = HeadlessTestSession.Start();

        await DispatchAsync(session, async () =>
        {
            var turn = new TranscriptTurn("turn:height-baseline");
            turn.Items.Add(new AssistantMessageItem(
                new ChatMessageViewModel(new ChatMessage
                {
                    Role = "assistant",
                    Content = new string('x', 20_000),
                }),
                showTimestamps: false));
            turn.MeasuredHeight = 12_000;

            var control = new TranscriptTurnControl
            {
                Turn = turn,
                IsViewportManaged = true,
            };
            var window = new Window
            {
                Width = 420,
                Height = 320,
                Content = control,
            };
            window.Show();
            try
            {
                await PumpAsync();
                Assert.Null(control.Content);
                Assert.Equal(TranscriptLayoutMetrics.MinimumEstimatedTurnHeight, control.MinHeight);
            }
            finally
            {
                window.Close();
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
    public async Task ClearingDataContext_ClearsMainTranscriptItemsSource()
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
                var transcript = Assert.IsType<TranscriptItemsControl>(
                    view.FindControl<TranscriptItemsControl>("Transcript"));
                Assert.Null(transcript.ItemsSource);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task UnmeasuredTurnPlaceholderUsesNeutralLocalMinimum()
    {
        using var session = HeadlessTestSession.Start();

        await DispatchAsync(session, async () =>
        {
            var turn = new TranscriptTurn("turn:estimated-baseline");
            turn.Items.Add(new AssistantMessageItem(
                new ChatMessageViewModel(new ChatMessage
                {
                    Role = "assistant",
                    Content = string.Join('\n', Enumerable.Repeat("A markdown paragraph with enough text to wrap.", 12))
                }),
                showTimestamps: false));
            var estimatedHeight = TranscriptPageWeightEstimator.EstimateTurnHeight(turn, 56d);
            Assert.True(estimatedHeight > TranscriptLayoutMetrics.MinimumEstimatedTurnHeight);

            var control = new TranscriptTurnControl
            {
                Turn = turn,
                IsViewportManaged = true,
            };
            var window = new Window
            {
                Width = 420,
                Height = 320,
                Content = control,
            };
            window.Show();
            await PumpAsync();

            Assert.Null(control.Content);
            Assert.Equal(TranscriptLayoutMetrics.MinimumEstimatedTurnHeight, control.MinHeight);
            Assert.False(turn.HasMeasuredRealizedHeight);

            window.Close();
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
