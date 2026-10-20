using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Capabilities;
using Lumi.Services.Discover;
using Lumi.Services.Onboarding;
using Lumi.ViewModels;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class SetupSuggestionsTests
{
    private static readonly SetupScenario Microsoft365 = SetupScenarios.All.Single(scenario => scenario.Id == "microsoft-365");
    private static readonly PluginInstallation WorkIq = new("workiq", "work-iq", "1.0", true, null, false);

    [Fact]
    public void EveryScenarioIsWellFormed()
    {
        Assert.Equal(SetupScenarios.All.Count, SetupScenarios.All.Select(scenario => scenario.Id).Distinct().Count());
        Assert.All(SetupScenarios.All, scenario =>
        {
            Assert.Matches("^[^/\\s]+/[^/\\s]+$", scenario.Plugin.Marketplace);
            Assert.False(string.IsNullOrWhiteSpace(Loc.Get(scenario.NameKey)));
            Assert.False(string.IsNullOrWhiteSpace(Loc.Get(scenario.PromptKey)));
            Assert.NotEmpty(scenario.Mark);
        });
    }

    [Theory]
    [InlineData("Microsoft 365", "Teams", true)]
    [InlineData("Excel", "teams", true)]
    [InlineData("Microsoft Office", "Chrome", false)]
    [InlineData("Teams", "Chrome", false)]
    [InlineData("Outlook", "Firefox", false)]
    public void Microsoft365NeedsOfficeTogetherWithTeams(string first, string second, bool expected)
        => Assert.Equal(expected, Microsoft365.Matches([first, second], []));

    [Theory]
    [InlineData("contoso.sharepoint.com", true)]
    [InlineData("https://contoso.sharepoint.com/sites/work", true)]
    [InlineData("outlook.office.com", true)]
    [InlineData("learn.microsoft.com", false)]
    [InlineData("outlook.live.com", false)]
    [InlineData("contoso.sharepoint.com.other.test", false)]
    [InlineData("notsharepoint.com", false)]
    public void Microsoft365RecognizesWorkSitesNotPersonalMailOrLookalikes(string site, bool expected)
        => Assert.Equal(expected, Microsoft365.Matches([], [site]));

    [Fact]
    public async Task StartupReadsAppNamesOnceAndNothingElse()
    {
        var discovery = new FakeDiscovery(["Microsoft 365", "Teams"]);
        using var vm = Create(new DataStore(new AppData()), discovery);

        await vm.CheckAtStartupAsync();
        await vm.CheckAtStartupAsync();

        Assert.Same(vm.Items.Single(), vm.Current);
        Assert.Equal(1, discovery.AppReads);
        Assert.Equal(0, discovery.SiteReads);
        Assert.Equal(0, discovery.ProjectReads);
        Assert.Equal(0, discovery.DocumentReads);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task AnAnsweredSuggestionOrDeclinedAppSourceSkipsTheStartupScan(bool answered, bool allowApps)
    {
        var data = new AppData();
        if (answered)
            data.Settings.HandledSetupSuggestions.Add(Microsoft365.Id);
        data.Settings.AllowAppSetupSuggestions = allowApps;
        var discovery = new FakeDiscovery(["Microsoft 365", "Teams"]);
        using var vm = Create(new DataStore(data), discovery);

        await vm.CheckAtStartupAsync();

        Assert.Equal(0, discovery.AppReads);
        Assert.False(vm.HasCurrent);
    }

    [Fact]
    public async Task AnInstalledSetupIsNotSuggestedButShowsAsReady()
    {
        var discovery = new FakeDiscovery(["Microsoft 365", "Teams"]);
        using var vm = Create(new DataStore(new AppData()), discovery, installed: [WorkIq]);

        await vm.CheckAtStartupAsync();

        var setup = vm.Items.Single();
        Assert.True(setup.IsReady);
        Assert.Equal(Loc.Setup_Try, setup.ActionLabel);
        Assert.False(vm.HasCurrent);
        Assert.Equal(0, discovery.AppReads);
    }

    [Fact]
    public async Task DismissingSurvivesTheIndexSnapshotAndJsonRoundTrip()
    {
        var data = new AppData();
        using (var vm = Create(new DataStore(data)))
        {
            vm.ConsiderWorkspace(WorkWorkspace());
            Assert.True(vm.HasCurrent);
            await vm.Current!.DismissCommand.ExecuteAsync(null);
            Assert.False(vm.HasCurrent);
        }

        data.Settings.AllowAppSetupSuggestions = false;
        var snapshot = AppDataSnapshotFactory.CreateIndexSnapshot(data);
        var json = JsonSerializer.Serialize(snapshot, AppDataJsonContext.Default.AppData);
        var restored = JsonSerializer.Deserialize(json, AppDataJsonContext.Default.AppData)!;
        Assert.Equal([Microsoft365.Id], restored.Settings.HandledSetupSuggestions);
        Assert.False(restored.Settings.AllowAppSetupSuggestions);

        using var restarted = Create(new DataStore(restored));
        restarted.ConsiderWorkspace(WorkWorkspace());
        Assert.False(restarted.HasCurrent);
    }

    [Fact]
    public async Task SetUpInstallsOnlyWhenAskedAndTryItDraftsWithoutSending()
    {
        var store = new DataStore(new AppData());
        var installs = 0;
        using var vm = Create(store, install: (_, _) =>
        {
            installs++;
            return Task.FromResult(new PluginInstallOutcome(WorkIq, null, null));
        });
        var prompts = new List<string>();
        vm.TryRequested += prompts.Add;
        vm.ConsiderWorkspace(WorkWorkspace());
        var setup = vm.Current!;
        Assert.Equal(0, installs);
        Assert.Equal(Loc.Setup_SetUp, setup.ActionLabel);

        await setup.ActCommand.ExecuteAsync(null);

        Assert.Equal(1, installs);
        Assert.True(setup.IsReady);
        Assert.Equal(Loc.Setup_Ready, setup.Detail);
        Assert.Contains(Microsoft365.Id, store.Data.Settings.HandledSetupSuggestions);
        Assert.Same(setup, vm.Current);
        Assert.Empty(prompts);

        await setup.ActCommand.ExecuteAsync(null);

        Assert.Equal([Loc.Setup_Microsoft365_Prompt], prompts);
        Assert.False(vm.HasCurrent);
        Assert.Equal(1, installs);
    }

    [Fact]
    public async Task ATurnedOffPluginIsManagedNotReinstalled()
    {
        var installs = 0;
        using var vm = Create(new DataStore(new AppData()), installed: [WorkIq with { Enabled = false }],
            install: (_, _) =>
            {
                installs++;
                return Task.FromResult(new PluginInstallOutcome(WorkIq, null, null));
            });
        await vm.RefreshAsync();
        var setup = vm.Items.Single();
        SetupSuggestionItem? managed = null;
        vm.ManageRequested += item => managed = item;

        Assert.Equal(Loc.Setup_TurnedOff, setup.Detail);
        await setup.ActCommand.ExecuteAsync(null);

        Assert.Same(setup, managed);
        Assert.Equal("workiq", setup.Installation?.Name);
        Assert.Equal(0, installs);
    }

    [Fact]
    public async Task AConnectorAddedAnotherWayCountsAsSetUp()
    {
        var snapshot = new CapabilitySnapshot(CapabilityQuery.Empty,
        [
            new CapabilityDescriptor
            {
                Kind = CapabilityKind.McpServer,
                Name = "workiq",
                Origin = CapabilityOrigin.Workspace,
                IsEnabled = false,
            },
        ], isComplete: true);
        using var vm = new SetupSuggestionsViewModel(new DataStore(new AppData()),
            _ => Task.FromResult<IReadOnlyList<PluginInstallation>>([]),
            (_, _) => throw new InvalidOperationException("Must reuse the configured connector"),
            () => snapshot);
        await vm.RefreshAsync();
        var setup = vm.Items.Single();
        var managed = false;
        vm.ManageRequested += _ => managed = true;

        vm.ConsiderWorkspace(WorkWorkspace());
        await setup.ActCommand.ExecuteAsync(null);

        Assert.False(vm.HasCurrent);
        Assert.True(managed);
        Assert.True(setup.IsInstalled);
        Assert.Null(setup.Installation);
    }

    [Fact]
    public async Task AFailedSetupSaysWhyAndCanBeRetried()
    {
        var attempts = 0;
        using var vm = Create(new DataStore(new AppData()), install: (_, _) => ++attempts == 1
            ? Task.FromException<PluginInstallOutcome>(new InvalidOperationException("Fixture connection failure"))
            : Task.FromResult(new PluginInstallOutcome(WorkIq, null, null)));
        var setup = vm.Items.Single();

        await setup.ActCommand.ExecuteAsync(null);
        Assert.True(setup.HasError);
        Assert.Contains("Fixture connection failure", setup.Detail);
        Assert.Equal(Loc.Setup_Retry, setup.ActionLabel);
        Assert.False(setup.IsInstalled);
        Assert.False(setup.IsBusy);

        await setup.ActCommand.ExecuteAsync(null);
        Assert.Equal(2, attempts);
        Assert.True(setup.IsReady);
        Assert.False(setup.HasError);
    }

    [Fact]
    public async Task AnUnconfirmedInstallIsNotReportedAsSetUp()
    {
        using var vm = Create(new DataStore(new AppData()),
            install: (_, _) => Task.FromResult(new PluginInstallOutcome(null, null, null)));
        var setup = vm.Items.Single();

        await setup.ActCommand.ExecuteAsync(null);

        Assert.False(setup.IsInstalled);
        Assert.True(setup.HasError);
    }

    [Fact]
    public async Task DiscoverListsEverySetupOnEverythingAndSearchesItsTerms()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var data = new AppData();
            data.Settings.McpRegistryUrls = [];
            var store = new DataStore(data);
            using var setups = Create(store);
            var discover = new DiscoverViewModel(store,
                new PluginCatalogService(static () => null, static () => { }),
                new McpRegistryClient(), static () => null)
            {
                Setups = setups,
            };
            setups.ConsiderWorkspace(WorkWorkspace());
            await setups.Current!.DismissCommand.ExecuteAsync(null);

            Assert.Equal(setups.Items, discover.SetupsInView);
            discover.SearchQuery = "Outlook";
            Assert.True(discover.HasSetupsInView);
            discover.SearchQuery = "M365";
            Assert.True(discover.HasSetupsInView);
            discover.SearchQuery = "weather";
            Assert.False(discover.HasSetupsInView);
            discover.SearchQuery = "";
            discover.ShowPluginsCommand.Execute(null);
            Assert.False(discover.HasSetupsInView);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TryItOpensANewDraftWithoutTouchingTheOpenConversation()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var chat = new Chat
            {
                Title = "Existing conversation",
                Messages = [new ChatMessage { Role = "user", Content = "Keep this conversation unchanged." }],
            };
            var data = new AppData { Chats = [chat] };
            data.Settings.IsOnboarded = true;
            using var main = new MainViewModel(new DataStore(data), TestCopilot.Shared,
                new UpdateService(), startBackgroundJobs: false);
            var draft = main.ChatVM;
            Assert.Same(main.SetupsVM, draft.SetupSuggestions);
            await main.OpenChatByIdAsync(chat.Id);
            // Only the chat a window shows holds its suggestions.
            Assert.Null(draft.SetupSuggestions);
            Assert.Same(main.SetupsVM, main.ChatVM.SetupSuggestions);
            var setup = main.SetupsVM.Items.Single();
            setup.IsInstalled = true;

            await setup.ActCommand.ExecuteAsync(null);

            Assert.NotSame(chat, main.ChatVM.CurrentChat);
            Assert.Same(main.SetupsVM, main.ChatVM.SetupSuggestions);
            Assert.Equal(Loc.Setup_Microsoft365_Prompt, main.ChatVM.PromptText);
            Assert.Single(chat.Messages);
            Assert.Equal("Keep this conversation unchanged.", chat.Messages[0].Content);
            Assert.False(main.ChatVM.IsBusy);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ADetachedChatWindowHasNoSuggestionAndAClosedWindowReleasesIt()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var data = new AppData();
            data.Settings.IsOnboarded = true;
            var main = new MainViewModel(new DataStore(data), TestCopilot.Shared,
                new UpdateService(), startBackgroundJobs: false);
            DetachedChatWindowRequest? request = null;
            main.OpenChatWindowRequested += requested => request = requested;

            main.OpenNewChatInNewWindowCommand.Execute(null);

            // Its buttons would otherwise act on the main window.
            Assert.NotNull(request);
            Assert.Null(request.WindowVM.ChatVM.SetupSuggestions);
            request.WindowVM.Dispose();
            request.ReleaseSurface();

            var shown = main.ChatVM;
            Assert.Same(main.SetupsVM, shown.SetupSuggestions);
            main.Dispose();
            Assert.Null(shown.SetupSuggestions);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task WelcomeUsesOnlyTheAllowedScanAndRemembersAnAppOptOut()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var store = new DataStore(new AppData());
            var discovery = new FakeDiscovery(["Microsoft 365", "Teams"], ["contoso.sharepoint.com"]);
            using var setups = Create(store, discovery);
            setups.IsWelcome = true;
            var welcome = new OnboardingViewModel(store, TestCopilot.Shared, discovery)
            {
                Setups = setups,
                MinimumScanDuration = TimeSpan.Zero,
                CurrentStep = (int)OnboardingStep.Workspace,
            };
            welcome.Sources.Single(source => source.Key == "apps").IsEnabled = false;

            await welcome.ContinueCommand.ExecuteAsync(null);

            Assert.Equal(0, discovery.AppReads);
            Assert.Equal(1, discovery.SiteReads);
            Assert.True(setups.HasCurrent);
            Assert.False(store.Data.Settings.AllowAppSetupSuggestions);

            // Welcome cannot open a chat, so a setup finished there only confirms itself.
            await setups.Current!.ActCommand.ExecuteAsync(null);
            Assert.True(setups.Current!.IsReady);
            Assert.False(setups.Current.ShowAction);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SkippingAfterGoingBackFromResultsDropsTheSuggestion()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var store = new DataStore(new AppData());
            var discovery = new FakeDiscovery(["Microsoft 365", "Teams"]);
            using var setups = Create(store, discovery);
            var welcome = new OnboardingViewModel(store, TestCopilot.Shared, discovery)
            {
                Setups = setups,
                MinimumScanDuration = TimeSpan.Zero,
                CurrentStep = (int)OnboardingStep.Workspace,
                Pacing = TimeSpan.Zero,
            };
            await welcome.ContinueCommand.ExecuteAsync(null);
            Assert.True(setups.HasCurrent);

            welcome.GoBackCommand.Execute(null);
            welcome.SecondaryCommand.Execute(null);

            Assert.False(setups.HasCurrent);
            Assert.False(store.Data.Settings.AllowAppSetupSuggestions);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SkippingWelcomeDiscoveryPreventsALaterStartupScan()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var store = new DataStore(new AppData());
            var discovery = new FakeDiscovery(["Microsoft 365", "Teams"]);
            using var setups = Create(store, discovery);
            var welcome = new OnboardingViewModel(store, TestCopilot.Shared, discovery)
            {
                Setups = setups,
                CurrentStep = (int)OnboardingStep.Workspace,
                Pacing = TimeSpan.Zero,
            };

            welcome.SecondaryCommand.Execute(null);
            await setups.CheckAtStartupAsync();

            Assert.False(store.Data.Settings.AllowAppSetupSuggestions);
            Assert.Equal(0, discovery.AppReads);
            Assert.False(setups.HasCurrent);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheRowShowsTheSetupAndDismissesThroughItsCommand()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            using var vm = Create(new DataStore(new AppData()));
            vm.ConsiderWorkspace(WorkWorkspace());
            var view = new SetupSuggestionView
            {
                DataContext = vm.Current,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            };
            var window = new Window { Content = view, Width = 520, Height = 200 };
            window.Show();
            try
            {
                window.UpdateLayout();
                var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
                Assert.Contains(Loc.Setup_Microsoft365_Name, texts);
                Assert.Contains(Loc.Setup_Microsoft365_Description, texts);
                Assert.True(view.Bounds.Height < 80, $"The row should stay compact, was {view.Bounds.Height}.");
                var action = view.FindControl<Button>("SetupSuggestionAction")!;
                var dismiss = view.FindControl<Button>("SetupSuggestionDismiss")!;
                Assert.True(action.IsEffectivelyVisible);
                Assert.True(dismiss.IsEffectivelyVisible);

                view.CanDismiss = false;
                Assert.False(dismiss.IsVisible);
                view.CanDismiss = true;

                dismiss.Command!.Execute(null);
                await Task.Yield();
                Assert.False(vm.HasCurrent);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static SetupSuggestionsViewModel Create(
        DataStore store,
        IWorkspaceDiscovery? discovery = null,
        IReadOnlyList<PluginInstallation>? installed = null,
        Func<SetupScenario, CancellationToken, Task<PluginInstallOutcome>>? install = null)
        => new(store,
            _ => Task.FromResult(installed ?? (IReadOnlyList<PluginInstallation>)[]),
            install ?? ((_, _) => Task.FromResult(new PluginInstallOutcome(WorkIq, null, null))),
            discovery: discovery ?? new FakeDiscovery([]));

    private static WorkspaceSnapshot WorkWorkspace()
        => new([], ["Microsoft 365", "Teams"], [], WorkspaceDocuments.Empty);

    private sealed class FakeDiscovery(IReadOnlyList<string> apps, IReadOnlyList<string>? sites = null) : IWorkspaceDiscovery
    {
        public int AppReads { get; private set; }
        public int SiteReads { get; private set; }
        public int ProjectReads { get; private set; }
        public int DocumentReads { get; private set; }

        public Task<IReadOnlyList<string>> FindAppsAsync(CancellationToken ct)
        {
            AppReads++;
            return Task.FromResult(apps);
        }

        public Task<IReadOnlyList<string>> FindSitesAsync(CancellationToken ct)
        {
            SiteReads++;
            return Task.FromResult(sites ?? (IReadOnlyList<string>)[]);
        }

        public Task<IReadOnlyList<DiscoveredProject>> FindProjectsAsync(CancellationToken ct)
        {
            ProjectReads++;
            return Task.FromResult<IReadOnlyList<DiscoveredProject>>([]);
        }

        public Task<WorkspaceDocuments> FindDocumentsAsync(CancellationToken ct)
        {
            DocumentReads++;
            return Task.FromResult(WorkspaceDocuments.Empty);
        }
    }
}
