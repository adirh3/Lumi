using System.Text.Json;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

[Trait("Category", "CoreQA")]
public sealed class ResponseDetailSettingsTests
{
    private static SettingsViewModel CreateVm(AppData data) => new(
        new DataStore(data), TestCopilot.Shared, new BrowserService(), new UpdateService());

    [Theory]
    [InlineData(ResponseDetailLevel.ModelDefault, null)]
    [InlineData(ResponseDetailLevel.Concise, "low")]
    [InlineData(ResponseDetailLevel.Balanced, "medium")]
    [InlineData(ResponseDetailLevel.Detailed, "high")]
    public void Choices_MapToNativeVerbosityWithoutRewritingThePrompt(
        ResponseDetailLevel detail, string? expected)
        => Assert.Equal(expected, SessionConfigBuilder.CreateResponseVerbosity(detail)?.Value);

    [Fact]
    public void DefaultAndEditedDetail_SaveAndRevertWithoutChangingReasoning()
    {
        var data = new AppData { Settings = new UserSettings { ReasoningEffort = "high" } };
        using var vm = CreateVm(data);
        Assert.Equal(ResponseDetailLevel.ModelDefault, vm.ResponseDetail);
        Assert.Equal(ResponseDetailLevel.ModelDefault, vm.SelectedResponseDetailOption!.Value);
        Assert.False(vm.IsResponseDetailModified);

        vm.SelectedResponseDetailOption = vm.ResponseDetailOptions.Single(
            option => option.Value == ResponseDetailLevel.Detailed);

        Assert.Equal(ResponseDetailLevel.Detailed, vm.ResponseDetail);
        Assert.Equal(ResponseDetailLevel.Detailed, data.Settings.ResponseDetail);
        Assert.True(vm.IsResponseDetailModified);
        Assert.Equal("high", data.Settings.ReasoningEffort);

        vm.SelectedResponseDetailOption = null;
        Assert.Equal(ResponseDetailLevel.Detailed, vm.ResponseDetail);

        vm.RevertResponseDetailCommand.Execute(null);

        Assert.Equal(ResponseDetailLevel.ModelDefault, vm.ResponseDetail);
        Assert.Equal(ResponseDetailLevel.ModelDefault, data.Settings.ResponseDetail);
        Assert.False(vm.IsResponseDetailModified);
        Assert.Equal("high", data.Settings.ReasoningEffort);
    }

    [Fact]
    public void SavedDetail_LoadsAndResetsWithAllSettings()
    {
        var data = new AppData
        {
            Settings = new UserSettings { ResponseDetail = ResponseDetailLevel.Balanced }
        };
        using var vm = CreateVm(data);
        Assert.Equal(ResponseDetailLevel.Balanced, vm.SelectedResponseDetailOption!.Value);
        Assert.True(vm.IsResponseDetailModified);

        vm.ResetSettingsCommand.Execute(null);

        Assert.Equal(ResponseDetailLevel.ModelDefault, vm.ResponseDetail);
        Assert.Equal(ResponseDetailLevel.ModelDefault, data.Settings.ResponseDetail);
        Assert.False(vm.IsResponseDetailModified);
    }

    [Fact]
    public void Detail_SurvivesSnapshotAndJsonWhileExistingProfilesKeepModelDefault()
    {
        var legacy = JsonSerializer.Deserialize("{}", AppDataJsonContext.Default.UserSettings)!;
        Assert.Equal(ResponseDetailLevel.ModelDefault, legacy.ResponseDetail);

        var data = new AppData
        {
            Settings = new UserSettings { ResponseDetail = ResponseDetailLevel.Detailed }
        };
        var snapshot = AppDataSnapshotFactory.CreateIndexSnapshot(data);
        data.Settings.ResponseDetail = ResponseDetailLevel.ModelDefault;
        var restored = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(snapshot, AppDataJsonContext.Default.AppData),
            AppDataJsonContext.Default.AppData)!;

        Assert.Equal(ResponseDetailLevel.Detailed, snapshot.Settings.ResponseDetail);
        Assert.Equal(ResponseDetailLevel.Detailed, restored.Settings.ResponseDetail);
    }

    [Theory]
    [InlineData("en", "Detailed")]
    [InlineData("he", "מפורט")]
    public void DisplayLanguage_DoesNotChangeCanonicalSettingOrNativeVerbosity(string language, string label)
    {
        try
        {
            Loc.Load(language);
            var data = new AppData();
            using var vm = CreateVm(data);
            vm.SelectedResponseDetailOption = vm.ResponseDetailOptions.Single(
                option => option.Value == ResponseDetailLevel.Detailed);

            Assert.Equal(label, vm.SelectedResponseDetailOption!.DisplayName);
            Assert.Equal("high", SessionConfigBuilder.CreateResponseVerbosity(data.Settings.ResponseDetail)?.Value);
            var json = JsonSerializer.Serialize(data.Settings, AppDataJsonContext.Default.UserSettings);
            using var document = JsonDocument.Parse(json);
            Assert.Equal((int)ResponseDetailLevel.Detailed,
                document.RootElement.GetProperty("responseDetail").GetInt32());
            Assert.DoesNotContain(label, json);
        }
        finally
        {
            Loc.Load("en");
        }
    }

    [Fact]
    public void UnknownDetail_IsRejectedInsteadOfSilentlySelectingAnotherLevel()
    {
        var data = new AppData();
        using var vm = CreateVm(data);
        var unknown = (ResponseDetailLevel)999;

        Assert.Throws<ArgumentOutOfRangeException>(() => vm.ResponseDetail = unknown);
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionConfigBuilder.CreateResponseVerbosity(unknown));
        Assert.Equal(ResponseDetailLevel.ModelDefault, vm.ResponseDetail);
        Assert.Equal(ResponseDetailLevel.ModelDefault, data.Settings.ResponseDetail);
    }
}
