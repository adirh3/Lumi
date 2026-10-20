using System.Reflection;
using GitHub.Copilot;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class CopilotProcessEnvironmentTests
{
    [Fact]
    public void ApplyAgentProcessEnvironment_DisablesPersistentDotnetBuildServers()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSBUILDDISABLENODEREUSE"] = "0",
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "1",
            ["UseSharedCompilation"] = "true"
        };

        typeof(CopilotService)
            .GetMethod("ApplyAgentProcessEnvironment", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [environment]);

        Assert.Equal("1", environment["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal("0", environment["DOTNET_CLI_USE_MSBUILD_SERVER"]);
        Assert.Equal("false", environment["UseSharedCompilation"]);
    }

    [Fact]
    public void ConfigureAuthentication_AlwaysPassesAgentProcessEnvironmentToCli()
    {
        var options = new CopilotClientOptions();

        typeof(CopilotService)
            .GetMethod("ConfigureAuthentication", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [options]);

        Assert.NotNull(options.Environment);
        Assert.Equal("1", options.Environment["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal("0", options.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"]);
        Assert.Equal("false", options.Environment["UseSharedCompilation"]);
        Assert.False(string.IsNullOrWhiteSpace(options.Environment[CopilotService.SkillListingBudgetVariable]));
        Assert.Contains(CopilotService.AutoReviewFeatureFlag,
            options.Environment[CopilotService.FeatureFlagsVariable].Split(','));
    }

    [Fact]
    public void AutoReviewFeature_IsTurnedOn_KeepingTheUsersOwnFlags()
    {
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CopilotService.ApplyAutoReviewFeature(defaults);
        Assert.Equal("AUTO_APPROVAL", defaults[CopilotService.FeatureFlagsVariable]);

        var chosen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CopilotService.FeatureFlagsVariable] = "SANDBOX, WORKTREE,"
        };
        CopilotService.ApplyAutoReviewFeature(chosen);
        Assert.Equal("SANDBOX, WORKTREE,AUTO_APPROVAL", chosen[CopilotService.FeatureFlagsVariable]);

        var already = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CopilotService.FeatureFlagsVariable] = "sandbox, auto_approval"
        };
        CopilotService.ApplyAutoReviewFeature(already);
        Assert.Equal("sandbox, auto_approval", already[CopilotService.FeatureFlagsVariable]);
    }

    [Fact]
    public void SkillListingBudget_IsRaisedUnlessTheUserChoseOne()
    {
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CopilotService.ApplySkillListingBudget(defaults);
        Assert.Equal("40000", defaults[CopilotService.SkillListingBudgetVariable]);

        var chosen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["skill_char_budget"] = "90000" };
        CopilotService.ApplySkillListingBudget(chosen);
        Assert.Equal("90000", Assert.Single(chosen).Value);
    }

#pragma warning disable GHCP001 // Match the SDK account identity used by the logout RPC.
    [Fact]
    public void StoredCredentialSelectionPreservesReleasedWindowsPriorityAndToolEnvironment()
    {
        var options = new CopilotClientOptions();

        var selected = CopilotService.ConfigureAuthenticationSelection(
            options, null, ("stored-token", "https://github.com", "stored-user"));

        Assert.Equal("stored-user", selected?.Login);
        Assert.Equal("stored-token", options.GitHubToken);
        Assert.False(options.UseLoggedInUser);
        Assert.Equal("stored-token", options.Environment!["GITHUB_PERSONAL_ACCESS_TOKEN"]);
        Assert.Equal("stored-token", options.Environment["GITHUB_COPILOT_GITHUB_TOKEN"]);
    }

    [Fact]
    public void ExplicitTokenIsNeverTreatedAsStoredUser()
    {
        var options = new CopilotClientOptions();

        var selected = CopilotService.ConfigureAuthenticationSelection(
            options, "external-token", ("stored-token", "https://github.com", "stored-user"));

        Assert.Null(selected);
        Assert.Equal("external-token", options.GitHubToken);
        Assert.False(options.UseLoggedInUser);
        Assert.Equal("external-token", options.Environment!["GITHUB_COPILOT_GITHUB_TOKEN"]);
    }

    [Fact]
    public void NoTokenStillUsesTheSdkStoredUserOrGitHubCliFallback()
    {
        var options = new CopilotClientOptions();

        var selected = CopilotService.ConfigureAuthenticationSelection(options, null, null);

        Assert.Null(selected);
        Assert.Null(options.GitHubToken);
        Assert.True(options.UseLoggedInUser);
    }
#pragma warning restore GHCP001
}
