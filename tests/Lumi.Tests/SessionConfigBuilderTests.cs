using System.Collections.Generic;
using System.Text.Json;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace Lumi.Tests;

public sealed class SessionConfigBuilderTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("resume")]
    [InlineData("lightweight")]
    public async Task LumiTools_PreloadWithoutChangingTheirContract(string sessionKind)
    {
        using var cts = new CancellationTokenSource();
        var arguments = new AIFunctionArguments { ["text"] = "receipt" };
        var metadata = new object();
        var original = AIFunctionFactory.Create(
            (string text, AIFunctionArguments actualArguments, CancellationToken cancellationToken, int repeat = 2) =>
            {
                Assert.Same(arguments, actualArguments);
                Assert.Equal(cts.Token, cancellationToken);
                return string.Concat(Enumerable.Repeat(text, repeat));
            },
            new AIFunctionFactoryOptions
            {
                Name = "lumi_test_tool",
                Description = "Return a diagnostic receipt.",
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["defer"] = CopilotToolDefer.Auto,
                    ["skip_permission"] = true,
                    ["test_metadata"] = metadata
                }
            });

        var config = BuildWithTools(sessionKind, [original]);
        var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(config.Tools!));

        Assert.Equal(original.Name, tool.Name);
        Assert.Equal(original.Description, tool.Description);
        Assert.Equal(original.JsonSchema.GetRawText(), tool.JsonSchema.GetRawText());
        Assert.Equal(original.ReturnJsonSchema?.GetRawText(), tool.ReturnJsonSchema?.GetRawText());
        Assert.Same(original.JsonSerializerOptions, tool.JsonSerializerOptions);
        Assert.Same(original.UnderlyingMethod, tool.UnderlyingMethod);
        Assert.Equal(CopilotToolDefer.Never,
            Assert.IsType<CopilotToolDefer>(tool.AdditionalProperties["defer"]));
        Assert.True(Assert.IsType<bool>(tool.AdditionalProperties["skip_permission"]));
        Assert.Same(metadata, tool.AdditionalProperties["test_metadata"]);
        Assert.Equal(CopilotToolDefer.Auto,
            Assert.IsType<CopilotToolDefer>(original.AdditionalProperties["defer"]));
        Assert.Null(config.ToolSearch);

        var result = await tool.InvokeAsync(arguments, cts.Token);
        Assert.Equal("receiptreceipt", Assert.IsType<JsonElement>(result).GetString());
    }

    [Fact]
    public async Task PreloadedLumiTools_PropagateErrorsAndCancellation()
    {
        var failure = new InvalidOperationException("Tool failed.");
        string Invoke(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw failure;
        }

        var original = AIFunctionFactory.Create(Invoke, "failing_tool", "Diagnostic failure.");
        var config = BuildWithTools("create", [original]);
        var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(config.Tools!));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.InvokeAsync().AsTask());
        Assert.Same(failure, error);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tool.InvokeAsync(cancellationToken: cts.Token).AsTask());
        Assert.Equal(cts.Token, cancelled.CancellationToken);
    }

    private static SessionConfigBase BuildWithTools(string sessionKind, List<AIFunction> tools) =>
        sessionKind switch
        {
            "create" => SessionConfigBuilder.Build(
                "prompt", null, null, null, [], [], tools, null, null, null, null),
            "resume" => SessionConfigBuilder.BuildForResume(
                "prompt", null, null, null, [], [], tools, null, null, null, null),
            "lightweight" => SessionConfigBuilder.BuildLightweight(new LightweightSessionOptions
            {
                SystemPrompt = "prompt",
                Tools = tools
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(sessionKind))
        };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnresolvedExternalDiscovery_DoesNotDisableTheLumiProvider(bool resume)
    {
        var provider = new LumiSkillProvider(_ =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<Skill>>([]));
        SessionConfigBase config = resume
            ? SessionConfigBuilder.BuildForResume(
                "prompt", null, null, null, [@"C:\external-skills"], [], [], null, null, null, null,
                enableCapabilityDiscovery: false, skillProvider: provider)
            : SessionConfigBuilder.Build(
                "prompt", null, null, null, [@"C:\external-skills"], [], [], null, null, null, null,
                enableCapabilityDiscovery: false, skillProvider: provider);

        Assert.True(config.EnableSkills);
        Assert.False(config.EnableConfigDiscovery);
        Assert.Null(config.SkillDirectories);
        Assert.Empty(config.IncludedBuiltinSkills!);
    }

    [Fact]
    public void SkillProvider_ReachesCreateAndResumeWithoutReplacingNativeSkillRoots()
    {
        var provider = new LumiSkillProvider(_ =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<Skill>>([]));
        var roots = new List<string> { @"C:\native-skills" };
        var created = SessionConfigBuilder.Build(
            "prompt", null, null, null, roots, [], [], null, null, null, null,
            skillProvider: provider);
        var resumed = SessionConfigBuilder.BuildForResume(
            "prompt", null, null, null, roots, [], [], null, null, null, null,
            skillProvider: provider);

#pragma warning disable GHCP001 // The trial intentionally exercises the SDK's experimental provider binding.
        Assert.Same(provider, created.SkillProvider);
        Assert.Same(provider, resumed.SkillProvider);
#pragma warning restore GHCP001
        Assert.Equal(roots, created.SkillDirectories);
        Assert.Equal(roots, resumed.SkillDirectories);
        Assert.DoesNotContain("builtin:skill", created.ExcludedTools!);
        Assert.DoesNotContain("builtin:skill", resumed.ExcludedTools!);
    }

    [Fact]
    public void McpToolTimeout_ReachesBothCreateAndResumeConfigurations()
    {
        var data = new AppData
        {
            Settings = new UserSettings { McpToolTimeoutSeconds = 600 },
            McpServers = [new McpServer { Name = "local", Command = "node" }]
        };
        using var plan = McpSessionPlanner.Build(
            data, @"C:\repo", Lumi.Services.Capabilities.CapabilitySnapshot.Empty, new Chat(), null, null);

        var created = SessionConfigBuilder.Build(
            systemPrompt: "prompt", model: null, workingDirectory: @"C:\repo", mcpPlan: plan,
            skillDirectories: null, customAgents: [], tools: [], reasoningEffort: null,
            userInputHandler: null, onPermission: null, hooks: null);
        var resumed = SessionConfigBuilder.BuildForResume(
            systemPrompt: "prompt", model: null, workingDirectory: @"C:\repo", mcpPlan: plan,
            skillDirectories: null, customAgents: [], tools: [], reasoningEffort: null,
            userInputHandler: null, onPermission: null, hooks: null);

        Assert.Equal(600_000, created.McpServers!["local"].Timeout);
        Assert.Equal(600_000, resumed.McpServers!["local"].Timeout);
        Assert.Same(plan.Servers, created.McpServers);
        Assert.Same(plan.Servers, resumed.McpServers);
        Assert.Null(created.ToolSearch);
        Assert.Null(resumed.ToolSearch);
    }

    [Fact]
    public void Build_UsesLumiCopilotConfigDir()
    {
        const string workDir = @"C:\Repo";

        var config = SessionConfigBuilder.Build(
            systemPrompt: "prompt",
            model: "gpt-5.4",
            workingDirectory: workDir,
            mcpPlan: new McpSessionPlan([], []),
           skillDirectories: null,
            customAgents: [],
            tools: [],
            reasoningEffort: null,
            userInputHandler: null,
            onPermission: null,
            hooks: null);

        Assert.Equal(workDir, config.WorkingDirectory);
        Assert.Equal(DataStore.CopilotConfigDir, config.ConfigDirectory);
        Assert.NotEqual(workDir, config.ConfigDirectory);
        // Capability discovery is delegated to the Copilot runtime.
        Assert.True(config.EnableConfigDiscovery);
        Assert.True(config.EnableSkills);
#pragma warning disable GHCP001
        Assert.False(config.RequestExtensions);
#pragma warning restore GHCP001
        Assert.Null(config.SkillDirectories);
        Assert.NotNull(config.McpServers);
        Assert.Empty(config.McpServers!);
        Assert.Contains("builtin:web_fetch", config.ExcludedTools!);
        Assert.Contains("builtin:browser", config.ExcludedTools!);
        Assert.Contains("builtin:ask_user", config.ExcludedTools!);
        Assert.DoesNotContain("builtin:web_search", config.ExcludedTools!);
    }

    [Fact]
    public void Build_WithUnresolvedCapabilities_DisablesRuntimeDiscovery()
    {
        // Config discovery starts every MCP server not named in DisabledMcpServers, and that list
        // is derived from the capability snapshot. Building from an unresolved snapshot would
        // therefore start the servers the user deselected, so discovery must fail closed.
        var config = SessionConfigBuilder.Build(
            systemPrompt: "prompt",
            model: "gpt-5.4",
            workingDirectory: @"C:\Repo",
            mcpPlan: new McpSessionPlan([], []),
            skillDirectories: [@"C:\Users\me\.copilot\skills"],
            customAgents: [],
            tools: [],
            reasoningEffort: null,
            userInputHandler: null,
            onPermission: null,
            hooks: null,
            enableCapabilityDiscovery: false);

        Assert.False(config.EnableConfigDiscovery);
        Assert.False(config.EnableSkills);
        Assert.Null(config.SkillDirectories);
    }

    [Fact]
    public void BuildForResume_WithUnresolvedCapabilities_DisablesRuntimeDiscovery()
    {
        var config = SessionConfigBuilder.BuildForResume(
            systemPrompt: "prompt",
            model: "gpt-5.4",
            workingDirectory: @"C:\Repo",
            mcpPlan: new McpSessionPlan([], []),
            skillDirectories: [@"C:\Users\me\.copilot\skills"],
            customAgents: [],
            tools: [],
            reasoningEffort: null,
            userInputHandler: null,
            onPermission: null,
            hooks: null,
            enableCapabilityDiscovery: false);

        Assert.False(config.EnableConfigDiscovery);
        Assert.False(config.EnableSkills);
        Assert.Null(config.SkillDirectories);
    }

    [Fact]
    public void BuildForResume_UsesLumiCopilotConfigDir()
    {
        const string workDir = @"C:\Repo";

        var config = SessionConfigBuilder.BuildForResume(
            systemPrompt: "prompt",
            model: "gpt-5.4",
            workingDirectory: workDir,
            mcpPlan: new McpSessionPlan([], []),
           skillDirectories: null,
            customAgents: [],
            tools: [],
            reasoningEffort: null,
            userInputHandler: null,
            onPermission: null,
            hooks: null);

        Assert.Equal(workDir, config.WorkingDirectory);
        Assert.Equal(DataStore.CopilotConfigDir, config.ConfigDirectory);
        Assert.NotEqual(workDir, config.ConfigDirectory);
        // Capability discovery is delegated to the Copilot runtime.
        Assert.True(config.EnableConfigDiscovery);
        Assert.True(config.EnableSkills);
#pragma warning disable GHCP001
        Assert.False(config.RequestExtensions);
#pragma warning restore GHCP001
        Assert.Null(config.SkillDirectories);
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
            mcpPlan: new McpSessionPlan([], []),
           skillDirectories: null,
            customAgents: [],
            tools: [],
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
            mcpPlan: new McpSessionPlan([], []),
           skillDirectories: null,
            customAgents: [],
            tools: [],
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
            mcpPlan: new McpSessionPlan([], []),
           skillDirectories: null,
            customAgents: [],
            tools: [],
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
            mcpPlan: new McpSessionPlan([], []),
           skillDirectories: null,
            customAgents: [],
            tools: [],
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
            mcpPlan: new McpSessionPlan([], []),
           skillDirectories: null,
            customAgents: [],
            tools: [],
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
            mcpPlan: new McpSessionPlan([], []),
           skillDirectories: null,
            customAgents: [],
            tools: [],
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
            mcpPlan: new McpSessionPlan([], []),
           skillDirectories: null,
            customAgents: [],
            tools: [],
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
        // Helper sessions stay isolated: no capability discovery at all.
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
