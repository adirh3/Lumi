using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class StartupExperienceTests
{
    [Fact]
    public async Task LoadingWindow_ShowsLocalizedStatusAndIndeterminateProgress()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var window = new StartupWindow();
            try
            {
                window.Show();
                await PumpAsync();
                window.UpdateLayout();

                var status = window.FindControl<TextBlock>("StartupStatus")!;
                var progress = window.FindControl<ProgressBar>("StartupProgress")!;
                Assert.Equal(Loc.Startup_Loading, status.Text);
                Assert.True(status.IsEffectivelyVisible);
                Assert.True(progress.IsIndeterminate);
                Assert.True(progress.IsEffectivelyVisible);
                Assert.True(progress.Bounds.Width > 0);
                Assert.True(window.ShowInTaskbar);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task OnboardedWindow_DoesNotConstructHiddenOnboardingScreens()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            using var viewModel = CreateViewModel(isOnboarded: true);
            var window = new MainWindow { DataContext = viewModel };
            try
            {
                window.Show();
                await PumpAsync();

                var host = window.FindControl<ContentControl>("OnboardingHost")!;
                Assert.Null(host.Content);
                Assert.Empty(window.GetVisualDescendants().OfType<OnboardingView>());
                Assert.False(window.FindControl<Panel>("OnboardingPanel")!.IsVisible);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task FirstRunWindow_CreatesAndReusesOnboardingWithItsBindings()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            using var viewModel = CreateViewModel(isOnboarded: false);
            var window = new MainWindow { DataContext = viewModel };
            try
            {
                window.Show();
                await PumpAsync();

                var host = window.FindControl<ContentControl>("OnboardingHost")!;
                var onboarding = Assert.IsType<OnboardingView>(host.Content);
                Assert.Same(viewModel.OnboardingVM, onboarding.DataContext);
                Assert.True(window.FindControl<Panel>("OnboardingPanel")!.IsVisible);

                viewModel.OnboardingVM.UserName = "Startup test";
                await PumpAsync();
                Assert.Equal("Startup test", onboarding.FindControl<TextBox>("OnboardingNameBox")!.Text);

                viewModel.IsOnboarded = true;
                await PumpAsync();
                Assert.False(window.FindControl<Panel>("OnboardingPanel")!.IsVisible);

                viewModel.IsOnboarded = false;
                await PumpAsync();
                Assert.Same(onboarding, host.Content);
                Assert.True(window.FindControl<Panel>("OnboardingPanel")!.IsVisible);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LocalModelChoices_AreAvailableBeforeDeferredConnection()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            var endpoint = new ByokEndpoint
            {
                Name = "Local provider",
                ProviderType = "openai",
                BaseUrl = "http://localhost:11434/v1",
                ApiKeyMode = ByokApiKeyMode.None,
            };
            var model = new ByokModel
            {
                EndpointId = endpoint.Id,
                ModelId = "startup-model",
                DisplayName = "Startup model",
            };
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false,
                    ByokEndpoints = [endpoint],
                    ByokModels = [model],
                }
            };
            using var viewModel = new MainViewModel(
                new DataStore(data),
                TestCopilot.Shared,
                new UpdateService(),
                startBackgroundJobs: false,
                initializeCopilotOnStartup: true);

            var token = ByokConfigHelper.BuildModelToken(model);
            Assert.Contains(token, viewModel.ChatVM.AvailableModels);
            Assert.Contains(token, viewModel.SettingsVM.AvailableModels);
        }, CancellationToken.None);
    }

    private static MainViewModel CreateViewModel(bool isOnboarded)
        => new(
            new DataStore(new AppData
            {
                Settings = new UserSettings
                {
                    IsOnboarded = isOnboarded,
                    MinimizeToTray = false,
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false,
                    ShowAnimations = false,
                }
            }),
            TestCopilot.Shared,
            new UpdateService(),
            startBackgroundJobs: false);

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
