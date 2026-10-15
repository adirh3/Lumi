using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using SkiaSharp;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Mobile.Tests;

[Collection("Headless mobile UI")]
public sealed class BackdropBlurRenderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PopupGlassFrostsOnlyBeneathTheSheetWhileThePageStaysSharp(bool darkTheme)
    {
        using var session = HeadlessUnitTestSession.StartNew(
            typeof(MobileMotionRenderApp), AvaloniaTestIsolationLevel.PerTest);
        await session.Dispatch<bool>(async () =>
        {
            // A hard light/dark edge runs down the whole page, beneath the sheet and above it.
            var light = new Border
            {
                Width = 120, Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left
            };
            var content = new Border { Height = 160 };
            var sheet = new StrataBottomSheet
            {
                Title = "Projects",
                Classes = { "safe-area" },
                SheetMargin = new Thickness(12),
                Content = content
            };
            var window = new Window
            {
                Width = 240, Height = 420, Background = Brushes.Black,
                RequestedThemeVariant = darkTheme ? ThemeVariant.Dark : ThemeVariant.Light,
                Content = new Panel { Children = { light, sheet } }
            };
            try
            {
                window.Show();
                await PumpAsync(TimeSpan.FromMilliseconds(80));
                var pageRow = 40;
                Assert.True(Step(Row(pageRow)) > 240, "The page edge starts crisp.");

                sheet.IsOpen = true;
                await PumpAsync(TimeSpan.FromMilliseconds(320));
                var sheetRow = (int)content.TranslatePoint(new Point(0, content.Bounds.Height / 2), window)!.Value.Y;
                Assert.InRange(sheetRow, pageRow + 120, 400);

                var page = Row(pageRow);
                Assert.True(Step(page) > 100, $"Outside the popup the page is only dimmed, never blurred: step {Step(page)}.");

                var glass = Row(sheetRow);
                Assert.True(Step(glass) < 10, $"Beneath the popup the edge must be frosted: step {Step(glass)}.");
                Assert.True(glass[40] - glass[200] > 12,
                    $"The popup is glass, not an opaque card: {glass[40]} over light, {glass[200]} over dark.");

                sheet.Close();
                await PumpAsync(TimeSpan.FromMilliseconds(240));
                Assert.False(sheet.IsPresented);
                Assert.True(Step(Row(sheetRow)) > 240, "Dismissal restores the crisp page.");
            }
            finally
            {
                window.Close();
            }
            return true;

            byte[] Row(int y)
            {
                using var frame = window.CaptureRenderedFrame()!;
                var pixels = Luma(frame);
                return Enumerable.Range(0, 240).Select(x => pixels[x, y]).ToArray();
            }

            static int Step(byte[] row)
            {
                var steepest = 0;
                for (var x = 24; x < row.Length - 25; x++)
                    steepest = Math.Max(steepest, Math.Abs(row[x + 1] - row[x]));
                return steepest;
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task FrostBlursOnlyTheContentBeneathItsBounds() =>
        RenderAsync(StrataBackdropFade.None, pixels =>
        {
            // Outside the glass the stripes stay crisp: pure black and pure white.
            var (outsideMin, outsideMax) = Range(pixels, x: 150, y: 20, width: 40, height: 20);
            Assert.True(outsideMin < 10 && outsideMax > 245, $"Outside range was {outsideMin}..{outsideMax}.");

            // Under the glass, the same stripes average into mid tones.
            var (insideMin, insideMax) = Range(pixels, x: 40, y: 40, width: 60, height: 20);
            Assert.True(insideMin > 60 && insideMax < 195, $"Frosted range was {insideMin}..{insideMax}.");
        });

    [Fact]
    public Task FadedEdgeReturnsToTheUnfrostedContent() =>
        RenderAsync(StrataBackdropFade.Bottom, pixels =>
        {
            var (topMin, topMax) = Range(pixels, x: 40, y: 24, width: 60, height: 8);
            var (bottomMin, bottomMax) = Range(pixels, x: 40, y: 96, width: 60, height: 3);
            Assert.True(topMax - topMin < 140, $"Solid frost range was {topMin}..{topMax}.");
            Assert.True(bottomMax - bottomMin > 200, $"Faded edge range was {bottomMin}..{bottomMax}.");
        });

    [Fact]
    public Task FrostHidesTheContentBeneathOverATranslucentWindow() =>
        // Over Mica or acrylic the window itself is translucent, and so is the frost of it: the sharp
        // stripes beneath must not show through the glass.
        RenderAsync(StrataBackdropFade.None, pixels =>
        {
            var (outsideMin, outsideMax) = Range(pixels, x: 150, y: 20, width: 40, height: 20);
            Assert.True(outsideMin < 10 && outsideMax > 245, $"Outside range was {outsideMin}..{outsideMax}.");

            var (insideMin, insideMax) = Range(pixels, x: 40, y: 40, width: 60, height: 20);
            Assert.True(insideMax - insideMin < 12, $"Frosted range was {insideMin}..{insideMax}.");
        }, translucentWindow: true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SinglePresenceLeavesTheComposerWhileWorking(bool darkTheme)
    {
        using var session = HeadlessUnitTestSession.StartNew(
            typeof(MobileMotionRenderApp), AvaloniaTestIsolationLevel.PerTest);
        var settingsPath = Path.Combine(Path.GetTempPath(), "Lumi.Mobile.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            await session.Dispatch<bool>(async () =>
            {
                await using var model = new MobileShellViewModel(
                    store: new MobileSettingsStore(settingsPath), post: action => action());
                model.IsPaired = model.IsConnected = model.IsHostReady = true;
                var id = Guid.NewGuid();
                model.Chat.Reset(id, "Single moving presence");
                model.Chat.ApplyTranscript(new RemoteTranscript
                {
                    ChatId = id, Revision = 1, IsLatestWindow = true,
                    Turns = [new() { Id = "turn", Items = [new()
                    {
                        Id = "answer", Kind = RemoteProtocol.ItemKinds.Assistant, Text = "Ready."
                    }] }]
                });
                var view = new MobileShellView { DataContext = model };
                var window = new Window
                {
                    Width = 412, Height = 892, Content = view,
                    RequestedThemeVariant = darkTheme ? ThemeVariant.Dark : ThemeVariant.Light
                };
                try
                {
                    window.Show();
                    await PumpAsync(TimeSpan.FromMilliseconds(2000));
                    var presence = view.GetVisualDescendants().OfType<StrataPresence>().Single();
                    Assert.Equal(PresenceState.Idle, presence.State);
                    Assert.False(presence.Halo);
                    var composer = view.GetVisualDescendants().OfType<StrataChatComposer>()
                        .Single(control => control.Name == "Composer");
                    var surface = composer.GetVisualDescendants().OfType<Border>()
                        .Single(control => control.Name == "PART_Root");
                    var lit = Sample();
                    presence.Opacity = 0;
                    await PumpAsync(TimeSpan.FromMilliseconds(500));
                    var neutral = Sample();
                    var difference = Math.Max(Math.Abs(lit.Red - neutral.Red),
                        Math.Max(Math.Abs(lit.Green - neutral.Green), Math.Abs(lit.Blue - neutral.Blue)));
                    Assert.True(difference >= (darkTheme ? 15 : 5),
                        $"The resting field should remain visible through the glass: lit={lit}, neutral={neutral}.");
                    presence.Opacity = 1;
                    model.Chat.ApplyStatus(new RemoteChatStatus { ChatId = id, IsBusy = true, IsStreaming = true });
                    await PumpAsync(TimeSpan.FromMilliseconds(2400));
                    Assert.True(presence.FocusPoint.Y < 0.60);
                    var working = Sample();
                    Assert.True(lit.Blue - lit.Red > working.Blue - working.Red + (darkTheme ? 8 : 3),
                        $"A fixed composer pool remains while working: idle={lit}, working={working}.");
                    Assert.Equal(0, Assert.IsType<Border>(Assert.IsType<Border>(presence.Children[^1]).Child).Opacity);

                    SKColor Sample()
                    {
                        using var frame = window.CaptureRenderedFrame()!;
                        using var encoded = new MemoryStream();
                        frame.Save(encoded, new PngBitmapEncoderOptions());
                        using var pixels = SKBitmap.Decode(encoded.ToArray());
                        var point = surface.TranslatePoint(
                            new Point(surface.Bounds.Width * 0.67, surface.Bounds.Height * 0.50), window)!.Value;
                        return pixels.GetPixel((int)point.X, (int)point.Y);
                    }
                }
                finally
                {
                    window.Close();
                }
                return true;
            }, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(settingsPath))
                Directory.Delete(settingsPath, recursive: true);
        }
    }

    [Fact]
    public async Task FloatingControlShowsTheContentBeneathAsFrostedGlass()
    {
        using var session = HeadlessUnitTestSession.StartNew(
            typeof(MobileMotionRenderApp), AvaloniaTestIsolationLevel.PerTest);
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(() =>
        {
            // Content with a hard light/dark edge passes under a floating control.
            var light = new Border { Width = 160, Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
            var button = new Button
            {
                Classes = { "floating-action" },
                Width = 120, Height = 48, Margin = new Thickness(100, 20, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
            };
            var window = new Window
            {
                Width = 320, Height = 100, Background = Brushes.Black,
                Content = new Panel { Children = { light, button } }
            };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame()!;
                var pixels = Luma(frame);
                Assert.True(pixels[159, 85] - pixels[160, 85] > 200, "The edge beside the control should stay crisp.");

                // Across the control's middle, clear of its rim.
                byte min = byte.MaxValue, max = byte.MinValue;
                var steepest = 0;
                for (var x = 108; x < 212; x++)
                {
                    min = Math.Min(min, pixels[x, 44]);
                    max = Math.Max(max, pixels[x, 44]);
                    steepest = Math.Max(steepest, Math.Abs(pixels[x + 1, 44] - pixels[x, 44]));
                }

                Assert.True(max - min > 60, $"The content should show through the control: {min}..{max}.");
                Assert.True(steepest < 40, $"The content should be frosted under the control: step {steepest}.");
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlassButtonKeepsItsTintAndRimOutOfTheBackdrop(bool pressed)
    {
        using var session = HeadlessUnitTestSession.StartNew(
            typeof(MobileMotionRenderApp), AvaloniaTestIsolationLevel.PerTest);
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(() =>
        {
            var button = new Button
            {
                Classes = { "floating-action" },
                Width = 120, Height = 48, Margin = new Thickness(20),
                Background = new SolidColorBrush(Color.FromArgb(128, 32, 128, 128)),
                BorderBrush = Brushes.Red,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
            };
            var window = new Window
            {
                Width = 180, Height = 100, Background = Brushes.Black, Content = button
            };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var surface = button.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Name == "PART_Surface");
                if (pressed)
                    surface.RenderTransform = new ScaleTransform(0.98, 0.98);
                using var frame = window.CaptureRenderedFrame()!;
                using var encoded = new MemoryStream();
                frame.Save(encoded, new PngBitmapEncoderOptions());
                using var pixels = SKBitmap.Decode(encoded.ToArray());
                var tint = pixels.GetPixel(80, 44);
                // Black stays neutral when frosted. The foreground tint must not be saturated by
                // the backdrop filter, and the border must never bleed into the glass.
                Assert.InRange(tint.Red, 14, 18);
                Assert.InRange(tint.Green, 62, 66);
                Assert.InRange(tint.Blue, 62, 66);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DrawerControlsShowTheScrollingListRatherThanAFrostedBar(bool darkTheme)
    {
        using var session = HeadlessUnitTestSession.StartNew(
            typeof(MobileMotionRenderApp), AvaloniaTestIsolationLevel.PerTest);
        var settingsPath = Path.Combine(Path.GetTempPath(), "Lumi.Mobile.Tests", Guid.NewGuid().ToString("N"));
        ExceptionDispatchInfo? failure = null;
        try
        {
            await session.Dispatch<bool>(async () =>
            {
                await using var model = new MobileShellViewModel(
                    store: new MobileSettingsStore(settingsPath), post: action => action());
                model.SafeArea = new Thickness(0, 28, 0, 24);
                var drawer = new MobileDrawerView { DataContext = model };
                var window = new Window
                {
                    Width = 412, Height = 892, Content = drawer,
                    RequestedThemeVariant = darkTheme ? ThemeVariant.Dark : ThemeVariant.Light
                };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    var content = new Border { Height = 2000, Background = Brushes.Black };
                    var scroll = drawer.FindControl<ScrollViewer>("DrawerScroll")!;
                    scroll.Content = new Border { Child = content };
                    window.UpdateLayout();
                    scroll.Offset = new Vector(0, 160);
                    await PumpAsync(TimeSpan.FromMilliseconds(400));

                    var controls = new[] { "DrawerSearchButton", "DrawerCloseButton", "DrawerProjectButton", "DrawerAccountButton" };
                    var samples = controls.Select(name =>
                    {
                        var button = drawer.FindControl<Button>(name)!;
                        var surface = button.GetVisualDescendants().OfType<Border>()
                            .Single(border => border.Name == "PART_Surface");
                        // Inside the tint, below the rim and above the icon/text.
                        return surface.TranslatePoint(new Point(surface.Bounds.Width / 2, 6), window)!.Value;
                    }).ToArray();
                    byte[,] dark;
                    using (var frame = window.CaptureRenderedFrame()!)
                        dark = Luma(frame);

                    content.Background = Brushes.White;
                    await PumpAsync(TimeSpan.FromMilliseconds(400));
                    using var changed = window.CaptureRenderedFrame()!;
                    var light = Luma(changed);
                    for (var index = 0; index < samples.Length; index++)
                    {
                        var x = (int)samples[index].X;
                        var y = (int)samples[index].Y;
                        var contrast = light[x, y] - dark[x, y];
                        Assert.True(contrast > 75,
                            $"{controls[index]} hides the scrolling backdrop: luminance change {contrast}.");
                    }
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
        finally
        {
            if (Directory.Exists(settingsPath))
                Directory.Delete(settingsPath, recursive: true);
        }
    }

    [Fact]
    public async Task PartialRedrawPatchesTheFrostThenHealsOverlappingGlassOnce()
    {
        using var session = HeadlessUnitTestSession.StartNew(
            typeof(MobileMotionRenderApp), AvaloniaTestIsolationLevel.PerTest);
        ExceptionDispatchInfo? failure = null;
        // Dispatch<bool> awaits the whole body; an async lambda passed as a plain Func<Task> would only
        // be awaited up to its first await.
        await session.Dispatch<bool>(async () =>
        {
            var stripes = new StackPanel { Orientation = Orientation.Horizontal };
            for (var index = 0; index < 60; index++)
                stripes.Children.Add(new Border { Width = 4, Background = index % 2 == 0 ? Brushes.White : Brushes.Black });

            var spot = new Border
            {
                Width = 16, Height = 16, Background = Brushes.Black, Margin = new Thickness(44, 44, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
            };
            // Overlapping without either containing the other, like the chat's bottom band and its
            // scroll-to-bottom button: a repaint of either redraws only part of the other.
            var first = new CountingBackdropBlur
            {
                BlurRadius = 8, Width = 100, Height = 80, Margin = new Thickness(20, 20, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
            };
            var second = new CountingBackdropBlur
            {
                BlurRadius = 8, Width = 100, Height = 60, Margin = new Thickness(90, 70, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
            };
            var window = new Window
            {
                Width = 240, Height = 140, Background = Brushes.Black,
                Content = new Panel { Children = { stripes, spot, first, second } }
            };
            try
            {
                window.Show();
                await PumpAsync(StrataBackdropBlur.HealDelay * 2);
                var (spotBefore, haloBefore) = Sample(window);
                var renders = (first.Renders, second.Renders);

                // Repaints only the spot, beneath the first frost: the frost over it changes in that
                // same frame, while the halo just outside the repainted area waits for the heal.
                spot.Background = Brushes.White;
                var (spotPatched, haloPatched) = Sample(window);
                Assert.True(spotPatched > spotBefore + 60, $"Frost over the spot went from {spotBefore} to {spotPatched}.");
                Assert.True(Math.Abs(haloPatched - haloBefore) < 8, $"Halo changed early: {haloBefore} to {haloPatched}.");
                Assert.Equal(renders, (first.Renders, second.Renders));

                await PumpAsync(StrataBackdropBlur.HealDelay * 2);
                var (_, haloHealed) = Sample(window);
                Assert.True(haloHealed > haloBefore + 20, $"Halo went from {haloBefore} to {haloHealed}.");
                Assert.Equal((renders.Item1 + 1, renders.Item2 + 1), (first.Renders, second.Renders));

                // One heal, then idle: overlapping glass must not keep repainting each other.
                await PumpAsync(StrataBackdropBlur.HealDelay * 3);
                Assert.Equal((renders.Item1 + 1, renders.Item2 + 1), (first.Renders, second.Renders));
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

        static (byte Spot, byte Halo) Sample(Window window)
        {
            using var frame = window.CaptureRenderedFrame()!;
            var pixels = Luma(frame);
            return (pixels[52, 52], pixels[67, 52]);
        }
    }

    private static async Task PumpAsync(TimeSpan duration)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < duration)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(16);
        }
    }

    private sealed class CountingBackdropBlur : StrataBackdropBlur
    {
        public int Renders { get; private set; }

        public override void Render(DrawingContext context)
        {
            Renders++;
            base.Render(context);
        }
    }

    private static async Task RenderAsync(
        StrataBackdropFade fade, Action<byte[,]> assert, bool translucentWindow = false)
    {
        using var session = HeadlessUnitTestSession.StartNew(
            typeof(MobileMotionRenderApp), AvaloniaTestIsolationLevel.PerTest);
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(() =>
        {
            // Over a translucent window the stripes' gaps show nothing of the app.
            var gap = translucentWindow ? null : Brushes.Black;
            var stripes = new StackPanel { Orientation = Orientation.Horizontal };
            for (var index = 0; index < 60; index++)
                stripes.Children.Add(new Border { Width = 4, Background = index % 2 == 0 ? Brushes.White : gap });

            var frost = new StrataBackdropBlur
            {
                BlurRadius = 8,
                Fade = fade,
                FadeLength = 40,
                Width = 100,
                Height = 80,
                Margin = new Thickness(20, 20, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            var window = new Window
            {
                Width = 240, Height = 140, Background = translucentWindow ? Brushes.Transparent : Brushes.Black,
                Content = new Panel { Children = { stripes, frost } }
            };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                if (translucentWindow)
                {
                    // Like Mica: nothing opaque behind the content where headless has no real transparency.
                    foreach (var fallback in window.GetVisualDescendants().OfType<Border>()
                                 .Where(border => border.Name == "PART_TransparencyFallback"))
                        fallback.IsVisible = false;
                    Dispatcher.UIThread.RunJobs();
                }

                using var frame = window.CaptureRenderedFrame()!;
                assert(Luma(frame));
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

    private static byte[,] Luma(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var bytes = new byte[stride * size.Height];
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), buffer, bytes.Length, stride);
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        var luma = new byte[size.Width, size.Height];
        for (var y = 0; y < size.Height; y++)
        for (var x = 0; x < size.Width; x++)
        {
            var index = y * stride + x * 4;
            luma[x, y] = (byte)((bytes[index] + bytes[index + 1] + bytes[index + 2]) / 3);
        }
        return luma;
    }

    private static (byte Min, byte Max) Range(byte[,] pixels, int x, int y, int width, int height)
    {
        byte min = byte.MaxValue, max = byte.MinValue;
        for (var row = y; row < y + height; row++)
        for (var column = x; column < x + width; column++)
        {
            min = Math.Min(min, pixels[column, row]);
            max = Math.Max(max, pixels[column, row]);
        }
        return (min, max);
    }
}
