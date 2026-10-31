using System.Collections;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Lumi.Mobile.Behaviors;

/// <summary>
/// Gives an items control its items a few at a time: one a frame until they fill the screen and a screen
/// below it, then the rest at once, which a virtualizing panel builds only as they near the screen. A long
/// file appears top first, a section a frame, instead of being built whole in one long frame.
/// </summary>
public sealed class ProgressiveItems
{
    public static readonly AttachedProperty<IEnumerable?> SourceProperty =
        AvaloniaProperty.RegisterAttached<ProgressiveItems, ItemsControl, IEnumerable?>("Source");

    /// <summary>Past this many items a frame, the rest come at once even if the screen has not filled.</summary>
    internal const int MaxProgressiveItems = 24;

    private static readonly ConditionalWeakTable<ItemsControl, Feed> Feeds = new();

    static ProgressiveItems()
    {
        SourceProperty.Changed.AddClassHandler<ItemsControl>(static (control, change) =>
            Start(control, change.GetNewValue<IEnumerable?>()));
    }

    private ProgressiveItems()
    {
    }

    public static IEnumerable? GetSource(ItemsControl control) => control.GetValue(SourceProperty);

    public static void SetSource(ItemsControl control, IEnumerable? value) => control.SetValue(SourceProperty, value);

    private static void Start(ItemsControl control, IEnumerable? source)
    {
        if (Feeds.TryGetValue(control, out var previous))
        {
            previous.IsStopped = true;
            Feeds.Remove(control);
        }

        if (source is null)
        {
            control.ItemsSource = null;
            return;
        }

        var feed = new Feed(control, source.Cast<object?>().ToArray());
        control.ItemsSource = feed.Items;
        if (TopLevel.GetTopLevel(control) is null)
        {
            // Not on screen: nothing builds until it is, and then only what the screen shows.
            feed.AddRemaining();
            return;
        }

        // The first item comes with the next frame, so its build never joins whatever showed the list.
        Feeds.Add(control, feed);
        feed.Schedule();
    }

    private sealed class Feed(ItemsControl control, object?[] all)
    {
        private int _next;
        private int _progressive;

        public ObservableCollection<object?> Items { get; } = [];

        public bool IsStopped { get; set; }

        public void AddNext()
        {
            if (_next < all.Length)
                Items.Add(all[_next++]);
        }

        public void AddRemaining()
        {
            while (_next < all.Length)
                Items.Add(all[_next++]);
        }

        public void Schedule()
        {
            if (_next >= all.Length || TopLevel.GetTopLevel(control) is not { } topLevel)
            {
                AddRemaining();
                Feeds.Remove(control);
                return;
            }

            topLevel.RequestAnimationFrame(_ => Tick());
        }

        private void Tick()
        {
            if (IsStopped)
                return;

            // The items so far reach a screen below the viewport: what is left is built as it nears it.
            var scroll = control.FindAncestorOfType<ScrollViewer>();
            var filled = scroll is { Viewport.Height: > 0 }
                         && scroll.Extent.Height >= scroll.Offset.Y + scroll.Viewport.Height * 2;
            if (filled || ++_progressive >= MaxProgressiveItems)
            {
                AddRemaining();
                Feeds.Remove(control);
                return;
            }

            AddNext();
            Schedule();
        }
    }
}
