using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Lumi.Models;
using Microsoft.Extensions.AI;

namespace Lumi.Services;

public sealed class LightweightSessionOptions
{
    public required string SystemPrompt { get; init; }
    public string? Model { get; init; }

    /// <summary>The AI feature (<see cref="AiFeatureIds"/>) the session serves. Without <see cref="Model"/>, it picks the
    /// feature's model, or the background model when the feature follows it.</summary>
    public string? Feature { get; init; }
    public bool Streaming { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? ConfigDir { get; init; }
    public List<AIFunction>? Tools { get; init; }
    public GitHub.Copilot.ProviderConfig? Provider { get; init; }
    public ModelCapabilitiesOverride? ModelCapabilities { get; init; }
}

/// <summary>
/// Builds <see cref="SessionConfig"/> and <see cref="ResumeSessionConfig"/>
/// for creating and resuming Copilot sessions with consistent defaults.
    /// </summary>
    public static class SessionConfigBuilder
    {
        private const string ClientName = "lumi";

        /// <summary>
        /// Copilot owns capability discovery. With this on, the runtime finds skills, agents and
        /// MCP servers from every mechanism it supports — project folders, the signed-in user's
        /// profile, plugins and built-ins — so Lumi never has to enumerate the file system for
        /// them. Per-chat opt-outs are expressed with <c>DisabledMcpServers</c> and
        /// <c>DisabledSkills</c> rather than by withholding configuration.
        /// </summary>
        private const bool EnableSdkConfigDiscovery = true;

        /// <summary>Helper sessions stay isolated and cheap: no capability discovery at all.</summary>
        private const bool EnableLightweightConfigDiscovery = false;

        /// <summary>
        /// Lumi is a single-user desktop client (like VS Code), so MCP OAuth tokens must be persisted
        /// in the OS keychain and shared across sessions. The SDK default (<c>null</c>) maps to
        /// <see cref="McpOAuthTokenStorageMode.InMemory"/>, which discards tokens when a session ends —
        /// intended for multitenant hosts. Because Lumi creates and resumes sessions frequently
        /// (reloads, reconnects, per-chat lifecycle), in-memory tokens are lost at every session
        /// boundary, so OAuth MCP servers re-prompt or drop mid-conversation. Persisting them lets the
        /// runtime's browserless fallback silently reuse the cached token on every reconnect.
        /// </summary>
        private const McpOAuthTokenStorageMode McpOAuthTokenStorage = McpOAuthTokenStorageMode.Persistent;

        /// <summary>
        /// Reasoning models (e.g. the GPT-5 family) never expose their raw chain-of-thought — they
        /// only emit it as an opt-in summary that defaults to "none". Without requesting a summary the
        /// model still reasons, but no reasoning text reaches the client, so the transcript shows none.
        /// Request a detailed summary so reasoning stays visible; the CLI ignores this for models that
        /// don't support configurable reasoning summaries.
        /// </summary>
        public static readonly ReasoningSummary DefaultReasoningSummary = ReasoningSummary.Detailed;

    /// <summary>Built-in namespaces that Lumi provides itself and should not be duplicated by the SDK.</summary>
    private static ToolSet ExcludedBuiltInTools()
        => new ToolSet()
            .AddBuiltIn("web_fetch")
            .AddBuiltIn("browser")
            .AddBuiltIn("ask_user");

