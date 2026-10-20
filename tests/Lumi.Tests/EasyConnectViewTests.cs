using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using System.Net;
using System.Net.Http;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Byok;
using Lumi.ViewModels;
using Lumi.Views.Controls;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class EasyConnectViewTests
{
    [Theory]
    [InlineData("")]
    [InlineData("octocat")]
    public async Task GitHubAccountDisplaysTheAuthenticatedState(string user)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            using var vm = new EasyConnectViewModel(new DataStore(new AppData()), TestCopilot.Shared,
                new FakeSecureKeyStore(), () => { })
            {
                LoginVM = new GitHubLoginViewModel(TestCopilot.Shared) { GitHubLogin = user, IsAuthenticated = true }
            };
            var view = new EasyConnectView { DataContext = vm };
            var window = new Window { Width = 560, Height = 300, Content = view };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.Equal(string.IsNullOrEmpty(user) ? Loc.Get("EasyConnect_GitHubConnected")
                    : string.Format(Loc.Get("EasyConnect_GitHubSignedIn"), user),
                    view.FindControl<TextBlock>("GitHubAccountStatus")!.Text);
                Assert.True(vm.IsGitHubDefault);
                Assert.False(view.FindControl<Button>("GitHubSignInButton")!.IsEffectivelyVisible);
                Assert.True(view.FindControl<Button>("GitHubMoreButton")!.IsEffectivelyVisible);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AccountListPutsGitHubFirstThenConnectionsThenAddAccount()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var data = new AppData();
            data.Settings.ByokEndpoints.Add(new()
            {
                Id = "endpoint", Name = "OpenRouter", ConnectionProviderId = "openrouter",
                BaseUrl = "https://openrouter.ai/api/v1"
            });
            data.Settings.ByokModels.Add(new() { EndpointId = "endpoint", ModelId = "starter:free", DisplayName = "Starter" });
            using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared, new FakeSecureKeyStore(), () => { })
            {
                LoginVM = new GitHubLoginViewModel(TestCopilot.Shared)
            };
            var view = new EasyConnectView { DataContext = vm };
            var window = new Window { Width = 560, Height = 600, Content = view };
            window.Show();
            try
            {
                window.UpdateLayout();
                var gitHubRow = view.FindControl<Border>("GitHubAccountRow")!;
                var connectedRow = Assert.Single(view.GetVisualDescendants().OfType<Button>(),
                    button => button.Name == "ConnectedAccountRow");
                var addButton = view.FindControl<Button>("AddAiAccountButton")!;
                Assert.True(gitHubRow.TranslatePoint(default, view)!.Value.Y < connectedRow.TranslatePoint(default, view)!.Value.Y);
                Assert.True(connectedRow.TranslatePoint(default, view)!.Value.Y < addButton.TranslatePoint(default, view)!.Value.Y);
                Assert.True(view.FindControl<Button>("GitHubSignInButton")!.IsEffectivelyVisible);
                Assert.False(view.FindControl<Button>("GitHubMoreButton")!.IsEffectivelyVisible);

                addButton.Command!.Execute(null);
                Assert.True(vm.IsDialogOpen);
                Assert.True(vm.IsGallery);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ManagingConnectionShowsSavedModelsWithoutRediscovery()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var data = new AppData();
            data.Settings.ByokEndpoints.Add(new()
            {
                Id = "endpoint", Name = "OpenRouter", ConnectionProviderId = "openrouter",
                BaseUrl = "https://openrouter.ai/api/v1"
            });
            data.Settings.ByokModels.Add(new()
            {
                EndpointId = "endpoint", ModelId = "starter:free", DisplayName = "OpenRouter · Starter"
            });
            using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared,
                new FakeSecureKeyStore(), () => { });
            var view = new EasyConnectDialogView { DataContext = vm };
            var window = new Window { Width = 700, Height = 760, Content = view };
            window.Show();
            try
            {
                Assert.Single(vm.Connections).ManageCommand.Execute(null);
                window.UpdateLayout();
                Assert.True(vm.IsDialogOpen);
                Assert.True(vm.IsManaging);
                Assert.True(vm.IsModelStage);
                var choice = Assert.Single(view.GetVisualDescendants().OfType<ToggleButton>(),
                    control => control.DataContext is ConnectModelChoice);
                Assert.True(choice.IsEffectivelyVisible);
                Assert.True(choice.IsChecked);
                var model = Assert.IsType<ConnectModelChoice>(choice.DataContext);
                Assert.Equal("starter:free", model.Id);
                Assert.Equal("Starter", model.Name);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task GalleryGroupsProvidersAndOpensTheAccountStep()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var vm = new EasyConnectViewModel(new DataStore(new AppData()), TestCopilot.Shared,
                new FakeSecureKeyStore(), () => { });
            var view = new EasyConnectDialogView { DataContext = vm };
            var window = new Window { Width = 700, Height = 760, Content = view };
            window.Show();
            try
            {
                vm.OpenGalleryCommand.Execute(null);
                window.UpdateLayout();
                Assert.Equal("copilot", vm.ProviderGroups[0].Providers[0].Profile.Id);
                Assert.Contains(vm.ProviderGroups, group => group.Providers.Any(card => card.Profile.Id == "lmstudio"));

                vm.Search = "Poe";
                window.UpdateLayout();
                var card = Assert.Single(view.GetVisualDescendants().OfType<Button>(),
                    control => control.Name == "ProviderCard" && control.DataContext is ProviderCard { Profile.Id: "poe" });
                card.Command!.Execute(null);
                window.UpdateLayout();
                Assert.True(vm.IsAccountStage);
                Assert.Equal("Poe", vm.SelectedName);
                Assert.True(view.FindControl<Button>("ProviderSignIn")!.IsEffectivelyVisible);
                Assert.False(view.FindControl<TextBox>("ConnectApiKey")!.IsEffectivelyVisible);

                vm.ToggleKeyEntryCommand.Execute(null);
                window.UpdateLayout();
                Assert.True(view.FindControl<TextBox>("ConnectApiKey")!.IsEffectivelyVisible);

                vm.BackCommand.Execute(null);
                window.UpdateLayout();
                Assert.True(vm.IsGallery);
                Assert.Empty(vm.Search);
                Assert.Contains(view.GetVisualDescendants().OfType<Button>(), control => control.Name == "ProviderCard");
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task WelcomeOffersFoundryWithGuidedKeySetupAndDeploymentFallback()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            using var http = new HttpClient(new ResponseHandler(_ => new(HttpStatusCode.NotFound)));
            var store = new DataStore(new AppData());
            using var vm = new EasyConnectViewModel(store, TestCopilot.Shared, new FakeSecureKeyStore(), () => { },
                new ProviderConnectionService(http));
            var onboarding = new OnboardingView
            {
                DataContext = new OnboardingViewModel(store, TestCopilot.Shared)
                {
                    UserName = "Test", EasyConnect = vm, CurrentStep = (int)OnboardingStep.Connect
                }
            };
            var window = new Window { Width = 700, Height = 900, Content = onboarding };
            window.Show();
            try
            {
                window.UpdateLayout();
                var add = Assert.Single(onboarding.GetVisualDescendants().OfType<Button>(),
                    button => button.Name == "AddAiAccountButton");
                add.Command!.Execute(null);
                Assert.True(vm.IsGallery);
                Assert.Contains(vm.ProviderGroups, group => group.Providers.Any(card => card.Profile.IsFoundry));

                var dialog = new EasyConnectDialogView { DataContext = vm };
                window.Content = dialog;
                vm.Search = "Foundry";
                window.UpdateLayout();
                var card = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(),
                    button => button.Name == "ProviderCard" && button.DataContext is ProviderCard { Profile.IsFoundry: true });
                card.Command!.Execute(null);
                window.UpdateLayout();

                Assert.True(dialog.FindControl<TextBox>("ConnectBaseUrl")!.IsEffectivelyVisible);
                Assert.True(dialog.FindControl<TextBox>("ConnectApiKey")!.IsEffectivelyVisible);
                Assert.False(dialog.FindControl<CheckBox>("RequiresApiKeyToggle")!.IsEffectivelyVisible);
                Assert.False(dialog.FindControl<Button>("ProviderSignIn")!.IsEffectivelyVisible);
                Assert.Equal(Loc.Get("EasyConnect_FoundryEndpoint"), vm.ServerAddressLabel);
                Assert.True(vm.ShowWireChoice);
                Assert.True(vm.IsResponsesWire);

                vm.BaseUrl = "https://resource.openai.azure.com";
                vm.ApiKey = "test-key";
                await vm.DiscoverCommand.ExecuteAsync(null);
                window.UpdateLayout();
                Assert.True(vm.IsModelStage);
                Assert.True(dialog.FindControl<TextBox>("ConnectManualModel")!.IsEffectivelyVisible);
                Assert.Equal(Loc.Get("EasyConnect_FoundryDeploymentName"),
                    dialog.FindControl<TextBox>("ConnectManualModel")!.PlaceholderText);
                Assert.Equal(Loc.Get("EasyConnect_FoundryNoModelDiscovery"), vm.Status);
                vm.ManualModel = "my-specific-deployment";
                vm.AddModelCommand.Execute(null);
                Assert.Equal("my-specific-deployment", Assert.Single(vm.Models).Id);
                Assert.True(vm.CanSave);
                var responses = dialog.FindControl<Button>("FoundryUseResponses")!;
                var completions = dialog.FindControl<Button>("FoundryUseChatCompletions")!;
                Assert.True(responses.IsEffectivelyVisible);
                Assert.True(completions.IsEffectivelyVisible);
                completions.Command!.Execute(completions.CommandParameter);
                Assert.Equal("completions", vm.WireApi);
                Assert.True(Assert.Single(vm.Models).IsSelected);
                Assert.Equal("test-key", vm.ApiKey);
                responses.Command!.Execute(responses.CommandParameter);
                Assert.True(vm.IsResponsesWire);
                Assert.Empty(store.Data.Settings.ByokEndpoints);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ModelStepRequiresAChoiceAndCanSaveWithoutVerification()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = new AppData();
            using var http = new HttpClient(new ResponseHandler(_ => new(HttpStatusCode.OK)
            { Content = new StringContent("""{"data":[{"id":"gpt-6-luna"},{"id":"gpt-4o"}]}""") }));
            using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared,
                new FakeSecureKeyStore(), () => { }, new ProviderConnectionService(http));
            var dialog = new EasyConnectDialogView { DataContext = vm };
            var footer = new EasyConnectDialogFooter { DataContext = vm };
            var window = new Window
            {
                Width = 700, Height = 1000,
                Content = new StackPanel { Children = { dialog, footer } }
            };
            window.Show();
            try
            {
                vm.Providers.Single(card => card.Profile.IsFoundry).OpenCommand.Execute(null);
                vm.BaseUrl = "https://resource.services.ai.azure.com";
                vm.ApiKey = "test-key";
                await vm.DiscoverCommand.ExecuteAsync(null);
                window.UpdateLayout();
                var save = footer.FindControl<Button>("VerifyProviderConnection")!;
                var skip = dialog.FindControl<CheckBox>("SkipModelCheckToggle")!;
                Assert.True(skip.IsEffectivelyVisible);
                Assert.False(skip.IsChecked);
                Assert.False(save.IsEnabled);
                Assert.All(vm.Models, choice => Assert.False(choice.IsSelected));

                skip.IsChecked = true;
                window.UpdateLayout();
                Assert.True(vm.SkipModelCheck);
                Assert.False(save.IsEnabled);
                Assert.Equal(Loc.Get("EasyConnect_SaveWithoutTesting"), save.Content);
                vm.Models.Single(choice => choice.Id == "gpt-6-luna").IsSelected = true;
                window.UpdateLayout();
                Assert.True(save.IsEnabled);
                await vm.SaveConnectionCommand.ExecuteAsync(null);
                window.UpdateLayout();
                Assert.True(vm.IsComplete);
                Assert.False(vm.HasError);
                Assert.Equal(string.Format(Loc.Get("EasyConnect_DoneUnverifiedDefault"), "gpt-6-luna"), vm.DoneText);
                Assert.Equal("gpt-6-luna", Assert.Single(data.Settings.ByokModels).ModelId);
                Assert.True(Assert.Single(data.Settings.ByokModels).SupportsReasoningEffort);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SettingsSearchFindsAiAccountsAndRestoresAdvancedExpansionState()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            using var vm = new SettingsViewModel(new DataStore(new AppData()), TestCopilot.Shared,
                new BrowserService(), new UpdateService(), new FakeSecureKeyStore())
            {
                LoginVM = new GitHubLoginViewModel(TestCopilot.Shared),
                SelectedPageIndex = 5
            };
            var view = new SettingsView { DataContext = vm };
            var window = new Window { Width = 840, Height = 760, Content = view };
            window.Show();
            try
            {
                window.UpdateLayout();
                var advanced = view.FindSettingsControl<Expander>("AdvancedByokSettings")!;
                Assert.False(advanced.IsExpanded);
                vm.SearchQuery = "OpenRouter";
                window.UpdateLayout();
                Assert.True(view.FindSettingsControl<StrataSettingGroup>("AiAccountsGroup")!.IsVisible);
                Assert.True(view.FindSettingsControl<StackPanel>("PageAI")!.IsVisible);
                vm.SearchQuery = "endpoints";
                window.UpdateLayout();
                Assert.True(advanced.IsExpanded);
                vm.SearchQuery = "";
                window.UpdateLayout();
                Assert.False(advanced.IsExpanded);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task OnboardingOffersGitHubFirstOnTheConnectStep()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var store = new DataStore(new AppData());
            using var connect = new EasyConnectViewModel(store, TestCopilot.Shared, new FakeSecureKeyStore(), () => { });
            var login = new GitHubLoginViewModel(TestCopilot.Shared);
            connect.LoginVM = login;
            var vm = new OnboardingViewModel(store, TestCopilot.Shared) { LoginVM = login, EasyConnect = connect };
            var view = new OnboardingView { DataContext = vm };
            var window = new Window { Width = 900, Height = 900, Content = view };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.Equal(OnboardingStep.Hello, vm.Step);
                Assert.Equal(OnboardingViewModel.StepCount, view.FindControl<ItemsControl>("OnboardingProgress")!.ItemCount);
                Assert.False(vm.ContinueCommand.CanExecute(null));

                vm.UserName = "Test";
                vm.ContinueCommand.Execute(null);
                vm.ContinueCommand.Execute(null);
                Assert.Equal(OnboardingStep.Autonomy, vm.Step);
                vm.ContinueCommand.Execute(null);
                window.UpdateLayout();
                Assert.Equal(OnboardingStep.Connect, vm.Step);
                var accounts = view.GetVisualDescendants().OfType<EasyConnectView>().Single();
                Assert.True(accounts.FindControl<Button>("GitHubSignInButton")!.IsEffectivelyVisible);
                Assert.False(vm.CanLearn);
                Assert.Equal(Loc.Onboarding_ConnectMissing, vm.ConnectStatus);

                login.IsAuthenticated = true;
                Assert.True(vm.CanLearn);
                Assert.Equal(Loc.Onboarding_ConnectReady, vm.ConnectStatus);
                login.IsAuthenticated = false;

                // Connecting is never a dead end: onboarding goes on and says how to connect later.
                Assert.True(vm.ContinueCommand.CanExecute(null));
                vm.ContinueCommand.Execute(null);
                Assert.Equal(OnboardingStep.Workspace, vm.Step);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task OnboardingUpdatesConnectionWhenExternalModelsBecomeAvailableOrDisabled()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var data = new AppData();
            data.Settings.ByokEndpoints.Add(new()
            {
                Id = "local", Name = "LM Studio", ConnectionProviderId = "lmstudio",
                BaseUrl = "http://localhost:1234/v1", ProviderType = "openai", WireApi = "completions",
                ApiKeyMode = ByokApiKeyMode.None
            });
            var store = new DataStore(data);
            using var connect = new EasyConnectViewModel(store, TestCopilot.Shared,
                new FakeSecureKeyStore(), () => { });
            var vm = new OnboardingViewModel(store, TestCopilot.Shared)
            {
                UserName = "Test", EasyConnect = connect, CurrentStep = (int)OnboardingStep.Connect
            };
            var view = new OnboardingView { DataContext = vm };
            var window = new Window { Width = 900, Height = 900, Content = view };
            window.Show();
            try
            {
                window.UpdateLayout();
                var status = view.FindControl<Border>("ConnectStatus")!;
                Assert.False(vm.CanLearn);
                Assert.DoesNotContain("ready", status.Classes);

                var model = new ByokModel { EndpointId = "local", ModelId = "local-model", DisplayName = "Local model" };
                data.Settings.ByokModels.Add(model);
                connect.Refresh();
                window.UpdateLayout();
                Assert.True(vm.CanLearn);
                Assert.Contains("ready", status.Classes);

                model.IsEnabled = false;
                connect.Refresh();
                window.UpdateLayout();
                Assert.False(vm.CanLearn);
                Assert.DoesNotContain("ready", status.Classes);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BrowserSignInUsesTheAuthorizedKeyAndOffersRequiredStorageConsent(bool secureStorage)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var opened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            string? discoveredKey = null;
            using var discoveryHttp = new HttpClient(new ResponseHandler(request =>
            {
                discoveredKey = request.Headers.Authorization!.Parameter;
                return new(HttpStatusCode.OK)
                { Content = new StringContent("""{"data":[{"id":"tool-model","supported_parameters":["tools"]}]}""") };
            }));
            using var oauthHttp = new HttpClient(new ResponseHandler(_ => new(HttpStatusCode.OK)
            { Content = new StringContent("""{"api_key":"authorized-key","api_key_expires_in":3600}""") }));
            var data = new AppData();
            using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared,
                new FakeSecureKeyStore { IsSupported = secureStorage }, () => { },
                new ProviderConnectionService(discoveryHttp), new ProviderOAuthService(oauthHttp),
                uri => opened.TrySetResult(uri));
            var view = new EasyConnectDialogView { DataContext = vm };
            var window = new Window { Width = 700, Height = 760, Content = view };
            window.Show();
            try
            {
                vm.Providers.Single(card => card.Profile.Id == "poe").OpenCommand.Execute(null);
                vm.ToggleKeyEntryCommand.Execute(null);
                vm.ApiKey = "abandoned-key";
                vm.ToggleKeyEntryCommand.Execute(null);
                var authentication = vm.SignInCommand.ExecuteAsync(null);
                var authorization = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var fields = authorization.Query.TrimStart('?').Split('&')
                    .Select(pair => pair.Split('=', 2))
                    .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
                using var local = new HttpClient(new HttpClientHandler { UseProxy = false });
                using var response = await local.GetAsync(fields["redirect_uri"]
                    + "?code=test-code&state=" + Uri.EscapeDataString(fields["state"]));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                await authentication.WaitAsync(TimeSpan.FromSeconds(5));
                window.UpdateLayout();

                Assert.False(vm.HasError);
                Assert.True(vm.IsModelStage);
                Assert.Empty(vm.ApiKey);
                Assert.Equal("authorized-key", discoveredKey);
                var consent = view.FindControl<CheckBox>("ModelPlaintextConsent")!;
                Assert.Equal(!secureStorage, consent.IsEffectivelyVisible);
                Assert.Equal(secureStorage, vm.CanSaveCredential);
                if (!secureStorage)
                {
                    consent.IsChecked = true;
                    Assert.True(vm.CanSaveCredential);
                }
                Assert.Empty(data.Settings.ByokEndpoints);

                vm.BackCommand.Execute(null);
                vm.ToggleKeyEntryCommand.Execute(null);
                vm.ApiKey = "replacement-key";
                await vm.DiscoverCommand.ExecuteAsync(null);
                Assert.Equal("replacement-key", discoveredKey);
                vm.BackCommand.Execute(null);
                vm.ApiKey = "";
                Assert.False(vm.CanDiscover);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task BrowserLaunchFailureEndsSignInWithoutExchangingCredentials()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var exchanges = 0;
            using var http = new HttpClient(new ResponseHandler(_ =>
            {
                exchanges++;
                return new(HttpStatusCode.OK);
            }));
            using var vm = new EasyConnectViewModel(new DataStore(new AppData()), TestCopilot.Shared,
                new FakeSecureKeyStore(), () => { }, oauth: new ProviderOAuthService(http),
                openUrl: _ => throw new InvalidOperationException("private-browser-error"));
            vm.Providers.Single(card => card.Profile.Id == "poe").OpenCommand.Execute(null);

            await vm.SignInCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(vm.IsWorking);
            Assert.False(vm.IsWaitingForBrowser);
            Assert.True(vm.HasError);
            Assert.DoesNotContain("private-browser-error", vm.Error);
            Assert.Equal(0, exchanges);
        }, CancellationToken.None);
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
