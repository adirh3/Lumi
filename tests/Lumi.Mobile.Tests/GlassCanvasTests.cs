using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.Behaviors;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Mobile.Tests;

[Collection("Headless mobile UI")]
public sealed class GlassCanvasTests
{
    private const string FinalAnswer = "Here is the **final** answer.\n\n- one\n- two";

    [Fact]
    public Task FinalAnswerCopiesItsTextAndConfirmsOnTheTap() =>
        RunChatAsync(Turn(finalAnswerId: "answer"), async (_, view, window) =>
        {
            var copy = Assert.Single(Named<Button>(view, "CopyAnswerButton"), button => button.IsEffectivelyVisible);
            Assert.Equal("answer", Assert.IsType<AssistantItemViewModel>(copy.DataContext).Id);
            Assert.True(copy.Bounds.Width >= 48 && copy.Bounds.Height >= 48);
            Assert.DoesNotContain(Named<Button>(view, "ShareAnswerButton"), button => button.IsEffectivelyVisible);

            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("copied", copy.Classes);

            await SettleAsync(window);
            Assert.Contains("copied", copy.Classes);
            Assert.Equal(FinalAnswer, await TopLevel.GetTopLevel(view)!.Clipboard!.TryGetTextAsync());

            await Task.Delay(1700);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("copied", copy.Classes);
        });

    [Fact]
    public Task StreamingAndPreambleAnswersOfferNoActions() =>
        RunChatAsync(Turn(finalAnswerId: null, streaming: true), (_, view, _) =>
        {
            Assert.NotEmpty(view.GetVisualDescendants().OfType<StrataMarkdown>());
            Assert.DoesNotContain(Named<Button>(view, "CopyAnswerButton"), button => button.IsEffectivelyVisible);
            return Task.CompletedTask;
        });

    [Fact]
    public async Task ShareHandsTheAnswerToThePlatformShareSheet()
    {
        var share = new RecordingShareService();
        MobilePlatformServices.TextShare = share;
        try
        {
            await RunChatAsync(Turn(finalAnswerId: "answer"), (model, view, _) =>
            {
                var button = Assert.Single(Named<Button>(view, "ShareAnswerButton"), item => item.IsEffectivelyVisible);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(FinalAnswer, share.Text);
                Assert.Equal(model.Chat.Title, share.Subject);
                return Task.CompletedTask;
            });
        }
        finally
        {
            MobilePlatformServices.TextShare = new DefaultTextShareService();
        }
    }

    [Fact]
    public Task ConversationRunsUnderFloatingChromeAndFrostsOnlyWhenContentPassesBeneath() =>
        RunChatAsync(LongTranscript(), async (_, view, window) =>
        {
            var shell = Single<StrataChatShell>(view, "ChatShell");
            var scroll = shell.TranscriptScrollViewer!;
            Assert.True(shell.ExtendsTranscriptBehindChrome);
            Assert.Equal(view.Bounds.Height, scroll.Bounds.Height, 1);

            // Resting at the latest message: it sits clear of the composer and no glass is drawn.
            var composer = Single<Border>(view, "ComposerSideInset");
            var composerTop = composer.TranslatePoint(default, view)!.Value.Y;
            var lastTurn = view.GetVisualDescendants().OfType<TranscriptTurnView>().Last(turn => turn.IsEffectivelyVisible);
            Assert.True(lastTurn.TranslatePoint(new Point(0, lastTurn.Bounds.Height), view)!.Value.Y <= composerTop + 1);
            Assert.Equal(0, Single<Panel>(view, "BottomEdgeMaterial").Opacity, 2);

            shell.PreserveViewport();
            shell.ScrollToVerticalOffset(Math.Max(0, shell.VerticalOffset - 400));
            await SettleAsync(window);
            Assert.Contains(":content-under-header", shell.Classes);
            Assert.Contains(":content-under-composer", shell.Classes);
            Assert.Equal(1, Single<Panel>(view, "TopEdgeMaterial").Opacity, 2);
            Assert.Equal(1, Single<Panel>(view, "BottomEdgeMaterial").Opacity, 2);
            Assert.False(Single<Panel>(view, "TopEdgeMaterial").IsHitTestVisible);
        });

