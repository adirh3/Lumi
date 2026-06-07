using System.Collections.Generic;
using GitHub.Copilot;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class SessionConfigBuilderTests
{
    [Fact]
    public void Build_UsesLumiCopilotConfigDir()
    {
        const string workDir = @"C:\Repo";

        var config = SessionConfigBuilder.Build(
            systemPrompt: "prompt",
            model: "gpt-5.4",
            workingDirectory: workDir,
            skillDirectories: [],
            customAgents: [],
            tools: [],
            mcpServers: new Dictionary<string, McpServerConfig>(),
            reasoningEffort: null,
            userInputHandler: null,
            onPermission: null,
            hooks: null);

        Assert.Equal(workDir, config.WorkingDirectory);
        Assert.Equal(DataStore.CopilotConfigDir, config.ConfigDirectory);
        Assert.NotEqual(workDir, config.ConfigDirectory);
        Assert.False(config.EnableConfigDiscovery);
        Assert.NotNull(config.McpServers);
        Assert.Empty(config.McpServers!);
    }

    [Fact]
    public void BuildForResume_UsesLumiCopilotConfigDir()
    {
        const string workDir = @"C:\Repo";

        var config = SessionConfigBuilder.BuildForResume(
            systemPrompt: "prompt",
            model: "gpt-5.4",
            workingDirectory: workDir,
            skillDirectories: [],
            customAgents: [],
            tools: [],
            mcpServers: new Dictionary<string, McpServerConfig>(),
            reasoningEffort: null,
            userInputHandler: null,
            onPermission: null,
            hooks: null);

        Assert.Equal(workDir, config.WorkingDirectory);
        Assert.Equal(DataStore.CopilotConfigDir, config.ConfigDirectory);
        Assert.NotEqual(workDir, config.ConfigDirectory);
        Assert.False(config.EnableConfigDiscovery);
        Assert.NotNull(config.McpServers);
        Assert.Empty(config.McpServers!);
    }

    [Fact]
    public void BuildLightweight_UsesLumiCopilotConfigDirByDefault()
    {
        var config = SessionConfigBuilder.BuildLightweight(new LightweightSessionOptions
        {
            SystemPrompt = "prompt"
        });

        Assert.Equal(DataStore.CopilotConfigDir, config.ConfigDirectory);
        Assert.False(config.EnableConfigDiscovery);
    }

    [Fact]
    public void BuildLightweight_HonorsExplicitConfigDir()
    {
        const string configDir = @"C:\CustomCopilotConfig";

        var config = SessionConfigBuilder.BuildLightweight(new LightweightSessionOptions
        {
            SystemPrompt = "prompt",
            ConfigDir = configDir
        });

        Assert.Equal(configDir, config.ConfigDirectory);
        Assert.False(config.EnableConfigDiscovery);
    }
}
