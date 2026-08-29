using System;
using System.Collections.Generic;
using System.Linq;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services.Capabilities;

namespace Lumi.Services;

/// <summary>
/// The MCP half of a session: the servers Lumi configures itself, plus the names of
/// runtime-discovered servers that must stay switched off for this chat.
/// </summary>
/// <param name="Servers">Lumi-owned servers passed to the session as explicit configuration.</param>
/// <param name="DisabledServerNames">
/// Servers the Copilot runtime discovered (workspace config, user profile, plugins) that the user
/// has not selected. Lumi no longer parses those configs, so deselection is expressed by disabling
/// them on the session rather than by omitting them.
/// </param>
/// <param name="Servers">Servers Lumi supplies to the session, keyed by CAPI-safe namespace.</param>
/// <param name="DisabledServerNames">Discovered servers the session must not start.</param>
/// <param name="RuntimeKeysByName">
/// Maps a Lumi server's display name to the namespace the session actually registered it under.
/// Live enable/disable calls must use that key: the display name may contain characters the
/// namespace cannot, so calling with the raw name silently targets a server that does not exist.
/// </param>
public sealed record McpSessionPlan(
    Dictionary<string, McpServerConfig> Servers,
    IReadOnlyList<string> DisabledServerNames,
    IReadOnlyDictionary<string, string>? RuntimeKeysByName = null)
{
    public static McpSessionPlan Empty { get; } = new([], []);

    /// <summary>
    /// The name the running session knows a server by. Discovered servers are registered by the
    /// runtime under their own name, so they pass through unchanged.
    /// </summary>
    public string ResolveRuntimeKey(string serverName)
        => RuntimeKeysByName is not null && RuntimeKeysByName.TryGetValue(serverName, out var key)
            ? key
            : serverName;
}

