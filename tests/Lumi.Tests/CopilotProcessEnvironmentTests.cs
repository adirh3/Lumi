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
}
