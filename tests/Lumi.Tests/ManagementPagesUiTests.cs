using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ManagementPagesUiTests
{
    [Fact]
    public async Task EveryManagementPage_OpensItsOverviewAndDetail_AndTheSidebarFollows()
    {
        using var session = HeadlessTestSession.Start();
        var skillsDirectory = Path.Combine(Path.GetTempPath(), $"lumi-management-ui-{Guid.NewGuid():N}");

        try
        {
            await session.Dispatch(async () =>
            {
                Loc.Load("en");
                var project = new Project { Name = "Lumi", Instructions = "Keep it simple." };
                var skill = new Skill { Name = "Tone", Description = "Friendly", Content = "# Tone\nBe kind." };
                var server = new McpServer { Name = "Files", Command = "npx", Args = ["-y", "@modelcontextprotocol/server-filesystem"] };
                var agent = new LumiAgent { Name = "Helper", SkillIds = [skill.Id], McpServerIds = [server.Id] };
                var memory = new Memory { Key = "Name", Content = "Adir", Category = "Personal" };
                var data = new AppData
                {
                    Settings = new UserSettings { IsOnboarded = true, AutoSaveChats = false, EnableMemoryAutoSave = false },
                    Projects = [project],
                    Skills = [skill],
                    Agents = [agent],
                    McpServers = [server],
                    Memories = [memory],
                    Chats = [new Chat { Title = "Planning", ProjectId = project.Id }]
                };

                using var vm = new MainViewModel(
                    new DataStore(data, skillsDirectory),
                    TestCopilot.Shared,
                    new UpdateService(),
                    startBackgroundJobs: false,
                    initializeCopilotOnStartup: false);
                var window = new MainWindow { DataContext = vm, Width = 1280, Height = 860 };
                window.Show();

                try
                {
                    async Task Settle()
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                        window.UpdateLayout();
                    }

                    T Named<T>(string name) where T : Control
                        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

                    async Task CheckPage(int nav, string overview, string detail, string list, object item, Action open, Action close, Func<object?> selected)
                    {
                        vm.SelectedNavIndex = nav;
                        await Settle();
                        Assert.True(Named<Panel>(overview).IsVisible, $"{overview} should show first");
                        Assert.False(Named<Panel>(detail).IsVisible);

                        open();
                        await Settle();
                        Assert.True(Named<Panel>(detail).IsVisible, $"{detail} should open");
                        Assert.Same(item, Named<ListBox>(list).SelectedItem);

                        close();
                        await Settle();
                        Assert.True(Named<Panel>(overview).IsVisible);
                        Assert.Null(Named<ListBox>(list).SelectedItem);

                        // Picking a row in the sidebar opens it, like clicking its card.
                        Named<ListBox>(list).SelectedItem = item;
                        await Settle();
                        Assert.Same(item, selected());
                        Assert.True(Named<Panel>(detail).IsVisible);
                    }

                    await CheckPage(2, "ProjectsOverview", "ProjectDetail", "ProjectsListBox", project,
                        () => vm.ProjectsVM.SelectedProject = project, () => vm.ProjectsVM.CloseDetailCommand.Execute(null),
                        () => vm.ProjectsVM.SelectedProject);
                    await CheckPage(3, "SkillsOverview", "SkillDetail", "SkillsListBox", skill,
                        () => vm.SkillsVM.SelectedSkill = skill, () => vm.SkillsVM.CloseDetailCommand.Execute(null),
                        () => vm.SkillsVM.SelectedSkill);
                    await CheckPage(4, "LumisOverview", "LumiDetail", "LumisListBox", agent,
                        () => vm.AgentsVM.SelectedAgent = agent, () => vm.AgentsVM.CloseDetailCommand.Execute(null),
                        () => vm.AgentsVM.SelectedAgent);
                    await CheckPage(5, "MemoriesOverview", "MemoryDetail", "MemoriesListBox", memory,
                        () => vm.MemoriesVM.SelectedMemory = memory, () => vm.MemoriesVM.CloseDetailCommand.Execute(null),
                        () => vm.MemoriesVM.SelectedMemory);
                    await CheckPage(6, "McpOverview", "McpDetail", "McpServersListBox", server,
                        () => vm.McpServersVM.SelectedServer = server, () => vm.McpServersVM.CloseDetailCommand.Execute(null),
                        () => vm.McpServersVM.SelectedServer);

                    // The MCP page opens on its servers, not the catalog, and the catalog is one click away.
                    vm.McpServersVM.BrowseCatalogCommand.Execute(null);
                    await Settle();
                    Assert.True(Named<Panel>("McpCatalog").IsVisible);
                    Assert.NotEmpty(vm.McpServersVM.CatalogEntries);

                    // Editing shows the save bar; saving keeps the skill open and hides it again.
                    vm.SelectedNavIndex = 3;
                    vm.SkillsVM.SelectedSkill = skill;
                    await Settle();
                    Assert.False(Named<Border>("SkillSaveBar").IsVisible);
                    vm.SkillsVM.EditDescription = "Warm and friendly";
                    await Settle();
                    Assert.True(Named<Border>("SkillSaveBar").IsVisible);
                    vm.SkillsVM.SaveSkillCommand.Execute(null);
                    await Settle();
                    Assert.False(Named<Border>("SkillSaveBar").IsVisible);
                    Assert.True(Named<Panel>("SkillDetail").IsVisible);
                    Assert.Equal("Warm and friendly", skill.Description);
                }
                finally
                {
                    window.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(skillsDirectory, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task ManagementCards_AreNotClippedWhenTheyLiftOnHover()
    {
        using var session = HeadlessTestSession.Start();
        var skillsDirectory = Path.Combine(Path.GetTempPath(), $"lumi-management-ui-{Guid.NewGuid():N}");

        try
        {
            await session.Dispatch(async () =>
            {
                Loc.Load("en");
                var data = new AppData
                {
                    Settings = new UserSettings { IsOnboarded = true, AutoSaveChats = false, EnableMemoryAutoSave = false },
                    Projects = [new Project { Name = "Lumi" }],
                    Skills = [new Skill { Name = "Tone" }],
                    Agents = [new LumiAgent { Name = "Helper" }],
                    McpServers = [new McpServer { Name = "Files", Command = "npx" }],
                    Memories = [new Memory { Key = "Name", Content = "Adir" }]
                };
                using var vm = new MainViewModel(
                    new DataStore(data, skillsDirectory),
                    TestCopilot.Shared,
                    new UpdateService(),
                    startBackgroundJobs: false,
                    initializeCopilotOnStartup: false);
                var window = new MainWindow { DataContext = vm, Width = 1280, Height = 860 };
                window.Show();

                try
                {
                    foreach (var nav in new[] { 2, 3, 4, 5, 6 })
                    {
                        vm.SelectedNavIndex = nav;
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                        window.UpdateLayout();

                        // A hovered card moves up and casts a shadow past its own bounds. Everything between
                        // it and the page's scroll viewport must let that draw, or its top edge is cut off.
                        var frame = window.GetVisualDescendants()
                            .OfType<Border>()
                            .First(border => border.Classes.Contains("mg-card-frame") && border.IsEffectivelyVisible);
                        var clipping = frame.GetVisualAncestors()
                            .TakeWhile(ancestor => ancestor is not Avalonia.Controls.Presenters.ScrollContentPresenter)
                            .OfType<Control>()
                            .Where(ancestor => ancestor.ClipToBounds)
                            .Select(ancestor => $"{ancestor.GetType().Name}{(string.IsNullOrEmpty(ancestor.Name) ? "" : "#" + ancestor.Name)}")
                            .ToList();

                        Assert.True(clipping.Count == 0, $"Page {nav}: cards are clipped by {string.Join(" > ", clipping)}");
                    }
                }
                finally
                {
                    window.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(skillsDirectory, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task ManagementActionsFitAtMinimumWindowWidthWithLongNames()
    {
        using var session = HeadlessTestSession.Start();
        var skillsDirectory = Path.Combine(Path.GetTempPath(), $"lumi-management-ui-{Guid.NewGuid():N}");

        try
        {
            await session.Dispatch(async () =>
            {
                Loc.Load("en");
                const string longName = "Document preparation and review with industry-standard instructions";
                var project = new Project { Name = longName };
                var skill = new Skill { Name = longName };
                var agent = new LumiAgent { Name = longName };
                var memory = new Memory { Key = longName };
                var server = new McpServer { Name = longName, Command = "npx" };
                var data = new AppData
                {
                    Settings = new UserSettings
                    {
                        IsOnboarded = true,
                        AutoSaveChats = false,
                        EnableMemoryAutoSave = false,
                        ShowAnimations = false,
                        WindowWidth = 800,
                        WindowHeight = 860
                    },
                    Projects = [project],
                    Skills = [skill],
                    Agents = [agent],
                    Memories = [memory],
                    McpServers = [server]
                };
                using var vm = new MainViewModel(
                    new DataStore(data, skillsDirectory),
                    TestCopilot.Shared,
                    new UpdateService(),
                    startBackgroundJobs: false,
                    initializeCopilotOnStartup: false);
                var window = new MainWindow { DataContext = vm, Width = 800, Height = 860 };
                window.Show();

                try
                {
                    var pages = new (int Nav, string Detail, Action Open, Action Edit, Action RequestDelete,
                        Action CancelDelete, IRelayCommand Save, IRelayCommand Confirm)[]
                    {
                        (2, "ProjectDetail", () => vm.ProjectsVM.SelectedProject = project,
                            () => vm.ProjectsVM.EditInstructions = "Changed",
                            () => vm.ProjectsVM.RequestDeleteCommand.Execute(null),
                            () => vm.ProjectsVM.CancelDeleteCommand.Execute(null),
                            vm.ProjectsVM.SaveProjectCommand, vm.ProjectsVM.ConfirmDeleteCommand),
                        (3, "SkillDetail", () => vm.SkillsVM.SelectedSkill = skill,
                            () => vm.SkillsVM.EditContent = "Changed",
                            () => vm.SkillsVM.RequestDeleteCommand.Execute(null),
                            () => vm.SkillsVM.CancelDeleteCommand.Execute(null),
                            vm.SkillsVM.SaveSkillCommand, vm.SkillsVM.ConfirmDeleteCommand),
                        (4, "LumiDetail", () => vm.AgentsVM.SelectedAgent = agent,
                            () => vm.AgentsVM.EditSystemPrompt = "Changed",
                            () => vm.AgentsVM.RequestDeleteCommand.Execute(null),
                            () => vm.AgentsVM.CancelDeleteCommand.Execute(null),
                            vm.AgentsVM.SaveAgentCommand, vm.AgentsVM.ConfirmDeleteCommand),
                        (5, "MemoryDetail", () => vm.MemoriesVM.SelectedMemory = memory,
                            () => vm.MemoriesVM.EditContent = "Changed",
                            () => vm.MemoriesVM.RequestDeleteCommand.Execute(null),
                            () => vm.MemoriesVM.CancelDeleteCommand.Execute(null),
                            vm.MemoriesVM.SaveMemoryCommand, vm.MemoriesVM.ConfirmDeleteCommand),
                        (6, "McpDetail", () => vm.McpServersVM.SelectedServer = server,
                            () => vm.McpServersVM.EditDescription = "Changed",
                            () => vm.McpServersVM.RequestDeleteCommand.Execute(null),
                            () => vm.McpServersVM.CancelDeleteCommand.Execute(null),
                            vm.McpServersVM.SaveServerCommand, vm.McpServersVM.ConfirmDeleteCommand)
                    };

                    foreach (var page in pages)
                    {
                        vm.SelectedNavIndex = page.Nav;
                        page.Open();
                        page.RequestDelete();
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                        window.UpdateLayout();

                        var detail = window.GetVisualDescendants().OfType<Panel>()
                            .Single(panel => panel.Name == page.Detail);
                        Assert.True(detail.Bounds.Width <= 520,
                            $"The narrow-window case was not reached: window {window.Bounds.Width}, page {detail.Bounds.Width}.");
                        var confirmation = detail.GetVisualDescendants().OfType<Border>()
                            .Single(border => border.Classes.Contains("danger") && border.IsEffectivelyVisible);
                        var confirmationButtons = confirmation.GetVisualDescendants().OfType<Button>().ToArray();
                        Assert.Equal(2, confirmationButtons.Length);
                        Assert.Contains(confirmationButtons, button => ReferenceEquals(button.Command, page.Confirm));
                        foreach (var button in confirmationButtons)
                            AssertButtonFits(button, detail);

                        page.CancelDelete();
                        page.Edit();
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                        window.UpdateLayout();

                        var save = detail.GetVisualDescendants().OfType<Button>()
                            .Single(button => ReferenceEquals(button.Command, page.Save) && button.IsEffectivelyVisible);
                        AssertButtonFits(save, detail);
                    }
                }
                finally
                {
                    window.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(skillsDirectory, recursive: true); }
            catch { }
        }
    }

    private static void AssertButtonFits(Button button, Control page)
    {
        var offset = button.TranslatePoint(default, page);
        Assert.NotNull(offset);
        Assert.True(button.Bounds.Width > 0);
        Assert.True(offset.Value.X >= 0 && offset.Value.X + button.Bounds.Width <= page.Bounds.Width,
            $"{button.Content} is outside {page.Name}: {offset.Value.X}..{offset.Value.X + button.Bounds.Width}, page width {page.Bounds.Width}.");
    }

    [Fact]
    public async Task StartChatFromALumi_OpensANewChatWithIt_AndNewChatFromAProjectJoinsIt()
    {
        using var session = HeadlessTestSession.Start();
        var skillsDirectory = Path.Combine(Path.GetTempPath(), $"lumi-management-ui-{Guid.NewGuid():N}");

        try
        {
            await session.Dispatch(() =>
            {
                Loc.Load("en");
                var firstAgent = new LumiAgent { Name = "Planner", SystemPrompt = "The first planner" };
                var agent = new LumiAgent { Name = "Planner", Description = "Plans the day", SystemPrompt = "The requested planner" };
                var project = new Project { Name = "Lumi" };
                var data = new AppData
                {
                    Settings = new UserSettings { IsOnboarded = true, AutoSaveChats = false, EnableMemoryAutoSave = false },
                    Agents = [firstAgent, agent],
                    Projects = [project]
                };
                using var vm = new MainViewModel(
                    new DataStore(data, skillsDirectory),
                    TestCopilot.Shared,
                    new UpdateService(),
                    startBackgroundJobs: false,
                    initializeCopilotOnStartup: false);

                vm.SelectedNavIndex = 4;
                vm.AgentsVM.SelectedAgent = agent;
                vm.AgentsVM.StartChatCommand.Execute(null);

                Assert.Equal(0, vm.SelectedNavIndex);
                Assert.Same(agent, vm.ChatVM.ActiveAgent);

                vm.SelectedNavIndex = 2;
                vm.ProjectsVM.ProjectCards.Single().NewChatCommand.Execute(null);

                Assert.Equal(0, vm.SelectedNavIndex);
                Assert.Equal(project.Id, vm.SelectedProjectFilter);
            }, CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(skillsDirectory, recursive: true); }
            catch { }
        }
    }
}
