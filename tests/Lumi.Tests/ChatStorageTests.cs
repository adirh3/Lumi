using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.EmbeddedApps;
using Lumi.Services.Storage;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

// Some tests build transcripts on the shared headless UI thread.
[Collection("Headless UI")]
public sealed class ChatStorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestProfile.Root, "chat-storage", Guid.NewGuid().ToString("N"));
    private readonly List<ChatStore> _stores = [];

    public ChatStorageTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        foreach (var store in _stores)
            store.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private string DatabasePath => Path.Combine(_directory, "lumi.db");

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

    private static string Legacy(ChatMessage message)
        => JsonSerializer.Serialize(message, AppDataJsonContext.Default.ChatMessage);

    // ── Codec ───────────────────────────────────────────────────────────────

    [Fact]
    public void Codec_RoundTripsEveryPersistedFieldExactly()
    {
        var message = CreateFullyPopulatedMessage();

        var json = ChatStorageJson.SerializeMessage(message);
        var restored = ChatStorageJson.DeserializeMessage(json);

        Assert.Equal(Legacy(message), Legacy(restored));
        Assert.DoesNotContain("\n", json);
    }

    [Fact]
    public void Codec_OmitsDefaultsAndStillRoundTripsAFreshMessage()
    {
        var message = new ChatMessage { Role = "assistant", Content = "hi" };

        var json = ChatStorageJson.SerializeMessage(message);
        var restored = ChatStorageJson.DeserializeMessage(json);

        Assert.DoesNotContain("attachments", json);
        Assert.DoesNotContain("isStreaming", json);
        Assert.DoesNotContain("null", json);
        Assert.Equal(Legacy(message), Legacy(restored));
    }

    [Fact]
    public void Codec_RoundTripsChatMetadata()
    {
        var chat = new Chat
        {
            Title = "Plans",
            ProjectId = Guid.NewGuid(),
            IsPinned = true,
            IsPaused = true,
            ActiveSkillIds = [Guid.NewGuid()],
            ActiveMcpServerNames = ["github"],
            HasExplicitMcpServerSelection = true,
            ApprovalMode = ApprovalMode.AskFirst,
            ApprovalRules = [new ApprovalRule { Action = ApprovalRuleAction.Allow, Kind = ApprovalRuleKind.Command, Target = "git" }],
            FollowUpSuggestions = ["next"],
            TotalInputTokens = 42,
            PlanContent = "# plan",
            ForkedFromChatId = Guid.NewGuid(),
            ForkedFromMessage = true,
        };

        var restored = ChatStorageJson.DeserializeChat(ChatStorageJson.SerializeChat(chat));

        Assert.Equal(
            JsonSerializer.Serialize(chat, AppDataJsonContext.Default.Chat),
            JsonSerializer.Serialize(restored, AppDataJsonContext.Default.Chat));
    }

    [Theory]
    [InlineData(typeof(ChatMessage))]
    [InlineData(typeof(Chat))]
    [InlineData(typeof(MessageReply))]
    [InlineData(typeof(ChatReference))]
    [InlineData(typeof(SkillReference))]
    [InlineData(typeof(SearchSource))]
    [InlineData(typeof(ApprovalRule))]
    [InlineData(typeof(EmbeddedApp))]
    public void Codec_OmittedValuesAreWhatAFreshObjectHolds(Type type)
    {
        // The codec leaves out false flags and empty collections; that is only lossless while a new
        // instance starts with exactly those values.
        var instance = Activator.CreateInstance(type)!;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0)
                continue;
            var value = property.GetValue(instance);
            if (property.PropertyType == typeof(bool))
                Assert.False((bool)value!, $"{type.Name}.{property.Name} must default to false.");
            else if (value is System.Collections.ICollection collection)
                Assert.True(collection.Count == 0, $"{type.Name}.{property.Name} must default to empty.");
        }
    }

    // ── Revisions ───────────────────────────────────────────────────────────

    [Fact]
    public void Revision_ChangesOnlyWhenAPersistedValueChanges()
    {
        var message = new ChatMessage { Content = "a" };
        var revision = message.Revision;

        message.Content = "a";
        Assert.Equal(revision, message.Revision);

        message.Content = "b";
        Assert.True(message.Revision > revision);

        revision = message.Revision;
        message.Sources.Add(new SearchSource { Url = "https://x" });
        message.MarkChanged();
        Assert.True(message.Revision > revision);

        revision = message.Revision;
        message.InteractionReceipt = new InteractionReceipt { Title = "t" };
        Assert.True(message.Revision > revision);
        revision = message.Revision;
        message.InteractionReceipt.Status = "Working";
        Assert.True(message.Revision > revision);
    }

    // ── Store ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Store_ReadsTailOutlineRangesAndLookups()
    {
        var dataStore = OpenDataStore(out var store);
        var chat = CreateChat(dataStore, exchanges: 3, toolsPerExchange: 4);
        await dataStore.SaveChatAsync(chat);

        var tail = await store.ReadTailAsync(chat.Id);
        Assert.Equal("user", tail.Messages[0].Role);
        Assert.Equal("question 2", tail.Messages[0].Content);
        Assert.Equal(chat.Messages.Count - tail.Messages.Count, tail.HistoryCount);

        var outline = await store.ReadOutlineAsync(chat.Id);
        Assert.Equal(6, outline.Count);
        Assert.All(outline, entry => Assert.Contains(entry.Role, new[] { "user", "assistant" }));

        var window = store.ReadByIndexNow(chat.Id, 2, 3);
        Assert.Equal(chat.Messages.Skip(2).Take(3).Select(Legacy), window.Select(Legacy));
        Assert.Equal(chat.Messages.Count, await store.CountAsync(chat.Id));

        var target = chat.Messages[7];
        var found = await store.FindMessageAsync(chat.Id, target.Id);
        Assert.Equal(7, found!.Value.Index);
        Assert.Equal(Legacy(target), Legacy(found.Value.Message));

        var tool = chat.Messages.First(message => message.ToolCallId is not null);
        // A later step whose parent is that call: the call is read back without its body.
        var call = Assert.Single(await store.ReadEarlierToolCallsAsync(chat.Id, [(null, tool.ToolCallId)], long.MaxValue));
        Assert.Equal((tool.ToolCallId, tool.ParentToolCallId, tool.ToolName), (call.ToolCallId, call.ParentToolCallId, call.ToolName));
    }

    [Fact]
    public async Task Store_ReadsSubagentDescendantsAtAnyDepth()
    {
        var dataStore = OpenDataStore(out var store);
        var chat = new Chat { Title = "agents" };
        dataStore.Data.Chats.Add(chat);
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "go" });
        chat.Messages.Add(new ChatMessage { Role = "tool", ToolName = "task", ToolCallId = "root" });
        chat.Messages.Add(new ChatMessage { Role = "tool", ToolName = "view", ToolCallId = "child", ParentToolCallId = "root" });
        chat.Messages.Add(new ChatMessage { Role = "tool", ToolName = "task", ToolCallId = "nested", ParentToolCallId = "root" });
        chat.Messages.Add(new ChatMessage { Role = "tool", ToolName = "grep", ToolCallId = "deep", ParentToolCallId = "nested" });
        chat.Messages.Add(new ChatMessage { Role = "tool", ToolName = "grep", ToolCallId = "other" });
        await dataStore.SaveChatAsync(chat);

        var tree = await store.ReadDescendantsAsync(chat.Id, "root");

        Assert.Equal(new[] { "root", "child", "nested", "deep" }, tree.Select(message => message.ToolCallId));
        var agents = await store.ReadByKindAsync(chat.Id, [ChatMessageKinds.Agent]);
        Assert.Equal(new[] { "root", "nested" }, agents.Select(message => message.ToolCallId));
    }

    [Fact]
    public async Task Store_ReadsNewestUserPromptsAcrossChats()
    {
        var dataStore = OpenDataStore(out var store);
        var older = CreateChat(dataStore, exchanges: 2, toolsPerExchange: 0, start: DateTimeOffset.Now.AddHours(-2));
        var newer = CreateChat(dataStore, exchanges: 2, toolsPerExchange: 0, start: DateTimeOffset.Now.AddHours(-1));
        await dataStore.SaveChatAsync(older);
        await dataStore.SaveChatAsync(newer);

        var prompts = await store.ReadUserPromptsAsync(3);

        Assert.Equal(3, prompts.Count);
        Assert.Equal(newer.Id, prompts[0].ChatId);
        Assert.Equal("question 1", prompts[0].Content);
        Assert.True(prompts[0].Timestamp >= prompts[1].Timestamp);
    }

    // ── Incremental saves and tails ─────────────────────────────────────────

    [Fact]
    public async Task Reopen_LoadsOnlyTheLastExchangeAndKeepsHistoryReadable()
    {
        var dataStore = OpenDataStore(out _);
        var chat = CreateChat(dataStore, exchanges: 5, toolsPerExchange: 6);
        var expected = chat.Messages.Select(Legacy).ToList();
        await dataStore.SaveChatAsync(chat);
        CloseStores();

        var reopened = OpenDataStore(out _);
        var stored = Assert.Single(reopened.Data.Chats);
        Assert.Empty(stored.Messages);
        Assert.Equal(expected.Count, stored.MessageCount);

        await reopened.LoadChatMessagesAsync(stored);

        Assert.Equal("question 4", stored.Messages[0].Content);
        Assert.Equal(8, stored.Messages.Count);
        var window = reopened.GetTranscriptWindow(stored);
        Assert.Equal(expected.Count - 8, window.HistoryCount);
        Assert.Equal(expected, (await reopened.ReadChatMessagesAsync(stored)).Select(Legacy));
        Assert.Equal(expected.Count, reopened.GetTotalMessageCount(stored));
    }

    [Fact]
    public async Task Save_WritesOnlyChangedMessagesAndRemovesDroppedOnes()
    {
        var dataStore = OpenDataStore(out var store);
        var chat = CreateChat(dataStore, exchanges: 2, toolsPerExchange: 3);
        await dataStore.SaveChatAsync(chat);
        var versionAfterFirstSave = store.GetContentVersion(chat.Id);

        await dataStore.SaveChatAsync(chat);
        Assert.Equal(versionAfterFirstSave, store.GetContentVersion(chat.Id));

        var tool = chat.Messages.Last(message => message.Role == "tool");
        tool.ToolStatus = "Failed";
        tool.ToolOutput = "boom";
        var dropped = chat.Messages[^1];
        chat.Messages.Remove(dropped);
        chat.Messages.Add(new ChatMessage { Role = "assistant", Content = "replacement" });
        await dataStore.SaveChatAsync(chat);

        Assert.True(store.GetContentVersion(chat.Id) > versionAfterFirstSave);
        var all = await store.ReadAllAsync(chat.Id);
        Assert.Equal(chat.Messages.Select(Legacy), all.Select(Legacy));
        Assert.DoesNotContain(all, message => message.Id == dropped.Id);
    }

    [Fact]
    public async Task Save_PersistsReorderedTailInListOrder()
    {
        var dataStore = OpenDataStore(out var store);
        var chat = CreateChat(dataStore, exchanges: 1, toolsPerExchange: 3);
        await dataStore.SaveChatAsync(chat);

        var moved = chat.Messages[1];
        chat.Messages.Remove(moved);
        chat.Messages.Add(moved);
        await dataStore.SaveChatAsync(chat);

        var all = await store.ReadAllAsync(chat.Id);
        Assert.Equal(chat.Messages.Select(message => message.Id), all.Select(message => message.Id));
    }

    [Fact]
    public async Task Load_LeavesStaleInProgressWorkOfOlderExchangesInHistory()
    {
        var dataStore = OpenDataStore(out _);
        var chat = CreateChat(dataStore, exchanges: 4, toolsPerExchange: 2);
        var stale = chat.Messages.First(message => message.Role == "tool" && message.Content.Contains("exchange 1"));
        stale.ToolStatus = "InProgress";
        await dataStore.SaveChatAsync(chat);
        CloseStores();

        var reopened = OpenDataStore(out _);
        var stored = reopened.Data.Chats.Single();
        await reopened.LoadChatMessagesAsync(stored);

        Assert.Equal("question 3", stored.Messages[0].Content);
        Assert.DoesNotContain(stored.Messages, message => message.Id == stale.Id);
        var all = await reopened.ReadChatMessagesAsync(stored);
        Assert.Equal("InProgress", all.Single(message => message.Id == stale.Id).ToolStatus);
    }

    [Fact]
    public async Task AppendingToAnUnloadedChat_FollowsItsStoredHistory()
    {
        var dataStore = OpenDataStore(out _);
        var chat = CreateChat(dataStore, exchanges: 2, toolsPerExchange: 1);
        var expected = chat.Messages.Select(message => message.Id).ToList();
        await dataStore.SaveChatAsync(chat);
        CloseStores();

        var reopened = OpenDataStore(out _);
        var stored = reopened.Data.Chats.Single();
        var appended = new ChatMessage { Role = "user", Content = "from a job" };
        stored.Messages.Add(appended);
        await reopened.SaveChatAsync(stored);
        await reopened.LoadChatMessagesAsync(stored);

        Assert.Same(appended, stored.Messages[0]);
        expected.Add(appended.Id);
        Assert.Equal(expected, (await reopened.ReadChatMessagesAsync(stored)).Select(message => message.Id));
    }

    [Fact]
    public async Task HistoryTruncation_RemovesTheMessageAndEverythingAfterIt()
    {
        var dataStore = OpenDataStore(out _);
        var chat = CreateChat(dataStore, exchanges: 4, toolsPerExchange: 2);
        await dataStore.SaveChatAsync(chat);
        CloseStores();

        var reopened = OpenDataStore(out var store);
        var stored = reopened.Data.Chats.Single();
        await reopened.LoadChatMessagesAsync(stored);
        var history = await ReadHistoryAsync(reopened, stored, 1, reopened.GetTranscriptWindow(stored).TailStartSeq);
        var cut = history.First(message => message.Role == "user" && message.Content == "question 1");

        await reopened.TruncateChatHistoryAsync(stored, cut);
        stored.Messages.Add(new ChatMessage { Role = "user", Content = "edited" });
        await reopened.SaveChatAsync(stored);

        var all = await store.ReadAllAsync(stored.Id);
        Assert.Equal(new[] { "question 0", "edited" }, all.Where(message => message.Role == "user").Select(message => message.Content));
        Assert.Equal(all.Count, reopened.GetTotalMessageCount(stored));
    }

    [Fact]
    public async Task AttachedHistoryEdits_AreSavedWithTheChat()
    {
        var dataStore = OpenDataStore(out _);
        var chat = CreateChat(dataStore, exchanges: 3, toolsPerExchange: 1);
        await dataStore.SaveChatAsync(chat);
        CloseStores();

        var reopened = OpenDataStore(out var store);
        var stored = reopened.Data.Chats.Single();
        await reopened.LoadChatMessagesAsync(stored);
        var history = await ReadHistoryAsync(reopened, stored, 1, reopened.GetTranscriptWindow(stored).TailStartSeq);
        reopened.AttachHistoryMessages(stored, history);
        var answer = history.First(message => message.Role == "assistant");
        answer.InteractionStates = new Dictionary<string, string> { ["form"] = "{\"done\":true}" };
        await reopened.SaveChatAsync(stored);

        var again = await ReadHistoryAsync(reopened, stored, answer.StorageSeq, answer.StorageSeq + 1);
        Assert.Same(answer, Assert.Single(again));
        var raw = await store.ReadRangeAsync(stored.Id, answer.StorageSeq, answer.StorageSeq + 1);
        Assert.Equal("{\"done\":true}", Assert.Single(raw).InteractionStates!["form"]);
    }

    [Fact]
    public async Task ReleasedTail_IsReloadedFromStorage()
    {
        var dataStore = OpenDataStore(out _);
        var chat = CreateChat(dataStore, exchanges: 2, toolsPerExchange: 2);
        await dataStore.SaveChatAsync(chat);
        var expected = chat.Messages.Select(Legacy).ToList();

        chat.Messages[^1].Content = "unsaved";
        Assert.False(dataStore.TryReleaseChatMessages(chat));
        await dataStore.SaveChatAsync(chat);
        expected[^1] = Legacy(chat.Messages[^1]);
        Assert.True(dataStore.TryReleaseChatMessages(chat));
        Assert.Empty(chat.Messages);

        await dataStore.LoadChatMessagesAsync(chat);
        Assert.Equal(expected, (await dataStore.ReadChatMessagesAsync(chat)).Select(Legacy));
    }

    [Fact]
    public async Task DeletingAChat_RemovesItsTranscript()
    {
        var dataStore = OpenDataStore(out var store);
        var chat = CreateChat(dataStore, exchanges: 2, toolsPerExchange: 1);
        await dataStore.SaveChatAsync(chat);

        await dataStore.DeleteChatFileAsync(chat.Id);

        Assert.False(dataStore.HasStoredMessages(chat.Id));
        Assert.Empty(await store.ReadAllAsync(chat.Id));
    }

    // ── Legacy import ───────────────────────────────────────────────────────

    [Fact]
    public async Task LegacyImport_MovesTranscriptsExactlyAndKeepsUnreadableFiles()
    {
        var legacyDirectory = Path.Combine(_directory, "chats");
        Directory.CreateDirectory(legacyDirectory);
        var good = new Chat { Title = "good", MessageCount = 0 };
        var goodMessages = Enumerable.Range(0, 1200)
            .Select(i => i % 10 == 0
                ? new ChatMessage { Role = "user", Content = $"q{i}" }
                : new ChatMessage { Role = "tool", ToolName = "view", ToolCallId = $"t{i}", Content = $"{{\"path\":\"f{i}.cs\"}}", ToolOutput = new string('x', i % 50), ToolStatus = "Completed" })
            .ToList();
        await File.WriteAllTextAsync(Path.Combine(legacyDirectory, $"{good.Id}.json"),
            JsonSerializer.Serialize(goodMessages, AppDataJsonContext.Default.ListChatMessage));
        var broken = new Chat { Title = "broken" };
        await File.WriteAllTextAsync(Path.Combine(legacyDirectory, $"{broken.Id}.json"), "[{\"role\":\"user\",\"content\":");
        var missing = new Chat { Title = "missing" };
        var orphanFile = Path.Combine(legacyDirectory, $"{Guid.NewGuid()}.json");
        await File.WriteAllTextAsync(orphanFile, "[{\"role\":\"user\",\"content\":\"orphan\"}]");

        var (store, chats, importer) = DataStore.OpenChatStorage(DatabasePath, [good, broken, missing], legacyDirectory, null);
        _stores.Add(store);
        using (importer)
        {
            Assert.Equal(new[] { good.Id, broken.Id }.Order(), chats.Select(chat => chat.Id).Order());
            Assert.True(importer.IsPending(good.Id));

            var tail = await store.ReadTailAsync(good.Id);
            Assert.False(importer.IsPending(good.Id));
            Assert.Equal(goodMessages.Count, tail.HistoryCount + tail.Messages.Count);
            Assert.Equal(goodMessages.Select(Legacy), (await store.ReadAllAsync(good.Id)).Select(Legacy));

            await importer.EnsureImportedAsync(broken.Id);
            Assert.Equal(ChatImportState.Failed, store.GetImportState(broken.Id));
            Assert.Empty(await store.ReadAllAsync(broken.Id));

            importer.RetireLegacyFolder();
            var backup = Path.Combine(_directory, LegacyChatImporter.BackupFolderName);
            Assert.False(Directory.Exists(legacyDirectory));
            Assert.True(File.Exists(Path.Combine(backup, $"{broken.Id}.json")));
            Assert.True(File.Exists(Path.Combine(backup, Path.GetFileName(orphanFile))));
        }

        // Starting again finds the good chat imported, never twice; the damaged file, which gave its
        // chat nothing, is kept as it is and not read again.
        CloseStores();
        var (reopened, again, secondImporter) = DataStore.OpenChatStorage(DatabasePath, [good, broken], legacyDirectory, null);
        _stores.Add(reopened);
        using (secondImporter)
        {
            Assert.False(secondImporter.IsPending(good.Id));
            Assert.False(secondImporter.IsPending(broken.Id));
            Assert.Equal(goodMessages.Count, again.Single(chat => chat.Id == good.Id).MessageCount);
        }
    }

    [Fact]
    public async Task LegacyImport_InterruptedImportStartsOver()
    {
        var legacyDirectory = Path.Combine(_directory, "chats");
        Directory.CreateDirectory(legacyDirectory);
        var chat = new Chat { Title = "chat" };
        var messages = Enumerable.Range(0, 900).Select(i => new ChatMessage { Role = i == 0 ? "user" : "assistant", Content = $"m{i}" }).ToList();
        await File.WriteAllTextAsync(Path.Combine(legacyDirectory, $"{chat.Id}.json"),
            JsonSerializer.Serialize(messages, AppDataJsonContext.Default.ListChatMessage));

        var (store, _, importer) = DataStore.OpenChatStorage(DatabasePath, [chat], legacyDirectory, null);
        _stores.Add(store);
        // Simulate a crash after part of the transcript landed: rows exist, the chat is still pending.
        await store.Writer.Enqueue(chat.Id, (connection, transaction) =>
        {
            var key = ChatStore.UpsertChatRow(connection, transaction, chat.Id, 0, ChatStorageJson.SerializeChat(chat));
            ChatStore.UpsertMessages(connection, transaction, key, [EncodedMessage.From(new ChatMessage { Content = "partial" }, 1)]);
        });

        await importer.EnsureImportedAsync(chat.Id);
        importer.Dispose();

        Assert.Equal(messages.Select(Legacy), (await store.ReadAllAsync(chat.Id)).Select(Legacy));
    }

    // ── Whole-chat reads without loading the transcript ────────────────────

    [Fact]
    public async Task WholeChatReads_CoverHistoryWhileOnlyTheTailIsLoaded()
    {
        var dataStore = OpenDataStore(out _);
        var chat = CreateChat(dataStore, exchanges: 6, toolsPerExchange: 4);
        chat.Messages.Insert(3, new ChatMessage { Role = "error", Content = "old failure", Timestamp = chat.Messages[2].Timestamp });
        await dataStore.SaveChatAsync(chat);
        CloseStores();

        var reopened = OpenDataStore(out _);
        var stored = Assert.Single(reopened.Data.Chats);
        await reopened.LoadChatMessagesAsync(stored);
        Assert.Equal(6, stored.Messages.Count);

        var roles = await reopened.CountChatRolesAsync(stored);
        Assert.Equal(6, roles["user"].Count);
        Assert.Equal(6, roles["assistant"].WithText);
        Assert.Equal(24, roles["tool"].Count);
        Assert.Equal(1, roles["error"].Count);
        Assert.Equal(6, reopened.CountUserMessages(stored, withText: true));
        Assert.Equal(6, reopened.CountAnsweredAssistantMessages(stored));

        // The newest error is in history; the newest answer is in the tail.
        Assert.Equal("old failure", (await reopened.ReadLastChatMessageAsync(stored, "error"))?.Content);
        Assert.Equal("answer 5", (await reopened.ReadLastChatMessageAsync(stored, "assistant", withText: true))?.Content);

        var recent = await reopened.ReadRecentChatMessagesAsync(stored, 4, conversationOnly: true);
        Assert.Equal(["question 4", "answer 4", "question 5", "answer 5"], recent.Select(message => message.Content));
    }

    [Fact]
    public async Task HistorySearch_CountsMatchesInStoredMessagesIgnoringCase()
    {
        var dataStore = OpenDataStore(out _);
        var chat = CreateChat(dataStore, exchanges: 4, toolsPerExchange: 2);
        chat.Messages[0].Content = "Find the ÉCLAIR recipe, then the éclair photo";
        await dataStore.SaveChatAsync(chat);
        CloseStores();

        var reopened = OpenDataStore(out _);
        var stored = Assert.Single(reopened.Data.Chats);
        await reopened.LoadChatMessagesAsync(stored);

        var matches = await reopened.SearchHistoryTextAsync(stored, "éclair", includeReasoning: true);
        var match = Assert.Single(matches);
        Assert.Equal(2, match.Occurrences);
        // Tool output is not searched, and the live tail is not part of the stored history.
        Assert.Empty(await reopened.SearchHistoryTextAsync(stored, "output 0.1", includeReasoning: true));
        Assert.Empty(await reopened.SearchHistoryTextAsync(stored, "question 3", includeReasoning: true));
        Assert.Equal(3, (await reopened.SearchHistoryTextAsync(stored, "answer", includeReasoning: true)).Count);
    }

    [Fact]
    public async Task RemoteSource_ReadsOnlyThePagesAWindowReaches()
    {
        var dataStore = OpenDataStore(out _);
        var chat = CreateChat(dataStore, exchanges: 60, toolsPerExchange: 20);
        var expected = chat.Messages.Select(message => message.Id).ToList();
        await dataStore.SaveChatAsync(chat);
        CloseStores();

        var reopened = OpenDataStore(out _);
        var stored = Assert.Single(reopened.Data.Chats);
        await reopened.LoadChatMessagesAsync(stored);
        var window = reopened.GetTranscriptWindow(stored);
        var source = new Lumi.Services.Remote.RemoteMessageSource(
            (start, count) => reopened.ReadStoredMessagesByIndex(stored.Id, start, count),
            window.HistoryCount,
            stored.Messages);

        Assert.Equal(expected.Count, source.Count);
        var latest = Lumi.Services.Remote.RemoteProjector.SelectTranscriptWindow(source, null, 40);
        Assert.Equal(expected.Skip(expected.Count - 40), latest.Messages.Select(message => message.Id));
        var older = Lumi.Services.Remote.RemoteProjector.SelectTranscriptWindow(source, 100, 40);
        Assert.Equal(expected.Skip(60).Take(40), older.Messages.Select(message => message.Id));
        // A page of 256 positions serves both windows' stored part; the rest of the chat was never read.
        Assert.True(source.ReadMessageCount <= 512, $"{source.ReadMessageCount} stored messages were read.");
        // An exchange is a user message, 20 steps and an answer.
        Assert.Equal(22, source.FindTurnEnd(0));
        Assert.Equal(22, source.FindTurnStart(43));
        Assert.Equal(expected.Take(22), source.Slice(0, source.FindTurnEnd(0)).Select(message => message.Id));
    }

    [Fact]
    public void WorkspaceHistory_ReducesStoredStepsToRows()
    {
        var question = Guid.NewGuid();
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = "Build the report", StorageSeq = 1 },
            new() { Role = "tool", ToolName = "report_intent", ToolCallId = "intent-1", Content = "{\"intent\":\"Drafting the report\"}", StorageSeq = 2 },
            new() { Role = "tool", ToolName = "web_search", ToolCallId = "search-1", Content = "{\"query\":\"quarterly revenue\"}", StorageSeq = 3 },
            new() { Role = "tool", ToolName = "ask_question", ToolCallId = "ask-1", QuestionId = question.ToString(), QuestionText = "Which quarter?", StorageSeq = 4 },
            new() { Role = "assistant", Content = "Done, see [the docs](https://example.com/docs).", StorageSeq = 5 },
            new() { Role = "error", Content = "Network down\nretrying", StorageSeq = 6 },
        };

        var rows = ChatViewModel.DeriveWorkspaceHistory(messages, []);

        Assert.Same(messages[0], Assert.Single(rows.UserMessages));
        Assert.Equal(
            [WorkspaceActivityKind.Intent, WorkspaceActivityKind.Search, WorkspaceActivityKind.Question, WorkspaceActivityKind.Error],
            rows.Activities.Select(activity => activity.Kind));
        Assert.Equal("Network down", rows.Activities[^1].Title);
        Assert.StartsWith($"stored:{messages[3].Id:N}:{question}", rows.Activities[2].Target);
        Assert.Equal(("the docs", "https://example.com/docs"), Assert.Single(rows.Links));
    }

    [Fact]
    public void WorkspaceHistory_ListsEachSkillWhereTheTranscriptShowsIt()
    {
        SkillReference Skill(string name) => new() { Name = name };
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = "first", ActiveSkills = [Skill("Word")], StorageSeq = 1 },
            new()
            {
                Role = "user", Content = "receipt", ActiveSkills = [Skill("Charts")], StorageSeq = 2,
                InteractionReceipt = new InteractionReceipt { SourceMessageId = Guid.NewGuid(), BlockId = "b" },
            },
            new() { Role = "tool", ToolName = "fetch_skill", ToolCallId = "fetch-1", Content = "{\"name\":\"Excel\"}", StorageSeq = 3 },
            new() { Role = "user", Author = "Lumi Job - Watcher", Content = "Background job triggered: Watcher", ActiveSkills = [Skill("Notes")], StorageSeq = 4 },
            new() { Role = "user", Content = "again", ActiveSkills = [Skill("word"), Skill("Charts")], StorageSeq = 5 },
            new() { Role = "tool", ToolName = "fetch_skill", ToolCallId = "fetch-2", Content = "{\"name\":\"Excel\"}", StorageSeq = 6 },
        };

        var rows = ChatViewModel.DeriveWorkspaceHistory(messages, []);

        // A skill shows once, where it first appears; a form's receipt and a job's wake show none.
        Assert.Equal(["Word", "Excel", "Charts"], rows.Skills.Select(static skill => skill.Name));
    }

    [Fact]
    public void WorkspaceHistory_ListsEachFileByTheLatestExchangeThatEditedIt()
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = "first", StorageSeq = 10 },
            new() { Role = "assistant", Content = "done", StorageSeq = 20 },
            new() { Role = "user", Content = "second", StorageSeq = 30 },
        };
        StoredFileEdit[] edits =
        [
            new(5, "early.cs", 1, 0, false),
            new(12, "a.cs", 3, 1, true),
            new(15, "a.cs", 2, 2, false),
            new(15, "b.cs", 1, 0, false),
            new(40, "B.CS", 4, 0, false),
        ];

        var rows = ChatViewModel.DeriveWorkspaceHistory(messages, edits);

        // Each edit belongs to the exchange its position is in, those after the last message to the
        // last exchange; a file's row adds up the edits of the latest exchange that touched it, and is
        // a creation when that exchange's first edit created it.
        Assert.Equal(
            [("early.cs", false, 0L, 1, 0, "5"), ("a.cs", true, 10L, 5, 3, "12,15"), ("B.CS", false, 30L, 4, 0, "40")],
            rows.FileChanges.Select(static change => (
                change.FilePath, change.IsCreate, change.Exchange, change.LinesAdded, change.LinesRemoved, string.Join(",", change.Seqs))));
    }

    [Fact]
    public async Task WorkspaceHistory_ListsFilesTheSessionReportedChanged_AsTheTranscriptDoes()
    {
        var created = Path.Combine(_directory, "made.txt");
        File.WriteAllText(created, "one\ntwo\n");
        ChatMessage Reported(long seq, string path, string operation, string status = "Completed") => new()
        {
            Role = "tool", ToolName = ToolDisplayHelper.WorkspaceFileChangedToolName, ToolStatus = status, StorageSeq = seq,
            Content = JsonSerializer.Serialize(new { filePath = path, operation }),
        };
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = "first", StorageSeq = 10 },
            Reported(11, "shell.ps1", "update"),
            Reported(12, created, "create"),
            Reported(14, "both.cs", "create"),
            Reported(15, "later.cs", "create"),
            Reported(17, "failed.cs", "create", status: "Failed"),
            new() { Role = "user", Content = "second", StorageSeq = 20 },
            Reported(21, "shell.ps1", "Update"),
        };
        StoredFileEdit[] edits = [new(13, "both.cs", 2, 1, false), new(16, "later.cs", 1, 0, false)];

        var rows = ChatViewModel.DeriveWorkspaceHistory(messages, edits);

        // A reported file shows with its exchange's edits unless one of them edited it, whichever came
        // first; a reported creation counts the file's text as added.
        Assert.Equal(
            [(created, true, 10L, 3, 0, false), ("both.cs", false, 10L, 2, 1, true), ("later.cs", false, 10L, 1, 0, true), ("shell.ps1", false, 20L, 0, 0, false)],
            rows.FileChanges.Select(static change => (change.FilePath, change.IsCreate, change.Exchange, change.LinesAdded, change.LinesRemoved, change.HasEdits)));

        // Stored, the reports come with the overview.
        var dataStore = OpenDataStore(out var store);
        var chat = new Chat { Title = "reported" };
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "run the script" });
        chat.Messages.Add(Reported(0, created, "create"));
        chat.Messages.Add(new ChatMessage { Role = "assistant", Content = "Done." });
        dataStore.Data.Chats.Add(chat);
        await dataStore.SaveChatAsync(chat);
        var overview = await store.ReadOverviewAsync(chat.Id, ChatViewModel.WorkspaceHistoryKinds, long.MaxValue);
        var listed = ChatViewModel.DeriveWorkspaceHistory(overview.Messages, overview.FileEdits);
        Assert.Equal((created, true, 3), listed.FileChanges.Select(static change => (change.FilePath, change.IsCreate, change.LinesAdded)).Single());
    }

    [Fact]
    public async Task FileEdits_AreCountedAsTheTranscriptCountsThem()
    {
        var patch = string.Join("\n",
            "*** Begin Patch",
            "*** Add File: src/new.cs",
            "+line one",
            "+line two",
            "-not read in an added file",
            "*** Update File:  src/spaced.cs  ",
            "@@ context",
            "-old one",
            "+new one",
            "+++ a header, not a line",
            "--- a header, not a line either",
            "",
            "++++ starts with +++ after an empty line",
            "+var s = \"\\n*** Update File: not a header\";",
            "*** Delete File: src/old.cs",
            "+not read after a delete",
            "*** Update File: src/one-empty-line.cs",
            "+",
            "-",
            "*** Update File:    ",
            "+a blank path is no file",
            "*** End Patch",
            "*** Update File: after-the-end.cs",
            "+read on, as the transcript reads past the end",
            "-x");
        (string Tool, string Content, string Status)[] edits =
        [
            ("edit", """{"path":"a.cs","old_str":"one\ntwo","new_str":"three"}""", "Completed"),
            ("str_replace_editor", """{"file":"b.cs","old_str":null,"new_str":"x\ny\n"}""", "Completed"),
            ("replace_string_in_file", """{"filePath":"c.cs","path":"not-c.cs","oldString":"é\nü","old_str":"ignored","newString":"日本\n語\n"}""", "Completed"),
            ("create", """{"path":"d.cs","file_text":"a\nb"}""", "Completed"),
            ("write_file", """{"path":"e.cs","content":""}""", "Completed"),
            ("insert", """{"path":"f.cs","insert_text":"z"}""", "Completed"),
            ("edit", """{"path":null,"file":"g.cs","new_str":"x"}""", "Completed"),
            ("edit", """{"path":5,"new_str":"x"}""", "Completed"),
            ("edit", """{"path":"h.cs","old_str":5}""", "Completed"),
            ("edit", "[1, 2]", "Completed"),
            ("edit", "not json", "Completed"),
            ("edit", "", "Completed"),
            ("edit", """{"path":"failed.cs","new_str":"x"}""", "Failed"),
            ("multi_replace_string_in_file", """{"replacements":[{"filePath":"m1.cs","oldString":"a","newString":"b\nc"},{"path":"m2.cs","newString":"d"},{"filePath":null,"path":"skipped.cs"},{"filePath":"m3.cs","oldString":null}]}""", "Completed"),
            ("multi_replace_string_in_file", """{"replacements":{"a":{"filePath":"not-a-list.cs"}}}""", "Completed"),
            ("apply_patch", patch.Replace("\n", "\r\n"), "Completed"),
            ("apply_patch", JsonSerializer.Serialize(new { patch }), "Completed"),
            ("apply_patch", JsonSerializer.Serialize(patch), "Completed"),
            ("apply_patch", "  \n" + JsonSerializer.Serialize(new { patch = 5, input = "*** Add File: input.cs\n+one" }), "Completed"),
            ("apply_patch", """{"other":"*** Add File: not-a-patch.cs"}""", "Completed"),
            ("apply_patch", "{broken\n*** Add File: raw.cs\n+one", "Completed"),
            ("apply_patch", "*** Begin Patch\r*** Update File: mac.cs\r-a\r+b\r+c", "Completed"),
            ("apply_patch", "*** Update File: no-body.cs", "Completed"),
        ];
        var dataStore = OpenDataStore(out var store);
        var chat = new Chat { Title = "edits" };
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "edit everything" });
        for (var i = 0; i < edits.Length; i++)
        {
            chat.Messages.Add(new ChatMessage
            {
                Role = "tool", ToolName = edits[i].Tool, ToolCallId = $"edit-{i}", ToolStatus = edits[i].Status, Content = edits[i].Content,
            });
        }

        chat.Messages.Add(new ChatMessage { Role = "assistant", Content = "Done." });
        dataStore.Data.Chats.Add(chat);
        await dataStore.SaveChatAsync(chat);
        var expected = (await store.ReadAllAsync(chat.Id))
            .Where(static message => message.ToolName is { } tool && ToolDisplayHelper.IsFileEditTool(tool) && message.ToolStatus != "Failed")
            .SelectMany(static message => ToolDisplayHelper.ExtractAllDiffs(message.ToolName!, message.Content).Select(diff => new StoredFileEdit(
                message.StorageSeq,
                diff.FilePath,
                FileChangeItem.CountLines(diff.NewText),
                FileChangeItem.CountLines(diff.OldText),
                TranscriptBuilder.IsCreateDiff(message.ToolName!, diff.OldText))))
            .ToList();

        var overview = await store.ReadOverviewAsync(chat.Id, [], long.MaxValue);

        Assert.Equal(expected, overview.FileEdits);
        var patched = overview.FileEdits.Where(edit => edit.Seq == expected.First(e => e.FilePath == "src/new.cs").Seq)
            .Select(static edit => (edit.FilePath, edit.LinesAdded, edit.LinesRemoved, edit.IsCreate));
        Assert.Equal(
            [("src/new.cs", 2, 0, true), ("src/spaced.cs", 2, 1, false), ("src/old.cs", 0, 0, false),
             ("src/one-empty-line.cs", 0, 0, false), ("after-the-end.cs", 1, 1, false)],
            patched);
        Assert.Equal(
            ["a.cs", "b.cs", "c.cs", "d.cs", "e.cs", "f.cs", "m1.cs", "m2.cs", "m3.cs", "input.cs", "raw.cs", "mac.cs", "no-body.cs"],
            overview.FileEdits.Select(static edit => edit.FilePath).Where(static path => !path.StartsWith("src/") && path != "after-the-end.cs"));
    }

    [Fact]
    public async Task Overview_ReadsOnlyTheAnswersThatCanHoldALinkOrCiteSources()
    {
        var dataStore = OpenDataStore(out var store);
        var chat = new Chat { Title = "answers" };
        var sources = new List<SearchSource> { new() { Title = "Docs", Snippet = "About the docs", Url = "https://example.com/docs" } };
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "question" });
        chat.Messages.Add(new ChatMessage { Role = "assistant", Content = "A plain answer, with nothing to list.", Author = "Lumi" });
        chat.Messages.Add(new ChatMessage { Role = "assistant", Content = "See [the docs](https://example.com/docs)." });
        chat.Messages.Add(new ChatMessage { Role = "assistant", Content = "Cited, without links.", Sources = sources });
        chat.Messages.Add(new ChatMessage { Role = "assistant", Content = "Read [the spec](HTTPS://EXAMPLE.COM/SPEC) and [the guide](https://example.com/guide).", Sources = sources });
        chat.Messages.Add(new ChatMessage { Role = "assistant", Content = "A [bracket](not a page) next to http." });
        chat.Messages.Add(new ChatMessage { Role = "assistant", Content = "Text about http and ](, apart." });
        dataStore.Data.Chats.Add(chat);
        await dataStore.SaveChatAsync(chat);

        var overview = await store.ReadOverviewAsync(chat.Id, [], long.MaxValue);

        // An answer's text comes only when it could hold a link to a web page; one that cites sources
        // comes with them.
        Assert.Equal(
            [
                ("user", "question", 0), ("assistant", "See [the docs](https://example.com/docs).", 0), ("assistant", "", 1),
                ("assistant", "Read [the spec](HTTPS://EXAMPLE.COM/SPEC) and [the guide](https://example.com/guide).", 1),
                ("assistant", "A [bracket](not a page) next to http.", 0), ("assistant", "Text about http and ](, apart.", 0),
            ],
            overview.Messages.Select(static message => (message.Role, message.Content, message.Sources.Count)));
        var whole = ChatViewModel.DeriveWorkspaceHistory(await store.ReadAllAsync(chat.Id), []);
        var listed = ChatViewModel.DeriveWorkspaceHistory(overview.Messages, overview.FileEdits);
        Assert.Equal(3, listed.Links.Count);
        Assert.Equal(whole.Links, listed.Links);
        Assert.Equal(
            whole.Sources.Select(static source => (source.Title, source.Snippet, source.Url)),
            listed.Sources.Select(static source => (source.Title, source.Snippet, source.Url)));
        Assert.Equal(2, listed.Sources.Count);
    }

    [Fact]
    public async Task Overview_ReadsRunsWithoutWhatOnlyTheirPageShows()
    {
        var dataStore = OpenDataStore(out var store);
        var chat = CreateChat(dataStore, exchanges: 2, toolsPerExchange: 2);
        var run = chat.Messages.Single(static message => message.ToolCallId == "call-0-0");
        run.ToolName = "task";
        run.ToolStatus = "Failed";
        run.ToolOutput = "The agent ran out of time.";
        run.Content = """{"description":"Inspect the repo","agent_type":"explore","prompt":"Look for the cause","transcript":"Reading","entries":[{"kind":"assistant","text":"Found it"}]}""";
        var search = chat.Messages.Single(static message => message.ToolCallId == "call-0-1");
        search.ToolName = "web_search";
        search.Content = """{"query":"the cause"}""";
        await dataStore.SaveChatAsync(chat);

        var overview = await store.ReadOverviewAsync(chat.Id, [ChatMessageKinds.Agent, ChatMessageKinds.Web], long.MaxValue);

        // The conversation (its answers here can hold no link) and other steps whole; the run as its
        // row reads it, with why it failed.
        Assert.Equal(
            chat.Messages.Where(static message => message.ToolCallId != "call-0-0" && message.Role != "assistant" && (message.Role != "tool" || message.ToolCallId == "call-0-1")).Select(Legacy),
            overview.Messages.Where(static message => message.ToolCallId != "call-0-0").Select(Legacy));
        var listed = overview.Messages.Single(static message => message.ToolCallId == "call-0-0");
        var payload = SubagentPayload.Parse(listed.Content);
        Assert.Equal(("Inspect the repo", "explore"), (payload.Description, payload.AgentType));
        Assert.Null(payload.Prompt);
        Assert.Null(payload.Transcript);
        Assert.Null(payload.EntriesJson);
        Assert.Equal(run.ToolOutput, listed.ToolOutput);
        // The stored run is untouched.
        Assert.Equal(Legacy(run), Legacy((await store.ReadAllAsync(chat.Id)).Single(static message => message.ToolCallId == "call-0-0")));
    }

    [Fact]
    public async Task OlderFileChange_IsListedByItsLineCounts_AndItsDiffReadsItsEdits()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var dataStore = OpenDataStore(out _);
            var chat = CreateChat(dataStore, exchanges: 3, toolsPerExchange: 3);
            var first = chat.Messages.Single(static message => message.ToolCallId == "call-0-0");
            first.ToolName = "edit";
            first.Content = """{"path":"C:\\repo\\a.cs","old_str":"one\ntwo","new_str":"three"}""";
            var second = chat.Messages.Single(static message => message.ToolCallId == "call-0-2");
            second.ToolName = "edit";
            second.Content = """{"path":"C:\\repo\\a.cs","old_str":"four","new_str":"five\nsix\nseven"}""";
            await dataStore.SaveChatAsync(chat);
            CloseStores();

            var reopened = OpenDataStore(out _);
            using var vm = new ChatViewModel(reopened, TestCopilot.Shared);
            await vm.LoadChatAsync(Assert.Single(reopened.Data.Chats));
            FileChangeItem? change = null;
            for (var wait = 0; wait < 500 && (change = vm.WorkspaceEdits.Items.SingleOrDefault()) is null; wait++)
                await Task.Delay(10);

            // Listed by its counts; its edits stay in storage until its diff reads them.
            Assert.NotNull(change);
            Assert.Empty(change.Edits);
            Assert.Equal((4, 3), (change.LinesAdded, change.LinesRemoved));
            var edits = await Task.Run(change.ReadEdits);
            Assert.Equal([("one\ntwo", "three"), ("four", "five\nsix\nseven")], edits);
        }, CancellationToken.None);
    }

    // ── Tails, deletes and failures ─────────────────────────────────────────

    [Fact]
    public async Task ReadTail_KeepsSendsHeldByPauseWithTheExchangeBeforeThem()
    {
        var dataStore = OpenDataStore(out _);
        var chat = CreateChat(dataStore, exchanges: 3, toolsPerExchange: 2);
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "held 1", IsPendingPausedSend = true });
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "held 2", IsPendingPausedSend = true });
        await dataStore.SaveChatAsync(chat);
        CloseStores();

        var reopened = OpenDataStore(out _);
        var stored = Assert.Single(reopened.Data.Chats);
        await reopened.LoadChatMessagesAsync(stored);

        Assert.Equal(["question 2", "held 1", "held 2"], stored.Messages.Where(static m => m.Role == "user").Select(static m => m.Content));
        Assert.Equal(2, stored.Messages.Count(static message => message.IsPendingPausedSend));
    }

    [Fact]
    public async Task NewChat_IsWholeInMemory_SoLoadingItNeverTrimsIt()
    {
        var dataStore = OpenDataStore(out _);
        var chat = CreateChat(dataStore, exchanges: 3, toolsPerExchange: 2);
        await dataStore.SaveChatAsync(chat);
        var shown = chat.Messages.ToList();

        await dataStore.LoadChatMessagesAsync(chat);

        Assert.Equal(shown, chat.Messages);
        var window = dataStore.GetTranscriptWindow(chat);
        Assert.True(window.IsLoaded);
        Assert.False(window.HasHistory);
    }

    [Fact]
    public async Task HistoryTruncation_KeepsTheChatAsItWasWhenTheWriteFails()
    {
        var dataStore = OpenDataStore(out var store);
        var chat = CreateChat(dataStore, exchanges: 4, toolsPerExchange: 1);
        await dataStore.SaveChatAsync(chat);
        CloseStores();

        var reopened = OpenDataStore(out store);
        var stored = Assert.Single(reopened.Data.Chats);
        await reopened.LoadChatMessagesAsync(stored);
        var tail = stored.Messages.ToList();
        var window = reopened.GetTranscriptWindow(stored);
        var history = await ReadHistoryAsync(reopened, stored, 1, window.TailStartSeq);
        store.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            reopened.TruncateChatHistoryAsync(stored, history.First(static message => message.Content == "question 1")));

        Assert.Equal(tail, stored.Messages);
        Assert.Equal(window, reopened.GetTranscriptWindow(stored));
        Assert.Equal(4, reopened.CountUserMessages(stored));
    }

    [Fact]
    public async Task DetachingAnEditedOlderMessage_KeepsTheEditUntilItIsSaved()
    {
        var dataStore = OpenDataStore(out _);
        var chat = CreateChat(dataStore, exchanges: 3, toolsPerExchange: 1);
        await dataStore.SaveChatAsync(chat);
        CloseStores();

        var reopened = OpenDataStore(out _);
        reopened.Data.Settings.AutoSaveChats = false;
        var stored = Assert.Single(reopened.Data.Chats);
        await reopened.LoadChatMessagesAsync(stored);
        var history = await ReadHistoryAsync(reopened, stored, 1, reopened.GetTranscriptWindow(stored).TailStartSeq);
        reopened.AttachHistoryMessages(stored, history);
        var answer = history.First(static message => message.Role == "assistant");
        answer.InteractionStates = new Dictionary<string, string> { ["form"] = "{\"done\":true}" };

        reopened.DetachHistoryMessages(stored, history);

        // Chats are not saved automatically, so the edit stays in memory: reading the exchange again
        // returns the edited message, and it is saved with the chat when it is saved.
        var again = await ReadHistoryAsync(reopened, stored, answer.StorageSeq, answer.StorageSeq + 1);
        Assert.Same(answer, Assert.Single(again));
        var clean = history.First(static message => message.Role == "user");
        Assert.NotSame(clean, Assert.Single(await ReadHistoryAsync(reopened, stored, clean.StorageSeq, clean.StorageSeq + 1)));
    }

    [Fact]
    public async Task DeletedChat_StaysDeleted_WhenALateSaveArrives()
    {
        var dataStore = OpenDataStore(out var store);
        var deleted = CreateChat(dataStore, exchanges: 2, toolsPerExchange: 1);
        var cleared = CreateChat(dataStore, exchanges: 2, toolsPerExchange: 1);
        await dataStore.SaveChatAsync(deleted);
        await dataStore.SaveChatAsync(cleared);

        await dataStore.DeleteChatFileAsync(deleted.Id);
        deleted.Messages.Add(new ChatMessage { Role = "assistant", Content = "late" });
        await dataStore.SaveChatAsync(deleted);
        await dataStore.DeleteAllChatFilesAsync();
        cleared.Messages.Add(new ChatMessage { Role = "assistant", Content = "late" });
        await dataStore.SaveChatAsync(cleared);

        Assert.Empty(await store.ReadAllAsync(deleted.Id));
        Assert.Empty(await store.ReadAllAsync(cleared.Id));
        CloseStores();
        Assert.Empty(OpenDataStore(out _).Data.Chats);
    }

    [Fact]
    public async Task RollingTheTail_LeavesTheChatAsReopeningItWould()
    {
        var dataStore = OpenDataStore(out _);
        var chat = CreateChat(dataStore, exchanges: 4, toolsPerExchange: 3);
        await dataStore.SaveChatAsync(chat);
        chat.Messages[2].ToolOutput = "edited";
        Assert.False(dataStore.TryRollTail(chat, 15));
        await dataStore.SaveChatAsync(chat);
        var expected = chat.Messages.Select(Legacy).ToList();
        var rolled = chat.Messages.Take(15).ToList();

        Assert.True(dataStore.TryRollTail(chat, 15));

        Assert.Equal("question 3", chat.Messages[0].Content);
        var window = dataStore.GetTranscriptWindow(chat);
        // The older messages a surface shows are the ones history reads return.
        Assert.Equal(rolled, await ReadHistoryAsync(dataStore, chat, 1, window.TailStartSeq));
        Assert.Equal(expected, (await dataStore.ReadChatMessagesAsync(chat)).Select(Legacy));
        var counts = (dataStore.CountUserMessages(chat), dataStore.CountAnsweredAssistantMessages(chat), dataStore.GetTotalMessageCount(chat));
        CloseStores();

        var reopened = OpenDataStore(out _);
        var stored = Assert.Single(reopened.Data.Chats);
        await reopened.LoadChatMessagesAsync(stored);
        Assert.Equal(window, reopened.GetTranscriptWindow(stored));
        Assert.Equal(counts, (reopened.CountUserMessages(stored), reopened.CountAnsweredAssistantMessages(stored), reopened.GetTotalMessageCount(stored)));
    }

    [Fact]
    public async Task ToolDescendants_FollowANestedRunIntoTheTail()
    {
        var dataStore = OpenDataStore(out _);
        var chat = new Chat { Title = "nested" };
        dataStore.Data.Chats.Add(chat);
        chat.Messages.AddRange(
        [
            new ChatMessage { Role = "user", Content = "go" },
            new ChatMessage { Role = "tool", ToolName = "task", ToolCallId = "root" },
            new ChatMessage { Role = "tool", ToolName = "task", ToolCallId = "nested", ParentToolCallId = "root" },
            new ChatMessage { Role = "user", Content = "meanwhile" },
            new ChatMessage { Role = "tool", ToolName = "grep", ToolCallId = "late", ParentToolCallId = "nested" },
            new ChatMessage { Role = "tool", ToolName = "grep", ToolCallId = "other" },
        ]);
        await dataStore.SaveChatAsync(chat);
        CloseStores();

        var reopened = OpenDataStore(out _);
        var stored = Assert.Single(reopened.Data.Chats);
        await reopened.LoadChatMessagesAsync(stored);
        Assert.Equal("meanwhile", stored.Messages[0].Content);

        var tree = await reopened.ReadChatToolDescendantsAsync(stored, "root");

        Assert.Equal(["root", "nested", "late"], tree.Select(static message => message.ToolCallId));
    }

    // ── Searching and prompting from history ────────────────────────────────

    [Fact]
    public async Task HistorySearch_FindsWhatTheTranscriptShows()
    {
        var dataStore = OpenDataStore(out _);
        var chat = CreateChat(dataStore, exchanges: 2, toolsPerExchange: 1);
        chat.Messages.Insert(0, new ChatMessage { Role = "reasoning", Content = "thinking about kiwi" });
        chat.Messages.Insert(1, new ChatMessage
        {
            Role = "user",
            Content = "{\"kiwi\":1}",
            InteractionReceipt = new InteractionReceipt { Title = "Kiwi order", Summary = "two kiwi", Detail = "" },
        });
        chat.Messages.Insert(2, new ChatMessage
        {
            Role = "tool",
            ToolName = "ask_input",
            ToolCallId = "ask",
            InputKind = "form",
            InputDefinition = """{"id":"kiwi_form","title":"Kiwi details","fields":[{"id":"n","label":"Count","type":"text"}]}""",
        });
        await dataStore.SaveChatAsync(chat);
        CloseStores();

        var reopened = OpenDataStore(out _);
        var stored = Assert.Single(reopened.Data.Chats);
        await reopened.LoadChatMessagesAsync(stored);

        // A receipt shows its title and summary, not the submitted content; a form shows its title;
        // reasoning only when it is shown.
        Assert.Equal(3, (await reopened.SearchHistoryTextAsync(stored, "kiwi", includeReasoning: false)).Sum(static m => m.Occurrences));
        Assert.Equal(4, (await reopened.SearchHistoryTextAsync(stored, "kiwi", includeReasoning: true)).Sum(static m => m.Occurrences));
    }

    [Fact]
    public async Task SearchSnapshots_IncludeContentThatIsNotSavedYet()
    {
        var dataStore = OpenDataStore(out _);
        var chat = CreateChat(dataStore, exchanges: 2, toolsPerExchange: 1);
        await dataStore.SaveChatAsync(chat);
        Assert.NotNull(dataStore.GetChatIndexVersion(chat));

        chat.Messages.Add(new ChatMessage { Role = "user", Content = "an unsaved pomegranate" });

        Assert.Null(dataStore.GetChatIndexVersion(chat));
        Assert.Contains(dataStore.GetChatIndexSnapshot(chat).Messages, static message => message.Text.Contains("pomegranate"));
        Assert.Contains(dataStore.GetChatIndexSnapshot(chat).Messages, static message => message.Text.Contains("question 0"));
        await dataStore.SaveChatAsync(chat);
        Assert.NotNull(dataStore.GetChatIndexVersion(chat));
        Assert.Contains(dataStore.GetChatIndexSnapshot(chat).Messages, static message => message.Text.Contains("pomegranate"));
    }

    [Fact]
    public async Task InteractionMessages_IncludeStoredWidgetStatesOfTheWholeChat()
    {
        var dataStore = OpenDataStore(out _);
        var chat = CreateChat(dataStore, exchanges: 4, toolsPerExchange: 1);
        var answers = chat.Messages.Where(static message => message.Role == "assistant").ToList();
        answers[0].InteractionStates = new Dictionary<string, string> { ["a"] = "{}" };
        answers[1].InteractionStates = new Dictionary<string, string> { ["b"] = "{}" };
        answers[1].ActiveSkills.Add(new SkillReference { Name = "writer" });
        var asking = chat.Messages.First(static message => message.Role == "user");
        asking.ActiveSkills.Add(new SkillReference { Name = "writer" });
        await dataStore.SaveChatAsync(chat);
        CloseStores();

        var reopened = OpenDataStore(out _);
        var stored = Assert.Single(reopened.Data.Chats);
        await reopened.LoadChatMessagesAsync(stored);

        var messages = await reopened.ReadInteractionMessagesAsync(stored);

        Assert.Equal([answers[0].Id, answers[1].Id], messages.Where(static m => m.InteractionStates is { Count: > 0 }).Select(static m => m.Id));
        Assert.Contains("block b", InteractiveResponseState.BuildPromptContext(messages));
        // Only answers hold widget states: a user message sharing the skill facet stays in storage.
        Assert.DoesNotContain(messages, message => message.Id == asking.Id);
    }

    [Fact]
    public async Task OlderApps_StayInMemoryWithTheTail_AndTheirChangesAreSaved()
    {
        var dataStore = OpenDataStore(out _);
        var chat = CreateChat(dataStore, exchanges: 4, toolsPerExchange: 1);
        var opener = chat.Messages.First(static message => message.Role == "tool");
        opener.EmbeddedApp = new EmbeddedApp
        {
            Kind = EmbeddedAppKind.Canvas, Title = "Flow", SessionId = "session", InstanceId = "flow", CanvasId = "draw", ExtensionId = "project:draw",
        };
        await dataStore.SaveChatAsync(chat);
        CloseStores();

        var reopened = OpenDataStore(out var store);
        var stored = Assert.Single(reopened.Data.Chats);
        await reopened.LoadChatMessagesAsync(stored);

        // Only the newest exchange is the tail, but the chat's app is in memory with it.
        Assert.DoesNotContain(stored.Messages, message => message.Id == opener.Id);
        var app = Assert.Single(reopened.GetAppMessages(stored));
        Assert.Equal(opener.Id, app.Id);
        Assert.Equal(opener.Id, Assert.Single(await store.ReadByKindAsync(stored.Id, [ChatMessageKinds.App])).Id);

        // An exchange that showed it letting go keeps it, and reads of its exchange return the same object.
        reopened.DetachHistoryMessages(stored, [app]);
        Assert.Same(app, Assert.Single(reopened.GetAppMessages(stored)));
        Assert.Same(app, (await reopened.ReadChatMessagesAsync(stored)).Single(message => message.Id == opener.Id));

        // Changing it in place is saved with the chat.
        Assert.Same(app, EmbeddedAppData.CloseCanvas(reopened.GetAppMessages(stored), "flow"));
        await reopened.SaveChatAsync(stored);
        CloseStores();
        var again = OpenDataStore(out _);
        var reloaded = Assert.Single(again.Data.Chats);
        await again.LoadChatMessagesAsync(reloaded);
        Assert.True(Assert.Single(again.GetAppMessages(reloaded)).EmbeddedApp!.IsClosed);
    }

    [Fact]
    public async Task AppOfAnOlderExchange_IsListedOpensAndIsTheCardItsExchangeShows()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var dataStore = OpenDataStore(out _);
            var chat = CreateChat(dataStore, exchanges: 3, toolsPerExchange: 1);
            var opener = chat.Messages.Single(static message => message.ToolCallId == "call-0-0");
            opener.ToolName = "open_canvas";
            opener.EmbeddedApp = new EmbeddedApp { Kind = EmbeddedAppKind.Canvas, Title = "Flow", InstanceId = "flow", CanvasId = "draw", ExtensionId = "project:draw" };
            await dataStore.SaveChatAsync(chat);
            CloseStores();

            var reopened = OpenDataStore(out _);
            using var vm = new ChatViewModel(reopened, TestCopilot.Shared);
            await vm.LoadChatAsync(Assert.Single(reopened.Data.Chats));
            vm.RebuildWorkspacePanel();

            // The Workspace lists the app of the first exchange, which only storage holds, and it opens.
            Assert.True(vm.HasTranscriptHistory);
            var item = Assert.Single(vm.AppItems);
            Assert.Equal("Flow", item.Title);
            item.OpenCommand.Execute(null);
            Assert.Same(item, vm.SelectedApp);

            // Its exchange, once built, shows that same item as its card.
            Assert.True(await vm.EnsureMessageInTranscriptAsync(opener.Id));
            Assert.Contains(item, vm.TranscriptTurns.SelectMany(static turn => turn.EnumerateItems()));
        }, CancellationToken.None);
    }

    // ── Legacy import edge cases ────────────────────────────────────────────

    [Fact]
    public async Task LegacyImport_KeepsWhatWasReadBeforeTheFileIsDamaged()
    {
        var legacyDirectory = Path.Combine(_directory, "chats");
        Directory.CreateDirectory(legacyDirectory);
        var chat = new Chat { Title = "damaged" };
        var messages = Enumerable.Range(0, 3).Select(i => new ChatMessage { Role = i == 0 ? "user" : "assistant", Content = $"m{i}" }).ToList();
        var json = JsonSerializer.Serialize(messages, AppDataJsonContext.Default.ListChatMessage);
        await File.WriteAllTextAsync(Path.Combine(legacyDirectory, $"{chat.Id}.json"), json[..^1] + ",{\"role\":\"assistant\",\"cont");

        var (store, _, importer) = DataStore.OpenChatStorage(DatabasePath, [chat], legacyDirectory, null);
        _stores.Add(store);
        using (importer)
            await importer.EnsureImportedAsync(chat.Id);

        Assert.Equal(messages.Select(Legacy), (await store.ReadAllAsync(chat.Id)).Select(Legacy));
        Assert.Equal(ChatImportState.Failed, store.GetImportState(chat.Id));
        CloseStores();
        var (reopened, _, again) = DataStore.OpenChatStorage(DatabasePath, [chat], legacyDirectory, null);
        _stores.Add(reopened);
        using (again)
            Assert.False(again.IsPending(chat.Id));
    }

    [Fact]
    public async Task LegacyImport_FileThatCannotBeRead_GoesInBeforeWhatWasAddedMeanwhile()
    {
        var legacyDirectory = Path.Combine(_directory, "chats");
        Directory.CreateDirectory(legacyDirectory);
        var chat = new Chat { Title = "locked" };
        var messages = Enumerable.Range(0, 5).Select(i => new ChatMessage { Role = i == 0 ? "user" : "assistant", Content = $"m{i}" }).ToList();
        var file = Path.Combine(legacyDirectory, $"{chat.Id}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(messages, AppDataJsonContext.Default.ListChatMessage));

        var (store, _, importer) = DataStore.OpenChatStorage(DatabasePath, [chat], legacyDirectory, null);
        _stores.Add(store);
        using (importer)
        {
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                await importer.EnsureImportedAsync(chat.Id);
            Assert.Equal(ChatImportState.Unreadable, store.GetImportState(chat.Id));

            // The chat opens empty, and the user carries on in it.
            var dataStore = new DataStore(new AppData { Settings = new UserSettings { AutoSaveChats = true } }, store, legacyImporter: importer);
            var opened = Assert.Single(dataStore.Data.Chats);
            await dataStore.LoadChatMessagesAsync(opened);
            Assert.Empty(opened.Messages);
            opened.Messages.Add(new ChatMessage { Role = "user", Content = "carried on" });
            await dataStore.SaveChatAsync(opened);
        }

        // Once the file can be read, its transcript goes in before what was added meanwhile.
        CloseStores();
        var (reopened, _, again) = DataStore.OpenChatStorage(DatabasePath, [chat], legacyDirectory, null);
        _stores.Add(reopened);
        using (again)
        {
            Assert.True(again.IsPending(chat.Id));
            await again.EnsureImportedAsync(chat.Id);
        }

        Assert.Equal(ChatImportState.Imported, reopened.GetImportState(chat.Id));
        Assert.Equal(
            messages.Select(static m => m.Content).Append("carried on"),
            (await reopened.ReadAllAsync(chat.Id)).Select(static m => m.Content));
        Assert.Equal(messages.Count + 1, reopened.GetStoredMessageCount(chat.Id));
    }

    [Fact]
    public async Task AppendingToAChatStillBeingImported_FollowsItsImportedTranscript()
    {
        var legacyDirectory = Path.Combine(_directory, "chats");
        Directory.CreateDirectory(legacyDirectory);
        var chat = new Chat { Title = "pending" };
        var messages = Enumerable.Range(0, 6).Select(i => new ChatMessage { Role = i % 2 == 0 ? "user" : "assistant", Content = $"m{i}" }).ToList();
        await File.WriteAllTextAsync(Path.Combine(legacyDirectory, $"{chat.Id}.json"),
            JsonSerializer.Serialize(messages, AppDataJsonContext.Default.ListChatMessage));
        var (store, chats, importer) = DataStore.OpenChatStorage(DatabasePath, [chat], legacyDirectory, null);
        _stores.Add(store);
        using (importer)
        {
            var dataStore = new DataStore(new AppData { Settings = new UserSettings { AutoSaveChats = true } }, store, legacyImporter: importer);
            var pending = Assert.Single(dataStore.Data.Chats);
            Assert.True(importer.IsPending(pending.Id));

            // A job's prompt arrives before anything read the chat.
            var appended = new ChatMessage { Role = "user", Content = "from a job" };
            pending.Messages.Add(appended);
            await dataStore.SaveChatAsync(pending);

            Assert.Equal(messages.Select(static m => m.Content).Append("from a job"), (await store.ReadAllAsync(pending.Id)).Select(static m => m.Content));
        }
    }

    [Fact]
    public void LegacyFiles_AreDeletedWithTheirChats()
    {
        var legacyDirectory = Path.Combine(_directory, "chats");
        var backup = Path.Combine(_directory, LegacyChatImporter.BackupFolderName);
        Directory.CreateDirectory(legacyDirectory);
        Directory.CreateDirectory(backup);
        var deleted = Guid.NewGuid();
        var kept = Guid.NewGuid();
        File.WriteAllText(Path.Combine(legacyDirectory, $"{deleted}.json"), "[]");
        File.WriteAllText(Path.Combine(backup, $"{deleted}.json"), "[]");
        File.WriteAllText(Path.Combine(backup, $"{deleted}.20250101000000.json"), "[]");
        File.WriteAllText(Path.Combine(backup, $"{kept}.json"), "[]");
        var store = OpenStore();
        using var importer = new LegacyChatImporter(store, legacyDirectory, []);

        importer.DeleteLegacyFile(deleted);

        Assert.Equal([$"{kept}.json"], Directory.GetFiles(backup).Select(Path.GetFileName));
        Assert.Empty(Directory.GetFiles(legacyDirectory));
        importer.DeleteLegacyFiles();
        Assert.False(Directory.Exists(backup));
        Assert.False(Directory.Exists(legacyDirectory));
    }

    [Fact]
    public void LegacyIndex_LosesADeletedChat_AndKeepsEverythingElse()
    {
        var index = Path.Combine(_directory, "data.pre-sqlite.json");
        var deleted = new Chat { Title = "deleted title", Preview = "private words" };
        var kept = new Chat { Title = "kept" };
        var data = new AppData { Settings = new UserSettings { AutoSaveChats = false } };
        data.Chats.AddRange([deleted, kept]);
        File.WriteAllText(index, JsonSerializer.Serialize(data, AppDataJsonContext.Default.AppData));

        DataStore.RemoveFromLegacyIndex(index, deleted.Id);

        var text = File.ReadAllText(index);
        var after = JsonSerializer.Deserialize(text, AppDataJsonContext.Default.AppData)!;
        Assert.Equal([kept.Id], after.Chats.Select(static chat => chat.Id));
        Assert.False(after.Settings.AutoSaveChats);
        Assert.DoesNotContain("private words", text);
    }

    [Fact]
    public async Task ClearAll_KeepsAChatStartedWhileAnImportUnderWayEnds()
    {
        var legacyDirectory = Path.Combine(_directory, "chats");
        Directory.CreateDirectory(legacyDirectory);
        var legacy = new Chat { Title = "legacy" };
        var messages = Enumerable.Range(0, 4000).Select(i => new ChatMessage { Role = i % 2 == 0 ? "user" : "assistant", Content = $"m{i}" }).ToList();
        await File.WriteAllTextAsync(Path.Combine(legacyDirectory, $"{legacy.Id}.json"),
            JsonSerializer.Serialize(messages, AppDataJsonContext.Default.ListChatMessage));
        var (store, _, importer) = DataStore.OpenChatStorage(DatabasePath, [legacy], legacyDirectory, null);
        _stores.Add(store);
        using (importer)
        {
            var dataStore = new DataStore(new AppData { Settings = new UserSettings { AutoSaveChats = true } }, store, legacyImporter: importer);
            // The writer is held, so the import is still under way when Clear All starts.
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = store.Writer.Enqueue(null, (_, _) => release.Task.Wait());
            var importing = importer.EnsureImportedAsync(legacy.Id);
            await Task.Delay(200);

            // Clear All waits for that import; a chat started meanwhile is not cleared.
            var clearing = dataStore.DeleteAllChatFilesAsync();
            dataStore.Data.Chats.Clear();
            var started = new Chat { Title = "started meanwhile" };
            started.Messages.Add(new ChatMessage { Role = "user", Content = "hello" });
            dataStore.Data.Chats.Add(started);
            var saving = dataStore.SaveChatAsync(started);
            release.SetResult();
            await held;
            await saving;
            await clearing;
            await Record.ExceptionAsync(() => importing);

            Assert.Equal(["hello"], (await store.ReadAllAsync(started.Id)).Select(static message => message.Content));
            Assert.Empty(await store.ReadAllAsync(legacy.Id));
        }
    }

    [Fact]
    public async Task MetadataUpdate_NeverBringsBackADeletedChat()
    {
        var dataStore = OpenDataStore(out var store);
        var chat = CreateChat(dataStore, exchanges: 1, toolsPerExchange: 1);
        await dataStore.SaveChatAsync(chat);

        // A metadata save prepared before the chat was deleted lands after the deletion.
        var metadata = (chat.Id, chat.UpdatedAt.ToUnixTimeMilliseconds(), ChatStorageJson.SerializeChat(chat));
        await store.DeleteChatAsync(chat.Id);
        await store.UpdateChatMetadataAsync([metadata]);

        Assert.DoesNotContain(store.LoadChats(), stored => stored.Chat.Id == chat.Id);
    }

    [Fact]
    public async Task HistoryCut_KeepsAMessageAddedWhileItsWriteIsUnderWay()
    {
        var dataStore = OpenDataStore(out _);
        var chat = CreateChat(dataStore, exchanges: 4, toolsPerExchange: 1);
        await dataStore.SaveChatAsync(chat);
        CloseStores();

        var reopened = OpenDataStore(out var store);
        var stored = Assert.Single(reopened.Data.Chats);
        await reopened.LoadChatMessagesAsync(stored);
        var window = reopened.GetTranscriptWindow(stored);
        var history = await ReadHistoryAsync(reopened, stored, 1, window.TailStartSeq);
        var cutAt = history.First(static message => message.Content == "question 1");

        // The cut takes effect at once; a message a job adds while its write waits comes after it.
        var cutting = reopened.TruncateChatHistoryAsync(stored, cutAt);
        stored.Messages.Add(new ChatMessage { Role = "user", Content = "from a job" });
        await reopened.SaveChatAsync(stored);
        await cutting;

        Assert.Equal(["question 0", "answer 0", "from a job"],
            (await store.ReadAllAsync(stored.Id)).Where(static m => m.Role != "tool").Select(static m => m.Content));
        Assert.Equal(2, reopened.CountUserMessages(stored));
    }

    [Fact]
    public async Task Editing_IsUnavailable_WhenPartOfTheChatsLegacyTranscriptCouldNotBeRead()
    {
        var legacyDirectory = Path.Combine(_directory, "chats");
        Directory.CreateDirectory(legacyDirectory);
        var chat = new Chat { Title = "damaged" };
        var messages = Enumerable.Range(0, 4).Select(i => new ChatMessage { Role = i % 2 == 0 ? "user" : "assistant", Content = $"m{i}" }).ToList();
        var json = JsonSerializer.Serialize(messages, AppDataJsonContext.Default.ListChatMessage);
        await File.WriteAllTextAsync(Path.Combine(legacyDirectory, $"{chat.Id}.json"), json[..^1] + ",{\"role\":\"user\",\"cont");
        var (store, _, importer) = DataStore.OpenChatStorage(DatabasePath, [chat], legacyDirectory, null);
        _stores.Add(store);
        using (importer)
        {
            await importer.EnsureImportedAsync(chat.Id);
            using var session = HeadlessTestSession.Start();
            await session.Dispatch(async () =>
            {
                var dataStore = new DataStore(new AppData { Settings = new UserSettings { AutoSaveChats = true } }, store, legacyImporter: importer);
                var damaged = Assert.Single(dataStore.Data.Chats);
                Assert.True(dataStore.IsMissingLegacyMessages(damaged));
                using var vm = new ChatViewModel(dataStore, TestCopilot.Shared);
                await vm.LoadChatAsync(damaged);
                var user = damaged.Messages.Last(static message => message.Role == "user");

                // Its Copilot session holds turns the chat could not read, so it is not rewound by position.
                typeof(ChatViewModel).GetMethod("BeginComposerEdit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [user]);

                Assert.False(vm.IsEditingMessage);
                Assert.Equal(Lumi.Localization.Loc.Get("Chat_EditUnavailableMissingMessages"), vm.StatusText);
            }, CancellationToken.None);
        }
    }

    [Fact]
    public void Database_OpensWhatANewerLumiOnlyAddedTo_AndRefusesWhatItChanged()
    {
        OpenStore();
        CloseStores();
        SetSchema(version: 2, minimumReader: 1);
        OpenStore();
        CloseStores();

        SetSchema(version: 3, minimumReader: 2);
        var error = Assert.Throws<InvalidDataException>(() => new ChatStore(DatabasePath));
        Assert.Contains("newer version of Lumi", error.Message);

        void SetSchema(int version, int minimumReader)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {version}; UPDATE meta SET value = '{minimumReader}' WHERE key = 'min_reader_version';";
            command.ExecuteNonQuery();
        }
    }

    // ── A long live session ─────────────────────────────────────────────────

    [Fact]
    public async Task TailRoll_MovesOlderExchangesIntoHistory_LeavingTheTranscriptAsItWas()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var dataStore = OpenDataStore(out _);
            var chat = CreateChat(dataStore, exchanges: 5, toolsPerExchange: 90);
            var users = chat.Messages.Where(static message => message.Role == "user").Select(static message => message.Id).ToList();
            using var vm = new ChatViewModel(dataStore, TestCopilot.Shared);
            await vm.LoadChatAsync(chat);
            var turns = vm.TranscriptTurns.ToList();
            var transcript = Describe(vm.TranscriptTurns);

            await vm.RollTailIntoHistoryAsync(chat);

            // The same turns stay on screen, now the history's; the tail is the newest exchange.
            Assert.Equal(turns, vm.TranscriptTurns);
            Assert.Equal(transcript, Describe(vm.TranscriptTurns));
            Assert.Equal(4, vm.TranscriptHistory.Segments.Count);
            Assert.Equal("question 4", chat.Messages[0].Content);
            Assert.All(vm.Messages, viewModel => Assert.Contains(viewModel.Message, chat.Messages));

            // The chat goes on, and reads as it does once reopened.
            foreach (var message in new[] { new ChatMessage { Role = "user", Content = "question 5" }, new ChatMessage { Role = "assistant", Content = "answer 5" } })
            {
                chat.Messages.Add(message);
                vm.Messages.Add(new ChatMessageViewModel(message));
            }

            await dataStore.SaveChatAsync(chat);
            var continued = Describe(vm.TranscriptTurns);
            CloseStores();

            var reopened = OpenDataStore(out _);
            using var fresh = new ChatViewModel(reopened, TestCopilot.Shared);
            await fresh.LoadChatAsync(Assert.Single(reopened.Data.Chats));
            foreach (var user in users)
                Assert.True(await fresh.EnsureMessageInTranscriptAsync(user, includeFoldedSteps: true));
            Assert.Equal(continued, Describe(fresh.TranscriptTurns));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TailRoll_LetsGoOfRolledWork_OnceTheHistoryReleasesIt()
    {
        using var session = HeadlessTestSession.Start();
        ChatViewModel vm = null!;
        WeakReference[] rolled = [];
        await session.Dispatch(async () =>
        {
            var dataStore = OpenDataStore(out _);
            var chat = CreateChat(dataStore, exchanges: 5, toolsPerExchange: 90);
            vm = new ChatViewModel(dataStore, TestCopilot.Shared);
            await vm.LoadChatAsync(chat);
            rolled = WeakReferencesToFirstExchange(vm, chat);

            await vm.RollTailIntoHistoryAsync(chat);
            vm.TranscriptHistory.ReleaseAll();
            Assert.DoesNotContain(vm.TranscriptHistory.Segments, static segment => segment.IsHydrated);
        }, CancellationToken.None);

        try
        {
            for (var attempt = 0; attempt < 8 && rolled.Any(static reference => reference.IsAlive); attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await session.Dispatch(static () => { }, CancellationToken.None);
                await Task.Delay(25);
            }

            Assert.All(rolled, static reference => Assert.False(reference.IsAlive));
        }
        finally
        {
            await session.Dispatch(() => vm.Dispose(), CancellationToken.None);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] WeakReferencesToFirstExchange(ChatViewModel vm, Chat chat)
        => [new(chat.Messages[0]), new(chat.Messages[1]), new(vm.Messages[0]), new(vm.Messages[1]), new(vm.TranscriptTurns[0]), new(vm.TranscriptTurns[1])];

    [Fact]
    public async Task TailRoll_WhileWorkGoesOnInTheBackground_KeepsWhatThatWorkCanChangeLive()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var dataStore = OpenDataStore(out _);
            var chat = CreateChat(dataStore, exchanges: 6, toolsPerExchange: 150);
            // A background agent the fourth exchange started is still at work.
            var running = chat.Messages.Single(static message => message.ToolCallId == "call-3-0");
            running.ToolStatus = "InProgress";
            using var vm = new ChatViewModel(dataStore, TestCopilot.Shared);
            await vm.LoadChatAsync(chat);
            var runtime = (ChatRuntimeState)typeof(ChatViewModel)
                .GetMethod("GetOrCreateRuntimeState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [chat.Id])!;
            runtime.IsSessionActive = true;

            await vm.RollTailIntoHistoryAsync(chat);

            Assert.Equal("question 3", chat.Messages[0].Content);
            Assert.Contains(running, chat.Messages);
            Assert.Equal(3, vm.TranscriptHistory.Segments.Count);

            // Once that work is done and the session settles, the rest follows.
            running.ToolStatus = "Completed";
            runtime.IsSessionActive = false;
            await vm.RollTailIntoHistoryAsync(chat);

            Assert.Equal("question 5", chat.Messages[0].Content);
            Assert.Equal(5, vm.TranscriptHistory.Segments.Count);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TailRoll_LetsSearchSeeTheNewOwnersBeforeReleasingAny()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var dataStore = OpenDataStore(out _);
            // More exchanges than the history keeps built, so the roll releases some at once.
            var chat = CreateChat(dataStore, exchanges: TranscriptHistory.MaxHydratedSegments + 4, toolsPerExchange: 20);
            using var vm = new ChatViewModel(dataStore, TestCopilot.Shared);
            await vm.LoadChatAsync(chat);
            var newest = chat.Messages.Last(static message => message.Role == "user");
            var turns = vm.TranscriptTurns.ToList();
            var rolled = turns.Take(turns.FindIndex(turn => turn.StableId == $"turn:message:{newest.Id}")).ToList();
            var owned = new List<bool>();
            vm.TranscriptHistory.Changed += () => owned.Add(rolled.All(static turn => turn.HistorySegment is not null));

            await vm.RollTailIntoHistoryAsync(chat);

            Assert.True(owned[0]);
            Assert.Contains(rolled, static turn => turn.HistorySegment is null);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReleasedExchange_IsBuiltAgainAtTheHeightItWasShown()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var dataStore = OpenDataStore(out _);
            var chat = CreateChat(dataStore, exchanges: 3, toolsPerExchange: 4);
            await dataStore.SaveChatAsync(chat);
            CloseStores();

            var reopened = OpenDataStore(out _);
            using var vm = new ChatViewModel(reopened, TestCopilot.Shared);
            await vm.LoadChatAsync(Assert.Single(reopened.Data.Chats));
            var segment = vm.TranscriptHistory.Segments[0];
            var shown = (await vm.TranscriptHistory.EnsureHydratedAsync(segment))!;
            var heights = new Dictionary<string, double>();
            for (var i = 0; i < shown.Count; i++)
                heights[shown[i].StableId] = shown[i].MeasuredHeight = 100 + i * 37;

            vm.TranscriptHistory.ReleaseAll();
            var rebuilt = (await vm.TranscriptHistory.EnsureHydratedAsync(segment))!;

            Assert.NotSame(shown[0], rebuilt[0]);
            Assert.Equal(heights, rebuilt.ToDictionary(static turn => turn.StableId, static turn => turn.MeasuredHeight));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DisplaySettingChange_BuildsShownHistoryAgainWithTheSetting()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var dataStore = OpenDataStore(out _);
            var chat = CreateChat(dataStore, exchanges: 3, toolsPerExchange: 4);
            await dataStore.SaveChatAsync(chat);
            CloseStores();

            var reopened = OpenDataStore(out _);
            reopened.Data.Settings.ShowToolCalls = true;
            using var vm = new ChatViewModel(reopened, TestCopilot.Shared);
            await vm.LoadChatAsync(Assert.Single(reopened.Data.Chats));
            var segment = vm.TranscriptHistory.Segments[0];
            var withToolCalls = Describe((await vm.TranscriptHistory.EnsureHydratedAsync(segment))!);

            reopened.Data.Settings.ShowToolCalls = false;
            vm.RebuildTranscriptAndHistory();

            Assert.False(segment.IsHydrated);
            Assert.NotEqual(withToolCalls, Describe((await vm.TranscriptHistory.EnsureHydratedAsync(segment))!));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SavedPlan_WhenTheNewestMessageWentUnanswered_FollowsTheAnswerBeforeIt()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var dataStore = OpenDataStore(out _);
            var chat = CreateChat(dataStore, exchanges: 3, toolsPerExchange: 2);
            chat.Messages.Add(new ChatMessage { Role = "user", Content = "question 3" });
            chat.Messages.Add(new ChatMessage { Role = "error", Content = "The request failed." });
            chat.PlanContent = "# Plan";
            await dataStore.SaveChatAsync(chat);
            CloseStores();

            var reopened = OpenDataStore(out _);
            using var vm = new ChatViewModel(reopened, TestCopilot.Shared);
            await vm.LoadChatAsync(Assert.Single(reopened.Data.Chats));

            // Not under the unanswered message, but after the answer before it, each time its exchange is built.
            Assert.DoesNotContain(
                vm.TranscriptTurns.Where(static turn => turn.HistorySegment is null).SelectMany(static turn => turn.Items),
                static item => item is PlanCardItem);
            var newest = vm.TranscriptHistory.Segments[^1];
            for (var build = 0; build < 2; build++)
            {
                var turns = (await vm.TranscriptHistory.EnsureHydratedAsync(newest))!;
                var withPlan = Assert.Single(turns, static turn => turn.Items.Any(static item => item is PlanCardItem));
                Assert.Single(withPlan.Items, static item => item is PlanCardItem);
                Assert.Contains(withPlan.Items, static item => item is AssistantMessageItem { Content: "answer 2" });
                vm.TranscriptHistory.ReleaseAll();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task OlderRun_KeepsOnlyItsIndexRow_UntilItsPageOpens()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var dataStore = OpenDataStore(out _);
            var chat = CreateChat(dataStore, exchanges: 3, toolsPerExchange: 2);
            var entries = SubagentRunLog.Append(null, SubagentRunEntryKind.Assistant, "Found the cause.", DateTimeOffset.Now.AddMinutes(-30));
            var run = chat.Messages.Single(static message => message.ToolCallId == "call-0-0");
            run.ToolName = "task";
            run.Content = "{\"description\":\"Inspect the repo\",\"agent_type\":\"explore\",\"prompt\":\"Look for the cause\",\"entries\":" + entries + "}";
            chat.Messages.Single(static message => message.ToolCallId == "call-0-1").ParentToolCallId = "call-0-0";
            await dataStore.SaveChatAsync(chat);
            CloseStores();

            var reopened = OpenDataStore(out _);
            using var vm = new ChatViewModel(reopened, TestCopilot.Shared);
            await vm.LoadChatAsync(Assert.Single(reopened.Data.Chats));
            SubagentToolCallItem? listed = null;
            for (var wait = 0; wait < 500 && (listed = vm.AllSubagentRuns().SingleOrDefault()) is null; wait++)
                await Task.Delay(10);

            // The Agents index lists the run; what only its page shows stays in storage.
            Assert.NotNull(listed);
            Assert.Equal("Inspect the repo", listed.Title);
            Assert.Null(listed.Prompt);
            Assert.Empty(listed.RunEntries);

            // Its page reads it back with the run's steps, and shows no empty state meanwhile.
            vm.OpenSubagentRunCommand.Execute(listed);
            Assert.False(vm.ShowSubagentRunEmptyState);
            for (var wait = 0; wait < 500 && listed.Prompt is null; wait++)
                await Task.Delay(10);

            Assert.Equal("Look for the cause", listed.Prompt);
            Assert.Equal("Found the cause.", Assert.Single(listed.RunEntries).Text);
            Assert.True(vm.HasSubagentRunContent);

            // Back at the index, it lets go again: its detail, and the steps its page read.
            var storedSteps = typeof(ChatViewModel).GetField("_storedRunMessages", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.NotNull(storedSteps.GetValue(vm));
            vm.BackToSubagentIndexCommand.Execute(null);
            Assert.Null(listed.Prompt);
            Assert.Empty(listed.RunEntries);
            Assert.Null(storedSteps.GetValue(vm));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task BuildingAnInterruptedOlderExchange_SavesNothingWhileChatsAreNotSavedAutomatically()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var dataStore = OpenDataStore(out _);
            var chat = CreateChat(dataStore, exchanges: 3, toolsPerExchange: 2);
            var asked = chat.Messages.Single(static message => message.ToolCallId == "call-0-0");
            asked.ToolName = "ask_input";
            asked.ToolStatus = "InProgress";
            await dataStore.SaveChatAsync(chat);
            CloseStores();

            var reopened = OpenDataStore(out var store);
            reopened.Data.Settings.AutoSaveChats = false;
            var stored = Assert.Single(reopened.Data.Chats);
            using var vm = new ChatViewModel(reopened, TestCopilot.Shared);
            await vm.LoadChatAsync(stored);
            await vm.TranscriptHistory.EnsureHydratedAsync(vm.TranscriptHistory.Segments[0]);

            // Shown stopped, as work a crash cut off is, but not written while saving is off.
            Assert.Equal("Stopped", vm.TranscriptHistory.HydratedMessages.Single(message => message.Id == asked.Id).ToolStatus);
            await store.Writer.Enqueue(null, static (_, _) => { });
            Assert.Equal("InProgress", (await store.ReadAllAsync(stored.Id)).Single(message => message.Id == asked.Id).ToolStatus);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EditingAnOlderMessage_WhenTheChatCannotBeSaved_KeepsTheChatAndTheEdit()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var (reopened, store, stored, vm, older) = await OpenToEditOlderQuestionAsync();
            using (vm)
            {
                store.Dispose();

                await vm.SendMessageCommand.ExecuteAsync(null);

                // Nothing was cut or changed; the edit is back in the composer.
                Assert.Equal(4, reopened.CountUserMessages(stored));
                Assert.True(vm.IsEditingMessage);
                Assert.Equal("question 1, edited", vm.PromptText);
                Assert.Equal("question 1", older.Content);
                Assert.False(older.HasUnpersistedChanges);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EditingAnOlderMessage_LeavesItAsStored_WhenOnlyTheCutFails()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var (_, store, stored, vm, older) = await OpenToEditOlderQuestionAsync();
            using (vm)
            {
                // Saves still go through, but the cut cannot: a disk too full to delete a long history.
                ExecuteSql("CREATE TRIGGER no_cut BEFORE DELETE ON messages BEGIN SELECT RAISE(ABORT, 'disk full'); END;");

                await vm.SendMessageCommand.ExecuteAsync(null);
                await store.Writer.Enqueue(null, static (_, _) => { });

                Assert.Equal("question 1", (await store.ReadAllAsync(stored.Id)).Single(message => message.Id == older.Id).Content);
                Assert.True(vm.IsEditingMessage);
                Assert.Equal("question 1, edited", vm.PromptText);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EditingAnOlderMessage_HoldsTheChatAgainstOtherSendsWhileItsHistoryIsCut()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var (_, store, stored, vm, _) = await OpenToEditOlderQuestionAsync();
            using (vm)
            {
                // The cut fails in the end, so the edit stops before it would reach Copilot.
                ExecuteSql("CREATE TRIGGER no_cut BEFORE DELETE ON messages BEGIN SELECT RAISE(ABORT, 'disk full'); END;");
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var held = store.Writer.Enqueue(null, (_, _) => release.Task.Wait());
                var editing = Task.CompletedTask;
                try
                {
                    editing = vm.SendMessageCommand.ExecuteAsync(null);

                    // While the cut waits for the database, a job, the phone or the composer cannot start
                    // a turn: it would be counted among the turns kept before the edit.
                    Assert.False(editing.IsCompleted);
                    Assert.True(vm.IsChatBusyForSend(stored.Id));
                    Assert.Null(vm.TryReserveExternalSend(stored.Id));
                }
                finally
                {
                    release.SetResult();
                }

                await held;
                await editing;
                using var later = vm.TryReserveExternalSend(stored.Id);
                Assert.NotNull(later);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EditingAnOlderMessage_WhileAnotherSendStarts_SendsNothingAndKeepsTheEdit()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var (reopened, _, stored, vm, older) = await OpenToEditOlderQuestionAsync();
            using (vm)
            {
                // A job's message is starting in the chat.
                using var job = vm.TryReserveExternalSend(stored.Id);
                Assert.NotNull(job);

                await vm.SendMessageCommand.ExecuteAsync(null);

                Assert.Equal(Lumi.Localization.Loc.Get("Chat_ResendChatBusy"), vm.StatusText);
                Assert.True(vm.IsEditingMessage);
                Assert.Equal("question 1, edited", vm.PromptText);
                Assert.Equal(4, reopened.CountUserMessages(stored));
                Assert.Equal("question 1", older.Content);

                // Once its reply has started the chat cannot be edited, but the text stays as a draft.
                vm.IsBusy = true;
                await vm.SendMessageCommand.ExecuteAsync(null);

                Assert.False(vm.IsEditingMessage);
                Assert.Equal("question 1, edited", vm.PromptText);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EditingAnOlderMessage_ThatComesBackUnsent_StillCancelsToWhatWasThereBefore()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var (reopened, _, stored, vm, _) = await OpenToEditOlderQuestionAsync(draft: "an unrelated draft");
            using (vm)
            {
                var agent = new LumiAgent { Name = "Reviewer" };
                reopened.Data.Agents.Add(agent);
                vm.SetActiveAgent(agent);
                using (var job = vm.TryReserveExternalSend(stored.Id))
                {
                    Assert.NotNull(job);
                    await vm.SendMessageCommand.ExecuteAsync(null);
                }

                Assert.True(vm.IsEditingMessage);
                Assert.Equal(agent.Id, stored.AgentId);
                // Cancelling the returned edit restores the composer, the chat's agent and the selection
                // the session was built with as they were before the edit began.
                await vm.CancelComposerEditCommand.ExecuteAsync(null);

                Assert.False(vm.IsEditingMessage);
                Assert.Equal("an unrelated draft", vm.PromptText);
                Assert.Null(vm.ActiveAgent);
                Assert.Null(stored.AgentId);
            }
        }, CancellationToken.None);
    }

    // A stored four-exchange chat, reopened with "question 1" (in its history) being edited in the
    // composer, which held the given draft before the edit began.
    private async Task<(DataStore DataStore, ChatStore Store, Chat Chat, ChatViewModel Vm, ChatMessage Older)> OpenToEditOlderQuestionAsync(string draft = "")
    {
        var dataStore = OpenDataStore(out _);
        var chat = CreateChat(dataStore, exchanges: 4, toolsPerExchange: 1);
        var question = chat.Messages.Single(static message => message.Content == "question 1").Id;
        await dataStore.SaveChatAsync(chat);
        CloseStores();

        var reopened = OpenDataStore(out var store);
        var stored = Assert.Single(reopened.Data.Chats);
        var vm = new ChatViewModel(reopened, TestCopilot.Shared);
        await vm.LoadChatAsync(stored);
        Assert.True(await vm.EnsureMessageInTranscriptAsync(question));
        var older = vm.TranscriptHistory.HydratedMessages.Single(message => message.Id == question);
        vm.PromptText = draft;
        typeof(ChatViewModel).GetMethod("BeginComposerEdit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [older]);
        Assert.True(vm.IsEditingMessage);
        vm.PromptText = "question 1, edited";
        return (reopened, store, stored, vm, older);
    }

    private void ExecuteSql(string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void HistoryFacts_CutAtAPosition_ForgetWhatTheCutConversationShowed()
    {
        var outline = new List<ChatOutlineEntry>
        {
            new(1, Guid.NewGuid(), "user", DateTimeOffset.Now, 5),
            new(10, Guid.NewGuid(), "user", DateTimeOffset.Now, 5),
        };
        var usedSkill = new ChatMessage { Role = "user", Content = "use it", StorageSeq = 10 };
        usedSkill.ActiveSkills.Add(new SkillReference { Name = "Word" });
        var facts = TranscriptHistoryFacts.Create(outline, [usedSkill]);

        // The second exchange was edited away; a new one followed it.
        var resent = new ChatMessage { Role = "user", Content = "again", StorageSeq = 20 };
        var seed = facts.Before(10).Extend([resent]).SeedBefore(30);

        Assert.Empty(seed.ShownSkillNames);
        Assert.Equal(2, seed.ExchangeIndex);
    }

    [Fact]
    public async Task Outline_TellsTheUsersOwnMessagesFromJobWakesAndReceipts_AsTheTranscriptShowsThem()
    {
        var dataStore = OpenDataStore(out var store);
        var chat = new Chat { Title = "stops" };
        ChatMessage[] users =
        [
            new() { Role = "user", Content = "A question of my own" },
            new() { Role = "user", Author = "Lumi Job - Watcher", Content = "Background job triggered: Watcher\n" + new string('x', 500) },
            new() { Role = "user", Author = "Lumi Job - Watcher", Content = "Not a wake after all" },
            new() { Role = "user", Content = "Kiwi order", InteractionReceipt = new InteractionReceipt { Title = "Kiwi order", Summary = "two kiwi" } },
        ];
        foreach (var user in users)
        {
            chat.Messages.Add(user);
            chat.Messages.Add(new ChatMessage { Role = "assistant", Content = "Done." });
        }

        dataStore.Data.Chats.Add(chat);
        await dataStore.SaveChatAsync(chat);

        var outline = await store.ReadOutlineAsync(chat.Id);

        using var vm = new ChatViewModel(new DataStore(new AppData()), TestCopilot.Shared);
        foreach (var message in chat.Messages)
            vm.Messages.Add(new ChatMessageViewModel(message));
        var shown = vm.TranscriptTurns.SelectMany(static turn => turn.Items).OfType<UserMessageItem>()
            .Select(static item => item.Message.Id).ToHashSet();
        Assert.Equal([true, false, true, false], users.Select(user => shown.Contains(user.Id)));
        Assert.Equal(
            users.Select(user => shown.Contains(user.Id)),
            outline.Where(static entry => entry.Role == "user")
                .Select(static entry => TranscriptBuilder.ShowsAsUserMessage(entry.Role, entry.Author, entry.TextStart, entry.IsReceipt)));
    }

    // ── Runs that span exchanges ────────────────────────────────────────────

    [Fact]
    public async Task RunGlance_ReadsWhatTheRunsCardSummarizes()
    {
        var dataStore = OpenDataStore(out var store);
        const string plan = """{"todoList":[{"title":"Read","status":"completed"},{"title":"Report","status":"in-progress"}]}""";
        ChatMessage Step(string id, string tool, string content, string? parent = "run") => new()
        {
            Role = "tool", ToolName = tool, ToolCallId = id, ParentToolCallId = parent, ToolStatus = "Completed", Content = content,
        };
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = "Explore" },
            Step("run", "agent:explore", """{"description":"Explore the code","agent_type":"explore"}""", parent: null),
            Step("s1", "report_intent", """{"intent":"Reading"}"""),
            Step("s2", "update_todo", plan),
            Step("s3", "view", """{"path":"C:\\repo\\a.cs"}"""),
            Step("s4", "powershell", """{"command":"dir","description":"List files"}"""),
            Step("s5", "update_todo", plan),
            Step("s6", "report_intent", """{"intent":"Summarizing"}"""),
            Step("s7", "read_agent", "{}"),
            Step("s8", "task_complete", "{}"),
            new() { Role = "assistant", Content = "Done." },
        };
        var chat = new Chat { Title = "glance" };
        chat.Messages.AddRange(messages);
        dataStore.Data.Chats.Add(chat);
        await dataStore.SaveChatAsync(chat);

        var glance = await store.ReadRunGlanceStepsAsync(chat.Id, ["run"], TranscriptBuilder.ToolsWithoutStepCards);

        // The first plan update (when the plan card appears), the latest step card and the latest intent.
        Assert.Equal(["s2", "s4", "s6"], glance.Select(static message => message.ToolCallId));
        using var whole = new ChatViewModel(new DataStore(new AppData()), TestCopilot.Shared);
        foreach (var message in messages)
            whole.Messages.Add(new ChatMessageViewModel(message));
        using var row = new ChatViewModel(new DataStore(new AppData()), TestCopilot.Shared);
        foreach (var message in glance.Prepend(messages[1]))
            row.Messages.Add(new ChatMessageViewModel(message));
        var expected = Assert.Single(whole.SubagentRuns);
        var actual = Assert.Single(row.SubagentRuns);
        Assert.Equal("Summarizing", expected.Title);
        Assert.Equal(expected.Title, actual.Title);
        Assert.False(string.IsNullOrEmpty(expected.LatestActivityText));
        Assert.Equal(expected.LatestActivityText, actual.LatestActivityText);
    }

    [Fact]
    public async Task BackgroundRunAcrossExchanges_ReadsAsInAWholeChatBuild()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            // The whole chat in one build: the transcript as it read before history was stored.
            var messages = CreateBackgroundRunMessages();
            var reference = new DataStore(new AppData { Settings = new UserSettings { AutoSaveChats = false } });
            var whole = new Chat { Title = "whole" };
            whole.Messages.AddRange(messages.Select(static message =>
                JsonSerializer.Deserialize(Legacy(message), AppDataJsonContext.Default.ChatMessage)!));
            reference.Data.Chats.Add(whole);
            using var full = new ChatViewModel(reference, TestCopilot.Shared);
            await full.LoadChatAsync(whole);
            var expected = Describe(full.TranscriptTurns);
            Assert.Equal(5, full.SubagentRuns.Single(static run => run.ToolCallId == "review").StepCount);

            // The same chat stored and reopened: the newest exchange is the tail, and each older one
            // is built on its own when it is reached.
            var dataStore = OpenDataStore(out _);
            var chat = new Chat { Title = "stored" };
            chat.Messages.AddRange(messages);
            dataStore.Data.Chats.Add(chat);
            await dataStore.SaveChatAsync(chat);
            var users = chat.Messages.Where(static message => message.Role == "user").Select(static message => message.Id).ToList();
            CloseStores();

            var reopened = OpenDataStore(out _);
            var stored = Assert.Single(reopened.Data.Chats);
            using var vm = new ChatViewModel(reopened, TestCopilot.Shared);
            await vm.LoadChatAsync(stored);
            Assert.True(vm.HasTranscriptHistory);
            foreach (var user in users.SkipLast(1))
                Assert.True(await vm.EnsureMessageInTranscriptAsync(user, includeFoldedSteps: true));

            Assert.Equal(expected, Describe(vm.TranscriptTurns));

            // The runs' plain steps stayed in storage; the card holds none of them, and its page reads
            // and counts them all, as the whole build does.
            Assert.DoesNotContain(vm.TranscriptHistory.HydratedMessages, static message => message.ToolCallId is "r1" or "r3");
            var card = vm.TranscriptTurns
                .SelectMany(static turn => turn.EnumerateItems())
                .OfType<SubagentToolCallItem>()
                .Single(static run => run.ToolCallId == "review");
            Assert.Equal(0, card.StepCount);
            vm.OpenSubagentRunCommand.Execute(card);
            for (var wait = 0; wait < 500 && vm.SelectedSubagentRun?.StepCount != 5; wait++)
                await Task.Delay(10);
            Assert.Equal(5, vm.SelectedSubagentRun?.StepCount);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task OlderExchange_KeepsLumisReasoningApart_AroundRunStepsLeftInStorage()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var time = DateTimeOffset.Now.AddHours(-1);
            ChatMessage Message(string role, string content, string? tool = null, string? id = null, string? parent = null) => new()
            {
                Role = role,
                Content = content,
                ToolName = tool,
                ToolCallId = id,
                ParentToolCallId = parent,
                ToolStatus = tool is null ? null : "Completed",
                Timestamp = time = time.AddSeconds(5),
            };

            // Lumi reasons while its agent works: each stretch of reasoning between the agent's steps reads on its own.
            List<ChatMessage> messages =
            [
                Message("user", "Look into the crash"),
                Message("reasoning", "Where does it start?"),
                Message("tool", """{"description":"Find the crash","agent_type":"explore"}""", "agent:explore", "run"),
                Message("tool", """{"path":"C:\\repo\\a.cs"}""", "view", "s1", "run"),
                Message("reasoning", "The agent is reading the code."),
                Message("tool", """{"command":"dir"}""", "powershell", "s2", "run"),
                Message("reasoning", "It should find it soon."),
                Message("tool", """{"path":"C:\\repo\\b.cs"}""", "view", "s3", "run"),
                Message("assistant", "It starts in b.cs."),
                Message("user", "Thanks"),
                Message("assistant", "You're welcome."),
            ];
            var reference = new DataStore(new AppData { Settings = new UserSettings { AutoSaveChats = false, ShowReasoning = true } });
            var whole = new Chat { Title = "whole" };
            whole.Messages.AddRange(messages.Select(static message =>
                JsonSerializer.Deserialize(Legacy(message), AppDataJsonContext.Default.ChatMessage)!));
            reference.Data.Chats.Add(whole);
            using var full = new ChatViewModel(reference, TestCopilot.Shared);
            await full.LoadChatAsync(whole);
            var expected = Describe(full.TranscriptTurns);
            Assert.Equal(3, expected.Count(static line => line.Contains(nameof(ReasoningItem), StringComparison.Ordinal)));

            var dataStore = OpenDataStore(out _);
            var chat = new Chat { Title = "stored" };
            chat.Messages.AddRange(messages);
            dataStore.Data.Chats.Add(chat);
            await dataStore.SaveChatAsync(chat);
            CloseStores();

            var reopened = OpenDataStore(out _);
            reopened.Data.Settings.ShowReasoning = true;
            using var vm = new ChatViewModel(reopened, TestCopilot.Shared);
            await vm.LoadChatAsync(Assert.Single(reopened.Data.Chats));
            Assert.True(await vm.EnsureMessageInTranscriptAsync(messages[0].Id, includeFoldedSteps: true));

            Assert.DoesNotContain(vm.TranscriptHistory.HydratedMessages, static message => message.ToolCallId is "s1" or "s2");
            Assert.Equal(expected, Describe(vm.TranscriptTurns));
        }, CancellationToken.None);
    }

    /// <summary>
    /// A background review that keeps working while the user moves on: it is started in the first
    /// exchange, takes steps (and starts a helper) in the second, and one more step in the third.
    /// </summary>
    private static List<ChatMessage> CreateBackgroundRunMessages()
    {
        var time = DateTimeOffset.Now.AddHours(-1);
        ChatMessage User(string text) => new() { Role = "user", Content = text, Timestamp = time = time.AddSeconds(5) };
        ChatMessage Answer(string text) => new() { Role = "assistant", Content = text, Timestamp = time = time.AddSeconds(5) };
        ChatMessage Tool(string id, string name, string content, string? parent = null) => new()
        {
            Role = "tool",
            ToolName = name,
            ToolCallId = id,
            ParentToolCallId = parent,
            ToolStatus = "Completed",
            Content = content,
            ToolOutput = $"{id} done",
            Timestamp = time = time.AddSeconds(5),
            ToolDurationMs = 1000,
        };

        const string plan = """{"todoList":[{"title":"Read","status":"completed"},{"title":"Report","status":"in-progress"}]}""";
        return
        [
            User("Review the change in the background"),
            Tool("review", "agent:code-review", """{"description":"Review the change","agent_type":"code-review","prompt":"Review it","mode":"background"}"""),
            Tool("r1", "view", """{"path":"C:\\repo\\a.cs"}""", parent: "review"),
            Tool("r2", "report_intent", """{"intent":"Reading the diff"}""", parent: "review"),
            Answer("The review is running in the background."),
            User("Meanwhile, list the files"),
            Tool("own", "powershell", """{"command":"dir","description":"List files"}"""),
            Tool("r3", "grep", """{"pattern":"TODO"}""", parent: "review"),
            Tool("r4", "update_todo", plan, parent: "review"),
            Tool("r5", "edit", """{"path":"C:\\repo\\a.cs","old_str":"a","new_str":"b"}""", parent: "review"),
            Tool("helper", "agent:explore", """{"description":"Find callers","agent_type":"explore","prompt":"Find them"}""", parent: "review"),
            Tool("h1", "view", """{"path":"C:\\repo\\b.cs"}""", parent: "helper"),
            Answer("Here are the files."),
            User("Thanks"),
            Tool("r6", "view", """{"path":"C:\\repo\\c.cs"}""", parent: "review"),
            Answer("You're welcome."),
        ];
    }

    private static List<string> Describe(IEnumerable<TranscriptTurn> turns)
    {
        var lines = new List<string>();
        foreach (var turn in turns)
        {
            lines.Add($"turn {turn.StableId}");
            foreach (var item in turn.Items)
                Describe(item, "  ", lines);
        }

        return lines;
    }

    private static void Describe(TranscriptItem item, string indent, List<string> lines)
    {
        switch (item)
        {
            case TurnSummaryItem summary:
                lines.Add($"{indent}summary {summary.StableId} \"{summary.Label}\"");
                foreach (var inner in summary.InnerItems)
                    Describe(inner, indent + "  ", lines);
                break;
            case SubagentGroupItem group:
                lines.Add($"{indent}agents {group.StableId} \"{group.HeaderLabel}\"");
                foreach (var run in group.Subagents)
                    Describe(run, indent + "  ", lines);
                break;
            case SubagentToolCallItem run:
                // What its card and page headline show; its steps and their count are on its page.
                lines.Add($"{indent}run {run.ToolCallId} title=\"{run.Title}\" latest=\"{run.LatestActivityText}\" {run.Status}");
                break;
            case ToolGroupItem group:
                lines.Add($"{indent}group {group.StableId} \"{group.Label}\" [{string.Join(", ", group.ToolCalls.Select(static tool => tool.GetType().Name))}]");
                break;
            default:
                lines.Add($"{indent}{item.GetType().Name} {item.StableId}");
                break;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    // Every stored message in [fromSeq, toSeq), read the way an older exchange's build reads it, keeping all.
    private static async Task<List<ChatMessage>> ReadHistoryAsync(DataStore dataStore, Chat chat, long fromSeq, long toSeq)
        => (await dataStore.ReadHistoryExchangeAsync(
            chat, fromSeq, toSeq, static outline => outline.Messages.Select(static header => header.Seq).ToHashSet())).Messages;

    private static Chat CreateChat(DataStore dataStore, int exchanges, int toolsPerExchange, DateTimeOffset? start = null)
    {
        var chat = new Chat { Title = $"chat {Guid.NewGuid():N}" };
        var time = start ?? DateTimeOffset.Now.AddHours(-1);
        for (var exchange = 0; exchange < exchanges; exchange++)
        {
            chat.Messages.Add(new ChatMessage { Role = "user", Content = $"question {exchange}", Timestamp = time = time.AddSeconds(1) });
            for (var tool = 0; tool < toolsPerExchange; tool++)
            {
                chat.Messages.Add(new ChatMessage
                {
                    Role = "tool",
                    ToolName = tool % 2 == 0 ? "view" : "powershell",
                    ToolCallId = $"call-{exchange}-{tool}",
                    ToolStatus = "Completed",
                    Content = $"{{\"command\":\"exchange {exchange} step {tool}\"}}",
                    ToolOutput = $"output {exchange}.{tool}",
                    Timestamp = time = time.AddSeconds(1),
                });
            }

            chat.Messages.Add(new ChatMessage { Role = "assistant", Content = $"answer {exchange}", Timestamp = time = time.AddSeconds(1) });
        }

        dataStore.Data.Chats.Add(chat);
        return chat;
    }

    private static ChatMessage CreateFullyPopulatedMessage()
    {
        var message = new ChatMessage
        {
            Role = "tool",
            Content = "{\"a\":1}",
            Author = "Lumi",
            RemoteRequestId = "r1",
            Timestamp = new DateTimeOffset(2026, 10, 20, 12, 30, 15, TimeSpan.FromHours(3)),
            ToolName = "task",
            ToolSkillName = "skill",
            ToolCallId = "call",
            ParentToolCallId = "parent",
            ToolStatus = "Completed",
            ToolOutput = "out",
            EmbeddedApp = new EmbeddedApp
            {
                Kind = EmbeddedAppKind.Canvas, Title = "Flow", SourceName = "draw", SessionId = "session", ServerName = "server",
                ResourceUri = "ui://flow", InputJson = "{}", ResultJson = "{\"content\":[]}", ModelContextJson = "{\"a\":1}",
                ExtensionId = "project:draw", CanvasId = "draw", InstanceId = "flow", IsClosed = true,
            },
            ToolStartedAt = DateTimeOffset.Now,
            ToolDurationMs = 12.5,
            PlanSnapshot = "[]",
            LinkedChatId = Guid.NewGuid(),
            LinkedChatTitle = "linked",
            QuestionId = "q",
            QuestionText = "why?",
            QuestionOptions = "[\"a\",\"b\"]",
            QuestionAllowFreeText = false,
            QuestionAllowMultiSelect = true,
            InteractionStates = new Dictionary<string, string> { ["b"] = "{}" },
            InputKind = "form",
            InputDefinition = "{}",
            InputAnswer = "{}",
            InputCanceled = true,
            InteractionReceipt = new InteractionReceipt { SourceMessageId = Guid.NewGuid(), BlockId = "b", Title = "t", Summary = "s", Detail = "d", Action = "go", Status = "Done", Error = "e" },
            IsStreaming = true,
            IsProgress = false,
            Model = "m",
            ReasoningEffort = "high",
            ContextWindowTier = "long_context",
            AgentId = Guid.NewGuid(),
            SdkAgentName = "agent",
            HasAgentSelection = true,
            ActiveMcpServerNames = ["github"],
            HasMcpSelection = true,
            Attachments = ["C:\\a.txt"],
            Sources = [new SearchSource { Title = "t", Snippet = "s", Url = "https://e" }],
            ActiveSkills = [new SkillReference { Name = "n", Glyph = "g", Description = "d", Content = "c" }],
            ReplyTo = new MessageReply { MessageId = Guid.NewGuid(), Quote = "q", IsSelection = true, Author = "Lumi" },
            ReferencedChats = [new ChatReference { ChatId = Guid.NewGuid(), Title = "other" }],
            FailureDisposition = SessionFailureDisposition.RebuildSession,
            IsPendingPausedSend = true,
        };
        return message;
    }
}
