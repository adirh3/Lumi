using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrataTheme.Animation;

namespace Lumi.Mobile.Behaviors;

/// <summary>A short surface entrance; ownership and layout stay with the existing view.</summary>
public sealed class MobileEntrance : IDisposable
{
    private readonly Control _target;
    private readonly TransformOperations _hiddenTransform;
    private readonly double _offsetY;
    private readonly TimeSpan _duration;
    private int _version;
    private bool _disposed;
    private bool _revealPending;

    public MobileEntrance(Control target, double offsetY = 16, TimeSpan? duration = null)
    {
        _target = target;
        _offsetY = offsetY;
        _duration = duration ?? TimeSpan.FromMilliseconds(320);
        _hiddenTransform = TransformOperations.Parse(
            FormattableString.Invariant($"translateY({offsetY}px)"));
        _target.AttachedToVisualTree += OnAttached;
        _target.DetachedFromVisualTree += OnDetached;
    }

    public void Prepare()
    {
        if (_disposed)
            return;

        _version++;
        _revealPending = false;
        SlideFadeEntrance.StopComposited(_target);
        _target.Transitions = null;
        _target.Opacity = 0;
        _target.RenderTransform = _hiddenTransform;
    }

    public void Play()
    {
        Prepare();
        Reveal();
    }

    public void Reveal()
    {
        if (_disposed)
            return;

        _revealPending = true;
        var version = _version;
        void RevealCore()
        {
            if (_disposed || !_revealPending || version != _version || !_target.IsAttachedToVisualTree())
                return;

            _revealPending = false;
            if (!_target.IsEffectivelyVisible)
            {
                Stop();
                return;
            }

            SlideFadeEntrance.PlayComposited(_target, _offsetY, _duration);
        }

        // Visibility bindings and the initial viewport must settle before motion starts.
        Dispatcher.UIThread.Post(RevealCore, DispatcherPriority.Loaded);
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_revealPending)
            Reveal();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => Stop();

    public void Stop()
    {
        _version++;
        _revealPending = false;
        SlideFadeEntrance.StopComposited(_target);
        _target.Transitions = null;
        _target.Opacity = 1;
        _target.RenderTransform = TransformOperations.Identity;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _target.AttachedToVisualTree -= OnAttached;
        _target.DetachedFromVisualTree -= OnDetached;
        Stop();
    }
}
