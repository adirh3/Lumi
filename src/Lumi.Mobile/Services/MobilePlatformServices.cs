using System.Security.Cryptography;

namespace Lumi.Mobile.Services;

public interface IProducedFileOpener
{
    Task<bool> TryOpenAsync(
        string downloadedPath,
        string displayName,
        CancellationToken cancellationToken);
}

public static class MobilePlatformServices
{
    public static IProducedFileOpener ProducedFileOpener { get; set; } =
        new DefaultProducedFileOpener();
}

internal static class ProducedFileExport
{
    public static async Task<long> CopyAndVerifyAsync(
        string sourcePath,
        Func<Task<Stream>> openWriteAsync,
        Func<Task<Stream>> openReadAsync,
        CancellationToken cancellationToken)
    {
        var expectedLength = new FileInfo(sourcePath).Length;
        byte[] expectedHash;
        var buffer = new byte[64 * 1024];
        using var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = File.OpenRead(sourcePath))
        await using (var output = await openWriteAsync())
        {
            if (output.CanSeek)
                output.SetLength(0);

            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;

                sourceHash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            await output.FlushAsync(cancellationToken);
            expectedHash = sourceHash.GetHashAndReset();
        }

        long actualLength = 0;
        byte[] actualHash;
        using var destinationHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var verification = await openReadAsync())
        {
            while (true)
            {
                var read = await verification.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;

                destinationHash.AppendData(buffer, 0, read);
                actualLength += read;
            }

            actualHash = destinationHash.GetHashAndReset();
        }

        if (actualLength != expectedLength
            || !CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
        {
            throw new IOException(
                $"The saved file did not match the download ({actualLength} of {expectedLength} bytes).");
        }

        return actualLength;
    }
}

internal sealed class DefaultProducedFileOpener : IProducedFileOpener
{
    public Task<bool> TryOpenAsync(
        string downloadedPath,
        string displayName,
        CancellationToken cancellationToken) =>
        Task.FromResult(false);
}
