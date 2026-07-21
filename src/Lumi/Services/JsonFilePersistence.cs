using System.Diagnostics;
using System.Text.Json;
using Lumi.Models;

namespace Lumi.Services;

internal enum AppDataReadStatus
{
    Missing,
    Valid,
    Malformed
}

internal readonly record struct AppDataReadResult(AppDataReadStatus Status, AppData? Data)
{
    public static AppDataReadResult Missing => new(AppDataReadStatus.Missing, null);
    public static AppDataReadResult Malformed => new(AppDataReadStatus.Malformed, null);
}

internal static class JsonFilePersistence
{
    private const int BufferSize = 81920;
    private const string TempMarker = ".lumi-";

    internal static string GetBackupPath(string dataFile)
        => $"{Path.GetFullPath(dataFile)}.bak";

    internal static AppDataReadResult ReadPrimaryAppData(string dataFile)
        => ReadAppData(Path.GetFullPath(dataFile));

    internal static AppData LoadAppData(string dataFile)
    {
        dataFile = Path.GetFullPath(dataFile);
        CleanupHelperTemps(dataFile);

        var primary = ReadAppData(dataFile);
        if (primary.Status == AppDataReadStatus.Valid)
            return primary.Data!;

        var backupFile = GetBackupPath(dataFile);
        var backup = ReadAppData(backupFile);
        if (backup.Status == AppDataReadStatus.Valid)
        {
            if (primary.Status == AppDataReadStatus.Malformed)
                Quarantine(dataFile);

            CopyFileAtomically(backupFile, dataFile);
            return backup.Data!;
        }

        if (primary.Status == AppDataReadStatus.Malformed)
            Quarantine(dataFile);
        if (backup.Status == AppDataReadStatus.Malformed)
            Quarantine(backupFile);

        return new AppData();
    }

    internal static async Task SaveAppDataAsync(
        string dataFile,
        AppData data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        dataFile = Path.GetFullPath(dataFile);
        Directory.CreateDirectory(Path.GetDirectoryName(dataFile)!);
        CleanupHelperTemps(dataFile);
        cancellationToken.ThrowIfCancellationRequested();

        var dataTemp = CreateTempPath(dataFile);
        string? backupTemp = null;
        try
        {
            await WriteJsonTempAsync(dataTemp, data, cancellationToken).ConfigureAwait(false);

            var primary = ReadAppData(dataFile);
            if (primary.Status == AppDataReadStatus.Valid)
            {
                backupTemp = CreateTempPath(GetBackupPath(dataFile));
                await CopyToTempAsync(dataFile, backupTemp, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (backupTemp is not null)
            {
                Publish(backupTemp, GetBackupPath(dataFile));
                backupTemp = null;
            }

            Publish(dataTemp, dataFile);
            dataTemp = null;
        }
        finally
        {
            TryDelete(dataTemp);
            TryDelete(backupTemp);
        }
    }

    private static AppDataReadResult ReadAppData(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                BufferSize,
                FileOptions.SequentialScan);
            using var document = JsonDocument.Parse(stream);
            ValidateRoot(document.RootElement);

            var data = document.RootElement.Deserialize(AppDataJsonContext.Default.AppData);
            return data is null
                ? AppDataReadResult.Malformed
                : new AppDataReadResult(AppDataReadStatus.Valid, data);
        }
        catch (FileNotFoundException)
        {
            return AppDataReadResult.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return AppDataReadResult.Missing;
        }
        catch (JsonException)
        {
            return AppDataReadResult.Malformed;
        }
        catch (InvalidDataException)
        {
            return AppDataReadResult.Malformed;
        }
    }

    private static void ValidateRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("App data must have an object root.");

