using System.Collections.Generic;
using GitHub.Copilot;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class GitHubMcpWebSearchBootstrapTests
{
    [Fact]
    public void Ensure_AddsBuiltInGitHubMcpWebSearchServer()
    {
        var servers = new Dictionary<string, McpServerConfig>();

        var changed = GitHubMcpWebSearchBootstrap.Ensure(servers, "test-token");

        Assert.True(changed);
        var server = Assert.IsType<McpHttpServerConfig>(servers[GitHubMcpWebSearchBootstrap.ServerName]);
        Assert.Equal("http", server.Type);
        Assert.StartsWith("https://api.githubcopilot.com/mcp", server.Url);
        Assert.Equal(["*"], server.Tools);
        Assert.Equal("copilot-cli", server.Headers!["X-MCP-Host"]);
        Assert.Equal("web_search", server.Headers["X-MCP-Tools"]);
        Assert.Equal("Bearer test-token", server.Headers["Authorization"]);
    }

    [Fact]
    public void Ensure_AddsServerWithoutAuthorizationWhenTokenUnavailable()
    {
        var servers = new Dictionary<string, McpServerConfig>();

        var changed = GitHubMcpWebSearchBootstrap.Ensure(servers);

        Assert.True(changed);
        var server = Assert.IsType<McpHttpServerConfig>(servers[GitHubMcpWebSearchBootstrap.ServerName]);
        Assert.False(server.Headers!.ContainsKey("Authorization"));
    }

    [Fact]
    public void Ensure_DoesNotOverrideExistingGitHubMcpServer()
    {
        var existing = new McpHttpServerConfig
        {
            Url = "https://api.githubcopilot.com/mcp/readonly",
            Tools = ["*"]
        };
        var servers = new Dictionary<string, McpServerConfig>
        {
            ["custom-github"] = existing
        };

        var changed = GitHubMcpWebSearchBootstrap.Ensure(servers);

        Assert.False(changed);
        Assert.Single(servers);
        Assert.Same(existing, servers["custom-github"]);
    }
}
