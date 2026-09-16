using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class LazyMcpSettingsTests
{
    private static SettingsViewModel CreateVm(DataStore store, McpProxyRuntime? mcpProxyRuntime = null) => new(
        store, TestCopilot.Shared, new BrowserService(), new UpdateService(), mcpProxyRuntime: mcpProxyRuntime);

    [Fact]
    public void DefaultSettings_LeaveLazyInitializationOffAndUnmodified()
    {
        var data = new AppData();
        using var vm = CreateVm(new DataStore(data));

        Assert.False(data.Settings.UseLazyMcpInitialization);
        Assert.False(vm.UseLazyMcpInitialization);
        Assert.False(vm.IsUseLazyMcpInitializationModified);
        Assert.False(vm.UseMcpProxy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedPreference_LoadsRegardlessOfProxySetting(bool useMcpProxy)
    {
        var data = new AppData
        {
            Settings = new UserSettings
            {
                UseMcpProxy = useMcpProxy,
                UseLazyMcpInitialization = true
            }
        };
        using var vm = CreateVm(new DataStore(data));

        Assert.True(vm.UseLazyMcpInitialization);
        Assert.True(vm.IsUseLazyMcpInitializationModified);
        Assert.True(data.Settings.UseLazyMcpInitialization);
        Assert.Equal(useMcpProxy, vm.UseMcpProxy);
    }

    [Fact]
    public void EditingPreference_SavesAndNotifiesModifiedStateInBothDirections()
    {
        var data = new AppData { Settings = new UserSettings { UseMcpProxy = true } };
        var store = new DataStore(data);
        using var vm = CreateVm(store);
        var savedValues = new List<bool>();
        var changedProperties = new List<string?>();
        store.IndexSaved += () => savedValues.Add(data.Settings.UseLazyMcpInitialization);
        vm.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        vm.UseLazyMcpInitialization = true;

        Assert.True(data.Settings.UseLazyMcpInitialization);
        Assert.True(vm.IsUseLazyMcpInitializationModified);
        Assert.Equal(new[] { true }, savedValues);
        Assert.Contains(nameof(vm.UseLazyMcpInitialization), changedProperties);
        Assert.Contains(nameof(vm.IsUseLazyMcpInitializationModified), changedProperties);

        changedProperties.Clear();
        vm.UseLazyMcpInitialization = false;

        Assert.False(data.Settings.UseLazyMcpInitialization);
        Assert.False(vm.IsUseLazyMcpInitializationModified);
        Assert.Equal(new[] { true, false }, savedValues);
        Assert.Contains(nameof(vm.UseLazyMcpInitialization), changedProperties);
        Assert.Contains(nameof(vm.IsUseLazyMcpInitializationModified), changedProperties);
    }

    [Fact]
    public void DisablingAndReenablingProxy_PreservesLazyPreference()
    {
        var data = new AppData
        {
            Settings = new UserSettings { UseMcpProxy = true, UseLazyMcpInitialization = true }
        };
        using var vm = CreateVm(new DataStore(data));

        vm.UseMcpProxy = false;

        Assert.True(vm.UseLazyMcpInitialization);
        Assert.True(data.Settings.UseLazyMcpInitialization);
        Assert.True(vm.IsUseLazyMcpInitializationModified);

        vm.UseMcpProxy = true;

        Assert.True(vm.UseLazyMcpInitialization);
        Assert.True(data.Settings.UseLazyMcpInitialization);
        Assert.True(vm.IsUseLazyMcpInitializationModified);
    }

    [Fact]
    public async Task EditingRuntimeMode_RequestsSessionInvalidationWithoutClearingDiscoveryCache()
    {
        var directory = Directory.CreateTempSubdirectory("Lumi-lazy-mcp-mode-").FullName;
        try
        {
            var snapshotPath = Path.Combine(directory, new string('c', 64) + ".json");
            File.WriteAllText(snapshotPath, "{}");
            await using var runtime = new McpProxyRuntime(directory);
            using var vm = CreateVm(new DataStore(new AppData()), runtime);
            var invalidationRequests = 0;
            vm.McpRuntimeConfigurationChanged += () => invalidationRequests++;

            vm.UseMcpProxy = true;
            vm.UseLazyMcpInitialization = true;

            Assert.Equal(2, invalidationRequests);
            Assert.True(File.Exists(snapshotPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevertPreference_RestoresDefaultAndSavesWithoutChangingProxy(bool useMcpProxy)
    {
        var data = new AppData
        {
            Settings = new UserSettings
            {
                UseMcpProxy = useMcpProxy,
                UseLazyMcpInitialization = true
            }
        };
        var store = new DataStore(data);
        using var vm = CreateVm(store);
        var saveCount = 0;
        store.IndexSaved += () => saveCount++;

        vm.RevertUseLazyMcpInitializationCommand.Execute(null);

        Assert.False(vm.UseLazyMcpInitialization);
        Assert.False(data.Settings.UseLazyMcpInitialization);
        Assert.False(vm.IsUseLazyMcpInitializationModified);
        Assert.Equal(useMcpProxy, vm.UseMcpProxy);
        Assert.Equal(useMcpProxy, data.Settings.UseMcpProxy);
        Assert.Equal(1, saveCount);
    }

    [Fact]
    public void ResetAllSettings_RestoresBothMcpDefaults()
    {
        var data = new AppData
        {
            Settings = new UserSettings { UseMcpProxy = true, UseLazyMcpInitialization = true }
        };
        using var vm = CreateVm(new DataStore(data));

        vm.ResetSettingsCommand.Execute(null);

        Assert.False(vm.UseLazyMcpInitialization);
        Assert.False(data.Settings.UseLazyMcpInitialization);
        Assert.False(vm.IsUseLazyMcpInitializationModified);
        Assert.False(vm.UseMcpProxy);
        Assert.False(data.Settings.UseMcpProxy);
        Assert.False(vm.IsUseMcpProxyModified);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefreshCommand_TracksProxyAvailabilityWithoutChangingLazyPreference(bool useLazyInitialization)
    {
        var data = new AppData
        {
            Settings = new UserSettings { UseLazyMcpInitialization = useLazyInitialization }
        };
        using var vm = CreateVm(new DataStore(data));
        var canExecuteChanges = 0;
        vm.RefreshMcpToolsCommand.CanExecuteChanged += (_, _) => canExecuteChanges++;

        Assert.False(vm.RefreshMcpToolsCommand.CanExecute(null));

        vm.UseMcpProxy = true;

        Assert.True(vm.RefreshMcpToolsCommand.CanExecute(null));

        vm.UseMcpProxy = false;

        Assert.False(vm.RefreshMcpToolsCommand.CanExecute(null));
        Assert.Equal(2, canExecuteChanges);
        Assert.Equal(useLazyInitialization, vm.UseLazyMcpInitialization);
        Assert.Equal(useLazyInitialization, data.Settings.UseLazyMcpInitialization);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshCommand_ClearsSnapshotsBeforeRequestingReconnectAndPreservesPreferences(bool useLazyInitialization)
    {
        var directory = Directory.CreateTempSubdirectory("Lumi-lazy-mcp-settings-").FullName;
        try
        {
            var snapshotPath = Path.Combine(directory, new string('a', 64) + ".json");
            // Clearing stored snapshots must not require live discovery or a current catalog format.
            File.WriteAllText(snapshotPath, """
                {
                  "version": 1,
                  "createdAtUtc": "2025-06-18T00:00:00Z",
                  "initializeResult": {
                    "protocolVersion": "2025-06-18",
                    "capabilities": { "tools": { "listChanged": false } },
                    "serverInfo": { "name": "settings-test", "version": "1.0" }
                  },
                  "toolsResult": { "tools": [] }
                }
                """);
            await using var runtime = new McpProxyRuntime(directory);
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    UseMcpProxy = true,
                    UseLazyMcpInitialization = useLazyInitialization
                }
            };
            var store = new DataStore(data);
            using var vm = CreateVm(store, runtime);
            var saveCount = 0;
            var refreshRequests = 0;
            store.IndexSaved += () => saveCount++;
            vm.McpDiscoveryRefreshRequested += () =>
            {
                Assert.False(File.Exists(snapshotPath));
                refreshRequests++;
            };

            Assert.True(File.Exists(snapshotPath));
            Assert.Empty(vm.McpDiscoveryRefreshStatus);
            Assert.True(vm.RefreshMcpToolsCommand.CanExecute(null));

            await vm.RefreshMcpToolsCommand.ExecuteAsync(null);

            Assert.False(File.Exists(snapshotPath));
            Assert.Equal(1, refreshRequests);
            Assert.False(string.IsNullOrWhiteSpace(vm.McpDiscoveryRefreshStatus));
            Assert.Equal(Loc.SettingStatus_RefreshMcpTools, vm.McpDiscoveryRefreshStatus);
            Assert.False(vm.RefreshMcpToolsCommand.IsRunning);
            Assert.True(vm.RefreshMcpToolsCommand.CanExecute(null));
            Assert.True(vm.UseMcpProxy);
            Assert.True(data.Settings.UseMcpProxy);
            Assert.Equal(useLazyInitialization, vm.UseLazyMcpInitialization);
            Assert.Equal(useLazyInitialization, data.Settings.UseLazyMcpInitialization);
            Assert.Equal(0, saveCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

    }

    [SkippableFact]
    public async Task RefreshCommand_ShowsStorageFailureWithoutReconfiguringChats()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows file sharing is required to simulate a locked cache file.");
        var directory = Directory.CreateTempSubdirectory("Lumi-lazy-mcp-settings-failure-").FullName;
        try
        {
            var path = Path.Combine(directory, new string('b', 64) + ".json");
            await using var runtime = new McpProxyRuntime(directory);
            using var locked = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var vm = CreateVm(new DataStore(new AppData
            {
                Settings = new UserSettings { UseMcpProxy = true }
            }), runtime);
            var refreshRequests = 0;
            vm.McpDiscoveryRefreshRequested += () => refreshRequests++;

            await vm.RefreshMcpToolsCommand.ExecuteAsync(null);

            Assert.Equal(0, refreshRequests);
            Assert.NotEmpty(vm.McpDiscoveryRefreshStatus);
            Assert.NotEqual(Loc.SettingStatus_RefreshMcpTools, vm.McpDiscoveryRefreshStatus);
            Assert.False(vm.RefreshMcpToolsCommand.IsRunning);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
