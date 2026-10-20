using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace Lumi.Services.Discover;

/// <summary>
/// Downloads small images (publisher avatars, plugin logos) once per run. A failed image is
/// remembered as missing, so a broken URL is not retried on every card render.
/// </summary>
public static class RemoteImageCache
{
    private const int MaxBytes = 2 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> Images = new(StringComparer.Ordinal);

    public static Task<Bitmap?> LoadAsync(string? url, int width)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return Task.FromResult<Bitmap?>(null);

        return Images.GetOrAdd($"{width}|{url}", _ => FetchAsync(uri, width));
    }

    /// <summary>GitHub serves avatars at any size; asking for the size shown keeps downloads tiny.</summary>
    public static string? GitHubAvatar(string? owner, int size)
        => string.IsNullOrWhiteSpace(owner) ? null : $"https://github.com/{Uri.EscapeDataString(owner)}.png?size={size}";

    public static string? Sized(string? avatarUrl, int size)
    {
        if (string.IsNullOrWhiteSpace(avatarUrl))
            return null;

        return avatarUrl.Contains("avatars.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            ? avatarUrl + (avatarUrl.Contains('?') ? "&" : "?") + $"s={size}"
            : avatarUrl;
    }

    private static async Task<Bitmap?> FetchAsync(Uri uri, int width)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var response = await DiscoverHttp.Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentLength > MaxBytes
                || response.Content.Headers.ContentType?.MediaType is { } type && type.Contains("svg", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaxBytes)
                return null;

            using var stream = new MemoryStream(bytes);
            return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Images are decoration: an unreachable or undecodable one leaves the letter tile.
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>A gallery asks for dozens of avatars at once; a few at a time keeps the UI's own requests snappy.</summary>
    private static readonly System.Threading.SemaphoreSlim Gate = new(6);
}
