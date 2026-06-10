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

    [Fact]
    public async Task Build_RunIsolatedLocalServer_BypassesProxyEvenWhenRuntimePresent()
    {
        await using var proxyRuntime = new McpProxyRuntime();
        var isolated = new McpServer
        {
            Name = "isolated",
            Command = "node",
            Args = ["server.js"],
            RunIsolated = true
        };
        var shared = new McpServer
        {
            Name = "shared",
            Command = "node",
            Args = ["server.js"]
        };
        var data = new AppData { McpServers = [isolated, shared] };
        var chat = new Chat { ActiveMcpServerNames = ["isolated", "shared"] };

        var servers = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, null, proxyRuntime);

        // The isolated server runs natively (stdio); the shared one is routed through the proxy.
        Assert.IsType<McpStdioServerConfig>(servers["isolated"]);
        var proxied = Assert.IsType<McpHttpServerConfig>(servers["shared"]);
        Assert.StartsWith("http://127.0.0.1:", proxied.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Build_SameServerAndWorkingDirectory_ReusesSameProxyRoute()
    {
        await using var proxyRuntime = new McpProxyRuntime();
        var server = new McpServer { Name = "filesystem", Command = "node", Args = ["server.js"] };
        var data = new AppData { McpServers = [server] };
        var chat = new Chat { ActiveMcpServerNames = ["filesystem"] };

        var first = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, null, proxyRuntime);
        var second = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, null, proxyRuntime);

        var firstUrl = Assert.IsType<McpHttpServerConfig>(first["filesystem"]).Url;
        var secondUrl = Assert.IsType<McpHttpServerConfig>(second["filesystem"]).Url;
        Assert.Equal(firstUrl, secondUrl);
    }

    [Fact]
    public async Task Build_SameServerDifferentWorkingDirectory_CreatesDistinctProxyRoutes()
    {
        await using var proxyRuntime = new McpProxyRuntime();
        var server = new McpServer { Name = "filesystem", Command = "node", Args = ["server.js"] };
        var data = new AppData { McpServers = [server] };
        var chat = new Chat { ActiveMcpServerNames = ["filesystem"] };

        var inRepoA = McpSessionPlanner.Build(data, "C:\\repoA", EmptyCatalog(), chat, null, null, proxyRuntime);
        var inRepoB = McpSessionPlanner.Build(data, "C:\\repoB", EmptyCatalog(), chat, null, null, proxyRuntime);

        var urlA = Assert.IsType<McpHttpServerConfig>(inRepoA["filesystem"]).Url;
        var urlB = Assert.IsType<McpHttpServerConfig>(inRepoB["filesystem"]).Url;
        // Different effective working directories must not share one upstream process,
        // otherwise a chat rooted in repoB would inherit repoA's cwd.
        Assert.NotEqual(urlA, urlB);
    }

    [Fact]
    public async Task Build_WorkingDirectoryTrailingSlashOrCasing_ReusesSameProxyRoute()
    {
        // Path normalization is Windows-specific (casing + separator handling).
        if (!OperatingSystem.IsWindows())
            return;

        await using var proxyRuntime = new McpProxyRuntime();
        var server = new McpServer { Name = "filesystem", Command = "node", Args = ["server.js"] };
        var data = new AppData { McpServers = [server] };
        var chat = new Chat { ActiveMcpServerNames = ["filesystem"] };

        var canonical = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, null, proxyRuntime);
        var trailingSlash = McpSessionPlanner.Build(data, "C:\\repo\\", EmptyCatalog(), chat, null, null, proxyRuntime);
        var differentCasing = McpSessionPlanner.Build(data, "C:\\REPO", EmptyCatalog(), chat, null, null, proxyRuntime);

        var urlCanonical = Assert.IsType<McpHttpServerConfig>(canonical["filesystem"]).Url;
        var urlTrailing = Assert.IsType<McpHttpServerConfig>(trailingSlash["filesystem"]).Url;
        var urlCasing = Assert.IsType<McpHttpServerConfig>(differentCasing["filesystem"]).Url;

        // Equivalent paths must collapse to one shared upstream pool instead of spawning redundant processes.
        Assert.Equal(urlCanonical, urlTrailing);
        Assert.Equal(urlCanonical, urlCasing);
    }

    [Fact]
    public async Task Build_PopulatesRegisteredProxyKeysForProxiedServer()
    {
        await using var proxyRuntime = new McpProxyRuntime();
        var server = new McpServer { Name = "filesystem", Command = "node", Args = ["server.js"] };
        var data = new AppData { McpServers = [server] };
        var chat = new Chat { ActiveMcpServerNames = ["filesystem"] };
        var registeredKeys = new List<string>();

        McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, null, proxyRuntime, registeredKeys);

        var key = Assert.Single(registeredKeys);
        Assert.StartsWith($"lumi:{server.Id}:", key);
    }

    [Fact]
    public async Task Build_RunIsolatedServer_DoesNotRegisterProxyKey()
    {
        await using var proxyRuntime = new McpProxyRuntime();
        var server = new McpServer { Name = "filesystem", Command = "node", Args = ["server.js"], RunIsolated = true };
        var data = new AppData { McpServers = [server] };
        var chat = new Chat { ActiveMcpServerNames = ["filesystem"] };
        var registeredKeys = new List<string>();

        var servers = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, null, proxyRuntime, registeredKeys);

        // Isolated servers bypass the proxy, so they must not contribute a shared route key.
        Assert.Empty(registeredKeys);
        Assert.IsType<McpStdioServerConfig>(servers["filesystem"]);
    }

    private static ProjectContextCatalogSnapshot EmptyCatalog()
        => new([], [], []);
}
