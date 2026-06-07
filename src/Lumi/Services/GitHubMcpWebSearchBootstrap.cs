using System;
using System.Collections.Generic;
using GitHub.Copilot;

namespace Lumi.Services;

internal static class GitHubMcpWebSearchBootstrap
{
    public const string ServerName = "github-mcp-server";

    private const string CopilotMcpUrl = "https://api.githubcopilot.com/mcp/readonly";
    private const string CopilotMcpUrlPrefix = "https://api.githubcopilot.com/mcp";
    private const string EnterpriseCopilotMcpUrlPrefix = "https://api.enterprise.githubcopilot.com/mcp";
    private const string WebSearchToolName = "web_search";

    public static bool Ensure(Dictionary<string, McpServerConfig> mcpServers, string? gitHubToken = null)
    {
        if (mcpServers.ContainsKey(ServerName) || ContainsGitHubCopilotMcpServer(mcpServers.Values))
            return false;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-MCP-Host"] = "copilot-cli",
            ["X-MCP-Tools"] = WebSearchToolName,
        };

        if (!string.IsNullOrWhiteSpace(gitHubToken))
            headers["Authorization"] = "Bearer " + gitHubToken;

        mcpServers[ServerName] = new McpHttpServerConfig
        {
            Url = CopilotMcpUrl,
            Tools = ["*"],
            Headers = headers
        };

        return true;
    }

    private static bool ContainsGitHubCopilotMcpServer(IEnumerable<McpServerConfig> mcpServers)
    {
        foreach (var server in mcpServers)
        {
            if (server is McpHttpServerConfig remote && IsGitHubCopilotMcpUrl(remote.Url))
                return true;
        }

        return false;
    }

    private static bool IsGitHubCopilotMcpUrl(string? url)
    {
        return url is not null
            && (url.StartsWith(CopilotMcpUrlPrefix, StringComparison.OrdinalIgnoreCase)
                || url.StartsWith(EnterpriseCopilotMcpUrlPrefix, StringComparison.OrdinalIgnoreCase));
    }
}
