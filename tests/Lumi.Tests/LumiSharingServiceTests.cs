using System.Text.Json;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class LumiSharingServiceTests
{
    [Fact]
    public async Task SyncRepository_ImportsAllSharedCapabilityTypes()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var repoPath = Path.Combine(tempRoot, "team-capabilities");
            CreateSharedRepositoryFixture(repoPath);
            var now = new DateTimeOffset(2026, 6, 2, 20, 0, 0, TimeSpan.Zero);
            var repository = new LumiSharedRepository
            {
                Name = "Team",
                Repository = repoPath,
                UpdateIntervalMinutes = 30
            };
            var data = new AppData
            {
                SharedRepositories = [repository]
            };
            var service = new LumiSharingService(new DataStore(data), () => now);

            var result = await service.SyncRepositoryAsync(repository);

            Assert.Equal(1, result.RepositoryCount);
            Assert.Equal(1, result.SkillCount);
            Assert.Equal(1, result.AgentCount);
            Assert.Equal(1, result.McpServerCount);
            Assert.Equal(1, result.MemoryCount);
            Assert.Equal(SharedRepositorySyncStatuses.Synced, repository.LastSyncStatus);
            Assert.Equal(now.AddMinutes(30), repository.NextSyncAt);

            var skill = Assert.Single(data.Skills);
            Assert.Equal("Team Skill", skill.Name);
            Assert.Equal("🧩", skill.IconGlyph);
            Assert.Equal(repository.Id, skill.SharedSource?.RepositoryId);

            var server = Assert.Single(data.McpServers);
            Assert.Equal("team-tools", server.Name);
            Assert.Equal("local", server.ServerType);
            Assert.Equal("node", server.Command);
            Assert.Equal(["server.js"], server.Args);

            var agent = Assert.Single(data.Agents);
            Assert.Equal("Team Lumi", agent.Name);
            Assert.Equal([skill.Id], agent.SkillIds);
            Assert.Equal(["web_search"], agent.ToolNames);
            Assert.Equal([server.Id], agent.McpServerIds);

            var memory = Assert.Single(data.Memories);
            Assert.Equal("Team/process", memory.Key);
            Assert.Equal("shared", memory.Source);
            Assert.Equal(repository.Id, memory.SharedSource?.RepositoryId);

            await service.PublishSkillAsync(repository, skill);
            Assert.True(File.Exists(Path.Combine(repoPath, ".github", "skills", "team-skill-source", "SKILL.md")));
            Assert.False(Directory.Exists(Path.Combine(repoPath, ".github", "skills", "team-skill")));
        }
        finally
        {
            DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task SyncRepository_UpdatesExistingSharedItemsWithoutDuplicating()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var repoPath = Path.Combine(tempRoot, "team-capabilities");
            CreateSharedRepositoryFixture(repoPath);
            var repository = new LumiSharedRepository
            {
                Name = "Team",
                Repository = repoPath
            };
            var data = new AppData { SharedRepositories = [repository] };
            var service = new LumiSharingService(new DataStore(data));

            await service.SyncRepositoryAsync(repository);
            File.WriteAllText(
                Path.Combine(repoPath, ".github", "skills", "team-skill-source", "SKILL.md"),
                """
                ---
                name: Team Skill
                description: Updated shared skill
                icon: 🧩
                ---

                Updated instructions.
                """);

            await service.SyncRepositoryAsync(repository);

            var skill = Assert.Single(data.Skills);
            Assert.Equal("Updated shared skill", skill.Description);
            Assert.Equal("Updated instructions.", skill.Content);
            Assert.Single(data.Agents);
            Assert.Single(data.McpServers);
            Assert.Single(data.Memories);
        }
        finally
        {
            DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task SyncRepository_ImportsCurrentContentsWhenGitPullFails()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var repoPath = Path.Combine(tempRoot, "team-capabilities");
            CreateSharedRepositoryFixture(repoPath);
            Directory.CreateDirectory(Path.Combine(repoPath, ".git"));
            var repository = new LumiSharedRepository
            {
                Name = "Team",
                Repository = repoPath
            };
            var data = new AppData { SharedRepositories = [repository] };
            var service = new LumiSharingService(new DataStore(data));

            var result = await service.SyncRepositoryAsync(repository);

            Assert.Equal(1, result.SkillCount);
            Assert.Equal(SharedRepositorySyncStatuses.Synced, repository.LastSyncStatus);
            Assert.Contains("Update warning", repository.LastSyncMessage);
        }
        finally
        {
            DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task PublishCapability_WritesRepositoryFilesAndMarksItemsShared()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var repoPath = Path.Combine(tempRoot, "team-capabilities");
            Directory.CreateDirectory(repoPath);
            var repository = new LumiSharedRepository
            {
                Name = "Team",
                Repository = repoPath
            };
            var skill = new Skill
            {
                Name = "Writing Helper",
                Description = "Helps write",
                Content = "Write clearly.",
                IconGlyph = "✍"
            };
            var server = new McpServer
            {
                Name = "docs",
                Command = "node"
            };
            var agent = new LumiAgent
            {
                Name = "Docs Lumi",
                Description = "Documentation helper",
                SystemPrompt = "Help with docs.",
                IconGlyph = "📚",
                SkillIds = [skill.Id],
                ToolNames = ["web_search"],
                McpServerIds = [server.Id]
            };
            var memory = new Memory
            {
                Key = "Docs/style",
                Content = "Use concise prose.",
                Category = "Team"
            };
            var data = new AppData
            {
                SharedRepositories = [repository],
                Skills = [skill],
                Agents = [agent],
                McpServers = [server],
                Memories = [memory]
            };
            var service = new LumiSharingService(new DataStore(data));

            await service.PublishSkillAsync(repository, skill);
            await service.PublishAgentAsync(repository, agent);
            await service.PublishMemoryAsync(repository, memory);

            Assert.True(File.Exists(Path.Combine(repoPath, ".github", "skills", "writing-helper", "SKILL.md")));
            var agentMarkdown = File.ReadAllText(Path.Combine(repoPath, ".github", "agents", "docs-lumi", "AGENT.md"));
            Assert.Contains("skills:", agentMarkdown);
            Assert.Contains("Writing Helper", agentMarkdown);
            Assert.Contains("mcpServers:", agentMarkdown);
            Assert.Equal(repository.Id, skill.SharedSource?.RepositoryId);
            Assert.Equal(repository.Id, agent.SharedSource?.RepositoryId);
            Assert.Equal(repository.Id, memory.SharedSource?.RepositoryId);

            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoPath, ".lumi", "memories.json")));
            var savedMemory = document.RootElement.GetProperty("memories")[0];
            Assert.Equal("Docs/style", savedMemory.GetProperty("key").GetString());
            Assert.Equal("Use concise prose.", savedMemory.GetProperty("content").GetString());
        }
        finally
        {
            DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task PublishSkill_CreatesPushesBranchAndRestoresBaseBranch()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var remotePath = Path.Combine(tempRoot, "remote.git");
            var repoPath = Path.Combine(tempRoot, "repo");
            Directory.CreateDirectory(repoPath);
            RunGit(tempRoot, $"init --bare \"{remotePath}\"");
            RunGit(repoPath, "init");
            RunGit(repoPath, "checkout -b main");
            File.WriteAllText(Path.Combine(repoPath, "README.md"), "sharing fixture");
            RunGit(repoPath, "add README.md");
            RunGit(repoPath, "-c user.name=\"Test\" -c user.email=\"test@example.com\" commit -m \"Initial\"");
            RunGit(repoPath, $"remote add origin \"{remotePath}\"");
            RunGit(repoPath, "push -u origin main");

            var repository = new LumiSharedRepository
            {
                Name = "Team",
                Repository = repoPath
            };
            var skill = new Skill
            {
                Name = "Branch Skill",
                Description = "Published on a branch",
                Content = "Branch content.",
                IconGlyph = "🌿"
            };
            var data = new AppData
            {
                SharedRepositories = [repository],
                Skills = [skill]
            };
            var service = new LumiSharingService(new DataStore(data));

            var result = await service.PublishSkillAsync(repository, skill);

            Assert.True(result.Pushed);
            Assert.StartsWith("lumi/share/branch-skill-", result.BranchName);
            Assert.Equal("main", RunGit(repoPath, "branch --show-current").Trim());
            var remoteBranches = RunGit(repoPath, "ls-remote --heads origin");
            Assert.Contains(result.BranchName!, remoteBranches);
            var publishedContent = RunGit(repoPath, $"show \"origin/{result.BranchName}:.github/skills/branch-skill/SKILL.md\"");
            Assert.Contains("Branch Skill", publishedContent);
            Assert.Equal(repository.Id, skill.SharedSource?.RepositoryId);
        }
        finally
        {
            DeleteDirectory(tempRoot);
        }
    }

    [SkippableFact]
    public async Task SyncRepository_ImportsFromRealGitHubRepository()
    {
        var repoUrl = Environment.GetEnvironmentVariable("LUMI_SHARING_GITHUB_REPO_URL");
        Skip.If(string.IsNullOrWhiteSpace(repoUrl), "Set LUMI_SHARING_GITHUB_REPO_URL to run the real GitHub sharing integration test.");

        var tempRoot = CreateTempDirectory();
        try
        {
            var repository = new LumiSharedRepository
            {
                Name = "GitHub fixture",
                Repository = repoUrl!,
                Branch = Environment.GetEnvironmentVariable("LUMI_SHARING_GITHUB_REPO_BRANCH") ?? "",
                LocalPath = Path.Combine(tempRoot, "clone")
            };
            var data = new AppData { SharedRepositories = [repository] };
            var service = new LumiSharingService(new DataStore(data));

            var result = await service.SyncRepositoryAsync(repository);

            Assert.Equal(1, result.RepositoryCount);
            Assert.True(result.SkillCount >= 1, repository.LastSyncMessage);
            Assert.True(result.McpServerCount >= 1, repository.LastSyncMessage);
            Assert.True(result.MemoryCount >= 1, repository.LastSyncMessage);
            Assert.Contains(data.Skills, skill => skill.Name == "GitHub Shared Skill");
            Assert.Contains(data.McpServers, server => server.Name == "github-tools");
            Assert.Contains(data.Memories, memory => memory.Key == "GitHub/shared");
            Assert.All(data.Skills.Where(skill => skill.Name == "GitHub Shared Skill"), skill =>
                Assert.Equal(repository.Id, skill.SharedSource?.RepositoryId));
        }
        finally
        {
            DeleteDirectory(tempRoot);
        }
    }

    private static void CreateSharedRepositoryFixture(string repoPath)
    {
        Directory.CreateDirectory(Path.Combine(repoPath, ".github", "skills", "team-skill-source"));
        Directory.CreateDirectory(Path.Combine(repoPath, ".github", "agents", "team-lumi"));
        Directory.CreateDirectory(Path.Combine(repoPath, ".vscode"));
        Directory.CreateDirectory(Path.Combine(repoPath, ".lumi"));

        File.WriteAllText(
            Path.Combine(repoPath, ".github", "skills", "team-skill-source", "SKILL.md"),
            """
            ---
            name: Team Skill
            description: Shared skill for the team
            icon: 🧩
            ---

            Use the team skill.
            """);

        File.WriteAllText(
            Path.Combine(repoPath, ".github", "agents", "team-lumi", "AGENT.md"),
            """
            ---
            name: Team Lumi
            description: Shared Lumi for the team
            icon: 🚀
            skills:
              - Team Skill
            tools:
              - web_search
            mcpServers:
              - team-tools
            ---

            Act like the team Lumi.
            """);

        File.WriteAllText(
            Path.Combine(repoPath, ".vscode", "mcp.json"),
            """
            {
              "servers": {
                "team-tools": {
                  "type": "stdio",
                  "command": "node",
                  "args": ["server.js"],
                  "env": {
                    "TEAM": "1"
                  }
                }
              }
            }
            """);

        File.WriteAllText(
            Path.Combine(repoPath, ".lumi", "memories.json"),
            """
            {
              "memories": [
                {
                  "key": "Team/process",
                  "content": "Review shared capabilities before publishing.",
                  "category": "Team",
                  "confidence": 90
                }
              ]
            }
            """);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lumi-sharing-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100 * attempt);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    private static string RunGit(string workingDirectory, string arguments)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        var output = stdout + stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments} failed: {output}");
        return output;
    }
}
