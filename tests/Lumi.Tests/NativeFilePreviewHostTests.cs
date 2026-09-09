using Avalonia.Controls;
using Avalonia.Threading;
using Lumi.Views;
using Xunit;
#if WINDOWS
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
#endif

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class NativeFilePreviewHostLifecycleTests
{
    [Fact]
    public async Task UnavailableNativeHostReportsFailureAfterAttachment()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var host = new NativeFilePreviewHost("unavailable.preview");
            var failures = new List<string>();
            host.PreviewFailed += failures.Add;
            Assert.Empty(failures);
            var window = new Window { Content = host };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Assert.False(string.IsNullOrWhiteSpace(Assert.Single(failures)));
                host.Dispose();
                host.Dispose();
                window.Content = null;
                Dispatcher.UIThread.RunJobs();
                Assert.Single(failures);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DetachingBeforeInitializationCancelsPendingPreview()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var host = new NativeFilePreviewHost("unavailable.preview");
            var failures = new List<string>();
            host.PreviewFailed += failures.Add;
            var window = new Window { Content = host };
            try
            {
                window.Show();
                window.Content = null;
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(failures);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }
}

#if WINDOWS
public sealed class NativeFilePreviewHostTests
{
    [Fact]
    public void NativeWindowProbeSurfacesAllocationFailure()
    {
        var error = Assert.Throws<Win32Exception>(() => NativeFilePreviewHost.VerifyNativeWindowSupport(0));
        Assert.Contains("could not create the document preview window", error.Message);
    }

    [Fact]
    public void StreamClonesHaveIndependentCursorsAndReportEndOfFile()
    {
        using var source = new PreviewSource([1, 2, 3, 4]);
        var stream = new ReadOnlyPreviewStream(source.File);
        stream.Seek(1, 0, 0);
        stream.Clone(out var clone);

        Assert.Equal(new byte[] { 2, 3 }, Read(clone, 2));
        Assert.Equal(new byte[] { 2, 3, 4 }, Read(stream, 8));
        Assert.Equal(new byte[] { 4 }, Read(clone, 8));
        Assert.Empty(Read(clone, 1));
    }

