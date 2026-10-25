using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Lumi.Services;
using StrataTheme;
using Part = Lumi.Services.AppIconArt.Part;

namespace Lumi.Views;

/// <summary>
/// The loading window's motion. Lumi's mark puts itself together: the tile springs in, Lumi pops in with a
/// twirl, and you and the model spin out of Lumi to either side of it. Then, while Lumi starts, you float and
/// Lumi breathes, and every few seconds you and the model circle Lumi once, always on opposite sides of it,
/// and land with a ping. Every curve is a function of progress, sampled into render-thread keyframes, so the
/// motion keeps playing while the UI thread builds the main window. Positions are fractions of the mark's side.
/// </summary>
internal static class StartupMotion
{
    /// <summary>When the loop takes over from the entrance, and how long one round of it lasts.</summary>
    internal static readonly TimeSpan LoopStart = TimeSpan.FromMilliseconds(1250);
    internal static readonly TimeSpan LoopPeriod = TimeSpan.FromMilliseconds(3600);

    private const double Tau = Math.PI * 2;

    // The loop's beats, as fractions of its period: you float and Lumi breathes; you and the model wind up,
    // swing once around Lumi past where they started, settle back and land; then you float again.
    private const double FirstFloatEnd = 0.24, SecondFloatStart = 0.72;
    private const double WindUpStart = 0.26, SwingStart = 0.30, SwingEnd = 0.58, SettleEnd = 0.66;
    private const double RippleEnd = SwingEnd + 0.14, FlareStart = SwingEnd - 0.02, FlareEnd = SwingEnd + 0.17;
    private const double WindUp = 0.16, Overshoot = 0.14;
    private const double FloatHeight = 0.022, BreathDepth = 0.03;

    // How far you and the model come around Lumi as they spin out of it.
    private const double SpiralTurn = Math.PI * 1.25;

    private static readonly double PopPeak = PopShape(Math.Atan(Tau / 3) / Tau);
    private static readonly LinearEasing Linear = new();

    /// <summary>The angle you and the model have travelled around Lumi at loop progress <paramref name="p"/>.</summary>
    internal static double OrbitAngle(double p)
    {
        if (p <= WindUpStart)
            return 0;
        if (p < SwingStart)
            return -WindUp * EaseOutSine(Progress(p, WindUpStart, SwingStart));
        if (p < SwingEnd)
            return -WindUp + (Tau + Overshoot + WindUp) * EaseInOutSine(Progress(p, SwingStart, SwingEnd));
        if (p < SettleEnd)
            return Tau + Overshoot - Overshoot * EaseInOutSine(Progress(p, SwingEnd, SettleEnd));
        return Tau;
    }

    /// <summary>Where you or the model is at loop progress <paramref name="p"/>, from its resting place.</summary>
    internal static Vector LoopOffset(Part part, double p)
    {
        var offset = OrbitOffset(part, OrbitAngle(p));
        if (part == Part.You)
            offset += new Vector(0, -FloatHeight * Rest(p));
        return offset;
    }

    /// <summary>Where you or the model is, from its resting place, once it has travelled
    /// <paramref name="turn"/> radians around Lumi.</summary>
    internal static Vector OrbitOffset(Part part, double turn)
    {
        var lumi = AppIconArt.CentreOf(Part.Lumi);
        var rest = AppIconArt.CentreOf(part);
        var radius = Distance(rest, lumi);
        var angle = Math.Atan2(rest.Y - lumi.Y, rest.X - lumi.X) + turn;
        return new Vector(lumi.X + radius * Math.Cos(angle) - rest.X, lumi.Y + radius * Math.Sin(angle) - rest.Y);
    }

    /// <summary>The scale of a part at loop progress <paramref name="p"/>: Lumi breathes, and each part
    /// swells as you and the model land.</summary>
    internal static double LoopScale(Part part, double p) => part switch
    {
        Part.Lumi => 1 + BreathDepth * Rest(p) + 0.07 * Pop(Progress(p, SwingEnd - 0.01, SwingEnd + 0.13)),
        Part.You => 1 + 0.16 * Pop(Progress(p, SwingEnd, SwingEnd + 0.12)),
        Part.Model => 1 + 0.6 * Pop(Progress(p, SwingEnd, SwingEnd + 0.12)),
        _ => 1,
    };

