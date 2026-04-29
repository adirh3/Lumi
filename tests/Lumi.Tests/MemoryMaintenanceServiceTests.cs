using Lumi.Models;
using Lumi.Services;
using Lumi.Localization;
using Lumi.ViewModels;
using System.Reflection;
using Xunit;

namespace Lumi.Tests;

public sealed class MemoryMaintenanceServiceTests
{
    [Fact]
    public async Task RunAsync_ArchivesNoiseDeletesMarkersAndMergesDuplicates()
    {
        var data = new AppData
        {
            Memories =
            [
                new Memory
                {
                    Key = "Favorite food",
                    Content = "Adir's favorite food is burgers.",
                    Category = "Preferences",
                    UpdatedAt = DateTimeOffset.Now.AddDays(-2)
                },
                new Memory
                {
                    Key = "Likes burgers",
                    Content = "Adir's favorite food is sushi.",
                    Category = "Preferences",
                    UpdatedAt = DateTimeOffset.Now
                },
                new Memory
                {
                    Key = "Current Blast branch activity",
                    Content = "Blast is currently on branch version/1.1.0.0 with many uncommitted changes.",
                    Category = "Work"
                },
                new Memory
                {
                    Key = "Helper marker",
                    Content = "Ignore test marker MEMCHK_123 while saving this memory.",
                    Category = "General"
                }
            ]
        };

        var result = await new MemoryMaintenanceService(new DataStore(data)).RunAsync();

        Assert.Equal(4, result.Reviewed);
        Assert.Equal(1, result.Merged);
        Assert.Equal(1, result.Archived);
        Assert.Equal(1, result.Deleted);
        Assert.DoesNotContain(data.Memories, m => m.Key == "Helper marker");
        Assert.Contains(data.Memories, m => m.Key == "Current Blast branch activity" && m.Status == MemoryStatuses.Archived);
        var activeFood = Assert.Single(data.Memories, m => m.Status == MemoryStatuses.Active);
        Assert.Equal("Likes burgers", activeFood.Key);
        Assert.NotNull(activeFood.LastReviewedAt);
    }

    [Fact]
    public async Task RunAsync_RewritesOnboardingSelectionIntoDirectFact()
    {
        var data = new AppData
        {
            Memories =
            [
                new Memory
                {
                    Key = "Preferred Lumi help",
                    Content = "When asked what they'd like Lumi to help with most, Adir selected 'Coding and debugging'.",
                    Category = "Goals"
                }
            ]
        };

        var result = await new MemoryMaintenanceService(new DataStore(data)).RunAsync();

        Assert.Equal(1, result.Rewritten);
        var memory = Assert.Single(data.Memories);
        Assert.Equal("Adir wants Lumi to help most with coding and debugging.", memory.Content);
        Assert.Equal(MemoryStatuses.Active, memory.Status);
    }

    [Fact]
    public async Task AutomaticMaintenanceToggleOffPreventsCleanupAfterSave()
    {
        var data = new AppData
        {
            Settings = new UserSettings { EnableMemoryAutoMaintenance = false },
            Memories =
            [
                new Memory
                {
                    Key = "Current Blast branch activity",
                    Content = "Blast is currently on branch version/1.1.0.0 with many uncommitted changes.",
                    Category = "Work"
                }
            ]
        };
        var service = new MemoryAgentService(new DataStore(data), new CopilotService());

        await service.SaveMemoryAsync(
            "Favorite tea",
            "Adir's favorite tea is jasmine green tea.",
            "Preferences",
            source: "auto");

        Assert.Contains(data.Memories, m => m.Key == "Current Blast branch activity" && m.Status == MemoryStatuses.Active);
    }

    [Fact]
    public async Task AutomaticMaintenanceArchivesNoiseAfterSaveWhenEnabled()
    {
        var data = new AppData
        {
            Settings = new UserSettings { EnableMemoryAutoMaintenance = true },
            Memories =
            [
                new Memory
                {
                    Key = "Current Blast branch activity",
                    Content = "Blast is currently on branch version/1.1.0.0 with many uncommitted changes.",
                    Category = "Work"
                }
            ]
        };
        var service = new MemoryAgentService(new DataStore(data), new CopilotService());

        await service.SaveMemoryAsync(
            "Favorite tea",
            "Adir's favorite tea is jasmine green tea.",
            "Preferences",
            source: "auto");

        Assert.Contains(data.Memories, m => m.Key == "Current Blast branch activity" && m.Status == MemoryStatuses.Archived);
    }

    [Fact]
    public async Task SettingsCommandRunsManualMemoryCleanup()
    {
        Loc.Load("en");
        var data = new AppData
        {
            Memories =
            [
                new Memory
                {
                    Key = "Helper marker",
                    Content = "Ignore test marker MEMCHK_456 while saving this memory.",
                    Category = "General"
                }
            ]
        };
        var viewModel = new SettingsViewModel(
            new DataStore(data),
            new CopilotService(),
            new BrowserService(),
            new UpdateService());

        await viewModel.CleanUpMemoriesNowCommand.ExecuteAsync(null);

        Assert.Empty(data.Memories);
        Assert.Contains("Cleaned up 1 memories", viewModel.MemoryCleanupStatus);
        Assert.False(viewModel.IsMemoryCleanupRunning);
    }

    [Fact]
    public void ChatCheckpointIncludesOnlyActiveRelevantMemories()
    {
        var projectId = Guid.NewGuid();
        var otherProjectId = Guid.NewGuid();
        var chat = new Chat
        {
            ProjectId = projectId,
            Messages =
            [
                new ChatMessage { Role = "user", Content = "My favorite tea is jasmine green tea." },
                new ChatMessage { Role = "assistant", Content = "Got it." }
            ]
        };
        var data = new AppData
        {
            Projects = [new Project { Id = projectId, Name = "Lumi" }],
            Chats = [chat],
            Memories =
            [
                new Memory
                {
                    Key = "Favorite color",
                    Content = "Adir's favorite color is blue.",
                    Category = "Preferences"
                },
                new Memory
                {
                    Key = "Archived favorite food",
                    Content = "Adir's favorite food is burgers.",
                    Category = "Preferences",
                    Status = MemoryStatuses.Archived
                },
                new Memory
                {
                    Key = "Noisy branch",
                    Content = "Lumi is currently on branch memory-maintenance.",
                    Category = "Work"
                },
                new Memory
                {
                    Key = "Other project convention",
                    Content = "In OtherProject, always use feature folders.",
                    Category = "Project",
                    Scope = MemoryScopes.Project,
                    ProjectId = otherProjectId
                },
                new Memory
                {
                    Key = "Lumi UI convention",
                    Content = "In Lumi, always use Strata controls for chat UI.",
                    Category = "Project",
                    Scope = MemoryScopes.Project,
                    ProjectId = projectId
                }
            ]
        };
        var viewModel = new ChatViewModel(new DataStore(data), new CopilotService());
        var method = typeof(ChatViewModel).GetMethod(
            "CreateMemoryCheckpoint",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var checkpoint = Assert.IsType<MemoryAgentCheckpoint>(method!.Invoke(viewModel, [chat]));
        var keys = checkpoint.ExistingMemories.Select(m => m.Key).ToArray();

        Assert.Contains("Favorite color", keys);
        Assert.Contains("Lumi UI convention", keys);
        Assert.DoesNotContain("Archived favorite food", keys);
        Assert.DoesNotContain("Noisy branch", keys);
        Assert.DoesNotContain("Other project convention", keys);
    }
}
