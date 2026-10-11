using System.Net;
using System.Text;
using System.Text.Json;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Byok;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class ProviderConnectionTests
{
    [Fact]
    public void CatalogOffersSupportedApisNotRestrictedConsumerOAuth()
    {
        Assert.Equal(ProviderConnectionKind.ApiKey, ProviderCatalog.Find("anthropic")!.Kind);
        Assert.Null(ProviderCatalog.Find("anthropic")!.OAuth);
        Assert.Null(ProviderCatalog.Find("gemini")!.OAuth);
        var foundry = ProviderCatalog.Find("foundry")!;
        Assert.True(foundry.IsFoundry);
        Assert.Equal(ProviderConnectionKind.ApiKey, foundry.Kind);
        Assert.Equal("openai", foundry.ProviderType);
        Assert.Equal("responses", foundry.WireApi);
        Assert.Equal(ProviderCategory.Apis, foundry.Category);
        Assert.Null(foundry.OAuth);
        Assert.DoesNotContain(ProviderCatalog.All, profile => profile.Id is "claude-pro" or "antigravity");
        Assert.Equal(ProviderOAuthKind.OpenRouter, ProviderCatalog.Find("openrouter")!.OAuth);
        Assert.Equal(ProviderOAuthKind.Poe, ProviderCatalog.Find("poe")!.OAuth);
        Assert.Equal(ProviderCatalog.All.Count, ProviderCatalog.All.Select(profile => profile.Id).Distinct().Count());
        Assert.All(ProviderCatalog.All.Where(profile => !profile.IsCopilot && !profile.IsCustom),
            profile => Assert.False(string.IsNullOrWhiteSpace(profile.Monogram)));
        Assert.Equal(ProviderCategory.Recommended, ProviderCatalog.Find("copilot")!.Category);
        foreach (var profile in ProviderCatalog.All)
        {
            Assert.DoesNotContain("EasyConnect_", profile.Description);
            Assert.DoesNotContain("EasyConnect_", profile.Billing);
            Assert.DoesNotContain("EasyConnect_", profile.Notice);
        }
    }

    [Theory]
    [InlineData("https://api.example.com/v1", "openai", "https://api.example.com/v1/models")]
    [InlineData("https://api.example.com", "anthropic", "https://api.example.com/v1/models")]
    [InlineData("https://api.example.com/anthropic", "anthropic", "https://api.example.com/anthropic/v1/models")]
    [InlineData("http://localhost:11434/v1", "openai", "http://localhost:11434/v1/models")]
    public void ModelsUrlPreservesProviderRoute(string url, string type, string expected)
        => Assert.Equal(expected, ProviderConnectionService.BuildModelsUri(new() { BaseUrl = url, ProviderType = type }).AbsoluteUri);

    [Theory]
    [InlineData("https://resource.services.ai.azure.com", "https://resource.services.ai.azure.com/openai/v1")]
    [InlineData("https://resource.openai.azure.com/", "https://resource.openai.azure.com/openai/v1")]
    [InlineData("https://resource.services.ai.azure.com/openai/v1/", "https://resource.services.ai.azure.com/openai/v1")]
    [InlineData("http://127.0.0.1:9000/", "http://127.0.0.1:9000/openai/v1")]
    public void FoundryNormalizesOnlyTheSupportedV1ResourceEndpoint(string input, string expected)
        => Assert.Equal(expected, ProviderConnectionService.NormalizeFoundryBaseUrl(input));

    [Theory]
    [InlineData("https://resource.services.ai.azure.com/api/projects/project")]
    [InlineData("https://resource.services.ai.azure.com/openai/deployments/deployment")]
    [InlineData("https://resource.services.ai.azure.com/models")]
    [InlineData("https://resource.services.ai.azure.com/openai/v1?api-key=private-key")]
    [InlineData("https://private-key@resource.services.ai.azure.com/openai/v1")]
    [InlineData("http://resource.services.ai.azure.com")]
    public void FoundryRejectsLegacyOrUnsafeEndpointsWithoutEchoingKeys(string input)
    {
        var error = Assert.Throws<ProviderConnectionException>(() => ProviderConnectionService.NormalizeFoundryBaseUrl(input));
        Assert.DoesNotContain("private-key", error.Message);
    }

    [Theory]
    [InlineData("https://resource.services.ai.azure.com/openai/v1", true)]
    [InlineData("https://resource.openai.azure.com/openai/v1/", true)]
    [InlineData("https://resource.openai.azure.com", false)]
    [InlineData("https://resource.services.ai.azure.com.evil.example/openai/v1", false)]
    public void FoundryInferenceRecognizesV1HostsWithoutConvertingLegacyAzureConnections(string url, bool expected)
        => Assert.Equal(expected, ProviderCatalog.FromUrl(url)?.IsFoundry == true);

    [Fact]
    public async Task FoundryDiscoveryUsesTheResourceKeyAndPreservesReturnedModelIds()
    {
        using var http = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal("https://resource.services.ai.azure.com/openai/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("api-key")));
            return new(HttpStatusCode.OK)
            { Content = new StringContent("""{"data":[{"id":"my-deployment","object":"model","owned_by":"owner"}]}""") };
        }));
        var data = new AppData();
        using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared,
            new FakeSecureKeyStore(), () => { }, new ProviderConnectionService(http))
        {
            LoginVM = new GitHubLoginViewModel(TestCopilot.Shared) { IsAuthenticated = true }
        };
        vm.Providers.Single(card => card.Profile.IsFoundry).OpenCommand.Execute(null);
        vm.ApiKey = "test-key";
        Assert.False(vm.CanDiscover);
        vm.BaseUrl = "https://resource.services.ai.azure.com/";
        Assert.True(vm.CanDiscover);
        await vm.DiscoverCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        Assert.True(vm.IsFoundry);
        Assert.True(vm.IsModelStage);
        Assert.False(vm.MakeDefault);
        Assert.True(vm.CanToggleDefault);
        Assert.Equal("my-deployment", Assert.Single(vm.Models).Id);
        Assert.Empty(data.Settings.ByokEndpoints);
        Assert.Empty(data.Settings.ByokModels);
    }

    [Fact]
    public void FoundryCatalogOmitsUnavailableAndNonChatOfferings()
    {
        using var json = JsonDocument.Parse("""
            {"data":[
              {"id":"code-cushman-001","capabilities":{"inference":false,"chat_completion":false}},
              {"id":"fine-tune-only","capabilities":{"inference":false,"chat_completion":true}},
              {"id":"completion-only","capabilities":{"inference":true,"chat_completion":false}},
              {"id":"gpt-4o","capabilities":{"inference":true,"chat_completion":true}}
            ]}
            """);
        Assert.Equal("gpt-4o", Assert.Single(ProviderConnectionService.ParseModels(json.RootElement, "/v1/chat/completions")).Id);
    }

    [Fact]
    public void FoundryResponsesFiltersVideoCatalogEntriesButRetainsExplicitResponsesModels()
    {
        using var json = JsonDocument.Parse("""
            {"data":[
              {"id":"aoai-sora","capabilities":{"inference":true,"chat_completion":false}},
              {"id":"sora-2","capabilities":{"inference":true,"chat_completion":false}},
              {"id":"gpt-6-luna","capabilities":{"inference":true,"chat_completion":true}},
              {"id":"responses-only","capabilities":{"inference":true,"chat_completion":false},
               "supported_endpoints":["/v1/responses"]}
            ]}
            """);
        Assert.Equal(["gpt-6-luna", "responses-only"],
            ProviderConnectionService.ParseModels(json.RootElement, "/v1/responses").Select(model => model.Id));
        Assert.Equal("gpt-6-luna",
            Assert.Single(ProviderConnectionService.ParseModels(json.RootElement, "/v1/chat/completions")).Id);
    }

    [Fact]
    public void FoundryGalleryOpensTheExistingAccountWithoutDuplicating()
    {
        var data = new AppData();
        data.Settings.ByokEndpoints.Add(new()
        {
            Id = "foundry", Name = "My Foundry", ConnectionProviderId = "foundry",
            BaseUrl = "https://resource.services.ai.azure.com/openai/v1", ProviderType = "openai",
            WireApi = "completions", ApiKeyMode = ByokApiKeyMode.Stored, ApiKey = "test-key"
        });
        data.Settings.ByokModels.Add(new() { EndpointId = "foundry", ModelId = "deployment", DisplayName = "Deployment" });
        using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared, new FakeSecureKeyStore(), () => { });
        var card = vm.Providers.Single(provider => provider.Profile.IsFoundry);

        Assert.True(card.IsConnected);
        card.OpenCommand.Execute(null);
        Assert.True(vm.IsFoundry);
        Assert.True(vm.IsManaging);
        Assert.True(vm.IsModelStage);
        Assert.Equal("completions", vm.WireApi);
        Assert.Single(vm.Models);
        Assert.Single(data.Settings.ByokEndpoints);
    }

    [Theory]
    [InlineData("gpt-6-luna", "responses", "low,medium,high,xhigh,max")]
    [InlineData("gpt-6-luna-2026-09-22", "responses", "low,medium,high,xhigh,max")]
    [InlineData("gpt-6-luna", "completions", "")]
    [InlineData("gpt-5.4-mini", "responses", "low,medium,high,xhigh")]
    [InlineData("gpt-5.1", "completions", "low,medium,high")]
    [InlineData("gpt-5", "responses", "minimal,low,medium,high")]
    [InlineData("gpt-5-pro", "responses", "high")]
    public void FoundryUsesDocumentedEffortsForRecognizedModels(string modelId, string wireApi, string expected)
    {
        var model = ProviderConnectionService.WithFoundryReasoning(new(modelId, modelId), wireApi);
        Assert.NotNull(model.SupportedReasoningEfforts);
        Assert.Equal(expected, string.Join(",", model.SupportedReasoningEfforts));
        Assert.DoesNotContain("none", model.SupportedReasoningEfforts);
    }

    [Fact]
    public void FoundryReasoningDoesNotGuessDeploymentAliasesOrOverrideExplicitMetadata()
    {
        var alias = new ProviderModel("my-luna-deployment", "GPT-6 Luna");
        Assert.Same(alias, ProviderConnectionService.WithFoundryReasoning(alias, "responses"));
        var reported = new ProviderModel("gpt-6-luna", "GPT-6 Luna",
            SupportedReasoningEfforts: ["low", "high"], DefaultReasoningEffort: "low");
        Assert.Same(reported, ProviderConnectionService.WithFoundryReasoning(reported, "responses"));
        var disabled = new ProviderModel("gpt-6-luna", "GPT-6 Luna", SupportedReasoningEfforts: []);
        Assert.Same(disabled, ProviderConnectionService.WithFoundryReasoning(disabled, "responses"));
    }

    [Fact]
    public async Task FoundryReasoningReachesPersistenceChatAndSettingsWithoutGitHub()
    {
        var data = new AppData();
        using var http = new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK)
        { Content = new StringContent("""{"data":[{"id":"gpt-6-luna","capabilities":{"inference":true,"chat_completion":true}}]}""") }));
        var store = new DataStore(data);
        using var connect = new EasyConnectViewModel(store, TestCopilot.Shared,
            new FakeSecureKeyStore(), () => { }, new ProviderConnectionService(http));
        connect.Providers.Single(card => card.Profile.IsFoundry).OpenCommand.Execute(null);
        connect.BaseUrl = "https://resource.services.ai.azure.com";
        connect.ApiKey = "test-key";
        await connect.DiscoverCommand.ExecuteAsync(null);
        var choice = Assert.Single(connect.Models);
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], choice.Model.SupportedReasoningEfforts);
        Assert.False(choice.IsSelected);

        connect.WireApi = "completions";
        Assert.Empty(choice.Model.SupportedReasoningEfforts!);
        connect.WireApi = "responses";
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], choice.Model.SupportedReasoningEfforts);
        choice.IsSelected = true;
        connect.SkipModelCheck = true;
        await connect.SaveConnectionCommand.ExecuteAsync(null);
        Assert.False(connect.HasError);
        var saved = Assert.Single(data.Settings.ByokModels);
        Assert.True(saved.SupportsReasoningEffort);
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], saved.SupportedReasoningEfforts);
        Assert.True(ByokConfigHelper.BuildModelCapabilitiesOverride(saved, null)!.Supports!.ReasoningEffort);
        var snapshot = AppDataSnapshotFactory.CreateIndexSnapshot(data);
        Assert.Equal(saved.SupportedReasoningEfforts, Assert.Single(snapshot.Settings.ByokModels).SupportedReasoningEfforts);

        using var main = new MainViewModel(store, TestCopilot.Shared, new UpdateService());
        typeof(MainViewModel).GetMethod("InjectByokModels",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, []);
        var token = ByokConfigHelper.BuildModelToken(saved);
        Assert.Equal(token, main.ChatVM.SelectedModel);
        Assert.Equal(["Low", "Medium", "High", "X-High", "Max"], Assert.IsType<string[]>(main.ChatVM.QualityLevels));
        Assert.Equal(["Low", "Medium", "High", "X-High", "Max"], Assert.IsType<string[]>(main.SettingsVM.QualityLevels));
    }

    [Fact]
    public async Task RefreshingFoundryAddsMissingReasoningWithoutReplacingConfiguredEfforts()
    {
        var data = new AppData();
        data.Settings.ByokEndpoints.Add(new()
        {
            Id = "foundry", Name = "Foundry", ConnectionProviderId = "foundry",
            BaseUrl = "https://resource.services.ai.azure.com/openai/v1", WireApi = "responses",
            ApiKeyMode = ByokApiKeyMode.Stored, ApiKey = "test-key"
        });
        var missing = new ByokModel { EndpointId = "foundry", ModelId = "gpt-6-luna", DisplayName = "Luna" };
        var configured = new ByokModel
        {
            EndpointId = "foundry", ModelId = "gpt-6-sol", DisplayName = "Sol",
            SupportsReasoningEffort = true, SupportedReasoningEfforts = ["low", "high"], DefaultReasoningEffort = "low"
        };
        data.Settings.ByokModels.AddRange([missing, configured]);
        using var http = new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK)
        { Content = new StringContent("""{"data":[{"id":"gpt-6-luna"},{"id":"gpt-6-sol"}]}""") }));
        var keys = new FakeSecureKeyStore();
        var saves = 0;
        using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared,
            keys, () => saves++, new ProviderConnectionService(http));
        vm.Connections[0].ManageCommand.Execute(null);
        await vm.DiscoverCommand.ExecuteAsync(null);

        Assert.True(missing.SupportsReasoningEffort);
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], missing.SupportedReasoningEfforts);
        Assert.Equal(["low", "high"], configured.SupportedReasoningEfforts);
        Assert.Equal("low", configured.DefaultReasoningEffort);
        Assert.Equal(["low", "high"], vm.Models.Single(choice => choice.Id == "gpt-6-sol").Model.SupportedReasoningEfforts);
        Assert.All(vm.Models, choice => Assert.True(choice.IsSelected));
        Assert.Equal(1, saves);
        Assert.Equal(0, keys.SetCallCount);
    }

    [Theory]
    [InlineData("http://provider.example/v1")]
    [InlineData("https://user:secret@provider.example/v1")]
    [InlineData("https://provider.example/v1?key=secret")]
    [InlineData("https://provider.example/v1#fragment")]
    public void UnsafeOrAmbiguousUrlsAreRejected(string url)
        => Assert.Throws<ProviderConnectionException>(() => ProviderConnectionService.BuildModelsUri(new() { BaseUrl = url }));

    [Fact]
    public void DiscoveryDeduplicatesAndFiltersNonToolModels()
    {
        using var json = JsonDocument.Parse("""
            {"data":[
              {"id":"model-paid","name":"Paid","context_length":131072,"supported_parameters":["tools"]},
              {"id":"model-free:free","name":"Free","supported_parameters":["tools"]},
              {"id":"model-no-tools","supported_parameters":["temperature"]},
              {"id":"model-paid"},
              {"id":"text-embedding-3-large"},
              {"id":"manual-unknown","context_length":"unknown"},
              null
            ]}
            """);
        var models = ProviderConnectionService.ParseModels(json.RootElement);
        Assert.Equal(["model-free:free", "model-paid", "manual-unknown"], models.Select(model => model.Id));
        Assert.Equal(131072, models[1].ContextTokens);
    }

    [Fact]
    public async Task DiscoveryUsesOnlySuppliedApiKeyAndReturnsAuthFailureWithoutSecret()
    {
        using var http = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            return new(HttpStatusCode.Unauthorized) { Content = new StringContent("secret response test-key") };
        }));
        using var service = new ProviderConnectionService(http);
        var error = await Assert.ThrowsAsync<ProviderConnectionException>(() =>
            service.DiscoverAsync(new() { BaseUrl = "https://provider.example/v1", ProviderType = "openai" }, "test-key", default));
        Assert.DoesNotContain("test-key", error.Message);
        Assert.DoesNotContain("secret response", error.Message);
    }

    [Fact]
    public void PoeCatalogUsesCapabilitiesEndpointsAndZeroPriceInsteadOfSelectingVideoBots()
    {
        using var json = JsonDocument.Parse("""
            {"data":[
              {"id":"video-model","architecture":{"output_modalities":["video"]},"supported_features":[],"supported_endpoints":[]},
              {"id":"responses-only","supported_features":["tools"],"supported_endpoints":["/v1/responses"]},
              {"id":"paid-chat","supported_features":["tools"],"supported_endpoints":["/v1/chat/completions"],"pricing":{"prompt":"0.001"}},
              {"id":"free-chat","supported_features":["tools"],"supported_endpoints":["/v1/chat/completions"],"pricing":{"prompt":null,"completion":null,"request":"0.00"},"metadata":{"display_name":"Free Chat"}}
            ]}
            """);
        var models = ProviderConnectionService.ParseModels(json.RootElement, "/v1/chat/completions");
        Assert.Equal(["free-chat", "paid-chat"], models.Select(model => model.Id));
        Assert.True(models[0].IsFree);
        Assert.Equal("Free Chat", models[0].Name);
        Assert.False(models[1].IsFree);
    }

    [Fact]
    public void MistralCatalogReadsNativeToolCapabilitiesAndContextLength()
    {
        using var json = JsonDocument.Parse("""
            {"data":[
              {"id":"mistral-small-latest","max_context_length":262144,"capabilities":{"completion_chat":true,"function_calling":true}},
              {"id":"codestral-fim","capabilities":{"completion_chat":false,"function_calling":false}},
              {"id":"mistral-text","capabilities":{"completion_chat":true,"function_calling":false}}
            ]}
            """);
        var model = Assert.Single(ProviderConnectionService.ParseModels(json.RootElement));
        Assert.Equal("mistral-small-latest", model.Id);
        Assert.True(model.SupportsTools);
        Assert.Equal(262144, model.ContextTokens);
    }

    [Fact]
    public void OpenRouterCatalogPreservesOnlyExplicitReasoningEfforts()
    {
        using var json = JsonDocument.Parse("""
            {"data":[
              {"id":"openai/gpt-5","supported_parameters":["tools","reasoning","reasoning_effort"],
               "reasoning":{"supported_efforts":["high","medium","low","minimal"],"default_effort":"medium"}},
              {"id":"thinking-model:free","supported_parameters":["tools","reasoning_effort"],
               "reasoning":{"supported_efforts":["max","high","medium","low","none","invalid","high"],"default_effort":"high"}},
              {"id":"budget-only","supported_parameters":["tools","reasoning"],
               "reasoning":{"mandatory":true}},
              {"id":"explicit-empty","reasoning":{"supported_efforts":[],"default_effort":"high"}}
            ]}
            """);
        var models = ProviderConnectionService.ParseModels(json.RootElement);
        var gpt = models.Single(model => model.Id == "openai/gpt-5");
        Assert.Equal(["minimal", "low", "medium", "high"], gpt.SupportedReasoningEfforts);
        Assert.Equal("medium", gpt.DefaultReasoningEffort);
        Assert.Equal(["low", "medium", "high", "max"],
            models.Single(model => model.Id == "thinking-model:free").SupportedReasoningEfforts);
        Assert.Null(models.Single(model => model.Id == "budget-only").SupportedReasoningEfforts);
        Assert.Empty(models.Single(model => model.Id == "explicit-empty").SupportedReasoningEfforts!);
    }

    [Fact]
    public void CompatibleCatalogReadsExplicitEffortListsWithoutGuessingUnsupportedValues()
    {
        using var json = JsonDocument.Parse("""
            {"data":[{"id":"compatible-model","supported_reasoning_efforts":["HIGH","low","none","unknown"],
              "default_reasoning_effort":"high"}]}
            """);
        var model = Assert.Single(ProviderConnectionService.ParseModels(json.RootElement));
        Assert.Equal(["low", "high"], model.SupportedReasoningEfforts);
        Assert.Equal("high", model.DefaultReasoningEffort);
    }

    [Fact]
    public async Task RefreshingAnExistingAccountUpdatesReasoningWithoutChangingSelectionOrKey()
    {
        var data = new AppData();
        data.Settings.ByokEndpoints.Add(new()
        {
            Id = "endpoint", Name = "OpenRouter", ConnectionProviderId = "openrouter",
            BaseUrl = "http://localhost:1234/v1", ProviderType = "openai", ApiKeyMode = ByokApiKeyMode.None
        });
        var model = new ByokModel { Id = "entry", EndpointId = "endpoint", ModelId = "wire-model", DisplayName = "OpenRouter · Model" };
        data.Settings.ByokModels.Add(model);
        data.Settings.PreferredModel = "byok:entry";
        var keys = new FakeSecureKeyStore();
        var saves = 0;
        using var http = new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"data":[{"id":"wire-model","supported_parameters":["tools"],
                 "reasoning":{"supported_efforts":["high","medium","low"],"default_effort":"medium"}}]}
                """)
        }));
        using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared, keys,
            () => saves++, new ProviderConnectionService(http));
        vm.Connections[0].ManageCommand.Execute(null);
        await vm.DiscoverCommand.ExecuteAsync(null);

        Assert.True(model.SupportsReasoningEffort);
        Assert.Equal(["low", "medium", "high"], model.SupportedReasoningEfforts);
        Assert.Equal("medium", model.DefaultReasoningEffort);
        Assert.Equal("byok:entry", data.Settings.PreferredModel);
        Assert.True(model.IsEnabled);
        Assert.Equal(1, saves);
        Assert.Equal(0, keys.SetCallCount);
        Assert.True(vm.IsModelStage);
        Assert.True(Assert.Single(vm.Models).IsSelected);
        var snapshot = AppDataSnapshotFactory.CreateIndexSnapshot(data);
        Assert.Equal(model.SupportedReasoningEfforts, Assert.Single(snapshot.Settings.ByokModels).SupportedReasoningEfforts);
        Assert.True(ByokConfigHelper.BuildModelCapabilitiesOverride(model, null)!.Supports!.ReasoningEffort);
    }

    [Fact]
    public void UnreportedReasoningDoesNotEraseManualCapabilities()
    {
        var model = new ByokModel
        { SupportsReasoningEffort = true, SupportedReasoningEfforts = ["low", "high"], DefaultReasoningEffort = "high" };
        Assert.False(EasyConnectViewModel.ApplyDiscoveredReasoning(model, new("manual-model", "Manual")));
        Assert.Equal(["low", "high"], model.SupportedReasoningEfforts);
        Assert.Equal("high", model.DefaultReasoningEffort);
        Assert.True(EasyConnectViewModel.ApplyDiscoveredReasoning(model,
            new("manual-model", "Manual", SupportedReasoningEfforts: [])));
        Assert.False(model.SupportsReasoningEffort);
        Assert.Empty(model.SupportedReasoningEfforts);
        Assert.Null(model.DefaultReasoningEffort);
    }


    [Fact]
    public void GeminiDiscoveryCarriesContextAndRemovesNativeNamePrefix()
    {
        using var json = JsonDocument.Parse("""
            {"models":[
              {"name":"models/gemini-flash","displayName":"Gemini Flash","inputTokenLimit":1048576,"supportedGenerationMethods":["generateContent","countTokens"]},
              {"name":"models/text-embedding","supportedGenerationMethods":["embedContent"]},
              {"name":"models/gemini-flash-image","supportedGenerationMethods":["generateContent"]}
            ]}
            """);
        var model = Assert.Single(ProviderConnectionService.ParseGeminiModels(json.RootElement));
        Assert.Equal("gemini-flash", model.Id);
        Assert.Equal(1048576, model.ContextTokens);
    }

    [Fact]
    public async Task GeminiDiscoveryUsesNativeMetadataButRetainsOpenAiInferenceEndpoint()
    {
        using var http = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal("generativelanguage.googleapis.com", request.RequestUri!.Host);
            Assert.Equal("/v1beta/models", request.RequestUri.AbsolutePath);
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            Assert.Null(request.Headers.Authorization);
            return new(HttpStatusCode.OK)
            { Content = new StringContent("""{"models":[{"name":"models/gemini-flash","inputTokenLimit":1048576,"supportedGenerationMethods":["generateContent"]}]}""") };
        }));
        using var service = new ProviderConnectionService(http);
        var endpoint = new ByokEndpoint
        { BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai", ProviderType = "openai" };
        var model = Assert.Single(await service.DiscoverAsync(endpoint, "test-key", default));
        Assert.Equal("gemini-flash", model.Id);
        Assert.EndsWith("/openai", endpoint.BaseUrl);
    }

    [Fact]
    public async Task ModelSelectionDoesNotPersistKeyBeforeVerification()
    {
        var data = new AppData();
        var store = new DataStore(data);
        var keys = new FakeSecureKeyStore();
        using var http = new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK)
        { Content = new StringContent("""{"data":[{"id":"test-model","name":"Test"}]}""", Encoding.UTF8, "application/json") }));
        using var vm = new EasyConnectViewModel(store, TestCopilot.Shared, keys, () => { },
            new ProviderConnectionService(http));
        vm.Providers.Single(card => card.Profile.Id == "openai").OpenCommand.Execute(null);
        vm.ApiKey = "test-key";
        await vm.DiscoverCommand.ExecuteAsync(null);
        Assert.True(vm.HasModels);
        Assert.True(vm.IsModelStage);
        Assert.False(vm.CanSave);
        Assert.False(Assert.Single(vm.Models).IsSelected);
        vm.Models[0].IsSelected = true;
        Assert.True(vm.CanSave);
        Assert.Empty(data.Settings.ByokEndpoints);
        Assert.Empty(data.Settings.ByokModels);
        Assert.Equal(0, keys.Count);
        vm.CloseDialogCommand.Execute(null);
        Assert.Empty(vm.ApiKey);
        Assert.Empty(vm.Models);
        Assert.True(vm.IsGallery);
    }

    [Theory]
    [InlineData("foundry")]
    [InlineData("openai")]
    [InlineData("custom")]
    public async Task DiscoveryNeverSelectsAModelImplicitly(string provider)
    {
        var data = new AppData();
        using var http = new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK)
        { Content = new StringContent("""{"data":[{"id":"first-model"},{"id":"second-model"}]}""") }));
        using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared,
            new FakeSecureKeyStore(), () => { }, new ProviderConnectionService(http));
        vm.Providers.Single(card => card.Profile.Id == provider).OpenCommand.Execute(null);
        vm.BaseUrl = provider == "foundry" ? "https://resource.services.ai.azure.com" : "https://provider.example/v1";
        vm.ApiKey = "test-key";
        await vm.DiscoverCommand.ExecuteAsync(null);
        Assert.All(vm.Models, choice => Assert.False(choice.IsSelected));
        Assert.False(vm.CanSave);
        vm.SkipModelCheck = true;
        await vm.SaveConnectionCommand.ExecuteAsync(null);
        Assert.Empty(data.Settings.ByokModels);

        vm.Models.Single(choice => choice.Id == "second-model").IsSelected = true;
        await vm.DiscoverCommand.ExecuteAsync(null);
        Assert.Equal("second-model", Assert.Single(vm.Models, choice => choice.IsSelected).Id);

        foreach (var choice in vm.Models)
            choice.IsSelected = false;
        await vm.DiscoverCommand.ExecuteAsync(null);
        Assert.All(vm.Models, choice => Assert.False(choice.IsSelected));
        Assert.False(vm.CanSave);
    }

    [Fact]
    public async Task SkippingModelCheckSavesSecurelyWithoutInferenceOrChangingGitHubDefault()
    {
        var data = new AppData();
        data.Settings.PreferredModel = "copilot-model";
        var keys = new FakeSecureKeyStore();
        var saves = 0;
        using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared, keys, () => saves++)
        {
            LoginVM = new GitHubLoginViewModel(TestCopilot.Shared) { IsAuthenticated = true }
        };
        vm.Providers.Single(card => card.Profile.Id == "openai").OpenCommand.Execute(null);
        vm.ApiKey = "test-key";
        vm.ManualModel = "explicit-model";
        vm.AddModelCommand.Execute(null);
        Assert.False(vm.SkipModelCheck);
        vm.SkipModelCheck = true;
        await vm.SaveConnectionCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        Assert.True(vm.IsComplete);
        var endpoint = Assert.Single(data.Settings.ByokEndpoints);
        Assert.Equal(ByokApiKeyMode.CredentialStore, endpoint.ApiKeyMode);
        Assert.Null(endpoint.ApiKey);
        Assert.Equal("test-key", await keys.GetAsync(SecureKeyStoreFactory.EndpointKey(endpoint.Id)));
        Assert.Equal("explicit-model", Assert.Single(data.Settings.ByokModels).ModelId);
        Assert.Equal("copilot-model", data.Settings.PreferredModel);
        Assert.Equal(string.Format(Lumi.Localization.Loc.Get("EasyConnect_DoneUnverifiedAvailable"), "explicit-model"), vm.DoneText);
        Assert.Equal(1, saves);
        vm.CloseDialogCommand.Execute(null);
        Assert.False(vm.SkipModelCheck);
        Assert.Empty(vm.ApiKey);
    }

    [Fact]
    public async Task SkippingModelCheckStillRequiresAValidUrlAndPlaintextConsent()
    {
        var data = new AppData();
        var keys = new FakeSecureKeyStore { IsSupported = false };
        using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared, keys, () => { });
        vm.Providers.Single(card => card.Profile.IsCustom).OpenCommand.Execute(null);
        vm.BaseUrl = "https://provider.example/v1";
        vm.ApiKey = "test-key";
        vm.ManualModel = "explicit-model";
        vm.AddModelCommand.Execute(null);
        vm.SkipModelCheck = true;
        await vm.SaveConnectionCommand.ExecuteAsync(null);
        Assert.Equal(Lumi.Localization.Loc.Get("EasyConnect_StorageConsentRequired"), vm.Error);
        Assert.Empty(data.Settings.ByokEndpoints);

        vm.AllowPlaintextStorage = true;
        vm.BaseUrl = "https://provider.example/v1?key=private-key";
        await vm.SaveConnectionCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);
        Assert.DoesNotContain("private-key", vm.Error);
        Assert.Empty(data.Settings.ByokEndpoints);
        Assert.Equal(0, keys.SetCallCount);

        vm.BaseUrl = "https://provider.example/v1";
        await vm.SaveConnectionCommand.ExecuteAsync(null);
        Assert.False(vm.HasError);
        var endpoint = Assert.Single(data.Settings.ByokEndpoints);
        Assert.Equal(ByokApiKeyMode.Stored, endpoint.ApiKeyMode);
        Assert.Equal("test-key", endpoint.ApiKey);
        Assert.Equal(string.Format(Lumi.Localization.Loc.Get("EasyConnect_DoneUnverifiedDefault"), "explicit-model"), vm.DoneText);
    }

    [Fact]
    public void HelperSessionsUseExternalProviderWithoutGitHubFallback()
    {
        var settings = new UserSettings { UseBYOKOnly = true };
        var model = new ByokModel { Id = "model", EndpointId = "endpoint", ModelId = "external-model", DisplayName = "External" };
        settings.ByokEndpoints.Add(new()
        {
            Id = "endpoint", Name = "Provider", BaseUrl = "https://provider.example/v1",
            ProviderType = "openai", WireApi = "completions", ApiKeyMode = ByokApiKeyMode.Stored, ApiKey = "test-key"
        });
        settings.ByokModels.Add(model);
        settings.PreferredModel = ByokConfigHelper.BuildModelToken(model);
        var selection = CopilotService.ResolveConfiguredBackgroundModel(settings, isGitHubAuthenticated: false);
        Assert.Equal(settings.PreferredModel, selection);
        var route = ByokConfigHelper.ResolveSessionModelRoute(settings, selection, keyStore: new FakeSecureKeyStore());
        Assert.Equal("external-model", route.WireModelId);
        Assert.Equal("https://provider.example/v1", route.Provider!.BaseUrl);
        Assert.Equal("test-key", route.Provider.ApiKey);
    }

    [Fact]
    public void ValidExternalDefaultSurvivesTheCopilotCatalog()
    {
        var data = new AppData { Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false } };
        data.Settings.ByokEndpoints.Add(new()
        {
            Id = "endpoint", Name = "OpenRouter", ConnectionProviderId = "openrouter", BaseUrl = "https://openrouter.ai/api/v1",
            ProviderType = "openai", WireApi = "completions", ApiKeyMode = ByokApiKeyMode.None
        });
        var model = new ByokModel { Id = "entry", EndpointId = "endpoint", ModelId = "model", DisplayName = "OpenRouter · Model" };
        data.Settings.ByokModels.Add(model);
        var token = ByokConfigHelper.BuildModelToken(model);
        data.Settings.PreferredModel = token;
        using var vm = new MainViewModel(new DataStore(data), TestCopilot.Shared, new UpdateService());

        typeof(MainViewModel).GetMethod("ApplyModelCatalog", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(vm, [
                new List<GitHub.Copilot.ModelInfo> { new() { Id = "claude-sonnet-4.5", Name = "Claude Sonnet 4.5" } },
                new ModelContextWindowCatalog(new HashSet<string>(), new Dictionary<string, ModelContextWindowLimits>()),
                true]);

        Assert.Equal(token, vm.SettingsVM.PreferredModel);
        Assert.Equal(token, vm.ChatVM.SelectedModel);
        Assert.Contains(token, vm.SettingsVM.AvailableModels);
        Assert.Contains("claude-sonnet-4.5", vm.SettingsVM.AvailableModels);
        var option = vm.SettingsVM.ModelOptions.Single(option => option.Id == token);
        Assert.Equal("OpenRouter", option.ProviderName);
        Assert.Equal("Model", option.DisplayName);
        typeof(MainViewModel).GetMethod("InjectByokModels",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(vm, []);
        Assert.Equal("Model", ChatViewModel.FormatModelDisplay(token));
    }

    [Fact]
    public async Task ByokOnlyNeverLabelsOrSelectsGitHubAsTheDefault()
    {
        var data = new AppData { Settings = new UserSettings { UseBYOKOnly = true, PreferredModel = "byok:external" } };
        using var vm = new EasyConnectViewModel(new DataStore(data),
            TestCopilot.Shared, new FakeSecureKeyStore(), () => { })
        {
            LoginVM = new GitHubLoginViewModel(TestCopilot.Shared) { IsAuthenticated = true },
            CopilotModels = () => ["claude-sonnet-4.5"]
        };
        Assert.False(vm.IsGitHubDefault);
        Assert.False(vm.CanMakeGitHubDefault);
        await vm.MakeGitHubDefaultCommand.ExecuteAsync(null);
        Assert.Equal("byok:external", data.Settings.PreferredModel);
    }

    [Fact]
    public void ReverifyingWithoutANewKeyKeepsTheAccountsKeySource()
    {
        var original = new ByokEndpoint
        {
            ApiKeyMode = ByokApiKeyMode.EnvVar, ApiKeyEnvVar = "LUMI_PROVIDER_KEY",
            CredentialExpiresAt = DateTimeOffset.UtcNow.AddDays(2)
        };
        var draft = new ByokEndpoint { ApiKeyMode = ByokApiKeyMode.Stored, ApiKey = "resolved-secret" };
        EasyConnectViewModel.PreserveSavedKey(draft, original);
        Assert.Equal(ByokApiKeyMode.EnvVar, draft.ApiKeyMode);
        Assert.Null(draft.ApiKey);
        Assert.Equal("LUMI_PROVIDER_KEY", draft.ApiKeyEnvVar);
        Assert.Equal(original.CredentialExpiresAt, draft.CredentialExpiresAt);
    }

    [Fact]
    public void BackingOutOfReplaceKeyDiscardsTheTypedKey()
    {
        var data = new AppData();
        data.Settings.ByokEndpoints.Add(new()
        {
            Id = "endpoint", Name = "OpenRouter", ConnectionProviderId = "openrouter", BaseUrl = "https://openrouter.ai/api/v1",
            ApiKeyMode = ByokApiKeyMode.CredentialStore
        });
        data.Settings.ByokModels.Add(new() { EndpointId = "endpoint", ModelId = "model", DisplayName = "OpenRouter · Model" });
        using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared, new FakeSecureKeyStore(), () => { });
        Assert.Single(vm.Connections).ManageCommand.Execute(null);
        vm.ReplaceKeyCommand.Execute(null);
        Assert.True(vm.IsAccountStage);
        vm.ApiKey = "typed-but-abandoned";
        vm.BackCommand.Execute(null);
        Assert.True(vm.IsModelStage);
        Assert.Empty(vm.ApiKey);
    }

    [Fact]
    public async Task InvalidExternalHelperModelFailsExplicitly()
        => await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestCopilot.Shared.BuildLightweightSessionConfigAsync(
                new() { SystemPrompt = "helper", Model = "byok:missing" }));

    [Fact]
    public void SignedInGitHubKeepsHelpersOnCopilotWhenAnotherAccountIsDefault()
    {
        var settings = new UserSettings();
        var model = new ByokModel { Id = "model", EndpointId = "endpoint", ModelId = "external-model", DisplayName = "External" };
        settings.ByokEndpoints.Add(new()
        {
            Id = "endpoint", Name = "Provider", BaseUrl = "https://provider.example/v1",
            ProviderType = "openai", WireApi = "completions", ApiKeyMode = ByokApiKeyMode.Stored, ApiKey = "test-key"
        });
        settings.ByokModels.Add(model);
        settings.PreferredModel = ByokConfigHelper.BuildModelToken(model);

        Assert.False(CopilotService.UsesExternalRuntime(settings, isGitHubAuthenticated: true));
        Assert.Null(CopilotService.ResolveConfiguredBackgroundModel(settings, isGitHubAuthenticated: true));

        Assert.True(CopilotService.UsesExternalRuntime(settings, isGitHubAuthenticated: false));
        Assert.Equal(settings.PreferredModel,
            CopilotService.ResolveConfiguredBackgroundModel(settings, isGitHubAuthenticated: false));

        settings.BackgroundModel = "chosen-copilot-helper";
        Assert.Equal("chosen-copilot-helper",
            CopilotService.ResolveConfiguredBackgroundModel(settings, isGitHubAuthenticated: false));
        settings.BackgroundModel = "byok:chosen-helper";
        Assert.Equal("byok:chosen-helper",
            CopilotService.ResolveConfiguredBackgroundModel(settings, isGitHubAuthenticated: true));
    }

    [Fact]
    public void NewConnectionKeepsSignedInGitHubAsTheDefault()
    {
        var data = new AppData();
        data.Settings.PreferredModel = "copilot-model";
        using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared, new FakeSecureKeyStore(), () => { })
        {
            LoginVM = new GitHubLoginViewModel(TestCopilot.Shared) { IsAuthenticated = true }
        };
        Assert.True(vm.IsGitHubDefault);
        vm.Providers.Single(card => card.Profile.Id == "openai").OpenCommand.Execute(null);
        Assert.False(vm.MakeDefault);
        Assert.True(vm.CanToggleDefault);
        Assert.DoesNotContain(vm.Providers, card => card.Profile.IsCopilot);
    }

    [Fact]
    public void FirstConnectionWithoutGitHubMustBecomeTheDefault()
    {
        using var vm = new EasyConnectViewModel(new DataStore(new AppData()), TestCopilot.Shared, new FakeSecureKeyStore(), () => { })
        {
            LoginVM = new GitHubLoginViewModel(TestCopilot.Shared)
        };
        Assert.Equal("copilot", vm.ProviderGroups[0].Providers.Single().Profile.Id);
        vm.Providers.Single(card => card.Profile.Id == "openai").OpenCommand.Execute(null);
        Assert.True(vm.MakeDefault);
        Assert.False(vm.CanToggleDefault);
    }

    [Fact]
    public void DisabledAccountDoesNotOfferUseAsDefault()
    {
        var data = new AppData();
        data.Settings.ByokEndpoints.Add(new()
        {
            Id = "disabled", Name = "Local", ProviderType = "openai", BaseUrl = "http://localhost:1234/v1",
            ApiKeyMode = ByokApiKeyMode.None, IsEnabled = false
        });
        data.Settings.ByokModels.Add(new() { EndpointId = "disabled", ModelId = "model", DisplayName = "Local model" });
        using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared,
            new FakeSecureKeyStore(), () => { });

        Assert.False(Assert.Single(vm.Connections).CanMakeDefault);
    }

    [Fact]
    public void ConnectedProviderCardOpensItsConnectionInsteadOfDuplicating()
    {
        var data = new AppData();
        data.Settings.ByokEndpoints.Add(new()
        {
            Id = "endpoint", Name = "OpenRouter", ConnectionProviderId = "openrouter", BaseUrl = "https://openrouter.ai/api/v1"
        });
        data.Settings.ByokModels.Add(new() { EndpointId = "endpoint", ModelId = "model", DisplayName = "OpenRouter · Model" });
        using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared, new FakeSecureKeyStore(), () => { });
        var card = vm.Providers.Single(provider => provider.Profile.Id == "openrouter");
        Assert.True(card.IsConnected);
        card.OpenCommand.Execute(null);
        Assert.True(vm.IsManaging);
        Assert.True(vm.IsModelStage);
        Assert.False(vm.IsCurrentDefault);
        Assert.Single(data.Settings.ByokEndpoints);
    }

    [Fact]
    public async Task DisconnectingTheDefaultHandsItBackToSignedInGitHub()
    {
        var data = new AppData();
        var model = new ByokModel { EndpointId = "endpoint", ModelId = "model", DisplayName = "OpenRouter · Model" };
        data.Settings.ByokEndpoints.Add(new()
        {
            Id = "endpoint", Name = "OpenRouter", ConnectionProviderId = "openrouter", BaseUrl = "https://openrouter.ai/api/v1",
            ApiKeyMode = ByokApiKeyMode.CredentialStore
        });
        data.Settings.ByokModels.Add(model);
        data.Settings.PreferredModel = ByokConfigHelper.BuildModelToken(model);
        var keys = new FakeSecureKeyStore();
        await keys.SetAsync(Lumi.Services.Byok.SecureKeyStoreFactory.EndpointKey("endpoint"), "test-key");
        using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared, keys, () => { })
        {
            LoginVM = new GitHubLoginViewModel(TestCopilot.Shared) { IsAuthenticated = true },
            CopilotModels = () => ["claude-sonnet-4.5", ByokConfigHelper.BuildModelToken(model)]
        };
        var card = Assert.Single(vm.Connections);
        Assert.True(card.IsDefault);
        Assert.False(vm.IsGitHubDefault);
        card.RequestDisconnectCommand.Execute(null);
        Assert.True(card.IsConfirmingDisconnect);
        await card.ConfirmDisconnectCommand.ExecuteAsync(null);
        Assert.Empty(data.Settings.ByokEndpoints);
        Assert.Equal(0, keys.Count);
        Assert.Equal("claude-sonnet-4.5", data.Settings.PreferredModel);
        Assert.True(vm.IsGitHubDefault);
    }

    [Fact]
    public async Task CustomLocalServerCanDiscoverWithoutAnApiKey()
    {
        using var http = new HttpClient(new StubHandler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            return new(HttpStatusCode.OK)
            { Content = new StringContent("""{"data":[{"id":"local-model","max_model_len":131072}]}""") };
        }));
        using var vm = new EasyConnectViewModel(new DataStore(new AppData()), TestCopilot.Shared,
            new FakeSecureKeyStore(), () => { }, new ProviderConnectionService(http));
        vm.Providers.Single(card => card.Profile.Id == "custom").OpenCommand.Execute(null);
        vm.BaseUrl = "http://localhost:9000/v1";
        vm.RequiresApiKey = false;
        Assert.True(vm.CanDiscover);
        await vm.DiscoverCommand.ExecuteAsync(null);
        Assert.False(vm.HasKey);
        Assert.Equal(131072, Assert.Single(vm.Models).Model.ContextTokens);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task ProviderWithoutDiscoveryOffersManualModelEntry(HttpStatusCode status)
    {
        var data = new AppData();
        using var http = new HttpClient(new StubHandler(_ => new(status)));
        using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared,
            new FakeSecureKeyStore(), () => { }, new ProviderConnectionService(http));
        vm.Providers.Single(card => card.Profile.IsCustom).OpenCommand.Execute(null);
        vm.BaseUrl = "https://provider.example/v1";
        vm.ApiKey = "test-key";

        await vm.DiscoverCommand.ExecuteAsync(null);

        Assert.True(vm.IsModelStage);
        Assert.Equal(Lumi.Localization.Loc.Get("EasyConnect_NoModelDiscovery"), vm.Status);
        Assert.True(vm.IsAddingManualModel);
        Assert.False(vm.HasError);
        Assert.False(vm.CanSave);
        vm.ManualModel = "my-deployment";
        vm.AddModelCommand.Execute(null);
        Assert.Equal("my-deployment", Assert.Single(vm.Models).Id);
        Assert.True(vm.CanSave);
        Assert.Empty(data.Settings.ByokEndpoints);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "EasyConnect_AuthFailed")]
    [InlineData(HttpStatusCode.PaymentRequired, "EasyConnect_NoCredits")]
    [InlineData(HttpStatusCode.TooManyRequests, "EasyConnect_RateLimited")]
    public async Task DiscoveryFailureKeepsTheAccountEditableAndDoesNotStoreItsKey(
        HttpStatusCode status, string resource)
    {
        var data = new AppData();
        var keys = new FakeSecureKeyStore();
        using var http = new HttpClient(new StubHandler(_ => new(status)
        { Content = new StringContent("private-provider-key") }));
        using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared,
            keys, () => { }, new ProviderConnectionService(http));
        vm.Providers.Single(card => card.Profile.Id == "openai").OpenCommand.Execute(null);
        vm.ApiKey = "private-provider-key";

        await vm.DiscoverCommand.ExecuteAsync(null);

        Assert.True(vm.IsAccountStage);
        Assert.Equal(Lumi.Localization.Loc.Get(resource), vm.Error);
        Assert.DoesNotContain(vm.ApiKey, vm.Error);
        Assert.True(vm.CanDiscover);
        Assert.False(vm.IsWorking);
        Assert.Empty(data.Settings.ByokEndpoints);
        Assert.Empty(data.Settings.ByokModels);
        Assert.Equal(0, keys.Count);
    }

    [Fact]
    public async Task AnUnavailableLocalServerExplainsHowToStartIt()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new HttpRequestException("Connection refused.")));
        using var vm = new EasyConnectViewModel(new DataStore(new AppData()), TestCopilot.Shared,
            new FakeSecureKeyStore(), () => { }, new ProviderConnectionService(http));
        vm.Providers.Single(card => card.Profile.Id == "lmstudio").OpenCommand.Execute(null);

        await vm.DiscoverCommand.ExecuteAsync(null);

        Assert.Equal(string.Format(Lumi.Localization.Loc.Get("EasyConnect_LocalNotRunning"), "LM Studio"), vm.Error);
        Assert.True(vm.IsAccountStage);
        Assert.True(vm.CanDiscover);
        Assert.False(vm.IsWorking);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingDiscoveryDoesNotPersistAnAccount(bool closeDialog)
    {
        var data = new AppData();
        var keys = new FakeSecureKeyStore();
        using var handler = new WaitingHandler();
        using var http = new HttpClient(handler);
        using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared,
            keys, () => { }, new ProviderConnectionService(http));
        vm.Providers.Single(card => card.Profile.Id == "openai").OpenCommand.Execute(null);
        vm.ApiKey = "test-key";
        var discovery = vm.DiscoverCommand.ExecuteAsync(null);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (closeDialog)
            vm.CloseDialogCommand.Execute(null);
        else
            vm.CancelCommand.Execute(null);
        await discovery.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsWorking);
        Assert.False(vm.HasError);
        Assert.Empty(data.Settings.ByokEndpoints);
        Assert.Empty(data.Settings.ByokModels);
        Assert.Equal(0, keys.Count);
        if (closeDialog)
        {
            Assert.True(vm.IsGallery);
            Assert.Empty(vm.ApiKey);
            Assert.False(vm.IsDialogOpen);
        }
        else
        {
            Assert.True(vm.IsAccountStage);
            Assert.True(vm.CanDiscover);
            Assert.Equal(Lumi.Localization.Loc.Get("EasyConnect_Cancelled"), vm.Status);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectingAnExternalDefaultSkipsExpiredFallbacks(bool byokOnly)
    {
        var data = new AppData();
        data.Settings.UseBYOKOnly = byokOnly;
        foreach (var id in new[] { "default", "expired", "ready" })
        {
            data.Settings.ByokEndpoints.Add(new()
            {
                Id = id, Name = id, BaseUrl = "http://localhost:1234/v1",
                ProviderType = "openai", WireApi = "completions", ApiKeyMode = ByokApiKeyMode.None,
                CredentialExpiresAt = id == "expired" ? DateTimeOffset.UtcNow.AddMinutes(-1) : null
            });
            data.Settings.ByokModels.Add(new() { Id = id, EndpointId = id, ModelId = id, DisplayName = id });
        }
        data.Settings.PreferredModel = "byok:default";
        using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared,
            new FakeSecureKeyStore(), () => { })
        {
            LoginVM = new GitHubLoginViewModel(TestCopilot.Shared) { IsAuthenticated = byokOnly },
            CopilotModels = () => ["claude-sonnet-4.5"]
        };

        await vm.Connections.Single(card => card.Endpoint.Id == "default").ConfirmDisconnectCommand.ExecuteAsync(null);

        Assert.Equal("byok:ready", data.Settings.PreferredModel);
        Assert.Equal(2, data.Settings.ByokEndpoints.Count);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task EmptyModelListAllowsManualEntryWithoutDuplicates()
    {
        var data = new AppData();
        using var http = new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK)
        { Content = new StringContent("""{"data":[]}""") }));
        using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared,
            new FakeSecureKeyStore(), () => { }, new ProviderConnectionService(http));
        vm.Providers.Single(card => card.Profile.Id == "lmstudio").OpenCommand.Execute(null);
        await vm.DiscoverCommand.ExecuteAsync(null);

        Assert.True(vm.IsModelStage);
        Assert.True(vm.ShowNoModels);
        Assert.False(vm.CanSave);
        vm.ManualModel = "local-model";
        vm.AddModelCommand.Execute(null);
        vm.Models[0].IsSelected = false;
        vm.ManualModel = "local-model";
        vm.AddModelCommand.Execute(null);
        Assert.True(Assert.Single(vm.Models).IsSelected);
        Assert.True(vm.CanSave);
        Assert.Empty(data.Settings.ByokModels);
    }

    [Fact]
    public async Task DisconnectingTheLastUsableAccountLeavesTheDefaultUnset()
    {
        var data = new AppData();
        data.Settings.ByokEndpoints.Add(new()
        {
            Id = "local", Name = "Local", BaseUrl = "http://localhost:1234/v1",
            ProviderType = "openai", ApiKeyMode = ByokApiKeyMode.None
        });
        data.Settings.ByokModels.Add(new() { Id = "model", EndpointId = "local", ModelId = "local", DisplayName = "Local" });
        data.Settings.PreferredModel = "byok:model";
        using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared,
            new FakeSecureKeyStore(), () => { });

        await vm.Connections[0].ConfirmDisconnectCommand.ExecuteAsync(null);

        Assert.Empty(data.Settings.PreferredModel);
        Assert.Empty(vm.Connections);
        Assert.False(vm.HasUsableConnections);
    }

    [Fact]
    public void OnboardingDoesNotOfferLearningWithOnlyAnExpiredAccount()
    {
        var data = new AppData();
        data.Settings.ByokEndpoints.Add(new()
        {
            Id = "expired", Name = "Poe", ConnectionProviderId = "poe",
            BaseUrl = "https://api.poe.com/v1", ProviderType = "openai", WireApi = "completions",
            ApiKeyMode = ByokApiKeyMode.Stored,
            ApiKey = "test-key", CredentialExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        data.Settings.ByokModels.Add(new() { EndpointId = "expired", ModelId = "model", DisplayName = "Model" });
        var store = new DataStore(data);
        using var connect = new EasyConnectViewModel(store, TestCopilot.Shared, new FakeSecureKeyStore(), () => { });
        var onboarding = new OnboardingViewModel(store, TestCopilot.Shared) { EasyConnect = connect };

        Assert.Single(connect.Connections);
        Assert.False(onboarding.CanLearn);
        connect.Connections[0].ManageCommand.Execute(null);
        Assert.True(connect.IsAccountStage);
        Assert.Equal(Lumi.Localization.Loc.Get("EasyConnect_ExpiredHint"), connect.Status);
    }

    [Fact]
    public async Task ReconnectingPreservesExistingEnvironmentFallbackForMissingStoredKey()
    {
        var variable = "LUMI_CONNECT_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "test-key");
        try
        {
            var data = new AppData();
            data.Settings.ByokEndpoints.Add(new()
            {
                Id = "endpoint", Name = "Existing", BaseUrl = "https://provider.example/v1", ProviderType = "openai",
                ApiKeyMode = ByokApiKeyMode.CredentialStore, ApiKeyEnvVar = variable
            });
            using var http = new HttpClient(new StubHandler(request =>
            {
                Assert.Equal("test-key", request.Headers.Authorization!.Parameter);
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"id":"model"}]}""") };
            }));
            using var vm = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared, new FakeSecureKeyStore(),
                () => { }, new ProviderConnectionService(http));
            Assert.Single(vm.Connections).ManageCommand.Execute(null);
            await vm.DiscoverCommand.ExecuteAsync(null);
            Assert.False(vm.HasError);
            Assert.Single(vm.Models);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task ExplicitVerificationProviderIsNeverRerouted()
    {
        var provider = new GitHub.Copilot.ProviderConfig { BaseUrl = "https://verify.example/v1", Type = "openai" };
        var config = await TestCopilot.Shared.BuildLightweightSessionConfigAsync(new()
        { SystemPrompt = "verify", Model = "test-model", Provider = provider });
        Assert.Same(provider, config.Provider);
        Assert.Equal("test-model", config.Model);
    }

    [Fact]
    public void ExpiredConnectionRequiresReauthorization()
        => Assert.Throws<InvalidOperationException>(() => ByokConfigHelper.ResolveApiKey(new()
        { ApiKeyMode = ByokApiKeyMode.Stored, ApiKey = "test-key", CredentialExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }));

    [Fact]
    public void PersistedSnapshotPreservesConnectionIdentityExpiryAndByokOnlyMode()
    {
        var data = new AppData();
        data.Settings.UseBYOKOnly = true;
        var expires = DateTimeOffset.UtcNow.AddDays(1);
        data.Settings.ByokEndpoints.Add(new()
        {
            Name = "Poe", ConnectionProviderId = "poe", CredentialExpiresAt = expires,
            ApiKeyMode = ByokApiKeyMode.CredentialStore
        });
        var snapshot = AppDataSnapshotFactory.CreateIndexSnapshot(data);
        var endpoint = Assert.Single(snapshot.Settings.ByokEndpoints);
        Assert.Equal("poe", endpoint.ConnectionProviderId);
        Assert.Equal(expires, endpoint.CredentialExpiresAt);
        Assert.Null(endpoint.ApiKey);
        Assert.True(snapshot.Settings.UseBYOKOnly);
    }

    [Theory]
    [InlineData("Remote provider rate_limit exceeded: private-key", "EasyConnect_RateLimited")]
    [InlineData("HTTP 401 Unauthorized: private-key", "EasyConnect_AuthFailed")]
    [InlineData("Insufficient credit balance: private-key", "EasyConnect_NoCredits")]
    [InlineData("HTTP 400: Function tools with reasoning_effort are not supported for gpt-6-luna in /v1/chat/completions. To use function tools, use /v1/responses or set reasoning_effort to 'none'. private-key", "EasyConnect_RequiresResponses")]
    [InlineData("Unexpected failure from /v1/responses containing private-key", "EasyConnect_ConnectFailed")]
    [InlineData("Unexpected provider response containing private-key", "EasyConnect_ConnectFailed")]
    public void RuntimeFailuresAreActionableWithoutEchoingProviderSecrets(string message, string resource)
    {
        var error = ProviderConnectionService.DescribeFailure(new InvalidOperationException("Outer", new Exception(message)));
        Assert.Equal(Lumi.Localization.Loc.Get(resource), error);
        Assert.DoesNotContain("private-key", error);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancelled discovery must not finish.");
        }
    }
}
