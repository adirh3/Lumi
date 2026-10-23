using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// The chat database looking after itself: its text index follows every write and is built for a
/// database from before it, deleted space goes back to the disk, and a damaged file is moved aside
/// and what still reads in it recovered.
/// </summary>
public sealed class ChatDatabaseUpkeepTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-upkeep-" + Guid.NewGuid().ToString("N"));
    private readonly List<ChatStore> _stores = [];

    public ChatDatabaseUpkeepTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "lumi.db");

    public void Dispose()
    {
        CloseStores();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ── The text index ──────────────────────────────────────────────────────

    [Fact]
    public async Task TextIndex_FollowsEveryWrite_AndFindsTextInsideWordsIgnoringCaseAndAccents()
    {
        var dataStore = OpenDataStore(out var store);
        var chat = AddChat(dataStore,
            ("user", "Fix the ChatStoreWriter crash"),
            ("assistant", "Done: the résumé and הבית are back"),
            ("tool", "tool output with a needle"),
            ("reasoning", "thinking about the needle"),
            ("user", "the newest question"),
            ("assistant", "the newest answer"));
        await dataStore.SaveChatAsync(chat);

        Assert.Equal(chat.Id, Assert.Single(store.FindText("storewriter")).ChatId);
        Assert.Equal(chat.Id, Assert.Single(store.FindText("RESUME")).ChatId);
        Assert.Equal(chat.Id, Assert.Single(store.FindText("בית")).ChatId);
        Assert.Empty(store.FindText("needle"));

        chat.Messages[0].Content = "An edited question";
        await dataStore.SaveChatAsync(chat);
        Assert.Empty(store.FindText("storewriter"));
        Assert.Single(store.FindText("edited question"));

        // Editing an older message cuts the history from it.
        CloseStores();
        var reopened = OpenDataStore(out store);
        var stored = Assert.Single(reopened.Data.Chats);
        await reopened.LoadChatMessagesAsync(stored);
        var transcript = await reopened.ReadChatMessagesAsync(stored);
        await reopened.TruncateChatHistoryAsync(stored, transcript.First(static message => message.Content.StartsWith("Done")));
        Assert.Empty(store.FindText("résumé"));
        Assert.Empty(store.FindText("newest answer"));
        Assert.Single(store.FindText("edited question"));

        await reopened.DeleteChatFileAsync(stored.Id);
        Assert.Empty(store.FindText("edited question"));
    }

    [Fact]
    public async Task TextIndex_IsEmptiedByClearAll_AndKeepsFollowingWritesAfterIt()
    {
        var dataStore = OpenDataStore(out var store);
        await dataStore.SaveChatAsync(AddChat(dataStore, ("user", "the first quince")));
        await dataStore.SaveChatAsync(AddChat(dataStore, ("user", "the second quince")));
        Assert.Equal(2, store.FindText("quince").Count);

        await dataStore.DeleteAllChatFilesAsync();
        Assert.Empty(store.FindText("quince"));
        CloseStores();

        var reopened = OpenDataStore(out var second);
        await reopened.SaveChatAsync(AddChat(reopened, ("user", "a third quince")));
        Assert.Single(second.FindText("quince"));
    }

    [Fact]
    public async Task TextIndex_IsBuiltForADatabaseFromBeforeIt()
    {
        var dataStore = OpenDataStore(out _);
        var chat = AddChat(dataStore, ("user", "an old persimmon"), ("assistant", "the old answer"));
        await dataStore.SaveChatAsync(chat);
        CloseStores();
        Execute("DROP TABLE messages_text; DROP TABLE IF EXISTS messages_text_content; DROP TRIGGER messages_text_insert; " +
                "DROP TRIGGER messages_text_update; DROP TRIGGER messages_text_delete; PRAGMA user_version = 1;");

        var reopened = OpenDataStore(out var store);
        Assert.False(store.HasTextIndex);
        Assert.True(store.NeedsUpgrade);
        Assert.Empty(store.FindText("persimmon"));

        await store.StartMaintenance();
        Assert.True(store.HasTextIndex);
        Assert.False(store.NeedsUpgrade);
        Assert.Equal(ChatDatabase.SchemaVersion, Scalar("PRAGMA user_version;"));
        Assert.Single(store.FindText("persimmon"));

        var stored = Assert.Single(reopened.Data.Chats);
        await reopened.LoadChatMessagesAsync(stored);
        stored.Messages.Add(new ChatMessage { Role = "user", Content = "a new persimmon" });
        await reopened.SaveChatAsync(stored);
        Assert.Equal("a new persimmon", reopened.ReadHitText(reopened.FindChatsContaining("new persimmon")[stored.Id]));
    }

    [Fact]
    public async Task TextIndex_HoldsWhatChatSearchFindsInMemory()
    {
        var dataStore = OpenDataStore(out _);
        var chat = AddChat(dataStore, ("user", "start"));
        chat.Messages.Add(new ChatMessage
        {
            Role = "user",
            Author = "Lumi Job - Nightly persimmon check",
            Content = "Background job triggered: nightly"
        });
        chat.Messages.Add(new ChatMessage
        {
            Role = "user",
            Content = "{\"choice\":1}",
            InteractionReceipt = new InteractionReceipt { Title = "Quince order", Summary = "two quince", Detail = "ripe" }
        });
        chat.Messages.Add(new ChatMessage
        {
            Role = "tool",
            ToolName = "ask_input",
            ToolCallId = "ask",
            InputKind = "form",
            InputDefinition = """{"id":"f","title":"Medlar details","description":"About the medlar","fields":[]}"""
        });
        chat.Messages.Add(new ChatMessage { Role = "assistant", Author = "Quaternion", Content = "an answer" });
        string[] needles = ["persimmon check", "quince order", "two quince", "ripe", "medlar details", "about the medlar"];

        // Not saved yet: found in memory. Saved: found by the index. The same text either way.
        var inMemory = needles.ToDictionary(needle => needle, needle => dataStore.FindChatsContaining(needle).ContainsKey(chat.Id));
        await dataStore.SaveChatAsync(chat);
        var stored = needles.ToDictionary(needle => needle, needle => dataStore.FindChatsContaining(needle).ContainsKey(chat.Id));
        Assert.All(needles, needle => Assert.True(inMemory[needle] && stored[needle], needle));

        // Authors other than a job's are not what a message says.
        Assert.Empty(dataStore.FindChatsContaining("Quaternion"));
    }

    [Fact]
    public async Task HistorySearch_ReadsTheIndexForLongQueries_AndTheChatsOwnRowsForShortOnesAndReasoning()
    {
        var dataStore = OpenDataStore(out _);
        var chat = AddChat(dataStore,
            ("user", "Find the ÉCLAIR recipe, then the éclair photo"),
            ("reasoning", "an éclair in reasoning"),
            ("assistant", "first answer"),
            ("user", "the newest question"),
            ("assistant", "newest answer"));
        await dataStore.SaveChatAsync(chat);
        CloseStores();

        var reopened = OpenDataStore(out _);
        var stored = Assert.Single(reopened.Data.Chats);
        await reopened.LoadChatMessagesAsync(stored);

        Assert.Equal(2, Assert.Single(await reopened.SearchHistoryTextAsync(stored, "éclair", includeReasoning: false)).Occurrences);
        Assert.Equal(3, (await reopened.SearchHistoryTextAsync(stored, "éclair", includeReasoning: true)).Sum(static match => match.Occurrences));
        // Too short to look up: the chat's rows are read, exactly as typed but for case.
        Assert.Equal(2, Assert.Single(await reopened.SearchHistoryTextAsync(stored, "ÉC", includeReasoning: false)).Occurrences);
        Assert.Equal(1, Assert.Single(await reopened.SearchHistoryTextAsync(stored, "ec", includeReasoning: false)).Occurrences);
        Assert.Equal(3, (await reopened.SearchHistoryTextAsync(stored, "éC", includeReasoning: true)).Sum(static match => match.Occurrences));
        Assert.Empty(await reopened.SearchHistoryTextAsync(stored, "zq", includeReasoning: true));
    }

    [Fact]
    public async Task ChatSearch_ShortWordsMatchTitles_OrTheWholeQueryAsTyped()
    {
        var titled = new Chat { Id = Guid.NewGuid(), Title = "UI polish", UpdatedAt = DateTimeOffset.Now.AddHours(-1) };
        var said = new Chat { Id = Guid.NewGuid(), Title = "Release notes", UpdatedAt = DateTimeOffset.Now.AddHours(-2) };
        said.Messages.Add(new ChatMessage { Role = "user", Content = "The UI broke after merging PR #44 into main" });
        var data = new AppData { Chats = [titled, said] };
        var search = new GlobalSearchService(() => data, new DataStore(data));

        Assert.Same(titled, Assert.Single(await Chats("ui")).Item);
        var phrase = Assert.Single(await Chats("PR #44"));
        Assert.Same(said, phrase.Item);
        Assert.True(phrase.IsContentMatch);
        Assert.Contains("PR #44", phrase.Subtitle);
        // A short word with a long one: the long one in the messages, the short one in the title.
        Assert.Empty(await Chats("ui merging"));
        said.Title = "UI release notes";
        Assert.Same(said, Assert.Single(await Chats("ui merging")).Item);

        async Task<List<GlobalSearchMatch>> Chats(string query)
            => (await search.SearchAsync(query)).Where(static match => match.Category == GlobalSearchCategory.Chats).ToList();
    }

    [Fact]
    public async Task ChatSearch_FindsStoredWordsAsTyped_InEveryScript()
    {
        var dataStore = OpenDataStore(out _);
        string[] texts = ["한국어 공부를 했어요", "Это новый файл", "データベースの設計", "سأل أحمد عن السؤال", "ένα κείμενο εδώ", "สวัสดีครับ เพื่อน", "a résumé update"];
        var chats = texts.Select(text => AddChat(dataStore, ("user", "notes"), ("assistant", text))).ToList();
        foreach (var chat in chats)
            await dataStore.SaveChatAsync(chat);
        var search = new GlobalSearchService(() => dataStore.Data, dataStore);

        string[] queries = ["한국어", "Новый", "データベース", "أحمد", "κείμενο", "สวัสดี", "resume"];
        for (var index = 0; index < queries.Length; index++)
        {
            var found = (await search.SearchAsync(queries[index])).Where(static match => match.Category == GlobalSearchCategory.Chats);
            Assert.Same(chats[index], Assert.Single(found).Item);
        }
    }

    // ── Space ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task NewDatabase_GivesDeletedSpaceBackToTheDisk()
    {
        var dataStore = OpenDataStore(out var store);
        Assert.Equal(2, Scalar("PRAGMA auto_vacuum;"));
        Assert.False(store.NeedsUpgrade);
        var large = AddChat(dataStore, Enumerable.Range(0, 40)
            .Select(index => ("tool", $"output {index} " + new string('x', 200_000)))
            .ToArray());
        await dataStore.SaveChatAsync(large);
        await dataStore.SaveChatAsync(AddChat(dataStore, ("user", "a small chat")));
        var full = Scalar("PRAGMA page_count;");

        await dataStore.DeleteChatFileAsync(large.Id);
        await store.Writer.WhenAllSettled();

        Assert.True(Scalar("PRAGMA page_count;") < full / 4, $"{Scalar("PRAGMA page_count;")} of {full} pages");
        Assert.True(Scalar("PRAGMA freelist_count;") < 1024);
        Assert.Equal(2, Scalar("PRAGMA auto_vacuum;"));
        Assert.Single(store.FindText("small chat"));
    }

    [Fact]
    public async Task DatabaseLeftInFullMode_GoesBackToIncrementalVacuum()
    {
        var dataStore = OpenDataStore(out _);
        await dataStore.SaveChatAsync(AddChat(dataStore, ("user", "kept in full mode")));
        CloseStores();
        Execute("PRAGMA auto_vacuum = FULL;");
        Assert.Equal(1, Scalar("PRAGMA auto_vacuum;"));

        var store = OpenStore();
        await store.StartMaintenance();

        Assert.Equal(2, Scalar("PRAGMA auto_vacuum;"));
        Assert.Single(store.FindText("in full mode"));
    }

    [Fact]
    public async Task OlderDatabase_IsSwitchedToIncrementalVacuumOnce()
    {
        var dataStore = OpenDataStore(out _);
        await dataStore.SaveChatAsync(AddChat(dataStore, ("user", "kept through the rewrite")));
        CloseStores();
        Execute("PRAGMA auto_vacuum = NONE; VACUUM;");
        Assert.Equal(0, Scalar("PRAGMA auto_vacuum;"));

        var store = OpenStore();
        Assert.True(store.NeedsUpgrade);
        await store.StartMaintenance();

        Assert.Equal(2, Scalar("PRAGMA auto_vacuum;"));
        Assert.False(store.NeedsUpgrade);
        Assert.Single(store.FindText("through the rewrite"));
    }

    // ── Damage ──────────────────────────────────────────────────────────────

    [Fact]
    public void DamagedFile_IsMovedAside_AndANewDatabaseTakesItsPlace()
    {
        File.WriteAllBytes(DatabasePath, Enumerable.Range(0, 8192).Select(static index => (byte)(index * 7)).ToArray());

        var (store, chats, importer, damaged) = DataStore.OpenChatStorage(DatabasePath, [], Path.Combine(_directory, "chats"), null);
        _stores.Add(store);
        importer.Dispose();

        Assert.Empty(chats);
        Assert.NotNull(damaged);
        Assert.Equal(8192, new FileInfo(damaged).Length);
        Assert.Matches(@"lumi\.damaged-\d{8}-\d{6}\.db$", damaged);
        Assert.Equal(ChatDatabase.SchemaVersion, Scalar("PRAGMA user_version;"));
    }

    [Fact]
    public async Task DamageFoundWhileRunning_IsCheckedAtTheNextStart_AndWhatStillReadsIsRecovered()
    {
        var dataStore = OpenDataStore(out _);
        var first = AddChat(dataStore, ("user", "the first plum"), ("assistant", "an answer"));
        var damaged = AddChat(dataStore,
            ("user", "the damaged chat"),
            ("assistant", "a long answer " + new string('a', 5_000) + "CORRUPT-ME" + new string('b', 20_000)),
            ("user", "a later plum"));
        var last = AddChat(dataStore, ("user", "the last plum"));
        foreach (var chat in new[] { first, damaged, last })
            await dataStore.SaveChatAsync(chat);
        CloseStores();
        SqliteConnection.ClearAllPools();
        BreakOverflowPageHolding("CORRUPT-ME");

        // Lumi opens and lists its chats; reading the damaged one notes the damage for the next start.
        var store = OpenStore();
        Assert.Equal(3, store.LoadChats().Count);
        Assert.False(ChatDatabase.HasDamageMarker(DatabasePath));
        await Assert.ThrowsAnyAsync<SqliteException>(() => store.ReadAllAsync(damaged.Id));
        Assert.True(ChatDatabase.HasDamageMarker(DatabasePath));
        CloseStores();

        // The next start checks the whole file, moves it aside and starts a new one.
        var (fresh, chats, importer, copy) = DataStore.OpenChatStorage(DatabasePath, [], Path.Combine(_directory, "chats"), null);
        _stores.Add(fresh);
        importer.Dispose();
        Assert.Empty(chats);
        Assert.NotNull(copy);
        Assert.False(ChatDatabase.HasDamageMarker(DatabasePath));

        var result = fresh.Salvage(copy);
        Assert.Equal(new ChatSalvage.Result(3, 5, 1), result);
        var recovered = fresh.LoadChats().ToDictionary(static chat => chat.Chat.Id);
        Assert.Equal(ChatImportState.Failed, recovered[damaged.Id].ImportState);
        Assert.Equal(ChatImportState.Imported, recovered[first.Id].ImportState);
        Assert.Equal(["the damaged chat", "a later plum"], (await fresh.ReadAllAsync(damaged.Id)).Select(static message => message.Content));
        Assert.Equal(3, fresh.FindText("plum").Count);
    }

    [Fact]
    public void DamageNotedForAnIntactFile_IsForgottenAfterTheCheck()
    {
        OpenStore();
        CloseStores();
        File.WriteAllText(DatabasePath + ".check", "a read failed");

        var (store, _, importer, damaged) = DataStore.OpenChatStorage(DatabasePath, [], Path.Combine(_directory, "chats"), null);
        _stores.Add(store);
        importer.Dispose();

        Assert.Null(damaged);
        Assert.False(ChatDatabase.HasDamageMarker(DatabasePath));
    }

    [Fact]
    public async Task Recovery_CopiesEveryRow_WhenOnlyIndexesAreDamaged()
    {
        var dataStore = OpenDataStore(out _);
        var chats = Enumerable.Range(0, 3)
            .Select(chat => AddChat(dataStore, Enumerable.Range(0, 300).Select(index => ("user", $"message {chat}-{index}")).ToArray()))
            .ToList();
        foreach (var chat in chats)
            await dataStore.SaveChatAsync(chat);
        CloseStores();
        SqliteConnection.ClearAllPools();
        BreakPage(Scalar("SELECT rootpage FROM sqlite_schema WHERE name = 'messages_by_seq';"));
        BreakPage(Scalar("SELECT rootpage FROM sqlite_schema WHERE name = 'chats_by_version';"));

        var target = new ChatStore(Path.Combine(_directory, "recovered.db"));
        _stores.Add(target);

        Assert.Equal(new ChatSalvage.Result(3, 900, 0), target.Salvage(DatabasePath));
        Assert.Equal(300, (await target.ReadAllAsync(chats[1].Id)).Count);
    }

    [Fact]
    public async Task Recovery_CrossesLongStretchesWithoutRows_InOneSeek()
    {
        var dataStore = OpenDataStore(out _);
        var chat = AddChat(dataStore, ("user", "near"), ("assistant", "an answer"));
        await dataStore.SaveChatAsync(chat);
        CloseStores();
        // A key far past the others, as a bound read from a damaged page can be: stepping range by
        // range, the copy would walk a billion empty ranges to reach it.
        Execute("INSERT INTO messages (message_key, chat_key, seq, id, role, timestamp, text_length, body) " +
                "SELECT 274877906955, chat_key, 99, randomblob(16), 'user', 0, 3, '{\"role\":\"user\",\"content\":\"far\"}' FROM chats;");

        var target = new ChatStore(Path.Combine(_directory, "recovered.db"));
        _stores.Add(target);
        var result = await Task.Run(() => target.Salvage(DatabasePath)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(new ChatSalvage.Result(1, 3, 0), result);
        Assert.Equal(["near", "an answer", "far"], (await target.ReadAllAsync(chat.Id)).Select(static message => message.Content));
    }

    [Fact]
    public async Task Recovery_LeavesBehindOnlyRowsTheNewFileDoesNotTake()
    {
        var dataStore = OpenDataStore(out _);
        var chat = AddChat(dataStore, ("user", "first"), ("assistant", "the garbled answer"), ("user", "after it"), ("assistant", "the last answer"));
        var other = AddChat(dataStore, ("user", "another chat"));
        await dataStore.SaveChatAsync(chat);
        await dataStore.SaveChatAsync(other);
        CloseStores();
        // Garbled text that still reads: it is no longer JSON, so the new file's index cannot take it.
        Execute("DROP TRIGGER messages_text_update; " +
                "UPDATE messages SET body = '{\"role\":\"assistant\",\"content\":\"garb' WHERE body LIKE '%the garbled answer%';");

        var target = new ChatStore(Path.Combine(_directory, "recovered.db"));
        _stores.Add(target);

        Assert.Equal(new ChatSalvage.Result(2, 4, 1), target.Salvage(DatabasePath));
        Assert.Equal(["first", "after it", "the last answer"], (await target.ReadAllAsync(chat.Id)).Select(static message => message.Content));
        Assert.Equal(ChatImportState.Failed, target.LoadChats().Single(stored => stored.Chat.Id == chat.Id).ImportState);
    }

    [Fact]
    public async Task RecoveryCutShort_GoesOnAtTheNextStart_WithoutCopyingAnythingTwice()
    {
        var dataStore = OpenDataStore(out _);
        var chat = AddChat(dataStore, ("user", "kept through the cut"), ("assistant", "an answer"));
        await dataStore.SaveChatAsync(chat);
        CloseStores();
        SqliteConnection.ClearAllPools();
        BreakPage(Scalar("SELECT rootpage FROM sqlite_schema WHERE name = 'messages_by_seq';"));
        File.WriteAllText(DatabasePath + ".check", "a read failed");

        var (first, _, firstImporter, copy) = DataStore.OpenChatStorage(DatabasePath, [], Path.Combine(_directory, "chats"), null);
        _stores.Add(first);
        firstImporter.Dispose();
        Assert.NotNull(copy);
        Assert.Equal(copy, ChatDatabase.PendingRecovery(DatabasePath));
        Assert.Equal(new ChatSalvage.Result(1, 2, 0), first.Salvage(copy));
        Assert.Null(ChatDatabase.PendingRecovery(DatabasePath));

        // As if Lumi was closed before the recovery was done.
        File.WriteAllText(DatabasePath + ".recover", copy);
        CloseStores();
        var (second, chats, secondImporter, resumed) = DataStore.OpenChatStorage(DatabasePath, [], Path.Combine(_directory, "chats"), null);
        _stores.Add(second);
        secondImporter.Dispose();

        Assert.Equal(copy, resumed);
        Assert.Single(chats);
        Assert.Equal(new ChatSalvage.Result(1, 2, 0), second.Salvage(resumed));
        Assert.Equal(2, (await second.ReadAllAsync(chat.Id)).Count);
        Assert.Null(ChatDatabase.PendingRecovery(DatabasePath));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private ChatStore OpenStore()
    {
        var store = new ChatStore(DatabasePath);
        _stores.Add(store);
        return store;
    }

    private DataStore OpenDataStore(out ChatStore store)
    {
        store = OpenStore();
        return new DataStore(new AppData { Settings = new UserSettings { AutoSaveChats = true } }, store);
    }

    private void CloseStores()
    {
        foreach (var store in _stores)
            store.Dispose();
        _stores.Clear();
    }

    private static Chat AddChat(DataStore dataStore, params (string Role, string Content)[] messages)
    {
        var chat = new Chat { Title = "chat " + Guid.NewGuid().ToString("N")[..6] };
        var time = DateTimeOffset.Now.AddHours(-1);
        foreach (var (role, content) in messages)
            chat.Messages.Add(new ChatMessage { Role = role, Content = content, Timestamp = time = time.AddSeconds(1) });
        dataStore.Data.Chats.Add(chat);
        return chat;
    }

    private long Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Overwrites the header of a page (numbered from 1), so reading it fails.</summary>
    private void BreakPage(long page)
    {
        Assert.False(File.Exists(DatabasePath + "-wal") && new FileInfo(DatabasePath + "-wal").Length > 0);
        var pageSize = Scalar("PRAGMA page_size;");
        using var file = new FileStream(DatabasePath, FileMode.Open, FileAccess.Write);
        file.Position = (page - 1) * pageSize;
        file.Write(Enumerable.Repeat((byte)0x7F, 16).ToArray());
    }

    /// <summary>Points the overflow page holding <paramref name="marker"/> past the end of the file.</summary>
    private void BreakOverflowPageHolding(string marker)
    {
        Assert.False(File.Exists(DatabasePath + "-wal") && new FileInfo(DatabasePath + "-wal").Length > 0);
        var pageSize = Scalar("PRAGMA page_size;");
        var bytes = File.ReadAllBytes(DatabasePath);
        var at = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker));
        Assert.True(at > 0);
        var page = at / pageSize * pageSize;
        bytes[page] = 0x7F;
        bytes[page + 1] = bytes[page + 2] = bytes[page + 3] = 0xFF;
        File.WriteAllBytes(DatabasePath, bytes);
    }
}