public static class McpSessionPlanner
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Chooses the MCP proxy runtime for a session. Returns the shared proxy when the user
    /// enabled fast MCP initialization, otherwise null so MCP servers are passed directly to
    /// Copilot and initialized per session.
    /// </summary>
    public static McpProxyRuntime? SelectProxyRuntime(UserSettings settings, McpProxyRuntime sharedRuntime)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sharedRuntime);
        return settings.UseMcpProxy ? sharedRuntime : null;
    }

    public static McpSessionPlan Build(
        AppData data,
        string workDir,
        CapabilitySnapshot capabilities,
        Chat chat,
        IReadOnlyCollection<string>? currentActiveServerNames,
        LumiAgent? activeAgent,
        McpProxyRuntime? proxyRuntime = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(chat);

        var selectedNames = ResolveSelectedNames(data, capabilities, chat, currentActiveServerNames);

        var configuredServers = data.McpServers
            .Where(server => server.IsEnabled)
            .Where(server => selectedNames.Contains(server.Name))
            .ToList();

        var agentRestrictsMcp = activeAgent is { McpServerIds.Count: > 0 };

        if (agentRestrictsMcp)
        {
            var allowedIds = activeAgent!.McpServerIds.ToHashSet();
            configuredServers = configuredServers.Where(server => allowedIds.Contains(server.Id)).ToList();
        }
        else
        {
            // Servers the runtime does not own must be supplied as configuration or they never
            // start. A Lumi server of the same name already won the merge, so this only adds ones
            // nothing else provides. Skipped entirely when an agent restricts MCP access by Lumi id,
            // since a workspace server has no id to appear in that allowlist.
            configuredServers.AddRange(capabilities.McpServers
                .Where(capability => capability is { IsEnabled: true, McpDefinition: not null })
                .Where(capability => selectedNames.Contains(capability.Name))
                .Where(capability => !configuredServers.Any(
                    existing => NameComparer.Equals(existing.Name, capability.Name)))
                .Select(capability => capability.McpDefinition!));
        }

        // Phase 1: select servers keyed by their raw name so the original precedence is preserved —
        // duplicate names collapse to a single entry with the last configured server winning.
        var selected = new Dictionary<string, McpServerConfig>(NameComparer);
        var order = new List<string>();

        foreach (var server in configuredServers)
        {
            if (!selected.ContainsKey(server.Name))
                order.Add(server.Name);
            selected[server.Name] = ToSdkConfig(
                server,
                ResolveWorkingDirectory(server, capabilities, workDir),
                proxyRuntime,
                ResolveProxyKey(server, capabilities));
        }

        // Phase 2: project each distinct server onto a CAPI-safe, collision-free namespace. The
        // dictionary key is sent to the backend as the tool namespace and must match ^[a-zA-Z0-9_-]+$.
        var result = new Dictionary<string, McpServerConfig>(NameComparer);
        var runtimeKeysByName = new Dictionary<string, string>(NameComparer);
        foreach (var rawName in order)
        {
            var key = ToNamespace(rawName, result);
            result[key] = selected[rawName];
            runtimeKeysByName[rawName] = key;
        }

        GitHubMcpWebSearchBootstrap.Ensure(result, CopilotService.TryGetGitHubTokenForMcp());

        // Servers the runtime discovered are loaded by the SDK itself, so deselection is expressed
        // by disabling them on the session. A name is disabled when Lumi is not supplying a config
        // for it and either the user did not select it, or Lumi owns that name — otherwise a Lumi
        // server the user turned off would be silently replaced by an identically named discovered
        // one that config discovery starts anyway. An agent that declares its own MCP allowlist
        // disables everything it did not name: that allowlist holds Lumi ids, so a server the
        // runtime discovered can never appear in it and must not be started by config discovery.
        //
        // Only raw catalog names go in here. The namespaced keys are what the session registers a
        // supplied server under, and treating one as a catalog name would exempt a discovered
        // server that happens to be called what another server sanitized to.
        var supplied = new HashSet<string>(selected.Keys, NameComparer);

        var disabled = capabilities.McpServers
            .Where(server => !supplied.Contains(server.Name))
            .Where(server => agentRestrictsMcp
                             || server.Origin.IsLumi
                             || !selectedNames.Contains(server.Name))
            .Select(server => server.Name)
            .Distinct(NameComparer)
            .ToArray();

        return new McpSessionPlan(result, disabled, runtimeKeysByName);
    }

    /// <summary>
    /// The key a proxied server is registered under. Lumi's own servers are keyed by their store id,
    /// which <see cref="McpProxyRuntime"/> also uses to retire registrations that no longer exist.
    /// A server from anywhere else has no store entry and a fresh id on every discovery, so keying
    /// it the same way would register a new route each load and leak the previous one's child
    /// process — and expose it to retirement meant for deleted Lumi servers.
    /// </summary>
    private static string ResolveProxyKey(McpServer server, CapabilitySnapshot capabilities)
    {
        if (capabilities.FindMcpServer(server.Name) is { Origin.IsLumi: false, SourcePath: var source })
            return $"external:{source ?? string.Empty}:{server.Name}";

        return $"lumi:{server.Id}";
    }

    private static string ResolveWorkingDirectory(
        McpServer server,
        CapabilitySnapshot capabilities,
        string fallback)
        => capabilities.FindMcpServer(server.Name) is
            { Origin.IsLumi: false, McpDefinition: not null, SourcePath: { Length: > 0 } source }
                ? source
                : fallback;

    private static HashSet<string> ResolveSelectedNames(
        AppData data,
        CapabilitySnapshot capabilities,
        Chat chat,
        IReadOnlyCollection<string>? currentActiveServerNames)
    {
        if (currentActiveServerNames is not null)
            return currentActiveServerNames.ToHashSet(NameComparer);

        if (chat.HasExplicitMcpServerSelection || chat.ActiveMcpServerNames.Count > 0)
            return chat.ActiveMcpServerNames.ToHashSet(NameComparer);

        var names = data.McpServers
            .Where(server => server.IsEnabled)
            .Select(server => server.Name)
            .ToHashSet(NameComparer);

        foreach (var server in capabilities.McpServers.Where(static server => !server.Origin.IsLumi))
            names.Add(server.Name);

        return names;
    }

    private static McpServerConfig ToSdkConfig(
        McpServer server,
        string workDir,
        McpProxyRuntime? proxyRuntime,
        string proxyKey)
    {
        if (string.Equals(server.ServerType, "remote", StringComparison.OrdinalIgnoreCase))
        {
            var remote = new McpHttpServerConfig
            {
                Url = server.Url,
                Tools = NormalizeTools(server.Tools)
            };

            if (server.Headers.Count > 0)
                remote.Headers = new Dictionary<string, string>(server.Headers, StringComparer.OrdinalIgnoreCase);
            if (server.Timeout.HasValue)
                remote.Timeout = server.Timeout.Value;

            return remote;
        }

        var local = new McpStdioServerConfig
        {
            Command = server.Command,
            Args = server.Args.ToList(),
            WorkingDirectory = workDir,
            Tools = NormalizeTools(server.Tools)
        };

        if (server.Env.Count > 0)
            local.Env = new Dictionary<string, string>(server.Env, StringComparer.OrdinalIgnoreCase);
        if (server.Timeout.HasValue)
            local.Timeout = server.Timeout.Value;

        if (proxyRuntime is not null)
        {
            return proxyRuntime.Register(new McpProxyServerDefinition(
                proxyKey,
                server.Name,
                local));
        }

        return local;
    }

    private static List<string> NormalizeTools(IEnumerable<string>? tools)
    {
        var list = tools?
            .Where(tool => !string.IsNullOrWhiteSpace(tool))
            .ToList() ?? [];
        return list.Count > 0 ? list : ["*"];
    }

    /// <summary>
    /// The dictionary key Lumi passes for each MCP server becomes the tool namespace sent to the
    /// Copilot backend, which requires it to match <c>^[a-zA-Z0-9_-]+$</c>. User-defined names with
    /// spaces or symbols (e.g. "Avalonia MCP") otherwise trip a CAPI 400 that fails every request in
    /// the chat. Sanitize to a safe namespace and de-duplicate so distinct servers never collide.
    /// </summary>
    private static string ToNamespace(string name, IReadOnlyDictionary<string, McpServerConfig> existing)
    {
        var safe = SanitizeNamespace(name);
        if (!existing.ContainsKey(safe))
            return safe;

        for (var i = 2; ; i++)
        {
            var candidate = $"{safe}_{i}";
            if (!existing.ContainsKey(candidate))
                return candidate;
        }
    }

    private static string SanitizeNamespace(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "mcp";

        // Replace only characters outside the backend pattern; leading/trailing '_'/'-' are valid
        // and must be preserved, otherwise a user name could be trimmed into a reserved namespace
        // (e.g. "github-mcp-server") and suppress built-in tools.
        var chars = name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray();
        var safe = new string(chars);
        return safe.Length > 0 ? safe : "mcp";
    }
}
