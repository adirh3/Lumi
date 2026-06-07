using System;
using System.Collections.Generic;
using System.Linq;
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
        McpProxyRuntime? proxyRuntime = null)
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
            result[server.Name] = ToSdkConfig(server, workDir, proxyRuntime);

        foreach (var contextServer in projectContextCatalog.McpServers)
        {
            if (selectedNames.Contains(contextServer.Name) && !result.ContainsKey(contextServer.Name))
                result[contextServer.Name] = CloneContextConfig(contextServer, proxyRuntime);
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
                    Args = local.Args.ToList(),
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
}
