using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Lumi.Mobile.ViewModels;

namespace Lumi.Mobile.Views;

/// <summary>
/// One PDF page: a white sheet that keeps the page's shape before it is drawn, asks for a bitmap as
/// sharp as it shows on screen while it is near the viewport, and gives the bitmap back when it
/// scrolls away, so a long document holds only the pages around the reader.
/// </summary>
public sealed class PdfPageView : Border
{
    /// <summary>How far beyond the viewport a page is still drawn, so scrolling finds it ready.</summary>
    private const double PrefetchScreens = 1;

    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private PdfPageViewModel? _page;
    private Rect _viewport;
    private bool _isNearViewport;

    public PdfPageView()
    {
        Child = _image;
        ClipToBounds = true;
        EffectiveViewportChanged += (_, e) =>
        {
            _viewport = e.EffectiveViewport;
            Evaluate();
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_page is not null)
        {
            _page.PropertyChanged -= OnPagePropertyChanged;
            // The image lets go of the bitmap before the page disposes it.
            _image.Source = null;
            _page.Release();
        }

        _page = DataContext as PdfPageViewModel;
        _isNearViewport = false;
        _image.Source = _page?.Image;
        AutomationProperties.SetName(this, _page?.AccessibleName ?? string.Empty);
        if (_page is not null)
            _page.PropertyChanged += OnPagePropertyChanged;
        InvalidateMeasure();
        Evaluate();
    }

    private void OnPagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PdfPageViewModel.Image) && ReferenceEquals(sender, _page))
            _image.Source = _page?.Image;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 400;
        var size = new Size(width, Math.Round(width * (_page?.AspectRatio ?? Math.Sqrt(2))));
        _image.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _image.Arrange(new Rect(finalSize));
        return finalSize;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_isNearViewport)
            Request();
        else
            Evaluate();
    }

    /// <summary>Draws the page while it is within a screen of the viewport and lets it go beyond that.</summary>
    private void Evaluate()
    {
        var margin = Math.Max(_viewport.Height, Bounds.Height) * PrefetchScreens;
        var near = _page is not null && IsEffectivelyVisible
                   && _viewport.Width > 0 && _viewport.Height > 0 && Bounds.Width > 0
                   && _viewport.Inflate(new Thickness(0, margin)).Intersects(new Rect(Bounds.Size));
        if (near == _isNearViewport)
            return;

        _isNearViewport = near;
        if (near)
            Request();
        else
        {
            _image.Source = null;
            _page?.Release();
        }
    }

    private void Request()
    {
        if (_page is null || Bounds.Width <= 0 || TopLevel.GetTopLevel(this) is not { } topLevel)
            return;
        _page.Request((int)Math.Ceiling(Bounds.Width * topLevel.RenderScaling));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isNearViewport = false;
        _viewport = default;
        _image.Source = null;
        _page?.Release();
    }
}
