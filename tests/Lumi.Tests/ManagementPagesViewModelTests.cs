using System;
using System.IO;
using System.Linq;
using Avalonia.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views.Controls;
using Lumi.Views.Management;
using Xunit;

namespace Lumi.Tests;

/// <summary>Editing behaviour of the redesigned Projects, Skills, Lumis, Memories and MCP pages.</summary>
public sealed class ManagementPagesViewModelTests : IDisposable
{
    // Saving or deleting a skill mirrors skills to disk, so every store writes to its own folder.
    private readonly string _skillsDirectory = Path.Combine(Path.GetTempPath(), $"lumi-management-tests-{Guid.NewGuid():N}");

    public ManagementPagesViewModelTests() => Loc.Load("en");

    public void Dispose()
    {
        try { Directory.Delete(_skillsDirectory, recursive: true); }
        catch { }
    }

    private DataStore Store(AppData data) => new(data, _skillsDirectory);

    [Fact]
    public void Skills_EditsShowTheSaveBar_AndDiscardRestoresWhatIsStored()
    {
        var skill = new Skill { Name = "Tone", Description = "Friendly", Content = "Be kind." };
        var vm = new SkillsViewModel(Store(new AppData { Skills = [skill] }));

        vm.SelectedSkill = skill;
        Assert.True(vm.IsEditing);
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.ShowSaveBar);

        vm.EditContent = "Be kind, and brief.";
        Assert.True(vm.HasUnsavedChanges);
        Assert.True(vm.ShowSaveBar);

