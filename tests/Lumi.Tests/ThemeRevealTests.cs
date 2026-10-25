using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Lumi.Views;
using SkiaSharp;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ThemeRevealTests
{
    private const int Width = 600;
    private const int Height = 400;

    [Fact]
    public void Edge_SpreadsOutAtOnce_AndNeverTurnsBack()
    {
        Assert.Equal(0, ThemeReveal.Reach(0));
        Assert.Equal(1, ThemeReveal.Reach(1));
        Assert.Equal(1, ThemeReveal.Reach(1.5));

        var reach = Enumerable.Range(0, 101).Select(i => ThemeReveal.Reach(i / 100.0)).ToArray();
        Assert.All(reach.Zip(reach.Skip(1)), step => Assert.True(step.Second > step.First));
        // Off at once: well on its way within a tenth of its time.
        Assert.True(ThemeReveal.Reach(0.1) > 0.15);
    }

    [Fact]
    public void Glow_ComesUpQuickly_AndIsGoneWhenTheRevealEnds()
    {
        Assert.Equal(0, ThemeReveal.Glow(0));
        Assert.Equal(1, ThemeReveal.Glow(0.4));
        Assert.Equal(0, ThemeReveal.Glow(1), 6);
        Assert.True(ThemeReveal.Glow(0.05) > 0.5);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(800, 300)]
    [InlineData(1600, 1000)]
    [InlineData(30, 990)]
    public void Edge_EndsPastTheFarthestCorner(double x, double y)
    {
        var origin = new Point(x, y);
        var end = ThemeReveal.EndRadius(new Size(1600, 1000), origin);

        foreach (var corner in new[] { new Point(0, 0), new Point(1600, 0), new Point(0, 1000), new Point(1600, 1000) })
            Assert.True(end >= Distance(origin, corner) + 40);
        var duration = ThemeReveal.DurationFor(end);
        Assert.InRange(duration.TotalSeconds, 0.5, 0.75);
    }

    [Fact]
    public void Shader_Compiles()
    {
        using var effect = ThemeReveal.CreateEffect();
        Assert.NotNull(effect);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reveal_StartsAsTheOldLookExactly_AndEndsWithNothingOverTheNewOne(bool effects)
    {
        using var picture = Picture(opaque: true);
        var reveal = Reveal(SKColors.Transparent, new Point(450, 120));

        using (var start = Render(reveal, picture, 0, effects))
            AssertSameAs(picture, start, tolerance: 1);

        using var end = Render(reveal, picture, 1, effects);
        for (var y = 0; y < Height; y += 3)
            for (var x = 0; x < Width; x += 3)
                Assert.Equal(0, end.GetPixel(x, y).Alpha);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reveal_Midway_ShowsTheNewLookBehindTheEdge_AndTheOldLookAheadOfIt(bool effects)
    {
        using var picture = Picture(opaque: true);
        var origin = new Point(0, 0);
        var reveal = Reveal(SKColors.Transparent, origin);
        const double q = 0.3;
        var radius = reveal.EndRadius * ThemeReveal.Reach(q);
        using var frame = Render(reveal, picture, q, effects);
        using var old = SKBitmap.FromImage(picture);

        for (var y = 0; y < Height; y += 4)
        {
            for (var x = 0; x < Width; x += 4)
            {
                var d = Distance(origin, new Point(x + 0.5, y + 0.5));
                if (d < radius - 230)
                    Assert.True(frame.GetPixel(x, y).Alpha <= 1, $"({x},{y}) still shows the old look");
                else if (d > radius + 170)
                    AssertClose(old.GetPixel(x, y), frame.GetPixel(x, y), 1);
            }
        }
    }

    [Fact]
    public void Reveal_OfASeeThroughWindow_StandsOnTheOldBackground()
    {
        using var picture = Picture(opaque: false);
        var background = new SKColor(0x16, 0x16, 0x16);
        var reveal = Reveal(background, new Point(300, 200));

        using var start = Render(reveal, picture, 0, effects: true);
        using var old = SKBitmap.FromImage(picture);
        for (var y = 0; y < Height; y += 7)
        {
            for (var x = 0; x < Width; x += 7)
            {
                var c = old.GetPixel(x, y);
                var shown = start.GetPixel(x, y);
                Assert.Equal(255, shown.Alpha);
                // The picture over the background, as the window showed it over its material.
                var a = c.Alpha / 255.0;
                Assert.InRange(shown.Red, Over(c.Red, a, background.Red) - 2, Over(c.Red, a, background.Red) + 2);
                Assert.InRange(shown.Blue, Over(c.Blue, a, background.Blue) - 2, Over(c.Blue, a, background.Blue) + 2);
            }
        }
    }

    [Fact]
    public async Task Pick_PlaysOnlyInItsOwnWindow_Once()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var button = new Border { Width = 80, Height = 30, Margin = new Thickness(40, 20, 0, 0) };
            var picked = new Window
            {
                Width = 400, Height = 300,
                Content = new Canvas { Children = { button } },
            };
            var other = new Window { Width = 400, Height = 300 };
            try
            {
                picked.Show();
                other.Show();
                picked.UpdateLayout();

                // Nothing picked: the theme simply changes.
                var applied = 0;
                ThemeReveal.Apply(other, dark: true, () => applied++);
                Assert.Equal(1, applied);

                // A pick waits for its own window's change, spreading from the middle of what was picked.
                ThemeReveal.Pick(button);
                Assert.Null(ThemeReveal.TakePick(other));
                Assert.Equal(new Point(80, 35), ThemeReveal.TakePick(picked));
                Assert.Null(ThemeReveal.TakePick(picked));
            }
            finally
            {
                picked.Close();
                other.Close();
            }
        }, CancellationToken.None);
    }

    private static ThemeReveal.Reveal Reveal(SKColor background, Point origin)
    {
        var size = new Size(Width, Height);
        var end = ThemeReveal.EndRadius(size, origin);
        return new ThemeReveal.Reveal(
            background, size, 1, Matrix.Identity, origin, end, ThemeReveal.DurationFor(end), Dark: true,
            new SKColor(0x81, 0x8C, 0xF8),
            new TaskCompletionSource());
    }

    private static SKBitmap Render(ThemeReveal.Reveal reveal, SKImage picture, double q, bool effects)
    {
        var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        ThemeReveal.DrawFrame(canvas, reveal, picture, q, effects);
        canvas.Flush();
        return bitmap;
    }

    /// <summary>Busy enough that any shift, blur or tint would show: stripes, a gradient and noise.</summary>
    private static SKImage Picture(bool opaque)
    {
        var random = new Random(7);
        using var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, opaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul));
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var stripe = (x / 6 + y / 9) % 2 == 0 ? 60 : 0;
                bitmap.SetPixel(x, y, new SKColor(
                    (byte)Math.Min(255, x * 255 / Width + stripe),
                    (byte)random.Next(256),
                    (byte)(y * 255 / Height),
                    opaque ? (byte)255 : (byte)(120 + random.Next(100))));
            }
        }
        return SKImage.FromBitmap(bitmap);
    }

    private static void AssertSameAs(SKImage picture, SKBitmap frame, int tolerance)
    {
        using var old = SKBitmap.FromImage(picture);
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                AssertClose(old.GetPixel(x, y), frame.GetPixel(x, y), tolerance);
    }

    private static void AssertClose(SKColor expected, SKColor actual, int tolerance)
    {
        Assert.InRange(actual.Red, expected.Red - tolerance, expected.Red + tolerance);
        Assert.InRange(actual.Green, expected.Green - tolerance, expected.Green + tolerance);
        Assert.InRange(actual.Blue, expected.Blue - tolerance, expected.Blue + tolerance);
        Assert.InRange(actual.Alpha, expected.Alpha - tolerance, expected.Alpha + tolerance);
    }

    private static int Over(byte colour, double alpha, byte background) => (int)Math.Round(colour * alpha + background * (1 - alpha));

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
