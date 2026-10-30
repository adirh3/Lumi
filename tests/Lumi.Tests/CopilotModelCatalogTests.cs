using System.Text.Json;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Remote;
using Lumi.ViewModels;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Trait("Category", "CoreQA")]
public sealed class CopilotModelCatalogTests
{
    [Fact]
    public void ImplicitAutoDoesNotInventAStandardTierWhenItsNativeDefaultIsUnknown()
    {
        var models = CopilotModelCatalog.BuildModels(NativeCatalog());
        Assert.Null(CopilotModelCatalog.GetAutoTierQuality("auto", models.Select(model => model.Id)));
    }

#pragma warning disable GHCP001
    internal static GitHub.Copilot.Rpc.ModelList NativeCatalog() =>
        JsonSerializer.Deserialize<GitHub.Copilot.Rpc.ModelList>("""
        {
          "models": [
            {"id":"auto","name":"Auto","capabilities":{}},
            {"id":"gpt-6-luna","name":"GPT-6 Luna",
             "capabilities":{"supports":{"vision":true,"reasoningEffort":true},
                             "limits":{"max_context_window_tokens":272000}},
             "policy":{"state":"enabled","terms":"Canonical terms"},
             "supportedReasoningEfforts":["low","high"],"defaultReasoningEffort":"high",
             "billing":{"multiplier":1,"tokenPrices":{"batchSize":1000,"inputPrice":0.2,"outputPrice":0.4,
                         "contextMax":272000,"longContext":{"contextMax":1000000}}}}
          ],
          "auto": {
            "defaultTier":"balance",
            "tiers":[
              {"id":"balance","displayName":"Standard","type":"auto","fusionTier":"standard","status":{"enabled":true}},
              {"id":"hydrafusion","displayName":"High","type":"fusion","fusionTier":"high","status":{"enabled":true,"pickerVisible":true}},
              {"id":"hydrafusion-max","displayName":"Max","type":"fusion","fusionTier":"max","status":{"enabled":true,"pickerVisible":true}},
              {"id":"disabled","displayName":"Disabled","type":"fusion","status":{"enabled":false,"pickerVisible":true}},
              {"id":"fast","displayName":"Fast","type":"auto","status":{"enabled":true,"pickerVisible":false}}
            ]
          },
          "imageModels":[{"id":"image-only","availability":"available","isDefault":true,"name":"Image only"}]
        }
        """)!;
#pragma warning restore GHCP001

    [Fact]
    public void NativeCatalogAddsOnlyAdvertisedEnabledPickerTiersAndRetainsConcreteMetadata()
    {
        var models = CopilotModelCatalog.BuildModels(NativeCatalog());

        Assert.Equal(["auto", "auto:balance", "auto:hydrafusion", "auto:hydrafusion-max", "gpt-6-luna"],
            models.Select(model => model.Id));
        Assert.DoesNotContain(models, model => model.Id.Contains("gemini") || model.Id == "image-only");
        var concrete = models.Single(model => model.Id == "gpt-6-luna");
        Assert.Equal(272000, concrete.Capabilities.Limits.MaxContextWindowTokens);
        Assert.True(concrete.Capabilities.Supports.Vision);
        Assert.Equal(["low", "high"], concrete.SupportedReasoningEfforts);
        Assert.Equal("high", concrete.DefaultReasoningEffort);
        Assert.Equal("Canonical terms", concrete.Policy!.Terms);
        Assert.Equal(0.2, concrete.Billing!.TokenPrices!.InputPrice);
        Assert.Equal(1, concrete.Billing.Multiplier);
        Assert.All(models.Where(model => CopilotModelCatalog.IsAutoTierSelection(model.Id)), model =>
        {
            Assert.Null(model.Billing);
            Assert.Null(model.SupportedReasoningEfforts);
            Assert.Equal(0, model.Capabilities.Limits.MaxContextWindowTokens);
        });
    }

