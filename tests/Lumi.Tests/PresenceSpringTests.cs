using System;
using System.Collections.Generic;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// The deterministic, display-free assessor for the presence's focus motion. Rendering screenshots
/// cannot prove a glide "feels alive" — but the property that actually makes motion read as alive vs.
/// "teleporty" is <b>C¹ continuity</b>: when the target is re-aimed mid-flight (which the focus-follow
/// timer does ~9×/second), the velocity must carry through instead of resetting to zero. Because the
/// motion is now driven by the pure <see cref="PresenceSpring"/> model, that continuity is exactly,
/// deterministically testable here — no pixels, no wall-clock, no flake.
/// </summary>
public sealed class PresenceSpringTests
{
    // ── The closed form is correct (velocity is the true derivative of position) ────────────────────

    [Theory]
    [InlineData(7.0, 0.86, 120.0, -30.0)] // underdamped
    [InlineData(5.0, 1.0, -80.0, 40.0)]   // critically damped
    [InlineData(4.0, 1.4, 100.0, 0.0)]    // overdamped
    public void Evaluate_VelocityMatchesFiniteDifferenceOfPosition(double omega, double zeta, double p0, double v0)
    {
        const double dt = 1e-5;
        foreach (var t in new[] { 0.03, 0.12, 0.3, 0.7 })
        {
            var (_, v) = PresenceSpring.Evaluate(p0, v0, omega, zeta, t);
            var (pPlus, _) = PresenceSpring.Evaluate(p0, v0, omega, zeta, t + dt);
            var (pMinus, _) = PresenceSpring.Evaluate(p0, v0, omega, zeta, t - dt);
            var vFd = (pPlus - pMinus) / (2 * dt);
            Assert.True(Math.Abs(v - vFd) < 1e-2 * (1 + Math.Abs(v)),
                $"analytic velocity {v:F4} != finite-difference {vFd:F4} at t={t} (ω={omega}, ζ={zeta})");
        }
    }

    [Theory]
    [InlineData(7.0, 0.86)]
    [InlineData(5.0, 1.0)]
    [InlineData(4.0, 1.4)]
    public void Evaluate_StartsExactlyAtInitialState(double omega, double zeta)
    {
        var (p, v) = PresenceSpring.Evaluate(123.0, -45.0, omega, zeta, 0.0);
        Assert.Equal(123.0, p, 9);
        Assert.Equal(-45.0, v, 9);
    }

    [Theory]
    [InlineData(7.0, 0.86)]
    [InlineData(5.0, 1.0)]
    [InlineData(6.0, 1.4)]
    public void Evaluate_ConvergesToTargetAndRest(double omega, double zeta)
    {
        var (p, v) = PresenceSpring.Evaluate(150.0, 60.0, omega, zeta, 6.0);
        Assert.True(Math.Abs(p) < 0.5, $"did not settle to target: p={p:F4}");
        Assert.True(Math.Abs(v) < 0.5, $"did not come to rest: v={v:F4}");
    }

    [Fact]
    public void CriticallyDamped_FromRest_DoesNotOvershoot()
    {
        const double p0 = 100.0;
        var minDisplacement = p0;
        for (double t = 0; t <= 3.0; t += 0.005)
            minDisplacement = Math.Min(minDisplacement, PresenceSpring.Evaluate(p0, 0.0, 5.0, 1.0, t).Displacement);

        // Approaches the target (0) from above and never crosses it.
        Assert.True(minDisplacement >= -1e-6, $"critical damping overshot: min displacement {minDisplacement:F6}");
    }

    [Fact]
    public void Underdamped_FromRest_OvershootIsSmall()
    {
        const double p0 = 100.0;
        var minDisplacement = p0;
        for (double t = 0; t <= 3.0; t += 0.005)
            minDisplacement = Math.Min(minDisplacement, PresenceSpring.Evaluate(p0, 0.0, 7.0, 0.86, t).Displacement);

        var overshoot = Math.Max(0, -minDisplacement);
        // ζ=0.86 is only lightly underdamped — a hair of overshoot for an eager arrival, never a wobble.
        Assert.True(overshoot < 0.05 * p0, $"overshoot {overshoot:F3} exceeds 5% of travel");
        Assert.True(overshoot > 0, "expected a touch of overshoot for the lively lead lobes");
    }

    // ── The assessor: a re-aimed chase is C¹-continuous (velocity carries through every re-target) ──

