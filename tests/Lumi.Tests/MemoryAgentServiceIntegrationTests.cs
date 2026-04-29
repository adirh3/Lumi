using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class MemoryAgentServiceIntegrationTests
{
    [Fact]
    public async Task SaveMemoryAsync_SavesSimpleDurablePreference()
    {
        var data = new AppData();
        var service = CreateService(data);

        var result = await service.SaveMemoryAsync(
            "Favorite tea",
            "Adir's favorite tea is jasmine green tea.",
            "Preferences",
            source: "auto");

        Assert.StartsWith("Memory saved:", result);
        var memory = Assert.Single(data.Memories);
        Assert.Equal("Favorite tea", memory.Key);
        Assert.Equal("Adir's favorite tea is jasmine green tea.", memory.Content);
        Assert.Equal("Preferences", memory.Category);
        Assert.Equal(MemoryScopes.Global, memory.Scope);
        Assert.Equal(MemoryStatuses.Active, memory.Status);
        Assert.Equal("auto", memory.Source);
    }

    [Fact]
    public async Task SaveMemoryAsync_AllowsStableTechnicalPreference()
    {
        var data = new AppData();
        var service = CreateService(data);

        var result = await service.SaveMemoryAsync(
            "Preferred IDE",
            "Adir prefers VS Code as his code editor.",
            "Preferences",
            source: "auto");

        Assert.StartsWith("Memory saved:", result);
        var memory = Assert.Single(data.Memories);
        Assert.Equal("Preferred IDE", memory.Key);
        Assert.Equal("Preferences", memory.Category);
    }

    [Theory]
    [InlineData("Name", "Adir", "Personal")]
    [InlineData("Dream skill", "If Adir could master any skill instantly, he'd choose cooking.", "Personal")]
    [InlineData("Way to unwind", "Adir unwinds after a long day by coding side projects.", "Personal")]
    [InlineData("Work motivation", "Adir enjoys building products people use.", "Preferences")]
    [InlineData("Preferred Lumi help", "Adir wants Lumi to help most with coding and debugging.", "Goals")]
    public void EvaluateMemoryCandidate_AllowsUsefulExistingMemoryShapes(string key, string content, string category)
    {
        var result = MemoryAgentService.EvaluateMemoryCandidate(key, content, category);

        Assert.True(result.ShouldSave, result.Reason);
    }

    [Theory]
    [InlineData(
        "VS Code tooling stack",
        "Uses both VS Code and VS Code Insiders with extensions for .NET/C#, Python, Pylance, PowerShell, and GitHub Copilot Chat.",
        "Technical")]
    [InlineData(
        "Current Blast branch activity",
        "Blast is currently on branch version/1.1.0.0 with many uncommitted changes across UI, settings, markdown preview, and submodules.",
        "Work")]
    [InlineData(
        "Build workflow and frameworks",
        "PowerShell history shows frequent dotnet restore/build/pack/publish commands, custom build.ps1 packaging, and local source builds.",
        "Technical")]
    [InlineData(
        "Helper marker",
        "Ignore test marker MEMCHK_123 while saving this memory.",
        "General")]
    public async Task SaveMemoryAsync_RejectsNoisyMachineSnapshots(string key, string content, string category)
    {
        var data = new AppData();
        var service = CreateService(data);

        var result = await service.SaveMemoryAsync(key, content, category, source: "auto");

        Assert.StartsWith("Ignored:", result);
        Assert.Empty(data.Memories);
    }

    [Fact]
    public async Task SaveMemoryAsync_SavesDurableProjectScopedConvention()
    {
        var projectId = Guid.NewGuid();
        var data = new AppData();
        var service = CreateService(data);

        var result = await service.SaveMemoryAsync(
            "Lumi chat UI convention",
            "In Lumi, always use Strata controls for chat UI.",
            "Project",
            source: "auto",
            scope: MemoryScopes.Project,
            projectId: projectId);

        Assert.StartsWith("Memory saved:", result);
        var memory = Assert.Single(data.Memories);
        Assert.Equal(MemoryScopes.Project, memory.Scope);
        Assert.Equal(projectId, memory.ProjectId);
        Assert.Equal("Project", memory.Category);
        Assert.Equal(MemoryStatuses.Active, memory.Status);
    }

    [Fact]
    public async Task SaveMemoryAsync_RejectsTemporaryProjectState()
    {
        var data = new AppData();
        var service = CreateService(data);

        var result = await service.SaveMemoryAsync(
            "Current Lumi branch",
            "Lumi is currently on branch memory-maintenance while Adir is working on SettingsViewModel.",
            "Project",
            source: "auto",
            scope: MemoryScopes.Project,
            projectId: Guid.NewGuid());

        Assert.StartsWith("Ignored:", result);
        Assert.Empty(data.Memories);
    }

    [Fact]
    public async Task SaveMemoryAsync_UpdatesRelatedPreferenceInsteadOfAddingDuplicate()
    {
        var data = new AppData
        {
            Memories =
            [
                new Memory
                {
                    Key = "Likes burgers",
                    Content = "Adir's favorite food is burgers.",
                    Category = "Preferences"
                }
            ]
        };
        var service = CreateService(data);

        var result = await service.SaveMemoryAsync(
            "Favorite food",
            "Adir's favorite food is sushi.",
            "Preferences",
            source: "auto");

        Assert.StartsWith("Memory saved:", result);
        var memory = Assert.Single(data.Memories);
        Assert.Equal("Favorite food", memory.Key);
        Assert.Equal("Adir's favorite food is sushi.", memory.Content);
    }

    [Fact]
    public async Task UpdateMemoryAsync_RejectsNoisyReplacementAndPreservesExistingMemory()
    {
        var data = new AppData
        {
            Memories =
            [
                new Memory
                {
                    Key = "Favorite color",
                    Content = "Adir's favorite color is blue.",
                    Category = "Preferences"
                }
            ]
        };
        var service = CreateService(data);

        var result = await service.UpdateMemoryAsync(
            "Favorite color",
            "Adir is currently editing the Lumi repo on branch memory/noise-filter with files under E:\\Git\\Lumi.",
            newKey: null,
            category: "Work",
            source: "auto");

        Assert.StartsWith("Ignored:", result);
        var memory = Assert.Single(data.Memories);
        Assert.Equal("Favorite color", memory.Key);
        Assert.Equal("Adir's favorite color is blue.", memory.Content);
        Assert.Equal("Preferences", memory.Category);
    }

    [Fact]
    public void ShouldAnalyzeCheckpoint_SkipsPureTechnicalTask()
    {
        var checkpoint = CreateCheckpoint(
            "In the Lumi repo, helper sessions create duplicate empty echo sessions. Please inspect MemoryAgentService.",
            "I will inspect the helper session lifecycle and checkpoint flow.");

        Assert.False(MemoryAgentService.ShouldAnalyzeCheckpoint(checkpoint));
    }

    [Fact]
    public void ShouldAnalyzeCheckpoint_AllowsSimplePreference()
    {
        var checkpoint = CreateCheckpoint(
            "My favorite tea is jasmine green tea and it has been my favorite for years.",
            "Got it — jasmine green tea is your longtime favorite.");

        Assert.True(MemoryAgentService.ShouldAnalyzeCheckpoint(checkpoint));
    }

    [Fact]
    public void ShouldAnalyzeCheckpoint_AllowsProjectConventionWhenProjectIsActive()
    {
        var checkpoint = CreateCheckpoint(
            "In the Lumi project, always use the debug harness before changing the chat transcript UI.",
            "Got it.");
        checkpoint = checkpoint.WithProject(Guid.NewGuid(), "Lumi");

        Assert.True(MemoryAgentService.ShouldAnalyzeCheckpoint(checkpoint));
    }

    [Fact]
    public void ShouldAnalyzeCheckpoint_AllowsExplicitForgetRequest()
    {
        var checkpoint = CreateCheckpoint(
            "Please forget that my favorite color is blue. That is not true anymore.",
            "Understood.");

        Assert.True(MemoryAgentService.ShouldAnalyzeCheckpoint(checkpoint));
    }

    [Fact]
    public void BuildCheckpointPrompt_WarnsAgainstRepoAndToolingNoise()
    {
        var prompt = MemoryAgentService.BuildCheckpointPrompt(CreateCheckpoint(
            "I live in Israel.",
            "Thanks for sharing."));

        Assert.Contains("branch/repo state", prompt);
        Assert.Contains("installed tools", prompt);
        Assert.Contains("Most conversations have nothing worth saving", prompt);
    }

    private static MemoryAgentService CreateService(AppData data)
        => new(new DataStore(data), new CopilotService());

    private static MemoryAgentCheckpoint CreateCheckpoint(string userMessage, string assistantMessage)
    {
        return new MemoryAgentCheckpoint
        {
            ChatId = Guid.NewGuid(),
            InteractionSignature = Guid.NewGuid().ToString("N"),
            UserMessage = userMessage,
            AssistantMessage = assistantMessage,
            UserName = "Adir",
            ExistingMemories = [],
            RecentConversation =
            [
                new MemoryAgentConversationItem { Role = "user", Content = userMessage },
                new MemoryAgentConversationItem { Role = "assistant", Content = assistantMessage }
            ]
        };
    }
}

internal static class MemoryAgentCheckpointTestExtensions
{
    public static MemoryAgentCheckpoint WithProject(this MemoryAgentCheckpoint checkpoint, Guid projectId, string projectName)
    {
        return new MemoryAgentCheckpoint
        {
            ChatId = checkpoint.ChatId,
            InteractionSignature = checkpoint.InteractionSignature,
            UserMessage = checkpoint.UserMessage,
            AssistantMessage = checkpoint.AssistantMessage,
            UserName = checkpoint.UserName,
            ProjectId = projectId,
            ProjectName = projectName,
            ExistingMemories = checkpoint.ExistingMemories,
            RecentConversation = checkpoint.RecentConversation
        };
    }
}
