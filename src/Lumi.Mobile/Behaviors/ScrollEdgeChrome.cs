using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Lumi.Mobile.Behaviors;

/// <summary>
/// Lets a scroller run underneath floating header and footer chrome. Its content rests clear of both,
/// then passes behind them while scrolling, so the chrome can be translucent glass over an unlimited
/// canvas instead of a slab that clips the list.
/// </summary>
/// <remarks>
/// The scroller's content is the behavior's inset wrapper: its margin is replaced with the chrome
/// heights and tracked as banners or safe areas change, so page content with its own (often
/// resource-bound) margin belongs inside that wrapper. Each chrome element carries the
/// <c>content-under</c> class only while scrolled content is actually behind it, so its glass
/// appears, and spends GPU time, only then.
/// </remarks>
public static class ScrollEdgeChrome
{
    public const string ContentUnderClass = "content-under";

    public static readonly AttachedProperty<Control?> HeaderProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, Control?>("Header", typeof(ScrollEdgeChrome));

    public static readonly AttachedProperty<Control?> FooterProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, Control?>("Footer", typeof(ScrollEdgeChrome));

    private static readonly AttachedProperty<Tracker?> TrackerProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, Tracker?>("Tracker", typeof(ScrollEdgeChrome));

    static ScrollEdgeChrome()
    {
        HeaderProperty.Changed.AddClassHandler<ScrollViewer>((scroll, _) => Track(scroll));
        FooterProperty.Changed.AddClassHandler<ScrollViewer>((scroll, _) => Track(scroll));
    }

    public static Control? GetHeader(ScrollViewer scroll) => scroll.GetValue(HeaderProperty);

    public static void SetHeader(ScrollViewer scroll, Control? value) => scroll.SetValue(HeaderProperty, value);

    public static Control? GetFooter(ScrollViewer scroll) => scroll.GetValue(FooterProperty);

    public static void SetFooter(ScrollViewer scroll, Control? value) => scroll.SetValue(FooterProperty, value);

    private static void Track(ScrollViewer scroll)
    {
        var tracker = scroll.GetValue(TrackerProperty);
        if (tracker is null)
        {
            tracker = new Tracker(scroll);
            scroll.SetValue(TrackerProperty, tracker);
        }

        tracker.Observe(GetHeader(scroll), GetFooter(scroll));
    }

    private sealed class Tracker
    {
        private readonly ScrollViewer _scroll;
        private Control? _header;
        private Control? _footer;
        private Layoutable? _content;
        private double _top;
        private double _bottom;

        public Tracker(ScrollViewer scroll)
        {
            _scroll = scroll;
            scroll.ScrollChanged += (_, _) => UpdateContentUnder();
            scroll.PropertyChanged += (_, change) =>
            {
                if (change.Property == ContentControl.ContentProperty)
                    Update();
            };
        }

        public void Observe(Control? header, Control? footer)
        {
            Swap(ref _header, header);
            Swap(ref _footer, footer);
            Update();
        }

        private void Swap(ref Control? current, Control? next)
        {
            if (ReferenceEquals(current, next))
                return;

            if (current is not null)
            {
                current.SizeChanged -= OnChromeSizeChanged;
                current.PropertyChanged -= OnChromePropertyChanged;
                current.Classes.Set(ContentUnderClass, false);
            }

            current = next;
            if (current is not null)
            {
                current.SizeChanged += OnChromeSizeChanged;
                current.PropertyChanged += OnChromePropertyChanged;
            }
        }

        private void OnChromeSizeChanged(object? sender, SizeChangedEventArgs e) => Update();

        private void OnChromePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.IsVisibleProperty)
                Update();
        }

        private void Update()
        {
            if (!ReferenceEquals(_content, _scroll.Content))
            {
                _content?.ClearValue(Layoutable.MarginProperty);
                _content = _scroll.Content as Layoutable;
                _top = _bottom = 0;
            }

            var top = _header is { IsVisible: true } header ? header.Bounds.Height : 0;
            var bottom = _footer is { IsVisible: true } footer ? footer.Bounds.Height : 0;
            if (_content is not null && (Math.Abs(top - _top) > 0.01 || Math.Abs(bottom - _bottom) > 0.01))
            {
                _top = top;
                _bottom = bottom;
                _content.Margin = new Thickness(0, top, 0, bottom);
            }

            UpdateContentUnder();
        }

        private void UpdateContentUnder()
        {
            var offset = _scroll.Offset.Y;
            var remaining = _scroll.Extent.Height - _scroll.Viewport.Height - offset;
            _header?.Classes.Set(ContentUnderClass, _top > 0 && offset > 0.5);
            _footer?.Classes.Set(ContentUnderClass, _bottom > 0 && remaining > 0.5);
        }
    }
}
