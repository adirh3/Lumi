using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class FeatureManagementUiRefreshTests
{
    private static DataStore CreateDataStore(params Project[] projects)
    {
        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            }
        };

        foreach (var project in projects)
            data.Projects.Add(project);

        return new DataStore(data);
    }

    [Fact]
    public void FeatureManagementStateChanged_RefreshesProjectCollections()
    {
        var existingProject = new Project { Name = "Existing" };
        var store = CreateDataStore(existingProject);
        var vm = new MainViewModel(store, new CopilotService(), new UpdateService());

        var createdProject = new Project { Name = "Created From Chat" };
        store.Data.Projects.Add(createdProject);

        vm.ChatVM.RaiseFeatureManagementStateChangedForTest();

        Assert.Contains(vm.Projects, project => project.Id == createdProject.Id);
        Assert.Contains(vm.ProjectsVM.Projects, project => project.Id == createdProject.Id);
    }

    [Fact]
    public void FeatureManagementStateChanged_FromNonDisplayedSurface_RefreshesProjectCollections()
    {
        var store = CreateDataStore();
        using var registry = new ChatSurfaceRegistry();
        using var sessionStore = new ChatSessionStore(store, new CopilotService(), registry);
        var vm = new MainViewModel(
            store,
            new CopilotService(),
            new UpdateService(),
            chatSurfaceRegistry: registry,
            chatSessionStore: sessionStore);

        // Simulate a chat surface that is not the one currently displayed in the
        // main window — e.g. a chat the user navigated away from while it was still
        // running, or a background-job chat. The management tool executes on this
        // surface, not on vm.ChatVM.
        var backgroundSurface = sessionStore.AcquireDraft(null);
        Assert.NotSame(vm.ChatVM, backgroundSurface);

        var createdProject = new Project { Name = "Created From Background Chat" };
        store.Data.Projects.Add(createdProject);

        backgroundSurface.RaiseFeatureManagementStateChangedForTest();

        Assert.Contains(vm.Projects, project => project.Id == createdProject.Id);
        Assert.Contains(vm.ProjectsVM.Projects, project => project.Id == createdProject.Id);
    }

    [Fact]
    public void FeatureManagementStateChanged_ClearsDeletedProjectFilter()
    {
        var project = new Project { Name = "Temporary Project" };
        var store = CreateDataStore(project);
        var vm = new MainViewModel(store, new CopilotService(), new UpdateService());

        vm.SelectedProjectFilter = project.Id;
        store.Data.Projects.Remove(project);

        vm.ChatVM.RaiseFeatureManagementStateChangedForTest();

        Assert.Null(vm.SelectedProjectFilter);
        Assert.DoesNotContain(vm.Projects, item => item.Id == project.Id);
        Assert.DoesNotContain(vm.ProjectsVM.Projects, item => item.Id == project.Id);
    }
}
