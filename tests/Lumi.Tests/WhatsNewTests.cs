using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Converters;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.WhatsNew;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

public sealed class WhatsNewScheduleTests
{
    private static readonly Version V101 = new(1, 0, 1);
    private static readonly Version V102 = new(1, 0, 2);
    private static readonly Version V103 = new(1, 0, 3);

    private static WhatsNewItem Item(string key, bool spotlight = false, WhatsNewPlatforms platforms = WhatsNewPlatforms.All)
        => new(nameof(StrataIcons.Image), key, key) { IsSpotlight = spotlight, Platforms = platforms };

    private static readonly IReadOnlyList<WhatsNewRelease> Releases =
    [
        new("1.0.3", [Item("c1"), Item("c2")]),
        new("1.0.2", [Item("b-spot", spotlight: true), Item("b1")]),
        new("1.0.1", [Item("a1")]),
    ];

    [Fact]
    public void ProfilesFromBeforeWhatsNewSeeEverythingSince10()
    {
        var settings = new UserSettings { IsOnboarded = true };

        var since = WhatsNewSchedule.PendingSince(settings, V102, WhatsNewCatalog.Releases, WhatsNewPlatforms.Windows);

        Assert.Equal(WhatsNewSchedule.Baseline, since);
        Assert.Equal("", settings.LastSeenWhatsNewVersion);
        var selection = WhatsNewSchedule.Select(WhatsNewCatalog.Releases, since!, V102, WhatsNewPlatforms.Windows);
        Assert.Equal(V102, selection.Version);
        Assert.Equal(WhatsNewDestination.PhoneSettings, selection.Spotlight?.Destination);
        Assert.Equal(
            [
                nameof(Loc.WhatsNew_Images_Title), nameof(Loc.WhatsNew_Queue_Title), nameof(Loc.WhatsNew_Rail_Title),
                nameof(Loc.WhatsNew_Fusion_Title), nameof(Loc.WhatsNew_FollowUps_Title),
                nameof(Loc.WhatsNew_Detail_Title), nameof(Loc.WhatsNew_Jobs_Title),
            ],
            selection.Highlights.Select(item => item.TitleKey));
    }

    [Fact]
    public void AnUpdateShowsOnlyWhatCameOutSinceTheVersionLastSeen()
    {
        var settings = new UserSettings { IsOnboarded = true, LastSeenWhatsNewVersion = "1.0.2" };

        var since = WhatsNewSchedule.PendingSince(settings, V103, Releases, WhatsNewPlatforms.Linux);
        var selection = WhatsNewSchedule.Select(Releases, since!, V103, WhatsNewPlatforms.Linux);

        Assert.Equal(V102, since);
        Assert.Null(selection.Spotlight);
        Assert.Equal(["c1", "c2"], selection.Highlights.Select(item => item.TitleKey));

        // Skipping versions shows them all, newest first, and an older release's spotlight still leads.
        var skipped = WhatsNewSchedule.Select(Releases, new Version(1, 0, 0), V103, WhatsNewPlatforms.Linux);
        Assert.Equal("b-spot", skipped.Spotlight?.TitleKey);
        Assert.Equal(["c1", "c2", "b1", "a1"], skipped.Highlights.Select(item => item.TitleKey));
    }

    [Fact]
    public void ANewProfileStartsAtTheVersionItSetsLumiUpWith()
    {
        var settings = new UserSettings { IsOnboarded = false };

        Assert.Null(WhatsNewSchedule.PendingSince(settings, V102, Releases, WhatsNewPlatforms.Windows));
        Assert.Equal("1.0.2", settings.LastSeenWhatsNewVersion);
    }

