using System;
using System.Collections.Generic;
using System.Linq;
using GitHub.Copilot;
using Lumi.Models;

namespace Lumi.Services;

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

    public static Dictionary<string, McpServerConfig> Build(
        AppData data,
        string workDir,
        ProjectContextCatalogSnapshot projectContextCatalog,
        Chat chat,
        IReadOnlyCollection<string>? currentActiveServerNames,
        LumiAgent? activeAgent,
        McpProxyRuntime? proxyRuntime = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(projectContextCatalog);
        ArgumentNullException.ThrowIfNull(chat);

        var selectedNames = ResolveSelectedNames(data, projectContextCatalog, chat, currentActiveServerNames);

        var configuredServers = data.McpServers
            .Where(server => server.IsEnabled)
            .Where(server => selectedNames.Contains(server.Name))
            .ToList();

        if (activeAgent is { McpServerIds.Count: > 0 })
        {
            var allowedIds = activeAgent.McpServerIds.ToHashSet();
            configuredServers = configuredServers.Where(server => allowedIds.Contains(server.Id)).ToList();
        }

        // Phase 1: select servers keyed by their raw name so the original precedence is preserved —
        // a configured server wins over an identically named project-context server, and duplicate
        // names collapse to a single entry (configured: last wins; context: first wins).
        var selected = new Dictionary<string, McpServerConfig>(NameComparer);
        var order = new List<string>();

        foreach (var server in configuredServers)
        {
            if (!selected.ContainsKey(server.Name))
                order.Add(server.Name);
            selected[server.Name] = ToSdkConfig(server, workDir, proxyRuntime);
        }

        foreach (var contextServer in projectContextCatalog.McpServers)
        {
            if (selectedNames.Contains(contextServer.Name) && !selected.ContainsKey(contextServer.Name))
            {
                order.Add(contextServer.Name);
                selected[contextServer.Name] = CloneContextConfig(contextServer, proxyRuntime);
            }
        }

        // Phase 2: project each distinct server onto a CAPI-safe, collision-free namespace. The
        // dictionary key is sent to the backend as the tool namespace and must match ^[a-zA-Z0-9_-]+$.
        var result = new Dictionary<string, McpServerConfig>(NameComparer);
        foreach (var rawName in order)
            result[ToNamespace(rawName, result)] = selected[rawName];

        GitHubMcpWebSearchBootstrap.Ensure(result, CopilotService.TryGetGitHubTokenForMcp());
        return result;
    }

    private static HashSet<string> ResolveSelectedNames(
        AppData data,
        ProjectContextCatalogSnapshot projectContextCatalog,
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

        foreach (var server in projectContextCatalog.McpServers)
            names.Add(server.Name);

        return names;
    }

    private static McpServerConfig ToSdkConfig(McpServer server, string workDir, McpProxyRuntime? proxyRuntime)
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
                $"lumi:{server.Id}",
                server.Name,
                local));
        }

        return local;
    }

    private static McpServerConfig CloneContextConfig(ProjectContextMcpServerDefinition contextServer, McpProxyRuntime? proxyRuntime)
    {
        switch (contextServer.Config)
        {
            case McpStdioServerConfig local:
            {
                var clone = new McpStdioServerConfig
                {
                    Command = local.Command,
                    Args = local.Args?.ToList() ?? new List<string>(),
                    WorkingDirectory = string.IsNullOrWhiteSpace(local.WorkingDirectory) ? contextServer.SourceDirectory : local.WorkingDirectory,
                    Tools = NormalizeTools(local.Tools),
                    Timeout = local.Timeout
                };

                if (local.Env is not null)
                    clone.Env = new Dictionary<string, string>(local.Env, StringComparer.OrdinalIgnoreCase);

                if (proxyRuntime is not null)
                {
                    return proxyRuntime.Register(new McpProxyServerDefinition(
                        $"project:{contextServer.SourcePath}:{contextServer.Name}",
                        contextServer.Name,
                        clone));
                }

                return clone;
            }
            case McpHttpServerConfig remote:
            {
                var clone = new McpHttpServerConfig
                {
                    Url = remote.Url,
                    Tools = NormalizeTools(remote.Tools),
                    Timeout = remote.Timeout
                };

                if (remote.Headers is not null)
                    clone.Headers = new Dictionary<string, string>(remote.Headers, StringComparer.OrdinalIgnoreCase);

                return clone;
            }
            default:
                return contextServer.Config;
        }
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