    /// <summary>
    /// Builds a <see cref="SessionConfig"/> for creating a new session.
    /// </summary>
    public static SessionConfig Build(
        string? systemPrompt,
        string? model,
        string? workingDirectory,
        McpSessionPlan? mcpPlan,
        List<string>? skillDirectories,
        List<CustomAgentConfig>? customAgents,
        List<AIFunction>? tools,
        string? reasoningEffort,
        Func<UserInputRequest, UserInputInvocation, Task<UserInputResponse>>? userInputHandler,
        Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>>? onPermission,
        SessionHooks? hooks,
        string? agentName = null,
        string? contextTier = null,
        GitHub.Copilot.ProviderConfig? provider = null,
        bool enableCapabilityDiscovery = true,
        ISkillProvider? skillProvider = null,
        ModelCapabilitiesOverride? modelCapabilities = null,
        IReadOnlyList<string>? pluginDirectories = null)
    {
        var config = new SessionConfig
        {
            ClientName = ClientName,
            Model = model,
            Streaming = true,
            WorkingDirectory = workingDirectory,
            ConfigDirectory = GetDefaultConfigDir(),
            EnableConfigDiscovery = enableCapabilityDiscovery && EnableSdkConfigDiscovery,
            SkillProvider = skillProvider,
            EnableSessionStore = true,
            EnableSessionTelemetry = false,
            ExcludedTools = ExcludedBuiltInTools(),
            InfiniteSessions = new InfiniteSessionConfig { Enabled = true },
            OnPermissionRequest = onPermission ?? PermissionHandler.ApproveAll,
            ContextTier = CreateContextTier(contextTier),
            McpOAuthTokenStorage = McpOAuthTokenStorage,
            Provider = provider,
            ModelCapabilities = modelCapabilities,
            EnableMcpApps = enableCapabilityDiscovery && NativeBrowserLogic.IsEmbeddedBrowserAvailable,
            RequestCanvasRenderer = enableCapabilityDiscovery && NativeBrowserLogic.IsEmbeddedBrowserAvailable,
            RequestExtensions = enableCapabilityDiscovery && NativeBrowserLogic.IsEmbeddedBrowserAvailable
                && CopilotExtensionLaunchProvider.NodePath is not null,
        };

        Populate(config, systemPrompt, reasoningEffort, mcpPlan, skillDirectories,
            customAgents, tools, userInputHandler, hooks, agentName, enableCapabilityDiscovery);
        ApplySessionPluginDirectories(config, enableCapabilityDiscovery, pluginDirectories);

        return config;
    }

    /// <summary>
    /// Builds a <see cref="ResumeSessionConfig"/> for resuming an existing session.
    /// </summary>
    public static ResumeSessionConfig BuildForResume(
        string? systemPrompt,
        string? model,
        string? workingDirectory,
        McpSessionPlan? mcpPlan,
        List<string>? skillDirectories,
        List<CustomAgentConfig>? customAgents,
        List<AIFunction>? tools,
        string? reasoningEffort,
        Func<UserInputRequest, UserInputInvocation, Task<UserInputResponse>>? userInputHandler,
        Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>>? onPermission,
        SessionHooks? hooks,
        string? agentName = null,
        string? contextTier = null,
        GitHub.Copilot.ProviderConfig? provider = null,
        bool enableCapabilityDiscovery = true,
        ISkillProvider? skillProvider = null,
        ModelCapabilitiesOverride? modelCapabilities = null,
        IReadOnlyList<string>? pluginDirectories = null)
    {
        var config = new ResumeSessionConfig
        {
            ContinuePendingWork = false,
            ClientName = ClientName,
            Model = model,
            Streaming = true,
            WorkingDirectory = workingDirectory,
            ConfigDirectory = GetDefaultConfigDir(),
            EnableConfigDiscovery = enableCapabilityDiscovery && EnableSdkConfigDiscovery,
            SkillProvider = skillProvider,
            EnableSessionStore = true,
            EnableSessionTelemetry = false,
            ExcludedTools = ExcludedBuiltInTools(),
            InfiniteSessions = new InfiniteSessionConfig { Enabled = true },
            OnPermissionRequest = onPermission ?? PermissionHandler.ApproveAll,
            ContextTier = CreateContextTier(contextTier),
            McpOAuthTokenStorage = McpOAuthTokenStorage,
            Provider = provider,
            ModelCapabilities = modelCapabilities,
            EnableMcpApps = enableCapabilityDiscovery && NativeBrowserLogic.IsEmbeddedBrowserAvailable,
            RequestCanvasRenderer = enableCapabilityDiscovery && NativeBrowserLogic.IsEmbeddedBrowserAvailable,
            RequestExtensions = enableCapabilityDiscovery && NativeBrowserLogic.IsEmbeddedBrowserAvailable
                && CopilotExtensionLaunchProvider.NodePath is not null,
        };

        Populate(config, systemPrompt, reasoningEffort, mcpPlan, skillDirectories,
            customAgents, tools, userInputHandler, hooks, agentName, enableCapabilityDiscovery);
        ApplySessionPluginDirectories(config, enableCapabilityDiscovery, pluginDirectories);

        return config;
    }

