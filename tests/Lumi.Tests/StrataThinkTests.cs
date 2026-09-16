using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class StrataThinkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpandingNearViewportBottom_OnlyScrollsForUserInput(bool userInitiated)
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var scrollViewer = new ScrollViewer
            {
                Width = 520,
                Height = 300,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new Border { Height = 220 },
                        new StrataThink
                        {
                            Label = "7 sources",
                            Content = new Border
                            {
                                Width = 360,
                                Height = 220,
                            },
                        },
                        new Border { Height = 24 },
                    }
                }
            };

            var window = new Window
            {
                Width = 640,
                Height = 360,
                Content = scrollViewer,
            };

            window.Show();
            await PumpAsync();

            var think = Assert.IsType<StrataThink>(((StackPanel)scrollViewer.Content!).Children[1]);
            Assert.Equal(0, scrollViewer.Offset.Y);

            if (userInitiated)
            {
                think.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Enter
                });
            }
            else
            {
                think.IsExpanded = true;
            }
            await Task.Delay(450);
            await PumpAsync();

            Assert.True(think.IsExpanded);
            Assert.Equal(userInitiated, scrollViewer.Offset.Y > 0);

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task HostScrolling_DoesNotDisableExpandedThinkContent()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var think = new StrataThink
            {
                Label = "Reasoning",
                IsExpanded = true,
                Content = new Border
                {
                    Width = 360,
                    Height = 920,
                },
            };

            var transcript = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new Border { Height = 160 },
                    think,
                    new Border { Height = 160 },
                }
            };

            var shell = new StrataChatShell
            {
                Header = new TextBlock { Text = "Transcript Test" },
                Transcript = transcript,
                Composer = new Border { Height = 48 },
            };

            var window = new Window
            {
                Width = 700,
                Height = 520,
                Content = shell,
            };

            window.Show();
            await PumpAsync();
            await PumpAsync();

            var transcriptScrollViewer = shell.TranscriptScrollViewer;
            Assert.NotNull(transcriptScrollViewer);

            var expandedContent = Assert.IsType<Border>(think.DisplayedContent);
            Assert.True(expandedContent.Bounds.Height > 0);
            Assert.DoesNotContain(think.GetVisualDescendants(), control => control is ScrollViewer);

            SetTranscriptScrollingState(shell, true);
            await PumpAsync();

            var hitTestAncestors = expandedContent.GetVisualAncestors()
                .OfType<Control>()
                .TakeWhile(control => !ReferenceEquals(control, transcriptScrollViewer))
                .ToArray();

            Assert.Contains(think, hitTestAncestors);
            Assert.All(hitTestAncestors, control => Assert.True(control.IsHitTestVisible));

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ExpandedInsideNarrowHost_UsesAvailableHostWidth()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var think = new StrataThink
            {
                Label = "Reasoning",
                MaxWidth = 760,
                IsExpanded = true,
                Content = new StrataMarkdown
                {
                    IsInline = true,
                    Markdown = "This reasoning block should wrap inside the narrow host instead of being clipped when wider ancestors exist."
                }
            };

            var host = new Border
            {
                Width = 240,
                Child = think
            };

            var window = new Window
            {
                Width = 1000,
                Height = 400,
                Content = new StackPanel
                {
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    Children =
                    {
                        host
                    }
                }
            };

            window.Show();
            await PumpAsync();
            await PumpAsync();

            var pill = think.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(candidate => candidate.Name == "PART_Pill");

            Assert.NotNull(pill);
            Assert.True(host.Bounds.Width > 0);
            Assert.True(pill!.Bounds.Width <= host.Bounds.Width + 0.5,
                $"Expanded think pill width {pill.Bounds.Width} should stay within host width {host.Bounds.Width}.");

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ExpandedInsideStretchPresenter_RefreshesToPresenterWidth()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var think = new StrataThink
            {
                Label = "Reasoning",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                MaxWidth = 760,
                IsExpanded = true,
                Content = new TextBlock
                {
                    Text = "This reasoning block should expand to the available presenter width instead of staying stuck at a narrower initial measure.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                }
            };

            var presenter = new ContentPresenter
            {
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Content = think
            };

            var window = new Window
            {
                Width = 640,
                Height = 360,
                Content = new Border
                {
                    Width = 520,
                    Child = new StackPanel
                    {
                        Children =
                        {
                            presenter
                        }
                    }
                }
            };

            window.Show();
            await PumpAsync();

            var pill = think.GetVisualDescendants()
                .OfType<Border>()
                .Single(control => control.Name == "PART_Pill");
            // Check the layout target, not an intermediate width animation frame.
            pill.Transitions = null;
            await PumpAsync();

            Assert.True(presenter.Bounds.Width > 0);
            Assert.True(think.Bounds.Width >= presenter.Bounds.Width - 1,
                $"Expanded think width {think.Bounds.Width} should match presenter width {presenter.Bounds.Width}.");

            window.Close();
        }, CancellationToken.None);
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static void SetTranscriptScrollingState(StrataChatShell shell, bool isScrolling)
    {
        var method = typeof(StrataChatShell).GetMethod("SetTranscriptScrollingState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(shell, [isScrolling]);
    }
}