    [Fact]
    public async Task OverlaidShellChromeKeepsTapsOffTheTranscriptBeneath()
    {
        // Skia hit-tests the hosts' rounded-corner backgrounds exactly; the headless drawing stub
        // approximates rounded paths and misses taps along their top edge.
        using var session = HeadlessUnitTestSession.StartNew(typeof(MobileMotionRenderApp), AvaloniaTestIsolationLevel.PerTest);
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch<bool>(async () =>
        {
            var transcript = new StackPanel();
            for (var index = 0; index < 30; index++)
                transcript.Children.Add(new Border { Height = 80, Background = Avalonia.Media.Brushes.Gray });
            var shell = new StrataChatShell
            {
                Header = new Border { Height = 64 },
                Composer = new Border { Height = 96 },
                Transcript = transcript,
                ExtendsTranscriptBehindChrome = true
            };
            var window = new Window { Width = 400, Height = 800, Content = shell };
            try
            {
                window.Show();
                await SettleAsync(window);
                shell.PreserveViewport();
                shell.ScrollToVerticalOffset(300);
                await SettleAsync(window);
                Assert.Contains(":content-under-header", shell.Classes);
                Assert.Contains(":content-under-composer", shell.Classes);

                // Neither host's content paints a background, so only the host itself can stop a tap.
                AssertTapsStayOn(Single<Border>(shell, "PART_HeaderChrome"), window);
                AssertTapsStayOn(Single<Border>(shell, "PART_ComposerHost"), window);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
        failure?.Throw();
    }

    [Theory]
    [InlineData("drawer", "DrawerScroll", "DrawerHeaderChrome")]
    [InlineData("drawer", "DrawerScroll", "DrawerFooterChrome")]
    [InlineData("search", "SearchScroll", "SearchHeaderChrome")]
    [InlineData("settings", "SettingsScroll", "SettingsHeaderChrome")]
    public async Task FloatingPageChromeKeepsTapsOffTheContentBeneath(string page, string scrollName, string chromeName)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            await using var model = new MobileShellViewModel(store: session.NewStore(), post: action => action());
            model.ChatList.Apply(LargeChatHistory.Create());
            model.SearchChatList.Apply(LargeChatHistory.Create());
            Control view = page switch
            {
                "drawer" => new MobileDrawerView { DataContext = model },
                "search" => new MobileSearchView { DataContext = model },
                _ => new MobileSettingsView { DataContext = model }
            };
            var window = new Window { Width = 412, Height = 560, Content = view };
            try
            {
                window.Show();
                await SettleAsync(window);
                Single<ScrollViewer>(view, scrollName).Offset = new Vector(0, 160);
                await SettleAsync(window);

                AssertTapsStayOn(Single<Panel>(view, chromeName), window);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    private static void AssertTapsStayOn(Control chrome, Window window)
    {
        Assert.True(chrome.Bounds.Width > 0 && chrome.Bounds.Height > 0, $"{chrome.Name} has no area.");
        // Dense enough to land in the margins and gaps between a bar's controls, where taps used to leak.
        var shape = new RoundedRect(new Rect(chrome.Bounds.Size), (chrome as Border)?.CornerRadius ?? default);
        for (var row = 0; row <= 6; row++)
        for (var column = 0; column <= 24; column++)
        {
            var local = new Point(2 + (chrome.Bounds.Width - 4) * column / 24, 2 + (chrome.Bounds.Height - 4) * row / 6);
            if (!shape.ContainsExclusive(local))
                continue;

            var hit = window.InputHitTest(chrome.TranslatePoint(local, window)!.Value) as Visual;
            Assert.True(hit is not null && (ReferenceEquals(hit, chrome) || chrome.IsVisualAncestorOf(hit)),
                $"A tap at {local} on {chrome.Name} reached {hit?.GetType().Name ?? "nothing"} '{(hit as Control)?.Name}'.");
        }
    }

    [Fact]
    public Task ChatControlsAreGlassWhileScrimsStayAtTheScreenEdges() =>
        RunChatAsync(LongTranscript(), async (_, view, window) =>
        {
            var shell = Single<StrataChatShell>(view, "ChatShell");
            shell.PreserveViewport();
            shell.ScrollToVerticalOffset(Math.Max(0, shell.VerticalOffset - 400));
            await SettleAsync(window);
            Assert.True(view.TryFindResource("Glass.ControlBlurRadius", out var resource));
            var controlBlur = Assert.IsType<double>(resource);

            // Each floating control frosts the conversation inside its own surface.
            foreach (var name in new[] { "MenuButton", "HeaderDetailsButton", "NewChatButton" })
            {
                var frost = Assert.Single(Single<Button>(view, name).GetVisualDescendants().OfType<StrataBackdropBlur>());
                Assert.True(frost.IsEffectivelyVisible);
                Assert.Equal(controlBlur, frost.BlurRadius);
            }
            var composer = Single<StrataChatComposer>(view, "Composer");
            var composerFrost = Single<StrataBackdropBlur>(composer, "PART_Backdrop");
            Assert.True(composerFrost.IsEffectivelyVisible);
            Assert.Equal(controlBlur, composerFrost.BlurRadius);
            Assert.Equal(controlBlur, Single<StrataBackdropBlur>(shell, "PART_ScrollToBottomBackdrop").BlurRadius);

            // The scrims frost the system bars, never a frost of what is behind a control.
            var menu = Single<Button>(view, "MenuButton");
            var topScrim = Single<Panel>(view, "TopEdgeMaterial");
            Assert.True(topScrim.TranslatePoint(new Point(0, topScrim.Bounds.Height), view)!.Value.Y
                        < menu.TranslatePoint(new Point(0, menu.Bounds.Height / 2), view)!.Value.Y);
            var bottomScrim = Single<Panel>(view, "BottomEdgeMaterial");
            Assert.True(bottomScrim.TranslatePoint(default, view)!.Value.Y
                        > composer.TranslatePoint(new Point(0, composer.Bounds.Height / 2), view)!.Value.Y);
            Assert.Equal(view.Bounds.Height,
                bottomScrim.TranslatePoint(new Point(0, bottomScrim.Bounds.Height), view)!.Value.Y, 1);
        });

    [Fact]
    public async Task DrawerScrimsStayClearOfTheFloatingControls()
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            await using var model = new MobileShellViewModel(store: session.NewStore(), post: action => action());
            model.SafeArea = new Thickness(0, 28, 0, 24);
            model.ChatList.Apply(LargeChatHistory.Create());
            var drawer = new MobileDrawerView { DataContext = model };
            var window = new Window { Width = 412, Height = 892, Content = drawer };
            try
            {
                window.Show();
                await SettleAsync(window);
                Single<ScrollViewer>(drawer, "DrawerScroll").Offset = new Vector(0, 160);
                await SettleAsync(window);

                var top = Single<Panel>(drawer, "DrawerTopEdgeMaterial");
                var bottom = Single<Panel>(drawer, "DrawerBottomEdgeMaterial");
                Assert.Equal(1, top.Opacity, 2);
                Assert.Equal(1, bottom.Opacity, 2);
                Assert.False(top.IsHitTestVisible);
                Assert.False(bottom.IsHitTestVisible);
                Assert.Equal(0, top.TranslatePoint(default, drawer)!.Value.Y, 1);
                Assert.Equal(drawer.Bounds.Height,
                    bottom.TranslatePoint(new Point(0, bottom.Bounds.Height), drawer)!.Value.Y, 1);

                foreach (var name in new[] { "DrawerSearchButton", "DrawerCloseButton", "DrawerProjectButton", "DrawerAccountButton" })
                {
                    var button = Single<Button>(drawer, name);
                    var center = button.TranslatePoint(
                        new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), drawer)!.Value;
                    Assert.True(center.Y > top.TranslatePoint(new Point(0, top.Bounds.Height), drawer)!.Value.Y);
                    Assert.True(center.Y < bottom.TranslatePoint(default, drawer)!.Value.Y);
                    Assert.True(Assert.Single(button.GetVisualDescendants().OfType<StrataBackdropBlur>()).IsEffectivelyVisible);
                }
                Assert.Empty(Single<Panel>(drawer, "DrawerBrandSurface")
                    .GetVisualDescendants().OfType<StrataBackdropBlur>());
                AssertTapsStayOn(Single<Panel>(drawer, "DrawerHeaderChrome"), window);
                AssertTapsStayOn(Single<Panel>(drawer, "DrawerFooterChrome"), window);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    [Fact]
    public Task OverlaidShellKeepsAFollowingTailPinnedAboveAGrowingComposer() =>
        RunShellAsync(async (shell, _, composer, window) =>
        {
            shell.RequestInitialBottom();
            await SettleAsync(window);
            Assert.True(shell.CurrentDistanceFromBottom <= 1);

            // The keyboard or a multi-line draft grows the floating composer.
            composer.Height = 320;
            await SettleAsync(window);
            Assert.True(shell.IsFollowingTail);
            Assert.True(shell.CurrentDistanceFromBottom <= 1, $"distance={shell.CurrentDistanceFromBottom}");
            Assert.DoesNotContain(":content-under-composer", shell.Classes);
        });

    [Fact]
    public Task OverlaidShellKeepsAReadersPositionWhenTheHeaderGrows() =>
        RunShellAsync(async (shell, header, _, window) =>
        {
            shell.PreserveViewport();
            shell.ScrollToVerticalOffset(300);
            await SettleAsync(window);
            var before = shell.VerticalOffset;
            Assert.True(before > 0);
            Assert.Contains(":content-under-header", shell.Classes);
            Assert.Contains(":content-under-composer", shell.Classes);

            // A status banner appears in the floating header.
            header.Height += 40;
            await SettleAsync(window);
            Assert.Equal(before + 40, shell.VerticalOffset, 1);
        });

    [Fact]
    public async Task ScrollEdgeChromeKeepsListsClearAtRestAndMarksChromeWhileContentIsBehind()
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            var header = new Panel { Height = 72, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            var footer = new Panel { Height = 64, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom };
            var list = new StackPanel();
            for (var index = 0; index < 40; index++)
                list.Children.Add(new Border { Height = 56 });
            var inset = new Border { Child = list };
            var scroll = new ScrollViewer { Content = inset };
            ScrollEdgeChrome.SetHeader(scroll, header);
            ScrollEdgeChrome.SetFooter(scroll, footer);
            var window = new Window { Width = 360, Height = 640, Content = new Panel { Children = { scroll, header, footer } } };
            try
            {
                window.Show();
                await SettleAsync(window);
                Assert.Equal(new Thickness(0, 72, 0, 64), inset.Margin);
                Assert.DoesNotContain(ScrollEdgeChrome.ContentUnderClass, header.Classes);
                Assert.Contains(ScrollEdgeChrome.ContentUnderClass, footer.Classes);

                header.Height = 100;
                await SettleAsync(window);
                Assert.Equal(new Thickness(0, 100, 0, 64), inset.Margin);

                scroll.Offset = new Vector(0, scroll.Extent.Height);
                await SettleAsync(window);
                Assert.Contains(ScrollEdgeChrome.ContentUnderClass, header.Classes);
                Assert.DoesNotContain(ScrollEdgeChrome.ContentUnderClass, footer.Classes);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    private static RemoteTranscriptTurn Turn(string? finalAnswerId, bool streaming = false) => new()
    {
        Id = "turn",
        FinalAnswerId = finalAnswerId,
        Items =
        [
            new() { Id = "user", Kind = RemoteProtocol.ItemKinds.User, Text = "Help me out" },
            new() { Id = "answer", Kind = RemoteProtocol.ItemKinds.Assistant, Text = FinalAnswer, IsStreaming = streaming }
        ]
    };

    private static RemoteTranscriptTurn[] LongTranscript() =>
    [
        .. Enumerable.Range(0, 16).Select(index => new RemoteTranscriptTurn
        {
            Id = $"turn-{index}",
            FinalAnswerId = $"answer-{index}",
            Items =
            [
                new() { Id = $"user-{index}", Kind = RemoteProtocol.ItemKinds.User, Text = "A question" },
                new() { Id = $"answer-{index}", Kind = RemoteProtocol.ItemKinds.Assistant,
                    Text = "A readable answer on the open canvas.\n\nMore detail that passes under the chrome." }
            ]
        })
    ];

    private static Task RunChatAsync(RemoteTranscriptTurn turn, Func<MobileShellViewModel, ChatDetailView, Window, Task> test) =>
        RunChatAsync([turn], test);

    private static async Task RunShellAsync(Func<StrataChatShell, Border, Border, Window, Task> test)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            var header = new Border { Height = 64 };
            var composer = new Border { Height = 96 };
            var transcript = new StackPanel();
            for (var index = 0; index < 30; index++)
                transcript.Children.Add(new Border { Height = 80 });
            var shell = new StrataChatShell
            {
                Header = header,
                Composer = composer,
                Transcript = transcript,
                ExtendsTranscriptBehindChrome = true
            };
            var window = new Window { Width = 400, Height = 800, Content = shell };
            try
            {
                window.Show();
                await SettleAsync(window);
                await test(shell, header, composer, window);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    private static async Task RunChatAsync(
        RemoteTranscriptTurn[] turns, Func<MobileShellViewModel, ChatDetailView, Window, Task> test)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            await using var model = new MobileShellViewModel(store: session.NewStore(), post: action => action());
            model.IsPaired = model.IsConnected = model.IsHostReady = true;
            var id = Guid.NewGuid();
            model.Chat.Reset(id, "Glass canvas");
            model.Chat.ApplyTranscript(new RemoteTranscript { ChatId = id, Revision = 1, Turns = [.. turns] });
            var view = new ChatDetailView { DataContext = model };
            var window = new Window { Width = 412, Height = 892, Content = view };
            try
            {
                window.Show();
                await SettleAsync(window);
                await test(model, view, window);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    private static IEnumerable<T> Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Where(control => control.Name == name);

    private static T Single<T>(Control root, string name) where T : Control => Assert.Single(Named<T>(root, name));

    private static async Task SettleAsync(Window window)
    {
        // Advance the headless render clock as well, so style transitions (glass fades) complete.
        for (var pass = 0; pass < 20; pass++)
        {
            window.UpdateLayout();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class RecordingShareService : ITextShareService
    {
        public string? Text { get; private set; }
        public string? Subject { get; private set; }
        public bool IsAvailable => true;

        public bool TryShare(string text, string? subject)
        {
            Text = text;
            Subject = subject;
            return true;
        }
    }
}
