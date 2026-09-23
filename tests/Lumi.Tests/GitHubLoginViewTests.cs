using Avalonia.Controls;
using Lumi.Localization;
using Lumi.ViewModels;
using Lumi.Views.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class GitHubLoginViewTests
{
    [Theory]
    [InlineData("")]
    [InlineData("octocat")]
    public async Task AuthenticatedPanelDisplaysConnectedState(string user)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var login = new GitHubLoginViewModel(TestCopilot.Shared)
            {
                GitHubLogin = user,
                IsAuthenticated = true
            };
            var view = new GitHubLoginView { DataContext = login };
            var window = new Window { Width = 420, Height = 180, Content = view };
            window.Show();
            try
            {
                Assert.Equal(
                    string.IsNullOrEmpty(user) ? Loc.Status_Connected
                        : string.Format(Loc.Onboarding_SignInSuccess, user),
                    view.FindControl<TextBlock>("SuccessText")!.Text);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }
}
