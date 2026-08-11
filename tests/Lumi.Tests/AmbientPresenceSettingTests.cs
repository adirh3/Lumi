using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class AmbientPresenceSettingTests
{
    [Fact]
    public void RuntimeChangePrecedesGenericSettingsNotifications()
    {
        var viewModel = new SettingsViewModel(
            new DataStore(new AppData()),
            TestCopilot.Shared,
            new BrowserService(),
            new UpdateService(),
            new FakeSecureKeyStore());
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
}
