using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GitHub.Copilot;
using Lumi.Models;

namespace Lumi.Services;

public static class McpSessionPlanner
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    public static Dictionary<string, McpServerConfig> Build(
        AppData data,
        string workDir,
        ProjectContextCatalogSnapshot projectContextCatalog,
        Chat chat,
        IReadOnlyCollection<string>? currentActiveServerNames,
        LumiAgent? activeAgent,
        McpProxyRuntime? proxyRuntime = null,
        ICollection<string>? registeredProxyKeys = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(projectContextCatalog);
        ArgumentNullException.ThrowIfNull(chat);

        var selectedNames = ResolveSelectedNames(data, projectContextCatalog, chat, currentActiveServerNames);
        var result = new Dictionary<string, McpServerConfig>(NameComparer);

        var configuredServers = data.McpServers
            .Where(server => server.IsEnabled)
            .Where(server => selectedNames.Contains(server.Name))
            .ToList();

        if (activeAgent is { McpServerIds.Count: > 0 })
        {
            var allowedIds = activeAgent.McpServerIds.ToHashSet();
            configuredServers = configuredServers.Where(server => allowedIds.Contains(server.Id)).ToList();
        }

        foreach (var server in configuredServers)
            result[server.Name] = ToSdkConfig(server, workDir, proxyRuntime, registeredProxyKeys);

        foreach (var contextServer in projectContextCatalog.McpServers)
        {
            if (selectedNames.Contains(contextServer.Name) && !result.ContainsKey(contextServer.Name))
                result[contextServer.Name] = CloneContextConfig(contextServer, proxyRuntime, registeredProxyKeys);
        }

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

    private static McpServerConfig ToSdkConfig(McpServer server, string workDir, McpProxyRuntime? proxyRuntime, ICollection<string>? registeredProxyKeys = null)
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

        // Escape hatch: a server that needs interactive server→client features
        // (sampling / elicitation / roots) opts out of the shared proxy and runs natively
        // per session, where the CLI and Lumi's handlers can serve those requests.
        if (server.RunIsolated || proxyRuntime is null)
            return local;

        // The route key encodes the working directory + environment so that two chats using
        // the same logical server with different effective directories get independent shared
        // pools instead of fighting over one process whose cwd is "last registered wins".
        var key = $"lumi:{server.Id}:{ContextToken(local.WorkingDirectory, local.Env)}";
        registeredProxyKeys?.Add(key);
        return proxyRuntime.Register(new McpProxyServerDefinition(key, server.Name, local));
    }

    private static McpServerConfig CloneContextConfig(ProjectContextMcpServerDefinition contextServer, McpProxyRuntime? proxyRuntime, ICollection<string>? registeredProxyKeys = null)
    {
        switch (contextServer.Config)
        {
            case McpStdioServerConfig local:
            {
                var clone = new McpStdioServerConfig
                {
                    Command = local.Command,
                    Args = local.Args.ToList(),
                    WorkingDirectory = string.IsNullOrWhiteSpace(local.WorkingDirectory) ? contextServer.SourceDirectory : local.WorkingDirectory,
                    Tools = NormalizeTools(local.Tools),
                    Timeout = local.Timeout
                };

                if (local.Env is not null)
                    clone.Env = new Dictionary<string, string>(local.Env, StringComparer.OrdinalIgnoreCase);

                if (proxyRuntime is not null)
                {
                    var key = $"project:{contextServer.SourcePath}:{contextServer.Name}";
                    registeredProxyKeys?.Add(key);
                    return proxyRuntime.Register(new McpProxyServerDefinition(key, contextServer.Name, clone));
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
    /// Stable short hash of the per-context dimensions (working directory + environment) used
    /// to scope a proxy route to a specific execution context. The working directory is
    /// canonicalised first so that equivalent paths (case / trailing separators) collapse to a
    /// single shared pool rather than spawning a redundant upstream process.
    /// </summary>
    private static string ContextToken(string? workingDirectory, IDictionary<string, string>? env)
    {
        var builder = new StringBuilder();
        builder.Append("cwd:").AppendLine(NormalizeWorkingDirectory(workingDirectory));
        if (env is { Count: > 0 })
        {
            foreach (var pair in env.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                builder.Append("env:").Append(pair.Key).Append('=').AppendLine(pair.Value);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    private static string NormalizeWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return "";

        var normalized = workingDirectory.Trim();
        try
        {
            normalized = Path.GetFullPath(normalized);
        }
        catch
        {
            // Leave the raw value if it can't be canonicalised (e.g. invalid path chars).
        }

        normalized = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? normalized.ToLowerInvariant() : normalized;
    }
}