    [Fact]
    public void NothingShowsWithoutAnUpdate_AndAnUpdateWithNothingToShowCountsAsSeen()
    {
        var current = new UserSettings { IsOnboarded = true, LastSeenWhatsNewVersion = "1.0.2" };
        Assert.Null(WhatsNewSchedule.PendingSince(current, V102, Releases, WhatsNewPlatforms.Windows));
        Assert.Equal("1.0.2", current.LastSeenWhatsNewVersion);

        var newer = new UserSettings { IsOnboarded = true, LastSeenWhatsNewVersion = "1.0.4" };
        Assert.Null(WhatsNewSchedule.PendingSince(newer, V103, Releases, WhatsNewPlatforms.Windows));
        Assert.Equal("1.0.4", newer.LastSeenWhatsNewVersion);

        IReadOnlyList<WhatsNewRelease> quiet = [new("1.0.4", []), .. Releases];
        var updated = new UserSettings { IsOnboarded = true, LastSeenWhatsNewVersion = "1.0.3" };
        Assert.Null(WhatsNewSchedule.PendingSince(updated, new Version(1, 0, 4), quiet, WhatsNewPlatforms.Windows));
        Assert.Equal("1.0.4", updated.LastSeenWhatsNewVersion);
    }

    [Fact]
    public void ItemsForOtherPlatformsAreLeftOut()
    {
        IReadOnlyList<WhatsNewRelease> releases =
        [
            new("1.0.3", [Item("windows-only", platforms: WhatsNewPlatforms.Windows), Item("everywhere")]),
        ];

        Assert.Equal(["windows-only", "everywhere"],
            WhatsNewSchedule.Select(releases, V102, V103, WhatsNewPlatforms.Windows).Highlights.Select(item => item.TitleKey));
        Assert.Equal(["everywhere"],
            WhatsNewSchedule.Select(releases, V102, V103, WhatsNewPlatforms.MacOS).Highlights.Select(item => item.TitleKey));
    }

    [Fact]
    public void ALongSkipShowsTheNewestHighlights()
    {
        IReadOnlyList<WhatsNewRelease> releases =
        [
            new("1.0.3", Enumerable.Range(1, 6).Select(index => Item($"new{index}")).ToList()),
            new("1.0.2", Enumerable.Range(1, 6).Select(index => Item($"old{index}")).ToList()),
        ];

        var selection = WhatsNewSchedule.Select(releases, V101, V103, WhatsNewPlatforms.Windows);

        Assert.Equal(WhatsNewSchedule.MaxHighlights, selection.Highlights.Count);
        Assert.Equal(["new1", "new2", "new3", "new4", "new5", "new6", "old1", "old2"],
            selection.Highlights.Select(item => item.TitleKey));
    }

    [Fact]
    public void SeenNeverMovesBackAndVersionsReadNaturally()
    {
        var settings = new UserSettings { LastSeenWhatsNewVersion = "1.0.3" };
        WhatsNewSchedule.MarkSeen(settings, V102);
        Assert.Equal("1.0.3", settings.LastSeenWhatsNewVersion);
        WhatsNewSchedule.MarkSeen(settings, new Version(1, 1, 0));
        Assert.Equal("1.1.0", settings.LastSeenWhatsNewVersion);

        Assert.Equal("1.0", WhatsNewSchedule.Display(WhatsNewSchedule.Baseline));
        Assert.Equal("1.0.2", WhatsNewSchedule.Display(V102));
        Assert.Equal(V102, WhatsNewSchedule.Parse(" 1.0.2.0 "));
        Assert.Null(WhatsNewSchedule.Parse("not a version"));
    }

