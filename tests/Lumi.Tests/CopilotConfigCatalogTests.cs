using System;
using System.IO;
using System.Linq;
using GitHub.Copilot.SDK;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class CopilotConfigCatalogTests
{
    private const string LatestPackagedVersion = "1.0.35-6";
    private const string PreviousPackagedVersion = "1.0.27";

    [Fact]
    public void DiscoverSkills_MergesWorkspaceAndCopilotSources()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-copilot-skill-test-{Guid.NewGuid():N}");
        var workDir = Path.Combine(tempRoot, "repo");
        var copilotRoot = Path.Combine(tempRoot, "copilot");

        try
        {
            Directory.CreateDirectory(Path.Combine(workDir, ".github", "skills"));
            Directory.CreateDirectory(Path.Combine(copilotRoot, "skills", "user-skill"));
            Directory.CreateDirectory(Path.Combine(copilotRoot, "pkg", "universal", LatestPackagedVersion, "builtin-skills", "package-skill"));

            File.WriteAllText(
                Path.Combine(workDir, ".github", "skills", "workspace-skill.md"),
                """
                ---
                name: Workspace Skill
                description: Skill from the workspace
                ---

                # Workspace Skill
                Use workspace-specific context.
                """);

            File.WriteAllText(
                Path.Combine(copilotRoot, "skills", "user-skill", "SKILL.md"),
                """
                ---
                name: User Skill
                description: >-
                    Skill loaded from the user's Copilot config
                ---

                # User Skill
                Use user-level Copilot instructions.
                """);

            File.WriteAllText(
                Path.Combine(copilotRoot, "pkg", "universal", LatestPackagedVersion, "builtin-skills", "package-skill", "SKILL.md"),
                """
                ---
                name: Package Skill
                description: Skill bundled with Copilot
                ---

                # Package Skill
                Built-in package skill.
                """);

            var skills = CopilotConfigCatalog.DiscoverSkills(workDir, copilotRoot);

            Assert.Equal(3, skills.Count);
            Assert.Contains(skills, skill => skill.Name == "Workspace Skill" && skill.Description == "Skill from the workspace");
            Assert.Contains(skills, skill => skill.Name == "User Skill" && skill.Description == "Skill loaded from the user's Copilot config");
            Assert.Contains(skills, skill => skill.Name == "Package Skill" && skill.Description == "Skill bundled with Copilot");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void DiscoverAgents_PrefersWorkspaceDefinitionsOverUserCopilot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-copilot-agent-test-{Guid.NewGuid():N}");
        var workDir = Path.Combine(tempRoot, "repo");
        var copilotRoot = Path.Combine(tempRoot, "copilot");

        try
        {
            Directory.CreateDirectory(Path.Combine(workDir, ".github", "agents"));
            Directory.CreateDirectory(Path.Combine(copilotRoot, "agents"));

            File.WriteAllText(
                Path.Combine(workDir, ".github", "agents", "shared-agent.md"),
                """
                ---
                name: Shared Agent
                description: Workspace definition
                ---

                # Shared Agent
                Workspace agent content.
                """);

            File.WriteAllText(
                Path.Combine(copilotRoot, "agents", "shared-agent.md"),
                """
                ---
                name: Shared Agent
                description: User definition
                ---

                # Shared Agent
                User agent content.
                """);

            File.WriteAllText(
                Path.Combine(copilotRoot, "agents", "user-agent.md"),
                """
                ---
                name: User Agent
                description: User-only definition
                ---

                # User Agent
                User agent content.
                """);

            var agents = CopilotConfigCatalog.DiscoverAgents(workDir, copilotRoot);
            var sharedAgent = agents.Single(agent => agent.Name == "Shared Agent");

            Assert.Equal(2, agents.Count);
            Assert.Equal("Workspace definition", sharedAgent.Description);
            Assert.Contains("Workspace agent content.", sharedAgent.Content);
            Assert.Contains(agents, agent => agent.Name == "User Agent" && agent.Description == "User-only definition");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Discover_MergesPrimaryAndAdditionalWorkspaceSourcesInOrder()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-copilot-multi-folder-test-{Guid.NewGuid():N}");
        var primaryWorkDir = Path.Combine(tempRoot, "primary");
        var additionalDir = Path.Combine(tempRoot, "additional");
        var copilotRoot = Path.Combine(tempRoot, "copilot");

        try
        {
            Directory.CreateDirectory(Path.Combine(primaryWorkDir, ".github", "skills"));
            Directory.CreateDirectory(Path.Combine(additionalDir, ".github", "skills"));
            Directory.CreateDirectory(Path.Combine(additionalDir, ".github", "agents"));
            Directory.CreateDirectory(copilotRoot);

            File.WriteAllText(
                Path.Combine(primaryWorkDir, ".github", "skills", "shared-skill.md"),
                """
                ---
                name: Shared Skill
                description: Primary project version
                ---

                Use the primary project version.
                """);

            File.WriteAllText(
                Path.Combine(additionalDir, ".github", "skills", "shared-skill.md"),
                """
                ---
                name: Shared Skill
                description: Additional folder version
                ---

                Use the additional folder version.
                """);

            File.WriteAllText(
                Path.Combine(additionalDir, ".github", "skills", "additional-skill.md"),
                """
                ---
                name: Additional Skill
                description: Extra project folder skill
                ---

                Use the extra folder skill.
                """);

            File.WriteAllText(
                Path.Combine(additionalDir, ".github", "agents", "additional-agent.md"),
                """
                ---
                name: Additional Agent
                description: Extra project folder agent
                ---

                Use the extra folder agent.
                """);

            var catalog = CopilotConfigCatalog.Discover([primaryWorkDir, additionalDir], copilotRoot);

            var sharedSkill = catalog.Skills.Single(skill => skill.Name == "Shared Skill");
            Assert.Equal("Primary project version", sharedSkill.Description);
            Assert.Contains(catalog.Skills, skill => skill.Name == "Additional Skill" && skill.Description == "Extra project folder skill");
            Assert.Contains(catalog.Agents, agent => agent.Name == "Additional Agent" && agent.Description == "Extra project folder agent");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Discover_LoadsPluginSkillsAndAgents()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-copilot-plugin-catalog-test-{Guid.NewGuid():N}");
        var workDir = Path.Combine(tempRoot, "repo");
        var copilotRoot = Path.Combine(tempRoot, "copilot");
        var installedPlugin = Path.Combine(copilotRoot, "installed-plugins", "market", "installed-plugin");
        var loosePlugin = Path.Combine(copilotRoot, "plugins", "loose-plugin");

        try
        {
            Directory.CreateDirectory(workDir);
            Directory.CreateDirectory(Path.Combine(installedPlugin, "skills", "installed-plugin-skill"));
            Directory.CreateDirectory(Path.Combine(installedPlugin, "agents"));
            Directory.CreateDirectory(Path.Combine(loosePlugin, "skills", "loose-plugin-skill"));

            File.WriteAllText(
                Path.Combine(installedPlugin, "skills", "installed-plugin-skill", "SKILL.md"),
                """
                ---
                name: Installed Plugin Skill
                description: Loaded from an installed plugin
                ---

                Installed plugin skill content.
                """);

            File.WriteAllText(
                Path.Combine(installedPlugin, "agents", "installed.agent.md"),
                """
                ---
                name: Installed Plugin Agent
                description: Agent loaded from an installed plugin
                ---

                Installed plugin agent content.
                """);

            File.WriteAllText(
                Path.Combine(loosePlugin, "skills", "loose-plugin-skill", "SKILL.md"),
                "Loose plugin skill content without front matter.");

            var catalog = CopilotConfigCatalog.Discover(workDir, copilotRoot);

            Assert.Contains(catalog.Skills, skill => skill.Name == "Installed Plugin Skill"
                && skill.Description == "Loaded from an installed plugin");
            Assert.Contains(catalog.Skills, skill => skill.Name == "loose-plugin-skill"
                && skill.Content == "Loose plugin skill content without front matter.");
            Assert.Contains(catalog.Agents, agent => agent.Name == "Installed Plugin Agent"
                && agent.Description == "Agent loaded from an installed plugin");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ProjectContextCatalog_LoadsProjectSkillsAgentsAndMcpsFromOneSnapshot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-project-context-catalog-test-{Guid.NewGuid():N}");
        var primaryWorkDir = Path.Combine(tempRoot, "primary");
        var additionalDir = Path.Combine(tempRoot, "additional");
        var copilotRoot = Path.Combine(tempRoot, "copilot");

        try
        {
            Directory.CreateDirectory(Path.Combine(primaryWorkDir, ".github", "skills"));
            Directory.CreateDirectory(Path.Combine(primaryWorkDir, ".vscode"));
            Directory.CreateDirectory(Path.Combine(additionalDir, ".github", "agents"));
            Directory.CreateDirectory(Path.Combine(additionalDir, ".vscode"));
            Directory.CreateDirectory(copilotRoot);

            File.WriteAllText(
                Path.Combine(primaryWorkDir, ".github", "skills", "primary-skill.md"),
                """
                ---
                name: Primary Skill
                description: Primary project skill
                ---

                Use the primary project skill.
                """);

            File.WriteAllText(
                Path.Combine(additionalDir, ".github", "agents", "additional-agent.md"),
                """
                ---
                name: Additional Agent
                description: Extra project folder agent
                ---

                Use the additional folder agent.
                """);

            File.WriteAllText(
                Path.Combine(primaryWorkDir, ".vscode", "mcp.json"),
                """
                {
                  "servers": {
                    "shared": {
                      "command": "primary-command",
                      "args": ["--primary"]
                    }
                  }
                }
                """);

            File.WriteAllText(
                Path.Combine(additionalDir, ".vscode", "mcp.json"),
                """
                {
                  "servers": {
                    "shared": {
                      "command": "additional-command"
                    },
                    "additional": {
                      "type": "http",
                      "url": "https://example.com/mcp"
                    }
                  }
                }
                """);

            var project = new Project
            {
                WorkingDirectory = primaryWorkDir,
                AdditionalContextDirectories = [additionalDir]
            };

            var catalog = ProjectContextCatalog.Discover(primaryWorkDir, project, copilotRoot);

            Assert.Contains(catalog.Skills, skill => skill.Name == "Primary Skill");
            Assert.Contains(catalog.Agents, agent => agent.Name == "Additional Agent");

            var shared = Assert.Single(catalog.McpServers, server => server.Name == "shared");
            var sharedConfig = Assert.IsType<McpStdioServerConfig>(shared.Config);
            Assert.Equal("primary-command", sharedConfig.Command);
            Assert.Equal(primaryWorkDir, sharedConfig.Cwd);
            Assert.Equal(Path.Combine(primaryWorkDir, ".vscode", "mcp.json"), shared.SourcePath);

            var additional = Assert.Single(catalog.McpServers, server => server.Name == "additional");
            var additionalConfig = Assert.IsType<McpHttpServerConfig>(additional.Config);
            Assert.Equal("https://example.com/mcp", additionalConfig.Url);
            var diagnostic = Assert.Single(catalog.Diagnostics);
            Assert.Contains("duplicate MCP server 'shared'", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ProjectContextCatalog_SkipsInvalidMcpServersWithoutDroppingValidServers()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-project-context-mcp-invalid-test-{Guid.NewGuid():N}");
        var workDir = Path.Combine(tempRoot, "project");

        try
        {
            Directory.CreateDirectory(Path.Combine(workDir, ".vscode"));
            File.WriteAllText(
                Path.Combine(workDir, ".vscode", "mcp.json"),
                """
                {
                  "servers": {
                    "missing-command": {
                      "args": ["--flag"]
                    },
                    "bad-args": {
                      "command": "npx",
                      "args": [123]
                    },
                    "bad-url": {
                      "type": "http",
                      "url": "not-a-url"
                    },
                    "valid": {
                      "command": "npx",
                      "args": ["valid-server"]
                    }
                  }
                }
                """);

            var catalog = ProjectContextCatalog.Discover(workDir, project: null, copilotRootOverride: Path.Combine(tempRoot, "missing-copilot"));

            var server = Assert.Single(catalog.McpServers);
            Assert.Equal("valid", server.Name);
            Assert.Equal("npx", Assert.IsType<McpStdioServerConfig>(server.Config).Command);
            Assert.Equal(3, catalog.Diagnostics.Count);
            Assert.Contains(catalog.Diagnostics, diagnostic => diagnostic.Message.Contains("missing-command", StringComparison.Ordinal));
            Assert.Contains(catalog.Diagnostics, diagnostic => diagnostic.Message.Contains("bad-args", StringComparison.Ordinal));
            Assert.Contains(catalog.Diagnostics, diagnostic => diagnostic.Message.Contains("bad-url", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ProjectContextCatalog_ExpandsVsCodeMcpVariables()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-project-context-mcp-variables-test-{Guid.NewGuid():N}");
        var workDir = Path.Combine(tempRoot, "sherlock-diagnostics");
        var envName = "LUMI_MCP_TEST_TOKEN_" + Guid.NewGuid().ToString("N");

        try
        {
            Environment.SetEnvironmentVariable(envName, "test-token");
            Directory.CreateDirectory(Path.Combine(workDir, ".vscode"));
            File.WriteAllText(
                Path.Combine(workDir, ".vscode", "mcp.json"),
                $$"""
                {
                  "mcpServers": {
                    "agentic-investigation-runner": {
                      "type": "stdio",
                      "command": "pwsh",
                      "args": [
                        "-NoLogo",
                        "-NoProfile",
                        "-File",
                        "${workspaceFolder}/src/Tools/AgenticInvestigationRunner/run-mcp.ps1",
                        "${workspaceFolderBasename}"
                      ],
                      "env": {
                        "RUNNER_TOKEN": "${env:{{envName}}}"
                      }
                    },
                    "icm": {
                      "type": "http",
                      "url": "https://example.com/${workspaceFolderBasename}/mcp",
                      "headers": {
                        "Authorization": "Bearer ${env:{{envName}}}"
                      }
                    }
                  }
                }
                """);

            var catalog = ProjectContextCatalog.Discover(workDir, project: null, copilotRootOverride: Path.Combine(tempRoot, "missing-copilot"));

            var local = Assert.IsType<McpStdioServerConfig>(
                Assert.Single(catalog.McpServers, server => server.Name == "agentic-investigation-runner").Config);
            Assert.Equal("pwsh", local.Command);
            Assert.Equal($"{workDir}/src/Tools/AgenticInvestigationRunner/run-mcp.ps1", local.Args[3]);
            Assert.Equal("sherlock-diagnostics", local.Args[4]);
            Assert.NotNull(local.Env);
            Assert.Equal("test-token", local.Env!["RUNNER_TOKEN"]);

            var remote = Assert.IsType<McpHttpServerConfig>(
                Assert.Single(catalog.McpServers, server => server.Name == "icm").Config);
            Assert.Equal("https://example.com/sherlock-diagnostics/mcp", remote.Url);
            Assert.NotNull(remote.Headers);
            Assert.Equal("Bearer test-token", remote.Headers!["Authorization"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ProjectContextCatalog_WarnsWhenBothMcpRootsArePresent()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-project-context-mcp-roots-test-{Guid.NewGuid():N}");
        var workDir = Path.Combine(tempRoot, "project");

        try
        {
            Directory.CreateDirectory(Path.Combine(workDir, ".vscode"));
            File.WriteAllText(
                Path.Combine(workDir, ".vscode", "mcp.json"),
                """
                {
                  "servers": {
                    "preferred": {
                      "command": "preferred-command"
                    }
                  },
                  "mcpServers": {
                    "ignored": {
                      "command": "ignored-command"
                    }
                  }
                }
                """);

            var catalog = ProjectContextCatalog.Discover(workDir, project: null, copilotRootOverride: Path.Combine(tempRoot, "missing-copilot"));

            var server = Assert.Single(catalog.McpServers);
            Assert.Equal("preferred", server.Name);
            Assert.Equal("preferred-command", Assert.IsType<McpStdioServerConfig>(server.Config).Command);
            var diagnostic = Assert.Single(catalog.Diagnostics);
            Assert.Contains("Both 'servers' and 'mcpServers'", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ProjectContextCatalog_LoadsUserAndPluginMcpConfigs()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-project-context-plugin-mcp-test-{Guid.NewGuid():N}");
        var workDir = Path.Combine(tempRoot, "repo");
        var copilotRoot = Path.Combine(tempRoot, "copilot");
        var workiqPlugin = Path.Combine(copilotRoot, "installed-plugins", "copilot-plugins", "workiq");

        try
        {
            Directory.CreateDirectory(workDir);
            Directory.CreateDirectory(workiqPlugin);

            File.WriteAllText(
                Path.Combine(copilotRoot, "mcp-config.json"),
                """
                {
                  "mcpServers": {
                    "memory": {
                      "command": "npx",
                      "args": ["-y", "@modelcontextprotocol/server-memory"],
                      "tools": ["remember"],
                      "env": {
                        "WORKSPACE_NAME": "${workspaceFolderBasename}"
                      }
                    }
                  }
                }
                """);

            File.WriteAllText(
                Path.Combine(workiqPlugin, ".mcp.json"),
                """
                {
                  "mcpServers": {
                    "workiq": {
                      "command": "npx",
                      "args": ["-y", "@microsoft/workiq@latest", "mcp"],
                      "tools": ["*"]
                    }
                  }
                }
                """);

            var catalog = ProjectContextCatalog.Discover(workDir, project: null, copilotRootOverride: copilotRoot);

            var memory = Assert.IsType<McpStdioServerConfig>(
                Assert.Single(catalog.McpServers, server => server.Name == "memory").Config);
            Assert.Equal(workDir, memory.Cwd);
            Assert.Equal(["remember"], memory.Tools);
            Assert.NotNull(memory.Env);
            Assert.Equal("repo", memory.Env!["WORKSPACE_NAME"]);

            var workiqServer = Assert.Single(catalog.McpServers, server => server.Name == "workiq");
            var workiq = Assert.IsType<McpStdioServerConfig>(workiqServer.Config);
            Assert.Equal(workiqPlugin, workiq.Cwd);
            Assert.Equal("npx", workiq.Command);
            Assert.Equal(["-y", "@microsoft/workiq@latest", "mcp"], workiq.Args);
            Assert.Equal(["*"], workiq.Tools);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ProjectContextCatalogSnapshot_CopiesInputsAndUsesFirstDefinitionForLookup()
    {
        var skills = new List<CopilotSkillDefinition>
        {
            new("Shared", "First", "first content", @"C:\first.md"),
            new("Shared", "Second", "second content", @"C:\second.md")
        };
        var agents = new List<CopilotAgentDefinition>
        {
            new("Agent", "First agent", "first agent content", @"C:\agent-first.md"),
            new("Agent", "Second agent", "second agent content", @"C:\agent-second.md")
        };
        var mcpServers = new List<ProjectContextMcpServerDefinition>
        {
            new("server", new McpStdioServerConfig { Command = "npx" }, @"C:\mcp.json", @"C:\")
        };

        var snapshot = new ProjectContextCatalogSnapshot(skills, agents, mcpServers);
        skills.Add(new CopilotSkillDefinition("Added Later", "Late", "late content", @"C:\late.md"));
        agents.Clear();
        mcpServers.Clear();

        Assert.Equal(2, snapshot.Skills.Count);
        Assert.Equal(2, snapshot.Agents.Count);
        Assert.Single(snapshot.McpServers);
        Assert.Equal("First", snapshot.FindSkill("Shared")!.Description);
        Assert.Equal("First agent", snapshot.FindAgent("Agent")!.Description);
    }

    [Fact]
    public void Discover_UsesOnlyLatestPackagedCopilotCatalog()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-copilot-package-test-{Guid.NewGuid():N}");
        var workDir = Path.Combine(tempRoot, "repo");
        var copilotRoot = Path.Combine(tempRoot, "copilot");

        try
        {
            Directory.CreateDirectory(workDir);
            Directory.CreateDirectory(Path.Combine(copilotRoot, "pkg", "universal", PreviousPackagedVersion, "builtin-skills", "package-skill"));
            Directory.CreateDirectory(Path.Combine(copilotRoot, "pkg", "universal", PreviousPackagedVersion, "builtin-agents", "package-agent"));
            Directory.CreateDirectory(Path.Combine(copilotRoot, "pkg", "universal", LatestPackagedVersion, "builtin-skills", "package-skill"));
            Directory.CreateDirectory(Path.Combine(copilotRoot, "pkg", "universal", LatestPackagedVersion, "builtin-agents", "package-agent"));

            File.WriteAllText(
                Path.Combine(copilotRoot, "pkg", "universal", PreviousPackagedVersion, "builtin-skills", "package-skill", "SKILL.md"),
                """
                ---
                name: Package Skill
                description: Previous package
                ---

                # Package Skill
                Version 1.0.27
                """);

            File.WriteAllText(
                Path.Combine(copilotRoot, "pkg", "universal", PreviousPackagedVersion, "builtin-agents", "package-agent", "AGENT.md"),
                """
                ---
                name: Package Agent
                description: Previous package
                ---

                # Package Agent
                Version 1.0.27
                """);

            File.WriteAllText(
                Path.Combine(copilotRoot, "pkg", "universal", LatestPackagedVersion, "builtin-skills", "package-skill", "SKILL.md"),
                """
                ---
                name: Package Skill
                description: Latest package
                ---

                # Package Skill
                Version 1.0.35-6
                """);

            File.WriteAllText(
                Path.Combine(copilotRoot, "pkg", "universal", LatestPackagedVersion, "builtin-agents", "package-agent", "AGENT.md"),
                """
                ---
                name: Package Agent
                description: Latest package
                ---

                # Package Agent
                Version 1.0.35-6
                """);

            var catalog = CopilotConfigCatalog.Discover(workDir, copilotRoot);

            var skill = Assert.Single(catalog.Skills);
            var agent = Assert.Single(catalog.Agents);
            Assert.Equal("Latest package", skill.Description);
            Assert.Contains("Version 1.0.35-6", skill.Content);
            Assert.Equal("Latest package", agent.Description);
            Assert.Contains("Version 1.0.35-6", agent.Content);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
