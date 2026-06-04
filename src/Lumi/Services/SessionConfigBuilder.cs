using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

namespace Lumi.Services;

public sealed class LightweightSessionOptions
{
    public required string SystemPrompt { get; init; }
    public string? Model { get; init; }
    public bool Streaming { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? ConfigDir { get; init; }
    public List<AIFunction>? Tools { get; init; }
    public ProviderConfig? Provider { get; init; }
}

/// <summary>
/// Builds <see cref="SessionConfig"/> and <see cref="ResumeSessionConfig"/>
/// for creating and resuming Copilot sessions with consistent defaults.
    /// </summary>
    public static class SessionConfigBuilder
    {
        private const string ClientName = "lumi";

        /// <summary>Lumi owns MCP and skill selection explicitly; SDK discovery would bypass per-chat toggles.</summary>
        private const bool EnableSdkConfigDiscovery = false;

    /// <summary>Tools that Lumi provides natively and should not be duplicated by the SDK.</summary>
    private static readonly List<string> ExcludedBuiltInTools = ["web_fetch"];

    /// <summary>
    /// Builds a <see cref="SessionConfig"/> for creating a new session.
    /// </summary>
    public static SessionConfig Build(
        string? systemPrompt,
        string? model,
        string? workingDirectory,
        List<string>? skillDirectories,
        List<CustomAgentConfig>? customAgents,
        List<AIFunction>? tools,
        Dictionary<string, McpServerConfig>? mcpServers,
        string? reasoningEffort,
        UserInputHandler? userInputHandler,
        PermissionRequestHandler? onPermission,
        SessionHooks? hooks,
        string? agentName = null,
        ProviderConfig? provider = null)
    {
        var config = new SessionConfig
        {
            ClientName = ClientName,
            Model = model,
            Streaming = true,
            WorkingDirectory = workingDirectory,
            ConfigDir = GetDefaultConfigDir(),
            EnableConfigDiscovery = EnableSdkConfigDiscovery,
            ExcludedTools = ExcludedBuiltInTools,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = true },
            OnPermissionRequest = onPermission ?? PermissionHandler.ApproveAll,
            Provider = provider,
        };

        Populate(config, systemPrompt, reasoningEffort, skillDirectories,
            customAgents, tools, mcpServers, userInputHandler, hooks, agentName);

        return config;
    }

    /// <summary>
    /// Builds a <see cref="ResumeSessionConfig"/> for resuming an existing session.
    /// </summary>
    public static ResumeSessionConfig BuildForResume(
        string? systemPrompt,
        string? model,
        string? workingDirectory,
        List<string>? skillDirectories,
        List<CustomAgentConfig>? customAgents,
        List<AIFunction>? tools,
        Dictionary<string, McpServerConfig>? mcpServers,
        string? reasoningEffort,
        UserInputHandler? userInputHandler,
        PermissionRequestHandler? onPermission,
        SessionHooks? hooks,
        string? agentName = null,
        ProviderConfig? provider = null)
    {
        var config = new ResumeSessionConfig
        {
            ClientName = ClientName,
            Model = model,
            Streaming = true,
            WorkingDirectory = workingDirectory,
            ConfigDir = GetDefaultConfigDir(),
            EnableConfigDiscovery = EnableSdkConfigDiscovery,
            ExcludedTools = ExcludedBuiltInTools,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = true },
            OnPermissionRequest = onPermission ?? PermissionHandler.ApproveAll,
            Provider = provider,
        };

        Populate(config, systemPrompt, reasoningEffort, skillDirectories,
            customAgents, tools, mcpServers, userInputHandler, hooks, agentName);

        return config;
    }

    /// <summary>
    /// Builds a lightweight single-purpose <see cref="SessionConfig"/> for helper flows.
    /// The SDK currently has no public transient-session API, so these sessions are
    /// configured to be as cheap as possible and should be explicitly deleted after use.
    /// </summary>
    public static SessionConfig BuildLightweight(LightweightSessionOptions options)
    {
        var config = new SessionConfig
        {
            ClientName = ClientName,
            Model = options.Model,
            Streaming = options.Streaming,
            WorkingDirectory = options.WorkingDirectory,
            ConfigDir = options.ConfigDir ?? GetDefaultConfigDir(),
            EnableConfigDiscovery = EnableSdkConfigDiscovery,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            OnPermissionRequest = PermissionHandler.ApproveAll,
            SystemMessage = new SystemMessageConfig
            {
                Content = options.SystemPrompt,
                Mode = SystemMessageMode.Replace
            },
            Provider = options.Provider,
        };

        if (options.Tools is { Count: > 0 })
        {
            config.Tools = options.Tools;
            config.AvailableTools = options.Tools.Select(t => t.Name).ToList();
        }
        else
        {
            config.AvailableTools = [];
            config.ExcludedTools = ["*"];
        }

        return config;
    }

    private static string GetDefaultConfigDir()
    {
        var configDir = DataStore.CopilotConfigDir;
        if (!Directory.Exists(configDir))
            Directory.CreateDirectory(configDir);
        return configDir;
    }

    /// <summary>Sets the shared optional properties on a <see cref="SessionConfig"/>.</summary>
    private static void Populate(
        SessionConfig config,
        string? systemPrompt,
        string? reasoningEffort,
        List<string>? skillDirectories,
        List<CustomAgentConfig>? customAgents,
        List<AIFunction>? tools,
        Dictionary<string, McpServerConfig>? mcpServers,
        UserInputHandler? userInputHandler,
        SessionHooks? hooks,
        string? agentName)
    {
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
            config.ReasoningEffort = reasoningEffort;

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            config.SystemMessage = new SystemMessageConfig { Content = systemPrompt, Mode = SystemMessageMode.Append };

        if (skillDirectories is { Count: > 0 })
            config.SkillDirectories = skillDirectories;

        if (customAgents is { Count: > 0 })
            config.CustomAgents = customAgents;

        if (tools is { Count: > 0 })
            config.Tools = tools;

        if (mcpServers is not null)
            config.McpServers = mcpServers;

        if (userInputHandler is not null)
            config.OnUserInputRequest = userInputHandler;

        if (hooks is not null)
            config.Hooks = hooks;

        if (!string.IsNullOrWhiteSpace(agentName))
            config.Agent = agentName;
    }

    /// <summary>Sets the shared optional properties on a <see cref="ResumeSessionConfig"/>.</summary>
    private static void Populate(
        ResumeSessionConfig config,
        string? systemPrompt,
        string? reasoningEffort,
        List<string>? skillDirectories,
        List<CustomAgentConfig>? customAgents,
        List<AIFunction>? tools,
        Dictionary<string, McpServerConfig>? mcpServers,
        UserInputHandler? userInputHandler,
        SessionHooks? hooks,
        string? agentName)
    {
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
            config.ReasoningEffort = reasoningEffort;

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            config.SystemMessage = new SystemMessageConfig { Content = systemPrompt, Mode = SystemMessageMode.Append };

        if (skillDirectories is { Count: > 0 })
            config.SkillDirectories = skillDirectories;

        if (customAgents is { Count: > 0 })
            config.CustomAgents = customAgents;

        if (tools is { Count: > 0 })
            config.Tools = tools;

        if (mcpServers is not null)
            config.McpServers = mcpServers;

        if (userInputHandler is not null)
            config.OnUserInputRequest = userInputHandler;

        if (hooks is not null)
            config.Hooks = hooks;

        if (!string.IsNullOrWhiteSpace(agentName))
            config.Agent = agentName;
    }
}