    [Fact]
    public void TheCatalogIsNewestFirstAndEveryItemCanBeShown()
    {
        var releases = WhatsNewCatalog.Releases;
        Assert.NotEmpty(releases);
        for (var index = 1; index < releases.Count; index++)
            Assert.True(releases[index - 1].Version > releases[index].Version, "Releases must be listed newest first, once each.");

        foreach (var release in releases)
        {
            Assert.Equal(-1, release.Version.Revision);
            Assert.True(release.Items.Count(item => item.IsSpotlight) <= 1, $"{release.Version} has more than one spotlight.");
            foreach (var item in release.Items)
            {
                foreach (var icon in item.Points.Select(point => point.Icon).Prepend(item.Icon))
                    Assert.True(WhatsNewIconConverter.Find(icon) is not null, $"{icon} is not one of Strata's line icons (StrataIcons).");
                foreach (var key in new[] { item.TitleKey, item.BodyKey, item.ActionKey, item.EyebrowKey, item.NoteKey }
                             .Concat(item.Points.Select(point => point.TextKey))
                             .OfType<string>())
                    Assert.False(string.IsNullOrWhiteSpace(Loc.GetEnglish(key)), $"Missing text {key}.");
                Assert.True((item.ActionKey is not null) == (item.Destination != WhatsNewDestination.None),
                    $"{item.TitleKey}: an action needs both its label (ActionKey) and where it leads (Destination).");
                Assert.True(!item.IsSpotlight || item.EyebrowKey is not null, "A spotlight needs its eyebrow.");
            }
        }
    }

    [Fact]
    public void TheCatalogDescribesTheVersionBeingBuilt()
    {
        var project = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Lumi", "Lumi.csproj"));
        var version = WhatsNewSchedule.Parse(Regex.Match(project, @"<Version>([^<]+)</Version>").Groups[1].Value);

        Assert.NotNull(version);
        Assert.True(WhatsNewSchedule.Newest(WhatsNewCatalog.Releases) >= version,
            $"Lumi {version} has no entry in WhatsNewCatalog. Add one before releasing it (an empty one when it has " +
            "nothing to show); see .github/skills/Update Whats New.");
        Assert.Equal(version, WhatsNewSchedule.CurrentVersion);
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Lumi.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Lumi repository root was not found.");
    }
}

[Collection("Headless UI")]
public sealed class WhatsNewShellTests
{
    private static readonly Version V102 = new(1, 0, 2);

    private static AppData UpdatedProfile(string lastSeen = "") => new()
    {
        Settings = new UserSettings
        {
            IsOnboarded = true,
            UserName = "Ada",
            LastSeenWhatsNewVersion = lastSeen,
            AutoSaveChats = false,
            EnableMemoryAutoSave = false,
            ShowAnimations = false,
        },
    };

    private static MainViewModel Shell(AppData data)
        => new(new DataStore(data), TestCopilot.Shared, new UpdateService(), startBackgroundJobs: false);

    [Fact]
    public async Task AnUpdateShowsWhatsNewOnceAndClosingItRemembersTheVersion()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var data = UpdatedProfile();
            using (var shell = Shell(data))
            {
                shell.PrepareWhatsNew(isInstalledBuild: true, V102);
                Assert.False(shell.WhatsNewVM.IsOpen);
                shell.ShowPendingWhatsNew();

                var whatsNew = shell.WhatsNewVM;
                Assert.True(whatsNew.IsOpen);
                Assert.Equal("Version 1.0.2", whatsNew.VersionText);
                Assert.Equal(Loc.Get("WhatsNew_Since", "1.0"), whatsNew.Subtitle);
                var phone = Assert.IsType<WhatsNewItemViewModel>(whatsNew.Spotlight);
                Assert.True(phone.IsPhone);
                Assert.False(phone.IsConnected);
                Assert.Equal(Loc.WhatsNew_Phone_Connect, phone.ActionLabel);
                Assert.Equal(Loc.WhatsNew_Phone_Private, phone.Note);
                Assert.Equal(3, phone.Points.Count);
                Assert.Equal(7, whatsNew.Highlights.Count);
                Assert.Equal("", data.Settings.LastSeenWhatsNewVersion);

                whatsNew.DismissCommand.Execute(null);
                Assert.False(whatsNew.IsOpen);
                Assert.Equal("1.0.2", data.Settings.LastSeenWhatsNewVersion);
            }

