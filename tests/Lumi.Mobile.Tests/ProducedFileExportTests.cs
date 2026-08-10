using Lumi.Mobile.Services;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class ProducedFileExportTests
{
    [Fact]
    public async Task ExportCopiesEveryByteAndVerifiesTheDestination()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"lumi-source-{Guid.NewGuid():N}.bin");
        var destinationPath = Path.Combine(Path.GetTempPath(), $"lumi-destination-{Guid.NewGuid():N}.bin");
        var expected = Enumerable.Range(0, 257_123).Select(index => (byte)(index % 251)).ToArray();
        await File.WriteAllBytesAsync(sourcePath, expected);

        try
        {
            var copied = await ProducedFileExport.CopyAndVerifyAsync(
                sourcePath,
                () => Task.FromResult<Stream>(
                    File.Open(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None)),
                () => Task.FromResult<Stream>(
                    File.Open(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read)),
                CancellationToken.None);

            Assert.Equal(expected.Length, copied);
            Assert.Equal(expected, await File.ReadAllBytesAsync(destinationPath));
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(destinationPath);
        }
    }

    [Fact]
    public async Task ExportRejectsSameLengthCorruption()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"lumi-source-{Guid.NewGuid():N}.bin");
        var destinationPath = Path.Combine(Path.GetTempPath(), $"lumi-destination-{Guid.NewGuid():N}.bin");
        var expected = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
        await File.WriteAllBytesAsync(sourcePath, expected);

        try
        {
            await Assert.ThrowsAsync<IOException>(() => ProducedFileExport.CopyAndVerifyAsync(
                sourcePath,
                () => Task.FromResult<Stream>(
                    File.Open(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None)),
                () =>
                {
                    var corrupted = File.ReadAllBytes(destinationPath);
                    corrupted[0] ^= 0xFF;
                    File.WriteAllBytes(destinationPath, corrupted);
                    return Task.FromResult<Stream>(
                        File.Open(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read));
                },
                CancellationToken.None));
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(destinationPath);
        }
    }

}
