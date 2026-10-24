using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Lumi.Converters;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class AiFeatureSettingsTests
{
    private static SettingsViewModel CreateVm(AppData data, CopilotService? copilot = null) => new(
        new DataStore(data), copilot ?? TestCopilot.Shared, new BrowserService(), new UpdateService());

    private static AiFeatureViewModel Feature(SettingsViewModel vm, string id) => vm.AiFeatures.Single(feature => feature.Id == id);

    [Fact]
    public void AFeaturesOwnModelWinsOverTheDefaultModel()
    {
        var settings = new UserSettings
        {
            BackgroundModel = "gpt-6-luna",
            AiFeatureModels = { [AiFeatureIds.DailyBriefs] = "gpt-6-sol" }
        };

        Assert.Equal("gpt-6-sol", CopilotService.ResolveConfiguredBackgroundModel(settings, true, AiFeatureIds.DailyBriefs));
        Assert.Equal("gpt-6-luna", CopilotService.ResolveConfiguredBackgroundModel(settings, true, AiFeatureIds.ChatTitles));
        Assert.Equal("gpt-6-luna", CopilotService.ResolveConfiguredBackgroundModel(settings, true));

        // With the default on Auto, a feature with its own model keeps it and the rest pick from the catalog.
        settings.BackgroundModel = UserSettings.AutomaticBackgroundModel;
        Assert.Equal("gpt-6-sol", CopilotService.ResolveConfiguredBackgroundModel(settings, true, AiFeatureIds.DailyBriefs));
        Assert.Null(CopilotService.ResolveConfiguredBackgroundModel(settings, true, AiFeatureIds.ChatTitles));
    }

    [Fact]
    public async Task HelperSessionsRunOnTheModelOfTheFeatureTheyServe()
    {
        var settings = new UserSettings
        {
            BackgroundModel = "gpt-6-luna",
            AiFeatureModels = { [AiFeatureIds.ChatTitles] = "gpt-6-sol" }
        };
        await using var copilot = new CopilotService();
        copilot.SetSettingsProvider(() => settings);

        var titles = await copilot.BuildLightweightSessionConfigAsync(
            new LightweightSessionOptions { SystemPrompt = "Fixture.", Feature = AiFeatureIds.ChatTitles });
        var followUps = await copilot.BuildLightweightSessionConfigAsync(
            new LightweightSessionOptions { SystemPrompt = "Fixture.", Feature = AiFeatureIds.FollowUps });
        var explicitModel = await copilot.BuildLightweightSessionConfigAsync(
            new LightweightSessionOptions { SystemPrompt = "Fixture.", Feature = AiFeatureIds.ChatTitles, Model = "fixture-model" });

        Assert.Equal("gpt-6-sol", titles.Model);
        Assert.Equal("gpt-6-luna", followUps.Model);
        Assert.Equal("fixture-model", explicitModel.Model);
    }

    [Fact]
    public async Task OnlyYourOwnAccountsBlocksJustTheFeaturesOnCopilotModels()
    {
        var data = new AppData
        {
            Settings = new UserSettings
            {
                UseBYOKOnly = true,
                BackgroundModel = "gpt-6-luna",
                AiFeatureModels = { [AiFeatureIds.ChatTitles] = "byok:private" }
            }
        };
        await using var copilot = new CopilotService();
        copilot.SetSettingsProvider(() => data.Settings);

        Assert.False(copilot.IsHelperBlocked(AiFeatureIds.ChatTitles));
        Assert.True(copilot.IsHelperBlocked(AiFeatureIds.FollowUps));

        using var vm = CreateVm(data, copilot);
        Assert.False(Feature(vm, AiFeatureIds.ChatTitles).IsBlocked);
        Assert.True(Feature(vm, AiFeatureIds.FollowUps).IsBlocked);

        vm.UseBYOKOnly = false;
        Assert.False(Feature(vm, AiFeatureIds.FollowUps).IsBlocked);
    }

    [Fact]
    public void FeatureModelsPersistAndOlderProfilesGetDefaults()
    {
        var legacy = JsonSerializer.Deserialize("{}", AppDataJsonContext.Default.UserSettings)!;
        Assert.Empty(legacy.AiFeatureModels);
        Assert.Empty(JsonSerializer.Deserialize("""{"aiFeatureModels":null}""", AppDataJsonContext.Default.UserSettings)!.AiFeatureModels);

        legacy.AiFeatureModels[AiFeatureIds.DailyBriefs] = "gpt-6-sol";
        var restored = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(legacy, AppDataJsonContext.Default.UserSettings), AppDataJsonContext.Default.UserSettings)!;
        Assert.Equal("gpt-6-sol", restored.GetAiFeatureModel(AiFeatureIds.DailyBriefs));

        // The saved index is copied field by field: what isn't copied is lost on the next save.
        var data = new AppData { Settings = legacy };
        var snapshot = AppDataSnapshotFactory.CreateIndexSnapshot(data, includeChats: false).Settings;
        Assert.Equal("gpt-6-sol", snapshot.GetAiFeatureModel(AiFeatureIds.DailyBriefs));
        Assert.NotSame(legacy.AiFeatureModels, snapshot.AiFeatureModels);
    }

    [Fact]
    public void FeatureSwitchesTurnTheirFeaturesOffAndOn()
    {
        var data = new AppData();
        using var vm = CreateVm(data);
        var settings = data.Settings;

        Assert.Equal(Loc.Get("AiFeatures_SummaryAllOn", 4), vm.AiFeaturesSummary);
        Feature(vm, AiFeatureIds.ChatTitles).IsOn = false;
        Feature(vm, AiFeatureIds.FollowUps).IsOn = false;
        Feature(vm, AiFeatureIds.Memories).IsOn = false;
        Feature(vm, AiFeatureIds.DailyBriefs).IsOn = false;
        Assert.False(settings.AutoGenerateTitles);
        Assert.False(settings.SuggestFollowUps);
        Assert.False(settings.EnableMemoryAutoSave);
        Assert.False(settings.EnableDailyBriefs);
        Assert.True(vm.AreAllAiFeaturesOff);
        Assert.Equal(Loc.Get("AiFeatures_SummaryAllOff"), vm.AiFeaturesSummary);
        Assert.True(Feature(vm, AiFeatureIds.Memories).IsModified);

        // Changes made elsewhere (reset, the debug bridge) show on the feature.
        vm.EnableMemoryAutoSave = true;
        Assert.True(Feature(vm, AiFeatureIds.Memories).IsOn);
        Assert.Equal(Loc.Get("AiFeatures_SummarySome", 1, 4), vm.AiFeaturesSummary);

        vm.ToggleAllAiFeaturesCommand.Execute(null);
        Assert.True(vm.AreAllAiFeaturesOff);
        Assert.False(settings.EnableMemoryAutoSave);
        Assert.Equal(Loc.Get("AiFeatures_TurnAllOn"), vm.ToggleAllAiFeaturesLabel);

        vm.ToggleAllAiFeaturesCommand.Execute(null);
        Assert.All(vm.AiFeatures.Where(static feature => feature.CanTurnOff), static feature => Assert.True(feature.IsOn));
        Assert.True(settings.AutoGenerateTitles && settings.SuggestFollowUps && settings.EnableMemoryAutoSave
                    && settings.EnableDailyBriefs);
    }

    [Fact]
    public void FeaturesThatOnlyRunWhenAskedHaveNoSwitch()
    {
        using var vm = CreateVm(new AppData());
        foreach (var id in new[] { AiFeatureIds.SharedPrompts, AiFeatureIds.Welcome })
        {
            var feature = Feature(vm, id);
            Assert.False(feature.CanTurnOff);
            feature.IsOn = false;
            Assert.True(feature.IsOn);
            Assert.False(feature.IsModified);
        }

        Assert.Equal(AiFeatureIds.All.Order(), vm.AiFeatures.Select(static feature => feature.Id).Order());
        Assert.DoesNotContain(vm.AiFeatures, static feature => feature.Id == "coding-helpers");
        Assert.Equal(4, vm.AiFeatures.Count(static feature => feature.CanTurnOff));
        Assert.Equal(vm.AiFeatures.Count,
            vm.ChatAiFeatures.Count + vm.ContextAiFeatures.Count + vm.RequestedAiFeatures.Count);
    }

    [Fact]
    public void AFeatureModelIsSavedShownAndRevertedWithoutChangingOthers()
    {
        var data = new AppData { Settings = new UserSettings { BackgroundModel = "gpt-6-luna" } };
        using var vm = CreateVm(data);
        var briefs = Feature(vm, AiFeatureIds.DailyBriefs);
        var titles = Feature(vm, AiFeatureIds.ChatTitles);

        Assert.Equal(SettingsViewModel.DefaultAiFeatureModel, briefs.ModelChoice);
        Assert.Equal(Loc.Get("AiFeatures_DefaultModelLabel", "GPT 6 Luna"), briefs.ModelLabel);

        briefs.ModelChoice = "gpt-6-sol";
        Assert.Equal("gpt-6-sol", data.Settings.AiFeatureModels[AiFeatureIds.DailyBriefs]);
        Assert.Equal("GPT 6 Sol", briefs.ModelLabel);
        Assert.True(briefs.IsModified);
        Assert.False(titles.IsModified);
        Assert.Equal("gpt-6-luna", data.Settings.BackgroundModel);

        // The default's own choice reaches every feature that follows it.
        vm.BackgroundModel = "gpt-6-terra";
        Assert.Equal(Loc.Get("AiFeatures_DefaultModelLabel", "GPT 6 Terra"), titles.ModelLabel);
        Assert.Equal("GPT 6 Sol", briefs.ModelLabel);

        briefs.IsOn = false;
        briefs.RevertCommand.Execute(null);
        Assert.True(briefs.IsOn);
        Assert.Equal(SettingsViewModel.DefaultAiFeatureModel, briefs.ModelChoice);
        Assert.Empty(data.Settings.AiFeatureModels);
        Assert.False(briefs.IsModified);
    }

    [Fact]
    public void ResettingSettingsPutsEveryFeatureBackOnTheDefaultModel()
    {
        var data = new AppData
        {
            Settings = new UserSettings
            {
                EnableMemoryAutoSave = false,
                AiFeatureModels = { [AiFeatureIds.Memories] = "gpt-6-sol" }
            }
        };
        using var vm = CreateVm(data);

        vm.ResetSettingsCommand.Execute(null);

        Assert.True(data.Settings.EnableMemoryAutoSave);
        Assert.Empty(data.Settings.AiFeatureModels);
        Assert.All(vm.AiFeatures, static feature => Assert.False(feature.IsModified));
    }

    [Fact]
    public void ModelChoicesOfferTheDefaultFirstAndKeepAChosenModelAfterItLeavesTheCatalog()
    {
        var data = new AppData { Settings = new UserSettings { AiFeatureModels = { [AiFeatureIds.FollowUps] = "retired-helper" } } };
        using var vm = CreateVm(data);

        vm.UpdateAvailableModels(["gpt-6-luna", "byok:private"]);

        Assert.Equal(["default", "gpt-6-luna", "byok:private", "retired-helper"], vm.AiFeatureModelChoices);
        Assert.Equal(Loc.Get("AiFeatures_DefaultModelChoice"),
            ModelDisplayConverter.Instance.Convert("default", typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ThePageListsEveryFeatureAndARowRevertsItsOwnFeature()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var data = new AppData();
            using var vm = CreateVm(data);
            vm.SelectedPageIndex = SettingsViewModel.AiFeaturesPageIndex;
            using var view = new SettingsView { DataContext = vm };
            var window = new Window { Content = view, Width = 1000, Height = 900 };
            window.Show();
            try
            {
                Pump(window);
                var rows = view.FindSettingsControl<Control>("PageAiFeatures")!
                    .GetLogicalDescendants().OfType<StrataSetting>()
                    .Where(static row => row.Classes.Contains("ai-feature"))
                    .ToList();
                Assert.All(vm.AiFeatures, feature => Assert.Contains(rows, row => row.Header == feature.Title));
                Assert.NotNull(view.FindSettingsControl<StrataModelPicker>("BackgroundModelPicker"));

                var memories = Feature(vm, AiFeatureIds.Memories);
                memories.IsOn = false;
                memories.ModelChoice = "gpt-6-sol";
                var row = rows.Single(row => row.Header == memories.Title);
                Assert.True(row.IsModified);
                row.RaiseEvent(new RoutedEventArgs(StrataSetting.RevertedEvent, row));

                Assert.True(data.Settings.EnableMemoryAutoSave);
                Assert.Empty(data.Settings.AiFeatureModels);

                // Search finds a feature by its older setting name.
                vm.SearchQuery = "suggest follow";
                Pump(window);
                Assert.True(rows.Single(row => row.Header == Feature(vm, AiFeatureIds.FollowUps).Title).IsHighlighted);
                vm.SearchQuery = "";
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
