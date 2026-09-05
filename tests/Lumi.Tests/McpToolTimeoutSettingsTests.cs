using System;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class McpToolTimeoutSettingsTests
{
    private static SettingsViewModel CreateVm(AppData data) => new(
        new DataStore(data), TestCopilot.Shared, new BrowserService(), new UpdateService());

    [Fact]
    public void DefaultAndEditedTimeout_SaveAndRevert()
    {
        var data = new AppData();
        using var vm = CreateVm(data);
        Assert.Equal(180m, vm.McpToolTimeoutSeconds);
        Assert.False(vm.IsMcpToolTimeoutModified);

        vm.McpToolTimeoutSeconds = 600;
        Assert.Equal(600, data.Settings.McpToolTimeoutSeconds);
        Assert.True(vm.IsMcpToolTimeoutModified);

        vm.RevertMcpToolTimeoutCommand.Execute(null);
        Assert.Equal(180m, vm.McpToolTimeoutSeconds);
        Assert.Equal(180, data.Settings.McpToolTimeoutSeconds);
        Assert.False(vm.IsMcpToolTimeoutModified);
    }

    [Fact]
    public void SavedTimeout_LoadsAndResetsWithAllSettings()
    {
        var data = new AppData();
        data.Settings.McpToolTimeoutSeconds = 600;
        using var vm = CreateVm(data);
        Assert.Equal(600m, vm.McpToolTimeoutSeconds);
        Assert.True(vm.IsMcpToolTimeoutModified);

        vm.ResetSettingsCommand.Execute(null);

        Assert.Equal(180m, vm.McpToolTimeoutSeconds);
        Assert.Equal(180, data.Settings.McpToolTimeoutSeconds);
        Assert.False(vm.IsMcpToolTimeoutModified);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(86400)]
    public void Boundaries_AreAccepted(int seconds)
    {
        var data = new AppData();
        using var vm = CreateVm(data);
        vm.McpToolTimeoutSeconds = seconds;
        Assert.Equal(seconds, data.Settings.McpToolTimeoutSeconds);
    }

    [Fact]
    public void InvalidEdits_DoNotChangeStoredOrDisplayedValue()
    {
        var data = new AppData();
        using var vm = CreateVm(data);
        vm.McpToolTimeoutSeconds = 600;

        foreach (var value in new decimal?[] { null, 0, -1, 86401, 180.5m })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => vm.McpToolTimeoutSeconds = value);
            Assert.Equal(600m, vm.McpToolTimeoutSeconds);
            Assert.Equal(600, data.Settings.McpToolTimeoutSeconds);
        }
    }
}
