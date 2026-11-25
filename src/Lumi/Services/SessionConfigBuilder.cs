using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Copilot;
using Microsoft.Copilot.Rpc;
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
    public Microsoft.Copilot.ProviderConfig? Provider { get; init; }
    public string? ProviderModelId { get; init; }
    public ModelCapabilitiesOverride? ModelCapabilities { get; init; }
}

/// <summary>
/// Builds <see cref="CreateSessionOptions"/> and <see cref="ResumeSessionOptions"/>
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
    /// Builds a <see cref="CreateSessionOptions"/> for creating a new session.
    /// </summary>
    public static CreateSessionOptions Build(
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
        Microsoft.Copilot.ProviderConfig? provider = null,
        bool enableCapabilityDiscovery = true,
        ISkillProvider? skillProvider = null,
        ModelCapabilitiesOverride? modelCapabilities = null,
        IReadOnlyList<string>? pluginDirectories = null,
        string? providerModelId = null)
    {
        var config = new CreateSessionOptions
        {
            ClientName = ClientName,
            Model = SessionModelId(model, provider, providerModelId),
            WorkingDirectory = workingDirectory,
            ConfigDirectory = GetDefaultConfigDir(),
            EnableConfigDiscovery = enableCapabilityDiscovery && EnableSdkConfigDiscovery,
            Inference = CreateInferenceOptions(model, reasoningEffort, contextTier, provider, providerModelId, modelCapabilities),
            Events = new SessionEventOptions { Streaming = true },
            Filesystem = new CreateSessionFilesystemOptions { TrackFileChanges = true },
            State = new SessionStateOptions
            {
                EnableCrossSessionStore = true,
                InfiniteSessions = new InfiniteSessionConfig { Enabled = true }
            },
            Permissions = new SessionPermissionOptions { OnRequest = onPermission ?? PermissionHandler.ApproveAll },
            GitHub = CreateGitHubOptions(model, provider),
            Skills = new SessionSkillOptions { Provider = skillProvider },
            Tools = new SessionToolOptions { ExcludedTools = ExcludedBuiltInTools() },
            Mcp = new SessionMcpOptions
            {
                OauthTokenStorage = McpOAuthTokenStorage,
                EnableApps = enableCapabilityDiscovery && NativeBrowserLogic.IsEmbeddedBrowserAvailable
            },
            Canvases = new SessionCanvasOptions
            {
                RequestRenderer = enableCapabilityDiscovery && NativeBrowserLogic.IsEmbeddedBrowserAvailable
            },
            Extensions = BuildExtensionOptions(enableCapabilityDiscovery),
            Agents = new SessionAgentOptions(),
            Interaction = new SessionInteractionOptions(),
            Hooks = new SessionHookOptions(),
            Plugins = new SessionPluginOptions(),
            Policy = new SessionPolicyOptions()
        };

        Populate(config, systemPrompt, mcpPlan, skillDirectories,
            customAgents, tools, userInputHandler, hooks, agentName, enableCapabilityDiscovery);
        ApplySessionPluginDirectories(config, enableCapabilityDiscovery, pluginDirectories);

        return config;
    }

    /// <summary>
    /// Builds a <see cref="ResumeSessionOptions"/> for resuming an existing session.
    /// </summary>
    /// <remarks>
    /// File-change tracking (what lets an edit put back the files the agent changed) only starts
    /// with a session's first turn: a session resumed after untracked turns stays untracked, and a
    /// tracked one keeps tracking.
    /// </remarks>
    public static ResumeSessionOptions BuildForResume(
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
        Microsoft.Copilot.ProviderConfig? provider = null,
        bool enableCapabilityDiscovery = true,
        ISkillProvider? skillProvider = null,
        ModelCapabilitiesOverride? modelCapabilities = null,
        IReadOnlyList<string>? pluginDirectories = null,
        string? providerModelId = null)
    {
        var config = new ResumeSessionOptions
        {
            ContinuePendingWork = false,
            ClientName = ClientName,
            Model = SessionModelId(model, provider, providerModelId),
            WorkingDirectory = workingDirectory,
            ConfigDirectory = GetDefaultConfigDir(),
            EnableConfigDiscovery = enableCapabilityDiscovery && EnableSdkConfigDiscovery,
            Inference = CreateInferenceOptions(model, reasoningEffort, contextTier, provider, providerModelId, modelCapabilities),
            Events = new SessionEventOptions { Streaming = true },
            Filesystem = new SessionFilesystemOptions { TrackFileChanges = true },
            State = new SessionStateOptions
            {
                EnableCrossSessionStore = true,
                InfiniteSessions = new InfiniteSessionConfig { Enabled = true }
            },
            Permissions = new SessionPermissionOptions { OnRequest = onPermission ?? PermissionHandler.ApproveAll },
            GitHub = CreateGitHubOptions(model, provider),
            Skills = new SessionSkillOptions { Provider = skillProvider },
            Tools = new SessionToolOptions { ExcludedTools = ExcludedBuiltInTools() },
            Mcp = new SessionMcpOptions
            {
                OauthTokenStorage = McpOAuthTokenStorage,
                EnableApps = enableCapabilityDiscovery && NativeBrowserLogic.IsEmbeddedBrowserAvailable
            },
            Canvases = new ResumeSessionCanvasOptions
            {
                RequestRenderer = enableCapabilityDiscovery && NativeBrowserLogic.IsEmbeddedBrowserAvailable
            },
            Extensions = BuildExtensionOptions(enableCapabilityDiscovery),
            Agents = new SessionAgentOptions(),
            Interaction = new SessionInteractionOptions(),
            Hooks = new SessionHookOptions(),
            Plugins = new SessionPluginOptions(),
            Policy = new SessionPolicyOptions()
        };

        Populate(config, systemPrompt, mcpPlan, skillDirectories,
            customAgents, tools, userInputHandler, hooks, agentName, enableCapabilityDiscovery);
        ApplySessionPluginDirectories(config, enableCapabilityDiscovery, pluginDirectories);

        return config;
    }

    /// <summary>
    /// Builds the bare <see cref="ResumeSessionOptions"/> of a handle resumed only to read a session (to
    /// fork it, or to see which files an edit can put back): no prompt, tools or connectors, and no
    /// capability discovery, which would start every discovered MCP server for a handle thrown away at once.
    /// Copilot resumes leave the model unset so an unavailable model from an older turn cannot block the read.
    /// </summary>
    /// <param name="provider">
    /// The account the session's chat uses. Without one, a profile that uses only its own accounts
    /// refuses the resume.
    /// </param>
    public static ResumeSessionOptions BuildForReadOnlyResume(
        Microsoft.Copilot.ProviderConfig? provider = null,
        string? providerModelId = null,
        string? wireModelId = null,
        ModelCapabilitiesOverride? modelCapabilities = null)
        => BuildForResume(
            systemPrompt: null,
            model: provider is null ? null : wireModelId ?? provider.WireModel ?? provider.ModelId,
            workingDirectory: null, mcpPlan: null,
            skillDirectories: null, customAgents: null, tools: null, reasoningEffort: null,
            userInputHandler: null, onPermission: null, hooks: null,
            provider: provider, enableCapabilityDiscovery: false,
            providerModelId: providerModelId, modelCapabilities: modelCapabilities);

    /// <summary>
    /// Builds a lightweight single-purpose <see cref="CreateSessionOptions"/> for helper flows.
    /// One-shot callers delete the session after use. A warm helper must use native
    /// transient queries rather than append unrelated requests to its conversation.
    /// </summary>
    public static CreateSessionOptions BuildLightweight(LightweightSessionOptions options)
    {
        var config = new CreateSessionOptions
        {
            ClientName = ClientName,
            Model = SessionModelId(options.Model, options.Provider, options.ProviderModelId),
            WorkingDirectory = options.WorkingDirectory,
            ConfigDirectory = options.ConfigDir ?? GetDefaultConfigDir(),
            EnableConfigDiscovery = EnableLightweightConfigDiscovery,
            Inference = CreateInferenceOptions(
                options.Model, null, null, options.Provider, options.ProviderModelId, options.ModelCapabilities),
            Events = new SessionEventOptions { Streaming = options.Streaming },
            State = new SessionStateOptions
            {
                EnableCrossSessionStore = false,
                InfiniteSessions = new InfiniteSessionConfig { Enabled = false }
            },
            Permissions = new SessionPermissionOptions { OnRequest = PermissionHandler.ApproveAll },
            GitHub = CreateGitHubOptions(options.Model, options.Provider),
            Skills = new SessionSkillOptions { Enabled = false },
            Mcp = new SessionMcpOptions { EnableApps = false },
            Canvases = new SessionCanvasOptions { RequestRenderer = false },
            Extensions = new SessionExtensionOptions { Enabled = false },
            Tools = new SessionToolOptions(),
            Agents = new SessionAgentOptions(),
            Interaction = new SessionInteractionOptions(),
            Hooks = new SessionHookOptions(),
            Plugins = new SessionPluginOptions(),
            Policy = new SessionPolicyOptions(),
            SystemMessage = new SystemMessageConfig
            {
                Content = options.SystemPrompt,
                Mode = SystemMessageMode.Replace
            }
        };

        if (options.Tools is { Count: > 0 })
        {
            config.Tools.Definitions = PreloadLumiTools(options.Tools);
            config.Tools.AvailableTools = options.Tools.Select(t => t.Name).ToList();
        }
        else
        {
            config.Tools.AvailableTools = [];
            config.Tools.ExcludedTools = ExcludeAllToolSources();
        }

        return config;
    }

    internal static void ApplyProviderRouting(
        SessionOptionsBase config,
        string? wireModel,
        ProviderConfig provider,
        string? providerModelId,
        ModelCapabilitiesOverride? capabilities)
    {
        config.Inference ??= new SessionInferenceOptions();
        config.GitHub ??= new SessionGitHubOptions();
        config.Model = SessionModelId(wireModel, provider, providerModelId);
        config.Inference.Providers = [CreateNamedProvider(provider)];
        config.Inference.Models = [CreateProviderModel(provider, wireModel, providerModelId, capabilities)];
        config.Inference.Capabilities = null;
        config.GitHub.Capi = new Microsoft.Copilot.CapiSessionOptions { Enabled = false };
    }

    internal static async Task EnsureProviderModelRegisteredAsync(
        CopilotSession session,
        SessionModelRoute route,
        ModelCapabilitiesOverride? capabilities,
        CancellationToken cancellationToken)
    {
        if (route.Provider is null || route.ByokModel is null || route.SessionModelId is not { } sessionModelId)
            return;

        var catalog = await session.Rpc.Model.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (catalog.List.Any(model =>
                model.TryGetProperty("id", out var id)
                && string.Equals(id.GetString(), sessionModelId, StringComparison.Ordinal)))
            return;

        await session.Rpc.Provider.AddAsync(
            models: [CreateProviderModelRpc(
                route.Provider, route.WireModelId, route.ByokModel.Id, capabilities)],
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal static SetModelOptions CreateSetModelOptions(
        string? selection,
        string? reasoningEffort,
        string? contextTier,
        ModelCapabilitiesOverride? capabilities = null)
    {
#pragma warning disable GHCP001 // Switch the Auto model and its advertised routing preference atomically.
        return new SetModelOptions
        {
            ReasoningEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort,
            ReasoningSummary = DefaultReasoningSummary,
            ContextTier = CreateContextTier(contextTier),
            ModelCapabilities = capabilities,
            AutoTier = CopilotModelCatalog.GetAutoTier(selection),
            ResetAutoTier = string.Equals(selection, "auto", StringComparison.Ordinal)
        };
#pragma warning restore GHCP001
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
        SessionOptionsBase config,
        bool enableCapabilityDiscovery,
        List<string>? skillDirectories)
    {
        if (!enableCapabilityDiscovery || skillDirectories is not { Count: > 0 })
            return;

        config.Skills.Directories = skillDirectories;
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
        SessionOptionsBase config,
        bool enableCapabilityDiscovery,
        IReadOnlyList<string>? pluginDirectories)
    {
        if (!enableCapabilityDiscovery || pluginDirectories is not { Count: > 0 })
            return;

        config.Plugins.Directories = pluginDirectories.ToList();
    }

    private static SessionExtensionOptions BuildExtensionOptions(bool enableCapabilityDiscovery)
        => new()
        {
            Enabled = enableCapabilityDiscovery && NativeBrowserLogic.IsEmbeddedBrowserAvailable
                && CopilotExtensionLaunchProvider.NodePath is not null
        };

    internal static Verbosity CreateResponseVerbosity(ResponseDetailLevel detail) => detail switch
    {
        ResponseDetailLevel.Concise => Verbosity.Low,
        ResponseDetailLevel.Balanced => Verbosity.Medium,
        ResponseDetailLevel.Detailed => Verbosity.High,
        _ => throw new ArgumentOutOfRangeException(nameof(detail), detail, "Unknown response detail.")
    };

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

    /// <summary>Sets the optional properties shared by full chat creation and resume.</summary>
    private static void Populate(
        SessionOptionsBase config,
        string? systemPrompt,
        McpSessionPlan? mcpPlan,
        List<string>? skillDirectories,
        List<CustomAgentConfig>? customAgents,
        List<AIFunction>? tools,
        Func<UserInputRequest, UserInputInvocation, Task<UserInputResponse>>? userInputHandler,
        SessionHooks? hooks,
        string? agentName,
        bool enableCapabilityDiscovery = true)
    {
        config.Inference.ReasoningSummary = DefaultReasoningSummary;

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            config.SystemMessage = new SystemMessageConfig
            {
                Content = systemPrompt,
                Mode = SystemMessageMode.Append
            };

        config.Skills.Enabled = enableCapabilityDiscovery || config.Skills.Provider is not null;
        if (!enableCapabilityDiscovery)
            config.Skills.IncludedBuiltinSkills = [];

        ApplySessionSkillRoots(config, enableCapabilityDiscovery, skillDirectories);

        if (customAgents is { Count: > 0 })
            config.Agents.Definitions = customAgents;

        if (tools is { Count: > 0 })
            config.Tools.Definitions = PreloadLumiTools(tools);

        if (mcpPlan is not null)
        {
            config.Mcp.Servers = mcpPlan.Servers;
            if (mcpPlan.DisabledServerNames.Count > 0)
                config.Mcp.DisabledServers = mcpPlan.DisabledServerNames.ToList();
        }

        if (userInputHandler is not null)
            config.Interaction.OnUserInputRequest = userInputHandler;

        if (hooks is not null)
            config.Hooks.Handlers = hooks;

        if (!string.IsNullOrWhiteSpace(agentName))
            config.Agents.Selected = agentName;
    }

    private static SessionInferenceOptions CreateInferenceOptions(
        string? model,
        string? reasoningEffort,
        string? contextTier,
        ProviderConfig? provider,
        string? providerModelId,
        ModelCapabilitiesOverride? modelCapabilities)
    {
        var inference = new SessionInferenceOptions
        {
            ReasoningEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort,
            ContextTier = CreateContextTier(contextTier),
            Capabilities = provider is null ? modelCapabilities : null
        };
        if (provider is not null)
        {
            inference.Providers = [CreateNamedProvider(provider)];
            inference.Models = [CreateProviderModel(provider, model, providerModelId, modelCapabilities)];
        }

        return inference;
    }

    private static SessionGitHubOptions CreateGitHubOptions(string? model, ProviderConfig? provider)
        => new()
        {
            Capi = provider is null
                ? CopilotModelCatalog.CreateCapiOptions(model)
                : new Microsoft.Copilot.CapiSessionOptions { Enabled = false },
            TelemetryEnabled = false
        };

    private static string? SessionModelId(string? model, ProviderConfig? provider, string? providerModelId)
    {
        if (provider is null)
            return CopilotModelCatalog.GetWireModelId(model);

        return $"{ByokConfigHelper.SessionProviderName}/{GetProviderModelId(provider, model, providerModelId)}";
    }

    private static string GetProviderModelId(ProviderConfig provider, string? model, string? providerModelId)
    {
        var id = string.IsNullOrWhiteSpace(providerModelId)
            ? string.IsNullOrWhiteSpace(provider.ModelId) ? model : provider.ModelId
            : providerModelId;
        return string.IsNullOrWhiteSpace(id) || id.Contains('/') ? "selected" : id;
    }

    private static Microsoft.Copilot.NamedProviderConfig CreateNamedProvider(ProviderConfig provider)
        => new()
        {
            Name = ByokConfigHelper.SessionProviderName,
            Type = provider.Type,
            WireApi = provider.WireApi,
            BaseUrl = provider.BaseUrl,
            ApiKey = provider.ApiKey,
            BearerToken = provider.BearerToken,
            BearerTokenProvider = provider.BearerTokenProvider,
            Azure = provider.Azure,
            Headers = provider.Headers
        };

    private static Microsoft.Copilot.ProviderModelConfig CreateProviderModel(
        ProviderConfig provider,
        string? model,
        string? providerModelId,
        ModelCapabilitiesOverride? capabilities)
        => new()
        {
            Provider = ByokConfigHelper.SessionProviderName,
            Id = GetProviderModelId(provider, model, providerModelId),
            ModelId = provider.ModelId ?? model,
            WireModel = provider.WireModel ?? model,
            Name = model,
            MaxPromptTokens = provider.MaxPromptTokens,
            MaxOutputTokens = provider.MaxOutputTokens,
            Capabilities = capabilities
        };

    private static Microsoft.Copilot.Rpc.ProviderModelConfig CreateProviderModelRpc(
        ProviderConfig provider,
        string? model,
        string? providerModelId,
        ModelCapabilitiesOverride? capabilities)
    {
        var definition = CreateProviderModel(provider, model, providerModelId, capabilities);
        return new Microsoft.Copilot.Rpc.ProviderModelConfig
        {
            Provider = definition.Provider,
            Id = definition.Id,
            ModelId = definition.ModelId,
            WireModel = definition.WireModel,
            Name = definition.Name,
            MaxPromptTokens = definition.MaxPromptTokens,
            MaxOutputTokens = definition.MaxOutputTokens,
            Capabilities = definition.Capabilities
        };
    }
}