            using var restarted = Shell(data);
            restarted.PrepareWhatsNew(isInstalledBuild: true, V102);
            restarted.ShowPendingWhatsNew();
            Assert.False(restarted.WhatsNewVM.IsOpen);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DevelopmentBuildsNeitherShowNorRecordWhatsNew_UnlessAskedTo()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var data = UpdatedProfile();
            using var shell = Shell(data);

            shell.PrepareWhatsNew(isInstalledBuild: false, V102);
            shell.ShowPendingWhatsNew();
            Assert.False(shell.WhatsNewVM.IsOpen);

            // --whats-new previews it however the profile stands, and closing the preview records nothing.
            shell.PrepareWhatsNew(isInstalledBuild: false, V102, force: true, forceSince: "1.0.1");
            shell.ShowPendingWhatsNew();
            Assert.True(shell.WhatsNewVM.IsOpen);
            Assert.Equal(Loc.Get("WhatsNew_Since", "1.0.1"), shell.WhatsNewVM.Subtitle);
            Assert.NotNull(shell.WhatsNewVM.Spotlight);
            shell.WhatsNewVM.DismissCommand.Execute(null);
            Assert.Equal("", data.Settings.LastSeenWhatsNewVersion);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ThePhoneSpotlightOpensPhoneSettings_AndKnowsAPairedPhone()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var data = UpdatedProfile();
            data.Settings.RemoteAccessEnabled = true;
            data.Settings.RemotePairedDevices.Add(new RemotePairedDevice { DeviceId = "phone", DeviceName = "Pixel", Token = "token" });
            using var shell = Shell(data);
            shell.PrepareWhatsNew(isInstalledBuild: true, V102);
            shell.ShowPendingWhatsNew();

            var phone = Assert.IsType<WhatsNewItemViewModel>(shell.WhatsNewVM.Spotlight);
            Assert.True(phone.IsConnected);
            Assert.Equal(Loc.WhatsNew_Phone_Manage, phone.ActionLabel);
            Assert.False(phone.HasNote);

            phone.ActCommand.Execute(null);
            Assert.False(shell.WhatsNewVM.IsOpen);
            Assert.Equal(7, shell.SelectedNavIndex);
            Assert.Equal(SettingsViewModel.MobilePageIndex, shell.SettingsVM.SelectedPageIndex);
            Assert.Equal("1.0.2", data.Settings.LastSeenWhatsNewVersion);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task HighlightActionsOpenWhereTheyLive_AndAboutReopensTheLatest()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var data = UpdatedProfile(lastSeen: "1.0.1");
            using var shell = Shell(data);
            var whatsNew = shell.WhatsNewVM;

            shell.PrepareWhatsNew(isInstalledBuild: true, V102);
            shell.ShowPendingWhatsNew();
            Assert.Equal(Loc.Get("WhatsNew_Since", "1.0.1"), whatsNew.Subtitle);
            Assert.Equal(3, whatsNew.Highlights.Count);
            whatsNew.Highlights.Single(item => item.Destination == WhatsNewDestination.ImageSettings).ActCommand.Execute(null);
            Assert.False(whatsNew.IsOpen);
            Assert.Equal(7, shell.SelectedNavIndex);
            Assert.Equal(SettingsViewModel.AiModelsPageIndex, shell.SettingsVM.SelectedPageIndex);
            Assert.Equal("ImageGenerationGroup", shell.SettingsVM.PendingRevealSetting);
            Assert.Equal("1.0.2", data.Settings.LastSeenWhatsNewVersion);

