using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Capabilities;
using Lumi.ViewModels;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ChatViewModelConnectorTests
{
    private static readonly McpServerConfig LocalConfig = new McpStdioServerConfig { Command = "npx" };

    [Fact]
    public async Task AProblemSurvivesUnrelatedRebuildsAndOnlyProblemsNeedAttention()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var fixture = Fixture.Create();
            var vm = fixture.ViewModel;
            vm.RecordMcpIssue(fixture.Chat.Id, "Broken", Problem("spawn npx ENOENT\n    at spawn (node:child_process)"));

            var broken = Chip(vm, "Broken");
            Assert.True(broken.HasError);
            Assert.Equal(Loc.Get(nameof(Loc.Connector_Issue_CommandNotFound), "npx"), broken.ErrorMessage);
            Assert.Contains("at spawn (node:child_process)", broken.ErrorDetails, StringComparison.Ordinal);
            Assert.Equal(Loc.Chat_ConnectorsNeedAttentionOne, vm.ConnectorsButtonToolTip);

            // Saving a memory (or any management change) rebuilds the chips; the connector is still broken.
            vm.RefreshFeatureCatalogState(new FeatureChangeResult("Memory saved.", DataChanged: true));
            Assert.True(Chip(vm, "Broken").HasError);

            vm.RecordMcpIssue(fixture.Chat.Id, "Healthy", McpConnectorIssues.StillStarting("Healthy", TimeSpan.FromSeconds(10), LocalConfig, null));
            var starting = Chip(vm, "Healthy");
            Assert.False(starting.HasError);
            Assert.Equal(Loc.Connector_Issue_StillStarting, starting.StatusMessage);
            Assert.Equal(Loc.Chat_ConnectorsNeedAttentionOne, vm.ConnectorsButtonToolTip);

            vm.RecordMcpIssue(fixture.Chat.Id, "Broken", null);
            Assert.False(Chip(vm, "Broken").HasError);
            Assert.Null(Chip(vm, "Broken").ErrorDetails);
            Assert.Equal(AllOnToolTip(3), vm.ConnectorsButtonToolTip);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TurningAConnectorOffAndOnStartsItAfresh()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var fixture = Fixture.Create();
            var vm = fixture.ViewModel;
            vm.RecordMcpIssue(fixture.Chat.Id, "Broken", Problem("spawn npx ENOENT"));

            vm.ActiveMcpChips.Remove(Chip(vm, "Broken"));
            vm.ActiveMcpChips.Add(new StrataComposerChip("Broken"));
            vm.RecordMcpIssue(fixture.Chat.Id, "Healthy", null);

            Assert.False(Chip(vm, "Broken").HasError);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EditingAMessageKeepsWhatTheSessionReportedAboutItsConnectors()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var fixture = Fixture.Create();
            var vm = fixture.ViewModel;
            var sent = new ChatMessage
            {
                Role = "user",
                Content = "Earlier request",
                ActiveMcpServerNames = ["Broken", "Healthy"],
                HasMcpSelection = true,
            };
            fixture.Chat.Messages.Add(sent);
            vm.RecordMcpIssue(fixture.Chat.Id, "Broken", Problem("spawn npx ENOENT"));

            // Editing shows that message's connectors and canceling restores the draft's. The session still
            // runs them, so the broken one keeps saying so either way.
            typeof(ChatViewModel)
                .GetMethod("BeginComposerEdit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(vm, [sent]);
            Assert.True(Chip(vm, "Broken").HasError);

            vm.CancelComposerEditCommand.Execute(null);
            Assert.True(Chip(vm, "Broken").HasError);
            Assert.Equal(Loc.Chat_ConnectorsNeedAttentionOne, vm.ConnectorsButtonToolTip);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ARepairChatBelongsWhereTheConnectorRan_NotToTheSidebarsProject()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var fixture = Fixture.Create();
            var vm = fixture.ViewModel;
            vm.ActiveProjectFilterId = Assert.Single(fixture.Store.Data.Projects).Id;
            vm.RecordMcpIssue(fixture.Chat.Id, "Broken", Problem("spawn npx ENOENT"));

            var request = vm.BuildMcpRepairRequest(Chip(vm, "Broken"));

            Assert.Null(fixture.Chat.ProjectId);
            Assert.Null(request.Chat.ProjectId);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RepairAndConfigurationChangesTurnProblemsIntoAQuietRetry()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var fixture = Fixture.Create();
            var vm = fixture.ViewModel;
            vm.RecordMcpIssue(fixture.Chat.Id, "Broken", Problem("spawn npx ENOENT"));
            vm.RecordMcpIssue(fixture.Chat.Id, "Other broken", Problem("MCP error -32000: Connection closed"));

            vm.RetryMcpConnectorAfterRepair(fixture.Chat.Id, "Broken");
            Assert.False(Chip(vm, "Broken").HasError);
            Assert.Equal(Loc.Connector_Issue_Retrying, Chip(vm, "Broken").StatusMessage);
            Assert.True(Chip(vm, "Other broken").HasError);

            vm.InvalidateMcpSession();
            Assert.Equal(Loc.Connector_Issue_Retrying, Chip(vm, "Other broken").StatusMessage);
            Assert.Equal(AllOnToolTip(3), vm.ConnectorsButtonToolTip);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ConnectorChipsSayWhatEachConnectorIs_AndGroupLikeTheConnectorsPage()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var fixture = Fixture.Create(extraServers:
            [
                new McpServer { Name = "Docs", Description = "  Searches the\nteam docs  ", ServerType = "remote", Url = "https://docs.example/mcp" },
                new McpServer { Name = "Notes", ServerType = "remote", Url = "https://notes.example/mcp" },
            ]);
            var vm = fixture.ViewModel;

            var docs = Available(vm, "Docs");
            Assert.Equal("Searches the team docs", docs.SecondaryText);
            Assert.Equal(Loc.External_SectionYours, docs.Group);
            // The source hint Lumi relies on to tell its own connectors from discovered ones stays as it was.
            Assert.Equal(CapabilityOrigin.Lumi.Label, docs.SourceLabel);
            Assert.Equal(Loc.ConnectorCaption_Remote, Available(vm, "Notes").SecondaryText);
            Assert.Equal(Loc.ConnectorCaption_Local, Available(vm, "Broken").SecondaryText);
            Assert.Equal(Available(vm, "Broken"), Chip(vm, "Broken"));

            // Some sources only offer a command line or an address; the list says where they come from instead.
            Assert.Equal(
                Loc.Get(nameof(Loc.ConnectorCaption_Plugin), "Azure"),
                vm.DescribeConnector(External("azure", CapabilityOrigin.ForPlugin("Azure"), pluginName: "Azure")));
            Assert.Equal(
                Loc.ConnectorCaption_Project,
                vm.DescribeConnector(External("tools", CapabilityOrigin.Workspace, description: "npx -y @example/tools")));
            Assert.Equal(Loc.ConnectorCaption_Personal, vm.DescribeConnector(External("mine", CapabilityOrigin.Personal)));
            Assert.Equal(Loc.ConnectorCaption_Copilot, vm.DescribeConnector(External("built-in", CapabilityOrigin.BuiltIn)));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task WithoutConnectors_TheButtonInvitesTheUserToAddSome()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var fixture = Fixture.Create(withServers: false);
            var vm = fixture.ViewModel;

            Assert.Empty(vm.AvailableMcpChips);
            Assert.Equal(Loc.Chat_ConnectorsTooltipEmpty, vm.ConnectorsButtonToolTip);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task FindAndManageAskTheWindowForTheRightPage()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var fixture = Fixture.Create();
            var vm = fixture.ViewModel;
            var requests = new List<ConnectorsPageRequest>();
            vm.ConnectorsPageRequested += requests.Add;

            vm.FindConnectorsCommand.Execute("  jira ");
            vm.FindConnectorsCommand.Execute(null);
            vm.ManageConnectorsCommand.Execute(null);

            Assert.Equal(
                [new ConnectorsPageRequest(true, "jira"), new ConnectorsPageRequest(true, null), new ConnectorsPageRequest(false, null)],
                requests);
        }, CancellationToken.None);
    }

    [Fact]
    public void ManageOpensTheConnectorsSectionOfCustomize()
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

        main.ChatVM.ManageConnectorsCommand.Execute(null);

        Assert.Equal(6, main.SelectedNavIndex);
        Assert.True(main.IsMcpSection);
    }

    [Fact]
    public void LocalizedLabelStyle_SetsTheConnectorListsLabelsFromLoc()
    {
        // Built from Loc when called (after the saved language loads), like the model picker's.
        var setters = Lumi.App.CreateConnectorPickerTextStyle().Setters.OfType<Avalonia.Styling.Setter>()
            .ToDictionary(setter => setter.Property!, setter => setter.Value);

        Assert.Equal(16, setters.Count);
        Assert.Equal(Loc.Mcp_SearchPlaceholder, setters[StrataConnectorPicker.SearchWatermarkProperty]);
        Assert.Equal(Loc.ConnectorPicker_Summary, setters[StrataConnectorPicker.SummaryFormatProperty]);
        Assert.Equal(Loc.ConnectorPicker_Off, setters[StrataConnectorPicker.OffLabelProperty]);
        Assert.Equal(Loc.ConnectorPicker_SearchDiscover, setters[StrataConnectorPicker.SearchBrowseFormatProperty]);
        Assert.Equal(Loc.ConnectorPicker_EmptyAction, setters[StrataConnectorPicker.EmptyActionLabelProperty]);
        Assert.Equal(Loc.ConnectorPicker_Loading, setters[StrataConnectorPicker.LoadingTextProperty]);
        Assert.Equal(Loc.Composer_Connectors, setters[StrataConnectorPicker.LabelProperty]);
        Assert.Equal(Loc.Chat_ConnectorsHeader, setters[StrataConnectorPicker.HeaderProperty]);
    }

    [Fact]
    public async Task FixWithLumiOpensAFreshRepairChatAndLeavesThisChatAlone()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            using var fixture = Fixture.Create(withProject: true);
            var vm = fixture.ViewModel;
            vm.PromptText = "Keep my unsent draft";
            vm.RecordMcpIssue(fixture.Chat.Id, "Broken", Problem("spawn npx ENOENT\nfull stack line"));
            vm.RecordMcpIssue(fixture.Chat.Id, "Other broken", Problem("MCP error -32000: Connection closed"));
            vm.RecordMcpIssue(fixture.Chat.Id, "Healthy", McpConnectorIssues.StillStarting("Healthy", TimeSpan.FromSeconds(10), LocalConfig, null));

            var requests = new List<McpRepairRequest>();
            vm.McpRepairRequested += _ => throw new InvalidOperationException("Only one window may start the repair.");
            vm.McpRepairRequested += request =>
            {
                requests.Add(request);
                return Task.CompletedTask;
            };

            Assert.False(vm.FixMcpConnectorCommand.CanExecute(Chip(vm, "Healthy")));
            await vm.FixMcpConnectorCommand.ExecuteAsync(Chip(vm, "Broken"));

            var request = Assert.Single(requests);
            Assert.Equal(fixture.Chat.Id, request.SourceChatId);
            Assert.Equal("Broken", request.ConnectorName);
            Assert.NotEqual(fixture.Chat.Id, request.Chat.Id);
            Assert.Equal(fixture.Chat.ProjectId, request.Chat.ProjectId);
            Assert.Equal(fixture.Chat.WorktreePath, request.Chat.WorktreePath);
            Assert.Null(request.Chat.AgentId);
            Assert.Null(request.Chat.CopilotSessionId);
            Assert.Empty(request.Chat.Messages);
            Assert.Equal(["Healthy"], request.Chat.ActiveMcpServerNames);

            Assert.Contains("Broken", request.Message, StringComparison.Ordinal);
            Assert.Contains(Loc.Get(nameof(Loc.Connector_Issue_CommandNotFound), "npx"), request.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("full stack line", request.Message, StringComparison.Ordinal);
            Assert.Contains("full stack line", request.ReportContent, StringComparison.Ordinal);
            Assert.Equal(Loc.Get(nameof(Loc.Connector_RepairReportFileName), "Broken") + ".md", request.ReportFileName);

            Assert.Same(fixture.Chat, vm.CurrentChat);
            Assert.Equal("Keep my unsent draft", vm.PromptText);
            Assert.Equal(["Broken", "Other broken", "Healthy"], fixture.Chat.ActiveMcpServerNames);
            Assert.All(fixture.Store.Data.McpServers, server => Assert.True(server.IsEnabled));

            using var plan = McpSessionPlanner.Build(
                fixture.Store.Data, Path.GetTempPath(), CapabilitySnapshot.Empty, request.Chat, null, null);
            Assert.Contains("Healthy", plan.Servers.Keys);
            Assert.DoesNotContain("Broken", plan.Servers.Keys);
            Assert.DoesNotContain("Other_broken", plan.Servers.Keys);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ARecoveredConnectorCanNoLongerBeSentForRepair()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var fixture = Fixture.Create();
            var vm = fixture.ViewModel;
            vm.RecordMcpIssue(fixture.Chat.Id, "Broken", Problem("spawn npx ENOENT"));
            var captured = Chip(vm, "Broken");
            Assert.False(vm.FixMcpConnectorCommand.CanExecute(captured));

            vm.McpRepairRequested += _ => Task.CompletedTask;
            Assert.True(vm.FixMcpConnectorCommand.CanExecute(captured));

            vm.RecordMcpIssue(fixture.Chat.Id, "Broken", null);
            Assert.False(vm.FixMcpConnectorCommand.CanExecute(captured));
        }, CancellationToken.None);
    }

    [Fact]
    public void ASurfaceShownInTwoWindowsRepairsFromTheActiveOne()
    {
        var first = new RepairHost();
        var second = new RepairHost();
        Func<McpRepairRequest, Task> firstHandler = first.HandleAsync;
        Func<McpRepairRequest, Task> secondHandler = second.HandleAsync;

        Assert.Equal(firstHandler, ChatViewModel.SelectMcpRepairHandler([firstHandler, secondHandler], first));
        Assert.Equal(secondHandler, ChatViewModel.SelectMcpRepairHandler([firstHandler, secondHandler], second));
        Assert.Equal(secondHandler, ChatViewModel.SelectMcpRepairHandler([firstHandler, secondHandler], null));
        Assert.Null(ChatViewModel.SelectMcpRepairHandler([], first));
    }

    private static McpConnectorIssue Problem(string rawError)
        => McpConnectorIssues.Describe(McpServerStatus.Failed, rawError, LocalConfig);

    private static StrataComposerChip Chip(ChatViewModel vm, string name)
        => vm.ActiveMcpChips.OfType<StrataComposerChip>().Single(chip => chip.Name == name);

    private static StrataComposerChip Available(ChatViewModel vm, string name)
        => vm.AvailableMcpChips.Single(chip => chip.Name == name);

    private static string AllOnToolTip(int count)
        => string.Format(System.Globalization.CultureInfo.CurrentCulture, Loc.Chat_ConnectorsTooltip, count, count);

    private static CapabilityDescriptor External(string name, CapabilityOrigin origin, string? pluginName = null, string? description = null)
        => new()
        {
            Kind = CapabilityKind.McpServer,
            Name = name,
            Origin = origin,
            PluginName = pluginName,
            Description = description,
        };

    private sealed class RepairHost
    {
        public Task HandleAsync(McpRepairRequest request) => Task.CompletedTask;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly CapabilityCatalog _catalog;

        private Fixture(DataStore store, Chat chat, CapabilityCatalog catalog, ChatViewModel viewModel)
        {
            Store = store;
            Chat = chat;
            _catalog = catalog;
            ViewModel = viewModel;
        }

        public DataStore Store { get; }
        public Chat Chat { get; }
        public ChatViewModel ViewModel { get; }

        public static Fixture Create(bool withProject = false, bool withServers = true, IEnumerable<McpServer>? extraServers = null)
        {
            var project = new Project { Name = "Project", WorkingDirectory = Path.GetTempPath() };
            var servers = withServers
                ? new List<McpServer>
                {
                    new() { Name = "Broken", Command = "npx" },
                    new() { Name = "Other broken", Command = "node" },
                    new() { Name = "Healthy", Command = "uvx" },
                }
                : [];
            servers.AddRange(extraServers ?? []);
            var chat = new Chat
            {
                ProjectId = withProject ? project.Id : null,
                WorktreePath = withProject ? Path.GetTempPath() : null,
                AgentId = Guid.NewGuid(),
                CopilotSessionId = null,
                ActiveMcpServerNames = servers.Select(server => server.Name).ToList(),
                HasExplicitMcpServerSelection = true,
                Messages = [new ChatMessage { Role = "user", Content = "Original conversation" }],
            };
            var store = new DataStore(new AppData
            {
                Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false },
                Projects = [project],
                McpServers = servers,
                Chats = [chat],
            });
            var catalog = new CapabilityCatalog(new LumiCapabilityProvider(store));
            var viewModel = new ChatViewModel(store, TestCopilot.Shared, capabilityCatalog: catalog) { CurrentChat = chat };
            viewModel.PopulateDefaultMcps();
            return new Fixture(store, chat, catalog, viewModel);
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            _catalog.Dispose();
        }
    }
}
