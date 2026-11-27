using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

/// <summary>
/// What the composer carries: the chat's project, agent, skills and connectors, shown as the desktop
/// shows them and changed through the + sheet's pickers, which tell the PC as the desktop's menus do.
/// </summary>
public sealed class ComposerContextTests
{
    private static readonly Guid LumiId = Guid.NewGuid();
    private static readonly Guid FluentId = Guid.NewGuid();
    private static readonly Guid PlannerId = Guid.NewGuid();

    private static RemoteLibrary Library() => new()
    {
        Projects =
        [
            new() { Id = LumiId, Name = "Lumi", IsCodingProject = true },
            new() { Id = FluentId, Name = "Fluent Search", IsCodingProject = true }
        ],
        Lumis =
        [
            new() { Id = Guid.NewGuid(), Name = "Coding Lumi", IconGlyph = "⚡", Description = "Writes and tests software" },
            new() { Id = PlannerId, Name = "Daily Planner", IconGlyph = "📋", Description = "Plans your day" }
        ],
        Skills =
        [
            new() { Id = Guid.NewGuid(), Name = "Debug Expert", IconGlyph = "🐞", Description = "Isolate and fix" },
            new() { Id = Guid.NewGuid(), Name = "Web Researcher", IconGlyph = "🔎", Description = "Searches the web" }
        ],
        McpServers =
        [
            new() { Id = Guid.NewGuid(), Name = "GitHub", IsEnabled = true },
            new() { Id = Guid.NewGuid(), Name = "Avalonia diagnostics", IsEnabled = false }
        ]
    };

    private static (MobileChatViewModel Chat, RecordingSink Sink) StartedChat()
    {
        var sink = new RecordingSink();
        var chat = new MobileChatViewModel(sink);
        chat.ApplyLibraryCatalogs(Library());
        var chatId = Guid.NewGuid();
        chat.Reset(chatId, "Started");
        chat.ApplyStatus(new RemoteChatStatus
        {
            ChatId = chatId,
            ProjectId = LumiId,
            ProjectName = "Lumi",
            AgentName = "Coding Lumi",
            AgentGlyph = "⚡",
            SkillNames = ["Debug Expert"],
            McpNames = ["GitHub", "Plugin connector"]
        });
        return (chat, sink);
    }

    [Fact]
    public void AStartedChatShowsItsProjectAgentSkillsAndConnectorsWithoutTellingThePcBack()
    {
        var (chat, sink) = StartedChat();

        Assert.True(chat.HasComposerContext);
        Assert.True(chat.HasProject);
        Assert.True(chat.HasAgent);
        Assert.Equal("Lumi", chat.ProjectSummary);
        Assert.Equal("Coding Lumi", chat.AgentSummary);
        Assert.Equal("Debug Expert", chat.SkillsSummary);
        Assert.Equal("2 connectors", chat.ConnectorsSummary);
        // The PC's own state, applied, is not a change to send back.
        Assert.Empty(sink.Commands);

        chat.Reset(Guid.NewGuid(), "Plain");
        Assert.False(chat.HasComposerContext);
        Assert.Equal("None", chat.AgentSummary);
        Assert.Equal("None", chat.SkillsSummary);

        // Connectors are tools the chat may use, not what a message carries: as on the desktop they stay
        // behind + (its Connectors button), so a chat with only connectors shows no chips.
        chat.ApplyStatus(new RemoteChatStatus
        {
            ChatId = chat.ChatId,
            McpNames = ["Avalonia MCP", "DotSight", "avalonia-mcp", "memory", "lumi-mcp", "workiq"]
        });
        Assert.False(chat.HasComposerContext);
        Assert.Equal("6 connectors", chat.ConnectorsSummary);
    }

