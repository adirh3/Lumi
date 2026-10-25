using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Capabilities;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

public sealed class PersonalSkillDirectoryTests
{
    [Fact]
    public async Task SkillDiscovery_CancellationDoesNotWaitForAnUnresponsiveFolderProbe()
    {
        await using var client = new CopilotClient(new CopilotClientOptions
        {
            Mode = CopilotClientMode.Empty,
            BaseDirectory = Path.Combine(TestProfile.Root, "cancelled-skill-discovery"),
            UseLoggedInUser = false,
            Connection = RuntimeConnection.ForStdio(),
        });
        var provider = new CopilotSdkCapabilityProvider(() => client);
        var folders = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var method = typeof(CopilotSdkCapabilityProvider).GetMethod(
            "LoadSkillsAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var discovery = Assert.IsAssignableFrom<Task>(method.Invoke(provider,
            [client, new List<string>(), folders.Task, cancellation.Token]));
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => discovery.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            folders.TrySetCanceled(cancellation.Token);
            try { await discovery; }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public void Defaults_UseStandardSkillBundleFolders()
    {
        var home = Path.Combine(Path.GetTempPath(), "skill-home");
        var folders = PersonalSkillFolders.GetDefaults(home, _ => null);

        Assert.Equal(
        [
            Path.Combine(home, ".claude", "skills"),
            Path.Combine(home, ".codex", "skills"),
            Path.Combine(home, ".cursor", "skills"),
            Path.Combine(home, ".config", "opencode", "skills"),
        ], folders);
    }

    [Fact]
    public void Defaults_HonorTheToolsConfiguredHomes()
    {
        var home = Path.Combine(Path.GetTempPath(), "skill-home");
        var paths = new Dictionary<string, string>
        {
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, "claude-custom"),
            ["CODEX_HOME"] = Path.Combine(home, "codex-custom"),
            ["XDG_CONFIG_HOME"] = Path.Combine(home, "xdg-custom"),
        };
        string? Read(string name) => paths.GetValueOrDefault(name);

        Assert.Equal(
        [
            Path.Combine(home, "claude-custom", "skills"),
            Path.Combine(home, "codex-custom", "skills"),
            Path.Combine(home, ".cursor", "skills"),
            Path.Combine(home, "xdg-custom", "opencode", "skills"),
        ], PersonalSkillFolders.GetDefaults(home, Read));

        paths["OPENCODE_CONFIG_DIR"] = Path.Combine(home, "opencode-custom");
        Assert.Equal(Path.Combine(home, "opencode-custom", "skills"),
            PersonalSkillFolders.GetDefaults(home, Read)[3]);
    }

    [Fact]
    public void ExistingDirectories_AreNormalizedDeduplicatedAndNeverCreated()
    {
        var root = Path.Combine(Path.GetTempPath(), $"Lumi-skill-folders-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var missing = Path.Combine(root, "not-installed");
        try
        {
            var roots = PersonalSkillFolders.GetExistingDirectories(
                [root, root + Path.DirectorySeparatorChar, Path.Combine(root, ".", ""), missing, "", "relative"]);

            Assert.Equal([root], roots);
            Assert.False(Directory.Exists(missing));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root); }
    }

    [Fact]
    public void Settings_AddRemoveClearAndRevertPersistAndNotify()
    {
        var data = new AppData();
        using var vm = CreateViewModel(data);
        var defaults = data.Settings.PersonalSkillDirectories.ToArray();
        var folder = Path.Combine(Path.GetTempPath(), "additional-personal-skills");
        var changes = 0;
        vm.PersonalSkillDirectoriesChanged += () => changes++;

        Assert.Equal(defaults, vm.PersonalSkillDirectories);
        Assert.False(vm.IsPersonalSkillDirectoriesModified);
        vm.NewPersonalSkillDirectory = folder;
        vm.AddPersonalSkillDirectoryCommand.Execute(null);
        Assert.Contains(folder, data.Settings.PersonalSkillDirectories);
        Assert.True(vm.IsPersonalSkillDirectoriesModified);
        Assert.Equal("", vm.NewPersonalSkillDirectory);
        Assert.Equal(1, changes);

        vm.AddPersonalSkillDirectories([folder + Path.DirectorySeparatorChar]);
        Assert.Single(vm.PersonalSkillDirectories, path => path == folder);
        Assert.NotNull(vm.PersonalSkillDirectoryError);
        Assert.Equal(1, changes);

        foreach (var path in vm.PersonalSkillDirectories.ToArray())
            vm.RemovePersonalSkillDirectoryCommand.Execute(path);
        Assert.Empty(data.Settings.PersonalSkillDirectories);

        using var reopened = CreateViewModel(data);
        Assert.Empty(reopened.PersonalSkillDirectories);
        vm.RevertPersonalSkillDirectoriesCommand.Execute(null);
        Assert.Equal(defaults, data.Settings.PersonalSkillDirectories);
        Assert.False(vm.IsPersonalSkillDirectoriesModified);
        Assert.Null(vm.PersonalSkillDirectoryError);

        var beforeRefresh = changes;
        vm.RefreshPersonalSkillsCommand.Execute(null);
        Assert.Equal(beforeRefresh + 1, changes);
    }

    [Fact]
    public void InvalidFolderEdits_AreVisibleAndDoNotChangeTheSavedList()
    {
        var data = new AppData { Settings = new UserSettings { PersonalSkillDirectories = [] } };
        using var vm = CreateViewModel(data);

        vm.NewPersonalSkillDirectory = "relative-folder";
        vm.AddPersonalSkillDirectoryCommand.Execute(null);

        Assert.NotNull(vm.PersonalSkillDirectoryError);
        Assert.Empty(data.Settings.PersonalSkillDirectories);
        Assert.Equal("relative-folder", vm.NewPersonalSkillDirectory);

        vm.AddPersonalSkillDirectories([Path.GetTempPath(), "relative"]);
        Assert.Empty(data.Settings.PersonalSkillDirectories);
    }

    [Fact]
    public void ResetSettings_RestoresTheDefaultSkillFolders()
    {
        var data = new AppData { Settings = new UserSettings { PersonalSkillDirectories = [] } };
        using var vm = CreateViewModel(data);

        vm.ResetSettingsCommand.Execute(null);

        Assert.Equal(PersonalSkillFolders.GetDefaults(), data.Settings.PersonalSkillDirectories);
        Assert.Equal(data.Settings.PersonalSkillDirectories, vm.PersonalSkillDirectories);
        Assert.False(vm.IsPersonalSkillDirectoriesModified);
    }

    private static SettingsViewModel CreateViewModel(AppData data)
        => new(new DataStore(data), TestCopilot.Shared, new BrowserService(), new UpdateService());
}

