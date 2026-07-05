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
    public bool Streaming { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? ConfigDir { get; init; }
    public List<AIFunction>? Tools { get; init; }
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
        List<string>? skillDirectories,
        List<CustomAgentConfig>? customAgents,
        List<AIFunction>? tools,
        Dictionary<string, McpServerConfig>? mcpServers,
        string? reasoningEffort,
        Func<UserInputRequest, UserInputInvocation, Task<UserInputResponse>>? userInputHandler,
        Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>>? onPermission,
        SessionHooks? hooks,
        string? agentName = null,
        string? contextTier = null,
        GitHub.Copilot.ProviderConfig? provider = null)
    {
        var config = new SessionConfig
        {
            ClientName = ClientName,
            Model = model,
            Streaming = true,
            WorkingDirectory = workingDirectory,
            ConfigDirectory = GetDefaultConfigDir(),
            EnableConfigDiscovery = EnableSdkConfigDiscovery,
            EnableSessionStore = true,
            ExcludedTools = ExcludedBuiltInTools(),
            InfiniteSessions = new InfiniteSessionConfig { Enabled = true },
            OnPermissionRequest = onPermission ?? PermissionHandler.ApproveAll,
            ContextTier = CreateContextTier(contextTier),
            McpOAuthTokenStorage = McpOAuthTokenStorage,
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
        Func<UserInputRequest, UserInputInvocation, Task<UserInputResponse>>? userInputHandler,
        Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>>? onPermission,
        SessionHooks? hooks,
        string? agentName = null,
        string? contextTier = null,
        GitHub.Copilot.ProviderConfig? provider = null)
    {
        var config = new ResumeSessionConfig
        {
            ClientName = ClientName,
            Model = model,
            Streaming = true,
            WorkingDirectory = workingDirectory,
            ConfigDirectory = GetDefaultConfigDir(),
            EnableConfigDiscovery = EnableSdkConfigDiscovery,
            EnableSessionStore = true,
            ExcludedTools = ExcludedBuiltInTools(),
            InfiniteSessions = new InfiniteSessionConfig { Enabled = true },
            OnPermissionRequest = onPermission ?? PermissionHandler.ApproveAll,
            ContextTier = CreateContextTier(contextTier),
            McpOAuthTokenStorage = McpOAuthTokenStorage,
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
            ConfigDirectory = options.ConfigDir ?? GetDefaultConfigDir(),
            EnableConfigDiscovery = EnableSdkConfigDiscovery,
            EnableSessionStore = false,
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
            config.Tools = options.Tools.Cast<AIFunctionDeclaration>().ToList();
            config.AvailableTools = options.Tools.Select(t => t.Name).ToList();
        }
        else
        {
            config.AvailableTools = [];
            config.ExcludedTools = ExcludeAllToolSources();
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

    private static ToolSet ExcludeAllToolSources()
        => new ToolSet()
            .AddBuiltIn("*")
            .AddMcp("*")
            .AddCustom("*");

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

    /// <summary>
    /// Builds the system message for a chat session. GPT-family models receive a terse
    /// "Tone" section from the Copilot CLI base prompt that fights Lumi's writing style: it
    /// pushes 1-2 paragraph answers, discourages sections and lists, and prefers tables over
    /// structure, which suppresses rich formatting and Lumi's visualization blocks. Using the
    /// SDK's section-override mechanism we replace only that one section with a Lumi-aligned
    /// tone, leaving every other base-prompt section (identity, tool rules, safety, code-change
    /// rules, additional instructions, etc.) untouched. Other model families already produce
    /// richly structured output, so they get the prompt appended unchanged.
    /// </summary>
    private static SystemMessageConfig BuildSystemMessage(string systemPrompt, string? model)
    {
        if (!IsGptFamily(model))
            return new SystemMessageConfig { Content = systemPrompt, Mode = SystemMessageMode.Append };

        return new SystemMessageConfig
        {
            Mode = SystemMessageMode.Customize,
            Content = systemPrompt,
            Sections = new Dictionary<SystemMessageSection, SectionOverride>
            {
                [SystemMessageSection.Tone] = new SectionOverride
                {
                    Action = SectionOverrideAction.Replace,
                    Content = LumiToneOverride
                }
            }
        };
    }

    /// <summary>
    /// True for OpenAI GPT-family model ids (e.g. "gpt-5.5"), matching the CLI's own family
    /// routing which keys its formatting/tone defaults off the "gpt" prefix.
    /// </summary>
    private static bool IsGptFamily(string? model)
        => !string.IsNullOrWhiteSpace(model)
           && model.Trim().StartsWith("gpt", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lumi's replacement for the GPT-family "Tone" section. Keeps the genuinely useful,
    /// model-agnostic guidance from the default tone (lead with the answer, stay concise, be
    /// honest about uncertainty, warm natural voice) while removing the length and structure
    /// suppression and explicitly permitting rich structure and proactive use of Lumi's
    /// visualization blocks. This reinforces, rather than contradicts, the writing guidance
    /// already carried in Lumi's appended system prompt.
    /// </summary>
    private const string LumiToneOverride =
        """
        * Lead with the answer. Start with the main result or conclusion, then add the most important supporting detail.
        * Be concise and information-dense: cut filler and don't simply restate the request. Concise means high signal, not artificially short, so never drop a genuinely useful answer just to hit a length target.
        * Match the shape of the response to the request. Use the full Markdown palette (headings, subheadings, tables, lists, code blocks, and inline emphasis) plus Lumi's native visualization blocks (chart, comparison, card, confidence, mermaid) whenever they make the answer clearer or easier to scan. Reach for them proactively, not only when asked.
        * Don't force answers into one or two paragraphs. Give comparisons, multi-part answers, and rich explanations the structure they deserve, whether that's sections, a table, or the right visualization.
        * Be honest and direct: if something is uncertain, incomplete, or blocked, say so plainly.
        * Keep the voice warm, collaborative, and natural, and leave a blank line between paragraphs.
        """;

    /// <summary>Sets the shared optional properties on a <see cref="SessionConfig"/>.</summary>
    private static void Populate(
        SessionConfig config,
        string? systemPrompt,
        string? reasoningEffort,
        List<string>? skillDirectories,
        List<CustomAgentConfig>? customAgents,
        List<AIFunction>? tools,
        Dictionary<string, McpServerConfig>? mcpServers,
        Func<UserInputRequest, UserInputInvocation, Task<UserInputResponse>>? userInputHandler,
        SessionHooks? hooks,
        string? agentName)
    {
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
            config.ReasoningEffort = reasoningEffort;

        config.ReasoningSummary = DefaultReasoningSummary;

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            config.SystemMessage = BuildSystemMessage(systemPrompt, config.Model);

        if (skillDirectories is { Count: > 0 })
            config.SkillDirectories = skillDirectories;

        if (customAgents is { Count: > 0 })
            config.CustomAgents = customAgents;

        if (tools is { Count: > 0 })
            config.Tools = tools.Cast<AIFunctionDeclaration>().ToList();

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
        Func<UserInputRequest, UserInputInvocation, Task<UserInputResponse>>? userInputHandler,
        SessionHooks? hooks,
        string? agentName)
    {
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
            config.ReasoningEffort = reasoningEffort;

        config.ReasoningSummary = DefaultReasoningSummary;

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            config.SystemMessage = BuildSystemMessage(systemPrompt, config.Model);

        if (skillDirectories is { Count: > 0 })
            config.SkillDirectories = skillDirectories;

        if (customAgents is { Count: > 0 })
            config.CustomAgents = customAgents;

        if (tools is { Count: > 0 })
            config.Tools = tools.Cast<AIFunctionDeclaration>().ToList();

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
