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

    [Fact]
    public async Task StagedChatDeletion_RollbackRestoresUnloadedTranscriptBytes()
    {
        using var directory = new TemporaryDirectory();
        var chat = new Chat { Title = "Unloaded transcript", MessageCount = 1 };
        var store = new DataStore(
            new AppData { Chats = [chat] },
            chatsDirectoryOverride: directory.Path);
        var chatFile = Path.Combine(directory.Path, $"{chat.Id}.json");
        var transcript = """[{"role":"user","content":"preserve exactly"}]"""u8.ToArray();
        await File.WriteAllBytesAsync(chatFile, transcript);

        var staged = await store.StageChatFileDeletionAsync(chat.Id);

        Assert.False(File.Exists(chatFile));
        Assert.True(File.Exists(staged.TombstoneFile));
        Assert.Empty(chat.Messages);

        await store.RollbackStagedChatFileDeletionAsync(staged);

        Assert.Equal(transcript, await File.ReadAllBytesAsync(chatFile));
        Assert.False(File.Exists(staged.TombstoneFile));
    }

    [Fact]
    public async Task RecoverStagedChatFileDeletions_UsesPersistedIndexAsCommitDecision()
    {
        using var directory = new TemporaryDirectory();
        var retainedChat = new Chat { Title = "Retained" };
        var deletedChatId = Guid.NewGuid();
        var store = new DataStore(
            new AppData { Chats = [retainedChat] },
            chatsDirectoryOverride: directory.Path);
        var retainedFile = Path.Combine(directory.Path, $"{retainedChat.Id}.json");
        var retainedTombstone = retainedFile + ".deleting";
        var deletedFile = Path.Combine(directory.Path, $"{deletedChatId}.json");
        var deletedTombstone = deletedFile + ".deleting";
        await File.WriteAllTextAsync(retainedTombstone, """[{"role":"user","content":"restore"}]""");
        await File.WriteAllTextAsync(deletedTombstone, """[{"role":"user","content":"remove"}]""");

        var unresolved = store.RecoverStagedChatFileDeletions();

        Assert.Empty(unresolved);
        Assert.True(File.Exists(retainedFile));
        Assert.False(File.Exists(retainedTombstone));
        Assert.False(File.Exists(deletedFile));
        Assert.False(File.Exists(deletedTombstone));
    }

    [Fact]
    public async Task FailedTombstoneRestore_IsExcludedFromOrphanCleanupUntilRetrySucceeds()
    {
        using var directory = new TemporaryDirectory();
        var chat = new Chat { Title = "Retry restore" };
        var store = new DataStore(
            new AppData { Chats = [chat] },
            chatsDirectoryOverride: directory.Path);
        var chatFile = Path.Combine(directory.Path, $"{chat.Id}.json");
        var tombstoneFile = chatFile + ".deleting";
        await File.WriteAllTextAsync(tombstoneFile, """[{"role":"user","content":"keep"}]""");
        Directory.CreateDirectory(chatFile);

        var unresolved = store.RecoverStagedChatFileDeletions();
        store.CleanOrphanedChats(unresolved);

        Assert.Contains(chat.Id, unresolved);
        Assert.True(store.IsChatFileDeletionPending(chat.Id));
        Assert.Contains(chat, store.Data.Chats);
        Assert.True(File.Exists(tombstoneFile));
        await store.LoadChatMessagesAsync(chat);
        Assert.Equal("keep", Assert.Single(chat.Messages).Content);

        Directory.Delete(chatFile);
        unresolved = store.RecoverStagedChatFileDeletions();
        store.CleanOrphanedChats(unresolved);

        Assert.Empty(unresolved);
        Assert.False(store.IsChatFileDeletionPending(chat.Id));
        Assert.Contains(chat, store.Data.Chats);
        Assert.True(File.Exists(chatFile));
        Assert.False(File.Exists(tombstoneFile));
    }

    [Fact]
    public async Task FailedRollback_KeepsChatFileDeletionPendingUntilRetrySucceeds()
    {
        using var directory = new TemporaryDirectory();
        var chat = new Chat { Title = "Rollback retry" };
        var store = new DataStore(
            new AppData { Chats = [chat] },
            chatsDirectoryOverride: directory.Path);
        var chatFile = Path.Combine(directory.Path, $"{chat.Id}.json");
        var transcript = """[{"role":"user","content":"keep"}]"""u8.ToArray();
        await File.WriteAllBytesAsync(chatFile, transcript);
        var staged = await store.StageChatFileDeletionAsync(chat.Id);
        Directory.CreateDirectory(chatFile);

        var rollbackError = await Record.ExceptionAsync(
            () => store.RollbackStagedChatFileDeletionAsync(staged));

        Assert.True(
            rollbackError is IOException or UnauthorizedAccessException,
            $"Expected a filesystem failure, got {rollbackError?.GetType().Name ?? "none"}.");
        Assert.True(store.IsChatFileDeletionPending(chat.Id));
        Directory.Delete(chatFile);

        await store.RollbackStagedChatFileDeletionAsync(staged);

        Assert.False(store.IsChatFileDeletionPending(chat.Id));
        Assert.Equal(transcript, await File.ReadAllBytesAsync(chatFile));
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
