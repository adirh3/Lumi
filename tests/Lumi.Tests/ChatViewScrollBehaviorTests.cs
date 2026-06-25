using System;
using System.Linq;
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

        await session.Dispatch(async () =>
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

            var viewModel = new ChatViewModel(new DataStore(data), new CopilotService());
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
                var readerOffset = scrollViewer.Offset.Y;

                viewModel.SetProjectId(Guid.NewGuid());
                await PumpAsync();
                await PumpAsync();

                Assert.False(shell.IsFollowingTail);
                Assert.InRange(Math.Abs(scrollViewer.Offset.Y - readerOffset), 0, 1.5);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MountedTurnsRemovedWhileBusy_RehydratesViewportCoverage()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
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

            var viewModel = new ChatViewModel(new DataStore(data), new CopilotService());
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
                Assert.True(viewModel.MountedTranscriptTurns.Count < viewModel.TranscriptTurns.Count);

                viewModel.StatusText = "Thinking...";
                viewModel.IsBusy = true;
                await WaitUntilAsync(() => viewModel.MountedTranscriptTurns.Count > 0);
                await WaitUntilAsync(() => shell.IsPinnedToBottom);

                Assert.Contains(viewModel.MountedTranscriptTurns, turn => turn.StableId == "turn:typing");

                while (viewModel.MountedTranscriptTurns.Count > 1)
                    viewModel.MountedTranscriptTurns.RemoveAt(0);

                await PumpAsync();

                await WaitUntilAsync(() =>
                    viewModel.MountedTranscriptTurns.Count > 1
                    && scrollViewer.Extent.Height > scrollViewer.Viewport.Height
                    && shell.IsPinnedToBottom);

                Assert.True(viewModel.MountedTranscriptTurns.Count > 1);
                Assert.True(scrollViewer.Extent.Height > scrollViewer.Viewport.Height);
                Assert.True(shell.IsPinnedToBottom);
                Assert.Contains(viewModel.MountedTranscriptTurns, turn => turn.StableId == "turn:typing");
            }
            finally
            {
                viewModel.IsBusy = false;
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task WorktreeToggleHighlight_ResyncsWhenChatSurfaceChanges()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            };

            using var worktreeViewModel = new ChatViewModel(new DataStore(data), new CopilotService())
            {
                IsCodingProject = true
            };
            using var localViewModel = new ChatViewModel(new DataStore(data), new CopilotService())
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

        await session.Dispatch(async () =>
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

            var viewModel = new ChatViewModel(new DataStore(data), new CopilotService());
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
                await WaitUntilAsync(() => !shell.IsPinnedToBottom);
                var readerOffset = scrollViewer.Offset.Y;

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

                Assert.DoesNotContain(viewModel.MountedTranscriptTurns, turn => turn.StableId == assistantTurn.StableId);

                viewModel.IsBusy = false;

                await WaitUntilAsync(() =>
                    viewModel.MountedTranscriptTurns.Any(turn => turn.StableId == assistantTurn.StableId));

                Assert.False(shell.IsPinnedToBottom);
                Assert.InRange(Math.Abs(scrollViewer.Offset.Y - readerOffset), 0, 2.0);
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
    public async Task ScrollbarThumbDrag_DefersViewportPagingUntilReleaseOrCaptureLoss()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
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

            var viewModel = new ChatViewModel(new DataStore(data), new CopilotService());
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
                await WaitUntilAsync(() =>
                    viewModel.MountedTranscriptTurns.Count > 0
                    && viewModel.MountedTranscriptTurns[0].StableId != mountedBefore[0]);

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
                await WaitUntilAsync(() =>
                    viewModel.MountedTranscriptTurns.Count > 0
                    && viewModel.MountedTranscriptTurns[0].StableId != mountedBeforeCaptureLost[0]);
                window.MouseUp(thumbCenter, MouseButton.Left, RawInputModifiers.None);
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

    private static TranscriptTurn CreateCompletedAssistantTailTurn()
    {
        var turn = new TranscriptTurn("turn:test-completed-assistant-tail");
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

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