    [Fact]
    public void SkillsAndConnectorsSwitchInPlaceAndTellThePc()
    {
        var (chat, sink) = StartedChat();
        var changes = new List<string?>();
        chat.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        chat.OpenAddSheetCommand.Execute(null);
        Assert.True(chat.IsAddSheetOpen);
        Assert.True(chat.HasOpenSheet);
        chat.OpenContextPickerCommand.Execute("Skills");
        Assert.False(chat.IsAddSheetOpen);
        Assert.True(chat.IsContextPickerOpen);
        Assert.Equal("Skills", chat.ContextPickerTitle);
        Assert.Equal(["Debug Expert", "Web Researcher"], chat.ContextOptions.Select(option => option.Name));
        Assert.True(chat.ContextOptions[0].IsSelected);
        Assert.Equal("Isolate and fix", chat.ContextOptions[0].Description);
        Assert.True(chat.ContextOptions[0].UsesGlyph);

        var web = chat.ContextOptions[1];
        Assert.False(chat.ContextOptions[0].IsHighlighted);
        chat.PickContextOptionCommand.Execute(web);
        Assert.True(web.IsSelected);
        Assert.True(chat.IsContextPickerOpen);
        Assert.Contains(chat.SkillChips, chip => chip.Name == "Web Researcher");
        Assert.Equal("2 skills", chat.SkillsSummary);
        Assert.Contains(nameof(MobileChatViewModel.SkillsSummary), changes);
        Assert.Contains(sink.Configurations, command => command.GetList("addSkills")?.Contains("Web Researcher") == true);

        chat.PickContextOptionCommand.Execute(web);
        Assert.False(web.IsSelected);
        Assert.DoesNotContain(chat.SkillChips, chip => chip.Name == "Web Researcher");
        Assert.Contains(sink.Configurations, command => command.GetList("removeSkills")?.Contains("Web Researcher") == true);

        // Connectors list those switched on at the PC, and keep one the chat still uses so it can come off.
        chat.OpenContextPickerCommand.Execute("Connectors");
        Assert.Equal(["Plugin connector", "GitHub"], chat.ContextOptions.Select(option => option.Name));
        Assert.All(chat.ContextOptions, option => Assert.True(option.IsSelected && option.IsConnector));
        chat.PickContextOptionCommand.Execute(chat.ContextOptions[0]);
        Assert.Equal("GitHub", chat.ConnectorsSummary);
        Assert.Contains(sink.Configurations, command => command.GetList("removeMcps")?.Contains("Plugin connector") == true);

        // Back closes the picker before anything under it.
        Assert.True(chat.DismissTopmostSheet());
        Assert.False(chat.IsContextPickerOpen);
        Assert.False(chat.HasOpenSheet);
    }

    [Fact]
    public void AnAgentOrProjectIsOneChoiceThatClosesThePicker()
    {
        var (chat, sink) = StartedChat();

        chat.OpenContextPickerCommand.Execute("Agent");
        Assert.Equal("Agent", chat.ContextPickerTitle);
        var none = chat.ContextOptions[0];
        Assert.True(none.IsNone);
        Assert.Equal("No agent", none.Label);
        Assert.False(none.IsSelected);
        Assert.True(chat.ContextOptions.Single(option => option.Name == "Coding Lumi").IsSelected);

        var planner = chat.ContextOptions.Single(option => option.Name == "Daily Planner");
        Assert.True(chat.ContextOptions.Single(option => option.Name == "Coding Lumi").IsHighlighted);
        chat.PickContextOptionCommand.Execute(planner);
        Assert.False(chat.IsContextPickerOpen);
        Assert.Equal("Daily Planner", chat.AgentName);
        Assert.Equal(PlannerId.ToString(), chat.AgentValue);
        Assert.Equal("📋", chat.AgentGlyph);
        Assert.Contains(sink.Configurations, command => command.Get("agentId") == PlannerId.ToString());

        chat.OpenContextPickerCommand.Execute("Agent");
        Assert.True(chat.ContextOptions.Single(option => option.Name == "Daily Planner").IsSelected);
        chat.PickContextOptionCommand.Execute(chat.ContextOptions[0]);
        Assert.False(chat.HasAgent);
        Assert.Equal("None", chat.AgentSummary);

        chat.OpenContextPickerCommand.Execute("Project");
        Assert.True(chat.CanPickProject);
        Assert.Equal(["", "Lumi", "Fluent Search"], chat.ContextOptions.Select(option => option.Name));
        Assert.True(chat.ContextOptions[1].IsSelected && chat.ContextOptions[1].IsProject);
        chat.PickContextOptionCommand.Execute(chat.ContextOptions[2]);
        Assert.False(chat.IsContextPickerOpen);
        Assert.Equal("Fluent Search", chat.ProjectName);
        Assert.Equal(FluentId.ToString(), chat.ProjectValue);
    }

