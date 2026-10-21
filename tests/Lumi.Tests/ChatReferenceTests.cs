using System.Reflection;
using System.Text.Json;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using StrataTheme.Controls;
using Xunit;

using ChatMessage = Lumi.Models.ChatMessage;

namespace Lumi.Tests;

/// <summary>
/// Bringing other chats into a message from the composer: which chats are offered, how the chosen chips
/// stay with their chat as a draft, how exactly one message takes them, how they are persisted and shown,
/// and what the model receives when the message is sent.
/// </summary>
public sealed class ChatReferenceTests
{
    // ── Words for the model and for flattened transcripts ──

    [Fact]
    public void PromptContext_IsEmpty_WithoutChats()
        => Assert.Equal("", ChatReferenceFormatter.BuildPromptContext([]));

    [Fact]
    public void PromptContext_ShowsEachChatUnderItsTitle_AndSaysWhyAChatIsMissing()
    {
        var skippedId = Guid.NewGuid();
        var context = ChatReferenceFormatter.BuildPromptContext(
        [
            new ReferencedChatContent(new ChatReference { ChatId = Guid.NewGuid(), Title = " Trip plan " }, "User: Lisbon?\nLumi: Yes.", Exists: true),
            new ReferencedChatContent(new ChatReference { ChatId = Guid.NewGuid(), Title = "Gone" }, null, Exists: false),
            new ReferencedChatContent(new ChatReference { ChatId = skippedId, Title = "" }, null, Exists: true)
        ]);

        Assert.StartsWith("\n\n--- Referenced chats ---\n", context);
        Assert.Contains("these earlier Lumi chats", context);
        Assert.Contains("=== Chat: \"Trip plan\" ===\nUser: Lisbon?\nLumi: Yes.\n", context);
        Assert.Contains("=== Chat: \"Gone\" ===\n(This chat no longer exists", context);
        Assert.Contains("=== Chat: \"Untitled chat\" ===\n(Not included to keep this message short. Read it with read_chat, id: " + skippedId, context);
    }

    [Fact]
    public void TranscriptMarker_NamesTheChats_OrIsEmpty()
    {
        Assert.Equal(
            "(with chats: \"Trip\", \"Budget\") ",
            ChatReferenceFormatter.DescribeForTranscript(
            [
                new ChatReference { ChatId = Guid.NewGuid(), Title = "Trip" },
                new ChatReference { ChatId = Guid.NewGuid(), Title = "Budget" }
            ]));
        Assert.Equal("", ChatReferenceFormatter.DescribeForTranscript(null));
        Assert.Equal("", ChatReferenceFormatter.DescribeForTranscript([]));
    }

    // ── Persistence and history ──

    [Fact]
    public void ReferencedChats_RoundTripThroughPersistence_CloneIndependently_AndAreOmittedWhenAbsent()
    {
        var chatId = Guid.NewGuid();
        var original = new ChatMessage
        {
            Role = "user",
            Content = "Compare these",
            ReferencedChats = [new ChatReference { ChatId = chatId, Title = "Trip plan" }]
        };

        var json = JsonSerializer.Serialize(new List<ChatMessage> { original }, AppDataJsonContext.Default.ListChatMessage);
        var restored = Assert.Single(JsonSerializer.Deserialize(json, AppDataJsonContext.Default.ListChatMessage)!);
        var clone = restored.Clone();
        clone.ReferencedChats![0].Title = "changed";
        var plainJson = JsonSerializer.Serialize(
            new List<ChatMessage> { new() { Role = "user", Content = "Hi" } },
            AppDataJsonContext.Default.ListChatMessage);

        var reference = Assert.Single(restored.ReferencedChats!);
        Assert.Equal(chatId, reference.ChatId);
        Assert.Equal("Trip plan", reference.Title);
        Assert.NotSame(restored.ReferencedChats, clone.ReferencedChats);
        Assert.DoesNotContain("referencedChats", plainJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadChat_ShowsWhichChatsAUserMessageBroughtIn()
    {
        var now = DateTimeOffset.Now;
        var chat = new Chat
        {
            Title = "Planning",
            UpdatedAt = now,
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = "Which is cheaper?",
                    Timestamp = now,
                    ReferencedChats = [new ChatReference { ChatId = Guid.NewGuid(), Title = "Trip plan" }]
                }
            ]
        };
        var service = new ChatHistoryService(new DataStore(new AppData { Chats = [chat] }), null, () => now);

        var transcript = await service.ReadChatAsync(chat.Id.ToString());

        Assert.Contains("User: (with chats: \"Trip plan\") Which is cheaper?", transcript);
    }

