using System;
using System.Linq;
using Avalonia;
using Lumi.Services;
using Lumi.Views;
using Xunit;
using Part = Lumi.Services.AppIconArt.Part;

namespace Lumi.Tests;

public sealed class StartupMotionTests
{
    private const double Tolerance = 1e-9;

    private static readonly double[] Progress = Enumerable.Range(0, 721).Select(i => i / 720.0).ToArray();

    [Fact]
    public void Loop_EndsWhereItBegins_WithTheMarkAtRest()
    {
        foreach (var part in new[] { Part.You, Part.Model })
        {
            AssertNear(default, StartupMotion.LoopOffset(part, 0));
            AssertNear(default, StartupMotion.LoopOffset(part, 1));
        }

        foreach (var part in new[] { Part.Lumi, Part.You, Part.Model })
        {
            Assert.Equal(1, StartupMotion.LoopScale(part, 0), Tolerance);
            Assert.Equal(1, StartupMotion.LoopScale(part, 1), Tolerance);
        }

        Assert.Equal((StartupMotion.RippleStart, 0d), StartupMotion.Ripple(0));
        Assert.Equal((StartupMotion.RippleStart, 0d), StartupMotion.Ripple(1));
        Assert.Equal(0, StartupMotion.Flare(0));
        Assert.Equal(0, StartupMotion.Flare(1));
    }

    [Fact]
    public void Loop_MovesSmoothly()
    {
        // No jump between neighbouring keyframes: a step over a 720th of the loop stays small.
        foreach (var part in new[] { Part.You, Part.Model })
        {
            for (var i = 1; i < Progress.Length; i++)
            {
                var step = StartupMotion.LoopOffset(part, Progress[i]) - StartupMotion.LoopOffset(part, Progress[i - 1]);
                Assert.True(step.Length < 0.03, $"{part} jumps {step.Length:F3} of the mark at {Progress[i]:F3}.");
            }
        }
    }

    [Fact]
    public void Orbit_KeepsLumiBetweenYouAndTheModel()
    {
        var lumi = AppIconArt.CentreOf(Part.Lumi);
        var youRest = AppIconArt.CentreOf(Part.You);
        var modelRest = AppIconArt.CentreOf(Part.Model);
        var youRadius = ((Vector)(youRest - lumi)).Length;
        var modelRadius = ((Vector)(modelRest - lumi)).Length;
        var turned = false;

        foreach (var p in Progress)
        {
            var turn = StartupMotion.OrbitAngle(p);
            Vector you = youRest + StartupMotion.OrbitOffset(Part.You, turn) - lumi;
            Vector model = modelRest + StartupMotion.OrbitOffset(Part.Model, turn) - lumi;

            Assert.Equal(youRadius, you.Length, 1e-6);
            Assert.Equal(modelRadius, model.Length, 1e-6);
            Assert.Equal(0, you.X * model.Y - you.Y * model.X, 1e-6);
            Assert.True(you.X * model.X + you.Y * model.Y < 0, $"You and the model are on the same side of Lumi at {p:F3}.");
            turned |= Math.Abs(turn) > Math.PI;
        }

        Assert.True(turned, "You and the model never circle Lumi.");
    }

    [Fact]
    public void Entrance_SpinsYouAndTheModelOutOfLumiIntoPlace()
    {
        var lumi = AppIconArt.CentreOf(Part.Lumi);
        foreach (var part in new[] { Part.You, Part.Model })
        {
            var rest = AppIconArt.CentreOf(part);
            AssertNear(lumi - rest, StartupMotion.SpiralOffset(part, 0));
            AssertNear(default, StartupMotion.SpiralOffset(part, 1));
        }

        Assert.Equal(0, StartupMotion.Spring(0, 0.5));
        Assert.Equal(1, StartupMotion.Spring(1, 0.5));
        var peak = Progress.Max(q => StartupMotion.Spring(q, 0.5));
        Assert.InRange(peak, 1.1, 1.25);
        Assert.True(Progress.Max(q => StartupMotion.Spring(q, 0.62)) < peak, "A firmer spring overshoots less.");
    }

    private static void AssertNear(Vector expected, Vector actual)
        => Assert.True((expected - actual).Length < 1e-9, $"Expected {expected}, not {actual}.");
}