        vm.DiscardChangesCommand.Execute(null);
        Assert.Equal("Be kind.", vm.EditContent);
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.ShowSaveBar);
        Assert.Equal("Be kind.", skill.Content);
    }

    [Fact]
    public void Skills_SwitchingItemsOrStartingANewDraftResetsDirtyState()
    {
        var first = new Skill { Name = "Tone", Content = "Be kind." };
        var second = new Skill { Name = "Citations", Content = "Cite sources." };
        var vm = new SkillsViewModel(Store(new AppData { Skills = [first, second] }));
        vm.SelectedSkill = first;
        vm.EditContent = "An unsaved edit";

        vm.SelectedSkill = second;

        Assert.Equal(second.Content, vm.EditContent);
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.ShowSaveBar);

        vm.EditContent = "Another unsaved edit";
        vm.NewSkillCommand.Execute(null);

        Assert.True(vm.IsNewSkill);
        Assert.Equal("", vm.EditContent);
        Assert.False(vm.HasUnsavedChanges);
        Assert.True(vm.ShowSaveBar);
    }

    [Fact]
    public void Skills_SaveKeepsTheSkillOpen()
    {
        var skill = new Skill { Name = "Tone", Content = "Be kind." };
        var vm = new SkillsViewModel(Store(new AppData { Skills = [skill] }));
        vm.SelectedSkill = skill;

        vm.EditName = "Tone of voice";
        vm.SaveSkillCommand.Execute(null);

        Assert.Equal("Tone of voice", skill.Name);
        Assert.True(vm.IsEditing);
        Assert.Same(skill, vm.SelectedSkill);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Contains(vm.SkillCards, card => card.Model == skill && card.Name == "Tone of voice");
    }

    [Fact]
    public void Skills_NewSkillNeedsAName_AndStaysOpenOnceCreated()
    {
        var store = Store(new AppData());
        var vm = new SkillsViewModel(store);

        vm.NewSkillCommand.Execute(null);
        Assert.True(vm.IsNewSkill);
        Assert.True(vm.ShowSaveBar);
        Assert.False(vm.CanSave);

        vm.EditName = "Standup notes";
        vm.EditContent = "Summarise.";
        vm.SaveSkillCommand.Execute(null);

        var created = Assert.Single(store.Data.Skills);
        Assert.Same(created, vm.SelectedSkill);
        Assert.False(vm.IsNewSkill);
        Assert.False(vm.ShowSaveBar);
    }

    [Fact]
    public void Skills_DeleteWaitsForConfirmation()
    {
        var skill = new Skill { Name = "Tone" };
        var store = Store(new AppData { Skills = [skill] });
        var vm = new SkillsViewModel(store);
        vm.SelectedSkill = skill;

        vm.RequestDeleteCommand.Execute(null);
        Assert.True(vm.IsConfirmingDelete);
        Assert.Contains(skill, store.Data.Skills);

        vm.CancelDeleteCommand.Execute(null);
        Assert.False(vm.IsConfirmingDelete);
        Assert.Contains(skill, store.Data.Skills);

        vm.RequestDeleteCommand.Execute(null);
        vm.ConfirmDeleteCommand.Execute(null);
        Assert.DoesNotContain(skill, store.Data.Skills);
        Assert.False(vm.IsEditing);
        Assert.Null(vm.SelectedSkill);
    }

    [Fact]
    public void Skills_ShowWhichLumisUseThem()
    {
        var skill = new Skill { Name = "Citations" };
        var agent = new LumiAgent { Name = "Researcher", IconGlyph = "🔬", SkillIds = [skill.Id] };
        var vm = new SkillsViewModel(Store(new AppData { Skills = [skill], Agents = [agent] }));
        Guid? opened = null;
        vm.OpenAgentRequested += id => opened = id;

        var card = Assert.Single(vm.SkillCards);
        Assert.Equal(1, card.UsedByCount);

        vm.SelectedSkill = skill;
        var usedBy = Assert.Single(vm.UsedByAgents);
        Assert.Equal("Researcher", usedBy.Name);

        vm.OpenAgentCommand.Execute(usedBy);
        Assert.Equal(agent.Id, opened);
    }

    [Fact]
    public void Skills_LongInstructionsStartFolded()
    {
        var skill = new Skill { Name = "Long", Content = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"- step {i}")) };
        var vm = new SkillsViewModel(Store(new AppData { Skills = [skill] }));

        vm.SelectedSkill = skill;
        Assert.True(vm.IsPreviewingContent);
        Assert.True(vm.IsPreviewLong);
        Assert.True(vm.IsPreviewClipped);

        vm.TogglePreviewExpandedCommand.Execute(null);
        Assert.False(vm.IsPreviewClipped);
    }

    [Fact]
    public void Lumis_ChoiceChangesAreUnsavedEdits_AndDiscardRestoresThem()
    {
        var skill = new Skill { Name = "Citations" };
        var agent = new LumiAgent { Name = "Researcher" };
        var vm = new AgentsViewModel(Store(new AppData { Skills = [skill], Agents = [agent] }));

        vm.SelectedAgent = agent;
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal("0 of 1", vm.SkillsCountText);

        vm.AvailableSkills.Single().IsSelected = true;
        Assert.True(vm.HasUnsavedChanges);
        Assert.Equal("1 of 1", vm.SkillsCountText);

        vm.DiscardChangesCommand.Execute(null);
        Assert.False(vm.AvailableSkills.Single().IsSelected);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void Lumis_ToolGroupsShareTheToolChoices()
    {
        var agent = new LumiAgent { Name = "Researcher" };
        var vm = new AgentsViewModel(Store(new AppData { Agents = [agent] }));
        vm.SelectedAgent = agent;

        var group = vm.ToolGroups.First(g => g.Tools.Count > 1);
        Assert.True(group.AllSelected);

        group.ToggleAllCommand.Execute(null);
        Assert.All(group.Tools, tool => Assert.False(tool.IsSelected));
        Assert.True(vm.HasUnsavedChanges);
        Assert.False(vm.AllToolsSelected);

        vm.ToggleAllToolsCommand.Execute(null);
        Assert.True(vm.AllToolsSelected);
    }

    [Fact]
    public void Lumis_StartChatAsksTheShellToOpenAChat()
    {
        var agent = new LumiAgent { Name = "Planner" };
        var vm = new AgentsViewModel(Store(new AppData { Agents = [agent] }));
        LumiAgent? requested = null;
        vm.ChatRequested += a => requested = a;

        vm.AgentCards.Single().ChatCommand.Execute(null);
        Assert.Same(agent, requested);

        requested = null;
        vm.SelectedAgent = agent;
        vm.StartChatCommand.Execute(null);
        Assert.Same(agent, requested);
    }

    [Fact]
    public void Projects_CountChatsAndPageTheChatList()
    {
        var project = new Project { Name = "Lumi" };
        var chats = Enumerable.Range(0, ProjectsViewModel.ChatPreviewCount + 2)
            .Select(i => new Chat { Title = $"Chat {i}", ProjectId = project.Id, UpdatedAt = DateTimeOffset.Now.AddMinutes(-i) })
            .ToList();
        var vm = new ProjectsViewModel(Store(new AppData { Projects = [project], Chats = chats }));

        Assert.Equal(chats.Count, project.ChatCount);
        Assert.Equal(chats.Count, vm.ProjectCards.Single().ChatCount);

        vm.SelectedProject = project;
        Assert.Equal(ProjectsViewModel.ChatPreviewCount, vm.VisibleProjectChats.Count);
        Assert.True(vm.HasMoreChats);
        Assert.Equal("Chat 0", vm.VisibleProjectChats[0].Title);

        vm.ToggleShowAllChatsCommand.Execute(null);
        Assert.Equal(chats.Count, vm.VisibleProjectChats.Count);
    }

    [Fact]
    public void Projects_ContextFoldersCanBeAddedAndRemoved()
    {
        var project = new Project { Name = "Lumi" };
        var vm = new ProjectsViewModel(Store(new AppData { Projects = [project] }));
        vm.SelectedProject = project;

        vm.NewContextFolderPath = Path.Combine(_skillsDirectory, "Shared", "Skills");
        vm.AddContextFolderCommand.Execute(null);
        var folder = Assert.Single(vm.ContextFolders);
        Assert.Equal("Skills", folder.Name);
        Assert.Equal("", vm.NewContextFolderPath);
        Assert.True(vm.HasUnsavedChanges);

        folder.RemoveCommand.Execute(null);
        Assert.Empty(vm.ContextFolders);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void Projects_UnaddedFolderInputBelongsToTheOpenProject()
    {
        var first = new Project { Name = "Lumi" };
        var second = new Project { Name = "Strata" };
        var vm = new ProjectsViewModel(Store(new AppData { Projects = [first, second] }));
        vm.SelectedProject = first;
        vm.NewContextFolderPath = Path.Combine(_skillsDirectory, "ForLumi");

        vm.SelectedProject = second;

        Assert.Equal("", vm.NewContextFolderPath);
        Assert.Empty(vm.ContextFolders);

        vm.NewContextFolderPath = Path.Combine(_skillsDirectory, "ForStrata");
        vm.DiscardChangesCommand.Execute(null);
        Assert.Equal("", vm.NewContextFolderPath);

        vm.NewContextFolderPath = Path.Combine(_skillsDirectory, "ForAnotherProject");
        vm.NewProjectCommand.Execute(null);
        Assert.Equal("", vm.NewContextFolderPath);
    }

    [Fact]
    public void Projects_NewChatAsksTheShellToStartOneInTheProject()
    {
        var project = new Project { Name = "Lumi" };
        var vm = new ProjectsViewModel(Store(new AppData { Projects = [project] }));
        Project? requested = null;
        vm.NewChatRequested += p => requested = p;

        vm.ProjectCards.Single().NewChatCommand.Execute(null);
        Assert.Same(project, requested);
    }

    [Fact]
    public void Memories_AreGroupedByCategory_AndCanBeFiltered()
    {
        var store = Store(new AppData
        {
            Memories =
            [
                new Memory { Key = "Name", Content = "Adir", Category = "Personal" },
                new Memory { Key = "Wife", Content = "Noy", Category = "Personal" },
                new Memory { Key = "Theme", Content = "Dark", Category = "Preferences" },
                new Memory { Key = "Old", Content = "Gone", Category = "Work", Status = MemoryStatuses.Archived }
            ]
        });
        var vm = new MemoriesViewModel(store);

        Assert.Equal(["Personal", "Preferences"], vm.MemoryGroups.Select(group => group.Category));
        Assert.Equal(["All", "Personal", "Preferences"], vm.CategoryFilters.Select(filter => filter.Label));
        Assert.Equal(3, vm.CategoryFilters[0].Count);

        vm.CategoryFilters[2].SelectCommand.Execute(null);
        Assert.Equal("Preferences", vm.CategoryFilter);
        var group = Assert.Single(vm.MemoryGroups);
        Assert.Equal("Theme", Assert.Single(group.Items).Key);
        Assert.True(vm.CategoryFilters[2].IsSelected);

        vm.CategoryFilters[0].SelectCommand.Execute(null);
        Assert.Null(vm.CategoryFilter);
        Assert.Equal(3, vm.Memories.Count);
    }

    [Fact]
    public void Memories_CategoryChoicesFollowTheEditor()
    {
        var memory = new Memory { Key = "Theme", Content = "Dark", Category = "Preferences" };
        var store = Store(new AppData
        {
            Memories = [memory, new Memory { Key = "Name", Content = "Adir", Category = "Personal" }]
        });
        var vm = new MemoriesViewModel(store);

        vm.SelectedMemory = memory;
        Assert.True(vm.CategorySuggestions.Single(option => option.Key == "Preferences").IsSelected);

        vm.CategorySuggestions.Single(option => option.Key == "Personal").SelectCommand.Execute(null);
        Assert.Equal("Personal", vm.EditCategory);
        Assert.True(vm.HasUnsavedChanges);
        Assert.False(vm.CategorySuggestions.Single(option => option.Key == "Preferences").IsSelected);
    }

    [Fact]
    public void Memories_RemovingTheFilteredCategoryReturnsToAll()
    {
        var personal = new Memory { Key = "Name", Content = "Adir", Category = "Personal" };
        var general = new Memory { Key = "Notes", Content = "A note", Category = " " };
        var store = Store(new AppData { Memories = [personal, general] });
        var vm = new MemoriesViewModel(store);
        vm.CategoryFilters.Single(option => option.Key == "Personal").SelectCommand.Execute(null);
        Assert.Single(vm.Memories);

        store.Data.Memories.Remove(personal);
        vm.RefreshFromStore();

        Assert.Null(vm.CategoryFilter);
        Assert.Same(general, Assert.Single(vm.Memories));
        Assert.Equal(MemoriesViewModel.DefaultCategory, Assert.Single(vm.MemoryGroups).Category);
        Assert.Equal(["", MemoriesViewModel.DefaultCategory], vm.CategoryFilters.Select(option => option.Key));
        Assert.True(vm.CategoryFilters[0].IsSelected);
    }

    [Fact]
    public void McpServers_PreviewWhatRuns_AndRequireWhatTheTypeNeeds()
    {
        var vm = new McpServersViewModel(Store(new AppData()));
        vm.NewServerCommand.Execute(null);

        vm.EditName = "Files";
        vm.EditCommand = "npx";
        vm.EditNpxPackage = "@modelcontextprotocol/server-filesystem";
        vm.EditServerArgs = @"C:\My Files";
        Assert.True(vm.IsNpxCommand);
        Assert.True(vm.CanSave);
        Assert.Equal(@"npx -y @modelcontextprotocol/server-filesystem ""C:\My Files""", vm.CommandPreview);

        vm.UseRemoteCommand.Execute(null);
        Assert.True(vm.IsRemote);
        Assert.False(vm.CanSave);

        vm.EditUrl = "https://example.com/mcp";
        Assert.True(vm.CanSave);
        Assert.Equal("https://example.com/mcp", vm.CommandPreview);
    }

    [Fact]
    public void McpServers_SaveKeepsTheNewServerOpen()
    {
        var store = Store(new AppData());
        var vm = new McpServersViewModel(store);
        vm.NewServerCommand.Execute(null);
        vm.EditName = "Time";
        vm.EditCommand = "uvx";
        vm.EditArgs = "mcp-server-time";

        vm.SaveServerCommand.Execute(null);

        var saved = Assert.Single(store.Data.McpServers);
        Assert.Same(saved, vm.SelectedServer);
        Assert.True(vm.IsEditing);
        Assert.False(vm.IsNewServer);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal("uvx mcp-server-time", McpServersViewModel.DescribeEndpoint(saved));
    }

    [Fact]
    public void McpServers_CardToggleTurnsTheServerOnAndOff()
    {
        var server = new McpServer { Name = "Files", Command = "npx", IsEnabled = true };
        var vm = new McpServersViewModel(Store(new AppData { McpServers = [server] }));
        var changes = 0;
        vm.McpConfigChanged += () => changes++;

        vm.ServerCards.Single().ToggleCommand.Execute(null);

        Assert.False(server.IsEnabled);
        Assert.False(vm.ServerCards.Single().IsEnabled);
        Assert.Equal("0", vm.EnabledCountText);
        Assert.Equal(1, changes);
    }

    [Theory]
    [InlineData(1000, 240, 12, 0, 10, 4)]
    [InlineData(200, 240, 12, 0, 10, 1)]
    [InlineData(1000, 240, 12, 2, 10, 2)]
    [InlineData(1000, 240, 12, 0, 0, 1)]
    [InlineData(double.PositiveInfinity, 240, 12, 0, 3, 3)]
    public void AdaptiveGrid_FitsAsManyColumnsAsTheWidthAllows(
        double width, double minItemWidth, double spacing, int maxColumns, int items, int expected)
    {
        Assert.Equal(expected, AdaptiveGridPanel.ComputeColumns(width, minItemWidth, spacing, maxColumns, items));
    }

    [Fact]
    public void PlainPreview_ReadsMarkdownAsProse()
    {
        const string markdown = "# Title\n\n- **Bold** item\n```\ncode\n```\nSee [the docs](https://example.com).";

        Assert.Equal("Title Bold item See the docs.", ManagementText.PlainPreview(markdown));
    }

    [Theory]
    [InlineData("Lumi", "L")]
    [InlineData("'quoted", "Q")]
    [InlineData("  ", "?")]
    [InlineData("יועץ", "י")]
    public void Initial_UsesTheFirstLetterOrDigit(string name, string expected)
    {
        Assert.Equal(expected, ManagementText.Initial(name));
    }

    [Fact]
    public void Escape_BacksOutOneStep_WithoutLosingEdits()
    {
        var cancelled = 0;
        var closed = 0;
        static KeyEventArgs Escape() => new() { Key = Key.Escape };
        bool Handle(bool isOpen, bool confirming, bool unsaved)
            => ManagementPage.TryHandleEscape(Escape(), isOpen, confirming, unsaved, () => cancelled++, () => closed++);

        Assert.True(Handle(isOpen: true, confirming: true, unsaved: true));
        Assert.Equal((1, 0), (cancelled, closed));

        Assert.False(Handle(isOpen: true, confirming: false, unsaved: true));
        Assert.Equal((1, 0), (cancelled, closed));

        Assert.True(Handle(isOpen: true, confirming: false, unsaved: false));
        Assert.Equal((1, 1), (cancelled, closed));

        Assert.False(Handle(isOpen: false, confirming: false, unsaved: false));
        Assert.Equal((1, 1), (cancelled, closed));
    }
}
