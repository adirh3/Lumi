using System.Collections.Generic;
using GitHub.Copilot;
using Lumi.Models;
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
        Assert.Contains("builtin:web_fetch", config.ExcludedTools!);
        Assert.Contains("builtin:browser", config.ExcludedTools!);
        Assert.Contains("builtin:ask_user", config.ExcludedTools!);
        Assert.DoesNotContain("builtin:web_search", config.ExcludedTools!);
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
        Assert.Contains("builtin:web_fetch", config.ExcludedTools!);
        Assert.Contains("builtin:browser", config.ExcludedTools!);
        Assert.Contains("builtin:ask_user", config.ExcludedTools!);
        Assert.DoesNotContain("builtin:web_search", config.ExcludedTools!);
    }

    [Fact]
    public void Build_UsesPersistentMcpOAuthTokenStorage()
    {
        var config = SessionConfigBuilder.Build(
            systemPrompt: "prompt",
            model: "gpt-5.4",
            workingDirectory: @"C:\Repo",
            skillDirectories: [],
            customAgents: [],
            tools: [],
            mcpServers: new Dictionary<string, McpServerConfig>(),
            reasoningEffort: null,
            userInputHandler: null,
            onPermission: null,
            hooks: null);

        // The SDK default is InMemory ("discarded when the session ends"), which is meant for
        // multitenant hosts. Lumi is a single-user desktop client, so MCP OAuth tokens must be
        // stored in the OS keychain and reused across sessions — otherwise OAuth MCP servers
        // re-prompt / drop every time a session is created or resumed.
        Assert.Equal(McpOAuthTokenStorageMode.Persistent, config.McpOAuthTokenStorage);
    }

    [Fact]
    public void BuildForResume_UsesPersistentMcpOAuthTokenStorage()
    {
        var config = SessionConfigBuilder.BuildForResume(
            systemPrompt: "prompt",
            model: "gpt-5.4",
            workingDirectory: @"C:\Repo",
            skillDirectories: [],
            customAgents: [],
            tools: [],
            mcpServers: new Dictionary<string, McpServerConfig>(),
            reasoningEffort: null,
            userInputHandler: null,
            onPermission: null,
            hooks: null);

        Assert.Equal(McpOAuthTokenStorageMode.Persistent, config.McpOAuthTokenStorage);
    }

    [Fact]
    public void Build_RequestsReasoningSummary_SoReasoningStaysVisible()
    {
        var config = SessionConfigBuilder.Build(
            systemPrompt: "prompt",
            model: "gpt-5.5",
            workingDirectory: @"C:\Repo",
            skillDirectories: [],
            customAgents: [],
            tools: [],
            mcpServers: new Dictionary<string, McpServerConfig>(),
            reasoningEffort: "high",
            userInputHandler: null,
            onPermission: null,
            hooks: null);

        Assert.Equal(ReasoningSummary.Detailed, config.ReasoningSummary);
    }

    [Fact]
    public void Build_GptToneOverrideIncludesConcreteVisualizationTriggers()
    {
        var config = SessionConfigBuilder.Build(
            systemPrompt: "prompt",
            model: "gpt-5.6-sol",
            workingDirectory: @"C:\Repo",
            skillDirectories: [],
            customAgents: [],
            tools: [],
            mcpServers: new Dictionary<string, McpServerConfig>(),
            reasoningEffort: "high",
            userInputHandler: null,
            onPermission: null,
            hooks: null);

        Assert.NotNull(config.SystemMessage);
        Assert.Equal(SystemMessageMode.Customize, config.SystemMessage!.Mode);
        Assert.NotNull(config.SystemMessage.Sections);
        var tone = config.SystemMessage.Sections![SystemMessageSection.Tone];
        Assert.Equal(SectionOverrideAction.Replace, tone.Action);
        Assert.Contains("exactly two meaningful alternatives", tone.Content);
        Assert.Contains("compact profile/lookup/digest/deal", tone.Content);
        Assert.Contains("final URL-delivered artifact", tone.Content);
        Assert.Contains("one clear Markdown action link in the card's always-visible summary", tone.Content);
        Assert.Contains("bare URL or prose-only link", tone.Content);
        Assert.Contains("central numeric values or trends", tone.Content);
        Assert.Contains("functional UI controls", tone.Content);
        Assert.Contains("use that block instead of substituting a plain list or table", tone.Content);
        Assert.Contains("Do not wait for an explicit visualization request", tone.Content);
    }

    [Fact]
    public void Build_AppliesContextTier()
    {
        var config = SessionConfigBuilder.Build(
            systemPrompt: "prompt",
            model: "gpt-5.5",
            workingDirectory: @"C:\Repo",
            skillDirectories: [],
            customAgents: [],
            tools: [],
            mcpServers: new Dictionary<string, McpServerConfig>(),
            reasoningEffort: "high",
            userInputHandler: null,
            onPermission: null,
            hooks: null,
            contextTier: ModelContextWindowTiers.LongContext);

        Assert.Equal(ModelContextWindowTiers.LongContext, config.ContextTier?.Value);
    }

    [Fact]
    public void BuildForResume_RequestsReasoningSummary_SoReasoningStaysVisible()
    {
        var config = SessionConfigBuilder.BuildForResume(
            systemPrompt: "prompt",
            model: "gpt-5.5",
            workingDirectory: @"C:\Repo",
            skillDirectories: [],
            customAgents: [],
            tools: [],
            mcpServers: new Dictionary<string, McpServerConfig>(),
            reasoningEffort: "high",
            userInputHandler: null,
            onPermission: null,
            hooks: null);

        Assert.Equal(ReasoningSummary.Detailed, config.ReasoningSummary);
    }

    [Fact]
    public void BuildForResume_AppliesContextTier()
    {
        var config = SessionConfigBuilder.BuildForResume(
            systemPrompt: "prompt",
            model: "gpt-5.5",
            workingDirectory: @"C:\Repo",
            skillDirectories: [],
            customAgents: [],
            tools: [],
            mcpServers: new Dictionary<string, McpServerConfig>(),
            reasoningEffort: "high",
            userInputHandler: null,
            onPermission: null,
            hooks: null,
            contextTier: ModelContextWindowTiers.Default);

        Assert.Equal(ModelContextWindowTiers.Default, config.ContextTier?.Value);
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
