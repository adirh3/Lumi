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
