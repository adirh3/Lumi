using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class CurrentChatManagementTests
{
    [Fact]
    public void Get_ReturnsCurrentChatMetadata()
    {
        using var temp = new TempDirectory();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Lumi",
            WorkingDirectory = temp.Path
        };
        var agent = new LumiAgent { Id = Guid.NewGuid(), Name = "Coding Lumi" };
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Current chat",
            ProjectId = project.Id,
            AgentId = agent.Id,
            LastModelUsed = "gpt-test",
            LastReasoningEffortUsed = "high"
        };
        using var harness = CreateHarness(new AppData
        {
            Projects = [project],
            Agents = [agent],
            Chats = [chat]
        });

        var result = harness.ViewModel.DescribeManagedCurrentChat(chat);

        Assert.Contains($"id: {chat.Id}", result);
        Assert.Contains("title: Current chat", result);
        Assert.Contains("project: Lumi", result);
        Assert.Contains("agent: Coding Lumi", result);
        Assert.Contains("workspaceMode: local", result);
        Assert.Contains($"workspace: {temp.Path}", result);
        Assert.Contains("worktreeRoot: (none)", result);
        Assert.Contains("model: gpt-test", result);
        Assert.Contains("reasoningEffort: high", result);
    }

    [Fact]
    public void Update_TitlePersistsInMemoryAndRaisesEvents()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Old title" };
        using var harness = CreateHarness(new AppData { Chats = [chat] });
        (Guid Id, string Title)? titleChange = null;
        var chatUpdated = false;
        harness.ViewModel.ChatTitleChanged += (id, title) => titleChange = (id, title);
        harness.ViewModel.ChatUpdated += () => chatUpdated = true;

        var changes = harness.ViewModel.ApplyManagedCurrentChatUpdate(
            chat,
            normalizedTitle: "Useful title",
            normalizedWorktreeRoot: null,
            workspaceRequested: false,
            clearWorkspace: false);

        Assert.Equal("Useful title", chat.Title);
        Assert.Equal((chat.Id, "Useful title"), titleChange);
        Assert.True(chatUpdated);
        Assert.Contains("title: Useful title", changes);
    }

    [Fact]
    public async Task Update_WorkspaceAcceptsNestedFolderAndDefersSessionInvalidation()
    {
        using var git = GitFixture.Create();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Repo",
            WorkingDirectory = git.ProjectDirectory
        };
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Worktree chat",
            ProjectId = project.Id,
            CopilotSessionId = "session-1"
        };
        using var harness = CreateHarness(new AppData
        {
            Projects = [project],
            Chats = [chat]
        });
        harness.ViewModel.CurrentChat = chat;

        var validation = await ChatViewModel.ValidateManagedWorkspaceAsync(
            git.WorktreeProjectDirectory,
            git.ProjectDirectory);
        Assert.Null(validation.Error);

        var changes = harness.ViewModel.ApplyManagedCurrentChatUpdate(
            chat,
            normalizedTitle: null,
            normalizedWorktreeRoot: validation.WorktreeRoot,
            workspaceRequested: true,
            clearWorkspace: false);

        Assert.Equal(git.WorktreeDirectory, chat.WorktreePath);
        Assert.Equal(git.WorktreeDirectory, harness.ViewModel.WorktreePath);
        Assert.True(harness.ViewModel.IsWorktreeMode);
        Assert.Equal("session-1", chat.CopilotSessionId);
        Assert.Contains(chat.Id, GetPendingSessionInvalidations(harness.ViewModel));
        Assert.Contains($"workspace: {git.WorktreeProjectDirectory}", changes);
    }

    [Fact]
    public async Task Update_WorkspaceWithoutPersistedSessionStillQueuesNextTurnInvalidation()
    {
        using var git = GitFixture.Create();
        var chat = new Chat { Id = Guid.NewGuid(), Title = "First turn" };
        using var harness = CreateHarness(new AppData { Chats = [chat] });

        var validation = await ChatViewModel.ValidateManagedWorkspaceAsync(
            git.WorktreeDirectory,
            projectDirectory: null);
        Assert.Null(validation.Error);
        harness.ViewModel.ApplyManagedCurrentChatUpdate(
            chat,
            normalizedTitle: null,
            normalizedWorktreeRoot: validation.WorktreeRoot,
            workspaceRequested: true,
            clearWorkspace: false);

        Assert.Contains(chat.Id, GetPendingSessionInvalidations(harness.ViewModel));
        Assert.Null(chat.CopilotSessionId);
    }

    [Fact]
    public void Update_ClearWorkspaceReturnsToProjectDirectory()
    {
        using var git = GitFixture.Create();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Repo",
            WorkingDirectory = git.ProjectDirectory
        };
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Worktree chat",
            ProjectId = project.Id,
            WorktreePath = git.WorktreeDirectory,
            CopilotSessionId = "session-1"
        };
        using var harness = CreateHarness(new AppData
        {
            Projects = [project],
            Chats = [chat]
        });
        harness.ViewModel.CurrentChat = chat;

        var changes = harness.ViewModel.ApplyManagedCurrentChatUpdate(
            chat,
            normalizedTitle: null,
            normalizedWorktreeRoot: null,
            workspaceRequested: true,
            clearWorkspace: true);

        Assert.Null(chat.WorktreePath);
        Assert.Null(harness.ViewModel.WorktreePath);
        Assert.False(harness.ViewModel.IsWorktreeMode);
        Assert.Equal("session-1", chat.CopilotSessionId);
        Assert.Contains(chat.Id, GetPendingSessionInvalidations(harness.ViewModel));
        Assert.Contains("workspace: local/project directory", changes);
        Assert.Contains($"workspace: {git.ProjectDirectory}", harness.ViewModel.DescribeManagedCurrentChat(chat));
    }

    [Fact]
    public async Task Update_RejectsNormalCheckout()
    {
        using var git = GitFixture.Create();

        var validation = await ChatViewModel.ValidateManagedWorkspaceAsync(
            git.ProjectDirectory,
            projectDirectory: null);

        Assert.Contains("normal checkout, not a linked Git worktree", validation.Error);
        Assert.Null(validation.WorktreeRoot);
    }

    [Fact]
    public async Task Update_RejectsWorktreeFromDifferentProjectRepository()
    {
        using var projectGit = GitFixture.Create();
        using var otherGit = GitFixture.Create();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Project repo",
            WorkingDirectory = projectGit.ProjectDirectory
        };
        var validation = await ChatViewModel.ValidateManagedWorkspaceAsync(
            otherGit.WorktreeDirectory,
            project.WorkingDirectory);

        Assert.Contains(
            "not a registered worktree of the current chat project's repository",
            validation.Error);
        Assert.Null(validation.WorktreeRoot);
    }

    [Fact]
    public void PathEquality_UsesPlatformCaseSensitivity()
    {
        using var temp = new TempDirectory();
        var upperCasePath = Path.Combine(temp.Path, "Worktree");
        var lowerCasePath = Path.Combine(temp.Path, "worktree");

        Assert.Equal(
            OperatingSystem.IsWindows(),
            ChatViewModel.PathsEqual(upperCasePath, lowerCasePath));
    }

    [Fact]
    public void CodingLumiMigration_AddsCurrentChatToolExactlyOnce()
    {
        var codingLumi = new LumiAgent
        {
            Name = "Coding Lumi",
            IsBuiltIn = true,
            HasExplicitToolSelection = true,
            ToolNames = ["lumi_fetch"]
        };
        var data = new AppData
        {
            Settings = new UserSettings(),
            Agents = [codingLumi]
        };
        var store = new DataStore(data);
        var migration = typeof(DataStore).GetMethod(
            "EnsureCurrentChatManagementTool",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(migration);

        migration!.Invoke(store, null);
        data.Settings.CurrentChatManagementToolSeeded = false;
        migration.Invoke(store, null);

        Assert.True(data.Settings.CurrentChatManagementToolSeeded);
        Assert.Single(codingLumi.ToolNames, name => name == "manage_current_chat");
    }

    [Fact]
    public void CodingLumiMigration_UpgradesLegacyNonEmptyToolList()
    {
        var codingLumi = new LumiAgent
        {
            Name = "Coding Lumi",
            IsBuiltIn = true,
            HasExplicitToolSelection = false,
            ToolNames = ["lumi_fetch"]
        };
        var data = new AppData
        {
            Settings = new UserSettings(),
            Agents = [codingLumi]
        };
        var store = new DataStore(data);
        var migration = typeof(DataStore).GetMethod(
            "EnsureCurrentChatManagementTool",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(migration);

        migration!.Invoke(store, null);

        Assert.True(codingLumi.HasToolRestrictions);
        Assert.True(data.Settings.CurrentChatManagementToolSeeded);
        Assert.Contains("manage_current_chat", codingLumi.ToolNames);
    }

    [Fact]
    public void IndexSnapshot_PersistsMigrationFlagAndChatWorkspace()
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Managed chat",
            WorktreePath = @"E:\repo-worktree"
        };
        var snapshot = AppDataSnapshotFactory.CreateIndexSnapshot(new AppData
        {
            Settings = new UserSettings { CurrentChatManagementToolSeeded = true },
            Chats = [chat]
        });
        var json = JsonSerializer.Serialize(snapshot, AppDataJsonContext.Default.AppData);
        var restored = JsonSerializer.Deserialize(json, AppDataJsonContext.Default.AppData);

        Assert.NotNull(restored);
        Assert.True(restored.Settings.CurrentChatManagementToolSeeded);
        var restoredChat = Assert.Single(restored.Chats);
        Assert.Equal("Managed chat", restoredChat.Title);
        Assert.Equal(@"E:\repo-worktree", restoredChat.WorktreePath);
    }

    private static TestHarness CreateHarness(AppData data)
        => new(new ChatViewModel(new DataStore(data), new CopilotService()));

    private static HashSet<Guid> GetPendingSessionInvalidations(ChatViewModel viewModel)
    {
        var field = typeof(ChatViewModel).GetField(
            "_pendingSessionInvalidations",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<HashSet<Guid>>(field!.GetValue(viewModel));
    }

    private sealed record TestHarness(ChatViewModel ViewModel) : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class GitFixture : IDisposable
    {
        private readonly TempDirectory _temp;

        private GitFixture(TempDirectory temp, string repositoryDirectory, string worktreeDirectory)
        {
            _temp = temp;
            RepositoryDirectory = repositoryDirectory;
            WorktreeDirectory = worktreeDirectory;
        }

        public string RepositoryDirectory { get; }
        public string WorktreeDirectory { get; }
        public string ProjectDirectory => Path.Combine(RepositoryDirectory, "apps", "web");
        public string WorktreeProjectDirectory => Path.Combine(WorktreeDirectory, "apps", "web");

        public static GitFixture Create()
        {
            var temp = new TempDirectory();
            var repositoryDirectory = Path.Combine(temp.Path, "repo");
            var projectDirectory = Path.Combine(repositoryDirectory, "apps", "web");
            var worktreeDirectory = Path.Combine(temp.Path, "repo-worktree");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(repositoryDirectory, "README.md"), "seed");
            File.WriteAllText(Path.Combine(projectDirectory, "app.txt"), "project");

            Git(repositoryDirectory, "init", "-q", "-b", "main");
            Git(repositoryDirectory, "config", "user.email", "test@lumi.local");
            Git(repositoryDirectory, "config", "user.name", "Lumi Test");
            Git(repositoryDirectory, "add", "-A");
            Git(repositoryDirectory, "commit", "-q", "-m", "seed");
            Git(repositoryDirectory, "worktree", "add", "-q", "-b", "feature/current-chat", worktreeDirectory);

            return new GitFixture(temp, repositoryDirectory, worktreeDirectory);
        }

        public void Dispose() => _temp.Dispose();

        private static void Git(string workingDirectory, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start git.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(' ', arguments)} failed ({process.ExitCode}).\n{output}\n{error}");
            }
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lumi-current-chat-{Guid.NewGuid():N}");

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (!Directory.Exists(Path))
                return;

            try
            {
                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temporary Git files that may still be releasing handles.
            }
        }
    }
}
