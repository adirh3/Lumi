using System.Collections.Generic;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

namespace Lumi.Services;

/// <summary>
/// Builds <see cref="SessionConfig"/> and <see cref="ResumeSessionConfig"/>
/// for creating and resuming Copilot sessions with consistent defaults.
/// </summary>
public static class SessionConfigBuilder
{
    private const string ClientName = "lumi";

    /// <summary>Tools that Lumi provides natively and should not be duplicated by the SDK.</summary>
    private static readonly List<string> ExcludedBuiltInTools = ["web_fetch", "web_search"];

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
        Dictionary<string, object>? mcpServers,
        string? reasoningEffort,
        UserInputHandler? userInputHandler,
        PermissionRequestHandler? onPermission,
        SessionHooks? hooks,
        string? agentName = null)
    {
        var config = new SessionConfig
        {
            ClientName = ClientName,
            Model = model,
            Streaming = true,
            WorkingDirectory = workingDirectory,
            ExcludedTools = ExcludedBuiltInTools,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = true },
            OnPermissionRequest = onPermission ?? PermissionHandler.ApproveAll,
        };

        if (!string.IsNullOrWhiteSpace(reasoningEffort))
            config.ReasoningEffort = reasoningEffort;

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            config.SystemMessage = new SystemMessageConfig
            {
                Content = systemPrompt,
                Mode = SystemMessageMode.Append,
            };
        }

        if (skillDirectories is { Count: > 0 })
            config.SkillDirectories = skillDirectories;

        if (customAgents is { Count: > 0 })
            config.CustomAgents = customAgents;

        if (tools is { Count: > 0 })
            config.Tools = tools;

        if (mcpServers is { Count: > 0 })
            config.McpServers = mcpServers;

        if (userInputHandler is not null)
            config.OnUserInputRequest = userInputHandler;

        if (hooks is not null)
            config.Hooks = hooks;

        if (!string.IsNullOrWhiteSpace(agentName))
            config.Agent = agentName;

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
        Dictionary<string, object>? mcpServers,
        string? reasoningEffort,
        UserInputHandler? userInputHandler,
        PermissionRequestHandler? onPermission,
        SessionHooks? hooks,
        string? agentName = null)
    {
        var config = new ResumeSessionConfig
        {
            ClientName = ClientName,
            Model = model,
            Streaming = true,
            WorkingDirectory = workingDirectory,
            ExcludedTools = ExcludedBuiltInTools,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = true },
            OnPermissionRequest = onPermission ?? PermissionHandler.ApproveAll,
        };

        if (!string.IsNullOrWhiteSpace(reasoningEffort))
            config.ReasoningEffort = reasoningEffort;

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            config.SystemMessage = new SystemMessageConfig
            {
                Content = systemPrompt,
                Mode = SystemMessageMode.Append,
            };
        }

        if (skillDirectories is { Count: > 0 })
            config.SkillDirectories = skillDirectories;

        if (customAgents is { Count: > 0 })
            config.CustomAgents = customAgents;

        if (tools is { Count: > 0 })
            config.Tools = tools;

        if (mcpServers is { Count: > 0 })
            config.McpServers = mcpServers;

        if (userInputHandler is not null)
            config.OnUserInputRequest = userInputHandler;

        if (hooks is not null)
            config.Hooks = hooks;

        if (!string.IsNullOrWhiteSpace(agentName))
            config.Agent = agentName;

        return config;
    }
}