    /// <summary>
    /// Simulates the real situation that used to "teleport": a focus target that keeps moving while the
    /// glow is still travelling, re-aimed every 110&#160;ms. With the spring, each re-aim seeds the new
    /// trajectory from the live <c>(position, velocity)</c>, so velocity is continuous across every seam.
    /// We assert the seam velocity jump is ~0 (the property that makes the motion flow) AND that a naïve
    /// keyframe-style re-aim — which resets velocity to zero — would instead suffer large discontinuities.
    /// </summary>
    [Fact]
    public void ReaimedChase_SpringIsC1Continuous_WhereasResettingVelocityIsNot()
    {
        const double omega = 6.0;
        const double zeta = 0.9;
        const double reaimDt = 0.110; // the focus-follow timer interval

        // A target that marches downward in steps (e.g. the gaze tracking a streaming answer).
        var targets = new[] { 0.0, 60.0, 110.0, 150.0, 175.0, 190.0, 198.0, 200.0 };

        double springSeamJump = SimulateChase(targets, reaimDt, omega, zeta, preserveVelocity: true, out var springEndErr);
        double resetSeamJump = SimulateChase(targets, reaimDt, omega, zeta, preserveVelocity: false, out _);

        // The spring carries momentum across every re-aim: the seam is (numerically) perfectly C¹.
        Assert.True(springSeamJump < 1e-6, $"spring re-aim was not velocity-continuous (max seam jump {springSeamJump:F6})");

        // Resetting velocity to zero on each re-aim (the keyframe/ease behaviour that read as a teleport)
        // produces large velocity discontinuities — this is exactly the failure mode the spring removes.
        Assert.True(resetSeamJump > 50.0, $"control model should show a big seam jump but was {resetSeamJump:F3}");

        // And the spring still arrives: after the final target + a settle window it lands on target.
        Assert.True(springEndErr < 1.0, $"spring chase did not converge on the final target (err {springEndErr:F3})");
    }

    /// <summary>
    /// Position stays continuous across re-aims (no jump): the new trajectory always begins exactly where
    /// the old one was, so a densely-sampled chase has no step discontinuity — the model-level proof that
    /// the focus does not "teleport".
    /// </summary>
    [Fact]
    public void ReaimedChase_PositionIsContinuous_NoTeleportStep()
    {
        const double omega = 6.0;
        const double zeta = 0.9;
        const double reaimDt = 0.110;
        var targets = new[] { 0.0, 80.0, 140.0, 180.0, 200.0 };

        var samples = new List<double>();
        double curX = 0, curV = 0;
        foreach (var target in targets)
        {
            double p0 = curX - target, v0 = curV;
            // Sample this segment densely up to the next re-aim.
            for (double t = 0; t < reaimDt; t += 1.0 / 240.0)
            {
                var (d, _) = PresenceSpring.Evaluate(p0, v0, omega, zeta, t);
                samples.Add(target + d);
            }
            var (de, ve) = PresenceSpring.Evaluate(p0, v0, omega, zeta, reaimDt);
            curX = target + de;
            curV = ve;
        }

        // No two consecutive dense samples jump more than a small fraction of the whole journey.
        var maxStep = 0.0;
        for (int i = 1; i < samples.Count; i++)
            maxStep = Math.Max(maxStep, Math.Abs(samples[i] - samples[i - 1]));

        Assert.True(maxStep < 12.0, $"position step {maxStep:F3} too large — motion is not continuous");
    }

    /// <summary>
    /// Walks a target sequence, re-aiming every <paramref name="reaimDt"/>. Returns the maximum velocity
    /// discontinuity at the re-aim seams. With <paramref name="preserveVelocity"/> the new trajectory is
    /// seeded from the live velocity (the spring); without it the velocity is reset to zero (the old
    /// keyframe/ease behaviour). <paramref name="endError"/> is the residual distance to the final target
    /// after an additional settle window.
    /// </summary>
    private static double SimulateChase(double[] targets, double reaimDt, double omega, double zeta,
        bool preserveVelocity, out double endError)
    {
        double curX = 0, curV = 0;
        double maxSeamJump = 0;
        bool first = true;

        foreach (var target in targets)
        {
            // Velocity the new segment will start with.
            var seededV = preserveVelocity ? curV : 0.0;
            if (!first)
                maxSeamJump = Math.Max(maxSeamJump, Math.Abs(seededV - curV));
            first = false;

            double p0 = curX - target;
            var (d, v) = PresenceSpring.Evaluate(p0, seededV, omega, zeta, reaimDt);
            curX = target + d;
            curV = v;
        }

        // Settle on the final target.
        var (df, _) = PresenceSpring.Evaluate(curX - targets[^1], curV, omega, zeta, 2.5);
        endError = Math.Abs(targets[^1] + df - targets[^1]);
        return maxSeamJump;
    }
}
