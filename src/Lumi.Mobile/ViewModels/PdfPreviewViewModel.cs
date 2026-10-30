using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.Mobile.Services;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.ViewModels;

/// <summary>
/// A PDF's pages, each drawn only while it is on screen: a page that scrolls away gives its bitmap
/// back, so a long document costs a few pages of memory rather than all of them.
/// </summary>
public sealed class PdfPreviewViewModel : IDisposable
{
    private readonly IPdfPreviewDocument _document;
    private bool _disposed;

    private PdfPreviewViewModel(IPdfPreviewDocument document)
    {
        _document = document;
        Pages = Enumerable.Range(0, document.PageCount)
            .Select(index => new PdfPageViewModel(document, index, document.GetPageSize(index)))
            .ToArray();
    }

    public IReadOnlyList<PdfPageViewModel> Pages { get; }

    public int TotalPageCount => _document.TotalPageCount;

    /// <summary>The document is longer than the preview shows.</summary>
    public bool IsLimited => TotalPageCount > Pages.Count;

    public string PageCountText => Strings.Get(TotalPageCount == 1 ? "PdfPagesOne" : "PdfPagesMany", TotalPageCount);

    public static async Task<PdfPreviewViewModel> OpenAsync(
        IPdfPreviewRenderer renderer, string path, CancellationToken cancellationToken)
    {
        var document = await renderer.OpenAsync(path, cancellationToken).ConfigureAwait(true);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new PdfPreviewViewModel(document);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var page in Pages)
            page.Close();
        _document.Dispose();
    }
}

public sealed partial class PdfPageViewModel : ObservableObject
{
    private readonly IPdfPreviewDocument _document;
    private readonly int _index;
    private CancellationTokenSource? _render;
    private int _requestedWidth;
    private int _renderedWidth;
    private bool _closed;

    internal PdfPageViewModel(IPdfPreviewDocument document, int index, Avalonia.Size pageSize)
    {
        _document = document;
        _index = index;
        AspectRatio = pageSize.Width > 0 && pageSize.Height > 0
            ? Math.Clamp(pageSize.Height / pageSize.Width, 0.1, 10)
            : Math.Sqrt(2);
    }

    public int Number => _index + 1;

    /// <summary>The page's height over its width, so it keeps its shape before it is drawn.</summary>
    public double AspectRatio { get; }

    public string AccessibleName => Strings.Get("PdfPageOf", Number, _document.TotalPageCount);

    [ObservableProperty] private Bitmap? _image;

    [ObservableProperty] private bool _hasFailed;

    /// <summary>
    /// Draws the page <paramref name="pixelWidth"/> pixels wide, unless what it already shows is about
    /// as sharp. A newer request replaces one still drawing.
    /// </summary>
    public async void Request(int pixelWidth)
    {
        if (_closed || pixelWidth <= 0)
            return;

        pixelWidth = Math.Min(pixelWidth, PdfPreviewLimits.MaxPixelWidth);
        if (Image is not null && pixelWidth <= _renderedWidth * 1.15)
            return;
        if (_render is not null && pixelWidth <= _requestedWidth * 1.15)
            return;

        _render?.Cancel();
        var render = _render = new CancellationTokenSource();
        _requestedWidth = pixelWidth;
        try
        {
            var bitmap = await _document.RenderPageAsync(_index, pixelWidth, render.Token).ConfigureAwait(true);
            if (_closed || render.IsCancellationRequested || !ReferenceEquals(_render, render))
            {
                bitmap.Dispose();
                return;
            }

            var previous = Image;
            Image = bitmap;
            _renderedWidth = pixelWidth;
            HasFailed = false;
            // Only once the page shows the new bitmap is the old one let go.
            previous?.Dispose();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!_closed && ReferenceEquals(_render, render))
                HasFailed = true;
            Trace.TraceWarning($"[Mobile] PDF page {Number} could not be drawn: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_render, render))
            {
                _render = null;
                _requestedWidth = 0;
            }
            render.Dispose();
        }
    }

    /// <summary>The page left the screen: its bitmap goes until it is seen again.</summary>
    public void Release()
    {
        _render?.Cancel();
        _render = null;
        _requestedWidth = 0;
        _renderedWidth = 0;
        var previous = Image;
        Image = null;
        previous?.Dispose();
    }

    internal void Close()
    {
        _closed = true;
        Release();
    }
}
