using Avalonia.Headless;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class AmbientPresenceSettingTests
{
    private static SettingsViewModel CreateViewModel(DataStore dataStore)
        => new(
            dataStore,
            TestCopilot.Shared,
            new BrowserService(),
            new UpdateService(),
            new FakeSecureKeyStore());

    [Fact]
    public void RuntimeChangePrecedesGenericSettingsNotifications()
    {
        using var viewModel = CreateViewModel(new DataStore(new AppData()));
        var notifications = new List<string>();

        viewModel.AmbientPresenceChanged += _ => notifications.Add("runtime");
        viewModel.SettingsChanged += () => notifications.Add("settings");
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SettingsViewModel.ShowAmbientPresence))
                notifications.Add("property");
        };

        viewModel.ShowAmbientPresence = false;

        Assert.Equal(["runtime", "settings", "property"], notifications);
    }

    [Fact]
    public void AnimationRuntimeChangePrecedesGenericSettingsNotifications()
    {
        using var viewModel = CreateViewModel(new DataStore(new AppData()));
        var notifications = new List<string>();

        viewModel.PresenceAnimationChanged += _ => notifications.Add("runtime");
        viewModel.SettingsChanged += () => notifications.Add("settings");
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SettingsViewModel.AnimatePresenceWhileWorking))
                notifications.Add("property");
        };

        viewModel.AnimatePresenceWhileWorking = true;

        Assert.Equal(["runtime", "settings", "property"], notifications);
    }

    [Fact]
    public async Task PresencePreferencesSynchronizeAcrossSettingsWindows()
    {
        using var session = HeadlessTestSession.Start();
        var animationSynchronized = false;
        var presenceSynchronized = false;

        await session.Dispatch(() =>
        {
            var dataStore = new DataStore(new AppData());
            using var first = CreateViewModel(dataStore);
            using var second = CreateViewModel(dataStore);

            first.AnimatePresenceWhileWorking = true;
            animationSynchronized = second.AnimatePresenceWhileWorking;

            second.ShowAmbientPresence = false;
            presenceSynchronized = !first.ShowAmbientPresence;
        }, CancellationToken.None);

        Assert.True(animationSynchronized);
        Assert.True(presenceSynchronized);
    }
}
