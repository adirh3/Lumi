using Android.Content;
using Android.Webkit;
using AndroidX.Core.Content;
using Lumi.Mobile.Services;

namespace Lumi.Mobile.Android;

internal sealed class AndroidProducedFileOpener(Context context) : IProducedFileOpener
{
    private readonly Context _context = context.ApplicationContext ?? context;

    public async Task<bool> TryOpenAsync(
        string downloadedPath,
        string displayName,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(downloadedPath) || _context.CacheDir?.AbsolutePath is not { } cacheRoot)
            return false;

        var folder = Path.Combine(cacheRoot, "produced-files");
        Directory.CreateDirectory(folder);
        PruneOldFiles(folder);

        var safeName = SafeFileName(displayName);
        var target = Path.Combine(folder, $"{Guid.NewGuid():N}-{safeName}");
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

            var authority = $"{_context.PackageName}.files";
            var uri = FileProvider.GetUriForFile(
                _context,
                authority,
                new Java.IO.File(target));
            var mimeType = MimeTypeMap.Singleton?.GetMimeTypeFromExtension(
                               Path.GetExtension(safeName).TrimStart('.').ToLowerInvariant())
                           ?? "application/octet-stream";
            var viewIntent = new Intent(Intent.ActionView)
                .SetDataAndType(uri, mimeType)
                .AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
            if (_context.PackageManager is not { } packageManager
                || viewIntent.ResolveActivity(packageManager) is null)
            {
                File.Delete(target);
                return false;
            }

            var chooser = Intent.CreateChooser(viewIntent, "Open with");
            if (chooser is null)
                return false;
            chooser.AddFlags(ActivityFlags.NewTask);
            _context.StartActivity(chooser);
            return true;
        }
        catch (ActivityNotFoundException)
        {
            File.Delete(target);
            return false;
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

    private static void PruneOldFiles(string folder)
    {
        var cutoff = DateTime.UtcNow.AddDays(-1);
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