    [Fact]
    public void NativeSnapshotRetainsItsDefaultTierAndContextMetadata()
    {
        var snapshot = CopilotModelCatalog.BuildSnapshot(NativeCatalog());
        Assert.Equal("balance", snapshot.DefaultAutoTier);
        Assert.Equal(new ModelContextWindowLimits(272000, 1000000),
            snapshot.ContextWindows.Limits["gpt-6-luna"]);
        Assert.Contains("gpt-6-luna", snapshot.ContextWindows.LongContextModelIds);
    }

    [Fact]
    public async Task RefreshCanAddNewConcreteModelsAndRemoveUnavailableTiers()
    {
        var native = NativeCatalog();
        var cache = new ModelCatalogCache(
            _ => Task.FromResult(CopilotModelCatalog.BuildSnapshot(native)));
        await cache.GetModelsAsync();
#pragma warning disable GHCP001
        native.Models.Add(new GitHub.Copilot.Rpc.Model { Id = "gemini-advertised", Name = "Gemini advertised" });
        native.Auto!.Tiers.Single(tier => tier.Id == "hydrafusion").Status.Enabled = false;
#pragma warning restore GHCP001

        Assert.True(await cache.RefreshAsync(force: true));
        var updated = await cache.GetModelsAsync();
        Assert.Contains(updated, model => model.Id == "gemini-advertised");
        Assert.DoesNotContain(updated, model => model.Id == "auto:hydrafusion");
    }

    [Theory]
    [InlineData("create")]
    [InlineData("resume")]
    [InlineData("lightweight")]
    public void AllSessionKindsSendAutoWithTheSelectedNativeTier(string kind)
    {
        const string selection = "auto:hydrafusion-max";
        SessionConfigBase config = kind switch
        {
            "create" => SessionConfigBuilder.Build(
                "prompt", selection, null, null, [], [], [], null, null, null, null),
            "resume" => SessionConfigBuilder.BuildForResume(
                "prompt", selection, null, null, [], [], [], null, null, null, null),
            _ => SessionConfigBuilder.BuildLightweight(new LightweightSessionOptions
            {
                SystemPrompt = "prompt", Model = selection
            })
        };

        Assert.Equal("auto", config.Model);
        Assert.Equal("hydrafusion-max", config.Capi?.AutoTier?.Value);
        Assert.Null(config.Provider);
        Assert.Null(config.ReasoningEffort);
        var route = ByokConfigHelper.ResolveSessionModelRoute(new UserSettings(), selection);
        Assert.Equal(selection, route.SelectionToken);
        Assert.Equal("auto", route.WireModelId);
        Assert.False(route.IsByok);
    }

#pragma warning disable GHCP001
    [Theory]
    [InlineData("auto:balance", "balance", false)]
    [InlineData("auto:hydrafusion", "hydrafusion", false)]
    [InlineData("auto", null, true)]
    [InlineData("gpt-6-luna", null, false)]
    [InlineData("byok:model", null, false)]
    public void ModelSwitchStagesTierAtomicallyAndPlainAutoResetsIt(string selection, string? tier, bool reset)
    {
        var options = SessionConfigBuilder.CreateSetModelOptions(selection, null, null);
        Assert.Equal(tier, options.AutoTier?.Value);
        Assert.Equal(reset, options.ResetAutoTier);
    }
#pragma warning restore GHCP001

    [Fact]
    public void ByokWireIdsAreNotInterpretedAsNativeAutoTierSelections()
    {
        var provider = new ProviderConfig { BaseUrl = "https://provider.example", Type = "openai" };
        var config = SessionConfigBuilder.BuildLightweight(new LightweightSessionOptions
        {
            SystemPrompt = "prompt", Model = "auto:deployment", Provider = provider
        });
        Assert.Equal("auto:deployment", config.Model);
        Assert.Null(config.Capi);
    }