    [Fact]
    public void TheAddSheetsFilesRowOpensThePickerAndLeavesTheSheet()
    {
        var (chat, _) = StartedChat();
        var picks = 0;
        chat.AttachmentPickRequested += () => picks++;

        chat.OpenAddSheetCommand.Execute(null);
        chat.AttachFromAddSheetCommand.Execute(null);
        Assert.False(chat.IsAddSheetOpen);
        Assert.Equal(1, picks);

        // An empty catalog says where to make one rather than showing a blank sheet.
        var empty = new MobileChatViewModel(new RecordingSink());
        empty.Reset(Guid.NewGuid(), "Empty");
        empty.OpenContextPickerCommand.Execute("Skills");
        Assert.True(empty.IsContextPickerEmpty);
        Assert.Contains("Customize", empty.ContextPickerEmptyText);
        empty.OpenContextPickerCommand.Execute("Agent");
        Assert.True(empty.IsContextPickerEmpty);
        Assert.True(Assert.Single(empty.ContextOptions).IsNone);
    }

    [Fact]
    public async Task StrategyChipsCombineTogglePersistAcrossDraftsAndTravelWithSend()
    {
        var sink = new RecordingSink { SupportsRunOptions = true };
        var chat = new MobileChatViewModel(sink);
        chat.ApplyCatalogs(new RemoteSettings
        {
            PreferredModel = "gpt-6-luna",
            AvailableModels = ["gpt-6-luna", "byok:openai:gpt-5"],
            ModelOptions =
            [
                new RemoteModelOption { Model = "gpt-6-luna", DisplayName = "GPT 6 Luna" },
                new RemoteModelOption { Model = "byok:openai:gpt-5", DisplayName = "GPT-5", Account = "OpenAI" }
            ]
        });

        var chatId = Guid.NewGuid();
        chat.Reset(chatId, "Work");
        Assert.True(chat.SupportsRunOptions);
        Assert.True(chat.CanSteerWhileBusy);
        Assert.Equal("None", chat.StrategySummary);

        // Open Strategy picker: Autopilot and Parallel combine; Research is exclusive.
        chat.OpenContextPickerCommand.Execute("Strategy");
        Assert.True(chat.IsContextPickerOpen);
        Assert.Equal("Strategy", chat.ContextPickerTitle);
        Assert.Equal(
            [RemoteProtocol.RunOptions.Autopilot, RemoteProtocol.RunOptions.Parallel, RemoteProtocol.RunOptions.Research],
            chat.ContextOptions.Select(option => option.Name));

        chat.PickContextOptionCommand.Execute(chat.ContextOptions[0]);
        chat.PickContextOptionCommand.Execute(chat.ContextOptions[1]);
        Assert.Equal([RemoteProtocol.RunOptions.Autopilot, RemoteProtocol.RunOptions.Parallel], chat.RunOptions.Select(b => b.Id));
        Assert.Equal("Autopilot, Parallel", chat.StrategySummary);
        Assert.True(chat.HasComposerContext);
        Assert.False(chat.CanSteerWhileBusy);
        Assert.Contains(sink.Configurations, c => c.Get("runOptions") == "Autopilot\nParallel");

        // Selecting Research replaces Autopilot + Parallel.
        chat.PickContextOptionCommand.Execute(chat.ContextOptions[2]);
        Assert.Equal([RemoteProtocol.RunOptions.Research], chat.RunOptions.Select(b => b.Id));
        Assert.Equal("Research", chat.StrategySummary);

        // Removing a chip directly clears it and notifies the PC.
        chat.RemoveRunOptionCommand.Execute(RemoteProtocol.RunOptions.Research);
        Assert.Empty(chat.RunOptions);
        Assert.True(chat.CanSteerWhileBusy);
        Assert.Contains(sink.Configurations, c => c.Get("runOptions") == "");

        // Re-select Autopilot + Parallel, then send: optimistic echo carries them, composer clears,
        // and send_message carries runOptions.
        chat.ToggleRunOptionCommand.Execute(RemoteProtocol.RunOptions.Autopilot);
        chat.ToggleRunOptionCommand.Execute(RemoteProtocol.RunOptions.Parallel);
        chat.PromptText = "Audit the workspace";
        await chat.SendCommand.ExecuteAsync(null);

        Assert.Empty(chat.RunOptions);
        var sent = Assert.Single(sink.Commands, c => c.Action == RemoteProtocol.Actions.SendMessage);
        Assert.Equal([RemoteProtocol.RunOptions.Autopilot, RemoteProtocol.RunOptions.Parallel], sent.GetList("runOptions") ?? []);
        var echo = Assert.IsType<UserTurnItemViewModel>(Assert.Single(Assert.Single(chat.Turns).Items));
        Assert.True(echo.HasRunOptions);
        Assert.Equal([RemoteProtocol.RunOptions.Autopilot, RemoteProtocol.RunOptions.Parallel], echo.RunOptions.Select(b => b.Id));
    }

