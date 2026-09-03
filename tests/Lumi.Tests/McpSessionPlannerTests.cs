using System;
using System.Collections.Generic;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Capabilities;
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

        using var plan = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, null);
        var servers = plan.Servers;

        Assert.IsType<McpStdioServerConfig>(servers["filesystem"]);
        Assert.IsType<McpHttpServerConfig>(servers["jira"]);
        Assert.Null(plan.DetachProxyLease());
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

        using var plan = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, null, proxyRuntime);
        var servers = plan.Servers;

        var proxiedLocal = Assert.IsType<McpHttpServerConfig>(servers["filesystem"]);
        Assert.StartsWith("http://127.0.0.1:", proxiedLocal.Url, StringComparison.Ordinal);
        Assert.Equal(["read_file"], proxiedLocal.Tools);
        var nativeRemote = Assert.IsType<McpHttpServerConfig>(servers["jira"]);
        Assert.Equal("https://example.test/mcp", nativeRemote.Url);
        using var proxyLease = plan.DetachProxyLease();
        Assert.NotNull(proxyLease);
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
            null).Servers;

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

        var servers = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, [], null).Servers;

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

        var servers = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, null).Servers;

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

        var servers = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, null).Servers;

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

        var servers = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, agent).Servers;

        Assert.True(servers.ContainsKey("allowed"));
        Assert.False(servers.ContainsKey("blocked"));
    }

    [Fact]
    public void Build_LeavesSelectedDiscoveredServersToTheRuntime()
    {
        // Workspace/user/plugin MCP servers are discovered and loaded by the Copilot runtime, so a
        // selected one must neither be re-configured by Lumi nor disabled.
        var capabilities = Capabilities(McpCapability("workspace-files", CapabilityOrigin.Workspace));
        var chat = new Chat
        {
            ActiveMcpServerNames = ["workspace-files"]
        };

        var plan = McpSessionPlanner.Build(new AppData(), "C:\\repo", capabilities, chat, null, null);

        Assert.False(plan.Servers.ContainsKey("workspace-files"));
        Assert.DoesNotContain("workspace-files", plan.DisabledServerNames);
    }

    [Fact]
    public void Build_DisablesDeselectedDiscoveredServers()
    {
        var capabilities = Capabilities(McpCapability("workspace-files", CapabilityOrigin.Workspace));
        var chat = new Chat
        {
            ActiveMcpServerNames = ["other-server"]
        };

        var plan = McpSessionPlanner.Build(new AppData(), "C:\\repo", capabilities, chat, null, null);

        Assert.False(plan.Servers.ContainsKey("workspace-files"));
        Assert.Contains("workspace-files", plan.DisabledServerNames);
    }

    [Fact]
    public void Build_DefaultsToSelectingEveryDiscoveredServer()
    {
        // A chat with no explicit selection offers everything, matching the composer's default.
        var capabilities = Capabilities(
            McpCapability("workspace-files", CapabilityOrigin.Workspace),
            McpCapability("profile-server", CapabilityOrigin.Personal));

        var plan = McpSessionPlanner.Build(new AppData(), "C:\\repo", capabilities, new Chat(), null, null);

        Assert.Empty(plan.DisabledServerNames);
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

        var servers = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, null).Servers;

        Assert.All(servers.Keys, key => Assert.Matches("^[a-zA-Z0-9_-]+$", key));
        Assert.True(servers.ContainsKey("Avalonia_MCP"));
        Assert.False(servers.ContainsKey("Avalonia MCP"));
    }

    [Fact]
    public void Build_AgentMcpRestrictionAlsoDisablesDiscoveredServers()
    {
        // Regression: the agent's allowlist holds Lumi ids, so it could only ever filter Lumi's own
        // servers. Everything the runtime discovered was neither supplied nor disabled — and config
        // discovery starts whatever is not disabled, so the agent's MCP policy was silently void.
        var allowed = new McpServer { Name = "Allowed", Command = "dotnet" };
        var blocked = new McpServer { Name = "Blocked", Command = "dotnet" };
        var data = new AppData { McpServers = [allowed, blocked] };
        var agent = new LumiAgent { Name = "Restricted", McpServerIds = [allowed.Id] };
        var chat = new Chat
        {
            ActiveMcpServerNames = ["Allowed", "Blocked", "discovered"],
            HasExplicitMcpServerSelection = true,
        };
        var capabilities = new CapabilitySnapshot(
            CapabilityQuery.Empty,
            [
                new CapabilityDescriptor
                {
                    Kind = CapabilityKind.McpServer,
                    Name = "discovered",
                    Origin = CapabilityOrigin.Personal,
                },
            ],
            isComplete: true);

        var plan = McpSessionPlanner.Build(data, "C:\\repo", capabilities, chat, null, agent);

        Assert.True(plan.Servers.ContainsKey("Allowed"));
        Assert.False(plan.Servers.ContainsKey("Blocked"));
        // The selected-but-unlisted discovered server must be disabled, not left to start.
        Assert.Contains("discovered", plan.DisabledServerNames);
    }

    [Fact]
    public void Build_DoesNotTreatASanitizedNamespaceAsACatalogName()
    {
        // Regression: the supplied set mixed raw names with their sanitized namespaces, so a Lumi
        // server called "Avalonia MCP" (namespace "Avalonia_MCP") made a *different* discovered
        // server actually named "Avalonia_MCP" look supplied — and it escaped being disabled.
        var lumiServer = new McpServer { Name = "Avalonia MCP", Command = "dotnet" };
        var data = new AppData { McpServers = [lumiServer] };
        var chat = new Chat { ActiveMcpServerNames = ["Avalonia MCP"], HasExplicitMcpServerSelection = true };
        var capabilities = new CapabilitySnapshot(
            CapabilityQuery.Empty,
            [
                new CapabilityDescriptor
                {
                    Kind = CapabilityKind.McpServer,
                    Name = "Avalonia_MCP",
                    Origin = CapabilityOrigin.Personal,
                },
            ],
            isComplete: true);

        var plan = McpSessionPlanner.Build(data, "C:\\repo", capabilities, chat, null, null);

        Assert.Contains("Avalonia_MCP", plan.DisabledServerNames);
    }

    [Fact]
    public void Build_MapsDisplayNameToTheNamespaceTheSessionRegistered()
    {
        // Regression: live enable/disable was called with the display name, which the session never
        // registered — so deselecting "Avalonia MCP" mid-chat silently left it running.
        var data = new AppData
        {
            McpServers =
            [
                new McpServer { Name = "Avalonia MCP", Command = "dotnet", Args = ["avalonia-mcp"] },
                new McpServer { Name = "DotSight", Command = "dotnet", Args = ["dotsight"] },
            ]
        };
        var chat = new Chat { ActiveMcpServerNames = ["Avalonia MCP", "DotSight"] };

        var plan = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, null);

        Assert.Equal("Avalonia_MCP", plan.ResolveRuntimeKey("Avalonia MCP"));
        Assert.True(plan.Servers.ContainsKey(plan.ResolveRuntimeKey("Avalonia MCP")));
        // A name needing no sanitizing is unchanged.
        Assert.Equal("DotSight", plan.ResolveRuntimeKey("DotSight"));
        // A runtime-discovered server is registered by the runtime under its own name.
        Assert.Equal("memory", plan.ResolveRuntimeKey("memory"));
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

        var servers = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, null).Servers;

        Assert.All(servers.Keys, key => Assert.Matches("^[a-zA-Z0-9_-]+$", key));
        Assert.True(servers.ContainsKey("Avalonia_MCP"));
        Assert.True(servers.ContainsKey("Avalonia_MCP_2"));
    }

    [Fact]
    public void Build_PrefersLumiServerWhenDiscoveredServerSharesName()
    {
        var data = new AppData
        {
            McpServers = [new McpServer { Name = "shared", Command = "node", Args = ["configured.js"] }]
        };
        var capabilities = Capabilities(McpCapability("shared", CapabilityOrigin.Workspace));
        var chat = new Chat { ActiveMcpServerNames = ["shared"] };

        var plan = McpSessionPlanner.Build(data, "C:\\repo", capabilities, chat, null, null);

        Assert.True(plan.Servers.ContainsKey("shared"));
        Assert.False(plan.Servers.ContainsKey("shared_2"));
        // The Lumi server wins, and its name is never disabled out from under its explicit config.
        var local = Assert.IsType<McpStdioServerConfig>(plan.Servers["shared"]);
        Assert.Equal(["configured.js"], local.Args);
        Assert.DoesNotContain("shared", plan.DisabledServerNames);
    }

    [Fact]
    public void Build_UsesWorkspaceDefinitionDirectoryAsItsWorkingDirectory()
    {
        var definition = new McpServer
        {
            Name = "context-server",
            Command = "node",
            Args = ["server.js"],
        };
        var capabilities = Capabilities(new CapabilityDescriptor
        {
            Kind = CapabilityKind.McpServer,
            Name = definition.Name,
            Origin = CapabilityOrigin.Workspace,
            SourcePath = @"C:\repo\docs",
            McpDefinition = definition,
        });
        var chat = new Chat
        {
            ActiveMcpServerNames = [definition.Name],
            HasExplicitMcpServerSelection = true,
        };

        var plan = McpSessionPlanner.Build(
            new AppData(),
            @"C:\repo",
            capabilities,
            chat,
            currentActiveServerNames: null,
            activeAgent: null);

        var server = Assert.IsType<McpStdioServerConfig>(plan.Servers[definition.Name]);
        Assert.Equal(@"C:\repo\docs", server.WorkingDirectory);
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

        var servers = McpSessionPlanner.Build(data, "C:\\repo", EmptyCatalog(), chat, null, null).Servers;

        Assert.All(servers.Keys, key => Assert.Matches("^[a-zA-Z0-9_-]+$", key));
        Assert.True(servers.ContainsKey("_keep-this-"));
    }

    [Fact]
    public void Build_DisablesADiscoveredServerShadowedByADisabledLumiServer()
    {
        // A Lumi server the user switched off must not be silently replaced by an identically named
        // server that config discovery would otherwise start.
        var data = new AppData
        {
            McpServers = [new McpServer { Name = "github", Command = "node", IsEnabled = false }]
        };
        var capabilities = Capabilities(McpCapability("github", CapabilityOrigin.Lumi) with { IsEnabled = false });

        var plan = McpSessionPlanner.Build(data, "C:\\repo", capabilities, new Chat(), null, null);

        Assert.False(plan.Servers.ContainsKey("github"));
        Assert.Contains("github", plan.DisabledServerNames);
    }

    [Fact]
    public void Build_DoesNotDisableALumiServerItIsConfiguring()
    {
        var data = new AppData
        {
            McpServers = [new McpServer { Name = "github", Command = "node", IsEnabled = true }]
        };
        var capabilities = Capabilities(McpCapability("github", CapabilityOrigin.Lumi));
        var chat = new Chat { ActiveMcpServerNames = ["github"] };

        var plan = McpSessionPlanner.Build(data, "C:\\repo", capabilities, chat, null, null);

        Assert.True(plan.Servers.ContainsKey("github"));
        Assert.DoesNotContain("github", plan.DisabledServerNames);
    }

    private static CapabilitySnapshot EmptyCatalog() => CapabilitySnapshot.Empty;

    private static CapabilitySnapshot Capabilities(params CapabilityDescriptor[] capabilities)
        => new(CapabilityQuery.Empty, capabilities, isComplete: true);

    private static CapabilityDescriptor McpCapability(string name, CapabilityOrigin origin)
        => new()
        {
            Kind = CapabilityKind.McpServer,
            Name = name,
            Origin = origin,
        };
}