    /// <summary>A ring that spreads from the model as it lands.</summary>
    internal static (double Scale, double Opacity) Ripple(double p)
    {
        if (p <= SwingEnd || p >= RippleEnd)
            return (RippleStart, 0);
        var q = Progress(p, SwingEnd, RippleEnd);
        return (RippleStart + (1 - RippleStart) * EaseOutCubic(q), 0.75 * Math.Min(1, q / 0.1) * (1 - q) * (1 - q));
    }

    internal const double RippleStart = 0.3;

    /// <summary>A burst of light behind the mark as you and the model land.</summary>
    internal static double Flare(double p)
    {
        if (p <= FlareStart || p >= FlareEnd)
            return 0;
        var q = Progress(p, FlareStart, FlareEnd);
        return q < 0.15 ? EaseOutCubic(q / 0.15) : 1 - EaseInOutSine((q - 0.15) / 0.85);
    }

    // While you and the model rest, you float up and back, and Lumi breathes: 0 at rest, 1 at the top.
    private static double Rest(double p)
        => p < FirstFloatEnd ? Bell(p / FirstFloatEnd)
            : p > SecondFloatStart ? Bell(Progress(p, SecondFloatStart, 1))
            : 0;

    /// <summary>Where you or the model is as it spins out of Lumi, at entrance progress <paramref name="q"/>,
    /// from its resting place: it leaves Lumi's centre and comes around it to its place.</summary>
    internal static Vector SpiralOffset(Part part, double q)
    {
        var lumi = AppIconArt.CentreOf(Part.Lumi);
        var rest = AppIconArt.CentreOf(part);
        var radius = Distance(rest, lumi) * Spring(q, 0.62);
        var angle = Math.Atan2(rest.Y - lumi.Y, rest.X - lumi.X) - SpiralTurn * Math.Pow(1 - Math.Clamp(q, 0, 1), 3);
        return new Vector(lumi.X + radius * Math.Cos(angle) - rest.X, lumi.Y + radius * Math.Sin(angle) - rest.Y);
    }

    /// <summary>
    /// A damped spring from 0 to 1 at progress <paramref name="q"/>: it overshoots once by an amount the
    /// <paramref name="damping"/> ratio sets (0.5 overshoots by about 16%), then settles, exactly 1 at the end.
    /// </summary>
    internal static double Spring(double q, double damping)
    {
        if (q <= 0)
            return 0;
        if (q >= 1)
            return 1;
        const double stiffness = 12;
        var ringing = stiffness * Math.Sqrt(1 - damping * damping);
        var decay = Math.Exp(-damping * stiffness * q);
        return 1 - decay * (Math.Cos(ringing * q) + damping * stiffness / ringing * Math.Sin(ringing * q));
    }

    internal static double EaseOutCubic(double q) => 1 - Math.Pow(1 - Math.Clamp(q, 0, 1), 3);

    /// <summary>One render-thread animation: how long it plays, after what delay, and whether it repeats.</summary>
    internal readonly record struct Timing(TimeSpan Duration, TimeSpan Delay, bool Loop = false)
    {
        public static Timing Once(double durationMs, double delayMs = 0)
            => new(TimeSpan.FromMilliseconds(durationMs), TimeSpan.FromMilliseconds(delayMs));

        public static Timing Looping(TimeSpan period, TimeSpan delay) => new(period, delay, Loop: true);
    }

    /// <summary>Scales <paramref name="target"/> about <paramref name="centre"/> (in its own coordinates).</summary>
    internal static void Scale(Visual target, Point centre, Func<double, Vector> scale, Timing timing)
    {
        if (ElementComposition.GetElementVisual(target) is not { } visual)
            return;
        visual.CenterPoint = new Vector3D(centre.X, centre.Y, 0);
        visual.Scale = new Vector3D(1, 1, 1);
        var animation = visual.Compositor.CreateStableVector3DKeyFrameAnimation();
        Sample(timing, p =>
        {
            var value = scale(p);
            animation.InsertKeyFrame((float)p, new Vector3D(value.X, value.Y, 1), Linear);
        });
        Start(visual, nameof(CompositionVisual.Scale), animation, timing);
    }

    internal static void Scale(Visual target, Point centre, Func<double, double> scale, Timing timing)
        => Scale(target, centre, p => new Vector(scale(p), scale(p)), timing);

