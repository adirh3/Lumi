using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Diagnostics;
using Avalonia.Threading;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.Services;
using Lumi.Services.Remote;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class RemoteCommandRouterSurfaceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    public Task MessageEditRequiresAnExplicitChatAndMessage(string? chatId) => RunAsync(async () =>
    {
        var message = new ChatMessage { Role = "user", Content = "Keep this message" };
        var chat = new Chat { Messages = [message] };
        var store = new DataStore(new AppData { Settings = TestSettings(), Chats = [chat] });
        using var main = new MainViewModel(store, TestCopilot.Shared, new UpdateService(), initializeCopilotOnStartup: false);
        await main.OpenChatByIdAsync(chat.Id);
        main.ChatVM.PromptText = "Keep this draft";
        var command = new RemoteCommand(RemoteProtocol.Actions.EditMessage)
            .With("messageId", message.Id.ToString()).With("message", "Changed");
        if (chatId is not null)
            command.With("chatId", chatId);
        var result = await new RemoteCommandRouter(store, main).ExecuteAsync(command, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Same(message, Assert.Single(chat.Messages));
        Assert.Equal("Keep this message", message.Content);
        Assert.Equal("Keep this draft", main.ChatVM.PromptText);
    });

    [Fact]
    public Task MessageEditRejectsAssistantMessagesAndPreservesTheComposer() => RunAsync(async () =>
    {
        var answer = new ChatMessage { Role = "assistant", Content = "Keep this answer" };
        var chat = new Chat { Messages = [answer] };
        var store = new DataStore(new AppData { Settings = TestSettings(), Chats = [chat] });
        using var main = new MainViewModel(store, TestCopilot.Shared, new UpdateService(), initializeCopilotOnStartup: false);
        await main.OpenChatByIdAsync(chat.Id);
        main.ChatVM.PromptText = "Keep this draft";
        var result = await new RemoteCommandRouter(store, main).ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.EditMessage).With("chatId", chat.Id.ToString())
                .With("messageId", answer.Id.ToString()).With("message", "Changed"),
            CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Same(answer, Assert.Single(chat.Messages));
        Assert.Equal("Keep this answer", answer.Content);
        Assert.Equal("Keep this draft", main.ChatVM.PromptText);
    });

    [Fact]
    public Task PersistedMessageEditReceiptMakesAReplaySafeAfterRestart() => RunAsync(async () =>
    {
        var chat = new Chat { LastRemoteDeviceId = "phone", LastRemoteRequestId = "edit-request" };
        var store = new DataStore(new AppData { Settings = TestSettings(), Chats = [chat] });
        using var main = new MainViewModel(store, TestCopilot.Shared, new UpdateService(), initializeCopilotOnStartup: false);
        var result = await new RemoteCommandRouter(store, main).ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.EditMessage)
            {
                AuthenticatedDeviceId = "phone", RequestId = "edit-request"
            }.With("chatId", chat.Id.ToString()).With("messageId", Guid.NewGuid().ToString()).With("message", "Already accepted"),
            CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        Assert.Empty(chat.Messages);
    });

    [Fact]
    public Task PausedChatRejectsMessageEditsWithoutRemovingHistory() => RunAsync(async () =>
    {
        var message = new ChatMessage { Role = "user", Content = "Keep this message" };
        var chat = new Chat { IsPaused = true, Messages = [message] };
        var store = new DataStore(new AppData { Settings = TestSettings(), Chats = [chat] });
        using var main = new MainViewModel(store, TestCopilot.Shared, new UpdateService(), initializeCopilotOnStartup: false);
        var result = await new RemoteCommandRouter(store, main).ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.EditMessage).With("chatId", chat.Id.ToString())
                .With("messageId", message.Id.ToString()).With("message", "Changed"),
            CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Same(message, Assert.Single(chat.Messages));
        Assert.Equal("Keep this message", message.Content);
    });

    [Fact]
    public Task SetupOnlyFusionEffortEditPersistsTheTierWithoutChangingDefaults() => RunAsync(async () =>
    {
        var settings = TestSettings();
        settings.PreferredModel = "gpt-6-luna";
        settings.ReasoningEffort = "low";
        var chat = new Chat { LastModelUsed = "auto:balance" };
        var store = new DataStore(new AppData { Settings = settings, Chats = [chat] });
        using var main = new MainViewModel(store, TestCopilot.Shared, new UpdateService(),
            startBackgroundJobs: false, initializeCopilotOnStartup: false);
        var models = CopilotModelCatalog.BuildModels(CopilotModelCatalogTests.NativeCatalog());
        main.ChatVM.UpdateModelCapabilities(models);
        main.SettingsVM.UpdateModelCapabilities(models);
        await main.OpenChatByIdAsync(chat.Id);
        var router = new RemoteCommandRouter(store, main);
        var configured = await router.ExecuteAsync(new RemoteCommand(RemoteProtocol.Actions.ConfigureChat)
            .With("chatId", chat.Id.ToString()).With("useChatSetup", "true").With("quality", "Max"),
            CancellationToken.None);
        Assert.True(configured.Ok, configured.Error);
        Assert.Equal("auto:hydrafusion-max", main.ChatVM.SelectedModel);
        Assert.Equal("auto:hydrafusion-max", chat.LastModelUsed);
        Assert.Equal("auto:hydrafusion-max", main.ChatVM.ResolveSelectedModelForChat(chat));
        Assert.Equal("gpt-6-luna", settings.PreferredModel);
        Assert.Equal("low", settings.ReasoningEffort);
    });

    [Fact]
    public Task FusionEffortFromThePhoneSelectsANativeTierAndUnpinsTheWholeGroup() => RunAsync(async () =>
    {
        var settings = TestSettings();
        settings.PreferredModel = "gpt-6-luna";
        settings.ReasoningEffort = "low";
        settings.FavoriteModelIds = ["auto:hydrafusion-max"];
        var store = new DataStore(new AppData { Settings = settings });
        using var main = new MainViewModel(store, TestCopilot.Shared, new UpdateService(),
            startBackgroundJobs: false, initializeCopilotOnStartup: false);
        var models = CopilotModelCatalog.BuildModels(CopilotModelCatalogTests.NativeCatalog());
        main.ChatVM.UpdateModelCapabilities(models);
        main.SettingsVM.UpdateModelCapabilities(models);
        main.ChatVM.ApplyAvailableModels(models.Select(model => model.Id).ToArray(), "gpt-6-luna");
        var router = new RemoteCommandRouter(store, main);
        var created = await router.ExecuteAsync(new RemoteCommand(RemoteProtocol.Actions.CreateChat)
            .With("model", "auto:balance").With("quality", "Max").With("open", "false"), CancellationToken.None);
        Assert.True(created.Ok, created.Error);
        var chat = Assert.Single(store.Data.Chats);
        Assert.Equal("auto:hydrafusion-max", chat.LastModelUsed);
        Assert.Null(chat.LastReasoningEffortUsed);
        Assert.Equal("low", settings.ReasoningEffort);

        var unpinned = await router.ExecuteAsync(new RemoteCommand(RemoteProtocol.Actions.PinModel)
            .With("model", "auto:balance").With("pinned", "false"), CancellationToken.None);
        Assert.True(unpinned.Ok, unpinned.Error);
        Assert.Empty(settings.FavoriteModelIds);
    });

    [Fact]
    public Task LaunchpadSetupCommands_KeepRetuneAndHideTheSetupsTheRowShows() => RunAsync(async () =>
    {
        Loc.Load("en");
        var repo = CreateTempGitRepo();
        try
        {
            var project = new Project { Name = "Lumi", WorkingDirectory = repo };
            var (store, main) = LaunchpadRig(project);
            using var _ = main;
            var data = store.Data;
            var router = new RemoteCommandRouter(store, main);
            var learned = Assert.Single(RemoteProjector.BuildNewChatExperience(store, main.ChatVM).Setups);
            Assert.Equal((project.Id, false, "gpt-6-luna", "low", false),
                (learned.ProjectId!.Value, learned.UseWorktree, learned.Model, learned.Effort, learned.IsKept));

            // The phone says what it wants, so asking twice keeps the setup once.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var kept = await router.ExecuteAsync(SetupCommand(RemoteProtocol.Actions.KeepSetup, learned)
                    .With("kept", "true"), CancellationToken.None);
                Assert.True(kept.Ok, kept.Error);
                Assert.Equal(Loc.Remote_SetupKept, kept.Message);
            }

            Assert.Equal((project.Id, false, "gpt-6-luna", "low"), PinOf(Assert.Single(data.PinnedSetups)));
            Assert.True(Assert.Single(RemoteProjector.BuildNewChatExperience(store, main.ChatVM).Setups).IsKept);

            var released = await router.ExecuteAsync(SetupCommand(RemoteProtocol.Actions.KeepSetup, learned)
                .With("kept", "false"), CancellationToken.None);
            Assert.True(released.Ok, released.Error);
            Assert.Equal(Loc.Remote_SetupNotKept, released.Message);
            Assert.Empty(data.PinnedSetups);

            // Moving a setup into a worktree keeps it there; the folder's setup is learned again beside it.
            var moved = await router.ExecuteAsync(SetupCommand(RemoteProtocol.Actions.RetuneSetup, learned)
                .With("toWorktree", "true"), CancellationToken.None);
            Assert.True(moved.Ok, moved.Error);
            Assert.Equal(Loc.Remote_SetupChanged, moved.Message);
            Assert.Equal((project.Id, true, "gpt-6-luna", "low"), PinOf(Assert.Single(data.PinnedSetups)));
            var row = RemoteProjector.BuildNewChatExperience(store, main.ChatVM).Setups;
            Assert.Equal([(true, true), (false, false)], row.Select(setup => (setup.UseWorktree, setup.IsKept)));

            // A model without efforts drops the setup's effort.
            var retuned = await router.ExecuteAsync(SetupCommand(RemoteProtocol.Actions.RetuneSetup, row[0])
                .With("toModel", "auto:balance"), CancellationToken.None);
            Assert.True(retuned.Ok, retuned.Error);
            Assert.Equal((project.Id, true, "auto:balance", (string?)null), PinOf(Assert.Single(data.PinnedSetups)));

            var worktree = RemoteProjector.BuildNewChatExperience(store, main.ChatVM).Setups[0];
            var hidden = await router.ExecuteAsync(SetupCommand(RemoteProtocol.Actions.HideSetup, worktree), CancellationToken.None);
            Assert.True(hidden.Ok, hidden.Error);
            Assert.Equal(Loc.Remote_SetupHidden, hidden.Message);
            Assert.Empty(data.PinnedSetups);
            Assert.Equal((project.Id, true, (string?)null, (string?)null), PinOf(Assert.Single(data.HiddenSetups)));
            Assert.False(Assert.Single(RemoteProjector.BuildNewChatExperience(store, main.ChatVM).Setups).UseWorktree);

            // The phone's draft names its model and quality as its picker shows them, a fusion tier included; keeping
            // it keeps the folder's learned setup too, so the row loses nothing.
            var draft = await router.ExecuteAsync(new RemoteCommand(RemoteProtocol.Actions.KeepSetup)
                .With("projectId", project.Id.ToString()).With("worktree", "false")
                .With("model", "auto:balance").With("quality", "Max").With("kept", "true"), CancellationToken.None);
            Assert.True(draft.Ok, draft.Error);
            Assert.Equal(
                [(project.Id, false, "auto:hydrafusion-max", (string?)null), (project.Id, false, "gpt-6-luna", "low")],
                data.PinnedSetups.Select(PinOf));
            Assert.Single(data.HiddenSetups);
            Assert.Equal(["auto:hydrafusion-max", "gpt-6-luna"],
                RemoteProjector.BuildNewChatExperience(store, main.ChatVM).Setups
                    .Where(setup => setup.IsKept).Select(setup => setup.Model!));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    });

    [Fact]
    public Task LaunchpadSetupCommands_ReadThePhonesQualityInTheDesktopsLanguage() => RunAsync(async () =>
    {
        var repo = CreateTempGitRepo();
        Loc.Load("he");
        try
        {
            var project = new Project { Name = "Lumi", WorkingDirectory = repo };
            var (store, main) = LaunchpadRig(project);
            using var _ = main;
            // The phone's picker shows the desktop's words for the effort, not its id.
            var quality = ModelSelectionHelper.EffortToDisplay("low");
            Assert.False(string.Equals(quality, "low", StringComparison.OrdinalIgnoreCase));
            var kept = await new RemoteCommandRouter(store, main).ExecuteAsync(new RemoteCommand(RemoteProtocol.Actions.KeepSetup)
                .With("projectId", project.Id.ToString()).With("worktree", "true")
                .With("model", "gpt-6-luna").With("quality", quality).With("kept", "true"), CancellationToken.None);
            Assert.True(kept.Ok, kept.Error);
            Assert.Equal((project.Id, true, "gpt-6-luna", "low"), PinOf(Assert.Single(store.Data.PinnedSetups)));
        }
        finally
        {
            Loc.Load("en");
            TryDeleteDirectory(repo);
        }
    });

    [Fact]
    public Task LaunchpadSetupCommands_RefuseWhatThisComputerCannotStartAndChangeNothing() => RunAsync(async () =>
    {
        Loc.Load("en");
        var repo = CreateTempGitRepo();
        try
        {
            var project = new Project { Name = "Lumi", WorkingDirectory = repo };
            var notes = new Project { Name = "Notes" };
            var (store, main) = LaunchpadRig(project, notes);
            using var _ = main;
            var router = new RemoteCommandRouter(store, main);
            var learned = Assert.Single(RemoteProjector.BuildNewChatExperience(store, main.ChatVM).Setups);

            await Refused(new RemoteCommand(RemoteProtocol.Actions.KeepSetup)
                .With("projectId", Guid.NewGuid().ToString()).With("kept", "true"), Loc.Remote_SetupUnavailable);
            await Refused(new RemoteCommand(RemoteProtocol.Actions.HideSetup)
                .With("agentId", Guid.NewGuid().ToString()), Loc.Remote_SetupUnavailable);
            // Just chat is no setup to keep or hide.
            await Refused(new RemoteCommand(RemoteProtocol.Actions.KeepSetup)
                .With("model", "gpt-6-luna").With("kept", "true"), Loc.Remote_SetupUnavailable);
            await Refused(SetupCommand(RemoteProtocol.Actions.KeepSetup, learned),
                Loc.Get("Remote_ArgumentRequired", "kept"));
            await Refused(new RemoteCommand(RemoteProtocol.Actions.RetuneSetup)
                .With("projectId", notes.Id.ToString()).With("toWorktree", "true"), Loc.Remote_SetupUnavailable);
            await Refused(SetupCommand(RemoteProtocol.Actions.RetuneSetup, learned)
                .With("toModel", "not-a-model"), Loc.Remote_ModelUnavailable);
            await Refused(SetupCommand(RemoteProtocol.Actions.RetuneSetup, learned)
                .With("toModel", "gpt-6-luna"), Loc.Remote_NothingToConfigure);
            await Refused(SetupCommand(RemoteProtocol.Actions.RetuneSetup, learned), Loc.Remote_NothingToConfigure);
            await Refused(new RemoteCommand(RemoteProtocol.Actions.UseStarter).With("id", "not-a-starter"),
                Loc.Get("Remote_ArgumentInvalid", "id"));

            Assert.Empty(store.Data.PinnedSetups);
            Assert.Empty(store.Data.HiddenSetups);
            Assert.Empty(store.Data.StarterUses);

            async Task Refused(RemoteCommand command, string error)
            {
                var result = await router.ExecuteAsync(command, CancellationToken.None);
                Assert.False(result.Ok, command.Action);
                Assert.Equal(error, result.Error);
            }
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    });

    [Fact]
    public Task LaunchpadSetupCommands_TakeASetupWithoutProjectOrAgent_ThatTheRowShows() => RunAsync(async () =>
    {
        Loc.Load("en");
        var settings = TestSettings();
        settings.PreferredModel = "gpt-6-luna";
        settings.ReasoningEffort = "high";
        // New chats start on GPT-6 Luna, high; the user's chats outside projects run it on low, a setup of its own.
        var data = new AppData { Settings = settings };
        data.Chats.AddRange(Enumerable.Range(1, 2).Select(daysAgo => new Chat
        {
            Title = $"Chat {daysAgo}",
            LastModelUsed = "gpt-6-luna",
            LastReasoningEffortUsed = "low",
            MessageCount = 6,
            CreatedAt = DateTimeOffset.Now.AddDays(-daysAgo),
            UpdatedAt = DateTimeOffset.Now.AddDays(-daysAgo)
        }));
        var store = new DataStore(data);
        using var main = new MainViewModel(store, TestCopilot.Shared, new UpdateService(),
            startBackgroundJobs: false, initializeCopilotOnStartup: false);
        var models = CopilotModelCatalog.BuildModels(CopilotModelCatalogTests.NativeCatalog());
        main.ChatVM.UpdateModelCapabilities(models);
        main.SettingsVM.UpdateModelCapabilities(models);
        main.ChatVM.ApplyAvailableModels(models.Select(model => model.Id).ToArray(), "gpt-6-luna");
        var router = new RemoteCommandRouter(store, main);
        var setup = Assert.Single(RemoteProjector.BuildNewChatExperience(store, main.ChatVM).Setups);
        Assert.Equal(((Guid?)null, (Guid?)null, "gpt-6-luna", "low"), (setup.ProjectId, setup.AgentId, setup.Model, setup.Effort));

        var kept = await router.ExecuteAsync(SetupCommand(RemoteProtocol.Actions.KeepSetup, setup).With("kept", "true"),
            CancellationToken.None);
        Assert.True(kept.Ok, kept.Error);
        var pin = Assert.Single(data.PinnedSetups);
        Assert.Equal(((Guid?)null, (Guid?)null, "gpt-6-luna", "low"), (pin.ProjectId, pin.AgentId, pin.ModelId, pin.Effort));

        var hidden = await router.ExecuteAsync(SetupCommand(RemoteProtocol.Actions.HideSetup, setup), CancellationToken.None);
        Assert.True(hidden.Ok, hidden.Error);
        Assert.Empty(data.PinnedSetups);
        Assert.Empty(RemoteProjector.BuildNewChatExperience(store, main.ChatVM).Setups);

        // Plain chat itself, on the model new chats start with, is still no setup to keep.
        var plain = await router.ExecuteAsync(new RemoteCommand(RemoteProtocol.Actions.KeepSetup)
            .With("model", "gpt-6-luna").With("effort", "high").With("kept", "true"), CancellationToken.None);
        Assert.False(plain.Ok);
        Assert.Equal(Loc.Remote_SetupUnavailable, plain.Error);
    });

    [Fact]
    public Task UseStarter_TeachesForYouTheUsersHabitInThatProject() => RunAsync(async () =>
    {
        Loc.Load("en");
        var project = new Project { Name = "Trips" };
        var (store, main) = LaunchpadRig(project);
        using var _ = main;
        var router = new RemoteCommandRouter(store, main);
        for (var pick = 0; pick < 2; pick++)
        {
            var used = await router.ExecuteAsync(new RemoteCommand(RemoteProtocol.Actions.UseStarter)
                .With("id", "plan.trip").With("projectId", project.Id.ToString()), CancellationToken.None);
            Assert.True(used.Ok, used.Error);
            Assert.Equal(Loc.Remote_StarterNoted, used.Message);
        }

        // A project this computer no longer has counts as no project.
        var elsewhere = await router.ExecuteAsync(new RemoteCommand(RemoteProtocol.Actions.UseStarter)
            .With("id", "learn.quiz").With("projectId", Guid.NewGuid().ToString()), CancellationToken.None);
        Assert.True(elsewhere.Ok, elsewhere.Error);

        Assert.Equal([("plan.trip", (Guid?)project.Id, 2), ("learn.quiz", (Guid?)null, 1)],
            store.Data.StarterUses.Select(use => (use.Id, use.ProjectId, use.Count)));
        var forYou = RemoteProjector.BuildNewChatExperience(store, main.ChatVM).Scopes
            .Single(scope => scope.ProjectId == project.Id).ForYou;
        Assert.Equal("plan.trip", forYou[0].Id);
        Assert.Equal(Loc.Launchpad_ReasonHabit, forYou[0].Reason);
    });

    [Fact]
    public Task InteractiveEditsSaveWithoutOpeningAnotherDesktopChat() => RunAsync(async () =>
    {
        var current = new Chat { Title = "Current" };
        var answer = new ChatMessage
        {
            Role = "assistant",
            Content = "```checklist\n{\"id\":\"prep\",\"title\":\"Prep\",\"items\":[{\"id\":\"one\",\"label\":\"First step\"}]}\n```"
        };
        var target = new Chat { Title = "Target" };
        target.Messages.Add(answer);
        var store = new DataStore(new AppData { Settings = TestSettings(), Chats = [current, target] });
        using var main = new MainViewModel(store, TestCopilot.Shared, new UpdateService(), initializeCopilotOnStartup: false);
        await main.OpenChatByIdAsync(current.Id);
        var router = new RemoteCommandRouter(store, main);
        var state = new StrataTheme.Controls.InteractiveState { CheckedIds = ["one"] }.ToJson();
        var result = await router.ExecuteAsync(new RemoteCommand(RemoteProtocol.Actions.UpdateInteraction)
            .With("chatId", target.Id.ToString()).With("messageId", answer.Id.ToString())
            .With("blockId", "prep").With("state", state), CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(current.Id, main.ChatVM.CurrentChat!.Id);
        Assert.Equal(state, answer.InteractionStates!["prep"]);
        Assert.Single(target.Messages);
    });

    [Fact]
    public Task InteractiveCommandRejectsUnknownIdsAndDoesNotStartWork() => RunAsync(async () =>
    {
        var chat = new Chat();
        var store = new DataStore(new AppData { Settings = TestSettings(), Chats = [chat] });
        using var main = new MainViewModel(store, TestCopilot.Shared, new UpdateService(), initializeCopilotOnStartup: false);
        var router = new RemoteCommandRouter(store, main);
        var result = await router.ExecuteAsync(new RemoteCommand(RemoteProtocol.Actions.UpdateInteraction)
            .With("chatId", chat.Id.ToString()).With("messageId", Guid.NewGuid().ToString())
            .With("blockId", "missing").With("state", "{}").With("action", "execute"), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Empty(chat.Messages);
    });

    /// <summary>
    /// A phone's pin is the desktop's own: the model joins the PC's favorites after those already
    /// pinned, in the order every picker lists them; pinning twice is harmless, unpinning removes it,
    /// and only a model the PC offers can be pinned.
    /// </summary>
    [Fact]
    public Task PinModelSetsTheDesktopsFavorites() => RunAsync(async () =>
    {
        var settings = TestSettings();
        settings.FavoriteModelIds = ["claude-opus-5"];
        var store = new DataStore(new AppData { Settings = settings });
        using var main = new MainViewModel(store, TestCopilot.Shared, new UpdateService(), initializeCopilotOnStartup: false);
        main.ChatVM.ApplyAvailableModels(["gpt-5.4", "claude-opus-5", "auto"], "gpt-5.4");
        var router = new RemoteCommandRouter(store, main);
        var saves = 0;
        store.IndexSaved += () => saves++;

        Task<RemoteCommandResult> Pin(string? model, string? pinned) => router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.PinModel).With("model", model).With("pinned", pinned),
            CancellationToken.None);

        var pinned = await Pin("gpt-5.4", "true");
        Assert.True(pinned.Ok, pinned.Error);
        Assert.Equal(1, saves);
        Assert.True((await Pin("gpt-5.4", "True")).Ok);
        Assert.Equal(["claude-opus-5", "gpt-5.4"], settings.FavoriteModelIds);
        Assert.Equal(["claude-opus-5", "gpt-5.4", "auto"],
            ModelOptionCatalog.OrderPinnedFirst(main.ChatVM.AvailableModels, settings.FavoriteModelIds));

        Assert.True((await Pin("claude-opus-5", "false")).Ok);
        Assert.Equal(["gpt-5.4"], settings.FavoriteModelIds);

        var unknown = await Pin("retired-model", "true");
        Assert.False(unknown.Ok);
        Assert.Equal(Loc.Remote_ModelUnavailable, unknown.Error);
        Assert.False((await Pin("", "true")).Ok);
        Assert.False((await Pin("auto", null)).Ok);
        Assert.Equal(["gpt-5.4"], settings.FavoriteModelIds);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public Task PausedChatRejectsSendSteerAndStopAndSend(bool steer, bool stopAndSend) => RunAsync(async () =>
    {
        Loc.Load("en");
        var chat = new Chat { IsPaused = true };
        var store = new DataStore(new AppData { Settings = TestSettings(), Chats = [chat] });
        using var main = new MainViewModel(
            store, TestCopilot.Shared, new UpdateService(), initializeCopilotOnStartup: false);
        var router = new RemoteCommandRouter(store, main);
        var command = new RemoteCommand(RemoteProtocol.Actions.SendMessage)
            .With("chatId", chat.Id.ToString())
            .With("message", "must wait")
            .With("steer", steer.ToString())
            .With("stopAndSend", stopAndSend.ToString());

        var result = await router.ExecuteAsync(command, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(Loc.Get("Chat_PausedSendBlocked"), result.Error);
        Assert.True(chat.IsPaused);
        Assert.Empty(chat.Messages);
    });

    [Fact]
    public Task PersistedRemoteReceiptShortCircuitsARetryAfterRestart() => RunAsync(async () =>
    {
        var chat = new Chat
        {
            Title = "Accepted",
            LastRemoteDeviceId = "phone-1",
            LastRemoteRequestId = "request-1"
        };
        var dataStore = new DataStore(new AppData
        {
            Settings = TestSettings(),
            Chats = [chat]
        });
        using var main = new MainViewModel(
            dataStore,
            TestCopilot.Shared,
            new UpdateService(),
            initializeCopilotOnStartup: false);
        var started = false;
        var router = new RemoteCommandRouter(
            dataStore,
            main,
            (_, _, _, _, _, _) =>
            {
                started = true;
                return Task.FromResult<string?>(null);
            });
        var command = new RemoteCommand(RemoteProtocol.Actions.SendMessage)
        {
            AuthenticatedDeviceId = "phone-1",
            RequestId = "request-1"
        }.With("message", "continue").With("newChat", "true");

        var result = await router.ExecuteAsync(command, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(chat.Id, result.ChatId);
        Assert.False(started);
        Assert.Single(dataStore.Data.Chats);
    });

    [Fact]
    public Task AcceptedRemoteReceiptMarksTheChatIndexDirtyBeforeAcknowledgement() => RunAsync(async () =>
    {
        var chat = new Chat { Title = "Existing", LastModelUsed = "auto", MessageCount = 1 };
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "previous" });
        var dataStore = new DataStore(new AppData
        {
            Settings = TestSettings(),
            Chats = [chat]
        });
        using var main = new MainViewModel(
            dataStore,
            TestCopilot.Shared,
            new UpdateService(),
            initializeCopilotOnStartup: false);
        await main.OpenChatByIdAsync(chat.Id);
        var dirtyBefore = DirtyChatVersion(dataStore, chat.Id);
        var dirtyAtAcceptance = dirtyBefore;
        using var cancellation = new CancellationTokenSource();

        try
        {
            await main.ChatVM.SendExternalMessageAsync(
                chat,
                "continue",
                "Lumi Mobile",
                cancellation.Token,
                onAccepted: () =>
                {
                    dirtyAtAcceptance = DirtyChatVersion(dataStore, chat.Id);
                    cancellation.Cancel();
                },
                remoteDeviceId: "phone-1",
                remoteRequestId: "request-2");
        }
        catch (OperationCanceledException)
        {
            // Cancellation is triggered at the exact acceptance boundary so no model request starts.
        }

        Assert.Equal("phone-1", chat.LastRemoteDeviceId);
        Assert.Equal("request-2", chat.LastRemoteRequestId);
        Assert.True(
            dirtyAtAcceptance > dirtyBefore,
            $"expected acceptance to mark the index dirty; before={dirtyBefore}, accepted={dirtyAtAcceptance}");
    });

    [Fact]
    public Task AnExternalSendRetiresTheSavedSuggestionsOfTheReplyItMovesPast() => RunAsync(async () =>
    {
        // A phone shows a chat's saved suggestions even while the PC has its transcript unloaded, so a
        // message from a phone or a job must retire them just as one typed on the desktop does.
        var answer = new ChatMessage { Role = "assistant", Content = "The change is ready." };
        var chat = new Chat
        {
            Title = "Existing", LastModelUsed = "auto", MessageCount = 2,
            FollowUpSuggestionAssistantMessageId = answer.Id,
            FollowUpSuggestions = ["Show the diff", "Run the tests"]
        };
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "previous" });
        chat.Messages.Add(answer);
        var dataStore = new DataStore(new AppData { Settings = TestSettings(), Chats = [chat] });
        using var main = new MainViewModel(
            dataStore,
            TestCopilot.Shared,
            new UpdateService(),
            initializeCopilotOnStartup: false);
        await main.OpenChatByIdAsync(chat.Id);
        using var cancellation = new CancellationTokenSource();

        try
        {
            await main.ChatVM.SendExternalMessageAsync(
                chat,
                "continue",
                "Lumi Mobile",
                cancellation.Token,
                onAccepted: () => cancellation.Cancel(),
                remoteDeviceId: "phone-1",
                remoteRequestId: "request-3");
        }
        catch (OperationCanceledException)
        {
            // Cancellation is triggered at the acceptance boundary so no model request starts.
        }

        Assert.Empty(chat.FollowUpSuggestions);
        Assert.Null(chat.FollowUpSuggestionAssistantMessageId);
    });

    [Fact]
    public Task OpeningAChatMarksItReadWithoutChangingTheDesktopSurface() => RunAsync(async () =>
    {
        MainViewModel? main = null;
        try
        {
            var current = new Chat { Title = "Current" };
            var target = new Chat { Title = "Unread", HasUnreadMessages = true };
            var dataStore = new DataStore(new AppData
            {
                Settings = TestSettings(),
                Chats = [current, target]
            });
            main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            await main.OpenChatByIdAsync(current.Id);
            var dirtyBefore = DirtyChatVersion(dataStore, target.Id);
            var router = new RemoteCommandRouter(dataStore, main);

            current.HasUnreadMessages = true;
            var implicitRead = await router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.OpenChat),
                CancellationToken.None);
            Assert.False(implicitRead.Ok);
            Assert.True(current.HasUnreadMessages);

            var result = await router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.OpenChat)
                    .With("chatId", target.Id.ToString()),
                CancellationToken.None);

            Assert.True(result.Ok, result.Error);
            Assert.False(target.HasUnreadMessages);
            Assert.True(DirtyChatVersion(dataStore, target.Id) > dirtyBefore);
            Assert.Equal(current.Id, main.ChatVM.CurrentChat?.Id);
        }
        finally
        {
            main?.Dispose();
        }
    });

    [Fact]
    public Task DelayedReadAcknowledgementDoesNotClearNewerUnreadActivity() => RunAsync(async () =>
    {
        MainViewModel? main = null;
        try
        {
            var chat = new Chat
            {
                Title = "Unread",
                HasUnreadMessages = true,
                MessageCount = 2
            };
            var dataStore = new DataStore(new AppData
            {
                Settings = TestSettings(),
                Chats = [chat]
            });
            main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            var router = new RemoteCommandRouter(dataStore, main);

            var result = await router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.OpenChat)
                    .With("chatId", chat.Id.ToString())
                    .With("readThroughMessageCount", "1"),
                CancellationToken.None);

            Assert.True(result.Ok, result.Error);
            Assert.True(chat.HasUnreadMessages);
        }
        finally
        {
            main?.Dispose();
        }
    });

    [Fact]
    public Task StartedWorktreeChatRejectsRemoteProjectChanges() => RunAsync(async () =>
    {
        MainViewModel? main = null;
        try
        {
            var originalProject = new Project { Name = "Original" };
            var otherProject = new Project { Name = "Other" };
            var chat = new Chat
            {
                Title = "Started worktree",
                ProjectId = originalProject.Id,
                WorktreePath = @"C:\worktrees\started",
                MessageCount = 1
            };
            var dataStore = new DataStore(new AppData
            {
                Settings = TestSettings(),
                Projects = [originalProject, otherProject],
                Chats = [chat]
            });
            main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            await main.OpenChatByIdAsync(chat.Id);
            var router = new RemoteCommandRouter(dataStore, main);

            var result = await router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.ConfigureChat)
                    .With("chatId", chat.Id.ToString())
                    .With("projectId", otherProject.Id.ToString()),
                CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Equal(originalProject.Id, chat.ProjectId);
            Assert.Equal(@"C:\worktrees\started", chat.WorktreePath);
        }
        finally
        {
            main?.Dispose();
        }
    });

    [Fact]
    public Task StartedWorktreeSendRejectsProjectChangesBeforeStartingTheTurn() => RunAsync(async () =>
    {
        MainViewModel? main = null;
        try
        {
            var originalProject = new Project { Name = "Original" };
            var otherProject = new Project { Name = "Other" };
            var chat = new Chat
            {
                Title = "Started worktree",
                ProjectId = originalProject.Id,
                WorktreePath = @"C:\worktrees\started",
                MessageCount = 1
            };
            var dataStore = new DataStore(new AppData
            {
                Settings = TestSettings(),
                Projects = [originalProject, otherProject],
                Chats = [chat]
            });
            main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            await main.OpenChatByIdAsync(chat.Id);
            var started = false;
            var router = new RemoteCommandRouter(
                dataStore,
                main,
                (_, _, _, _, _, _) =>
                {
                    started = true;
                    return Task.FromResult<string?>(null);
                });

            var result = await router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                    .With("chatId", chat.Id.ToString())
                    .With("message", "continue")
                    .With("projectId", otherProject.Id.ToString()),
                CancellationToken.None);

            Assert.False(result.Ok);
            Assert.False(started);
            Assert.Equal(originalProject.Id, chat.ProjectId);
            Assert.Equal(@"C:\worktrees\started", chat.WorktreePath);
        }
        finally
        {
            main?.Dispose();
        }
    });

    [Fact]
    public Task StartedWorktreeSendRejectsStaleLocalIntent() => RunAsync(async () =>
    {
        MainViewModel? main = null;
        try
        {
            var project = new Project { Name = "Code" };
            var chat = new Chat
            {
                Title = "Started worktree",
                ProjectId = project.Id,
                WorktreePath = @"C:\worktrees\started",
                MessageCount = 1
            };
            var dataStore = new DataStore(new AppData
            {
                Settings = TestSettings(),
                Projects = [project],
                Chats = [chat]
            });
            main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            await main.OpenChatByIdAsync(chat.Id);
            var started = false;
            var router = new RemoteCommandRouter(
                dataStore,
                main,
                (_, _, _, _, _, _) =>
                {
                    started = true;
                    return Task.FromResult<string?>(null);
                });

            var result = await router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                    .With("chatId", chat.Id.ToString())
                    .With("message", "continue")
                    .With("worktree", "false"),
                CancellationToken.None);

            Assert.False(result.Ok);
            Assert.False(started);
            Assert.Equal(@"C:\worktrees\started", chat.WorktreePath);
        }
        finally
        {
            main?.Dispose();
        }
    });

    [Fact]
    public Task FirstTurnReservationBlocksACompetingDesktopSend() => RunAsync(async () =>
    {
        MainViewModel? main = null;
        try
        {
            var chat = new Chat { Title = "Empty" };
            var dataStore = new DataStore(new AppData
            {
                Settings = TestSettings(),
                Chats = [chat]
            });
            main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            await main.OpenChatByIdAsync(chat.Id);
            var startEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseStart = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var router = new RemoteCommandRouter(
                dataStore,
                main,
                async (_, _, _, _, _, _) =>
                {
                    startEntered.TrySetResult();
                    await releaseStart.Task;
                    return null;
                });

            var remoteSend = router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                    .With("chatId", chat.Id.ToString())
                    .With("message", "phone send"),
                CancellationToken.None);
            await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(main.ChatVM.IsChatBusy(chat.Id));

            main.ChatVM.PromptText = "desktop send";
            await main.ChatVM.SendMessageCommand.ExecuteAsync(null);

            Assert.Empty(chat.Messages);
            Assert.Equal("desktop send", main.ChatVM.PromptText);

            releaseStart.TrySetResult();
            var result = await remoteSend;
            Assert.True(result.Ok, result.Error);
        }
        finally
        {
            main?.Dispose();
        }
    });

    [Fact]
    public Task FirstTurnReservationBlocksDesktopProjectChanges() => RunAsync(async () =>
    {
        MainViewModel? main = null;
        ChatViewModel.ExternalSendReservation? reservation = null;
        try
        {
            var originalProject = new Project { Name = "Original" };
            var otherProject = new Project { Name = "Other" };
            var chat = new Chat { Title = "Empty", ProjectId = originalProject.Id };
            var dataStore = new DataStore(new AppData
            {
                Settings = TestSettings(),
                Projects = [originalProject, otherProject],
                Chats = [chat]
            });
            main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            await main.OpenChatByIdAsync(chat.Id);
            reservation = main.ChatVM.TryReserveExternalSend(chat.Id);
            Assert.NotNull(reservation);
            Assert.True(main.ChatVM.OwnsAnyLiveChat());

            main.ChatVM.SetProjectId(otherProject.Id);
            main.ChatVM.ClearProjectId();

            Assert.Equal(originalProject.Id, chat.ProjectId);
        }
        finally
        {
            reservation?.Dispose();
            main?.Dispose();
        }
    });

    [Fact]
    public Task StopCancelsAReservedFirstTurnBeforeItStarts() => RunAsync(async () =>
    {
        MainViewModel? main = null;
        ChatViewModel.ExternalSendReservation? reservation = null;
        try
        {
            var chat = new Chat { Title = "Empty" };
            var dataStore = new DataStore(new AppData
            {
                Settings = TestSettings(),
                Chats = [chat]
            });
            main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            await main.OpenChatByIdAsync(chat.Id);
            reservation = main.ChatVM.TryReserveExternalSend(chat.Id);
            Assert.NotNull(reservation);
            var router = new RemoteCommandRouter(dataStore, main);

            var result = await router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.StopGeneration)
                    .With("chatId", chat.Id.ToString()),
                CancellationToken.None);

            Assert.True(result.Ok, result.Error);
            Assert.True(reservation.IsCancellationRequested);
            Assert.Empty(chat.Messages);
        }
        finally
        {
            reservation?.Dispose();
            main?.Dispose();
        }
    });

    [Fact]
    public Task CanceledPreflightRemovesTheWorktreeItCreated() => RunAsync(async () =>
    {
        var repo = CreateTempGitRepo();
        MainViewModel? main = null;
        string? createdPath = null;
        try
        {
            var project = new Project { Name = "Code", WorkingDirectory = repo };
            var chat = new Chat { Title = "Empty", ProjectId = project.Id };
            var dataStore = new DataStore(new AppData
            {
                Settings = TestSettings(),
                Projects = [project],
                Chats = [chat]
            });
            main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            await main.OpenChatByIdAsync(chat.Id);
            var startEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseStart = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var router = new RemoteCommandRouter(
                dataStore,
                main,
                async (_, targetChat, _, _, _, _) =>
                {
                    createdPath = targetChat.WorktreePath;
                    startEntered.TrySetResult();
                    await releaseStart.Task;
                    return "The pending turn start was canceled.";
                });

            var send = router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                    .With("chatId", chat.Id.ToString())
                    .With("message", "phone send")
                    .With("worktree", "true"),
                CancellationToken.None);
            await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(createdPath);
            Assert.True(Directory.Exists(createdPath));

            var stop = await router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.StopGeneration)
                    .With("chatId", chat.Id.ToString()),
                CancellationToken.None);
            Assert.True(stop.Ok, stop.Error);
            releaseStart.TrySetResult();

            var sendResult = await send;
            Assert.False(sendResult.Ok);
            Assert.Null(chat.WorktreePath);
            Assert.False(Directory.Exists(createdPath));
        }
        finally
        {
            main?.Dispose();
            if (createdPath is not null && Directory.Exists(createdPath))
                await GitService.RemoveWorktreeAsync(repo, createdPath);
            TryDeleteDirectory(repo);
        }
    });

    [Fact]
    public Task CanceledPreflightDoesNotRemoveAPreexistingDeterministicWorktree() => RunAsync(async () =>
    {
        var repo = CreateTempGitRepo();
        MainViewModel? main = null;
        string? existingPath = null;
        try
        {
            var project = new Project { Name = "Code", WorkingDirectory = repo };
            var chat = new Chat { Title = "Empty", ProjectId = project.Id };
            var branchName = $"lumi/{chat.Id:N}"[..13];
            existingPath = await GitService.CreateWorktreeAsync(repo, branchName);
            Assert.NotNull(existingPath);
            var dataStore = new DataStore(new AppData
            {
                Settings = TestSettings(),
                Projects = [project],
                Chats = [chat]
            });
            main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            await main.OpenChatByIdAsync(chat.Id);
            var startEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseStart = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var router = new RemoteCommandRouter(
                dataStore,
                main,
                async (_, _, _, _, _, _) =>
                {
                    startEntered.TrySetResult();
                    await releaseStart.Task;
                    return "The pending turn start was canceled.";
                });

            var send = router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                    .With("chatId", chat.Id.ToString())
                    .With("message", "phone send")
                    .With("worktree", "true"),
                CancellationToken.None);
            await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var stop = await router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.StopGeneration)
                    .With("chatId", chat.Id.ToString()),
                CancellationToken.None);
            Assert.True(stop.Ok, stop.Error);
            releaseStart.TrySetResult();
            var sendResult = await send;

            Assert.False(sendResult.Ok);
            Assert.True(Directory.Exists(existingPath));
        }
        finally
        {
            main?.Dispose();
            if (existingPath is not null && Directory.Exists(existingPath))
                await GitService.RemoveWorktreeAsync(repo, existingPath);
            TryDeleteDirectory(repo);
        }
    });

    [Fact]
    public Task AcceptedTurnIsNotRejectedWhenProjectChangesAfterAcceptance() => RunAsync(async () =>
    {
        var repo = CreateTempGitRepo();
        MainViewModel? main = null;
        string? createdPath = null;
        try
        {
            var originalProject = new Project { Name = "Original", WorkingDirectory = repo };
            var otherProject = new Project { Name = "Other", WorkingDirectory = repo };
            var chat = new Chat { Title = "Empty", ProjectId = originalProject.Id };
            var dataStore = new DataStore(new AppData
            {
                Settings = TestSettings(),
                Projects = [originalProject, otherProject],
                Chats = [chat]
            });
            main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            await main.OpenChatByIdAsync(chat.Id);
            var router = new RemoteCommandRouter(
                dataStore,
                main,
                (_, targetChat, _, _, _, _) =>
                {
                    createdPath = targetChat.WorktreePath;
                    targetChat.ProjectId = otherProject.Id;
                    return Task.FromResult<string?>(null);
                });

            var result = await router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                    .With("chatId", chat.Id.ToString())
                    .With("message", "phone send")
                    .With("worktree", "true"),
                CancellationToken.None);

            Assert.True(result.Ok, result.Error);
            Assert.NotNull(createdPath);
            Assert.True(Directory.Exists(createdPath));
            Assert.Equal(createdPath, chat.WorktreePath);
        }
        finally
        {
            main?.Dispose();
            if (createdPath is not null && Directory.Exists(createdPath))
                await GitService.RemoveWorktreeAsync(repo, createdPath);
            TryDeleteDirectory(repo);
        }
    });

    [Fact]
    public Task RejectedProjectDirectoryChangeBeforeAcceptanceRemovesOwnedWorktree() => RunAsync(async () =>
    {
        var originalRepo = CreateTempGitRepo();
        var otherRepo = CreateTempGitRepo();
        MainViewModel? main = null;
        string? createdPath = null;
        try
        {
            var project = new Project { Name = "Code", WorkingDirectory = originalRepo };
            var chat = new Chat { Title = "Empty", ProjectId = project.Id };
            var dataStore = new DataStore(new AppData
            {
                Settings = TestSettings(),
                Projects = [project],
                Chats = [chat]
            });
            main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            await main.OpenChatByIdAsync(chat.Id);
            var router = new RemoteCommandRouter(
                dataStore,
                main,
                (_, targetChat, _, _, _, _) =>
                {
                    createdPath = targetChat.WorktreePath;
                    project.WorkingDirectory = otherRepo;
                    return Task.FromResult<string?>("The chat project changed while its turn was starting.");
                });

            var result = await router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                    .With("chatId", chat.Id.ToString())
                    .With("message", "phone send")
                    .With("worktree", "true"),
                CancellationToken.None);

            Assert.False(result.Ok);
            Assert.NotNull(createdPath);
            Assert.False(Directory.Exists(createdPath));
            Assert.Null(chat.WorktreePath);
        }
        finally
        {
            main?.Dispose();
            if (createdPath is not null && Directory.Exists(createdPath))
                await GitService.RemoveWorktreeAsync(originalRepo, createdPath);
            TryDeleteDirectory(originalRepo);
            TryDeleteDirectory(otherRepo);
        }
    });

    [Fact]
    public Task CanceledPreflightKeepsAWorktreeThatBecameDirty() => RunAsync(async () =>
    {
        var repo = CreateTempGitRepo();
        MainViewModel? main = null;
        string? createdPath = null;
        try
        {
            var project = new Project { Name = "Code", WorkingDirectory = repo };
            var chat = new Chat { Title = "Empty", ProjectId = project.Id };
            var dataStore = new DataStore(new AppData
            {
                Settings = TestSettings(),
                Projects = [project],
                Chats = [chat]
            });
            main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            await main.OpenChatByIdAsync(chat.Id);
            var startEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseStart = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var router = new RemoteCommandRouter(
                dataStore,
                main,
                async (_, targetChat, _, _, _, _) =>
                {
                    createdPath = targetChat.WorktreePath;
                    startEntered.TrySetResult();
                    await releaseStart.Task;
                    return "The pending turn start was canceled.";
                });

            var send = router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                    .With("chatId", chat.Id.ToString())
                    .With("message", "phone send")
                    .With("worktree", "true"),
                CancellationToken.None);
            await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(createdPath);
            await File.WriteAllTextAsync(Path.Combine(createdPath, "keep.txt"), "user change");

            var stop = await router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.StopGeneration)
                    .With("chatId", chat.Id.ToString()),
                CancellationToken.None);
            Assert.True(stop.Ok, stop.Error);
            releaseStart.TrySetResult();

            var sendResult = await send;
            Assert.False(sendResult.Ok);
            Assert.Contains("kept", sendResult.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(createdPath));
            Assert.Equal(createdPath, chat.WorktreePath);
        }
        finally
        {
            main?.Dispose();
            if (createdPath is not null && Directory.Exists(createdPath))
                await GitService.RemoveWorktreeAsync(repo, createdPath);
            TryDeleteDirectory(repo);
        }
    });

    [Fact]
    public Task CanceledPreflightKeepsAWorktreeReferencedByAnotherChat() => RunAsync(async () =>
    {
        var repo = CreateTempGitRepo();
        MainViewModel? main = null;
        string? createdPath = null;
        try
        {
            var project = new Project { Name = "Code", WorkingDirectory = repo };
            var chat = new Chat { Title = "Empty", ProjectId = project.Id };
            var dataStore = new DataStore(new AppData
            {
                Settings = TestSettings(),
                Projects = [project],
                Chats = [chat]
            });
            main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            await main.OpenChatByIdAsync(chat.Id);
            var startEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseStart = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var router = new RemoteCommandRouter(
                dataStore,
                main,
                async (_, targetChat, _, _, _, _) =>
                {
                    createdPath = targetChat.WorktreePath;
                    startEntered.TrySetResult();
                    await releaseStart.Task;
                    return "The pending turn start was canceled.";
                });

            var send = router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                    .With("chatId", chat.Id.ToString())
                    .With("message", "phone send")
                    .With("worktree", "true"),
                CancellationToken.None);
            await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(createdPath);
            dataStore.Data.Chats.Add(new Chat
            {
                Title = "Shared",
                ProjectId = project.Id,
                WorktreePath = createdPath
            });

            var stop = await router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.StopGeneration)
                    .With("chatId", chat.Id.ToString()),
                CancellationToken.None);
            Assert.True(stop.Ok, stop.Error);
            releaseStart.TrySetResult();

            var sendResult = await send;
            Assert.False(sendResult.Ok);
            Assert.Contains("another chat", sendResult.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(createdPath));
            Assert.Equal(createdPath, chat.WorktreePath);
        }
        finally
        {
            main?.Dispose();
            if (createdPath is not null && Directory.Exists(createdPath))
                await GitService.RemoveWorktreeAsync(repo, createdPath);
            TryDeleteDirectory(repo);
        }
    });

    [Fact]
    public Task CleanupReservationBlocksNewAssociationsAndCanRestoreItsOwner() => RunAsync(() =>
    {
        var worktreePath = Path.Combine(Path.GetTempPath(), $"lumi-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreePath);
        try
        {
            var owner = new Chat { Title = "Owner", WorktreePath = worktreePath };
            var other = new Chat { Title = "Other" };
            var dataStore = new DataStore(new AppData { Chats = [owner, other] });

            using var reservation = dataStore.TryReserveWorktreeCleanup(
                owner,
                worktreePath,
                out var isShared);

            Assert.NotNull(reservation);
            Assert.False(isShared);
            Assert.Null(owner.WorktreePath);
            Assert.True(dataStore.IsWorktreeCleanupReserved(worktreePath));
            Assert.False(dataStore.TrySetChatWorktreePath(other, worktreePath));
            Assert.True(reservation.RestoreOwnerAssociation());
            Assert.Equal(Path.GetFullPath(worktreePath), Path.GetFullPath(owner.WorktreePath!));
        }
        finally
        {
            TryDeleteDirectory(worktreePath);
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task ReservedFirstTurnBlocksDesktopAndRemoteDeletion() => RunAsync(async () =>
    {
        MainViewModel? main = null;
        ChatViewModel.ExternalSendReservation? reservation = null;
        try
        {
            var chat = new Chat { Title = "Empty" };
            var dataStore = new DataStore(new AppData
            {
                Settings = TestSettings(),
                Chats = [chat]
            });
            main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            await main.OpenChatByIdAsync(chat.Id);
            reservation = main.ChatVM.TryReserveExternalSend(chat.Id);
            Assert.NotNull(reservation);

            main.DeleteChatCommand.Execute(chat);
            Assert.Contains(chat, dataStore.Data.Chats);

            var router = new RemoteCommandRouter(dataStore, main);
            var result = await router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.DeleteChat)
                    .With("chatId", chat.Id.ToString()),
                CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Contains("first turn", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(chat, dataStore.Data.Chats);
        }
        finally
        {
            reservation?.Dispose();
            main?.Dispose();
        }
    });

    [Fact]
    public Task CreatingUnassignedChatIgnoresTheDesktopProjectFilter() => RunAsync(async () =>
    {
        var project = new Project { Name = "Lumi" };
        var store = new DataStore(new AppData { Settings = TestSettings(), Projects = [project] });
        using var main = new MainViewModel(
            store, TestCopilot.Shared, new UpdateService(), initializeCopilotOnStartup: false);
        main.SelectProjectFilterCommand.Execute(project);
        string? projectAtSend = null;
        var started = false;
        var router = new RemoteCommandRouter(store, main, (owner, _, _, _, _, _) =>
        {
            started = true;
            projectAtSend = owner.GetCurrentProjectName();
            return Task.FromResult<string?>(null);
        });

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                .With("newChat", "true")
                .With("message", "Start without a project"),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.True(started);
        Assert.Null(Assert.Single(store.Data.Chats).ProjectId);
        Assert.Null(projectAtSend);
        Assert.Equal(project.Id, main.ChatVM.ActiveProjectFilterId);
    });

    [Fact]
    public Task CreatingSendCanCreateAWorktreeBeforeTheTurnStarts() => RunAsync(async () =>
    {
        var repo = CreateTempGitRepo();
        MainViewModel? main = null;
        string? worktreePath = null;
        try
        {
            var project = new Project { Name = "Code", WorkingDirectory = repo };
            var dataStore = new DataStore(new AppData
            {
                Settings = TestSettings(),
                Projects = [project]
            });
            main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            var router = new RemoteCommandRouter(
                dataStore,
                main,
                (_, chat, _, _, _, _) =>
                {
                    worktreePath = chat.WorktreePath;
                    return Task.FromResult<string?>(null);
                });

            var result = await router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                    .With("newChat", "true")
                    .With("message", "fix the build")
                    .With("projectId", project.Id.ToString())
                    .With("worktree", "true"),
                CancellationToken.None);

            Assert.True(result.Ok, result.Error);
            Assert.NotNull(worktreePath);
            Assert.True(Directory.Exists(worktreePath));
            Assert.Equal(worktreePath, Assert.Single(dataStore.Data.Chats).WorktreePath);
        }
        finally
        {
            if (main is not null)
                main.Dispose();
            if (worktreePath is not null)
                await GitService.RemoveWorktreeAsync(repo, worktreePath);
            TryDeleteDirectory(repo);
        }
    });

    [Fact]
    public Task RetryingAnEmptyCreatedChatCanStillCreateItsRequestedWorktree() => RunAsync(async () =>
    {
        var repo = CreateTempGitRepo();
        MainViewModel? main = null;
        string? worktreePath = null;
        try
        {
            var project = new Project { Name = "Code", WorkingDirectory = repo };
            var chat = new Chat { Title = "Retry", ProjectId = project.Id };
            var dataStore = new DataStore(new AppData
            {
                Settings = TestSettings(),
                Projects = [project],
                Chats = [chat]
            });
            main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            await main.OpenChatByIdAsync(chat.Id);
            var router = new RemoteCommandRouter(
                dataStore,
                main,
                (_, targetChat, _, _, _, _) =>
                {
                    worktreePath = targetChat.WorktreePath;
                    return Task.FromResult<string?>(null);
                });

            var result = await router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                    .With("chatId", chat.Id.ToString())
                    .With("message", "retry")
                    .With("worktree", "true"),
                CancellationToken.None);

            Assert.True(result.Ok, result.Error);
            Assert.NotNull(worktreePath);
            Assert.True(Directory.Exists(worktreePath));
            Assert.Equal(worktreePath, chat.WorktreePath);
        }
        finally
        {
            if (main is not null)
                main.Dispose();
            if (worktreePath is not null)
                await GitService.RemoveWorktreeAsync(repo, worktreePath);
            TryDeleteDirectory(repo);
        }
    });

    [Fact]
    public Task SelectingLocalForAnEmptyChatRemovesItsExistingWorktree() => RunAsync(async () =>
    {
        var repo = CreateTempGitRepo();
        MainViewModel? main = null;
        string? worktreePath = null;
        try
        {
            worktreePath = await GitService.CreateWorktreeAsync(repo, "lumi/test-local");
            Assert.NotNull(worktreePath);
            var project = new Project { Name = "Code", WorkingDirectory = repo };
            var chat = new Chat
            {
                Title = "Empty",
                ProjectId = project.Id,
                WorktreePath = worktreePath
            };
            var dataStore = new DataStore(new AppData
            {
                Settings = TestSettings(),
                Projects = [project],
                Chats = [chat]
            });
            main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            await main.OpenChatByIdAsync(chat.Id);
            var router = new RemoteCommandRouter(
                dataStore,
                main,
                (_, _, _, _, _, _) => Task.FromResult<string?>(null));

            var result = await router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                    .With("chatId", chat.Id.ToString())
                    .With("message", "run locally")
                    .With("worktree", "false"),
                CancellationToken.None);

            Assert.True(result.Ok, result.Error);
            Assert.Null(chat.WorktreePath);
            Assert.True(Directory.Exists(worktreePath));
        }
        finally
        {
            if (main is not null)
                main.Dispose();
            if (worktreePath is not null)
                await GitService.RemoveWorktreeAsync(repo, worktreePath);
            TryDeleteDirectory(repo);
        }
    });

    [Fact]
    public Task SelectingLocalClearsAMissingPersistedWorktreeReference() => RunAsync(async () =>
    {
        MainViewModel? main = null;
        var repo = CreateTempGitRepo();
        try
        {
            var project = new Project { Name = "Code", WorkingDirectory = repo };
            var chat = new Chat
            {
                Title = "Empty",
                ProjectId = project.Id,
                WorktreePath = Path.Combine(repo, "missing-worktree")
            };
            var dataStore = new DataStore(new AppData
            {
                Settings = TestSettings(),
                Projects = [project],
                Chats = [chat]
            });
            main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            await main.OpenChatByIdAsync(chat.Id);
            var router = new RemoteCommandRouter(
                dataStore,
                main,
                (_, _, _, _, _, _) => Task.FromResult<string?>(null));

            var result = await router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                    .With("chatId", chat.Id.ToString())
                    .With("message", "run locally")
                    .With("worktree", "false"),
                CancellationToken.None);

            Assert.True(result.Ok, result.Error);
            Assert.Null(chat.WorktreePath);
        }
        finally
        {
            main?.Dispose();
            TryDeleteDirectory(repo);
        }
    });

    [Fact]
    public Task ChangingProjectAndKeepingWorktreeCreatesItInTheNewRepository() => RunAsync(async () =>
    {
        var oldRepo = CreateTempGitRepo();
        var newRepo = CreateTempGitRepo();
        MainViewModel? main = null;
        string? oldWorktree = null;
        string? newWorktree = null;
        try
        {
            oldWorktree = await GitService.CreateWorktreeAsync(oldRepo, "lumi/old-project");
            Assert.NotNull(oldWorktree);
            var oldProject = new Project { Name = "Old", WorkingDirectory = oldRepo };
            var newProject = new Project { Name = "New", WorkingDirectory = newRepo };
            var chat = new Chat
            {
                Title = "Empty",
                ProjectId = oldProject.Id,
                WorktreePath = oldWorktree
            };
            var dataStore = new DataStore(new AppData
            {
                Settings = TestSettings(),
                Projects = [oldProject, newProject],
                Chats = [chat]
            });
            main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            await main.OpenChatByIdAsync(chat.Id);
            var router = new RemoteCommandRouter(
                dataStore,
                main,
                (_, targetChat, _, _, _, _) =>
                {
                    newWorktree = targetChat.WorktreePath;
                    return Task.FromResult<string?>(null);
                });

            var result = await router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                    .With("chatId", chat.Id.ToString())
                    .With("message", "switch project")
                    .With("projectId", newProject.Id.ToString())
                    .With("worktree", "true"),
                CancellationToken.None);

            Assert.True(result.Ok, result.Error);
            Assert.Equal(newProject.Id, chat.ProjectId);
            Assert.NotNull(newWorktree);
            Assert.NotEqual(oldWorktree, newWorktree);
            Assert.True(Directory.Exists(oldWorktree));
            Assert.True(Directory.Exists(newWorktree));
        }
        finally
        {
            main?.Dispose();
            if (oldWorktree is not null)
                await GitService.RemoveWorktreeAsync(oldRepo, oldWorktree);
            if (newWorktree is not null)
                await GitService.RemoveWorktreeAsync(newRepo, newWorktree);
            TryDeleteDirectory(oldRepo);
            TryDeleteDirectory(newRepo);
        }
    });

    [Fact]
    public Task InvalidProjectDefaultDoesNotBreakTheFirstSend() => RunAsync(async () =>
    {
        MainViewModel? main = null;
        var directory = Path.Combine(Path.GetTempPath(), $"lumi-not-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var project = new Project
            {
                Name = "Not Git",
                WorkingDirectory = directory,
                DefaultNewChatsUseWorktree = true
            };
            var dataStore = new DataStore(new AppData
            {
                Settings = TestSettings(),
                Projects = [project]
            });
            main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            var router = new RemoteCommandRouter(
                dataStore,
                main,
                (_, _, _, _, _, _) => Task.FromResult<string?>(null));

            var result = await router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                    .With("newChat", "true")
                    .With("message", "hello")
                    .With("projectId", project.Id.ToString()),
                CancellationToken.None);

            Assert.True(result.Ok, result.Error);
            Assert.Null(Assert.Single(dataStore.Data.Chats).WorktreePath);
        }
        finally
        {
            main?.Dispose();
            TryDeleteDirectory(directory);
        }
    });

    [Fact]
    public Task SendUsesTheDetachedChatOwnerWithoutChangingTheMainSurface() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        ChatViewModel? startedOn = null;
        var router = new RemoteCommandRouter(
            rig.DataStore,
            rig.Main,
            (owner, _, _, _, _, _) =>
            {
                startedOn = owner;
                return Task.FromResult<string?>(null);
            });

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                .With("chatId", rig.DetachedChat.Id.ToString())
                .With("message", "send from phone"),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Same(rig.DetachedSurface, startedOn);
        Assert.Same(rig.MainChat, rig.Main.ChatVM.CurrentChat);
    });

    [Fact]
    public Task SendWithAnUnknownExplicitChatIdNeverFallsBackToTheMainSurface() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        var started = false;
        var router = new RemoteCommandRouter(
            rig.DataStore,
            rig.Main,
            (_, _, _, _, _, _) =>
            {
                started = true;
                return Task.FromResult<string?>(null);
            });

        var chatCount = rig.DataStore.Data.Chats.Count;

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                .With("chatId", Guid.NewGuid().ToString())
                .With("message", "must not be redirected"),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(started);
        Assert.Equal(chatCount, rig.DataStore.Data.Chats.Count);
        Assert.DoesNotContain(rig.MainChat.Messages, message => message.Content == "must not be redirected");
        Assert.Same(rig.MainChat, rig.Main.ChatVM.CurrentChat);
    });

    [Fact]
    public Task ReadyDetachedSessionWithBackgroundWorkAcceptsAnOrdinarySend() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        var runtime = MarkBackgroundActive(rig.DetachedSurface, rig.DetachedChat);
        ChatViewModel? startedOn = null;
        var router = new RemoteCommandRouter(
            rig.DataStore,
            rig.Main,
            (owner, _, _, _, _, _) =>
            {
                startedOn = owner;
                Assert.True(runtime.IsSessionActive);
                Assert.True(runtime.HasPendingBackgroundWork);
                return Task.FromResult<string?>(null);
            });

        Assert.True(rig.DetachedSurface.IsChatBusy(rig.DetachedChat.Id));
        Assert.False(rig.DetachedSurface.IsAssistantBusy(rig.DetachedChat.Id));
        var summary = RemoteProjector.BuildChat(rig.DataStore, rig.DetachedChat, rig.Main);
        Assert.False(summary.IsRunning);
        Assert.True(summary.IsSessionActive);

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                .With("chatId", rig.DetachedChat.Id.ToString())
                .With("message", "Continue without stopping the server"),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Same(rig.DetachedSurface, startedOn);
        Assert.True(runtime.IsSessionActive);
        Assert.True(runtime.HasPendingBackgroundWork);
        Assert.Same(rig.MainChat, rig.Main.ChatVM.CurrentChat);
    });

    [Fact]
    public Task ReapplyingTheSameProjectDoesNotInvalidateAReadyBackgroundSession() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync(includeProject: true);
        rig.DetachedChat.ProjectId = rig.ProjectId;
        var runtime = MarkBackgroundActive(rig.DetachedSurface, rig.DetachedChat);
        var pending = GetPrivateField<HashSet<Guid>>(rig.DetachedSurface, "_pendingSessionReconfigurations");
        var router = new RemoteCommandRouter(
            rig.DataStore,
            rig.Main,
            (_, _, _, _, _, _) =>
            {
                Assert.DoesNotContain(rig.DetachedChat.Id, pending);
                return Task.FromResult<string?>(null);
            });

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                .With("chatId", rig.DetachedChat.Id.ToString())
                .With("projectId", rig.ProjectId.ToString())
                .With("message", "Continue with the same setup"),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.DoesNotContain(rig.DetachedChat.Id, pending);
        Assert.True(runtime.HasPendingBackgroundWork);
    });

    [Fact]
    public Task PendingSameSessionRefreshDoesNotBlockMobileSendAdmission() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        var runtime = MarkBackgroundActive(rig.DetachedSurface, rig.DetachedChat);
        GetPrivateField<HashSet<Guid>>(rig.DetachedSurface, "_pendingSessionReconfigurations")
            .Add(rig.DetachedChat.Id);
        rig.DetachedSurface.AddAttachment(@"C:\desktop-draft.txt");
        var started = false;
        var router = new RemoteCommandRouter(rig.DataStore, rig.Main, (owner, chat, _, _, _, _) =>
        {
            Assert.Same(rig.DetachedSurface, owner);
            Assert.Same(rig.DetachedChat, chat);
            started = true;
            return Task.FromResult<string?>(null);
        });
        var command = new RemoteCommand(RemoteProtocol.Actions.SendMessage)
            .With("chatId", rig.DetachedChat.Id.ToString())
            .With("message", "Send when this session is ready");
        command.AuthenticatedDeviceId = "phone";
        command.RequestId = "pending-refresh-request";

        var result = await router.ExecuteAsync(command, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.True(started);
        Assert.Equal([@"C:\desktop-draft.txt"], rig.DetachedSurface.PendingAttachments);
        Assert.True(runtime.HasPendingBackgroundWork);
        Assert.False(runtime.IsBusy);
        Assert.Empty(GetPrivateField<Dictionary<Guid, List<ChatMessage>>>(
            rig.DetachedSurface, "_queuedBusySendPrompts"));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DeferredExternalProviderOverrideStaysSelectedForTheQueuedTurn(bool automaticSteering) => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        var endpoint = new ByokEndpoint
        {
            Name = "Private fixture provider",
            BaseUrl = "https://private.example.test/v1",
            ProviderType = "openai",
            WireApi = "completions",
            ApiKeyMode = ByokApiKeyMode.None,
            IsEnabled = true
        };
        var model = new ByokModel
        {
            Id = "private-fixture",
            EndpointId = endpoint.Id,
            ModelId = "private-model",
            DisplayName = "Private model",
            IsEnabled = true
        };
        rig.DataStore.Data.Settings.ByokEndpoints.Add(endpoint);
        rig.DataStore.Data.Settings.ByokModels.Add(model);
        var runtime = MarkBackgroundActive(rig.DetachedSurface, rig.DetachedChat);
        rig.DetachedChat.CopilotSessionId = "public-provider-session";
        rig.DetachedChat.SessionProviderSignature = null;
        if (!automaticSteering)
            GetPrivateField<HashSet<Guid>>(rig.DetachedSurface, "_pendingSessionReconfigurations")
                .Add(rig.DetachedChat.Id);
        if (automaticSteering)
        {
            var session = (CopilotSession)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(CopilotSession));
            typeof(CopilotSession).GetField("<SessionId>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, rig.DetachedChat.CopilotSessionId);
            GC.SuppressFinalize(session);
            GetPrivateField<Dictionary<Guid, CopilotSession>>(rig.DetachedSurface, "_sessionCache")
                [rig.DetachedChat.Id] = session;
        }
        var privateSelection = ByokConfigHelper.BuildModelToken(model);
        Assert.NotEqual(privateSelection, rig.DetachedSurface.SelectedModel);

        var result = await rig.DetachedSurface.StartExternalMessageAsync(
            rig.DetachedChat,
            "Confidential fixture message",
            "Manager fixture",
            modelOverride: privateSelection,
            remoteDeviceId: "fixture",
            remoteRequestId: "private-queued-request");

        Assert.True(result.Accepted, result.Error);
        Assert.Equal(privateSelection, rig.DetachedSurface.SelectedModel);
        Assert.Equal(privateSelection, rig.DetachedSurface.ResolveSelectedModelForChat(rig.DetachedChat));
        Assert.Equal(privateSelection, rig.DetachedChat.LastModelUsed);
        Assert.Equal("public-provider-session", rig.DetachedChat.CopilotSessionId);
        Assert.Null(rig.DetachedChat.SessionProviderSignature);
        Assert.True(runtime.HasPendingBackgroundWork);
        var queued = Assert.Single(GetPrivateField<Dictionary<Guid, List<ChatMessage>>>(
            rig.DetachedSurface, "_queuedBusySendPrompts")[rig.DetachedChat.Id]);
        Assert.Equal(MessageSteerState.Queued, queued.SteerDelivery);
        Assert.Equal("private-queued-request", queued.RemoteRequestId);
        rig.DetachedSurface.SelectedModel = "initial-model";
        Assert.NotEqual(privateSelection, rig.DetachedSurface.SelectedModel);
        var redelivery = Assert.IsAssignableFrom<Task>(typeof(ChatViewModel)
            .GetMethod(automaticSteering ? "FlushQueuedBusySendsAsSteerAsync" : "SendMessageCore",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(rig.DetachedSurface, automaticSteering
                ? [rig.DetachedChat.Id]
                : [queued.Content, false, queued]));
        await redelivery;
        Assert.Equal(privateSelection, rig.DetachedSurface.SelectedModel);
        Assert.Equal(privateSelection, rig.DetachedSurface.ResolveSelectedModelForChat(rig.DetachedChat));
        Assert.Equal(privateSelection, queued.Model);
        Assert.Equal(MessageSteerState.Queued, queued.SteerDelivery);
        Assert.True(runtime.HasPendingBackgroundWork);
    });

    [Fact]
    public Task MobileChatSetupKeepsGlobalModelDefaultsUnchanged() => RunAsync(async () =>
    {
        var settings = TestSettings();
        settings.PreferredModel = "gpt-5.4";
        settings.ReasoningEffort = "high";
        settings.ContextWindowTier = ModelContextWindowTiers.Default;
        var store = new DataStore(new AppData { Settings = settings });
        using var main = new MainViewModel(store, TestCopilot.Shared, new UpdateService(), initializeCopilotOnStartup: false);
        main.ChatVM.UpdateModelCapabilities(
            [new ModelInfo { Id = "claude-opus-5", SupportedReasoningEfforts = ["low", "high", "max"] }],
            new HashSet<string> { "claude-opus-5" },
            merge: true);
        var router = new RemoteCommandRouter(store, main, (owner, chat, _, _, _, _) =>
        {
            Assert.Equal("claude-opus-5", owner.SelectedModel);
            Assert.Equal("Max", owner.SelectedQuality);
            Assert.Equal(Loc.ContextWindow_Long, owner.SelectedContextWindowTier);
            Assert.Equal("max", chat.LastReasoningEffortUsed);
            Assert.Equal(ModelContextWindowTiers.LongContext, chat.LastContextWindowTierUsed);
            return Task.FromResult<string?>(null);
        });
        var result = await router.ExecuteAsync(new RemoteCommand(RemoteProtocol.Actions.SendMessage)
            .With("newChat", "true")
            .With("message", "Start with this setup")
            .With("model", "claude-opus-5")
            .With("quality", "Max")
            .With("contextWindowTier", Loc.ContextWindow_Long)
            .With("useChatSetup", "true"), CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        Assert.Equal("gpt-5.4", settings.PreferredModel);
        Assert.Equal("high", settings.ReasoningEffort);
        Assert.Equal(ModelContextWindowTiers.Default, settings.ContextWindowTier);
    });

    [Fact]
    public Task StopAndSendStillStopsWhenTheAssistantHasAlreadyBecomeReady() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        var runtime = MarkBackgroundActive(rig.DetachedSurface, rig.DetachedChat);
        var ordinarySendStarted = false;
        var router = new RemoteCommandRouter(
            rig.DataStore,
            rig.Main,
            (_, _, _, _, _, _) =>
            {
                ordinarySendStarted = true;
                return Task.FromResult<string?>(null);
            });

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                .With("chatId", rig.DetachedChat.Id.ToString())
                .With("message", "Stop that work and answer this")
                .With("stopAndSend", "true"),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.False(ordinarySendStarted);
        Assert.False(runtime.IsSessionActive);
        Assert.Contains(rig.DetachedChat.Messages, message => message.Content == "Stop that work and answer this");
    });

    [Fact]
    public Task StopStopsAReadyDetachedBackgroundSession() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        var runtime = MarkBackgroundActive(rig.DetachedSurface, rig.DetachedChat);
        var router = new RemoteCommandRouter(rig.DataStore, rig.Main);

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.StopGeneration)
                .With("chatId", rig.DetachedChat.Id.ToString()),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.NotEqual("That chat is already stopped.", result.Message);
        Assert.False(runtime.IsSessionActive);
        Assert.False(rig.DetachedSurface.IsSessionActive);
        Assert.False(rig.DetachedSurface.IsChatBusy(rig.DetachedChat.Id));
        Assert.Same(rig.MainChat, rig.Main.ChatVM.CurrentChat);
    });

    [Theory]
    [InlineData(RemoteProtocol.Actions.DeleteChat)]
    [InlineData(RemoteProtocol.Actions.RenameChat)]
    [InlineData(RemoteProtocol.Actions.PinChat)]
    public Task MutatingChatActionsRequireAnExplicitChatId(string action) => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        var router = new RemoteCommandRouter(rig.DataStore, rig.Main);
        var originalTitle = rig.MainChat.Title;
        var originalPinned = rig.MainChat.IsPinned;

        var result = await router.ExecuteAsync(
            new RemoteCommand(action)
                .With("title", "must not apply")
                .With("pinned", (!originalPinned).ToString()),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("chatId is required.", result.Error);
        Assert.Contains(rig.MainChat, rig.DataStore.Data.Chats);
        Assert.Equal(originalTitle, rig.MainChat.Title);
        Assert.Equal(originalPinned, rig.MainChat.IsPinned);
        Assert.Same(rig.MainChat, rig.Main.ChatVM.CurrentChat);
    });

    [Fact]
    public Task LegacyOpenChatValidatesWithoutActivatingTheDesktopSurface() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        var router = new RemoteCommandRouter(rig.DataStore, rig.Main);

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.OpenChat)
                .With("chatId", rig.DetachedChat.Id.ToString()),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Same(rig.MainChat, rig.Main.ChatVM.CurrentChat);
        Assert.NotSame(rig.DetachedChat, rig.Main.ChatVM.CurrentChat);
    });

    [Fact]
    public Task SteerUsesTheDetachedRuntimeWithoutChangingTheMainSurface() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        MarkBusy(rig.DetachedSurface, rig.DetachedChat);
        var router = new RemoteCommandRouter(
            rig.DataStore,
            rig.Main,
            (_, _, _, _, _, _) => throw new InvalidOperationException("A busy send must steer."));

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                .With("chatId", rig.DetachedChat.Id.ToString())
                .With("message", "change direction")
                .With("steer", "true"),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Contains(
            rig.DetachedChat.Messages,
            message => message.Content == "change direction" && message.Author == "Lumi Mobile");
        Assert.DoesNotContain(rig.MainChat.Messages, message => message.Content == "change direction");
        Assert.Same(rig.MainChat, rig.Main.ChatVM.CurrentChat);
    });

    [Fact]
    public Task StopAndSendUsesTheDetachedRuntimeWithoutChangingTheMainSurface() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        var runtime = MarkBusy(rig.DetachedSurface, rig.DetachedChat);
        var router = new RemoteCommandRouter(rig.DataStore, rig.Main);

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.SendMessage)
            {
                AuthenticatedDeviceId = "phone-stop-send",
                RequestId = "request-stop-send"
            }
                .With("chatId", rig.DetachedChat.Id.ToString())
                .With("message", "replace the running turn")
                .With("stopAndSend", "true"),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Contains(
            rig.DetachedChat.Messages,
            message => message.Content == "replace the running turn" && message.Author == "Lumi Mobile");
        Assert.True(runtime.SendQueuedNowWhenTurnStarts);
        Assert.True(runtime.IsBusy);
        Assert.Equal("phone-stop-send", rig.DetachedChat.LastRemoteDeviceId);
        Assert.Equal("request-stop-send", rig.DetachedChat.LastRemoteRequestId);
        Assert.DoesNotContain(rig.MainChat.Messages, message => message.Content == "replace the running turn");
        Assert.Same(rig.MainChat, rig.Main.ChatVM.CurrentChat);
    });

    [Fact]
    public Task StopUsesTheDetachedRuntimeWithoutChangingTheMainSurface() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        var runtime = MarkBusy(rig.DetachedSurface, rig.DetachedChat);
        rig.DetachedSurface.IsBusy = true;
        rig.DetachedSurface.IsStreaming = true;
        var router = new RemoteCommandRouter(rig.DataStore, rig.Main);

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.StopGeneration)
                .With("chatId", rig.DetachedChat.Id.ToString()),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.False(runtime.IsBusy);
        Assert.False(rig.DetachedSurface.IsBusy);
        Assert.False(rig.DetachedSurface.IsStreaming);
        Assert.Same(rig.MainChat, rig.Main.ChatVM.CurrentChat);
    });

    [Fact]
    public Task ConfigureUsesTheDetachedSurfaceWithoutChangingTheMainSurface() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync(includeProject: true);
        rig.Main.ChatVM.SelectedModel = "main-model";
        var router = new RemoteCommandRouter(rig.DataStore, rig.Main);

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.ConfigureChat)
                .With("chatId", rig.DetachedChat.Id.ToString())
                .With("model", "detached-model")
                .With("project", DetachedRig.ProjectName),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("detached-model", rig.DetachedChat.LastModelUsed);
        Assert.Equal(rig.ProjectId, rig.DetachedChat.ProjectId);
        Assert.Equal("main-model", rig.MainChat.LastModelUsed);
        Assert.Same(rig.MainChat, rig.Main.ChatVM.CurrentChat);
    });

    [Fact]
    public Task SendNeverAppliesConfigurationToALiveOwnerDisplayingAnotherChat() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        MarkBusy(rig.DetachedSurface, rig.DetachedChat);
        rig.DetachedSurface.CurrentChat = rig.MainChat;
        var router = new RemoteCommandRouter(rig.DataStore, rig.Main);

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                .With("chatId", rig.DetachedChat.Id.ToString())
                .With("message", "must not mutate the displayed chat")
                .With("model", "phone-model"),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("initial-model", rig.MainChat.LastModelUsed);
        Assert.Equal("initial-model", rig.DetachedChat.LastModelUsed);
    });

    [Fact]
    public Task UnknownProjectDoesNotClearAnExistingChatOrCreateANewChat() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync(includeProject: true);
        rig.DetachedChat.ProjectId = rig.ProjectId;
        var router = new RemoteCommandRouter(rig.DataStore, rig.Main);
        var chatCount = rig.DataStore.Data.Chats.Count;

        var configure = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.ConfigureChat)
                .With("chatId", rig.DetachedChat.Id.ToString())
                .With("project", "Deleted project"),
            CancellationToken.None);
        var send = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                .With("newChat", "true")
                .With("message", "do not create")
                .With("project", "Deleted project"),
            CancellationToken.None);

        Assert.False(configure.Ok);
        Assert.False(send.Ok);
        Assert.Equal(rig.ProjectId, rig.DetachedChat.ProjectId);
        Assert.Equal(chatCount, rig.DataStore.Data.Chats.Count);
    });

    [Fact]
    public Task ProjectFeatureRefreshReconfiguresOnlyAffectedBusyChats() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync(includeProject: true);
        rig.DetachedChat.ProjectId = rig.ProjectId;
        MarkBusy(rig.Main.ChatVM, rig.MainChat);
        MarkBusy(rig.DetachedSurface, rig.DetachedChat);

        await rig.Main.ApplyFeatureChangeAsync(
            new FeatureChangeResult("Project updated.", DataChanged: true),
            RemoteProtocol.Resources.Projects,
            new HashSet<Guid> { rig.DetachedChat.Id });

        var mainPending = GetPrivateField<HashSet<Guid>>(rig.Main.ChatVM, "_pendingSessionReconfigurations");
        var detachedPending = GetPrivateField<HashSet<Guid>>(rig.DetachedSurface, "_pendingSessionReconfigurations");
        Assert.DoesNotContain(rig.MainChat.Id, mainPending);
        Assert.Contains(rig.DetachedChat.Id, detachedPending);
        Assert.DoesNotContain(
            rig.DetachedChat.Id,
            GetPrivateField<HashSet<Guid>>(rig.DetachedSurface, "_pendingSessionInvalidations"));
    });

    [Fact]
    public Task McpFeatureRefreshPreservesExplicitPerChatSelection() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        rig.DataStore.Data.McpServers.AddRange(
        [
            new McpServer { Name = "Selected MCP", IsEnabled = true },
            new McpServer { Name = "Excluded MCP", IsEnabled = true }
        ]);
        rig.DetachedChat.ActiveMcpServerNames = ["Selected MCP"];
        rig.DetachedChat.HasExplicitMcpServerSelection = true;
        rig.DetachedSurface.ActiveMcpServerNames.Clear();
        rig.DetachedSurface.ActiveMcpServerNames.Add("Selected MCP");

        await rig.Main.ApplyFeatureChangeAsync(
            new FeatureChangeResult("MCP updated.", DataChanged: true),
            RemoteProtocol.Resources.Mcps);

        Assert.Equal(["Selected MCP"], rig.DetachedChat.ActiveMcpServerNames);
        Assert.Equal(["Selected MCP"], rig.DetachedSurface.ActiveMcpServerNames);
    });

    [Fact]
    public Task McpRenameDoesNotAttachTheServerToUnrelatedSurfaces() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        rig.DataStore.Data.McpServers.AddRange(
        [
            new McpServer { Name = "New", IsEnabled = true },
            new McpServer { Name = "Other", IsEnabled = true }
        ]);
        rig.MainChat.ActiveMcpServerNames = ["Other"];
        rig.Main.ChatVM.ActiveMcpServerNames.Clear();
        rig.Main.ChatVM.ActiveMcpServerNames.Add("Other");
        rig.DetachedChat.ActiveMcpServerNames = ["Old"];
        rig.DetachedSurface.ActiveMcpServerNames.Clear();
        rig.DetachedSurface.ActiveMcpServerNames.Add("Old");

        await rig.Main.ApplyFeatureChangeAsync(
            new FeatureChangeResult(
                "MCP renamed.",
                DataChanged: true,
                RenamedMcpOldName: "Old",
                RenamedMcpNewName: "New"),
            RemoteProtocol.Resources.Mcps);

        Assert.Equal(["Other"], rig.Main.ChatVM.ActiveMcpServerNames);
        Assert.Equal(["Other"], rig.MainChat.ActiveMcpServerNames);
        Assert.Equal(["New"], rig.DetachedSurface.ActiveMcpServerNames);
        Assert.Equal(["New"], rig.DetachedChat.ActiveMcpServerNames);
    });

    [Fact]
    public Task AnswerQuestionUsesTheDetachedPendingQuestionOwner() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        GetPrivateField<Dictionary<string, TaskCompletionSource<string>>>(
            rig.DetachedSurface,
            "_pendingQuestions").Add("question-1", answer);
        var router = new RemoteCommandRouter(rig.DataStore, rig.Main);

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.AnswerQuestion)
                .With("chatId", rig.DetachedChat.Id.ToString())
                .With("questionId", "question-1")
                .With("answer", "Detached answer"),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("Detached answer", await answer.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Same(rig.MainChat, rig.Main.ChatVM.CurrentChat);
    });

    [Fact]
    public Task FirstTurnAppliesAllConfigurationBeforeStartingAndReturnsCreatedChatIdOnFailure() =>
        RunAsync(async () =>
        {
            Loc.Load("en");
            const string modelId = "remote-first-turn-model";
            var project = new Project { Name = "Remote project" };
            var skill = new Skill { Name = "Remote skill", Content = "Use the skill." };
            var agent = new LumiAgent { Name = "Remote agent", SystemPrompt = "Be focused." };
            var mcp = new McpServer { Name = "remote-mcp", IsEnabled = true };
            var data = new AppData
            {
                Settings = TestSettings(),
                Projects = [project],
                Skills = [skill],
                Agents = [agent],
                McpServers = [mcp]
            };
            var dataStore = new DataStore(data);
            using var main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            main.ChatVM.UpdateModelCapabilities(
            [
                new ModelInfo
                {
                    Id = modelId,
                    SupportedReasoningEfforts = ["low", "medium", "high"],
                    DefaultReasoningEffort = "medium"
                }
            ],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { modelId });
            main.ChatVM.ApplyAvailableModels([modelId], modelId);

            FirstTurnSnapshot? snapshot = null;
            var router = new RemoteCommandRouter(
                dataStore,
                main,
                (owner, chat, _, _, _, _) =>
                {
                    snapshot = new FirstTurnSnapshot(
                        ReferenceEquals(owner, main.ChatVM),
                        owner.ResolveSelectedModelForChat(chat),
                        owner.ResolvePersistedReasoningEffortForChat(
                            chat,
                            owner.ResolveSelectedModelForChat(chat)),
                        owner.ResolveSelectedContextWindowTierForChat(
                            chat,
                            owner.ResolveSelectedModelForChat(chat)),
                        chat.ProjectId,
                        chat.AgentId,
                        [.. owner.ActiveSkillIds],
                        [.. owner.ActiveMcpServerNames]);
                    return Task.FromResult<string?>("Synthetic post-creation failure.");
                });

            var result = await router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                    .With("newChat", "true")
                    .With("message", "Start configured")
                    .With("model", modelId)
                    .With("quality", "high")
                    .With("contextWindowTier", "Long")
                    .With("agent", agent.Name)
                    .With("project", project.Name)
                    .WithList("addSkills", [skill.Name])
                    .WithList("addMcps", [mcp.Name]),
                CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Equal("Synthetic post-creation failure.", result.Error);
            Assert.NotNull(result.ChatId);
            Assert.Equal(result.ChatId, Assert.Single(data.Chats).Id);
            Assert.NotNull(snapshot);
            Assert.True(snapshot.OnMainOwner);
            Assert.Equal(modelId, snapshot.Model);
            Assert.Equal("high", snapshot.ReasoningEffort);
            Assert.Equal(ModelContextWindowTiers.LongContext, snapshot.ContextWindowTier);
            Assert.Equal(project.Id, snapshot.ProjectId);
            Assert.Equal(agent.Id, snapshot.AgentId);
            Assert.Equal([skill.Id], snapshot.SkillIds);
            Assert.Equal([mcp.Name], snapshot.McpNames);
        });

    [Fact]
    public Task FirstTurnSelectsTheRequestedByokRouteBeforeStarting() => RunAsync(async () =>
    {
        Loc.Load("en");
        var endpoint = new ByokEndpoint
        {
            Id = "remote-endpoint",
            Name = "Remote endpoint",
            BaseUrl = "https://api.example.test/v1",
            ProviderType = "openai",
            ApiKeyMode = ByokApiKeyMode.None,
            IsEnabled = true
        };
        var model = new ByokModel
        {
            Id = "remote-model",
            EndpointId = endpoint.Id,
            ModelId = "provider-model",
            DisplayName = "Remote BYOK",
            IsEnabled = true
        };
        var settings = TestSettings();
        settings.PreferredModel = "gpt-5-mini";
        settings.UseBYOKOnly = true;
        settings.ByokEndpoints = [endpoint];
        settings.ByokModels = [model];
        var data = new AppData
        {
            Settings = settings
        };
        var dataStore = new DataStore(data);
        using var main = new MainViewModel(
            dataStore,
            TestCopilot.Shared,
            new UpdateService(),
            initializeCopilotOnStartup: false);
        var requestedModel = $"byok:{model.Id}";
        string? selectedAtStart = null;
        var router = new RemoteCommandRouter(
            dataStore,
            main,
            (owner, chat, _, _, _, _) =>
            {
                selectedAtStart = owner.ResolveSelectedModelForChat(chat);
                return Task.FromResult<string?>(null);
            });

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                .With("newChat", "true")
                .With("message", "Use my provider")
                .With("model", requestedModel),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(requestedModel, selectedAtStart);
        Assert.Equal(requestedModel, Assert.Single(data.Chats).LastModelUsed);
    });

    private static ChatRuntimeState MarkBusy(ChatViewModel surface, Chat chat)
    {
        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true,
            TurnInProgress = true,
            IsStreaming = true
        };
        GetPrivateField<Dictionary<Guid, ChatRuntimeState>>(surface, "_runtimeStates")[chat.Id] = runtime;
        return runtime;
    }

    private static ChatRuntimeState MarkBackgroundActive(ChatViewModel surface, Chat chat)
    {
        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            IsSessionActive = true,
            HasPendingBackgroundWork = true
        };
        GetPrivateField<Dictionary<Guid, ChatRuntimeState>>(surface, "_runtimeStates")[chat.Id] = runtime;
        surface.IsSessionActive = true;
        return runtime;
    }

    private static UserSettings TestSettings() => new()
    {
        AutoSaveChats = false,
        AutoGenerateTitles = false,
        EnableMemoryAutoSave = false
    };

    /// <summary>A desktop whose new chat has learned one setup from two chats: <paramref name="project"/> on GPT-6 Luna, low effort.</summary>
    private static (DataStore Store, MainViewModel Main) LaunchpadRig(Project project, params Project[] others)
    {
        var settings = TestSettings();
        settings.PreferredModel = "gpt-6-luna";
        settings.ReasoningEffort = "high";
        var data = new AppData { Settings = settings };
        data.Projects.AddRange([project, .. others]);
        data.Chats.AddRange(Enumerable.Range(1, 2).Select(daysAgo => new Chat
        {
            Title = $"{project.Name} work {daysAgo}",
            ProjectId = project.Id,
            LastModelUsed = "gpt-6-luna",
            LastReasoningEffortUsed = "low",
            MessageCount = 6,
            CreatedAt = DateTimeOffset.Now.AddDays(-daysAgo),
            UpdatedAt = DateTimeOffset.Now.AddDays(-daysAgo)
        }));
        var store = new DataStore(data);
        var main = new MainViewModel(store, TestCopilot.Shared, new UpdateService(),
            startBackgroundJobs: false, initializeCopilotOnStartup: false);
        var models = CopilotModelCatalog.BuildModels(CopilotModelCatalogTests.NativeCatalog());
        main.ChatVM.UpdateModelCapabilities(models);
        main.SettingsVM.UpdateModelCapabilities(models);
        main.ChatVM.ApplyAvailableModels(models.Select(model => model.Id).ToArray(), "gpt-6-luna");
        return (store, main);
    }

    /// <summary>A launchpad command naming a setup of the row as the phone does.</summary>
    private static RemoteCommand SetupCommand(string action, RemoteChatSetup setup)
    {
        var command = new RemoteCommand(action).With("worktree", setup.UseWorktree ? "true" : "false");
        if (setup.ProjectId is { } projectId)
            command.With("projectId", projectId.ToString());
        if (setup.AgentId is { } agentId)
            command.With("agentId", agentId.ToString());
        if (setup.Model is { } model)
            command.With("model", model);
        if (setup.Effort is { } effort)
            command.With("effort", effort);
        return command;
    }

    private static (Guid ProjectId, bool UseWorktree, string? Model, string? Effort) PinOf(LaunchpadSetupPin pin)
        => (pin.ProjectId!.Value, pin.UseWorktree, pin.ModelId, pin.Effort);

    private static string CreateTempGitRepo()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"lumi-remote-worktree-{Guid.NewGuid():N}"[..31]);
        Directory.CreateDirectory(directory);
        RunGit(directory, "init -b main");
        RunGit(directory, "config user.email test@lumi.local");
        RunGit(directory, "config user.name \"Lumi Test\"");
        File.WriteAllText(Path.Combine(directory, "README.md"), "seed");
        RunGit(directory, "add -A");
        RunGit(directory, "commit -m seed");
        return directory;
    }

    private static void RunGit(string workingDirectory, string arguments)
    {
        var startInfo = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo)!;
        process.WaitForExit(30_000);
        Assert.Equal(0, process.ExitCode);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
        }
    }

    private static T GetPrivateField<T>(object target, string name) where T : class =>
        Assert.IsType<T>(
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target));

    private static long DirtyChatVersion(DataStore store, Guid chatId)
    {
        var field = typeof(DataStore).GetField(
            "_dirtyChatVersions",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var versions = (System.Collections.IDictionary)field.GetValue(store)!;
        return versions.Contains(chatId) ? Convert.ToInt64(versions[chatId]) : 0L;
    }

    private static async Task RunAsync(Func<Task> body)
    {
        using var session = HeadlessTestSession.Start();
        ExceptionDispatchInfo? failure = null;

        await session.Dispatch(() =>
        {
            try
            {
                var work = body();
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while (!work.IsCompleted)
                {
                    Dispatcher.UIThread.RunJobs();
                    if (DateTime.UtcNow > deadline)
                        throw new TimeoutException("The remote router test did not complete.");
                    Thread.Sleep(1);
                }

                work.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        }, CancellationToken.None);

        failure?.Throw();
    }

    private sealed class DetachedRig : IDisposable
    {
        public const string ProjectName = "Detached project";

        private DetachedRig(
            DataStore dataStore,
            MainViewModel main,
            Chat mainChat,
            Chat detachedChat,
            DetachedChatWindowRequest request,
            Guid? projectId)
        {
            DataStore = dataStore;
            Main = main;
            MainChat = mainChat;
            DetachedChat = detachedChat;
            Request = request;
            ProjectId = projectId;
        }

        public DataStore DataStore { get; }
        public MainViewModel Main { get; }
        public Chat MainChat { get; }
        public Chat DetachedChat { get; }
        public DetachedChatWindowRequest Request { get; }
        public ChatViewModel DetachedSurface => Request.WindowVM.ChatVM;
        public Guid? ProjectId { get; }

        public static async Task<DetachedRig> CreateAsync(bool includeProject = false)
        {
            Loc.Load("en");
            var mainChat = ChatWithMessage("Main chat", "main");
            var detachedChat = ChatWithMessage("Detached chat", "detached");
            var data = new AppData
            {
                Settings = TestSettings(),
                Chats = [mainChat, detachedChat]
            };
            Project? project = null;
            if (includeProject)
            {
                project = new Project { Name = ProjectName };
                data.Projects.Add(project);
            }

            var dataStore = new DataStore(data);
            var main = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                initializeCopilotOnStartup: false);
            main.ChatVM.CurrentChat = mainChat;
            DetachedChatWindowRequest? request = null;
            main.OpenChatWindowRequested += requested => request = requested;
            await main.OpenChatInNewWindowCommand.ExecuteAsync(detachedChat);

            return new DetachedRig(
                dataStore,
                main,
                mainChat,
                detachedChat,
                Assert.IsType<DetachedChatWindowRequest>(request),
                project?.Id);
        }

        public void Dispose()
        {
            Request.WindowVM.Dispose();
            Request.ReleaseSurface();
            Main.Dispose();
        }

        private static Chat ChatWithMessage(string title, string content)
        {
            var chat = new Chat { Title = title, LastModelUsed = "initial-model" };
            chat.Messages.Add(new ChatMessage { Role = "user", Content = content });
            chat.MessageCount = chat.Messages.Count;
            return chat;
        }
    }

    private sealed record FirstTurnSnapshot(
        bool OnMainOwner,
        string? Model,
        string? ReasoningEffort,
        string? ContextWindowTier,
        Guid? ProjectId,
        Guid? AgentId,
        IReadOnlyList<Guid> SkillIds,
        IReadOnlyList<string> McpNames);
}
