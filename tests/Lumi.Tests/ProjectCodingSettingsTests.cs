using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class ProjectCodingSettingsTests
{
    [Fact]
    public void SaveProject_PersistsCodingWorkflowPreferences()
    {
        var project = new Project { Name = "Code" };
        var store = new DataStore(new AppData { Projects = [project] });
        var viewModel = new ProjectsViewModel(store)
        {
            SelectedProject = project,
            EditAutoSyncMainBranchDaily = true,
            EditDefaultNewChatsUseWorktree = true
        };

        viewModel.SaveProjectCommand.Execute(null);

        Assert.True(project.AutoSyncMainBranchDaily);
        Assert.True(project.DefaultNewChatsUseWorktree);
    }

    [Fact]
    public async Task DraftProjectContext_SelectsConfiguredWorktreeDefault()
    {
        using var session = HeadlessTestSession.Start();
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, ".git"));
        var project = new Project
        {
            Name = "Code",
            WorkingDirectory = temp.Path,
            DefaultNewChatsUseWorktree = true
        };
        var store = new DataStore(new AppData { Projects = [project] });
        bool? selectedWorktree = null;
        bool? clearedToLocal = null;

        await session.Dispatch(() =>
        {
            using var viewModel = new ChatViewModel(store, new CopilotService());
            viewModel.SetProjectId(project.Id);
            selectedWorktree = viewModel.IsWorktreeMode;
            viewModel.ClearProjectId();
            clearedToLocal = !viewModel.IsWorktreeMode;
        }, CancellationToken.None);

        Assert.True(selectedWorktree);
        Assert.True(clearedToLocal);
    }

    [Fact]
    public void ProjectsView_ShowsCodingWorkflowSettingsOnlyForGitProjects()
    {
        var root = FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Lumi", "Views", "ProjectsView.axaml"));

        Assert.Contains("IsVisible=\"{Binding IsGitProject}\"", xaml);
        Assert.Contains("IsChecked=\"{Binding EditAutoSyncMainBranchDaily}\"", xaml);
        Assert.Contains("IsChecked=\"{Binding EditDefaultNewChatsUseWorktree}\"", xaml);
        Assert.Contains("Text=\"{Binding DefaultBranchSummary}\"", xaml);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Lumi.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate Lumi repository root.");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lumi-project-settings-{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