    [Fact]
    public void SessionRecoveryReplay_KeepsWhichChatsEarlierMessagesBroughtIn()
    {
        var retained = new List<ChatMessage>
        {
            new()
            {
                Role = "user",
                Content = "Which is cheaper?",
                ReferencedChats = [new ChatReference { ChatId = Guid.NewGuid(), Title = "Trip plan" }]
            }
        };
        var method = typeof(ChatViewModel).GetMethod(
            "BuildSessionRecoveryReplayPrompt",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var replay = (string)method.Invoke(null, [retained, "next"])!;

        Assert.Contains("User: (with chats: \"Trip plan\") Which is cheaper?", replay);
    }

    // ── What the composer offers ──

    [Fact]
    public void RefreshChatReferences_OffersTheOtherChats_MostRecentFirst_WithTheirProject()
    {
        Loc.Load("en");
        using var host = ReferenceHost.Create();
        var project = new Project { Name = "Travel" };
        host.DataStore.Data.Projects.Add(project);
        var older = host.AddChat("Budget", DateTimeOffset.Now.AddDays(-3));
        var newer = host.AddChat("Trip plan", DateTimeOffset.Now.AddHours(-1), project.Id);
        var untitled = host.AddChat("  ", DateTimeOffset.Now.AddDays(-1));
        host.Chat.UpdatedAt = DateTimeOffset.Now;

        host.ViewModel.RefreshChatReferencesCommand.Execute(null);

        Assert.Equal(
            [newer.Id.ToString(), untitled.Id.ToString(), older.Id.ToString()],
            host.ViewModel.AvailableChatChips.Select(static chip => chip.Value));
        var trip = host.ViewModel.AvailableChatChips[0];
        Assert.Equal("Trip plan", trip.Name);
        Assert.Equal("Travel", trip.SecondaryText);
        Assert.False(string.IsNullOrWhiteSpace(trip.SourceLabel));
        Assert.Equal(Loc.Sidebar_NewChat, host.ViewModel.AvailableChatChips[1].Name);
        Assert.Null(host.ViewModel.AvailableChatChips[2].SecondaryText);
    }

    [Fact]
    public void RefreshChatReferences_LeavesTheOfferedChatsAlone_WhenNothingChanged()
    {
        using var host = ReferenceHost.Create();
        host.AddChat("Trip plan", DateTimeOffset.Now.AddDays(-2));
        host.ViewModel.RefreshChatReferencesCommand.Execute(null);
        var changes = 0;
        host.ViewModel.AvailableChatChips.CollectionChanged += (_, _) => changes++;

        host.ViewModel.RefreshChatReferencesCommand.Execute(null);
        Assert.Equal(0, changes);

        host.AddChat("Budget", DateTimeOffset.Now.AddDays(-1));
        host.ViewModel.RefreshChatReferencesCommand.Execute(null);
        Assert.NotEqual(0, changes);
        Assert.Equal("Budget", host.ViewModel.AvailableChatChips[0].Name);
    }

    [Fact]
    public void ManageSkills_AsksToOpenTheSkillsPage()
    {
        using var host = ReferenceHost.Create();
        var requested = 0;
        host.ViewModel.SkillsPageRequested += () => requested++;

        host.ViewModel.ManageSkillsCommand.Execute(null);

        Assert.Equal(1, requested);
    }

    [Fact]
    public void ManageSkills_OpensTheSkillsSectionOfCustomize()
    {
        Loc.Load("en");
        var data = new AppData();
        data.Settings.IsOnboarded = true;
        data.Settings.AutoSaveChats = false;
        data.Settings.EnableMemoryAutoSave = false;
        using var main = new MainViewModel(
            new DataStore(data),
            TestCopilot.Shared,
            new UpdateService(),
            startBackgroundJobs: false,
            initializeCopilotOnStartup: false);

        main.ChatVM.ManageSkillsCommand.Execute(null);

        Assert.Equal(3, main.SelectedNavIndex);
    }

    [Fact]
    public void LocalizedComposerStyles_NameTheAddMenuDictationAndPickersFromLoc()
    {
        // Built from Loc when called (after the saved language loads), like the connector list's.
        var styles = Lumi.App.CreateComposerPickerTextStyles().OfType<Avalonia.Styling.Style>().ToList();
        var composer = styles[0].Setters.OfType<Avalonia.Styling.Setter>()
            .ToDictionary(setter => setter.Property!, setter => setter.Value);

        Assert.Equal(15, composer.Count);
        Assert.Equal(Loc.Composer_Add, composer[StrataChatComposer.AddButtonToolTipProperty]);
        Assert.Equal(Loc.Composer_AddFiles, composer[StrataChatComposer.AddFilesLabelProperty]);
        Assert.Equal(Loc.Composer_ChatsRecent, composer[StrataChatComposer.AddRecentChatsLabelProperty]);
        Assert.Equal(Loc.Composer_AddAllChats, composer[StrataChatComposer.AddAllChatsLabelProperty]);
        Assert.Equal(Loc.Composer_Skills, composer[StrataChatComposer.AddSkillsLabelProperty]);
        Assert.Equal(Loc.Composer_AddAgents, composer[StrataChatComposer.AddAgentsLabelProperty]);
        Assert.Equal(Loc.Composer_Connectors, composer[StrataChatComposer.AddConnectorsLabelProperty]);
        Assert.Equal(Loc.Composer_ConnectorsOn, composer[StrataChatComposer.AddConnectorsCountFormatProperty]);
        Assert.Equal(Loc.Composer_AddProject, composer[StrataChatComposer.AddProjectLabelProperty]);
        Assert.Equal(Loc.Composer_AddHint, composer[StrataChatComposer.AddMenuHintProperty]);
        Assert.Equal(Loc.Composer_Dictate, composer[StrataChatComposer.DictateLabelProperty]);
        Assert.Equal(Loc.Composer_StopDictating, composer[StrataChatComposer.StopDictatingLabelProperty]);
        Assert.Equal(Loc.Composer_DiscardDictation, composer[StrataChatComposer.CancelDictationLabelProperty]);
        Assert.Equal(Loc.Composer_DictationLanguage, composer[StrataChatComposer.VoiceLanguageLabelProperty]);
        Assert.Equal(Loc.Composer_MoreLanguages, composer[StrataChatComposer.MoreVoiceLanguagesLabelProperty]);
        Assert.Contains("{0}", Loc.Composer_ConnectorsOn);

        // The agent list opened from the + menu speaks the same language.
        var agents = styles.Single(style => style.Setters.OfType<Avalonia.Styling.Setter>()
            .Any(setter => setter.Property == StrataComposerPicker.SearchWatermarkProperty
                           && Equals(setter.Value, Loc.Composer_AgentsSearch)));
        var agentSetters = agents.Setters.OfType<Avalonia.Styling.Setter>()
            .ToDictionary(setter => setter.Property!, setter => setter.Value);
        Assert.Equal(Loc.Composer_AgentsSelected, agentSetters[StrataComposerPicker.SelectedLabelProperty]);
        Assert.Equal(Loc.Composer_AgentsNoResults, agentSetters[StrataComposerPicker.NoResultsFormatProperty]);
        Assert.Equal(Loc.Composer_AgentsHint, agentSetters[StrataComposerPicker.HintTextProperty]);
    }

    // ── The composer's chats as a draft ──

    [Fact]
    public void ReferencedChats_StayWithTheirChat_AndTheNewChatDraft_WhenSwitchingChats()
    {
        using var host = ReferenceHost.Create();
        var trip = host.AddChat("Trip plan", DateTimeOffset.Now);
        var budget = host.AddChat("Budget", DateTimeOffset.Now);
        var other = host.AddChat("Other", DateTimeOffset.Now);
        host.Reference(trip);

        host.ViewModel.CurrentChat = other;
        Assert.Empty(host.ViewModel.ReferencedChatChips);

        host.ViewModel.CurrentChat = null;
        host.Reference(budget);
        host.ViewModel.CurrentChat = host.Chat;
        Assert.Equal([trip.Id.ToString()], host.ReferencedIds());

        host.ViewModel.CurrentChat = null;
        Assert.Equal([budget.Id.ToString()], host.ReferencedIds());
    }

    [Fact]
    public void QueuedComposerSend_TakesTheReferencedChats_ButARemoteSendDoesNot()
    {
        using var host = ReferenceHost.Create();
        var trip = host.AddChat("Trip plan", DateTimeOffset.Now);
        host.Reference(trip);

        host.Invoke("QueueBusySendPrompt", host.Chat.Id, "from the phone", null, "Lumi Mobile");
        Assert.Null(host.Chat.Messages.Single().ReferencedChats);
        Assert.Single(host.ViewModel.ReferencedChatChips);

        host.Invoke("QueueBusySendPrompt", host.Chat.Id, "from the composer", null, null);
        var reference = Assert.Single(host.Chat.Messages.Last().ReferencedChats!);
        Assert.Equal(trip.Id, reference.ChatId);
        Assert.Equal("Trip plan", reference.Title);
        Assert.Empty(host.ViewModel.ReferencedChatChips);
    }

    [Fact]
    public void EditingAMessage_ShowsItsChats_AndCancelRestoresTheDraftChats()
    {
        using var host = ReferenceHost.Create();
        var trip = host.AddChat("Trip plan", DateTimeOffset.Now);
        var budget = host.AddChat("Budget", DateTimeOffset.Now);
        var deletedId = Guid.NewGuid();
        var edited = new ChatMessage
        {
            Role = "user",
            Content = "Compare",
            ReferencedChats =
            [
                new ChatReference { ChatId = trip.Id, Title = "Old title" },
                new ChatReference { ChatId = deletedId, Title = "Deleted chat" }
            ]
        };
        host.Chat.Messages.Add(edited);
        host.Reference(budget);

        host.Invoke("BeginComposerEdit", edited);

        Assert.True(host.ViewModel.IsEditingMessage);
        Assert.Equal([trip.Id.ToString(), deletedId.ToString()], host.ReferencedIds());
        // A chat that still exists shows its current title; a deleted one keeps the title it had.
        Assert.Equal(
            ["Trip plan", "Deleted chat"],
            host.ViewModel.ReferencedChatChips.OfType<StrataComposerChip>().Select(static chip => chip.Name));

        host.Invoke("CancelComposerEditInternal", true, false);

        Assert.False(host.ViewModel.IsEditingMessage);
        Assert.Equal([budget.Id.ToString()], host.ReferencedIds());
    }

    [Fact]
    public void ForkAndEdit_BringsTheForkedMessagesChatsBackIntoTheDraft()
    {
        var tripId = Guid.NewGuid();
        var question = new ChatMessage
        {
            Role = "user",
            Content = "Compare these",
            ReferencedChats = [new ChatReference { ChatId = tripId, Title = "Trip plan" }]
        };
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = "hi" },
            new() { Role = "assistant", Content = "Hello" },
            question
        };

