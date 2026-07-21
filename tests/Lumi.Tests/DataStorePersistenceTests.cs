using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class DataStorePersistenceTests
{
    [Fact]
    public async Task SaveAppDataAsync_RotatesValidPrimaryToBackup()
    {
        using var directory = new TemporaryDirectory();
        var dataFile = Path.Combine(directory.Path, "data.json");

        await JsonFilePersistence.SaveAppDataAsync(dataFile, CreateData("first"));
        var firstBytes = await File.ReadAllBytesAsync(dataFile);
        await JsonFilePersistence.SaveAppDataAsync(dataFile, CreateData("second"));

        Assert.Equal("second", ReadData(dataFile).Settings.UserName);
        Assert.Equal(
            firstBytes,
            await File.ReadAllBytesAsync(JsonFilePersistence.GetBackupPath(dataFile)));
        Assert.Equal(
            "first",
            ReadData(JsonFilePersistence.GetBackupPath(dataFile)).Settings.UserName);
    }

    [Fact]
    public async Task LoadAppData_ValidPrimaryDoesNotInspectBackup()
    {
        using var directory = new TemporaryDirectory();
        var dataFile = Path.Combine(directory.Path, "data.json");
        await JsonFilePersistence.SaveAppDataAsync(dataFile, CreateData("primary"));
        Directory.CreateDirectory(JsonFilePersistence.GetBackupPath(dataFile));

        var loaded = JsonFilePersistence.LoadAppData(dataFile);

        Assert.Equal("primary", loaded.Settings.UserName);
    }

    [Fact]
    public async Task LoadAppData_MalformedPrimaryRecoversBackupAndRepairsPrimary()
    {
        using var directory = new TemporaryDirectory();
        var dataFile = Path.Combine(directory.Path, "data.json");
        await CreatePrimaryAndBackupAsync(dataFile);
        await File.WriteAllTextAsync(dataFile, """{"settings":""");
        var backupBytes = await File.ReadAllBytesAsync(JsonFilePersistence.GetBackupPath(dataFile));

        var recovered = JsonFilePersistence.LoadAppData(dataFile);

        Assert.Equal("backup", recovered.Settings.UserName);
        Assert.Equal(backupBytes, await File.ReadAllBytesAsync(dataFile));
        Assert.Equal("backup", ReadData(dataFile).Settings.UserName);
        Assert.Single(Directory.EnumerateFiles(directory.Path, "data.json.corrupt-*"));
    }

    [Fact]
    public async Task LoadAppData_MissingPrimaryRecoversBackup()
    {
        using var directory = new TemporaryDirectory();
        var dataFile = Path.Combine(directory.Path, "data.json");
        await JsonFilePersistence.SaveAppDataAsync(dataFile, CreateData("backup"));
        File.Move(dataFile, JsonFilePersistence.GetBackupPath(dataFile));

        var recovered = JsonFilePersistence.LoadAppData(dataFile);

        Assert.Equal("backup", recovered.Settings.UserName);
        Assert.Equal("backup", ReadData(dataFile).Settings.UserName);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.corrupt-*"));
    }

    [Fact]
    public async Task LoadAppData_EmptyObjectRecoversBackup()
    {
        using var directory = new TemporaryDirectory();
        var dataFile = Path.Combine(directory.Path, "data.json");
        await CreatePrimaryAndBackupAsync(dataFile);
        await File.WriteAllTextAsync(dataFile, "{}");

        var recovered = JsonFilePersistence.LoadAppData(dataFile);

        Assert.Equal("backup", recovered.Settings.UserName);
        Assert.Equal("backup", ReadData(dataFile).Settings.UserName);
    }

    [Fact]
    public async Task LoadAppData_MalformedCopiesAreQuarantinedAndFreshDataReturned()
    {
        using var directory = new TemporaryDirectory();
        var dataFile = Path.Combine(directory.Path, "data.json");
        var backupFile = JsonFilePersistence.GetBackupPath(dataFile);
        await File.WriteAllTextAsync(dataFile, "{");
        await File.WriteAllTextAsync(backupFile, "null");

        var recovered = JsonFilePersistence.LoadAppData(dataFile);

        AssertFresh(recovered);
        Assert.False(File.Exists(dataFile));
        Assert.False(File.Exists(backupFile));
        Assert.Single(Directory.EnumerateFiles(directory.Path, "data.json.corrupt-*"));
        Assert.Single(Directory.EnumerateFiles(directory.Path, "data.json.bak.corrupt-*"));
    }

    [Fact]
    public async Task LoadAppData_MalformedPrimaryWithoutBackupStartsFresh()
    {
        using var directory = new TemporaryDirectory();
        var dataFile = Path.Combine(directory.Path, "data.json");
        await File.WriteAllTextAsync(dataFile, "{");

        var recovered = JsonFilePersistence.LoadAppData(dataFile);

        AssertFresh(recovered);
        Assert.False(File.Exists(dataFile));
        Assert.Single(Directory.EnumerateFiles(directory.Path, "data.json.corrupt-*"));
    }

    [Fact]
    public async Task LoadAppData_UnavailableBackupPropagatesWithoutMutation()
    {
        using var directory = new TemporaryDirectory();
        var dataFile = Path.Combine(directory.Path, "data.json");
        var backupFile = JsonFilePersistence.GetBackupPath(dataFile);
        const string malformed = """{"settings":""";
        await File.WriteAllTextAsync(dataFile, malformed);
        Directory.CreateDirectory(backupFile);

        var exception = Record.Exception(() => JsonFilePersistence.LoadAppData(dataFile));

        Assert.True(
            exception is IOException or UnauthorizedAccessException,
            $"Expected an unavailable-file exception, got {exception?.GetType().Name ?? "none"}.");
        Assert.Equal(malformed, await File.ReadAllTextAsync(dataFile));
        Assert.True(Directory.Exists(backupFile));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.corrupt-*"));
    }

    [Fact]
    public async Task SaveAppDataAsync_CancellationBeforePublicationPreservesPrimaryAndCleansTemps()
    {
        using var directory = new TemporaryDirectory();
        var dataFile = Path.Combine(directory.Path, "data.json");
        await JsonFilePersistence.SaveAppDataAsync(dataFile, CreateData("original"));
        var staleTemp = Path.Combine(directory.Path, ".data.json.lumi-stale.tmp");
        await File.WriteAllTextAsync(staleTemp, "stale");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => JsonFilePersistence.SaveAppDataAsync(
                dataFile,
                CreateData("replacement"),
                cancellation.Token));

        Assert.Equal("original", ReadData(dataFile).Settings.UserName);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, ".data.json.lumi-*.tmp"));
    }

    [Fact]
    public async Task SaveAppDataAsync_MalformedPrimaryDoesNotReplaceValidBackup()
    {
        using var directory = new TemporaryDirectory();
        var dataFile = Path.Combine(directory.Path, "data.json");
        await CreatePrimaryAndBackupAsync(dataFile);
        await File.WriteAllTextAsync(dataFile, "{");

        await JsonFilePersistence.SaveAppDataAsync(dataFile, CreateData("replacement"));

        Assert.Equal("replacement", ReadData(dataFile).Settings.UserName);
        Assert.Equal(
            "backup",
            ReadData(JsonFilePersistence.GetBackupPath(dataFile)).Settings.UserName);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.corrupt-*"));
    }

    [Fact]
    public async Task HelperCleanup_PreservesUnrelatedTempFiles()
    {
        using var directory = new TemporaryDirectory();
        var dataFile = Path.Combine(directory.Path, "data.json");
        await JsonFilePersistence.SaveAppDataAsync(dataFile, CreateData("valid"));
        var helperTemp = Path.Combine(directory.Path, ".data.json.lumi-stale.tmp");
        var unrelatedTemp = Path.Combine(directory.Path, "notes.tmp");
        await File.WriteAllTextAsync(helperTemp, "stale");
        await File.WriteAllTextAsync(unrelatedTemp, "keep");

        var loaded = JsonFilePersistence.LoadAppData(dataFile);

        Assert.Equal("valid", loaded.Settings.UserName);
        Assert.False(File.Exists(helperTemp));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelatedTemp));
    }

    private static async Task CreatePrimaryAndBackupAsync(string dataFile)
    {
        await JsonFilePersistence.SaveAppDataAsync(dataFile, CreateData("backup"));
        await JsonFilePersistence.SaveAppDataAsync(dataFile, CreateData("primary"));
    }

    private static AppData CreateData(string userName)
        => new()
        {
            Settings = new UserSettings { UserName = userName }
        };

    private static AppData ReadData(string path)
    {
        var result = JsonFilePersistence.ReadPrimaryAppData(path);
        Assert.Equal(AppDataReadStatus.Valid, result.Status);
        return result.Data!;
    }

    private static void AssertFresh(AppData data)
    {
        Assert.False(data.Settings.DefaultsSeeded);
        Assert.Empty(data.Chats);
        Assert.Empty(data.Projects);
        Assert.Empty(data.Skills);
        Assert.Empty(data.Agents);
        Assert.Empty(data.Memories);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"LumiPersistenceTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
