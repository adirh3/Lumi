using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;

namespace Lumi.Qa;

internal static class QaUiVisibility
{
    internal static bool IsVisibleWithin(Control control, Control boundary)
    {
        if (!control.IsEffectivelyVisible
            || (!ReferenceEquals(control, boundary) && !control.GetVisualAncestors().Contains(boundary)))
            return false;
        var transform = control.TransformToVisual(boundary);
        if (transform is null)
            return false;
        var visible = new Rect(control.Bounds.Size).TransformToAABB(transform.Value)
            .Intersect(new Rect(boundary.Bounds.Size));
        foreach (var ancestor in control.GetVisualAncestors().Prepend(control))
        {
            if (!ancestor.IsVisible || ancestor.Opacity <= 0)
                return false;
            if (ancestor.ClipToBounds || ancestor is ScrollContentPresenter)
            {
                var clipTransform = ancestor.TransformToVisual(boundary);
                if (clipTransform is null)
                    return false;
                visible = visible.Intersect(new Rect(ancestor.Bounds.Size).TransformToAABB(clipTransform.Value));
            }
            if (ReferenceEquals(ancestor, boundary))
                break;
        }
        return visible.Width > 0 && visible.Height > 0;
    }
}