        var plan = ChatForkFactory.CreateFork(new Chat { Title = "Plans" }, messages, question.Id);

        var carried = Assert.Single(plan.ComposerReferencedChats!);
        Assert.Equal(tripId, carried.ChatId);
        Assert.NotSame(question.ReferencedChats![0], carried);
        Assert.Null(ChatForkFactory.CreateFork(new Chat { Title = "Plans" }, messages, messages[0].Id).ComposerReferencedChats);

        using var host = ReferenceHost.Create();
        host.Reference(host.AddChat("Budget", DateTimeOffset.Now));

        host.ViewModel.SetComposerDraft(plan.ComposerPrefill!, plan.ComposerReply, plan.ComposerReferencedChats);

        Assert.Equal("Compare these", host.ViewModel.PromptText);
        Assert.Equal([tripId.ToString()], host.ReferencedIds());
    }

    // ── In the transcript ──

    [Fact]
    public void SentMessage_ShowsItsChatsAsPills_ThatOpenTheChat()
    {
        using var host = ReferenceHost.Create();
        var tripId = Guid.NewGuid();
        var message = new ChatMessage
        {
            Role = "user",
            Content = "Compare",
            ReferencedChats = [new ChatReference { ChatId = tripId, Title = "Trip plan" }]
        };
        host.Chat.Messages.Add(message);
        host.ViewModel.Messages.Add(new ChatMessageViewModel(message));
        var plainMessage = new ChatMessage { Role = "user", Content = "Hi" };
        host.Chat.Messages.Add(plainMessage);
        host.ViewModel.Messages.Add(new ChatMessageViewModel(plainMessage));
        Guid? opened = null;
        host.ViewModel.OpenChatRequested += id => opened = id;

        var item = host.UserItem(message.Id);
        var pill = Assert.Single(item.ReferencedChats);
        pill.OpenCommand.Execute(null);

        Assert.True(item.HasReferencedChats);
        Assert.Same(item.ReferencedChats, item.DisplayReferencedChats);
        Assert.Equal("Trip plan", pill.Title);
        Assert.Equal(tripId, opened);
        Assert.Null(host.UserItem(plainMessage.Id).DisplayReferencedChats);
    }

    // ── What the model receives ──

    [Fact]
    public async Task PromptContext_ReadsEachReferencedChat_AndNamesOnesThatAreGone()
    {
        using var host = ReferenceHost.Create();
        var trip = host.AddChat("Trip plan", DateTimeOffset.Now);
        trip.Messages.Add(new ChatMessage { Role = "user", Content = "Where in May?" });
        trip.Messages.Add(new ChatMessage { Role = "assistant", Content = "Lisbon fits the budget.", Author = "Lumi" });
        var message = new ChatMessage
        {
            Role = "user",
            Content = "Book it",
            ReferencedChats =
            [
                new ChatReference { ChatId = trip.Id, Title = "Trip plan" },
                new ChatReference { ChatId = Guid.NewGuid(), Title = "Gone" }
            ]
        };

        var context = await host.BuildContextAsync(message);

        Assert.Contains("--- Referenced chats ---", context);
        Assert.Contains("=== Chat: \"Trip plan\" ===", context);
        Assert.Contains("Lisbon fits the budget.", context);
        Assert.Contains("=== Chat: \"Gone\" ===\n(This chat no longer exists", context);
        Assert.Equal("", await host.BuildContextAsync(new ChatMessage { Role = "user", Content = "Hi" }));
    }

    [Fact]
    public async Task PromptContext_NamesChatsPastTheBudget_InsteadOfReadingThem()
    {
        using var host = ReferenceHost.Create();
        var chats = Enumerable.Range(0, 6)
            .Select(index =>
            {
                var chat = host.AddChat($"Long chat {index}", DateTimeOffset.Now);
                for (var turn = 0; turn < 20; turn++)
                    chat.Messages.Add(new ChatMessage { Role = "user", Content = $"chat {index} " + new string('x', 1_500) });
                return chat;
            })
            .ToList();
        var message = new ChatMessage
        {
            Role = "user",
            Content = "Summarize all",
            ReferencedChats = chats.Select(static chat => new ChatReference { ChatId = chat.Id, Title = chat.Title }).ToList()
        };

        var context = await host.BuildContextAsync(message);

        Assert.Contains("chat 0 xxx", context);
        Assert.DoesNotContain("chat 5 xxx", context);
        Assert.Contains("=== Chat: \"Long chat 5\" ===\n(Not included to keep this message short. Read it with read_chat, id: " + chats[5].Id, context);
    }

    private sealed class ReferenceHost : IDisposable
    {
        private ReferenceHost(ChatViewModel viewModel, DataStore dataStore, Chat chat)
        {
            ViewModel = viewModel;
            DataStore = dataStore;
            Chat = chat;
        }

        public ChatViewModel ViewModel { get; }
        public DataStore DataStore { get; }
        public Chat Chat { get; }

        public static ReferenceHost Create()
        {
            var dataStore = new DataStore(new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false,
                    NotificationsEnabled = false
                }
            });
            var chat = new Chat { Title = "current" };
            dataStore.Data.Chats.Add(chat);

            var viewModel = new ChatViewModel(dataStore, TestCopilot.Shared)
            {
                CurrentChat = chat
            };
            return new ReferenceHost(viewModel, dataStore, chat);
        }

        public Chat AddChat(string title, DateTimeOffset updatedAt, Guid? projectId = null)
        {
            var chat = new Chat { Title = title, UpdatedAt = updatedAt, ProjectId = projectId };
            DataStore.Data.Chats.Add(chat);
            return chat;
        }

        /// <summary>Picks <paramref name="chat"/> as the composer's Chats picker does: adds its chip.</summary>
        public void Reference(Chat chat)
            => ViewModel.ReferencedChatChips.Add(new StrataComposerChip(chat.Title, "💬", Value: chat.Id.ToString()));

        public string[] ReferencedIds()
            => ViewModel.ReferencedChatChips.OfType<StrataComposerChip>().Select(static chip => chip.Value ?? "").ToArray();

        public UserMessageItem UserItem(Guid messageId)
            => ViewModel.TranscriptTurns
                .SelectMany(static turn => turn.Items)
                .OfType<UserMessageItem>()
                .Single(item => item.Message.Id == messageId);

        public Task<string> BuildContextAsync(ChatMessage message)
            => (Task<string>)Invoke("BuildReferencedChatsContextAsync", message, CancellationToken.None)!;

        public object? Invoke(string name, params object?[] args)
        {
            var method = typeof(ChatViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Method {name} was not found.");
            return method.Invoke(ViewModel, args);
        }

        public void Dispose() => ViewModel.Dispose();
    }
}
