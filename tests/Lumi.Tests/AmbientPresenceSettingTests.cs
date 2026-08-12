using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

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
    public void PresencePreferencesSynchronizeAcrossSettingsWindows()
    {
        var dataStore = new DataStore(new AppData());
        using var first = CreateViewModel(dataStore);
        using var second = CreateViewModel(dataStore);

        first.AnimatePresenceWhileWorking = true;
        Assert.True(second.AnimatePresenceWhileWorking);

        second.ShowAmbientPresence = false;
        Assert.False(first.ShowAmbientPresence);
    }
}
