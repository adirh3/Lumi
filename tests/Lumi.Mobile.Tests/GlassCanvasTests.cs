using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
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
            // Without share, the footer follows the copy glyph by the same short step.
            var copyGlyph = copy.GetVisualDescendants().OfType<Viewbox>().First(box => box.IsEffectivelyVisible);
            var glyphRight = copyGlyph.TranslatePoint(new Point(copyGlyph.Bounds.Width, 0), view)!.Value.X;
            var footer = Assert.Single(Named<TextBlock>(view, "AnswerFooter"), item => item.IsEffectivelyVisible);
            Assert.InRange(footer.TranslatePoint(default, view)!.Value.X - glyphRight, 10, 14);

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
                // Two full targets, yet the glyphs sit a short step apart, as the desktop's actions do, with
                // the copy glyph on the answer's text edge and the footer a short step after share.
                var copy = Assert.Single(Named<Button>(view, "CopyAnswerButton"), item => item.IsEffectivelyVisible);
                Assert.True(button.Bounds.Width >= 48 && button.Bounds.Height >= 48);
                Assert.True(copy.Bounds.Width >= 48 && copy.Bounds.Height >= 48);
                Rect Glyph(Button host)
                {
                    var glyph = host.GetVisualDescendants().OfType<Viewbox>().First(box => box.IsEffectivelyVisible);
                    return new Rect(glyph.TranslatePoint(default, view)!.Value, glyph.Bounds.Size);
                }
                var copyGlyph = Glyph(copy);
                var shareGlyph = Glyph(button);
                Assert.InRange(shareGlyph.Center.X - copyGlyph.Center.X, 29, 31);
                Assert.InRange(shareGlyph.Left - copyGlyph.Right, 9, 13);
                var text = view.GetVisualDescendants().OfType<StrataMarkdown>()
                    .First(markdown => markdown.DataContext == copy.DataContext);
                Assert.InRange(copyGlyph.Left - text.TranslatePoint(default, view)!.Value.X, -1.5, 1.5);
                var footer = Assert.Single(Named<TextBlock>(view, "AnswerFooter"), item => item.IsEffectivelyVisible);
                Assert.InRange(footer.TranslatePoint(default, view)!.Value.X - shareGlyph.Right, 10, 14);
                // The targets meet but never overlap, so every tap lands on exactly one of them.
                Assert.InRange(button.TranslatePoint(default, view)!.Value.X - copy.TranslatePoint(new Point(copy.Bounds.Width, 0), view)!.Value.X, -0.5, 0.5);
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
    public Task ChatControlsAreGlassOverAVeiledHeaderAndAComposerScrim() =>
        RunChatAsync(LongTranscript(), async (_, view, window) =>
        {
            var shell = Single<StrataChatShell>(view, "ChatShell");
            shell.PreserveViewport();
            shell.ScrollToVerticalOffset(Math.Max(0, shell.VerticalOffset - 400));
            await SettleAsync(window);
            Assert.True(view.TryFindResource("Glass.FloatingBlurRadius", out var resource));
            var floatingBlur = Assert.IsType<double>(resource);

            // Each floating control frosts the conversation inside its own surface, as the composer does.
            foreach (var name in new[] { "MenuButton", "HeaderDetailsButton", "NewChatButton" })
            {
                var frost = Assert.Single(Single<Button>(view, name).GetVisualDescendants().OfType<StrataBackdropBlur>());
                Assert.True(frost.IsEffectivelyVisible);
                Assert.Equal(floatingBlur, frost.BlurRadius);
            }
            var composer = Single<StrataChatComposer>(view, "Composer");
            var composerFrost = Single<StrataBackdropBlur>(composer, "PART_Backdrop");
            Assert.True(composerFrost.IsEffectivelyVisible);
            Assert.Equal(floatingBlur, composerFrost.BlurRadius);
            // The one frost is light, so the presence settled behind the composer still glows through.
            Assert.InRange(floatingBlur, 6, 10);
            Assert.Equal(floatingBlur, Single<StrataBackdropBlur>(shell, "PART_ScrollToBottomBackdrop").BlurRadius);

            // As on the desktop, history passing under the top bar dissolves into a veil that covers
            // the whole bar and fades out below it, so no sharp text shows between its controls.
            var menu = Single<Button>(view, "MenuButton");
            var topVeil = Single<Panel>(view, "TopEdgeMaterial");
            Assert.Equal(0, topVeil.TranslatePoint(default, view)!.Value.Y, 1);
            Assert.True(topVeil.TranslatePoint(new Point(0, topVeil.Bounds.Height), view)!.Value.Y
                        >= menu.TranslatePoint(new Point(0, menu.Bounds.Height), view)!.Value.Y + 20);
            Assert.Equal(view.Bounds.Width, topVeil.Bounds.Width, 1);
            // The composer spans the conversation, so beneath it only the gesture area is scrimmed.
            var bottomScrim = Single<Panel>(view, "BottomEdgeMaterial");
            Assert.True(bottomScrim.TranslatePoint(default, view)!.Value.Y
                        > composer.TranslatePoint(new Point(0, composer.Bounds.Height / 2), view)!.Value.Y);
            Assert.Equal(view.Bounds.Height,
                bottomScrim.TranslatePoint(new Point(0, bottomScrim.Bounds.Height), view)!.Value.Y, 1);
        });

    [Fact]
    public async Task DrawerHeaderVeilsItsControlsWhileThePlacesPillIsGlassOfItsOwn()
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
                Assert.Equal(1, top.Opacity, 2);
                Assert.False(top.IsHitTestVisible);
                Assert.Equal(0, top.TranslatePoint(default, drawer)!.Value.Y, 1);

                // As on the desktop's sidebar, rows passing under the header soften under a veil that covers
                // its controls and fades out beyond them. The controls are the title bar's glass, each
                // frosting what passes beneath it, as the chat's bar does.
                var topEnd = top.TranslatePoint(new Point(0, top.Bounds.Height), drawer)!.Value.Y;
                Assert.True(drawer.TryFindResource("Glass.FloatingBlurRadius", out var floatingBlur));
                foreach (var name in new[] { "DrawerSearchButton", "DrawerNewChatButton", "DrawerProjectButton" })
                {
                    var button = Single<Button>(drawer, name);
                    Assert.True(button.TranslatePoint(new Point(0, button.Bounds.Height), drawer)!.Value.Y + 20 <= topEnd,
                        $"{name} must sit inside the header's veil");
                    var frost = Assert.Single(button.GetVisualDescendants().OfType<StrataBackdropBlur>());
                    Assert.Equal(Assert.IsType<double>(floatingBlur), frost.BlurRadius);
                    Assert.All(Paint(button.Background), color => Assert.InRange(color.A, (byte)1, (byte)254));
                }
                // The places pill is glass of its own, as the title bar's controls are: one frost beneath its
                // surface with the same blur and outline, taking in what lies just around the pill, and no band
                // frosting the rows beside it. The bar does not clip, so the pill's shadow falls whole.
                var places = Single<Panel>(drawer, "PlacesPill");
                var pillFrost = Single<StrataBackdropBlur>(drawer, "PlacesFrost");
                Assert.Same(places, pillFrost.GetVisualParent());
                Assert.Equal(Assert.IsType<double>(floatingBlur), pillFrost.BlurRadius);
                Assert.True(pillFrost.SamplesBeyondEdges);
                Assert.Equal(Single<Border>(drawer, "PlacesSurface").CornerRadius, pillFrost.CornerRadius);
                Assert.Equal(places.Bounds.Size, pillFrost.Bounds.Size);
                Assert.Equal(new[] { pillFrost }, Single<Panel>(drawer, "DrawerFooterChrome").GetVisualDescendants().OfType<StrataBackdropBlur>());
                Assert.False(Single<PlacesBarView>(drawer, "DrawerPlaces").ClipToBounds);
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

    /// <summary>
    /// Everything that floats over moving content is one material, the composer's: the same frost, tint, rim
    /// and quiet elevation, on the chat, the chat list and search alike, whichever notices and pills are up.
    /// Each frost has exactly its surface's geometry, so none frosts beyond its glass.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EveryFloatingSurfaceWearsTheComposersGlass(bool darkTheme)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            await using var model = new MobileShellViewModel(store: session.NewStore(), post: action => action());
            model.IsPaired = model.IsConnected = model.IsHostReady = true;
            model.ChatList.Apply(LargeChatHistory.Create());
            var id = Guid.NewGuid();
            model.Chat.Reset(id, "Glass canvas");
            model.Chat.ApplyTranscript(new RemoteTranscript { ChatId = id, Revision = 1, Turns = [.. LongTranscript()] });
            Window? window = null;
            async Task<Control> Show(Control content)
            {
                window?.Close();
                window = new Window
                {
                    Width = 412, Height = 892, Content = content,
                    RequestedThemeVariant = darkTheme ? ThemeVariant.Dark : ThemeVariant.Light
                };
                window.Show();
                await SettleAsync(window);
                return content;
            }

            try
            {
                // The chat: the composer, the title bar, jump-to-latest and the background-work pill.
                model.Chat.ApplyStatus(new RemoteChatStatus { ChatId = id, IsSessionActive = true });
                var chat = await Show(new ChatDetailView { DataContext = model });
                var composer = Single<StrataChatComposer>(chat, "Composer");
                AssertFloatingGlass(chat, Single<Border>(composer, "PART_Root"), Single<StrataBackdropBlur>(composer, "PART_Backdrop"));
                foreach (var name in new[] { "MenuButton", "HeaderDetailsButton", "NewChatButton" })
                    AssertGlassButton(chat, Single<Button>(chat, name));
                var shell = Single<StrataChatShell>(chat, "ChatShell");
                AssertFloatingGlass(chat, Single<Border>(shell, "PART_ScrollToBottomChrome"),
                    Single<StrataBackdropBlur>(shell, "PART_ScrollToBottomBackdrop"));
                Assert.True(Single<Border>(chat, "BackgroundSessionStatus").IsEffectivelyVisible);
                AssertFloatingGlass(chat, Single<Border>(chat, "BackgroundSessionStatus"),
                    FrostOf(Single<Panel>(chat, "BackgroundSessionStatusHost")));

                // While Lumi works, what it is doing floats above the composer in the same glass.
                model.Chat.IsBusy = true;
                await SettleAsync(window!);
                var typing = Single<StrataTypingIndicator>(chat, "ChatTyping");
                Assert.True(typing.IsEffectivelyVisible);
                AssertFloatingGlass(chat, Single<Border>(typing, "PART_Surface"), Single<StrataBackdropBlur>(typing, "PART_Backdrop"));

                // Without the PC, the notice in the bar is glass too.
                model.Chat.IsBusy = false;
                model.IsConnected = false;
                await SettleAsync(window!);
                Assert.True(Single<Border>(chat, "ConnectionBanner").IsEffectivelyVisible);
                AssertFloatingGlass(chat, Single<Border>(chat, "ConnectionBanner"), FrostOf(Single<Panel>(chat, "ConnectionBannerHost")));

                // The chat list: its title bar's buttons, the project lens, the places and its notice.
                var drawer = await Show(new MobileDrawerView { DataContext = model });
                foreach (var name in new[] { "DrawerSearchButton", "DrawerNewChatButton", "DrawerProjectButton" })
                    AssertGlassButton(drawer, Single<Button>(drawer, name));
                AssertFloatingGlass(drawer, Single<Border>(drawer, "PlacesSurface"), Single<StrataBackdropBlur>(drawer, "PlacesFrost"));
                Assert.True(Single<Border>(drawer, "DrawerConnectionBanner").IsEffectivelyVisible);
                AssertFloatingGlass(drawer, Single<Border>(drawer, "DrawerConnectionBanner"),
                    FrostOf(Single<Panel>(drawer, "DrawerConnectionBannerHost")));

                // Search: its field floats in the header beside the back button, drawn as tall as the button's
                // glass inside a full touch target.
                var search = await Show(new MobileSearchView { DataContext = model });
                var back = Single<Button>(search, "SearchBackButton");
                AssertGlassButton(search, back);
                var field = Single<TextBox>(search, "SearchField");
                var fieldSurface = Single<Border>(field, "PART_Border");
                AssertFloatingGlass(search, fieldSurface, FrostOf(Single<Panel>(search, "SearchFieldHost")),
                    field.IsFocused ? "Brush.FloatingGlassRimStrong" : "Brush.FloatingGlassRim");
                Assert.True(field.Bounds.Height >= 48, $"the search field is a {field.Bounds.Height} dp target");
                Assert.Equal(Single<Border>(back, "PART_Surface").Bounds.Height, fieldSurface.Bounds.Height, 1);
                // The whole target takes a tap, not only the glass: a touch just inside its top edge, above the
                // drawn pill, still reaches the field.
                var edge = field.TranslatePoint(new Point(field.Bounds.Width / 2, 1), window!)!.Value;
                var hit = window!.InputHitTest(edge) as Visual;
                Assert.True(hit is not null && (ReferenceEquals(hit, field) || field.IsVisualAncestorOf(hit)),
                    $"A tap just inside the search field's edge reached {hit?.GetType().Name ?? "nothing"} '{(hit as Control)?.Name}'.");
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window?.Close();
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

    private static StrataBackdropBlur FrostOf(Panel host) => Assert.Single(host.Children.OfType<StrataBackdropBlur>());

    private static void AssertGlassButton(Control root, Button button) =>
        AssertFloatingGlass(root, Single<Border>(button, "PART_Surface"), Single<StrataBackdropBlur>(button, "PART_Backdrop"));

    /// <summary>
    /// The surface wears the floating glass (tint, rim and elevation) over a frost with the floating blur and
    /// saturation, in exactly the surface's outline.
    /// </summary>
    private static void AssertFloatingGlass(Control root, Border surface, StrataBackdropBlur frost,
        string rimKey = "Brush.FloatingGlassRim")
    {
        object Resource(string key)
        {
            Assert.True(root.TryFindResource(key, root.ActualThemeVariant, out var value), key);
            return value!;
        }

        var name = surface.TemplatedParent is Control owner
            ? $"{owner.Name ?? owner.GetType().Name}'s {surface.Name}"
            : surface.Name;
        Assert.True(Paint((IBrush)Resource("Brush.FloatingGlass")).SequenceEqual(Paint(surface.Background)),
            $"{name} must be tinted as the floating glass");
        Assert.True(Paint((IBrush)Resource(rimKey)).SequenceEqual(Paint(surface.BorderBrush)),
            $"{name} must have the floating glass's rim");
        Assert.Equal((BoxShadows)Resource("Shadow.FloatingGlass"), surface.BoxShadow);
        Assert.True(frost.IsVisible, $"{name} must frost what passes beneath it");
        Assert.Equal((double)Resource("Glass.FloatingBlurRadius"), frost.BlurRadius);
        Assert.Equal((double)Resource("Glass.FloatingSaturation"), frost.Saturation);
        Assert.Equal(surface.CornerRadius, frost.CornerRadius);
        var surfaceAt = surface.TranslatePoint(default, root)!.Value;
        var frostAt = frost.TranslatePoint(default, root)!.Value;
        Assert.True(surface.Bounds.Width > 0 && surface.Bounds.Height > 0, $"{name} must be laid out");
        Assert.True(Math.Abs(surfaceAt.X - frostAt.X) < 0.5 && Math.Abs(surfaceAt.Y - frostAt.Y) < 0.5
                    && Math.Abs(surface.Bounds.Width - frost.Bounds.Width) < 0.5
                    && Math.Abs(surface.Bounds.Height - frost.Bounds.Height) < 0.5,
            $"{name}'s frost {new Rect(frostAt, frost.Bounds.Size)} must match its glass {new Rect(surfaceAt, surface.Bounds.Size)}");
    }

    private static Color[] Paint(IBrush? brush) => brush switch
    {
        ISolidColorBrush solid => [solid.Color],
        IGradientBrush gradient => [.. gradient.GradientStops.Select(stop => stop.Color)],
        _ => throw new Xunit.Sdk.XunitException($"Expected a solid or gradient brush, got {brush?.GetType().Name ?? "none"}.")
    };

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