    [Fact]
    public async Task FailedSendRestoresStrategyChipsAndByokGatingDisablesResearch()
    {
        var sink = new RecordingSink { SupportsRunOptions = true, NextSendResult = new RemoteCommandResult { Ok = false, Error = "Offline" } };
        var chat = new MobileChatViewModel(sink);
        chat.ApplyCatalogs(new RemoteSettings
        {
            PreferredModel = "gpt-6-luna",
            AvailableModels = ["gpt-6-luna", "byok:openai:gpt-5"],
            ModelOptions =
            [
                new RemoteModelOption { Model = "gpt-6-luna", DisplayName = "GPT 6 Luna" },
                new RemoteModelOption { Model = "byok:openai:gpt-5", DisplayName = "GPT-5", Account = "OpenAI" }
            ]
        });

        // Blank New Chat keeps draft Strategy across switches and restores on send failure.
        chat.Reset(Guid.Empty, "New chat");
        chat.ToggleRunOptionCommand.Execute(RemoteProtocol.RunOptions.Autopilot);
        chat.PromptText = "Finish the report";
        await chat.SendCommand.ExecuteAsync(null);
        Assert.Equal([RemoteProtocol.RunOptions.Autopilot], chat.RunOptions.Select(b => b.Id));
        Assert.Equal("Finish the report", chat.PromptText);

        // Switching to BYOK model disables Research in the Strategy picker.
        chat.Model = "byok:openai:gpt-5";
        Assert.False(chat.CanUseResearch);
        chat.OpenContextPickerCommand.Execute("Strategy");
        var research = chat.ContextOptions.Single(o => o.Name == RemoteProtocol.RunOptions.Research);
        Assert.False(research.IsEnabled);
        chat.PickContextOptionCommand.Execute(research);
        Assert.DoesNotContain(chat.RunOptions, b => b.IsResearch);

        // A host without RunOptionsV1 hides Strategy and ignores opening its picker.
        var legacy = new MobileChatViewModel(new RecordingSink { SupportsRunOptions = false });
        legacy.Reset(Guid.NewGuid(), "Legacy");
        Assert.False(legacy.SupportsRunOptions);
        legacy.OpenContextPickerCommand.Execute("Strategy");
        Assert.False(legacy.IsContextPickerOpen);
    }

    [Fact]
    public async Task BusyStrategySendUsesStopAndSendAndIgnoresStaleStatusWhileInFlight()
    {
        var sendGate = new TaskCompletionSource<RemoteCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingSink
        {
            SupportsRunOptions = true,
            SendOverride = cmd => cmd.Action == RemoteProtocol.Actions.SendMessage ? sendGate.Task : null
        };
        var chat = new MobileChatViewModel(sink);
        var chatId = Guid.NewGuid();
        chat.Reset(chatId, "Busy chat");
        chat.ApplyStatus(new RemoteChatStatus
        {
            ChatId = chatId,
            IsBusy = true,
            RunOptions = [RemoteProtocol.RunOptions.Autopilot]
        });

        Assert.False(chat.CanSteerWhileBusy);
        chat.PromptText = "Take over cleanly";
        var sending = chat.SendCommand.ExecuteAsync(null);

        // A stale SSE status frame arriving while send_message is in flight must not restore the cleared chip.
        chat.ApplyStatus(new RemoteChatStatus
        {
            ChatId = chatId,
            IsBusy = true,
            RunOptions = [RemoteProtocol.RunOptions.Autopilot]
        });
        Assert.Empty(chat.RunOptions);

        sendGate.SetResult(new RemoteCommandResult { Ok = true, ChatId = chatId });
        await sending;

        var sent = Assert.Single(sink.Commands, c => c.Action == RemoteProtocol.Actions.SendMessage);
        Assert.Equal("true", sent.Get("stopAndSend"));
        Assert.Null(sent.Get("steer"));
        Assert.Equal([RemoteProtocol.RunOptions.Autopilot], sent.GetList("runOptions") ?? []);
    }

