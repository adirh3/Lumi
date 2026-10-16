using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Appearance;
using Lumi.ViewModels;
using Lumi.Views;
using Lumi.Views.Settings;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class SettingsCustomizationTests
{
    private static SettingsViewModel CreateViewModel(DataStore dataStore)
        => new(
            dataStore,
            TestCopilot.Shared,
            new BrowserService(),
            new UpdateService(),
            new FakeSecureKeyStore());

    [Fact]
    public async Task AccentPalette_IsOneMergedDictionarySwappedWhole()
    {
        using var session = HeadlessTestSession.Start();
        var results = new List<object?>();

        await session.Dispatch(() =>
        {
            var resources = new ResourceDictionary();

            AccentPalettes.Apply(resources, "ocean");
            results.Add(resources.MergedDictionaries.Count);
            results.Add(AccentPalettes.AppliedId(resources));
            resources.TryGetResource("Color.AccentDefault", ThemeVariant.Dark, out var dark);
            resources.TryGetResource("Color.AccentDefault", ThemeVariant.Light, out var light);
            results.Add(dark);
            results.Add(light);

            AccentPalettes.Apply(resources, "sunset");
            results.Add(resources.MergedDictionaries.Count);
            results.Add(AccentPalettes.AppliedId(resources));

            AccentPalettes.Apply(resources, AccentPalettes.DefaultId);
            results.Add(resources.MergedDictionaries.Count);
            results.Add(AccentPalettes.AppliedId(resources));
        }, CancellationToken.None);

        var ocean = AccentPalettes.Find("ocean");
        Assert.Equal<object?>(
            [1, "ocean", ocean.Dark.Accent, ocean.Light.Accent, 1, "sunset", 0, AccentPalettes.DefaultId],
            results);
    }

    [Theory]
    [InlineData(null, "lumi")]
    [InlineData("", "lumi")]
    [InlineData("OCEAN", "ocean")]
    [InlineData("neon", "lumi")]
    public void AccentIds_NormalizeToAKnownPalette(string? id, string expected)
        => Assert.Equal(expected, AccentPalettes.Normalize(id));

    [Fact]
    public async Task AccentColor_RecolorsTheAppAndRevertsToLumi()
    {
        using var session = HeadlessTestSession.Start();
        string? appliedAfterChange = null, savedAfterChange = null, selectedAfterChange = null;
        string? appliedAfterRevert = null, normalizedUnknown = null;
        bool modifiedAfterChange = false, customizedAfterChange = false, modifiedAfterRevert = true;

        await session.Dispatch(() =>
        {
            var data = new AppData();
            using var viewModel = CreateViewModel(new DataStore(data));
            var resources = Application.Current!.Resources;
            try
            {
                viewModel.AccentOptions.Single(option => option.Id == "forest").SelectCommand.Execute(null);
                appliedAfterChange = AccentPalettes.AppliedId(resources);
                savedAfterChange = data.Settings.AccentColor;
                selectedAfterChange = string.Join(",", viewModel.AccentOptions.Where(o => o.IsSelected).Select(o => o.Id));
                modifiedAfterChange = viewModel.IsAccentColorModified;
                customizedAfterChange = viewModel.IsAppearanceCustomized;

                viewModel.RevertAccentColorCommand.Execute(null);
                appliedAfterRevert = AccentPalettes.AppliedId(resources);
                modifiedAfterRevert = viewModel.IsAccentColorModified;

                viewModel.AccentColor = "neon";
                normalizedUnknown = viewModel.AccentColor;
            }
            finally
            {
                AccentPalettes.Apply(resources, AccentPalettes.DefaultId);
            }
        }, CancellationToken.None);

        Assert.Equal("forest", appliedAfterChange);
        Assert.Equal("forest", savedAfterChange);
        Assert.Equal("forest", selectedAfterChange);
        Assert.True(modifiedAfterChange);
        Assert.True(customizedAfterChange);
        Assert.Equal(AccentPalettes.DefaultId, appliedAfterRevert);
        Assert.False(modifiedAfterRevert);
        Assert.Equal(AccentPalettes.DefaultId, normalizedUnknown);
    }

    [Fact]
    public async Task SystemTheme_FollowsTheComputerUntilLightOrDarkIsChosen()
    {
        using var session = HeadlessTestSession.Start();
        var steps = new List<string>();

        await session.Dispatch(() =>
        {
            var data = new AppData();
            using var viewModel = CreateViewModel(new DataStore(data));
            viewModel.SystemPrefersDark = () => false;
            string State() => $"{(viewModel.IsSystemThemeSelected ? "system" : viewModel.IsDarkThemeSelected ? "dark" : "light")}" +
                              $":{(viewModel.IsDarkTheme ? "dark" : "light")}:{data.Settings.FollowSystemTheme}";

            viewModel.SelectThemeCommand.Execute("system");
            steps.Add(State());

            viewModel.ApplySystemTheme(true);
            steps.Add(State());

            viewModel.SelectThemeCommand.Execute("light");
            viewModel.ApplySystemTheme(true);
            steps.Add(State());

            viewModel.SelectThemeCommand.Execute("system");
            viewModel.IsDarkTheme = true;
            steps.Add(State());
        }, CancellationToken.None);

        Assert.Equal(
            ["system:light:True", "system:dark:True", "light:light:False", "dark:dark:False"],
            steps);
    }

    [Fact]
    public async Task SystemTheme_FollowsTheSavedModeAcrossSettingsWindows()
    {
        using var session = HeadlessTestSession.Start();
        bool followsAfterOtherWindowLeft = false, darkAfterOtherWindowLeft = false;
        bool followsAfterLightElsewhere = true, darkAfterLightElsewhere = true;

        await session.Dispatch(() =>
        {
            var store = new DataStore(new AppData());
            using var stays = CreateViewModel(store);
            using (var leaves = CreateViewModel(store))
            {
                leaves.SystemPrefersDark = () => false;
                leaves.SelectThemeCommand.Execute("system");
            }

            // The window that chose System is gone; the computer turns dark.
            stays.ApplySystemTheme(true);
            followsAfterOtherWindowLeft = store.Data.Settings.FollowSystemTheme;
            darkAfterOtherWindowLeft = store.Data.Settings.IsDarkTheme;

            // This window never saw System chosen, but light here still stops following it.
            stays.SelectThemeCommand.Execute("light");
            stays.ApplySystemTheme(true);
            followsAfterLightElsewhere = store.Data.Settings.FollowSystemTheme;
            darkAfterLightElsewhere = store.Data.Settings.IsDarkTheme;
        }, CancellationToken.None);

        Assert.True(followsAfterOtherWindowLeft);
        Assert.True(darkAfterOtherWindowLeft);
        Assert.False(followsAfterLightElsewhere);
        Assert.False(darkAfterLightElsewhere);
    }

    [Theory]
    [InlineData(UserSettings.PresenceBrightnessSubtle, 0.6)]
    [InlineData(UserSettings.PresenceBrightnessBalanced, 1.0)]
    [InlineData(UserSettings.PresenceBrightnessVivid, 1.45)]
    [InlineData("blinding", 1.0)]
    public void PresenceBrightness_ScalesTheTunedGlow(string brightness, double expected)
        => Assert.Equal(expected, SettingsViewModel.PresenceBrightnessMultiplier(brightness));

    [Fact]
    public async Task PresenceBrightness_RaisesItsMultiplierAndSynchronizesSettingsWindows()
    {
        using var session = HeadlessTestSession.Start();
        var raised = new List<double>();
        string? secondBrightness = null, secondSelected = null, saved = null, normalized = null;

        await session.Dispatch(() =>
        {
            var store = new DataStore(new AppData());
            using var first = CreateViewModel(store);
            using var second = CreateViewModel(store);
            first.PresenceBrightnessChanged += raised.Add;
            second.PresenceBrightnessChanged += raised.Add;

            first.PresenceBrightness = UserSettings.PresenceBrightnessVivid;
            secondBrightness = second.PresenceBrightness;
            secondSelected = string.Join(",", second.PresenceBrightnessOptions.Where(o => o.IsSelected).Select(o => o.Id));
            saved = store.Data.Settings.PresenceBrightness;

            first.PresenceBrightness = "blinding";
            normalized = first.PresenceBrightness;
        }, CancellationToken.None);

        // Chat surfaces are shared by every window, so only the window that changed it applies it.
        Assert.Equal([1.45, 1.0], raised);
        Assert.Equal(UserSettings.PresenceBrightnessVivid, secondBrightness);
        Assert.Equal(UserSettings.PresenceBrightnessVivid, secondSelected);
        Assert.Equal(UserSettings.PresenceBrightnessVivid, saved);
        Assert.Equal(UserSettings.PresenceBrightnessBalanced, normalized);
    }

    [Fact]
    public async Task RestoreDefaultLook_ResetsAppearanceButNotChatBehavior()
    {
        using var session = HeadlessTestSession.Start();
        var after = new Dictionary<string, object?>();

        await session.Dispatch(() =>
        {
            var data = new AppData();
            using var viewModel = CreateViewModel(new DataStore(data));
            try
            {
                viewModel.SelectThemeCommand.Execute("light");
                viewModel.AccentColor = "grape";
                viewModel.WindowMaterial = UserSettings.WindowMaterialSolid;
                viewModel.IsCompactDensity = true;
                viewModel.PresenceBrightness = UserSettings.PresenceBrightnessSubtle;
                viewModel.SendWithEnter = false;
                after["customized"] = viewModel.IsAppearanceCustomized;

                viewModel.ResetAppearanceCommand.Execute(null);

                after["dark"] = viewModel.IsDarkTheme;
                after["accent"] = data.Settings.AccentColor;
                after["material"] = data.Settings.WindowMaterial;
                after["compact"] = data.Settings.IsCompactDensity;
                after["brightness"] = data.Settings.PresenceBrightness;
                after["sendWithEnter"] = data.Settings.SendWithEnter;
                after["customizedAfter"] = viewModel.IsAppearanceCustomized;
            }
            finally
            {
                AccentPalettes.Apply(Application.Current!.Resources, AccentPalettes.DefaultId);
            }
        }, CancellationToken.None);

        var defaults = new UserSettings();
        Assert.Equal(true, after["customized"]);
        Assert.Equal(defaults.IsDarkTheme, after["dark"]);
        Assert.Equal(defaults.AccentColor, after["accent"]);
        Assert.Equal(defaults.WindowMaterial, after["material"]);
        Assert.Equal(defaults.IsCompactDensity, after["compact"]);
        Assert.Equal(defaults.PresenceBrightness, after["brightness"]);
        Assert.Equal(false, after["sendWithEnter"]);
        Assert.Equal(false, after["customizedAfter"]);
    }

    [Fact]
    public async Task DangerZone_ActsOnlyAfterConfirmation()
    {
        using var session = HeadlessTestSession.Start();
        int memoriesAfterRequest = 0, memoriesAfterCancel = 0, memoriesAfterConfirm = -1;
        bool confirmingAfterRequest = false, confirmingAfterPageChange = true, confirmingAfterConfirm = true;
        string? confirmText = null, ignored = "unset";

        await session.Dispatch(() =>
        {
            var data = new AppData
            {
                Memories = [new Memory { Key = "a", Content = "1" }, new Memory { Key = "b", Content = "2" }],
            };
            using var viewModel = CreateViewModel(new DataStore(data));

            viewModel.RequestDangerActionCommand.Execute(SettingsViewModel.DangerClearMemories);
            confirmingAfterRequest = viewModel.IsConfirmingDanger;
            confirmText = viewModel.DangerConfirmText;
            memoriesAfterRequest = data.Memories.Count;

            viewModel.CancelDangerActionCommand.Execute(null);
            memoriesAfterCancel = data.Memories.Count;

            viewModel.RequestDangerActionCommand.Execute(SettingsViewModel.DangerClearMemories);
            viewModel.SelectedPageIndex = 1;
            confirmingAfterPageChange = viewModel.IsConfirmingDanger;

            viewModel.RequestDangerActionCommand.Execute("format-disk");
            ignored = viewModel.PendingDangerAction;

            viewModel.RequestDangerActionCommand.Execute(SettingsViewModel.DangerClearMemories);
            viewModel.ConfirmDangerActionCommand.Execute(null);
            confirmingAfterConfirm = viewModel.IsConfirmingDanger;
            memoriesAfterConfirm = data.Memories.Count;
        }, CancellationToken.None);

        Assert.True(confirmingAfterRequest);
        Assert.False(string.IsNullOrWhiteSpace(confirmText));
        Assert.Equal(2, memoriesAfterRequest);
        Assert.Equal(2, memoriesAfterCancel);
        Assert.False(confirmingAfterPageChange);
        Assert.Null(ignored);
        Assert.False(confirmingAfterConfirm);
        Assert.Equal(0, memoriesAfterConfirm);
    }

    [Theory]
    [InlineData("notif", "Enable Notifications", null, true)]
    [InlineData("notifcations", "Enable Notifications", null, true)]
    [InlineData("fontsize", "Font Size", null, true)]
    [InlineData("dark mode", "Dark Mode", null, true)]
    [InlineData("font", "Fast Connector Initialization", null, false)]
    [InlineData("delete", "Expand Reasoning While Streaming", null, false)]
    [InlineData("port", "Import Browser Cookies", null, false)]
    [InlineData("model", "Mode", null, false)]
    [InlineData("theme", "Show Timestamps", null, false)]
    [InlineData("glow", "Ambient Presence", "Lumi's glow behind the conversation", true)]
    [InlineData("low", "Ambient Presence", "Lumi's glow behind the conversation", false)]
    [InlineData("phone glow", "Ambient Presence", "Lumi's glow behind the conversation", false)]
    [InlineData("presence glow", "Ambient Presence", "Lumi's glow behind the conversation", true)]
    [InlineData("  ", "Ambient Presence", null, false)]
    public void SettingsSearch_MatchesNamesLooselyAndSentencesByWordStart(
        string query, string name, string? description, bool expected)
        => Assert.Equal(expected, SettingsSearch.Matches(query, name, description));

    [Fact]
    public async Task SettingsSearch_FindsRowsByTheirChoicesAndKeepsProviderMatchesOffTheRows()
    {
        using var session = HeadlessTestSession.Start();
        bool accentShown = false, accentHighlighted = false, accountsShown = false;
        bool byokOnlyShown = true, byokOnlyHighlighted = true;

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            using var viewModel = CreateViewModel(new DataStore(new AppData()));
            viewModel.LoginVM = new GitHubLoginViewModel(TestCopilot.Shared);
            // Typed before the page is built, as a search started on Memories opens it.
            viewModel.SearchQuery = "ocean";
            var view = new SettingsView { DataContext = viewModel };
            var window = new Window { Width = 900, Height = 760, Content = view };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var accent = view.GetLogicalDescendants().OfType<StrataSetting>()
                    .Single(setting => setting.Header == Loc.Setting_AccentColor);
                accentShown = accent.IsEffectivelyVisible;
                accentHighlighted = accent.IsHighlighted;

                viewModel.SearchQuery = "OpenRouter";
                window.UpdateLayout();
                accountsShown = view.FindControl<StrataSettingGroup>("AiAccountsGroup")!.IsEffectivelyVisible;
                var byokOnly = view.FindControl<StrataSetting>("ByokOnlySetting")!;
                byokOnlyShown = byokOnly.IsEffectivelyVisible;
                byokOnlyHighlighted = byokOnly.IsHighlighted;
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

        Assert.True(accentShown);
        Assert.True(accentHighlighted);
        Assert.True(accountsShown);
        Assert.False(byokOnlyShown);
        Assert.False(byokOnlyHighlighted);
    }

    [Fact]
    public async Task SettingsSearch_FromMemoriesShowsResultsOnTheSettingsPage()
    {
        using var session = HeadlessTestSession.Start();
        var steps = new List<string>();

        await session.Dispatch(() =>
        {
            var data = new AppData
            {
                Settings = new UserSettings { IsOnboarded = true, AutoSaveChats = false, EnableMemoryAutoSave = false },
            };
            using var main = new MainViewModel(new DataStore(data), TestCopilot.Shared, new UpdateService(),
                startBackgroundJobs: false, initializeCopilotOnStartup: false);
            void Step(string name) => steps.Add($"{name}:{main.SelectedNavIndex}:{main.SettingsVM.IsSearching}");

            main.SelectedNavIndex = MainViewModel.MemoriesNavIndex;
            Step("memories");
            main.SettingsVM.SearchQuery = "theme";
            Step("typed");
            main.OpenSettingsCommand.Execute(null);
            Step("settings place");
            main.SettingsVM.SearchQuery = "";
            Step("cleared");
            main.SettingsVM.SearchQuery = "theme";
            main.SelectedNavIndex = MainViewModel.MemoriesNavIndex;
            Step("memories again");
        }, CancellationToken.None);

        Assert.Equal(
            ["memories:5:False", "typed:7:True", "settings place:7:True", "cleared:5:False", "memories again:5:False"],
            steps);
    }

    [Fact]
    public async Task AccentAndWindowMaterial_FollowAcrossSettingsWindows()
    {
        using var session = HeadlessTestSession.Start();
        string? secondAccent = null, secondSelected = null, secondMaterial = null;
        string? firstAccentAfterReset = null, savedAfterReset = null;
        bool secondModified = false;

        await session.Dispatch(() =>
        {
            var store = new DataStore(new AppData());
            using var first = CreateViewModel(store);
            using var second = CreateViewModel(store);
            try
            {
                first.AccentColor = "ocean";
                first.WindowMaterial = UserSettings.WindowMaterialSolid;
                secondAccent = second.AccentColor;
                secondSelected = string.Join(",", second.AccentOptions.Where(o => o.IsSelected).Select(o => o.Id));
                secondMaterial = second.WindowMaterial;
                secondModified = second.IsAccentColorModified;

                // A reset in the other window resets what the first one chose.
                second.ResetAppearanceCommand.Execute(null);
                firstAccentAfterReset = first.AccentColor;
                savedAfterReset = store.Data.Settings.AccentColor;
            }
            finally
            {
                AccentPalettes.Apply(Application.Current!.Resources, AccentPalettes.DefaultId);
            }
        }, CancellationToken.None);

        Assert.Equal("ocean", secondAccent);
        Assert.Equal("ocean", secondSelected);
        Assert.Equal(UserSettings.WindowMaterialSolid, secondMaterial);
        Assert.True(secondModified);
        Assert.Equal(AccentPalettes.DefaultId, firstAccentAfterReset);
        Assert.Equal(AccentPalettes.DefaultId, savedAfterReset);
    }
}
