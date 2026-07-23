using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Lumi.Services;

/// <summary>
/// Extracts OS file-type icons via the Windows Shell API and converts them
/// to Avalonia bitmaps. For image files, generates a thumbnail from the file
/// content instead. Results are cached by file extension (icons) or full path (thumbnails).
/// </summary>
internal static class FileIconHelper
{
    internal const int IconCacheCapacity = 128;
    internal const int ThumbnailCacheCapacity = 64;

    private static readonly WeakBitmapCache IconCache =
        new(IconCacheCapacity, StringComparer.OrdinalIgnoreCase);
    private static readonly WeakBitmapCache ThumbnailCache =
        new(ThumbnailCacheCapacity, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tiff", ".tif"
    };

    public static Avalonia.Media.Imaging.Bitmap? GetFileIcon(string filePath)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var ext = Path.GetExtension(filePath)?.ToLowerInvariant() ?? "";

        // For image files, generate a thumbnail from the file content
        if (ImageExtensions.Contains(ext) && File.Exists(filePath))
        {
            var thumbnail = ThumbnailCache.GetOrCreate(filePath, LoadThumbnail);
            if (thumbnail is not null)
                return thumbnail;
        }

        return IconCache.GetOrCreate(ext, GetIconForExtension);
    }

    private static Avalonia.Media.Imaging.Bitmap? LoadThumbnail(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var full = new Avalonia.Media.Imaging.Bitmap(stream);
            // Decode at a small size to save memory (max 32px on longest side)
            var maxDim = Math.Max(full.PixelSize.Width, full.PixelSize.Height);
            if (maxDim <= 32)
                return full;

            try
            {
                stream.Seek(0, SeekOrigin.Begin);
                var targetWidth = Math.Max(
                    1,
                    (int)Math.Round(full.PixelSize.Width * (32d / maxDim)));
                return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, targetWidth);
            }
            finally
            {
                full.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    internal static (int IconEntries, int ThumbnailEntries) CaptureCacheDiagnostics()
        => (IconCache.Count, ThumbnailCache.Count);

    internal static void ClearCachesForTests()
    {
        IconCache.Clear();
        ThumbnailCache.Clear();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416")]
    private static Avalonia.Media.Imaging.Bitmap? GetIconForExtension(string ext)
    {
        try
        {
            var shfi = new SHFILEINFO();
            var result = SHGetFileInfo(
                $"*{ext}",
                FILE_ATTRIBUTE_NORMAL,
                ref shfi,
                (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_ICON | SHGFI_USEFILEATTRIBUTES);

            if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
                return null;

            try
            {
                using var icon = Icon.FromHandle(shfi.hIcon);
                using var bmp = icon.ToBitmap();
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                ms.Seek(0, SeekOrigin.Begin);
                return new Avalonia.Media.Imaging.Bitmap(ms);
            }
            finally
            {
                DestroyIcon(shfi.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    // ── P/Invoke ──

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbSizeFileInfo,
        uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// Keeps a bounded set of reusable lookup keys without making the cache the lifetime owner of
    /// native-backed bitmaps. An evicted or collected bitmap is recreated on the next request.
    /// </summary>
    private sealed class WeakBitmapCache
    {
        private readonly int _capacity;
        private readonly object _gate = new();
        private readonly Dictionary<string, CacheEntry> _entries;
        private readonly LinkedList<string> _lru = new();

        private sealed record CacheEntry(
            WeakReference<Avalonia.Media.Imaging.Bitmap> Reference,
            LinkedListNode<string> Node);

        public WeakBitmapCache(int capacity, IEqualityComparer<string> comparer)
        {
            _capacity = capacity;
            _entries = new Dictionary<string, CacheEntry>(comparer);
        }

        public int Count
        {
            get
            {
                lock (_gate)
                    return _entries.Count;
            }
        }

        public Avalonia.Media.Imaging.Bitmap? GetOrCreate(
            string key,
            Func<string, Avalonia.Media.Imaging.Bitmap?> factory)
        {
            lock (_gate)
            {
                if (TryGetAliveValue(key, out var cached))
                    return cached;
            }

            var created = factory(key);
            if (created is null)
                return null;

            lock (_gate)
            {
                if (TryGetAliveValue(key, out var cached))
                {
                    created.Dispose();
                    return cached;
                }

                RemoveEntry(key);
                var node = _lru.AddLast(key);
                _entries[key] = new CacheEntry(
                    new WeakReference<Avalonia.Media.Imaging.Bitmap>(created),
                    node);
                Trim();
                return created;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
                _lru.Clear();
            }
        }

        private bool TryGetAliveValue(
            string key,
            out Avalonia.Media.Imaging.Bitmap? bitmap)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.Reference.TryGetTarget(out bitmap))
                {
                    _lru.Remove(entry.Node);
                    _lru.AddLast(entry.Node);
                    return true;
                }

                RemoveEntry(key);
            }

            bitmap = null;
            return false;
        }

        private void Trim()
        {
            while (_entries.Count > _capacity && _lru.First is { } oldest)
            {
                _lru.RemoveFirst();
                _entries.Remove(oldest.Value);
            }
        }

        private void RemoveEntry(string key)
        {
            if (!_entries.Remove(key, out var entry))
                return;

            _lru.Remove(entry.Node);
        }
    }
}
