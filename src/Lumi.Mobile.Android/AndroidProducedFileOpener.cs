using Android.Content;
using Android.Webkit;
using AndroidX.Core.Content;
using Lumi.Mobile.Services;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.Android;

internal sealed class AndroidProducedFileOpener(Context context) : IProducedFileOpener
{
    private readonly Context _context = context.ApplicationContext ?? context;

    public bool CanShare => true;

    public async Task<bool> TryOpenAsync(
        string downloadedPath,
        string displayName,
        CancellationToken cancellationToken)
    {
        if (await CopyToSharedCacheAsync(downloadedPath, displayName, cancellationToken) is not { } shared)
            return false;

        try
        {
            var viewIntent = new Intent(Intent.ActionView)
                .SetDataAndType(shared.Uri, shared.MimeType)
                .AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
            if (_context.PackageManager is not { } packageManager
                || viewIntent.ResolveActivity(packageManager) is null)
            {
                File.Delete(shared.Path);
                return false;
            }

            var chooser = Intent.CreateChooser(viewIntent, Strings.Get("OpenWith"));
            if (chooser is null)
                return false;
            chooser.AddFlags(ActivityFlags.NewTask);
            _context.StartActivity(chooser);
            return true;
        }
        catch (ActivityNotFoundException)
        {
            File.Delete(shared.Path);
            return false;
        }
    }

    /// <summary>Hands the file to the share sheet: messages, mail, Drive, Nearby Share and the rest.</summary>
    public async Task<bool> TryShareAsync(
        string downloadedPath,
        string displayName,
        CancellationToken cancellationToken)
    {
        if (await CopyToSharedCacheAsync(downloadedPath, displayName, cancellationToken) is not { } shared)
            return false;

        try
        {
            var send = new Intent(Intent.ActionSend)
                .SetType(shared.MimeType)
                .PutExtra(Intent.ExtraStream, shared.Uri)
                .AddFlags(ActivityFlags.GrantReadUriPermission);
            // The chooser grants the read permission onward only for a URI it finds in the clip data.
            send.ClipData = ClipData.NewRawUri(displayName, shared.Uri);
            if (Intent.CreateChooser(send, (string?)null) is not { } chooser)
                return false;

            chooser.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
            _context.StartActivity(chooser);
            return true;
        }
        catch (ActivityNotFoundException)
        {
            File.Delete(shared.Path);
            return false;
        }
    }

    /// <summary>
    /// Copies the file into the cache folder the FileProvider serves, under its own name, so the app that
    /// receives it sees "report.pdf" rather than a download's internal name.
    /// </summary>
    private async Task<SharedFile?> CopyToSharedCacheAsync(
        string downloadedPath,
        string displayName,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(downloadedPath) || _context.CacheDir?.AbsolutePath is not { } cacheRoot)
            return null;

        var folder = Path.Combine(cacheRoot, "produced-files", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        PruneOldFiles(Path.Combine(cacheRoot, "produced-files"));

        var safeName = SafeFileName(displayName);
        var target = Path.Combine(folder, safeName);
        try
        {
            await using (var source = File.OpenRead(downloadedPath))
            await using (var destination = new FileStream(
                             target,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.Read,
                             64 * 1024,
                             FileOptions.Asynchronous))
            {
                await source.CopyToAsync(destination, 64 * 1024, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            if (new FileInfo(target).Length != new FileInfo(downloadedPath).Length)
                throw new IOException("The Android cache copy was incomplete.");

            var uri = FileProvider.GetUriForFile(_context, $"{_context.PackageName}.files", new Java.IO.File(target));
            var mimeType = MimeTypeMap.Singleton?.GetMimeTypeFromExtension(
                               Path.GetExtension(safeName).TrimStart('.').ToLowerInvariant())
                           ?? "application/octet-stream";
            return uri is null ? null : new SharedFile(target, uri, mimeType);
        }
        catch
        {
            if (File.Exists(target))
                File.Delete(target);
            throw;
        }
    }

    private static string SafeFileName(string displayName)
    {
        var fileName = Path.GetFileName(displayName);
        foreach (var invalid in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(fileName) ? "Lumi-file" : fileName;
    }

    /// <summary>Copies handed to other apps live a day, long enough for any of them to read it.</summary>
    private static void PruneOldFiles(string folder)
    {
        var cutoff = DateTime.UtcNow.AddDays(-1);
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            try
            {
                if (Directory.Exists(entry))
                {
                    if (Directory.GetLastWriteTimeUtc(entry) < cutoff)
                        Directory.Delete(entry, recursive: true);
                }
                else if (File.GetLastWriteTimeUtc(entry) < cutoff)
                {
                    File.Delete(entry);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record SharedFile(string Path, global::Android.Net.Uri Uri, string MimeType);
}
