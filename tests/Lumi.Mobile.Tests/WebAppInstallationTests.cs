using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Xunit;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.Tests;

[Collection("Headless mobile UI")]
public sealed class WebAppInstallationTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("he")]
    public async Task InstallationHelpFollowsTheUiLanguageWithoutChangingTheBrowserAction(string language)
    {
        using var session = HeadlessMobileSession.Start();
        var previousLanguage = Strings.Language;
        try
        {
            await WithHostAsync(new InstallHost(WebAppInstallMode.Apple), async () =>
            {
                var store = session.NewStore();
                var settings = store.Load();
                settings.Language = language;
                store.Save(settings);
                await using var shell = NewShell(store);
                Assert.Equal(language, Strings.Language);
                Assert.Equal(Strings.Get("WebAppInstallHow"), shell.WebAppInstallActionText);
                Assert.Equal(Strings.Get("WebAppInstallShareStep"), shell.WebAppInstallFirstStep);
                Assert.Equal(Strings.Get("WebAppInstallAppleStep"), shell.WebAppInstallSecondStep);
                Assert.Equal(WebAppInstallMode.Apple, MobilePlatformServices.HostEnvironment.WebAppInstallMode);
            });
        }
        finally
        {
            Strings.Load(previousLanguage);
        }
    }

    [Theory]
    [InlineData(WebAppInstallMode.NativePrompt)]
    [InlineData(WebAppInstallMode.Apple)]
    [InlineData(WebAppInstallMode.Android)]
    [InlineData(WebAppInstallMode.Browser)]
    public async Task OffersOnceAfterConnectingAndRemembersDismissal(WebAppInstallMode mode)
    {
        using var session = HeadlessMobileSession.Start();
        await WithHostAsync(new InstallHost(mode), async () =>
        {
            var store = session.NewStore();
            await using (var shell = NewShell(store))
            {
                shell.IsPaired = shell.IsConnected = true;
                shell.OfferPhoneSetupIfUseful();
                Assert.True(shell.IsWebAppInstallPromptOpen);
                Assert.False(shell.IsNotificationsPromptOpen);
                Assert.False(shell.CanDragDrawer);
                Assert.True(shell.CanGoBack);
                shell.GoBackCommand.Execute(null);
                Assert.False(shell.IsWebAppInstallPromptOpen);
                Assert.True(store.Load().WebAppInstallPromptAnswered);
                shell.OfferPhoneSetupIfUseful();
                Assert.False(shell.IsWebAppInstallPromptOpen);
            }
            await using var reopened = NewShell(store);
            reopened.IsPaired = reopened.IsConnected = true;
            reopened.OfferPhoneSetupIfUseful();
            Assert.False(reopened.IsWebAppInstallPromptOpen);
            // Declining the automatic suggestion never removes the Settings action.
            reopened.OpenWebAppInstallCommand.Execute(null);
            Assert.True(reopened.IsWebAppInstallPromptOpen);
        });
    }

    [Fact]
    public async Task InstalledOrNativeHostsNeverOfferInstallationOrKeepAnOpenSheet()
    {
        using var session = HeadlessMobileSession.Start();
        var host = new InstallHost(WebAppInstallMode.Apple);
        await WithHostAsync(host, async () =>
        {
            await using var shell = NewShell(session.NewStore());
            shell.IsPaired = shell.IsConnected = true;
            shell.OfferPhoneSetupIfUseful();
            Assert.True(shell.IsWebAppInstallPromptOpen);
            host.WebAppInstallMode = WebAppInstallMode.Unavailable;
            shell.NotifyWebAppInstallationChanged();
            Assert.False(shell.CanAddWebApp);
            Assert.False(shell.OpenWebAppInstallCommand.CanExecute(null));
            Assert.False(shell.IsWebAppInstallPromptOpen);
            shell.OfferPhoneSetupIfUseful();
            Assert.False(shell.IsWebAppInstallPromptOpen);
        });
        var nativeHost = MobilePlatformServices.HostEnvironment;
        Assert.Equal(WebAppInstallMode.Unavailable, nativeHost.WebAppInstallMode);
    }

    [Fact]
    public async Task DoesNotInterruptPairingOtherSheetsOrTheBackground()
    {
        using var session = HeadlessMobileSession.Start();
        await WithHostAsync(new InstallHost(WebAppInstallMode.Android), async () =>
        {
            await using var shell = NewShell(session.NewStore());
            shell.OfferPhoneSetupIfUseful();
            Assert.False(shell.IsWebAppInstallPromptOpen);
            shell.IsPaired = shell.IsConnected = true;
            shell.IsProjectPickerOpen = true;
            shell.OfferPhoneSetupIfUseful();
            Assert.False(shell.IsWebAppInstallPromptOpen);
            shell.IsProjectPickerOpen = false;
            await shell.NotifyApplicationDeactivatedAsync();
            shell.OfferPhoneSetupIfUseful();
            Assert.False(shell.IsWebAppInstallPromptOpen);
        });
    }

    [Theory]
    [InlineData(WebAppInstallMode.Apple, "WebAppInstallShareStep", "WebAppInstallAppleStep")]
    [InlineData(WebAppInstallMode.Android, "WebAppInstallMenuStep", "WebAppInstallBrowserStep")]
    public async Task ManualHelpUsesThePlatformStepsAndNeverPretendsToOpenAnInstallDialog(
        WebAppInstallMode mode, string first, string second)
    {
        using var session = HeadlessMobileSession.Start();
        var host = new InstallHost(mode);
        await WithHostAsync(host, async () =>
        {
            await using var shell = NewShell(session.NewStore());
            shell.OpenWebAppInstallCommand.Execute(null);
            Assert.Equal(Strings.Get("WebAppInstallHow"), shell.WebAppInstallActionText);
            await shell.InstallWebAppCommand.ExecuteAsync(null);
            Assert.Equal(0, host.Prompts);
            Assert.True(shell.IsWebAppInstallInstructionsVisible);
            Assert.Equal(Strings.Get(first), shell.WebAppInstallFirstStep);
            Assert.Equal(Strings.Get(second), shell.WebAppInstallSecondStep);
            Assert.Equal(Strings.Get("Done"), shell.WebAppInstallActionText);
            await shell.InstallWebAppCommand.ExecuteAsync(null);
            Assert.False(shell.IsWebAppInstallPromptOpen);
        });
    }

    [Theory]
    [InlineData(WebAppInstallResult.Accepted)]
    [InlineData(WebAppInstallResult.Dismissed)]
    [InlineData(WebAppInstallResult.Unavailable)]
    public async Task NativePromptStartsOnTheTapAndHandlesTheBrowserChoice(WebAppInstallResult result)
    {
        using var session = HeadlessMobileSession.Start();
        var host = new InstallHost(WebAppInstallMode.NativePrompt) { Result = result };
        await WithHostAsync(host, async () =>
        {
            await using var shell = NewShell(session.NewStore());
            shell.OpenWebAppInstallCommand.Execute(null);
            Assert.Equal(Strings.Get("WebAppInstallAction"), shell.WebAppInstallActionText);
            await shell.InstallWebAppCommand.ExecuteAsync(null);
            Assert.Equal(1, host.Prompts);
            Assert.Equal(result == WebAppInstallResult.Unavailable, shell.IsWebAppInstallPromptOpen);
            Assert.Equal(result == WebAppInstallResult.Unavailable, shell.IsWebAppInstallInstructionsVisible);
            Assert.False(shell.IsInstallingWebApp);
        });
    }

    [Fact]
    public async Task FailureShowsAnExplicitErrorAndManualInstructions()
    {
        using var session = HeadlessMobileSession.Start();
        var host = new InstallHost(WebAppInstallMode.NativePrompt) { Fail = true };
        await WithHostAsync(host, async () =>
        {
            await using var shell = NewShell(session.NewStore());
            shell.OpenWebAppInstallCommand.Execute(null);
            await shell.InstallWebAppCommand.ExecuteAsync(null);
            Assert.True(shell.IsWebAppInstallPromptOpen);
            Assert.True(shell.IsWebAppInstallInstructionsVisible);
            Assert.Equal(Strings.Get("WebAppInstallFailed"), shell.WebAppInstallError);
            Assert.False(shell.IsInstallingWebApp);
        });
    }

    private static MobileShellViewModel NewShell(IMobileSettingsStore store) => new(
        new LumiRemoteClient("install-test", "Test browser"), new LumiDiscoveryClient(), store, action => action());

    private static async Task WithHostAsync(IMobileHostEnvironment host, Func<Task> body)
    {
        var previous = MobilePlatformServices.HostEnvironment;
        MobilePlatformServices.HostEnvironment = host;
        try { await body(); }
        finally { MobilePlatformServices.HostEnvironment = previous; }
    }

    private sealed class InstallHost(WebAppInstallMode mode) : IMobileHostEnvironment
    {
        public bool HasFixedEndpoint => false;
        public string? FixedBaseUrl => null;
        public string FixedEndpointName => "";
        public WebAppInstallMode WebAppInstallMode { get; set; } = mode;
        public WebAppInstallResult Result { get; init; }
        public bool Fail { get; init; }
        public int Prompts { get; private set; }

        public Task<WebAppInstallResult> InstallWebAppAsync()
        {
            Prompts++;
            return Fail
                ? Task.FromException<WebAppInstallResult>(new InvalidOperationException("Browser refused"))
                : Task.FromResult(Result);
        }
    }
}
