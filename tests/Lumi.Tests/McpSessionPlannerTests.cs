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
    public async Task SelectProxyRuntime_ReturnsNull_WhenSettingDisabledByDefault()
    {
        var settings = new UserSettings();
        await using var shared = new McpProxyRuntime();

        Assert.False(settings.UseMcpProxy);
        Assert.Null(McpSessionPlanner.SelectProxyRuntime(settings, shared));
    }

    [Fact]
    public async Task SelectProxyRuntime_ReturnsSharedRuntime_WhenSettingEnabled()
    {
        var settings = new UserSettings { UseMcpProxy = true };
        await using var shared = new McpProxyRuntime();

        Assert.Same(shared, McpSessionPlanner.SelectProxyRuntime(settings, shared));
    }

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

    [Fact]
    public void Build_SanitizesNamespacesWithInvalidCharactersForCapi()
    {
        var server = new McpServer
        {
            Name = "Avalonia MCP",
            Command = "dotnet",
            Args = ["avalonia-mcp"]
        };
        var data = new AppData { McpServers = [server] };
        var chat = new Chat { ActiveMcpServerNames = ["Avalonia MCP"] };

        var servers = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, null);

        Assert.All(servers.Keys, key => Assert.Matches("^[a-zA-Z0-9_-]+$", key));
        Assert.True(servers.ContainsKey("Avalonia_MCP"));
        Assert.False(servers.ContainsKey("Avalonia MCP"));
    }

    [Fact]
    public void Build_DeduplicatesNamespacesThatCollideAfterSanitizing()
    {
        var data = new AppData
        {
            McpServers =
            [
                new McpServer { Name = "Avalonia MCP", Command = "dotnet" },
                new McpServer { Name = "Avalonia/MCP", Command = "dotnet" }
            ]
        };
        var chat = new Chat { ActiveMcpServerNames = ["Avalonia MCP", "Avalonia/MCP"] };

        var servers = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, null);

        Assert.All(servers.Keys, key => Assert.Matches("^[a-zA-Z0-9_-]+$", key));
        Assert.True(servers.ContainsKey("Avalonia_MCP"));
        Assert.True(servers.ContainsKey("Avalonia_MCP_2"));
    }

    [Fact]
    public void Build_DoesNotDuplicateWhenConfiguredAndContextServerShareName()
    {
        var data = new AppData
        {
            McpServers = [new McpServer { Name = "shared", Command = "node", Args = ["configured.js"] }]
        };
        var catalog = new ProjectContextCatalogSnapshot(
            [],
            [],
            [
                new ProjectContextMcpServerDefinition(
                    "shared",
                    new McpStdioServerConfig { Command = "node", Args = ["context.js"] },
                    "C:\\repo\\.vscode\\mcp.json",
                    "C:\\repo\\.vscode")
            ]);
        var chat = new Chat { ActiveMcpServerNames = ["shared"] };

        var servers = McpSessionPlanner.Build(data, "C:\\repo", catalog, chat, null, null);

        Assert.True(servers.ContainsKey("shared"));
        Assert.False(servers.ContainsKey("shared_2"));
        // The configured server wins over the identically named project-context server.
        var local = Assert.IsType<McpStdioServerConfig>(servers["shared"]);
        Assert.Equal(["configured.js"], local.Args);
    }

    [Fact]
    public void Build_PreservesValidLeadingAndTrailingNamespaceCharacters()
    {
        // Leading/trailing '_' and '-' are valid per ^[a-zA-Z0-9_-]+$, so they must be preserved.
        // Trimming them could collide a user server with a reserved namespace (e.g. github-mcp-server)
        // and suppress built-in tools.
        var data = new AppData
        {
            McpServers = [new McpServer { Name = "_keep-this-", Command = "node" }]
        };
        var chat = new Chat { ActiveMcpServerNames = ["_keep-this-"] };

        var servers = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, null);

        Assert.All(servers.Keys, key => Assert.Matches("^[a-zA-Z0-9_-]+$", key));
        Assert.True(servers.ContainsKey("_keep-this-"));
    }

    private static ProjectContextCatalogSnapshot EmptyCatalog()
        => new([], [], []);
}