[Collection("Headless UI")]
public sealed class PersonalSkillDirectoryUiTests
{
    [Fact]
    public async Task SharedSettingsWindows_PreserveEachOthersFolderEditsAndDrafts()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var data = new AppData { Settings = new UserSettings { PersonalSkillDirectories = [] } };
            var store = new DataStore(data);
            using var first = new SettingsViewModel(
                store, TestCopilot.Shared, new BrowserService(), new UpdateService());
            using var second = new SettingsViewModel(
                store, TestCopilot.Shared, new BrowserService(), new UpdateService());
            var firstFolder = Path.Combine(TestProfile.Root, "first-window-skills");
            var secondFolder = Path.Combine(TestProfile.Root, "second-window-skills");

            first.AddPersonalSkillDirectories([firstFolder]);
            second.AddPersonalSkillDirectories([secondFolder]);

            Assert.Equal([firstFolder, secondFolder], data.Settings.PersonalSkillDirectories);
            Assert.Equal(data.Settings.PersonalSkillDirectories, first.PersonalSkillDirectories);
            Assert.Equal(data.Settings.PersonalSkillDirectories, second.PersonalSkillDirectories);

            first.NewPersonalSkillDirectory = "unfinished folder path";
            second.RemovePersonalSkillDirectoryCommand.Execute(firstFolder);
            Assert.Equal([secondFolder], first.PersonalSkillDirectories);
            Assert.Equal("unfinished folder path", first.NewPersonalSkillDirectory);
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("he")]
    public async Task PopulatedSettings_AreLocalizedSearchableAndInteractive(string language)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load(language);
            var folder = Path.Combine(Path.GetTempPath(), "personal-skill-ui");
            var data = new AppData
            {
                Settings = new UserSettings { PersonalSkillDirectories = [folder] },
            };
            using var vm = new SettingsViewModel(
                new DataStore(data), TestCopilot.Shared, new BrowserService(), new UpdateService())
            {
                SelectedPageIndex = 4,
            };
            using var view = new SettingsView { DataContext = vm };
            var window = new Window { Content = view, Width = 1100, Height = 1000 };
            window.Show();
            try
            {
                Pump(window);
                var setting = view.FindSettingsControl<StrataSetting>("PersonalSkillFoldersSetting")!;
                Assert.Equal(Loc.Setting_PersonalSkillFolders, setting.Header);
                Assert.Same(vm.PersonalSkillDirectories,
                    view.FindSettingsControl<ItemsControl>("PersonalSkillFoldersList")!.ItemsSource);
                Assert.Equal(Loc.Setting_SkillFolders_Browse,
                    view.FindSettingsControl<Button>("BrowsePersonalSkillFoldersButton")!.Content);
                Assert.Equal(Loc.Setting_SkillFolders_Refresh,
                    view.FindSettingsControl<Button>("RefreshPersonalSkillsButton")!.Content);

                vm.SearchQuery = "Claude";
                Pump(window);
                Assert.True(setting.IsEffectivelyVisible);
                Assert.Equal([folder], data.Settings.PersonalSkillDirectories);

                vm.SearchQuery = "";
                Pump(window);
                vm.NewPersonalSkillDirectory = Path.Combine(Path.GetTempPath(), "personal-skill-ui-added");
                var add = view.FindSettingsControl<Button>("AddPersonalSkillFolderButton")!;
                Assert.True(add.Command!.CanExecute(null));
                add.Command.Execute(null);
                Pump(window);
                Assert.Equal(2, data.Settings.PersonalSkillDirectories.Count);
                vm.RemovePersonalSkillDirectoryCommand.Execute(folder);
                Assert.DoesNotContain(folder, data.Settings.PersonalSkillDirectories);
            }
            finally
            {
                window.Close();
                Loc.Load("en");
            }
        }, CancellationToken.None);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
