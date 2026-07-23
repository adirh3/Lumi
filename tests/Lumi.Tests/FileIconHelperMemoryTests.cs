using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class FileIconHelperMemoryTests
{
    [SkippableFact]
    public async Task ThumbnailCache_DoesNotRootBitmapAfterConsumerIsReleased()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows shell thumbnail behavior is Windows-only.");
        using var session = HeadlessTestSession.Start();
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "thumbnail.png");
        File.Copy(ResolveLumiIconPath(), path);
        FileIconHelper.ClearCachesForTests();

        try
        {
            WeakReference weakBitmap = null!;
            await session.Dispatch(
                () => weakBitmap = CreateWeakThumbnail(path),
                CancellationToken.None);
            ForceFullGc();

            Assert.False(weakBitmap.IsAlive);
            Assert.Equal(1, FileIconHelper.CaptureCacheDiagnostics().ThumbnailEntries);
        }
        finally
        {
            FileIconHelper.ClearCachesForTests();
            Directory.Delete(directory, recursive: true);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateWeakThumbnail(string path)
    {
        var item = new FileAttachmentItem(path);
        Assert.NotNull(item.IconImage);
        return new WeakReference(item.IconImage);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Lumi-file-icon-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ResolveLumiIconPath()
    {
        foreach (var startPath in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(startPath);
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                    || File.Exists(Path.Combine(directory.FullName, ".git")))
                {
                    var candidate = Path.Combine(directory.FullName, "src", "Lumi", "Assets", "lumi-icon.png");
                    if (File.Exists(candidate))
                        return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException("Could not find src\\Lumi\\Assets\\lumi-icon.png.");
    }

    private static void ForceFullGc()
    {
        for (var pass = 0; pass < 6; pass++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }
}
