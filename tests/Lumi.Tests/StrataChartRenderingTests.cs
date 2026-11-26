using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Rings and pies as Skia draws them. The drawing-free headless platform keeps a shape's points but not its arcs,
/// so only rendered pixels can tell a round part from a broken one.
/// </summary>
[Collection("Headless UI")]
public sealed class StrataChartRenderingTests
{
    // Of a thousand tests, a few fail: the passing part is all but the whole circle, and is still drawn round,
    // around a ring's hole that stays empty.
    [SkippableTheory]
    [InlineData(StrataChartType.Donut, 999.0)]
    [InlineData(StrataChartType.Donut, 992.0)]
    [InlineData(StrataChartType.Pie, 999.0)]
    public void NearlyWholePart_IsDrawnRound_AroundAnEmptyHole(StrataChartType type, double passed)
    {
        HeadlessUnitTestSession? session = null;
        string? skipReason = null;
        try
        {
            session = HeadlessUnitTestSession.StartNew(typeof(SkiaHeadlessTestApp), AvaloniaTestIsolationLevel.PerTest);
        }
        catch (Exception ex)
        {
            skipReason = $"Skia headless session unavailable: {ex.Message}";
        }

        Skip.If(session is null, skipReason ?? "Skia headless session unavailable.");

        var wrong = new List<string>();
        var captured = false;
        try
        {
            session!.Dispatch(() =>
            {
                var chart = new StrataChart
                {
                    ChartType = type,
                    ChartHeight = 220,
                    ShowLegend = false,
                    Labels = ["Passed", "Failed"],
                    Series = [new StrataChartSeries { Name = "Tests", Values = [passed, 1000 - passed] }],
                };
                var window = new Window { Width = 320, Height = 240, Background = Brushes.White, Content = chart };
                window.Show();
                Tick(40);

                // Its entrance played out, as it is a moment after it first shows.
                var plot = chart.GetVisualDescendants().OfType<Control>().First(control => control.GetType().Name == "ChartCanvas");
                const BindingFlags field = BindingFlags.Instance | BindingFlags.NonPublic;
                plot.GetType().GetField("_animProgress", field)!.SetValue(plot, 1.0);
                plot.GetType().GetField("_hasAnimated", field)!.SetValue(plot, true);
                plot.InvalidateVisual();
                Tick(40);
                Tick(40);

                var center = plot.TranslatePoint(new Point(plot.Bounds.Width / 2, plot.Bounds.Height / 2), window)!.Value;
                var corner = plot.TranslatePoint(new Point(3, 3), window)!.Value;
                using var bitmap = window.CaptureRenderedFrame();
                if (bitmap is null)
                    return;
                captured = true;
                using var pixels = bitmap.Lock();
                var background = Pixel(pixels, corner);
                bool Ink(Point point) => Distance(Pixel(pixels, point), background) > 60;

                // The ring's outer edge, along the row through its centre.
                var left = Enumerable.Range((int)corner.X, (int)(center.X - corner.X))
                    .Select(x => (int?)x).FirstOrDefault(x => Ink(new Point(x!.Value, center.Y)));
                if (left is null)
                {
                    wrong.Add("Nothing is drawn across the middle of the chart.");
                    return;
                }

                var radius = center.X - left.Value;
                var donut = type == StrataChartType.Donut;
                // All round, clear of the gap at the top, the part is there, and a ring's hole is empty.
                for (var degrees = 30; degrees <= 330; degrees += 30)
                {
                    var rad = degrees * Math.PI / 180;
                    Point At(double share) => center + new Vector(Math.Sin(rad), -Math.Cos(rad)) * radius * share;
                    if (!Ink(At(donut ? 0.81 : 0.6)))
                        wrong.Add($"Nothing is drawn at {degrees}°.");
                    if (donut && Ink(At(0.3)))
                        wrong.Add($"The hole is filled at {degrees}°.");
                }

                window.Close();
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            session?.Dispose();
        }

        Skip.If(!captured, "Skia headless capture returned no rendered frames.");
        Assert.Empty(wrong);
    }

    private static void Tick(int realMs)
    {
        Thread.Sleep(realMs);
        try
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        catch
        {
            // Render timer not available on this platform variant; the dispatcher pump still advances.
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static unsafe (int, int, int) Pixel(Avalonia.Platform.ILockedFramebuffer frame, Point point)
    {
        var x = Math.Clamp((int)Math.Round(point.X), 0, frame.Size.Width - 1);
        var y = Math.Clamp((int)Math.Round(point.Y), 0, frame.Size.Height - 1);
        var pixel = (byte*)frame.Address + y * frame.RowBytes + x * 4;
        return (pixel[0], pixel[1], pixel[2]);
    }

    private static int Distance((int, int, int) first, (int, int, int) second) =>
        Math.Abs(first.Item1 - second.Item1) + Math.Abs(first.Item2 - second.Item2) + Math.Abs(first.Item3 - second.Item3);
}