    [Fact]
    public void StreamIsReadOnlyAndProvidesShellMetadata()
    {
        using var source = new PreviewSource([1, 2, 3]);
        var stream = new ReadOnlyPreviewStream(source.File);

        Assert.Equal(unchecked((int)0x80030005),
            Assert.Throws<COMException>(() => stream.Write([9], 1, 0)).ErrorCode);
        Assert.Equal(unchecked((int)0x80030005),
            Assert.Throws<COMException>(() => stream.SetSize(0)).ErrorCode);
        stream.Stat(out var stat, 0);
        Assert.Equal(2, stat.type);
        Assert.Equal(0, stat.grfMode);
        Assert.Equal(3, stat.cbSize);
        Assert.Equal(source.File.Name, stat.pwcsName);
        stream.Stat(out stat, 1);
        Assert.Null(stat.pwcsName);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(source.Path));
    }

    [Fact]
    public void SeekSupportsRelativeAndEndOffsetsAndRejectsInvalidPositions()
    {
        using var source = new PreviewSource([1, 2, 3, 4]);
        var stream = new ReadOnlyPreviewStream(source.File);
        stream.Seek(-2, 2, 0);
        Assert.Equal(new byte[] { 3 }, Read(stream, 1));
        stream.Seek(-2, 1, 0);
        Assert.Equal(new byte[] { 2 }, Read(stream, 1));
        Assert.Equal(unchecked((int)0x80030001),
            Assert.Throws<COMException>(() => stream.Seek(-1, 0, 0)).ErrorCode);
        Assert.Equal(unchecked((int)0x80030001),
            Assert.Throws<COMException>(() => stream.Seek(0, 3, 0)).ErrorCode);
        stream.Seek(100, 0, 0);
        Assert.Empty(Read(stream, 1));
    }

    [Fact]
    public void OwnerDisposalInvalidatesStreamAndAllClones()
    {
        using var source = new PreviewSource([1]);
        var stream = new ReadOnlyPreviewStream(source.File);
        stream.Clone(out var clone);
        source.File.Dispose();

        Assert.Throws<ObjectDisposedException>(() => Read(stream, 1));
        Assert.Throws<ObjectDisposedException>(() => Read(clone, 1));
    }

    [Fact]
    public void SourceCanBeEditedAndAtomicallyReplacedWhilePreviewIsOpen()
    {
        using var source = new PreviewSource([1, 2, 3]);
        var stream = new ReadOnlyPreviewStream(source.File);
        using (var writer = new FileStream(source.Path, FileMode.Open, FileAccess.Write,
                   FileShare.ReadWrite | FileShare.Delete))
            writer.WriteByte(4);

        var replacement = source.Path + ".replacement";
        try
        {
            File.WriteAllBytes(replacement, [8, 9]);
            File.Replace(replacement, source.Path, destinationBackupFileName: null);
            Assert.Equal(new byte[] { 8, 9 }, File.ReadAllBytes(source.Path));
            Assert.Equal(new byte[] { 4, 2, 3 }, Read(stream, 3));
        }
        finally
        {
            File.Delete(replacement);
        }
    }

    [Fact]
    public void ComStreamAndPreviewFrameInterfacesAreVisibleToNativeHandlers()
    {
        using var source = new PreviewSource([1]);
        AssertComInterface(new ReadOnlyPreviewStream(source.File), typeof(IStream).GUID);
        var siteType = typeof(NativeFilePreviewHost)
            .GetNestedType("PreviewHandlerSite", BindingFlags.NonPublic)!;
        var site = Activator.CreateInstance(siteType, nonPublic: true)!;
        AssertComInterface(site, new Guid("FEC87AAF-35F9-447A-ADB7-20234491401A"));
        Assert.Equal(IntPtr.Size == 8 ? 16 : 8,
            Marshal.SizeOf<NativeFilePreviewHost.PreviewHandlerFrameInfo>());
        Assert.Equal((nint)IntPtr.Size, Marshal.OffsetOf<NativeFilePreviewHost.PreviewHandlerFrameInfo>(
            nameof(NativeFilePreviewHost.PreviewHandlerFrameInfo.AcceleratorCount)));
    }

    [Fact]
    public void CopyToNativeStreamCopiesMultipleChunksAndReportsActualCounts()
    {
        var bytes = Enumerable.Range(0, 70_000).Select(i => (byte)i).ToArray();
        using var source = new PreviewSource(bytes);
        var stream = new ReadOnlyPreviewStream(source.File);
        Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(0, true, out var target));
        var counts = Marshal.AllocCoTaskMem(16);
        try
        {
            stream.CopyTo(target, bytes.Length + 10, counts, counts + 8);
            Assert.Equal(bytes.Length, Marshal.ReadInt64(counts));
            Assert.Equal(bytes.Length, Marshal.ReadInt64(counts + 8));
            target.Seek(0, 0, 0);
            Assert.Equal(bytes, Read(target, bytes.Length));
        }
        finally
        {
            Marshal.FreeCoTaskMem(counts);
            Marshal.ReleaseComObject(target);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(".lumi-no-preview-4693b061")]
    public void MissingAssociationIsExplicitlyUnsupported(string extension)
    {
        Assert.Throws<NotSupportedException>(() => NativeFilePreviewHost.FindPreviewHandler(extension));
    }

    private static byte[] Read(IStream stream, int count)
    {
        var read = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            var buffer = new byte[count];
            stream.Read(buffer, count, read);
            return buffer[..Marshal.ReadInt32(read)];
        }
        finally
        {
            Marshal.FreeCoTaskMem(read);
        }
    }

    private static void AssertComInterface(object value, Guid iid)
    {
        var unknown = Marshal.GetIUnknownForObject(value);
        nint result = 0;
        try
        {
            Assert.Equal(0, Marshal.QueryInterface(unknown, in iid, out result));
            Assert.NotEqual(0, result);
        }
        finally
        {
            if (result != 0)
                Marshal.Release(result);
            Marshal.Release(unknown);
        }
    }

    private sealed class PreviewSource : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"lumi-preview-{Guid.NewGuid():N}.bin");
        public FileStream File { get; }

        public PreviewSource(byte[] bytes)
        {
            System.IO.File.WriteAllBytes(Path, bytes);
            File = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        public void Dispose()
        {
            File.Dispose();
            System.IO.File.Delete(Path);
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CreateStreamOnHGlobal(nint memory,
        [MarshalAs(UnmanagedType.Bool)] bool deleteOnRelease, out IStream stream);
}
#endif