    /// <summary>
    /// Builds a lightweight single-purpose <see cref="SessionConfig"/> for helper flows.
    /// One-shot callers delete the session after use. A warm helper must use native
    /// transient queries rather than append unrelated requests to its conversation.
    /// </summary>
    public static SessionConfig BuildLightweight(LightweightSessionOptions options)
    {
        var config = new SessionConfig
        {
            ClientName = ClientName,
            Model = options.Model,
            Provider = options.Provider,
            ModelCapabilities = options.ModelCapabilities,
            Streaming = options.Streaming,
            WorkingDirectory = options.WorkingDirectory,
            ConfigDirectory = options.ConfigDir ?? GetDefaultConfigDir(),
            EnableConfigDiscovery = EnableLightweightConfigDiscovery,
            EnableSessionStore = false,
            EnableSessionTelemetry = false,
            RequestExtensions = false,
            RequestCanvasRenderer = false,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            OnPermissionRequest = PermissionHandler.ApproveAll,
            SystemMessage = new SystemMessageConfig
            {
                Content = options.SystemPrompt,
                Mode = SystemMessageMode.Replace
            }
        };

        if (options.Tools is { Count: > 0 })
        {
            config.Tools = PreloadLumiTools(options.Tools);
            config.AvailableTools = options.Tools.Select(t => t.Name).ToList();
        }
        else
        {
            config.AvailableTools = [];
            config.ExcludedTools = ExcludeAllToolSources();
        }

        return config;
    }

    /// <summary>
    /// The host's answer to a native permission request. In Full access the runtime's allow-all mode
    /// normally answers first, and this approves anything still routed here. Every other request waits
    /// for Lumi: a person on an approval card, or, in Auto-review, the reviewer's recommendation.
    /// </summary>
    internal static Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>> BuildPermissionHandler(
        Func<ApprovalMode> mode) => (request, _) =>
        Task.FromResult(NeedsPerson(mode(), request) ? PermissionDecision.NoResult() : PermissionDecision.ApproveOnce());

    /// <summary>
    /// True when Lumi must not approve a request on its own: stricter modes, an Always-ask rule or
    /// managed policy (which forbids host auto-approval), a request kind this SDK doesn't know, or a
    /// request to leave the command sandbox.
    /// </summary>
    internal static bool NeedsPerson(ApprovalMode mode, PermissionRequest request)
        => mode != ApprovalMode.FullAccess
           || request.ManagedApprovalRequired == true
           || request.GetType() == typeof(PermissionRequest)
           || request is PermissionRequestShell { RequestSandboxBypass: true }
           || request is PermissionRequestWrite { RequestSandboxBypass: true }
           || request is PermissionRequestRead { RequestSandboxBypass: true }
           || request is PermissionRequestUrl { RequestSandboxBypass: true };

    // Copilot can stop exposing deferred tools after an earlier lookup or a resume.
    // Preload Lumi-owned functions only; MCP tools keep their separate loading policy.
    private static List<AIFunctionDeclaration> PreloadLumiTools(List<AIFunction> tools)
        => tools.ConvertAll<AIFunctionDeclaration>(tool => new PreloadedLumiTool(tool));

    private sealed class PreloadedLumiTool(AIFunction innerFunction) : DelegatingAIFunction(innerFunction)
    {
        public override IReadOnlyDictionary<string, object?> AdditionalProperties { get; } = Describe(innerFunction);

        private static Dictionary<string, object?> Describe(AIFunction function)
        {
            var properties = new Dictionary<string, object?>(function.AdditionalProperties, StringComparer.Ordinal)
            {
                ["defer"] = CopilotToolDefer.Never
            };
            // Tools that only read or talk to the user run without approval in every mode.
            if (Permissions.ApprovalPolicy.SkipsApproval(function.Name))
                properties["skip_permission"] = true;
            return properties;
        }
    }

    private static string GetDefaultConfigDir()
    {
        var configDir = DataStore.CopilotConfigDir;
        if (!Directory.Exists(configDir))
            Directory.CreateDirectory(configDir);
        return configDir;
    }

    private static ToolSet ExcludeAllToolSources()
        => new ToolSet()
            .AddBuiltIn("*")
            .AddMcp("*")
            .AddCustom("*");

    /// <summary>
    /// Hands a session the skill roots its own configuration would not reach.
    /// </summary>
    /// <remarks>
    /// Config discovery resolves a session's "personal" skill scope from its config directory, and
    /// Lumi points that at its own folder to keep Lumi's Copilot configuration isolated. Without
    /// this the user's <c>~/.copilot</c> skills are discovered and offered but fail to activate.
    /// Runtime discovery paths and configured personal skill folders add to config discovery
    /// rather than replacing it, so project, plugin and built-in skills keep loading normally.
    /// </remarks>
    private static void ApplySessionSkillRoots(
        SessionConfigBase config,
        bool enableCapabilityDiscovery,
        List<string>? skillDirectories)
    {
        if (!enableCapabilityDiscovery || skillDirectories is not { Count: > 0 })
            return;

        config.SkillDirectories = skillDirectories;
    }