    [Fact]
    public async Task RejectedResearchConfigurationDoesNotPoisonLaterUpdates()
    {
        var sink = new RecordingSink
        {
            SupportsRunOptions = true,
            SendOverride = cmd => cmd.Action == RemoteProtocol.Actions.ConfigureChat
                                  && cmd.Get("runOptions") == RemoteProtocol.RunOptions.Research
                ? Task.FromResult(new RemoteCommandResult { Ok = false, Error = "Requires GitHub Copilot" })
                : null
        };
        var chat = new MobileChatViewModel(sink);
        chat.ApplyCatalogs(new RemoteSettings { PreferredModel = "gpt-6-luna", AvailableModels = ["gpt-6-luna"] });
        var chatId = Guid.NewGuid();
        chat.Reset(chatId, "Remote");

        chat.ToggleRunOptionCommand.Execute(RemoteProtocol.RunOptions.Research);
        await chat.FlushPendingConfigurationAsync();

        Assert.DoesNotContain(chat.RunOptions, b => b.IsResearch);
        Assert.False(chat.HasPendingConfiguration);
        Assert.Equal("Requires GitHub Copilot", chat.ErrorText);
    }

    [Fact]
    public void BlankStrategyDraftSurvivesNavigationButNotSwitchingPcs()
    {
        var sink = new RecordingSink { SupportsRunOptions = true };
        var chat = new MobileChatViewModel(sink);
        chat.Reset(Guid.Empty, "New chat");
        chat.PromptText = "Finish this";
        chat.ToggleRunOptionCommand.Execute(RemoteProtocol.RunOptions.Autopilot);

        chat.Reset(Guid.NewGuid(), "Another chat");
        Assert.Empty(chat.RunOptions);
        chat.Reset(Guid.Empty, "New chat");

        Assert.Equal("Finish this", chat.PromptText);
        Assert.Equal(RemoteProtocol.RunOptions.Autopilot, Assert.Single(chat.RunOptions).Id);
        Assert.Empty(sink.Commands);

        chat.ResetHostState();
        Assert.Empty(chat.RunOptions);
        Assert.Empty(chat.PromptText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedStrategySendWithLostAcknowledgementDoesNotRearmTheStrategy(bool pickNewStrategy)
    {
        var sink = new RecordingSink
        {
            SupportsRunOptions = true,
            NextSendResult = new RemoteCommandResult { Error = "Connection lost", IsOutcomeUnknown = true }
        };
        var chat = new MobileChatViewModel(sink);
        var chatId = Guid.NewGuid();
        chat.Reset(chatId, "Work");
        chat.ApplyStatus(new RemoteChatStatus { ChatId = chatId, RunOptions = [RemoteProtocol.RunOptions.Autopilot] });
        chat.PromptText = "Finish this request";

        await chat.SendCommand.ExecuteAsync(null);

        Assert.Equal(RemoteProtocol.Actions.SendMessage, Assert.Single(sink.Commands).Action);
        Assert.Equal(RemoteProtocol.RunOptions.Autopilot, Assert.Single(chat.RunOptions).Id);
        if (pickNewStrategy)
            chat.ToggleRunOptionCommand.Execute(RemoteProtocol.RunOptions.Research);

        var request = Assert.Single(chat.GetPendingSendReconciliations());
        Assert.True(chat.TryReconcilePendingSend(request, new RemoteTranscript
        {
            ChatId = chatId,
            Turns = [new RemoteTranscriptTurn
            {
                Items = [new RemoteTranscriptItem { Kind = RemoteProtocol.ItemKinds.User, RequestId = request.RequestId }]
            }]
        }));

        if (pickNewStrategy)
            Assert.Equal(RemoteProtocol.RunOptions.Research, Assert.Single(chat.RunOptions).Id);
        else
            Assert.Empty(chat.RunOptions);
        Assert.Empty(chat.GetPendingSendReconciliations());
    }

    private sealed class RecordingSink : IRemoteCommandSink
    {
        public bool SupportsRunOptions { get; set; }
        public RemoteCommandResult? NextSendResult { get; set; }
        public Func<RemoteCommand, Task<RemoteCommandResult>?>? SendOverride { get; set; }
        public List<RemoteCommand> Commands { get; } = [];

        public IEnumerable<RemoteCommand> Configurations =>
            Commands.Where(command => command.Action == RemoteProtocol.Actions.ConfigureChat);

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command)
        {
            Commands.Add(command);
            if (SendOverride?.Invoke(command) is { } overridden)
                return overridden;
            if (command.Action == RemoteProtocol.Actions.SendMessage && NextSendResult is { } custom)
                return Task.FromResult(custom);
            return Task.FromResult(new RemoteCommandResult { Ok = true });
        }

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse { Ok = true });
    }
}
