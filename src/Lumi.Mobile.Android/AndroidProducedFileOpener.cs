using Android.Content;
using Android.Provider;
using Android.Webkit;
using AndroidX.Core.Content;
using Lumi.Mobile.Services;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.Android;

internal sealed class AndroidProducedFileOpener(Context context) : IProducedFileOpener
{
    private readonly Context _context = context.ApplicationContext ?? context;

    public bool CanShare => true;

    /// <summary>From Android 10 a picture goes into the shared Pictures folder without asking for storage access.</summary>
    public bool CanSaveToPhotos => OperatingSystem.IsAndroidVersionAtLeast(29);

    /// <summary>Saves a picture into Pictures/Lumi, where the gallery and Google Photos find it.</summary>
    public async Task<bool> TrySaveToPhotosAsync(
        string downloadedPath,
        string displayName,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29)
            || !File.Exists(downloadedPath)
            || _context.ContentResolver is not { } resolver
            || MediaStore.Images.Media.GetContentUri(MediaStore.VolumeExternalPrimary) is not { } collection)
        {
            return false;
        }

        var safeName = SafeFileName(displayName);
        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, safeName);
        values.Put(MediaStore.IMediaColumns.MimeType, MimeTypeOf(safeName));
        values.Put(MediaStore.IMediaColumns.RelativePath, Path.Combine(global::Android.OS.Environment.DirectoryPictures ?? "Pictures", "Lumi"));
        values.Put(MediaStore.IMediaColumns.IsPending, 1);
        if (resolver.Insert(collection, values) is not { } item)
            return false;

        try
        {
            await using (var source = File.OpenRead(downloadedPath))
            await using (var destination = resolver.OpenOutputStream(item)
                                           ?? throw new IOException("The gallery did not open the new picture for writing."))
            {
                await source.CopyToAsync(destination, 64 * 1024, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            // Until it is no longer pending, the picture is hidden from every other app.
            values.Clear();
            values.Put(MediaStore.IMediaColumns.IsPending, 0);
            resolver.Update(item, values, null, null);
            return true;
        }
        catch
        {
            resolver.Delete(item, null, null);
            throw;
        }
    }

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
            return uri is null ? null : new SharedFile(target, uri, MimeTypeOf(safeName));
        }
        catch
        {
            if (File.Exists(target))
                File.Delete(target);
            throw;
        }
    }

    private static string MimeTypeOf(string fileName) =>
        MimeTypeMap.Singleton?.GetMimeTypeFromExtension(Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant())
        ?? "application/octet-stream";

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