    /// <summary>
    /// Loads the user's installed Copilot plugins into a session.
    /// </summary>
    /// <remarks>
    /// The runtime installs plugins into its own home and only loads them for sessions configured
    /// from there; Lumi's sessions use Lumi's folder. Like discovery itself this needs a resolved
    /// snapshot: a plugin's MCP servers start unless named in DisabledMcpServers, which is derived
    /// from that snapshot.
    /// </remarks>
    private static void ApplySessionPluginDirectories(
        SessionConfigBase config,
        bool enableCapabilityDiscovery,
        IReadOnlyList<string>? pluginDirectories)
    {
        if (!enableCapabilityDiscovery || pluginDirectories is not { Count: > 0 })
            return;

        config.PluginDirectories = pluginDirectories.ToList();
    }

    internal static ContextTier? CreateContextTier(string? contextTier)
    {
        if (string.IsNullOrWhiteSpace(contextTier))
            return null;

        return contextTier.Trim().ToLowerInvariant() switch
        {
            ModelContextWindowTiers.Default => ContextTier.Default,
            ModelContextWindowTiers.LongContext => ContextTier.LongContext,
            var tier => new ContextTier(tier)
        };
    }

    /// <summary>Sets the shared optional properties on a <see cref="SessionConfig"/>.</summary>
    private static void Populate(
        SessionConfig config,
        string? systemPrompt,
        string? reasoningEffort,
        McpSessionPlan? mcpPlan,
        List<string>? skillDirectories,
        List<CustomAgentConfig>? customAgents,
        List<AIFunction>? tools,
        Func<UserInputRequest, UserInputInvocation, Task<UserInputResponse>>? userInputHandler,
        SessionHooks? hooks,
        string? agentName,
        bool enableCapabilityDiscovery = true)
    {
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
            config.ReasoningEffort = reasoningEffort;

        config.ReasoningSummary = DefaultReasoningSummary;

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            config.SystemMessage = new SystemMessageConfig
            {
                Content = systemPrompt,
                Mode = SystemMessageMode.Append
            };

        config.EnableSkills = enableCapabilityDiscovery || config.SkillProvider is not null;
        if (!enableCapabilityDiscovery)
            config.IncludedBuiltinSkills = [];

        ApplySessionSkillRoots(config, enableCapabilityDiscovery, skillDirectories);

        if (customAgents is { Count: > 0 })
            config.CustomAgents = customAgents;

        if (tools is { Count: > 0 })
            config.Tools = PreloadLumiTools(tools);

        if (mcpPlan is not null)
        {
            config.McpServers = mcpPlan.Servers;
            if (mcpPlan.DisabledServerNames.Count > 0)
                config.DisabledMcpServers = mcpPlan.DisabledServerNames.ToList();
        }

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
        McpSessionPlan? mcpPlan,
        List<string>? skillDirectories,
        List<CustomAgentConfig>? customAgents,
        List<AIFunction>? tools,
        Func<UserInputRequest, UserInputInvocation, Task<UserInputResponse>>? userInputHandler,
        SessionHooks? hooks,
        string? agentName,
        bool enableCapabilityDiscovery = true)
    {
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
            config.ReasoningEffort = reasoningEffort;

        config.ReasoningSummary = DefaultReasoningSummary;

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            config.SystemMessage = new SystemMessageConfig
            {
                Content = systemPrompt,
                Mode = SystemMessageMode.Append
            };

        config.EnableSkills = enableCapabilityDiscovery || config.SkillProvider is not null;
        if (!enableCapabilityDiscovery)
            config.IncludedBuiltinSkills = [];

        ApplySessionSkillRoots(config, enableCapabilityDiscovery, skillDirectories);

        if (customAgents is { Count: > 0 })
            config.CustomAgents = customAgents;

        if (tools is { Count: > 0 })
            config.Tools = PreloadLumiTools(tools);

        if (mcpPlan is not null)
        {
            config.McpServers = mcpPlan.Servers;
            if (mcpPlan.DisabledServerNames.Count > 0)
                config.DisabledMcpServers = mcpPlan.DisabledServerNames.ToList();
        }

        if (userInputHandler is not null)
            config.OnUserInputRequest = userInputHandler;

        if (hooks is not null)
            config.Hooks = hooks;

        if (!string.IsNullOrWhiteSpace(agentName))
            config.Agent = agentName;
    }
}
