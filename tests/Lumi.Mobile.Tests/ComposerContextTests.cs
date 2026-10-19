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

    private sealed class RecordingSink : IRemoteCommandSink
    {
        public List<RemoteCommand> Commands { get; } = [];

        public IEnumerable<RemoteCommand> Configurations =>
            Commands.Where(command => command.Action == RemoteProtocol.Actions.ConfigureChat);

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command)
        {
            Commands.Add(command);
            return Task.FromResult(new RemoteCommandResult { Ok = true });
        }

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse { Ok = true });
    }
}