            // About opens the latest highlights again, and closing them changes nothing.
            shell.SettingsVM.ShowWhatsNewCommand.Execute(null);
            Assert.True(whatsNew.IsOpen);
            Assert.Equal(Loc.WhatsNew_Recent, whatsNew.Subtitle);
            whatsNew.DismissCommand.Execute(null);
            Assert.Equal("1.0.2", data.Settings.LastSeenWhatsNewVersion);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheJobsAndResponseDetailHighlightsOpenTheirPages()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            MainViewModel OpenAfterUpdatingTo101()
            {
                var shell = Shell(UpdatedProfile(lastSeen: "1.0.0"));
                shell.PrepareWhatsNew(isInstalledBuild: true, new Version(1, 0, 1));
                shell.ShowPendingWhatsNew();
                Assert.True(shell.WhatsNewVM.IsOpen);
                return shell;
            }

            using (var shell = OpenAfterUpdatingTo101())
            {
                shell.WhatsNewVM.Highlights.Single(item => item.Destination == WhatsNewDestination.ResponseDetailSettings)
                    .ActCommand.Execute(null);
                Assert.Equal(7, shell.SelectedNavIndex);
                Assert.Equal(SettingsViewModel.AiModelsPageIndex, shell.SettingsVM.SelectedPageIndex);
                Assert.Equal("ResponseDetailSetting", shell.SettingsVM.PendingRevealSetting);
            }

            using (var shell = OpenAfterUpdatingTo101())
            {
                shell.WhatsNewVM.Highlights.Single(item => item.Destination == WhatsNewDestination.Jobs).ActCommand.Execute(null);
                Assert.Equal(1, shell.SelectedNavIndex);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EveryDestinationLeadsSomewhere()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            using var shell = Shell(UpdatedProfile());
            foreach (var destination in Enum.GetValues<WhatsNewDestination>().Where(value => value != WhatsNewDestination.None))
                Assert.True(shell.OpenWhatsNewDestination(destination),
                    $"WhatsNewDestination.{destination} leads nowhere: add its case to MainViewModel.OpenWhatsNewDestination.");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheOverlayShowsOverLumiAndClosesWithGotItEscapeOrAClickOutside()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = UpdatedProfile();
            using var shell = Shell(data);
            var window = new MainWindow { DataContext = shell, Width = 1280, Height = 820 };
            try
            {
                window.Show();
                await PumpAsync();
                var host = window.FindControl<ContentControl>("WhatsNewHost")!;
                Assert.False(host.IsVisible);
                Assert.Null(host.Content);

                async Task<WhatsNewView> OpenAsync()
                {
                    shell.SettingsVM.ShowWhatsNewCommand.Execute(null);
                    await PumpAsync();
                    Assert.True(host.IsVisible);
                    return Assert.IsType<WhatsNewView>(host.Content);
                }

                var view = await OpenAsync();
                var tiles = view.GetVisualDescendants().OfType<Border>().Count(border => border.Classes.Contains("wn-tile"));
                Assert.Equal(shell.WhatsNewVM.Highlights.Count, tiles);
                var gotIt = view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "WhatsNewDismissButton");

                // The card takes focus, so a Space or Enter typed as it arrives presses nothing; Tab reaches its buttons.
                var card = view.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "WhatsNewCard");
                Assert.True(card.IsFocused);
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                await PumpAsync();
                Assert.True(shell.WhatsNewVM.IsOpen);
                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                var tabbed = Assert.IsType<Button>(window.FocusManager!.GetFocusedElement(), exactMatch: false);
                Assert.True(card.IsVisualAncestorOf(tabbed));

                gotIt.Command!.Execute(null);
                await PumpAsync();
                Assert.False(host.IsVisible);
                Assert.Null(host.Content);

                await OpenAsync();
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                await PumpAsync();
                Assert.False(shell.WhatsNewVM.IsOpen);
                Assert.Null(host.Content);

                await OpenAsync();
                var outside = new Point(30, 410);
                window.MouseDown(outside, MouseButton.Left);
                window.MouseUp(outside, MouseButton.Left);
                await PumpAsync();
                Assert.False(shell.WhatsNewVM.IsOpen);
                Assert.Null(host.Content);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
