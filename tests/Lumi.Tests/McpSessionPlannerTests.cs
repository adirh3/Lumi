using System;
using System.Collections.Generic;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class McpSessionPlannerTests
{
    [Fact]
    public void Build_ReturnsLocalAndRemoteServersAsSdkConfigs()
    {
        var local = new McpServer
        {
            Name = "filesystem",
            Command = "node",
            Args = ["server.js"],
            Tools = ["read_file"]
        };
        var remote = new McpServer
        {
            Name = "jira",
            ServerType = "remote",
            Url = "https://example.test/mcp",
            Tools = ["search_issues"]
        };
        var data = new AppData
        {
            McpServers = [local, remote]
        };
        var chat = new Chat
        {
            ActiveMcpServerNames = ["filesystem", "jira"]
        };

        var servers = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, null);

        Assert.IsType<McpStdioServerConfig>(servers["filesystem"]);
        Assert.IsType<McpHttpServerConfig>(servers["jira"]);
    }

    [Fact]
    public async Task Build_WithProxyRuntime_RoutesLocalServersThroughRemoteProxy()
    {
        await using var proxyRuntime = new McpProxyRuntime();
        var local = new McpServer
        {
            Name = "filesystem",
            Command = "node",
            Args = ["server.js"],
            Tools = ["read_file"]
        };
        var remote = new McpServer
        {
            Name = "jira",
            ServerType = "remote",
            Url = "https://example.test/mcp",
            Tools = ["search_issues"]
        };
        var data = new AppData
        {
            McpServers = [local, remote]
        };
        var chat = new Chat
        {
            ActiveMcpServerNames = ["filesystem", "jira"]
        };

        var servers = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, null, proxyRuntime);

        var proxiedLocal = Assert.IsType<McpHttpServerConfig>(servers["filesystem"]);
        Assert.StartsWith("http://127.0.0.1:", proxiedLocal.Url, StringComparison.Ordinal);
        Assert.Equal(["read_file"], proxiedLocal.Tools);
        var nativeRemote = Assert.IsType<McpHttpServerConfig>(servers["jira"]);
        Assert.Equal("https://example.test/mcp", nativeRemote.Url);
    }

    [Fact]
    public void Build_UsesCurrentSessionSelectionInsteadOfPersistedChatSelection()
    {
        var data = new AppData
        {
            McpServers =
            [
                new McpServer { Name = "enabled-now", Command = "node", Args = ["a.js"] },
                new McpServer { Name = "persisted-only", Command = "node", Args = ["b.js"] }
            ]
        };
        var chat = new Chat
        {
            ActiveMcpServerNames = ["persisted-only"]
        };

        var servers = McpSessionPlanner.Build(
            data,
            "C:\\repo",
            EmptyCatalog(),
            chat,
            ["enabled-now"],
            null);

        Assert.True(servers.ContainsKey("enabled-now"));
        Assert.False(servers.ContainsKey("persisted-only"));
    }

    [Fact]
    public void Build_EmptyCurrentSelectionDisablesUserSelectableMcpServers()
    {
        var data = new AppData
        {
            McpServers = [new McpServer { Name = "filesystem", Command = "node", Args = ["server.js"] }]
        };
        var chat = new Chat
        {
            ActiveMcpServerNames = ["filesystem"]
        };

        var servers = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, [], null);

        Assert.False(servers.ContainsKey("filesystem"));
    }

    [Fact]
    public void Build_ExplicitEmptyPersistedSelectionDisablesUserSelectableMcpServers()
    {
        var data = new AppData
        {
            McpServers = [new McpServer { Name = "filesystem", Command = "node", Args = ["server.js"] }]
        };
        var chat = new Chat
        {
            ActiveMcpServerNames = [],
            HasExplicitMcpServerSelection = true
        };

        var servers = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, null);

        Assert.False(servers.ContainsKey("filesystem"));
    }

    [Fact]
    public void Build_LegacyEmptySelectionDefaultsToEnabledMcpServers()
    {
        var data = new AppData
        {
            McpServers = [new McpServer { Name = "filesystem", Command = "node", Args = ["server.js"] }]
        };
        var chat = new Chat
        {
            ActiveMcpServerNames = [],
            HasExplicitMcpServerSelection = false
        };

        var servers = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, null);

        Assert.True(servers.ContainsKey("filesystem"));
    }

    [Fact]
    public void Build_AppliesAgentMcpRestrictionsAsIntersection()
    {
        var allowed = new McpServer { Name = "allowed", Command = "node", Args = ["allowed.js"] };
        var blocked = new McpServer { Name = "blocked", Command = "node", Args = ["blocked.js"] };
        var data = new AppData
        {
            McpServers = [allowed, blocked]
        };
        var chat = new Chat
        {
            ActiveMcpServerNames = ["allowed", "blocked"]
        };
        var agent = new LumiAgent
        {
            McpServerIds = [allowed.Id]
        };

        var servers = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, agent);

        Assert.True(servers.ContainsKey("allowed"));
        Assert.False(servers.ContainsKey("blocked"));
    }

    [Fact]
    public void Build_AddsSelectedProjectContextStdioServers()
    {
        var catalog = new ProjectContextCatalogSnapshot(
            [],
            [],
            [
                new ProjectContextMcpServerDefinition(
                    "workspace-files",
                    new McpStdioServerConfig
                    {
                        Command = "node",
                        Args = ["workspace.js"],
                        WorkingDirectory = "C:\\repo\\.github"
                    },
                    "C:\\repo\\.vscode\\mcp.json",
                    "C:\\repo\\.vscode")
            ]);
        var chat = new Chat
        {
            ActiveMcpServerNames = ["workspace-files"]
        };

        var servers = McpSessionPlanner.Build(new AppData(), "C:\\repo", catalog, chat, null, null);

        var local = Assert.IsType<McpStdioServerConfig>(servers["workspace-files"]);
        Assert.Equal("node", local.Command);
        Assert.Equal("C:\\repo\\.github", local.WorkingDirectory);
    }

    [Fact]
    public void Build_DoesNotAddDeselectedProjectContextServers()
    {
        var catalog = new ProjectContextCatalogSnapshot(
            [],
            [],
            [
                new ProjectContextMcpServerDefinition(
                    "workspace-files",
                    new McpStdioServerConfig { Command = "node", Args = ["workspace.js"] },
                    "C:\\repo\\.vscode\\mcp.json",
                    "C:\\repo\\.vscode")
            ]);
        var chat = new Chat
        {
            ActiveMcpServerNames = ["other-server"]
        };

        var servers = McpSessionPlanner.Build(new AppData(), "C:\\repo", catalog, chat, null, null);

        Assert.False(servers.ContainsKey("workspace-files"));
    }

    private static ProjectContextCatalogSnapshot EmptyCatalog()
        => new([], [], []);
}
