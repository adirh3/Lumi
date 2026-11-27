using Avalonia;

namespace Lumi.Views;

internal static class WindowPlacement
{
    // Saving divides the frame origin by its host's scale. When the window spans displays,
    // Windows picks that host by the frame's largest overlap, not by its top-left corner.
    internal static PixelPoint? DecodeSavedPosition(Point saved, Size frameSize, double scale, Predicate<PixelRect> isOnScreen)
    {
        var position = new PixelPoint((int)Math.Round(saved.X * scale), (int)Math.Round(saved.Y * scale));
        return isOnScreen(new PixelRect(position, SavedPixels(frameSize, scale))) ? position : null;
    }

    private static PixelSize SavedPixels(Size size, double scale) => new(
        (int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale));

    internal static (Size Size, PixelPoint Position) RestoreNormalBounds(
        Size preferredSize, Size minimumSize, PixelPoint? savedPosition, PixelRect workArea,
        double scaling, Size chrome, IEnumerable<PixelRect> workAreas, Vector clientOffset = default)
    {
        var requested = new Size(Math.Max(preferredSize.Width, minimumSize.Width),
            Math.Max(preferredSize.Height, minimumSize.Height));
        if (savedPosition is { } saved && IsFullyVisible(new PixelRect(
            ClientOrigin(saved, clientOffset, scaling), SavedPixels(requested, scaling)), workAreas))
            return (requested, saved);

        var size = new Size(
            Math.Min(requested.Width, Math.Max(1.0, workArea.Width / scaling - chrome.Width)),
            Math.Min(requested.Height, Math.Max(1.0, workArea.Height / scaling - chrome.Height)));
        var frame = FrameSize(size);
        var position = savedPosition ?? new PixelPoint(
            workArea.X + (workArea.Width - frame.Width) / 2,
            workArea.Y + (workArea.Height - frame.Height) / 2);
        return (size, FitPosition(position, frame, workArea));

        PixelSize FrameSize(Size client) => new(
            (int)Math.Ceiling((client.Width + chrome.Width) * scaling),
            (int)Math.Ceiling((client.Height + chrome.Height) * scaling));
    }

    internal static PixelPoint ClientOrigin(PixelPoint frameOrigin, Vector clientOffset, double scaling) => new(
        frameOrigin.X + (int)Math.Round(clientOffset.X * scaling),
        frameOrigin.Y + (int)Math.Round(clientOffset.Y * scaling));

    internal static PixelPoint FitPosition(PixelPoint position, PixelSize size, PixelRect workArea) => new(
        Math.Clamp(position.X, workArea.X, Math.Max(workArea.X, workArea.Right - size.Width)),
        Math.Clamp(position.Y, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - size.Height)));

    internal static bool IsFullyVisible(PixelRect bounds, IEnumerable<PixelRect> workAreas)
    {
        var overlaps = new List<PixelRect>();
        foreach (var workArea in workAreas)
        {
            if (workArea.Contains(bounds))
                return true;

            var overlap = workArea.Intersect(bounds);
            if (overlap.Width > 0 && overlap.Height > 0)
                overlaps.Add(overlap);
        }
        if (overlaps.Count < 2)
            return false;

        overlaps.Sort(static (a, b) => a.X.CompareTo(b.X));
        // Each horizontal strip between monitor edges must be covered without a gap.
        for (var y = bounds.Y; y < bounds.Bottom;)
        {
            var nextY = bounds.Bottom;
            var right = bounds.X;
            foreach (var area in overlaps)
            {
                if (y < area.Y)
                    nextY = Math.Min(nextY, area.Y);
                else if (y < area.Bottom)
                {
                    nextY = Math.Min(nextY, area.Bottom);
                    if (area.X <= right)
                        right = Math.Max(right, area.Right);
                }
            }
            if (right < bounds.Right)
                return false;
            y = nextY;
        }
        return true;
    }
}
