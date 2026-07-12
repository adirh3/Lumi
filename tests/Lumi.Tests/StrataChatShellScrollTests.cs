using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class StrataChatShellScrollTests
{
    [Fact]
    public async Task SmallUserScrollAway_DisablesFollowModeUntilJumpToLatest()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var transcript = new StackPanel { Spacing = 8 };
            for (var i = 0; i < 48; i++)
            {
                transcript.Children.Add(new Border
                {
                    Height = 56,
                    Child = new TextBlock { Text = $"Turn {i}" }
                });
            }

            var shell = new StrataChatShell
            {
                Header = new TextBlock { Text = "Scroll Test" },
                Transcript = transcript,
                Composer = new Border { Height = 48 }
            };

            var window = new Window
            {
                Width = 720,
                Height = 520,
                Content = shell,
            };

            window.Show();
            await PumpAsync();
            await PumpAsync();

            var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);

            shell.JumpToLatest();
            await PumpAsync();

            var bottomOffset = scrollViewer.Offset.Y;
            Assert.True(bottomOffset > 0);
            Assert.True(shell.IsFollowingTail);
            Assert.True(shell.IsPinnedToBottom);

            scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Max(0, bottomOffset - 24));
            await PumpAsync();

            Assert.False(shell.IsFollowingTail);
            Assert.False(shell.IsPinnedToBottom);
            var readerOffset = scrollViewer.Offset.Y;

            transcript.Children.Add(new Border
            {
                Height = 56,
                Child = new TextBlock { Text = "Newest turn" }
            });
            await PumpAsync();

            Assert.True(shell.HasNewContent);

            shell.ScrollToEnd();
            await PumpAsync();

            Assert.InRange(Math.Abs(scrollViewer.Offset.Y - readerOffset), 0, 1.5);

            shell.JumpToLatest();
            await PumpAsync();

            Assert.True(shell.IsFollowingTail);
            Assert.True(shell.IsPinnedToBottom);
            Assert.False(shell.HasNewContent);
            Assert.True(scrollViewer.Offset.Y > readerOffset + 10);

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ProgrammaticBottomLanding_DoesNotReenterFollowMode()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var transcript = new StackPanel { Spacing = 8 };
            for (var i = 0; i < 48; i++)
            {
                transcript.Children.Add(new Border
                {
                    Height = 56,
                    Child = new TextBlock { Text = $"Turn {i}" }
                });
            }

            var shell = new StrataChatShell
            {
                Header = new TextBlock { Text = "Scroll Test" },
                Transcript = transcript,
                Composer = new Border { Height = 48 }
            };

            var window = new Window
            {
                Width = 720,
                Height = 520,
                Content = shell,
            };

            window.Show();
            await PumpAsync();
            await PumpAsync();

            var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);

            shell.JumpToLatest();
            await PumpAsync();

            var bottomOffset = scrollViewer.Offset.Y;
            scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Max(0, bottomOffset - 24));
            await PumpAsync();

            Assert.False(shell.IsFollowingTail);
            Assert.False(shell.IsPinnedToBottom);

            transcript.Children.Add(new Border
            {
                Height = 56,
                Child = new TextBlock { Text = "Newest turn" }
            });
            await PumpAsync();

            Assert.True(shell.HasNewContent);

            var maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            shell.ScrollToVerticalOffset(maxOffset);
            await PumpAsync();

            Assert.False(shell.IsFollowingTail);
            Assert.False(shell.IsPinnedToBottom);
            Assert.True(shell.HasNewContent);

            shell.JumpToLatest();
            await PumpAsync();

            Assert.True(shell.IsFollowingTail);
            Assert.True(shell.IsPinnedToBottom);
            Assert.False(shell.HasNewContent);

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ProgrammaticBottomLanding_HidesScrollButtonAtBottom()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var transcript = new StackPanel { Spacing = 8 };
            for (var i = 0; i < 48; i++)
            {
                transcript.Children.Add(new Border
                {
                    Height = 56,
                    Child = new TextBlock { Text = $"Turn {i}" }
                });
            }

            var shell = new StrataChatShell
            {
                Header = new TextBlock { Text = "Scroll Test" },
                Transcript = transcript,
                Composer = new Border { Height = 48 }
            };

            var window = new Window
            {
                Width = 720,
                Height = 520,
                Content = shell,
            };

            window.Show();
            await PumpAsync();
            await PumpAsync();

            var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);
            var scrollButtonHost = window
                .GetVisualDescendants()
                .OfType<Panel>()
                .First(control => control.Name == "PART_ScrollToBottomHost");

            shell.JumpToLatest();
            await PumpAsync();

            var bottomOffset = scrollViewer.Offset.Y;
            scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Max(0, bottomOffset - 24));
            await PumpAsync();

            transcript.Children.Add(new Border
            {
                Height = 56,
                Child = new TextBlock { Text = "Newest turn" }
            });
            await PumpAsync();

            Assert.True(scrollButtonHost.IsHitTestVisible);
            Assert.InRange(scrollButtonHost.Opacity, 0.99, 1.01);

            var maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            shell.ScrollToVerticalOffset(maxOffset);
            await PumpAsync();

            Assert.False(shell.IsFollowingTail);
            Assert.False(shell.IsPinnedToBottom);
            Assert.True(shell.HasNewContent);
            Assert.False(scrollButtonHost.IsHitTestVisible);
            Assert.InRange(scrollButtonHost.Opacity, 0, 0.01);

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SmallManualReturnToBottom_ReentersFollowMode()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var transcript = new StackPanel { Spacing = 8 };
            for (var i = 0; i < 48; i++)
            {
                transcript.Children.Add(new Border
                {
                    Height = 56,
                    Child = new TextBlock { Text = $"Turn {i}" }
                });
            }

            var shell = new StrataChatShell
            {
                Header = new TextBlock { Text = "Scroll Test" },
                Transcript = transcript,
                Composer = new Border { Height = 48 }
            };

            var window = new Window
            {
                Width = 720,
                Height = 520,
                Content = shell,
            };

            window.Show();
            await PumpAsync();
            await PumpAsync();

            var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);

            shell.JumpToLatest();
            await PumpAsync();

            var bottomOffset = scrollViewer.Offset.Y;
            scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Max(0, bottomOffset - 24));
            await PumpAsync();

            Assert.False(shell.IsFollowingTail);
            Assert.False(shell.IsPinnedToBottom);

            transcript.Children.Add(new Border
            {
                Height = 56,
                Child = new TextBlock { Text = "Newest turn" }
            });
            await PumpAsync();

            Assert.True(shell.HasNewContent);

            var maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            scrollViewer.Offset = scrollViewer.Offset.WithY(maxOffset);
            await PumpAsync();

            Assert.True(shell.IsFollowingTail);
            Assert.True(shell.IsPinnedToBottom);
            Assert.False(shell.HasNewContent);

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ManualReturnToBottom_ReentersFollowMode()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var transcript = new StackPanel { Spacing = 8 };
            for (var i = 0; i < 64; i++)
            {
                transcript.Children.Add(new Border
                {
                    Height = 56,
                    Child = new TextBlock { Text = $"Turn {i}" }
                });
            }

            var shell = new StrataChatShell
            {
                Header = new TextBlock { Text = "Scroll Test" },
                Transcript = transcript,
                Composer = new Border { Height = 48 }
            };

            var window = new Window
            {
                Width = 720,
                Height = 520,
                Content = shell,
            };

            window.Show();
            await PumpAsync();
            await PumpAsync();

            var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);

            shell.JumpToLatest();
            await PumpAsync();

            var bottomOffset = scrollViewer.Offset.Y;
            scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Max(0, bottomOffset - 320));
            await PumpAsync();

            Assert.False(shell.IsFollowingTail);
            Assert.False(shell.IsPinnedToBottom);

            transcript.Children.Add(new Border
            {
                Height = 56,
                Child = new TextBlock { Text = "Newest turn" }
            });
            await PumpAsync();

            Assert.True(shell.HasNewContent);

            var maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            scrollViewer.Offset = scrollViewer.Offset.WithY(maxOffset);
            await PumpAsync();

            Assert.True(shell.IsFollowingTail);
            Assert.True(shell.IsPinnedToBottom);
            Assert.False(shell.HasNewContent);

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MouseWheelOverTranscriptContent_ScrollsShellAndLeavesFollowMode()
    {
        using var session = HeadlessTestSession.Start();

        double before = 0;
        double after = 0;
        bool isFollowingTail = true;

        await session.Dispatch(async () =>
        {
            var transcript = new StackPanel { Spacing = 8 };
            for (var i = 0; i < 64; i++)
            {
                transcript.Children.Add(new Border
                {
                    Height = 56,
                    Child = new TextBlock { Text = $"Turn {i}" }
                });
            }

            var shell = new StrataChatShell
            {
                Header = new TextBlock { Text = "Scroll Test" },
                Transcript = transcript,
                Composer = new Border { Height = 48 }
            };

            var window = new Window
            {
                Width = 720,
                Height = 520,
                Content = shell,
            };

            window.Show();
            await PumpAsync();
            await PumpAsync();

            var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);

            shell.JumpToLatest();
            await PumpAsync();

            before = scrollViewer.Offset.Y;
            var wheelPoint = GetCenterPoint(window, scrollViewer);
            window.MouseWheel(wheelPoint, new Vector(0, 1), RawInputModifiers.None);
            await PumpAsync();
            await PumpAsync();

            after = scrollViewer.Offset.Y;
            isFollowingTail = shell.IsFollowingTail;

            window.Close();
        }, CancellationToken.None);

        Assert.True(before > 0, "Test setup should start at the bottom of a scrollable transcript.");
        Assert.True(after < before - 1, "A real upward mouse-wheel gesture over the transcript should scroll history into view.");
        Assert.False(isFollowingTail);
    }

    [Fact]
    public async Task MouseWheelOverExhaustedNestedScrollViewer_ScrollsShell()
    {
        using var session = HeadlessTestSession.Start();

        double before = 0;
        double after = 0;

        await session.Dispatch(async () =>
        {
            var transcript = new StackPanel { Spacing = 8 };
            for (var i = 0; i < 56; i++)
            {
                transcript.Children.Add(new Border
                {
                    Height = 56,
                    Child = new TextBlock { Text = $"Turn {i}" }
                });
            }

            var nested = new ScrollViewer
            {
                Height = 160,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new TextBlock
                {
                    Text = "Short tool output that does not need its own vertical scrolling.",
                    TextWrapping = TextWrapping.Wrap
                }
            };

            transcript.Children.Add(new Border
            {
                Height = 180,
                Padding = new Thickness(10),
                Child = nested
            });

            var shell = new StrataChatShell
            {
                Header = new TextBlock { Text = "Scroll Test" },
                Transcript = transcript,
                Composer = new Border { Height = 48 }
            };

            var window = new Window
            {
                Width = 720,
                Height = 520,
                Content = shell,
            };

            window.Show();
            await PumpAsync();
            await PumpAsync();

            var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);
            shell.JumpToLatest();
            await PumpAsync();

            before = scrollViewer.Offset.Y;
            var wheelPoint = GetCenterPoint(window, nested);
            window.MouseWheel(wheelPoint, new Vector(0, 1), RawInputModifiers.None);
            await PumpAsync();
            await PumpAsync();

            after = scrollViewer.Offset.Y;

            window.Close();
        }, CancellationToken.None);

        Assert.True(before > 0, "Test setup should start at the bottom of a scrollable transcript.");
        Assert.True(after < before - 1, "Wheel input over a nested scroller with no vertical room must keep scrolling the transcript.");
    }

    // ── Regression: a left-click in the transcript must not scroll it ──
    //
    // Avalonia's ScrollViewer.OnGotFocus calls BringIntoView() on whichever focusable descendant
    // received focus (gated by ScrollViewer.BringIntoViewOnFocusChange, default true). Transcript
    // items (StrataChatMessage, tool cards, etc.) are Focusable=True, so a plain left-click on one
    // whose top sits above the viewport made the ScrollViewer jump up to reveal that top. The shell
    // template disables BringIntoViewOnFocusChange on PART_TranscriptScroll to stop this.

    [Fact]
    public async Task LeftClickOnFocusableTranscriptItem_DoesNotScroll()
    {
        using var session = HeadlessTestSession.Start();

        double before = 0;
        double after = 0;
        bool targetFound = false;
        bool clickedItemFocused = false;

        await session.Dispatch(async () =>
        {
            var transcript = new StackPanel { Spacing = 8 };
            for (var i = 0; i < 40; i++)
            {
                transcript.Children.Add(new Border
                {
                    Height = 120,
                    Focusable = true,                 // mirrors StrataChatMessage (Focusable=True)
                    Background = Brushes.Transparent,  // hit-testable surface for the click
                    Child = new TextBlock { Text = $"Turn {i}" }
                });
            }

            var shell = new StrataChatShell
            {
                Header = new TextBlock { Text = "Scroll Test" },
                Transcript = transcript,
                Composer = new Border { Height = 48 }
            };

            var window = new Window { Width = 720, Height = 520, Content = shell };
            window.Show();
            await PumpAsync();
            await PumpAsync();

            var scrollViewer = (ScrollViewer)shell.TranscriptScrollViewer!;

            shell.JumpToLatest();
            await PumpAsync();

            // Park the reader in the middle so there is content above and below.
            var bottomOffset = scrollViewer.Offset.Y;
            scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Max(0, bottomOffset - 200));
            await PumpAsync();
            await PumpAsync();

            // Pick a focusable item straddling the TOP edge: its top is above the viewport, so the
            // buggy focus-driven BringIntoView would scroll UP to reveal it.
            Border? targetItem = null;
            Point clickPoint = default;
            foreach (var child in transcript.Children.OfType<Border>())
            {
                var top = child.TranslatePoint(new Point(0, 0), scrollViewer);
                if (top is null) continue;

                var topY = top.Value.Y;
                var bottomY = topY + child.Bounds.Height;
                if (topY < -10 && bottomY > 20 && bottomY < scrollViewer.Viewport.Height)
                {
                    var visibleMidY = bottomY / 2;
                    var pt = child.TranslatePoint(new Point(child.Bounds.Width / 2, visibleMidY - topY), window);
                    if (pt is null) continue;

                    targetItem = child;
                    clickPoint = pt.Value;
                    break;
                }
            }

            targetFound = targetItem is not null;
            if (targetItem is null)
            {
                window.Close();
                return;
            }

            before = scrollViewer.Offset.Y;
            window.MouseDown(clickPoint, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(clickPoint, MouseButton.Left, RawInputModifiers.None);
            await PumpAsync();
            await PumpAsync();

            after = scrollViewer.Offset.Y;
            clickedItemFocused = targetItem.IsFocused;

            window.Close();
        }, CancellationToken.None);

        Assert.True(targetFound, "Test setup should find a focusable item straddling the viewport top.");
        // The regression: the offset must stay put when the user simply clicks a transcript item.
        Assert.InRange(Math.Abs(after - before), 0, 1.0);
        // Focus still moves to the clicked item — only the auto-scroll side effect is suppressed.
        Assert.True(clickedItemFocused, "Clicked transcript item should still receive focus.");
    }

    [Fact]
    public async Task ExplicitBringIntoView_StillScrollsTranscript()
    {
        using var session = HeadlessTestSession.Start();

        double before = 0;
        double after = 0;

        await session.Dispatch(async () =>
        {
            var transcript = new StackPanel { Spacing = 8 };
            for (var i = 0; i < 40; i++)
            {
                transcript.Children.Add(new Border
                {
                    Height = 120,
                    Focusable = true,
                    Background = Brushes.Transparent,
                    Child = new TextBlock { Text = $"Turn {i}" }
                });
            }

            var shell = new StrataChatShell
            {
                Header = new TextBlock { Text = "Scroll Test" },
                Transcript = transcript,
                Composer = new Border { Height = 48 }
            };

            var window = new Window { Width = 720, Height = 520, Content = shell };
            window.Show();
            await PumpAsync();
            await PumpAsync();

            var scrollViewer = (ScrollViewer)shell.TranscriptScrollViewer!;

            // Anchor at the very top, then explicitly bring a far-below item into view. Disabling
            // BringIntoViewOnFocusChange must NOT break intentional BringIntoView() calls (used by
            // Ctrl+F search highlight and reasoning reveal).
            scrollViewer.Offset = scrollViewer.Offset.WithY(0);
            await PumpAsync();
            before = scrollViewer.Offset.Y;

            var target = transcript.Children.OfType<Border>().Last();
            target.BringIntoView();
            await PumpAsync();
            await PumpAsync();

            after = scrollViewer.Offset.Y;
            window.Close();
        }, CancellationToken.None);

        Assert.True(after > before + 10, "Explicit BringIntoView() should still scroll the transcript.");
    }

    [Fact]
    public async Task QueuedFollow_IsCancelledWhenReaderScrollsAwayBeforeItRuns()
    {
        using var session = HeadlessTestSession.Start();
        double readerOffset = 0;
        double finalOffset = 0;
        bool isFollowingTail = true;

        await session.Dispatch(async () =>
        {
            var transcript = new StackPanel { Spacing = 8 };
            for (var i = 0; i < 48; i++)
                transcript.Children.Add(new Border { Height = 56 });

            var shell = new StrataChatShell
            {
                Transcript = transcript,
                Composer = new Border { Height = 48 },
            };
            var window = new Window { Width = 720, Height = 520, Content = shell };
            window.Show();
            await PumpAsync();
            await PumpAsync();

            var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);
            shell.JumpToLatest();
            await PumpAsync();

            shell.NotifyTranscriptLayoutChanged();
            var wheelPoint = GetCenterPoint(window, scrollViewer);
            window.MouseWheel(wheelPoint, new Vector(0, 1), RawInputModifiers.None);
            await PumpAsync();
            await PumpAsync();

            readerOffset = scrollViewer.Offset.Y;
            await PumpAsync();
            finalOffset = scrollViewer.Offset.Y;
            isFollowingTail = shell.IsFollowingTail;
            window.Close();
        }, CancellationToken.None);

        Assert.False(isFollowingTail);
        Assert.InRange(Math.Abs(finalOffset - readerOffset), 0, 1.5);
    }

    [Fact]
    public async Task QueuedContentFollow_CancelledBeforeLanding_MarksNewContent()
    {
        using var session = HeadlessTestSession.Start();
        bool isFollowingTail = true;
        bool hasNewContent = false;

        await session.Dispatch(async () =>
        {
            var transcript = new StackPanel { Spacing = 8 };
            for (var i = 0; i < 48; i++)
                transcript.Children.Add(new Border { Height = 56 });

            var shell = new StrataChatShell
            {
                Transcript = transcript,
                Composer = new Border { Height = 48 },
            };
            var window = new Window { Width = 720, Height = 520, Content = shell };
            window.Show();
            await PumpAsync();
            await PumpAsync();

            shell.JumpToLatest();
            await PumpAsync();

            transcript.Children.Add(new Border { Height = 56 });
            shell.PreserveViewport();
            await PumpAsync();
            await PumpAsync();

            isFollowingTail = shell.IsFollowingTail;
            hasNewContent = shell.HasNewContent;
            window.Close();
        }, CancellationToken.None);

        Assert.False(isFollowingTail);
        Assert.True(hasNewContent);
    }

    [Fact]
    public async Task PageUpDuringProgrammaticScrollSuppression_LeavesFollowMode()
    {
        using var session = HeadlessTestSession.Start();
        double before = 0;
        double after = 0;
        bool isFollowingTail = true;

        await session.Dispatch(async () =>
        {
            var transcript = new StackPanel { Spacing = 8 };
            for (var i = 0; i < 48; i++)
            {
                transcript.Children.Add(new Border
                {
                    Height = 56,
                    Focusable = true,
                });
            }

            var shell = new StrataChatShell
            {
                Transcript = transcript,
                Composer = new Border { Height = 48 },
            };
            var window = new Window { Width = 720, Height = 520, Content = shell };
            window.Show();
            await PumpAsync();
            await PumpAsync();

            var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);
            shell.JumpToLatest();
            await PumpAsync();

            before = scrollViewer.Offset.Y;
            shell.ScrollToVerticalOffset(before);
            Assert.True(((Border)transcript.Children[^1]).Focus());
            window.KeyPress(Key.PageUp, RawInputModifiers.None, PhysicalKey.None, null);
            await PumpAsync();
            await PumpAsync();

            after = scrollViewer.Offset.Y;
            isFollowingTail = shell.IsFollowingTail;
            window.Close();
        }, CancellationToken.None);

        Assert.True(after < before - 1, "PageUp should scroll history even while a programmatic-scroll release is pending.");
        Assert.False(isFollowingTail);
    }

    [Fact]
    public async Task TouchScrollDuringProgrammaticScrollSuppression_LeavesFollowMode()
    {
        using var session = HeadlessTestSession.Start();
        long generationBefore = 0;
        long generationAfter = 0;
        bool isFollowingTail = true;

        await session.Dispatch(async () =>
        {
            var transcript = new StackPanel { Spacing = 8 };
            for (var i = 0; i < 48; i++)
                transcript.Children.Add(new Border { Height = 56 });

            var shell = new StrataChatShell
            {
                Transcript = transcript,
                Composer = new Border { Height = 48 },
            };
            var window = new Window { Width = 720, Height = 520, Content = shell };
            window.Show();
            await PumpAsync();
            await PumpAsync();

            var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);
            shell.JumpToLatest();
            await PumpAsync();

            shell.ScrollToVerticalOffset(scrollViewer.Offset.Y);
            generationBefore = shell.ScrollGeneration;
            transcript.RaiseEvent(new ScrollGestureEventArgs(
                ScrollGestureEventArgs.GetNextFreeId(),
                new Vector(0, -120))
            {
                RoutedEvent = InputElement.ScrollGestureEvent,
            });
            await PumpAsync();
            await PumpAsync();

            generationAfter = shell.ScrollGeneration;
            isFollowingTail = shell.IsFollowingTail;
            window.Close();
        }, CancellationToken.None);

        Assert.True(generationAfter > generationBefore);
        Assert.False(isFollowingTail);
    }

    [Fact]
    public async Task QueuedCompensation_IsCancelledByANewUserScroll()
    {
        using var session = HeadlessTestSession.Start();
        double beforeSecondUserScroll = 0;
        double afterSecondUserScroll = 0;
        long compensationGeneration = 0;
        long userGeneration = 0;
        bool isFollowingTail = true;

        await session.Dispatch(async () =>
        {
            var transcript = new StackPanel { Spacing = 8 };
            for (var i = 0; i < 48; i++)
                transcript.Children.Add(new Border { Height = 56 });

            var shell = new StrataChatShell
            {
                Transcript = transcript,
                Composer = new Border { Height = 48 },
            };
            var window = new Window { Width = 720, Height = 520, Content = shell };
            window.Show();
            await PumpAsync();
            await PumpAsync();

            var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);
            shell.JumpToLatest();
            await PumpAsync();

            var wheelPoint = GetCenterPoint(window, scrollViewer);
            window.MouseWheel(wheelPoint, new Vector(0, 1), RawInputModifiers.None);
            await PumpAsync();

            beforeSecondUserScroll = scrollViewer.Offset.Y;
            compensationGeneration = shell.ScrollGeneration;
            shell.CompensateForContentAbove(1_000);
            window.MouseWheel(wheelPoint, new Vector(0, 1), RawInputModifiers.None);
            userGeneration = shell.ScrollGeneration;
            await PumpAsync();
            await PumpAsync();

            afterSecondUserScroll = scrollViewer.Offset.Y;
            isFollowingTail = shell.IsFollowingTail;
            window.Close();
        }, CancellationToken.None);

        Assert.False(isFollowingTail);
        Assert.True(userGeneration > compensationGeneration);
        Assert.True(
            afterSecondUserScroll <= beforeSecondUserScroll + 1.5,
            "Stale positive compensation must not move the reader down after new user input.");
    }

    [Fact]
    public async Task LayoutGrowthWhileFollowing_RemainsPinnedToBottom()
    {
        using var session = HeadlessTestSession.Start();
        bool isFollowingTail = false;
        bool isPinnedToBottom = false;

        await session.Dispatch(async () =>
        {
            var transcript = new StackPanel { Spacing = 8 };
            for (var i = 0; i < 48; i++)
                transcript.Children.Add(new Border { Height = 56 });

            var shell = new StrataChatShell
            {
                Transcript = transcript,
                Composer = new Border { Height = 48 },
            };
            var window = new Window { Width = 720, Height = 520, Content = shell };
            window.Show();
            await PumpAsync();
            await PumpAsync();

            shell.JumpToLatest();
            await PumpAsync();

            ((Border)transcript.Children[^1]).Height += 320;
            shell.NotifyTranscriptLayoutChanged();
            await PumpAsync();
            await PumpAsync();

            isFollowingTail = shell.IsFollowingTail;
            isPinnedToBottom = shell.IsPinnedToBottom;
            window.Close();
        }, CancellationToken.None);

        Assert.True(isFollowingTail);
        Assert.True(isPinnedToBottom);
    }

    [Fact]
    public async Task DirectEndAppendWhileScrolledAway_MarksNewContent()
    {
        using var session = HeadlessTestSession.Start();
        bool isFollowingTail = true;
        bool hasNewContent = false;

        await session.Dispatch(async () =>
        {
            var transcript = new StackPanel { Spacing = 8 };
            for (var i = 0; i < 48; i++)
                transcript.Children.Add(new Border { Height = 56 });

            var shell = new StrataChatShell
            {
                Transcript = transcript,
                Composer = new Border { Height = 48 },
            };
            var window = new Window { Width = 720, Height = 520, Content = shell };
            window.Show();
            await PumpAsync();
            await PumpAsync();

            var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);
            shell.JumpToLatest();
            await PumpAsync();

            var wheelPoint = GetCenterPoint(window, scrollViewer);
            window.MouseWheel(wheelPoint, new Vector(0, 1), RawInputModifiers.None);
            await PumpAsync();

            transcript.Children.Add(new Border { Height = 56 });
            await PumpAsync();

            isFollowingTail = shell.IsFollowingTail;
            hasNewContent = shell.HasNewContent;
            window.Close();
        }, CancellationToken.None);

        Assert.False(isFollowingTail);
        Assert.True(hasNewContent);
    }

    [Fact]
    public async Task ReattachedShell_RestoresScrollAndAppendHandlers()
    {
        using var session = HeadlessTestSession.Start();
        bool isFollowingTail = true;
        bool hasNewContent = false;

        await session.Dispatch(async () =>
        {
            var transcript = new StackPanel { Spacing = 8 };
            for (var i = 0; i < 48; i++)
                transcript.Children.Add(new Border { Height = 56 });

            var shell = new StrataChatShell
            {
                Transcript = transcript,
                Composer = new Border { Height = 48 },
            };
            var window = new Window { Width = 720, Height = 520, Content = shell };
            window.Show();
            await PumpAsync();
            await PumpAsync();

            window.Content = null;
            await PumpAsync();
            window.Content = shell;
            await PumpAsync();
            await PumpAsync();

            var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);
            shell.JumpToLatest();
            await PumpAsync();

            var wheelPoint = GetCenterPoint(window, scrollViewer);
            window.MouseWheel(wheelPoint, new Vector(0, 1), RawInputModifiers.None);
            await PumpAsync();

            transcript.Children.Add(new Border { Height = 56 });
            await PumpAsync();

            isFollowingTail = shell.IsFollowingTail;
            hasNewContent = shell.HasNewContent;
            window.Close();
        }, CancellationToken.None);

        Assert.False(isFollowingTail);
        Assert.True(hasNewContent);
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static Point GetCenterPoint(Window window, Control target)
    {
        var topLeft = target.TranslatePoint(new Point(0, 0), window)
            ?? throw new InvalidOperationException("Target is not attached to the test window.");

        return topLeft + new Point(target.Bounds.Width / 2, target.Bounds.Height / 2);
    }
}