        RequireProperty(root, "settings", JsonValueKind.Object);
        RequireProperty(root, "chats", JsonValueKind.Array);
        RequireProperty(root, "projects", JsonValueKind.Array);
        RequireProperty(root, "skills", JsonValueKind.Array);
        RequireProperty(root, "agents", JsonValueKind.Array);
        RequireProperty(root, "memories", JsonValueKind.Array);
        ValidateOptionalArray(root, "mcpServers");
        ValidateOptionalArray(root, "backgroundJobs");
    }

    private static void RequireProperty(
        JsonElement root,
        string name,
        JsonValueKind expectedKind)
    {
        if (!root.TryGetProperty(name, out var property)
            || property.ValueKind != expectedKind)
        {
            throw new InvalidDataException(
                $"App data property '{name}' must be a {expectedKind}.");
        }
    }

    private static void ValidateOptionalArray(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var property)
            && property.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"App data property '{name}' must be an array.");
        }
    }

    private static async Task WriteJsonTempAsync(
        string tempFile,
        AppData data,
        CancellationToken cancellationToken)
    {
        await using var stream = CreateWriteStream(tempFile, asynchronous: true);
        await JsonSerializer.SerializeAsync(
            stream,
            data,
            AppDataJsonContext.Default.AppData,
            cancellationToken).ConfigureAwait(false);
        await FlushAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyToTempAsync(
        string sourceFile,
        string tempFile,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourceFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = CreateWriteStream(tempFile, asynchronous: true);
        await source.CopyToAsync(destination, BufferSize, cancellationToken).ConfigureAwait(false);
        await FlushAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static async Task FlushAsync(FileStream stream, CancellationToken cancellationToken)
    {
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        stream.Flush(flushToDisk: true);
    }

    private static void CopyFileAtomically(string sourceFile, string destinationFile)
    {
        var tempFile = CreateTempPath(destinationFile);
        try
        {
            using (var source = new FileStream(
                sourceFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                BufferSize,
                FileOptions.SequentialScan))
            using (var destination = CreateWriteStream(tempFile, asynchronous: false))
            {
                source.CopyTo(destination, BufferSize);
                destination.Flush(flushToDisk: true);
            }

            Publish(tempFile, destinationFile);
            tempFile = null;
        }
        finally
        {
            TryDelete(tempFile);
        }
    }

    private static FileStream CreateWriteStream(string path, bool asynchronous)
        => new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.WriteThrough
            | (asynchronous ? FileOptions.Asynchronous : FileOptions.None));

    private static void Publish(string tempFile, string destinationFile)
        => File.Move(tempFile, destinationFile, overwrite: true);

    private static void Quarantine(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var fileName = Path.GetFileName(path);
        var quarantine = Path.Combine(
            directory,
            $"{fileName}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
        File.Move(path, quarantine);
    }

    private static string CreateTempPath(string destinationFile)
    {
        var directory = Path.GetDirectoryName(destinationFile)!;
        var fileName = Path.GetFileName(destinationFile);
        return Path.Combine(
            directory,
            $".{fileName}{TempMarker}{Guid.NewGuid():N}.tmp");
    }

    private static void CleanupHelperTemps(string dataFile)
    {
        var directory = Path.GetDirectoryName(dataFile)!;
        if (!Directory.Exists(directory))
            return;

        DeleteMatchingTemps(directory, Path.GetFileName(dataFile));
        DeleteMatchingTemps(directory, Path.GetFileName(GetBackupPath(dataFile)));
    }

    private static void DeleteMatchingTemps(string directory, string fileName)
    {
        var prefix = $".{fileName}{TempMarker}";
        foreach (var tempFile in Directory.EnumerateFiles(directory))
        {
            var candidate = Path.GetFileName(tempFile);
            if (candidate.StartsWith(prefix, StringComparison.Ordinal)
                && candidate.EndsWith(".tmp", StringComparison.Ordinal))
            {
                TryDelete(tempFile);
            }
        }
    }

    private static void TryDelete(string? path)
    {
        if (path is null)
            return;

        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            Trace.TraceWarning($"[Lumi] Could not delete persistence temp '{path}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Trace.TraceWarning($"[Lumi] Could not delete persistence temp '{path}': {ex.Message}");
        }
    }
}
