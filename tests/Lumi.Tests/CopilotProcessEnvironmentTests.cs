using System.Reflection;
using Microsoft.Copilot;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class CopilotProcessEnvironmentTests
{
    [Theory]
    [InlineData(null, "copilot")]
    [InlineData("copilot-fixture", "copilot-fixture")]
    public void ClientOptions_DisableTelemetryAndNativeIdleExpiry(string? cliPath, string expectedPath)
    {
        var options = CopilotService.BuildClientOptions(cliPath);
        var connection = Assert.IsType<SpawnedStdioRuntimeConnection>(options.Runtime);
        CopilotService.ConfigureAuthenticationSelection(options, null, null);

        Assert.Equal(expectedPath, connection.Path);
        Assert.DoesNotContain("--disable-client-telemetry", connection.Args ?? []);
        Assert.Equal(0, connection.SessionIdleTimeoutSeconds);
        Assert.Equal(CopilotLogLevel.Error, options.Observability.LogLevel);
#pragma warning disable GHCP001 // Preserve the SDK extension launcher without opting into telemetry forwarding.
        Assert.NotNull(options.Extensions.LaunchProvider);
        Assert.Null(options.Observability.Telemetry);
        Assert.Null(options.GitHub.OnTelemetry);
#pragma warning restore GHCP001
        Assert.Equal("true", connection.Environment!["COPILOT_TELEMETRY_DISABLED"]);
    }

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
        var options = CopilotService.BuildClientOptions(null);

        typeof(CopilotService)
            .GetMethod("ConfigureAuthentication", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [options]);

        var environment = Assert.IsType<SpawnedStdioRuntimeConnection>(options.Runtime).Environment!;
        Assert.Equal("1", environment["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal("0", environment["DOTNET_CLI_USE_MSBUILD_SERVER"]);
        Assert.Equal("false", environment["UseSharedCompilation"]);
        Assert.False(string.IsNullOrWhiteSpace(environment[CopilotService.SkillListingBudgetVariable]));
        Assert.Contains(CopilotService.AutoReviewFeatureFlag,
            environment[CopilotService.FeatureFlagsVariable].Split(','));
        Assert.Contains("DYNAMIC_AUTO_TIERS",
            environment[CopilotService.FeatureFlagsVariable].Split(','));
        Assert.Contains("HYDRAFUSION_PLAN_V2",
            environment[CopilotService.FeatureFlagsVariable].Split(','));
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
    public void DynamicAutoTiers_PreservesExistingFlagsAndDoesNotDuplicateTheFeature()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CopilotService.FeatureFlagsVariable] = "AUTO_APPROVAL"
        };
        CopilotService.ApplyDynamicAutoTiersFeature(environment);
        CopilotService.ApplyDynamicAutoTiersFeature(environment);
        Assert.Equal("AUTO_APPROVAL,DYNAMIC_AUTO_TIERS,HYDRAFUSION_PLAN_V2", environment[CopilotService.FeatureFlagsVariable]);
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

    [Fact]
    public void IsolatedRuntime_KeepsMarketplaceClonesInItsProfile_AndSharesUnpackedCliVersions()
    {
        var isolated = Path.Combine(TestProfile.Root, "copilot-cache");
        var shared = Path.Combine(TestProfile.Root, "user-cache");

        var user = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["COPILOT_CACHE_HOME"] = shared };
        CopilotService.ApplyRuntimeCacheIsolation(user, isolated);
        Assert.Equal(isolated, user["COPILOT_CACHE_HOME"]);
        Assert.Equal(shared, user["COPILOT_PKG_CACHE_HOME"]);

        var packages = Path.Combine(TestProfile.Root, "packages");
        var chosen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["COPILOT_PKG_CACHE_HOME"] = packages };
        CopilotService.ApplyRuntimeCacheIsolation(chosen, isolated);
        Assert.Equal(isolated, chosen["COPILOT_CACHE_HOME"]);
        Assert.Equal(packages, chosen["COPILOT_PKG_CACHE_HOME"]);

        if (OperatingSystem.IsWindows())
        {
            var windows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["LOCALAPPDATA"] = @"C:\Users\octo\AppData\Local" };
            CopilotService.ApplyRuntimeCacheIsolation(windows, isolated);
            Assert.Equal(@"C:\Users\octo\AppData\Local\copilot", windows["COPILOT_PKG_CACHE_HOME"]);
        }

        var normal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["COPILOT_CACHE_HOME"] = shared };
        CopilotService.ApplyRuntimeCacheIsolation(normal, isolatedCache: null);
        Assert.Equal(shared, Assert.Single(normal).Value);
    }

#pragma warning disable GHCP001 // Match the SDK account identity used by the logout RPC.
    [Fact]
    public void StoredCredentialSelectionPreservesReleasedWindowsPriorityAndToolEnvironment()
    {
        var options = CopilotService.BuildClientOptions(null);

        var selected = CopilotService.ConfigureAuthenticationSelection(
            options, null, ("stored-token", "https://github.com", "stored-user"));

        Assert.Equal("stored-user", selected?.Login);
        Assert.Equal("stored-token", options.GitHub.Token);
        Assert.False(options.GitHub.UseLoggedInUser);
        var environment = Assert.IsType<SpawnedStdioRuntimeConnection>(options.Runtime).Environment!;
        Assert.Equal("stored-token", environment["GITHUB_PERSONAL_ACCESS_TOKEN"]);
        Assert.Equal("stored-token", environment["GITHUB_COPILOT_GITHUB_TOKEN"]);
    }

    [Fact]
    public void ExplicitTokenIsNeverTreatedAsStoredUser()
    {
        var options = CopilotService.BuildClientOptions(null);

        var selected = CopilotService.ConfigureAuthenticationSelection(
            options, "external-token", ("stored-token", "https://github.com", "stored-user"));

        Assert.Null(selected);
        Assert.Equal("external-token", options.GitHub.Token);
        Assert.False(options.GitHub.UseLoggedInUser);
        var environment = Assert.IsType<SpawnedStdioRuntimeConnection>(options.Runtime).Environment!;
        Assert.Equal("external-token", environment["GITHUB_COPILOT_GITHUB_TOKEN"]);
    }

    [Fact]
    public void NoTokenStillUsesTheSdkStoredUserOrGitHubCliFallback()
    {
        var options = CopilotService.BuildClientOptions(null);

        var selected = CopilotService.ConfigureAuthenticationSelection(options, null, null);

        Assert.Null(selected);
        Assert.Null(options.GitHub.Token);
        Assert.True(options.GitHub.UseLoggedInUser);
    }
#pragma warning restore GHCP001
}
