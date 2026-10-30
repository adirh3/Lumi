using Android.Graphics;
using Android.Graphics.Pdf;
using Android.OS;
using Avalonia;
using Avalonia.Platform;
using Lumi.Mobile.Services;
using AndroidBitmap = Android.Graphics.Bitmap;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;
using AvaloniaPixelFormat = Avalonia.Platform.PixelFormat;
using Size = Avalonia.Size;

namespace Lumi.Mobile.Android;

/// <summary>
/// Draws a PDF's pages with Android's own renderer, so a report Lumi made reads in place on the phone.
/// The renderer draws one page at a time, off the UI thread, on white.
/// </summary>
internal sealed class AndroidPdfPreviewRenderer : IPdfPreviewRenderer
{
    /// <summary>A page larger than this is drawn smaller, so a tall poster cannot exhaust memory.</summary>
    private const long MaxPagePixels = 2048L * 4096;

    public bool IsAvailable => true;

    public Task<IPdfPreviewDocument> OpenAsync(string path, CancellationToken cancellationToken) =>
        Task.Run<IPdfPreviewDocument>(() => new Document(path), cancellationToken);

    private sealed class Document : IPdfPreviewDocument
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ParcelFileDescriptor _descriptor;
        private readonly PdfRenderer _renderer;
        private readonly Size[] _pageSizes;
        private bool _disposed;
        private bool _closed;

        public Document(string path)
        {
            _descriptor = ParcelFileDescriptor.Open(new Java.IO.File(path), ParcelFileMode.ReadOnly)
                          ?? throw new IOException("The PDF could not be opened.");
            try
            {
                _renderer = new PdfRenderer(_descriptor);
            }
            catch
            {
                _descriptor.Close();
                throw;
            }

            try
            {
                TotalPageCount = _renderer.PageCount;
                _pageSizes = new Size[Math.Min(TotalPageCount, PdfPreviewLimits.MaxPages)];
                for (var index = 0; index < _pageSizes.Length; index++)
                {
                    using var page = _renderer.OpenPage(index);
                    try
                    {
                        _pageSizes[index] = new Size(page.Width, page.Height);
                    }
                    finally
                    {
                        page.Close();
                    }
                }
            }
            catch
            {
                _renderer.Close();
                _descriptor.Close();
                throw;
            }
        }

        public int PageCount => _pageSizes.Length;

        public int TotalPageCount { get; }

        public Size GetPageSize(int pageIndex) => _pageSizes[pageIndex];

        public async Task<AvaloniaBitmap> RenderPageAsync(
            int pageIndex, int pixelWidth, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                return await Task.Run(() => Render(pageIndex, pixelWidth), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // A document disposed while its page was drawing closes once the page is done.
                if (_disposed)
                    Close();
                _gate.Release();
            }
        }

        private AvaloniaBitmap Render(int pageIndex, int pixelWidth)
        {
            var size = _pageSizes[pageIndex];
            var width = Math.Clamp(pixelWidth, 1, PdfPreviewLimits.MaxPixelWidth);
            var height = Math.Max(1, (int)Math.Round(width * size.Height / Math.Max(1, size.Width)));
            if ((long)width * height > MaxPagePixels)
            {
                var scale = Math.Sqrt(MaxPagePixels / ((double)width * height));
                width = Math.Max(1, (int)(width * scale));
                height = Math.Max(1, (int)(height * scale));
            }

            using var bitmap = AndroidBitmap.CreateBitmap(width, height, AndroidBitmap.Config.Argb8888!)!;
            // Pages are transparent where nothing is printed; paper is white.
            bitmap.EraseColor(Color.White);
            using (var page = _renderer.OpenPage(pageIndex))
            {
                try
                {
                    page.Render(bitmap, null, null, PdfRenderMode.ForDisplay);
                }
                finally
                {
                    page.Close();
                }
            }

            // ARGB_8888 holds its bytes in RGBA order, premultiplied: Avalonia copies them as they are.
            var pixels = bitmap.LockPixels();
            try
            {
                return new AvaloniaBitmap(
                    AvaloniaPixelFormat.Rgba8888,
                    AlphaFormat.Premul,
                    pixels,
                    new PixelSize(width, height),
                    new Vector(96, 96),
                    bitmap.RowBytes);
            }
            finally
            {
                bitmap.UnlockPixels();
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            // With no page drawing, the renderer closes now; otherwise that page closes it.
            if (!_gate.Wait(0))
                return;
            try
            {
                Close();
            }
            finally
            {
                _gate.Release();
            }
        }

        private void Close()
        {
            if (_closed)
                return;

            _closed = true;
            _renderer.Close();
            _descriptor.Close();
        }
    }
}
