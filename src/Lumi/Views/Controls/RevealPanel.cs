using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.VisualTree;
using StrataTheme.Controls;

namespace Lumi.Views.Controls;

/// <summary>
/// Content that opens from the top while it fades in and folds away the same way, with the motion of
/// a transcript's "worked for" row (<see cref="StrataTurnSummary"/>). The content keeps its natural
/// layout and is clipped, so it never reflows mid-motion, and the transcript keeps the row in place
/// while what lies below it glides. A change while the panel isn't on screen, or with the
/// <c>motion-disabled</c> class, applies at once, and <see cref="Settle"/> ends a motion where a
/// caller needs final positions (a search hit inside the content).
/// </summary>
public sealed class RevealPanel : Decorator
{
    private const double OpenDurationMs = 220;
    private const double CloseDurationMs = 170;
    private static readonly Easing MotionEasing = new CubicEaseOut();

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<RevealPanel, bool>(nameof(IsOpen));

    private double _reveal;
    private double _naturalHeight;
    private bool _isMoving;
    private int _motionVersion;
    private long _motionStart;
    private double _motionFrom;
    private double _motionTo;
    private double _motionDurationMs;

    static RevealPanel()
    {
        ClipToBoundsProperty.OverrideDefaultValue<RevealPanel>(true);
        IsOpenProperty.Changed.AddClassHandler<RevealPanel>((panel, _) => panel.OnIsOpenChanged());
    }

    public RevealPanel()
    {
        IsVisible = false;
        Opacity = 0;
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>How much of the content shows, from 0 (folded) to 1 (open).</summary>
    public double Reveal => _reveal;

    /// <summary>Ends any motion where it would end: open or folded.</summary>
    public void Settle()
    {
        _motionVersion++;
        _isMoving = false;
        SetReveal(IsOpen ? 1 : 0);
        IsVisible = IsOpen;
    }

    private void OnIsOpenChanged()
    {
        _motionVersion++;
        _isMoving = false;
        var target = IsOpen ? 1d : 0d;
        if (IsOpen)
            IsVisible = true;

        if (Math.Abs(_reveal - target) < 0.001
            || Classes.Contains("motion-disabled")
            || this.GetVisualParent() is not Visual { IsEffectivelyVisible: true, Bounds.Width: > 0 }
            || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            Settle();
            return;
        }

        // The chat stops following its tail, so the content moves what lies below it, not the row.
        this.FindAncestorOfType<StrataChatShell>()?.PreserveViewport();
        _motionFrom = _reveal;
        _motionTo = target;
        _motionDurationMs = (IsOpen ? OpenDurationMs : CloseDurationMs) * Math.Max(0.4, Math.Abs(target - _motionFrom));
        _motionStart = 0;
        _isMoving = true;
        var version = _motionVersion;
        topLevel.RequestAnimationFrame(_ => OnMotionFrame(topLevel, version));
    }

    // Each step lands in the frame being composed, and the last one settles the panel in that same frame.
    private void OnMotionFrame(TopLevel topLevel, int version)
    {
        if (version != _motionVersion)
            return;

        // The clock starts once the content has been laid out, so building it delays the motion
        // instead of using it up.
        if (_motionStart == 0)
        {
            if (Child is { IsMeasureValid: true })
                _motionStart = Stopwatch.GetTimestamp();
            topLevel.RequestAnimationFrame(_ => OnMotionFrame(topLevel, version));
            return;
        }

        var t = Math.Clamp(Stopwatch.GetElapsedTime(_motionStart).TotalMilliseconds / _motionDurationMs, 0, 1);
        if (t >= 1)
        {
            Settle();
            return;
        }

        SetReveal(_motionFrom + (_motionTo - _motionFrom) * MotionEasing.Ease(t));
        topLevel.RequestAnimationFrame(_ => OnMotionFrame(topLevel, version));
    }

    private void SetReveal(double reveal)
    {
        _reveal = Math.Clamp(reveal, 0, 1);
        Opacity = _reveal;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var natural = base.MeasureOverride(availableSize.WithHeight(double.PositiveInfinity));
        _naturalHeight = natural.Height;
        return natural.WithHeight(natural.Height * _reveal);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        base.ArrangeOverride(finalSize.WithHeight(Math.Max(finalSize.Height, _naturalHeight)));
        return finalSize;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // A panel leaving the transcript mid-motion settles at once, so it never returns half open.
        if (_isMoving)
            Settle();
        base.OnDetachedFromVisualTree(e);
    }
}
