using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Capabilities;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// The Copilot runtime does not read a workspace's own MCP configuration, so Lumi reports those
/// servers itself and supplies them to the session as configuration.
/// </summary>
public sealed class WorkspaceMcpCapabilityProviderTests
{
    [Fact]
    public async Task ReadsVsCodeStdioServers()
    {
        using var root = new TempWorkspace();
        root.WriteVsCodeConfig("""
            {
              "servers": {
                "workspace-files": {
                  "command": "npx",
                  "args": ["-y", "server-filesystem", "${workspaceFolder}"],
                  "env": { "LOG": "debug" }
                }
              }
            }
            """);

        var capability = Assert.Single(await LoadAsync(root.Path));

        Assert.Equal("workspace-files", capability.Name);
        Assert.Equal(CapabilityKind.McpServer, capability.Kind);
        Assert.Equal(CapabilityOrigin.Workspace, capability.Origin);
        var definition = Assert.IsType<McpServer>(capability.McpDefinition);
        Assert.Equal("npx", definition.Command);
        // ${workspaceFolder} resolves to the directory the file was found in.
        Assert.Equal(["-y", "server-filesystem", root.Path], definition.Args);
        Assert.Equal("debug", definition.Env["LOG"]);
    }

    [Fact]
    public async Task ReadsRemoteServers()
    {
        using var root = new TempWorkspace();
        root.WriteVsCodeConfig("""
            {
              "servers": {
                "remote-api": {
                  "type": "http",
                  "url": "https://example.test/mcp",
                  "headers": { "Authorization": "Bearer x" }
                }
              }
            }
            """);

        var capability = Assert.Single(await LoadAsync(root.Path));
        var definition = Assert.IsType<McpServer>(capability.McpDefinition);

        Assert.Equal("remote", definition.ServerType);
        Assert.Equal("https://example.test/mcp", definition.Url);
        Assert.Equal("Bearer x", definition.Headers["Authorization"]);
    }

    [Fact]
    public async Task ReadsRootMcpJsonUsingTheMcpServersKey()
    {
        using var root = new TempWorkspace();
        root.WriteRootConfig("""
            { "mcpServers": { "root-server": { "command": "dotnet" } } }
            """);

        var capability = Assert.Single(await LoadAsync(root.Path));

        Assert.Equal("root-server", capability.Name);
        Assert.Equal("dotnet", Assert.IsType<McpServer>(capability.McpDefinition).Command);
    }

    [Fact]
    public async Task VsCodeConfigWinsOverRootConfigForTheSameName()
    {
        using var root = new TempWorkspace();
        root.WriteVsCodeConfig("""{ "servers": { "shared": { "command": "from-vscode" } } }""");
        root.WriteRootConfig("""{ "servers": { "shared": { "command": "from-root" } } }""");

        var capability = Assert.Single(await LoadAsync(root.Path));

        Assert.Equal("from-vscode", Assert.IsType<McpServer>(capability.McpDefinition).Command);
    }

    [Fact]
    public async Task MalformedConfigIsSkippedWithoutFailingTheLoad()
    {
        using var root = new TempWorkspace();
        root.WriteVsCodeConfig("{ this is not json");
        root.WriteRootConfig("""{ "servers": { "good": { "command": "dotnet" } } }""");

        var result = await new WorkspaceMcpCapabilityProvider()
            .LoadAsync(new CapabilityQuery([root.Path]), CancellationToken.None);

        // The source itself is still available — one bad file must not blank the workspace.
        Assert.True(result.IsAvailable);
        Assert.Equal("good", Assert.Single(result.Capabilities).Name);
    }

    [Fact]
    public async Task EntriesWithoutACommandOrUrlAreIgnored()
    {
        using var root = new TempWorkspace();
        root.WriteVsCodeConfig("""
            { "servers": { "incomplete": { "type": "stdio" }, "usable": { "command": "dotnet" } } }
            """);

        var capability = Assert.Single(await LoadAsync(root.Path));

        Assert.Equal("usable", capability.Name);
    }

    [Fact]
    public async Task NoConfigFilesYieldsNothingButStaysAvailable()
    {
        using var root = new TempWorkspace();

        var result = await new WorkspaceMcpCapabilityProvider()
            .LoadAsync(new CapabilityQuery([root.Path]), CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Empty(result.Capabilities);
    }

    [Fact]
    public void PlannerSuppliesAWorkspaceServerAsSessionConfiguration()
    {
        // A workspace server has no owner in the runtime, so unlike a discovered one it only starts
        // if Lumi hands the session its configuration.
        var descriptor = new CapabilityDescriptor
        {
            Kind = CapabilityKind.McpServer,
            Name = "workspace-files",
            Origin = CapabilityOrigin.Workspace,
            McpDefinition = new McpServer
            {
                Name = "workspace-files",
                ServerType = "local",
                Command = "npx",
                Args = ["-y", "server-filesystem"],
            },
        };
        var snapshot = new CapabilitySnapshot(CapabilityQuery.Empty, [descriptor], isComplete: true);
        var chat = new Chat { ActiveMcpServerNames = ["workspace-files"], HasExplicitMcpServerSelection = true };

        var plan = McpSessionPlanner.Build(new AppData(), @"C:\repo", snapshot, chat, null, null);

        Assert.True(plan.Servers.ContainsKey("workspace-files"));
        // Supplied, therefore not also named as disabled.
        Assert.DoesNotContain("workspace-files", plan.DisabledServerNames);
    }

    [Fact]
    public void PlannerDoesNotSupplyAWorkspaceServerTheUserDeselected()
    {
        var descriptor = new CapabilityDescriptor
        {
            Kind = CapabilityKind.McpServer,
            Name = "workspace-files",
            Origin = CapabilityOrigin.Workspace,
            McpDefinition = new McpServer { Name = "workspace-files", ServerType = "local", Command = "npx" },
        };
        var snapshot = new CapabilitySnapshot(CapabilityQuery.Empty, [descriptor], isComplete: true);
        var chat = new Chat { ActiveMcpServerNames = [], HasExplicitMcpServerSelection = true };

        var plan = McpSessionPlanner.Build(new AppData(), @"C:\repo", snapshot, chat, null, null);

        // The GitHub web-search server is always bootstrapped; only the deselected one must be absent.
        Assert.False(plan.Servers.ContainsKey("workspace-files"));
    }

    private static async Task<IReadOnlyList<CapabilityDescriptor>> LoadAsync(string directory)
    {
        var result = await new WorkspaceMcpCapabilityProvider()
            .LoadAsync(new CapabilityQuery([directory]), CancellationToken.None);
        return result.Capabilities;
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"lumi-workspace-mcp-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void WriteVsCodeConfig(string json)
        {
            var dir = System.IO.Path.Combine(Path, ".vscode");
            Directory.CreateDirectory(dir);
            File.WriteAllText(System.IO.Path.Combine(dir, "mcp.json"), json);
        }

        public void WriteRootConfig(string json)
            => File.WriteAllText(System.IO.Path.Combine(Path, ".mcp.json"), json);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }
}