    /// <summary>Moves <paramref name="target"/> from where layout put it, by <paramref name="offset"/> in DIPs.</summary>
    internal static void Move(Visual target, Func<double, Vector> offset, Timing timing)
    {
        if (ElementComposition.GetElementVisual(target) is not { } visual)
            return;
        visual.Translation = default;
        var animation = visual.Compositor.CreateStableVector3DKeyFrameAnimation();
        Sample(timing, p =>
        {
            var value = offset(p);
            animation.InsertKeyFrame((float)p, new Vector3D(value.X, value.Y, 0), Linear);
        });
        Start(visual, nameof(CompositionVisual.Translation), animation, timing);
    }

    /// <summary>Turns <paramref name="target"/> about <paramref name="centre"/> by <paramref name="radians"/>.</summary>
    internal static void Turn(Visual target, Point centre, Func<double, double> radians, Timing timing)
    {
        if (ElementComposition.GetElementVisual(target) is not { } visual)
            return;
        visual.CenterPoint = new Vector3D(centre.X, centre.Y, 0);
        visual.RotationAngle = 0;
        var animation = visual.Compositor.CreateStableScalarKeyFrameAnimation();
        Sample(timing, p => animation.InsertKeyFrame((float)p, (float)radians(p), Linear));
        Start(visual, nameof(CompositionVisual.RotationAngle), animation, timing);
    }

    /// <summary>Fades <paramref name="target"/>; it rests fully opaque.</summary>
    internal static void Fade(Visual target, Func<double, double> opacity, Timing timing)
    {
        if (ElementComposition.GetElementVisual(target) is not { } visual)
            return;
        visual.Opacity = (float)target.Opacity;
        var animation = visual.Compositor.CreateStableScalarKeyFrameAnimation();
        Sample(timing, p => animation.InsertKeyFrame((float)p, (float)Math.Clamp(opacity(p), 0, 1), Linear));
        Start(visual, nameof(CompositionVisual.Opacity), animation, timing);
    }

    /// <summary>
    /// Brings a property of <paramref name="target"/> from wherever its animation has it now to
    /// <paramref name="rest"/>, so motion can be handed off without a jump.
    /// </summary>
    internal static void Settle(Visual target, string property, Vector3D rest, TimeSpan duration)
    {
        if (ElementComposition.GetElementVisual(target) is not { } visual)
            return;
        var animation = visual.Compositor.CreateStableVector3DKeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0f, "this.StartingValue");
        animation.InsertKeyFrame(1f, rest, SettleEasing);
        Start(visual, property, animation, new Timing(duration, TimeSpan.Zero));
    }

    /// <inheritdoc cref="Settle(Visual, string, Vector3D, TimeSpan)"/>
    internal static void Settle(Visual target, string property, float rest, TimeSpan duration)
    {
        if (ElementComposition.GetElementVisual(target) is not { } visual)
            return;
        var animation = visual.Compositor.CreateStableScalarKeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0f, "this.StartingValue");
        animation.InsertKeyFrame(1f, rest, SettleEasing);
        Start(visual, property, animation, new Timing(duration, TimeSpan.Zero));
    }

    private static readonly CubicEaseOut SettleEasing = new();

    // About sixty keyframes a second, joined by straight lines: smooth at any frame rate.
    private static void Sample(Timing timing, Action<double> insert)
    {
        var count = Math.Max(8, (int)Math.Ceiling(timing.Duration.TotalMilliseconds / 16));
        for (var i = 0; i <= count; i++)
            insert((double)i / count);
    }

    private static void Start(CompositionVisual visual, string property, KeyFrameAnimation animation, Timing timing)
    {
        animation.Duration = timing.Duration;
        animation.DelayTime = timing.Delay;
        animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        animation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
        animation.IterationBehavior = timing.Loop ? AnimationIterationBehavior.Forever : AnimationIterationBehavior.Count;
        animation.IterationCount = 1;
        visual.StartAnimation(property, animation);
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double Progress(double p, double from, double to) => Math.Clamp((p - from) / (to - from), 0, 1);

    // Rises from 0 to 1 and back with no jolt at either end.
    private static double Bell(double q) => (1 - Math.Cos(Tau * Math.Clamp(q, 0, 1))) / 2;

    // A quick swell that settles through a small dip: 0 at both ends, 1 at its peak.
    private static double Pop(double q) => q <= 0 || q >= 1 ? 0 : PopShape(q) / PopPeak;

    private static double PopShape(double q) => Math.Sin(Tau * q) * Math.Exp(-3 * q);

    private static double EaseOutSine(double q) => Math.Sin(q * Math.PI / 2);

    private static double EaseInOutSine(double q) => (1 - Math.Cos(Math.PI * q)) / 2;
}
