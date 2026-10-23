using System.Reflection;
using System.Text.Json;
using Avalonia.Threading;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class BackgroundModelSettingsTests
{
    private static SettingsViewModel CreateVm(AppData data) => new(
        new DataStore(data), TestCopilot.Shared, new BrowserService(), new UpdateService());

    [Fact]
    public void NewAndExistingProfilesDefaultToAutoAndRoundTripExplicitChoices()
    {
        Assert.Equal("auto", new UserSettings().BackgroundModel);
        var legacy = JsonSerializer.Deserialize("{}", AppDataJsonContext.Default.UserSettings)!;
        Assert.Equal("auto", legacy.BackgroundModel);
        legacy.BackgroundModel = "gpt-6-luna";
        var json = JsonSerializer.Serialize(legacy, AppDataJsonContext.Default.UserSettings);
        Assert.Equal("gpt-6-luna",
            JsonSerializer.Deserialize(json, AppDataJsonContext.Default.UserSettings)!.BackgroundModel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticPriceRefreshPreservesByokChatSelectionAndLocalModels(bool byokOnly)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            await using var connection = new TestCopilot.SimulatedConnection();
            var endpoint = new ByokEndpoint
            {
                Id = "catalog-endpoint",
                Name = "Catalog fixture",
                ProviderType = "openai",
                BaseUrl = "http://127.0.0.1:1/v1",
                WireApi = "completions",
                ApiKeyMode = ByokApiKeyMode.None
            };
            var byokModel = new ByokModel
            {
                Id = "catalog-model",
                EndpointId = endpoint.Id,
                ModelId = "private-wire-model",
                DisplayName = "Private model",
                SupportsReasoningEffort = true,
                SupportedReasoningEfforts = ["low", "high"],
                DefaultReasoningEffort = "high",
                DefaultContextWindowTokens = 128_000,
                LongContextWindowTokens = 256_000
            };
            var token = ByokConfigHelper.BuildModelToken(byokModel);
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    PreferredModel = token,
                    ReasoningEffort = "high",
                    ContextWindowTier = "long",
                    UseBYOKOnly = byokOnly,
                    ByokEndpoints = [endpoint],
                    ByokModels = [byokModel],
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            };
            using var main = new MainViewModel(
                new DataStore(data), TestCopilot.Shared, new UpdateService(),
                startBackgroundJobs: false, initializeCopilotOnStartup: false);
            typeof(MainViewModel).GetMethod("InjectByokModels", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(main, null);
            main.IsConnected = true;
            Assert.Equal(token, main.ChatVM.SelectedModel);

            var snapshot = new ModelCatalogSnapshot(
                [new ModelInfo
                {
                    Id = "gpt-6-luna",
                    Billing = new ModelBilling
                    {
                        TokenPrices = new() { BatchSize = 1000, InputPrice = 0.01, OutputPrice = 0.05 }
                    }
                }],
                ModelCatalogCache.Empty);
            typeof(MainViewModel).GetMethod("OnModelCatalogChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(main, [snapshot]);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

            Assert.Equal(token, main.ChatVM.SelectedModel);
            Assert.Equal(token, main.SettingsVM.PreferredModel);
            Assert.Equal(token, data.Settings.PreferredModel);
            Assert.Contains(token, main.ChatVM.AvailableModels);
            Assert.Contains(token, main.SettingsVM.AvailableModels);
            Assert.Contains(token, main.SettingsVM.AvailableBackgroundModels);
            Assert.Equal(["Low", "High"], main.ChatVM.QualityLevels!);
            Assert.Equal(["Default", "Long"], main.ChatVM.ContextWindowTiers!);
            Assert.Equal("High", main.ChatVM.SelectedQuality);
            Assert.Equal("Long", main.ChatVM.SelectedContextWindowTier);
            Assert.Equal("auto", data.Settings.BackgroundModel);
            // Only your own accounts: Auto runs on the default external model, or says it can't run.
            var status = main.SettingsVM.BackgroundModelStatus ?? "";
            if (byokOnly)
                Assert.True(status == Loc.SettingStatus_BackgroundModelByokOnly || status.Contains("BYOK"), status);
            else
                Assert.Contains("GPT 6 Luna", status);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CatalogFallbackOnlyScoresCopilotIdsWhenByokModelsAreConfigured()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    ByokEndpoints =
                    [
                        new()
                        {
                            Id = "fallback-endpoint",
                            Name = "Fallback fixture",
                            ProviderType = "openai",
                            BaseUrl = "http://127.0.0.1:1/v1",
                            WireApi = "completions",
                            ApiKeyMode = ByokApiKeyMode.None
                        }
                    ],
                    ByokModels =
                    [
                        new()
                        {
                            Id = "99999999999949998999123456789abc",
                            EndpointId = "fallback-endpoint",
                            ModelId = "private-wire-model",
                            DisplayName = "Private model"
                        }
                    ],
                    AutoSaveChats = false
                }
            };
            using var main = new MainViewModel(
                new DataStore(data), TestCopilot.Shared, new UpdateService(),
                startBackgroundJobs: false, initializeCopilotOnStartup: false);
            typeof(MainViewModel).GetMethod("ApplyModelCatalog", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(main, [new List<ModelInfo> { new() { Id = "gpt-6-luna" } }, ModelCatalogCache.Empty, false]);

            Assert.Equal("gpt-6-luna", main.ChatVM.SelectedModel);
            Assert.Equal("gpt-6-luna", main.SettingsVM.PreferredModel);
            Assert.Contains(ByokConfigHelper.BuildModelToken(data.Settings.ByokModels[0]), main.ChatVM.AvailableModels);
        }, CancellationToken.None);
    }

    [Fact]
    public void EditingAndRevertingBackgroundModelDoesNotChangeChatDefaults()
    {
        var data = new AppData
        {
            Settings = new UserSettings { PreferredModel = "gpt-6-sol", ReasoningEffort = "high" }
        };
        using var vm = CreateVm(data);
        Assert.Equal("auto", vm.BackgroundModel);
        Assert.False(vm.IsBackgroundModelModified);
        vm.BackgroundModel = "gpt-6-luna";
        Assert.Equal("gpt-6-luna", data.Settings.BackgroundModel);
        Assert.True(vm.IsBackgroundModelModified);
        Assert.Equal("gpt-6-sol", data.Settings.PreferredModel);
        Assert.Equal("high", data.Settings.ReasoningEffort);

        vm.RevertBackgroundModelCommand.Execute(null);
        Assert.Equal("auto", data.Settings.BackgroundModel);
        Assert.False(vm.IsBackgroundModelModified);
        Assert.Equal("gpt-6-sol", data.Settings.PreferredModel);
        Assert.Equal("high", data.Settings.ReasoningEffort);
    }

    [Fact]
    public void SavedSelectionLoadsAndResetSettingsRestoresAuto()
    {
        var data = new AppData { Settings = new UserSettings { BackgroundModel = "gpt-6-luna" } };
        using var vm = CreateVm(data);
        Assert.Equal("gpt-6-luna", vm.BackgroundModel);
        Assert.Contains("gpt-6-luna", vm.AvailableBackgroundModels);
        vm.ResetSettingsCommand.Execute(null);
        Assert.Equal("auto", vm.BackgroundModel);
        Assert.Equal("auto", data.Settings.BackgroundModel);
        Assert.False(vm.IsBackgroundModelModified);
    }

    [Fact]
    public void CatalogRefreshKeepsAutoAndTheSavedBackgroundChoiceWithoutResettingRows()
    {
        var data = new AppData { Settings = new UserSettings { BackgroundModel = "retired-helper" } };
        using var vm = CreateVm(data);
        var resets = 0;
        vm.AvailableBackgroundModels.CollectionChanged += (_, args) =>
        {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                resets++;
        };

        vm.UpdateAvailableModels(["gpt-6-luna", "byok:private"]);

        Assert.Equal(["auto", "gpt-6-luna", "byok:private", "retired-helper"], vm.AvailableBackgroundModels);
        Assert.Equal("retired-helper", vm.BackgroundModel);
        Assert.Equal(0, resets);
        Assert.DoesNotContain("auto", vm.AvailableModels);
    }

    [Fact]
    public void AutoStatusUsesTheSharedCostPolicyAndSurvivesByokCapabilityMerges()
    {
        using var vm = CreateVm(new AppData());
        vm.UpdateModelCapabilities(
        [
            new() { Id = "gpt-6-sol", Billing = new ModelBilling { Multiplier = 1 } },
            new() { Id = "gpt-6-luna", Billing = new ModelBilling { Multiplier = 0.25 } }
        ]);
        Assert.Contains("GPT 6 Luna", vm.BackgroundModelStatus);
        var status = vm.BackgroundModelStatus;
        vm.UpdateModelCapabilities([new() { Id = "byok:private" }], merge: true);
        Assert.Equal(status, vm.BackgroundModelStatus);

        try
        {
            vm.UseBYOKOnly = true;
            Assert.Equal(Loc.SettingStatus_BackgroundModelByokOnly, vm.BackgroundModelStatus);
            vm.BackgroundModel = "byok:private";
            Assert.Null(vm.BackgroundModelStatus);
        }
        finally
        {
            TestCopilot.Shared.SetSettingsProvider(null);
        }
    }

    [Fact]
    public void AutoStatusReportsTokenCreditsInsteadOfAnObsoleteMultiplier()
    {
        using var vm = CreateVm(new AppData());
        vm.UpdateModelCapabilities(
        [
            new()
            {
                Id = "gpt-6-luna",
                Billing = new ModelBilling
                {
                    Multiplier = 1,
                    TokenPrices = new() { BatchSize = 1000, InputPrice = 0.1, OutputPrice = 0.3 }
                }
            }
        ]);

        Assert.Contains("GPT 6 Luna", vm.BackgroundModelStatus);
        Assert.Contains("0.4 credits", vm.BackgroundModelStatus);
    }

    [Fact]
    public void ChangingChatDefaultsDoesNotOverrideTheBackgroundSelection()
    {
        var data = new AppData { Settings = new UserSettings { BackgroundModel = "gpt-6-luna" } };
        using var vm = CreateVm(data);
        vm.SyncDefaultModelSelectionFromChat("gpt-6-sol", "high", null);
        Assert.Equal("gpt-6-luna", vm.BackgroundModel);
        Assert.Equal("gpt-6-luna", data.Settings.BackgroundModel);
    }
}
