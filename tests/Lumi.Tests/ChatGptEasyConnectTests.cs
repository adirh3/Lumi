using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Byok;
using Lumi.ViewModels;
using Lumi.Views.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ChatGptEasyConnectTests
{
    [Fact]
    public async Task AccountsList_OffersChatGptRightUnderGitHubUntilItIsConnected()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var data = new AppData();
            var store = new DataStore(data);
            using var vm = new EasyConnectViewModel(store, TestCopilot.Shared, new FakeSecureKeyStore(), () => { })
            {
                LoginVM = new GitHubLoginViewModel(TestCopilot.Shared)
            };
            var view = new EasyConnectView { DataContext = vm };
            var window = new Window { Width = 560, Height = 600, Content = view };
            window.Show();
            try
            {
                window.UpdateLayout();
                var offer = view.FindControl<Border>("ChatGptOfferRow")!;
                var button = view.FindControl<Button>("ChatGptConnectButton")!;
                Assert.True(offer.IsEffectivelyVisible);
                Assert.True(button.IsEffectivelyVisible);
                Assert.Contains("ec-chatgpt", button.Classes);
                Assert.Equal(Loc.Get("ChatGpt_Continue"),
                    button.GetVisualDescendants().OfType<TextBlock>().Single().Text);
                var top = (Control control) => control.TranslatePoint(default, view)!.Value.Y;
                Assert.True(top(view.FindControl<Border>("GitHubAccountRow")!) < top(offer));
                Assert.True(top(offer) < top(view.FindControl<Button>("AddAiAccountButton")!));

                vm.OpenGalleryCommand.Execute(null);
                var recommended = vm.ProviderGroups.First();
                Assert.Equal(Loc.Get("EasyConnect_GroupRecommended"), recommended.Title);
                Assert.Contains(recommended.Providers, card => card.Profile.IsChatGpt);
                vm.CloseDialogCommand.Execute(null);

                data.Settings.ByokEndpoints.Add(ChatGptConnectionTests.ChatGptEndpoint("connected"));
                vm.Refresh();
                window.UpdateLayout();
                Assert.False(vm.ShowChatGptOffer);
                Assert.False(offer.IsEffectivelyVisible);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AccountStep_OffersOnlyContinueWithChatGpt()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            using var vm = new EasyConnectViewModel(new DataStore(new AppData()), TestCopilot.Shared,
                new FakeSecureKeyStore(), () => { });
            var view = new EasyConnectDialogView { DataContext = vm };
            var window = new Window { Width = 700, Height = 760, Content = view };
            window.Show();
            try
            {
                vm.Providers.Single(card => card.Profile.IsChatGpt).OpenCommand.Execute(null);
                window.UpdateLayout();
                Assert.True(vm.IsAccountStage);
                Assert.True(view.FindControl<Button>("ChatGptSignIn")!.IsEffectivelyVisible);
                Assert.False(view.FindControl<Button>("ProviderSignIn")!.IsEffectivelyVisible);
                Assert.False(view.FindControl<Button>("UseApiKeyInstead")!.IsEffectivelyVisible);
                Assert.False(view.FindControl<TextBox>("ConnectApiKey")!.IsEffectivelyVisible);
                Assert.Equal(Loc.Get("ChatGpt_SignInHint"), vm.SignInHint);
                Assert.Equal(Loc.Get("EasyConnect_chatgpt_Notice"), vm.SelectedNotice);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SignIn_ConnectsThePlanSavesOnlyTheRefreshTokenAndDisconnectRevokesIt()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = new AppData();
            var keys = new FakeSecureKeyStore();
            using var fake = new FakeChatGpt();
            using var http = new HttpClient(fake);
            using var auth = new ChatGptAuthService(http);
            await using var bridge = new ChatGptPlanBridge(auth, new NoUpstream(), null, null);
            using var local = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            Task<HttpResponseMessage>? approval = null;
            using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared, keys, () => { },
                openUrl: uri =>
                {
                    var query = ChatGptAuthServiceTests.Query(uri.Query);
                    Assert.Equal(data.Settings.ChatGptHostId, query["ext_agent_host_id"]);
                    fake.Nonce = query["nonce"];
                    approval = local.GetAsync(query["redirect_uri"] + "?code=one-time-code&state="
                        + Uri.EscapeDataString(query["state"]) + "&client_id=oaiapp_test");
                },
                chatGpt: auth, bridge: bridge);

            await vm.ConnectChatGptCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(vm.HasError, vm.Error);
            Assert.Equal(HttpStatusCode.OK, (await approval!).StatusCode);
            Assert.StartsWith("urn:uuid:", data.Settings.ChatGptHostId);
            Assert.True(vm.IsModelStage);
            Assert.Equal(["gpt-6.1-sol", "gpt-5.5"], vm.Models.Select(model => model.Id));
            Assert.All(vm.Models, model => Assert.True(model.IsSelected));

            vm.SkipModelCheck = true;
            await vm.SaveConnectionCommand.ExecuteAsync(null);

            Assert.False(vm.HasError, vm.Error);
            Assert.True(vm.ShowPlanConfirmation);
            Assert.Equal(Loc.Get("ChatGpt_DoneTitle"), vm.DoneTitle);
            Assert.Equal(Loc.Get("ChatGpt_GotIt"), vm.DoneActionText);
            Assert.EndsWith(Loc.Get("ChatGpt_DoneUsage"), vm.DoneText);
            var endpoint = Assert.Single(data.Settings.ByokEndpoints);
            Assert.True(ByokConfigHelper.IsChatGptEndpoint(endpoint));
            Assert.Equal(ByokApiKeyMode.CredentialStore, endpoint.ApiKeyMode);
            Assert.Null(endpoint.ApiKey);
            Assert.Null(endpoint.CredentialExpiresAt);
            Assert.Equal(("oaiapp_test", "user-123", "ada@example.com"),
                (endpoint.Account!.ClientId, endpoint.Account.Subject, endpoint.Account.Email));
            Assert.Equal("refresh-1", await keys.GetAsync(SecureKeyStoreFactory.EndpointKey(endpoint.Id)));
            Assert.Equal(1, keys.Count);
            var models = data.Settings.ByokModels.Where(model => model.EndpointId == endpoint.Id).ToList();
            Assert.Equal(["gpt-6.1-sol", "gpt-5.5"], models.Select(model => model.ModelId));
            Assert.Equal(272000, models[0].DefaultContextWindowTokens);
            Assert.Equal(["low", "medium", "high"], models[0].SupportedReasoningEfforts);
            Assert.Equal(ByokConfigHelper.BuildModelToken(models[0]), data.Settings.PreferredModel);

            vm.DoneCommand.Execute(null);
            var card = Assert.Single(vm.Connections);
            Assert.StartsWith("ada@example.com · ", card.Detail);
            await card.ConfirmDisconnectCommand.ExecuteAsync(null);

            Assert.False(vm.HasError, vm.Error);
            Assert.Empty(data.Settings.ByokEndpoints);
            Assert.Equal(0, keys.Count);
            var revoke = Assert.Single(fake.Requests, request => request.Url == ChatGptAuthService.RevocationEndpoint);
            Assert.Equal("refresh-1", revoke.Form["token"]);
            Assert.Equal("oaiapp_test", revoke.Form["client_id"]);
            Assert.True(vm.ShowChatGptOffer);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SignIn_WithoutSecureStorage_ExplainsWhyItCannotConnect()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var opened = false;
            using var vm = new EasyConnectViewModel(new DataStore(new AppData()), TestCopilot.Shared,
                new FakeSecureKeyStore { IsSupported = false }, () => { }, openUrl: _ => opened = true);

            await vm.ConnectChatGptCommand.ExecuteAsync(null);

            Assert.False(opened);
            Assert.Equal(Loc.Get("ChatGpt_StorageRequired"), vm.Error);
            Assert.False(vm.ShowPlaintextConsent);
        }, CancellationToken.None);
    }

    private sealed class NoUpstream : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Inference is not expected while connecting.");
    }
}