    [Fact]
    public void AutoCostSelectionDoesNotMistakeFusionForAKnownCheapConcreteModel()
        => Assert.Null(CopilotService.SelectCheapestModelId(
            CopilotModelCatalog.BuildModels(NativeCatalog())
                .Where(model => model.Id != "gpt-6-luna")));
}

[Collection("Headless UI")]
public sealed class CopilotModelCatalogUiTests
{
    [Theory]
    [InlineData("auto:hydrafusion-max", "hydrafusion-max", "hydrafusion", "auto:hydrafusion", "en")]
    [InlineData("auto:balance", "hydrafusion-max", "hydrafusion", "auto:balance", "en")]
    [InlineData("gpt-6-luna", "hydrafusion-max", "hydrafusion", "gpt-6-luna", "en")]
    [InlineData("auto:hydrafusion-max", "hydrafusion-max", "hydrafusion", "auto:hydrafusion", "he")]
    [InlineData("auto", null, "hydrafusion", "auto:hydrafusion", "en")]
    public async Task FailedNativeTierSwitchWarnsWithoutStoppingWorkOrOverwritingANewerChoice(
        string selection, string? requestedTier, string effectiveTier, string expectedSelection, string language)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            try
            {
                Loc.Load(language);
                var chat = new Chat { LastModelUsed = selection, LastReasoningEffortUsed = "low" };
                var data = new AppData
                {
                    Settings = new UserSettings
                    {
                        ReasoningEffort = "low", AutoSaveChats = false, EnableMemoryAutoSave = false
                    },
                    Chats = [chat]
                };
                using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
                vm.UpdateModelCapabilities(CopilotModelCatalog.BuildModels(CopilotModelCatalogTests.NativeCatalog()));
                vm.CurrentChat = chat;
                vm.ApplyModelSelection(selection, "low");
                var runtime = new ChatRuntimeState
                {
                    ActiveModelId = "auto:hydrafusion", IsBusy = true, IsStreaming = true
                };
                var failure = new SessionAutoTierSwitchFailedData
                {
                    RequestedAutoTier = requestedTier is null ? null : new AutoTier(requestedTier),
                    EffectiveAutoTier = new AutoTier(effectiveTier),
                    Reason = AutoTierSwitchFailureReason.RequestFailed
                };

                vm.ApplyAutoTierSwitchFailure(chat, runtime, failure, updateDisplayed: true);

                Assert.Equal(expectedSelection, chat.LastModelUsed);
                Assert.Equal(expectedSelection, vm.SelectedModel);
                Assert.Equal("auto:hydrafusion", runtime.ActiveModelId);
                Assert.True(runtime.IsBusy);
                Assert.True(runtime.IsStreaming);
                Assert.Equal("low", data.Settings.ReasoningEffort);
                Assert.Equal("low", chat.LastReasoningEffortUsed);
                var warning = Assert.Single(chat.Messages, message => message.Role == "system");
                Assert.Contains(language == "he" ? "לא ניתן היה" : "Couldn't change Fusion effort", warning.Content);
                Assert.Contains(Loc.Quality_High, warning.Content);
                Assert.Contains(vm.Messages, message => message.Message.Id == warning.Id);
                Assert.Equal(requestedTier, failure.RequestedAutoTier?.Value);
                Assert.Equal(effectiveTier, failure.EffectiveAutoTier?.Value);
            }
            finally
            {
                Loc.Load("en");
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ImplicitAutoUsesTheAdvertisedDefaultAcrossSettingsPhoneAndCopiedSurfaces()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var native = CopilotModelCatalogTests.NativeCatalog();
#pragma warning disable GHCP001
            native.Auto!.DefaultTier = "hydrafusion";
#pragma warning restore GHCP001
            var snapshot = CopilotModelCatalog.BuildSnapshot(native);
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    PreferredModel = "auto", ReasoningEffort = "low",
                    AutoSaveChats = false, EnableMemoryAutoSave = false
                }
            };
            var store = new DataStore(data);
            using var main = new MainViewModel(store, TestCopilot.Shared, new UpdateService(),
                startBackgroundJobs: false, initializeCopilotOnStartup: false);
            typeof(MainViewModel).GetMethod("ApplyModelCatalog",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(main, [snapshot, true]);

            Assert.Equal("auto", main.ChatVM.SelectedModel);
            Assert.Equal("High", main.ChatVM.SelectedQuality);
            Assert.Equal("High", main.SettingsVM.SelectedQuality);
            Assert.Null(main.SettingsVM.SelectedBackgroundQuality);
            Assert.Equal("High", RemoteProjector.BuildSettings(store, main.ChatVM.AvailableModels.ToArray(), main.ChatVM)
                .ModelDefaults.Single(model => model.Model == "auto").Quality);

            main.ChatVM.UpdateModelCapabilities([], merge: true);
            main.SettingsVM.UpdateModelCapabilities([], merge: true);
            using var copy = new ChatViewModel(store, TestCopilot.Shared);
            copy.CopyModelCatalogFrom(main.ChatVM);
            copy.ApplyModelSelection("auto", "low");
            Assert.Equal("High", copy.SelectedQuality);
            Assert.Equal("High", main.ChatVM.SelectedQuality);
            Assert.Equal("High", main.SettingsVM.SelectedQuality);
            Assert.Equal("low", data.Settings.ReasoningEffort);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DesktopAndPhoneShowLocalizedTiersWithoutChangingCanonicalCatalogOrSelection()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var models = CopilotModelCatalog.BuildModels(CopilotModelCatalogTests.NativeCatalog());
            var store = new DataStore(new AppData());
            using var vm = new ChatViewModel(store, TestCopilot.Shared);
            vm.UpdateModelCapabilities(models);
            vm.ApplyAvailableModels(models.Select(model => model.Id).ToArray(), "gpt-6-luna");
            try
            {
                foreach (var language in new[] { "en", "he" })
                {
                    Loc.Load(language);
                    vm.ApplyModelSelection("auto:hydrafusion", "low");
                    var phone = RemoteProjector.BuildSettings(store, vm.AvailableModels.ToArray(), vm);
                    var fusion = Assert.Single(phone.ModelOptions, model => model.SelectionAliases.Count > 0);
                    Assert.Equal(Loc.Get("ModelPicker_Fusion"), fusion.DisplayName);
                    Assert.Contains("auto:hydrafusion", fusion.SelectionAliases);
                    Assert.Contains("auto:hydrafusion-max", fusion.SelectionAliases);
                    Assert.Equal(2, phone.ModelOptions.Count);
                    Assert.Equal(Loc.Quality_High, vm.SelectedQuality);
                    Assert.Null(vm.GetSelectedReasoningEffort());
                    Assert.Equal(Loc.Quality_High,
                        phone.ModelDefaults.Single(model => model.Model == "auto:hydrafusion").Quality);
                    Assert.Null(fusion.Cost);
                    Assert.Null(fusion.ContextSize);
                    Assert.Equal("Copilot Fusion High", models.Single(model => model.Id == "auto:hydrafusion").Name);
                }
            }
            finally
            {
                Loc.Load("en");
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SavedFusionTierUsesTheExistingEffortControlWithoutChangingReasoningPreferences()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    PreferredModel = "auto:hydrafusion-max",
                    ReasoningEffort = "low",
                    FavoriteModelIds = ["auto:hydrafusion-max"],
                    AutoSaveChats = false
                }
            };
            using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            var models = CopilotModelCatalog.BuildModels(CopilotModelCatalogTests.NativeCatalog());
            vm.UpdateModelCapabilities(models);
            vm.ApplyAvailableModels(models.Select(model => model.Id).ToArray(), data.Settings.PreferredModel);
            vm.ApplyModelSelection(data.Settings.PreferredModel, "low");

            var fusion = Assert.Single(vm.ModelOptions, option => option.SelectionAliases.Count > 0);
            Assert.Equal("Copilot Fusion", fusion.DisplayName);
            Assert.True(((IStrataModelOption)fusion).MatchesModelId(vm.SelectedModel!));
            Assert.True(fusion.IsPinned);
            Assert.Equal(["Standard", "High", "Max"], Assert.IsType<string[]>(vm.QualityLevels));
            Assert.Equal("Max", vm.SelectedQuality);

            vm.SelectedQuality = "High";
            Assert.Equal("auto:hydrafusion", vm.SelectedModel);
            Assert.Equal("low", data.Settings.ReasoningEffort);
            Assert.Null(vm.GetSelectedReasoningEffort());
            var native = SessionConfigBuilder.Build(
                "fixture", vm.SelectedModel, null, null, [], [], [],
                vm.GetSelectedReasoningEffort(), null, null, null);
            Assert.Equal("auto", native.Model);
            Assert.Equal("hydrafusion", native.Capi?.AutoTier?.Value);
            Assert.Null(native.ReasoningEffort);

            vm.TogglePinnedModelCommand.Execute(fusion);
            Assert.Empty(data.Settings.FavoriteModelIds);
            vm.SelectedModel = "gpt-6-luna";
            Assert.Equal("Low", vm.SelectedQuality);
            Assert.Equal("low", vm.GetSelectedReasoningEffort());
            Assert.Equal("low", data.Settings.ReasoningEffort);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SettingsAndAiFeaturesShareGroupedFusionButKeepAutomaticBackgroundSelectionSeparate()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    PreferredModel = "auto:hydrafusion-max",
                    BackgroundModel = "auto:hydrafusion-max",
                    ReasoningEffort = "low",
                    AiFeatureModels = { [AiFeatureIds.ChatTitles] = "auto:hydrafusion-max" }
                }
            };
            await using var browser = new BrowserService();
            using var vm = new SettingsViewModel(new DataStore(data), TestCopilot.Shared, browser, new UpdateService());
            var models = CopilotModelCatalog.BuildModels(CopilotModelCatalogTests.NativeCatalog());
            vm.UpdateModelCapabilities(models);
            vm.UpdateAvailableModels(models.Select(model => model.Id).ToList());
            Assert.Equal("Max", vm.SelectedQuality);
            Assert.Equal("Max", vm.SelectedBackgroundQuality);
            Assert.Single(vm.ModelOptions, option => option.SelectionAliases.Count > 0);
            var backgroundFusion = Assert.Single(vm.BackgroundModelOptions, option => option.SelectionAliases.Count > 0);
            Assert.False(((IStrataModelOption)backgroundFusion).MatchesModelId("auto"));
            Assert.Contains(vm.BackgroundModelOptions, option => option.Id == "auto");

            vm.SelectedQuality = "High";
            vm.SelectedBackgroundQuality = "Standard";
            var titles = vm.AiFeatures.Single(feature => feature.Id == AiFeatureIds.ChatTitles);
            Assert.Equal("Max", titles.SelectedQuality);
            Assert.Equal(["Standard", "High", "Max"], Assert.IsType<string[]>(titles.QualityLevels));
            titles.SelectedQuality = "High";
            Assert.Equal("auto:hydrafusion", data.Settings.PreferredModel);
            Assert.Equal("auto:balance", data.Settings.BackgroundModel);
            Assert.Equal("auto:hydrafusion", data.Settings.GetAiFeatureModel(AiFeatureIds.ChatTitles));
            Assert.Equal("low", data.Settings.ReasoningEffort);

            vm.BackgroundModel = "auto";
            Assert.Null(vm.BackgroundQualityLevels);
            Assert.Null(vm.SelectedBackgroundQuality);
        }, CancellationToken.None);
    }
}
