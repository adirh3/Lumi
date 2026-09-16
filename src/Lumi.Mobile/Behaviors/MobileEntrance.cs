using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Lumi.Mobile.Behaviors;

/// <summary>A short surface entrance; ownership and layout stay with the existing view.</summary>
public sealed class MobileEntrance : IDisposable
{
    private readonly Control _target;
    private int _version;
    private bool _disposed;

    public MobileEntrance(Control target) => _target = target;

    public void Prepare()
    {
        _version++;
        _target.Transitions = null;
        _target.Opacity = 0;
        _target.RenderTransform = TransformOperations.Parse("translateY(10px)");
    }

    public void Play()
    {
        Prepare();
        Reveal();
    }

    public void Reveal()
    {
        var version = _version;
        void RevealCore()
        {
            if (_disposed || version != _version || !_target.IsAttachedToVisualTree())
                return;

            _target.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(180),
                    Easing = new CubicEaseOut()
                },
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(220),
                    Easing = new CubicEaseOut()
                }
            };
            _target.RenderTransform = TransformOperations.Identity;
            _target.Opacity = 1;
        }

        if (_target.IsAttachedToVisualTree())
            RevealCore();
        else
            Dispatcher.UIThread.Post(RevealCore, DispatcherPriority.Loaded);
    }

    public void Dispose()
    {
        _disposed = true;
        _version++;
        _target.Transitions = null;
        _target.Opacity = 1;
        _target.RenderTransform = TransformOperations.Identity;
    }
}
