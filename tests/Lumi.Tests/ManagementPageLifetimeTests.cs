using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ManagementPageLifetimeTests
{
    [Theory]
    [InlineData(1, "PageJobsHost")]
    [InlineData(2, "PageProjectsHost")]
    [InlineData(3, "PageSkillsHost")]
    [InlineData(4, "PageAgentsHost")]
    [InlineData(5, "PageMemoriesHost")]
    [InlineData(6, "PageMcpServersHost")]
    [InlineData(8, "PageLibraryHost")]
    [InlineData(9, "PageDiscoverHost")]
    public async Task LeavingAPageReleasesItsViewWhileKeepingItsModel(int index, string hostName)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            using var main = CreateMain();
            var window = Show(main);
            try
            {
                main.SelectedNavIndex = index;
                Pump(window);
                var model = PageModel(window, hostName);
                Assert.NotNull(model);
                var released = LeavePage(window, main, hostName);
                Assert.Null(Host(window, hostName).Content);
                main.SelectedNavIndex = index;
                Pump(window);
                Assert.Same(model, Assert.IsAssignableFrom<Control>(Host(window, hostName).Content).DataContext);
                main.SelectedNavIndex = 0;
                Pump(window);
                await CollectAsync();
                Assert.All(released, reference => Assert.False(reference.IsAlive));
                GC.KeepAlive(main);
                GC.KeepAlive(window);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(1, "PageJobsHost", "JobNameInput")]
    [InlineData(2, "PageProjectsHost", "ProjectNameInput")]
    [InlineData(3, "PageSkillsHost", "SkillNameInput")]
    [InlineData(4, "PageAgentsHost", "LumiNameInput")]
    [InlineData(5, "PageMemoriesHost", "MemoryKeyInput")]
    [InlineData(6, "PageMcpServersHost", "McpNameInput")]
    public async Task ReopeningAnEditorKeepsItsSelectionAndDraft(int index, string hostName, string fieldName)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var main = CreateMain();
            var window = Show(main);
            try
            {
                main.SelectedNavIndex = index;
                SelectDetail(main, index);
                Pump(window);
                var field = Page(window, hostName).FindControl<TextBox>(fieldName)!;
                Assert.NotNull(field);
                field.Text = "Unsaved draft";
                Pump(window);
                main.SelectedNavIndex = 0;
                Pump(window);
                Assert.Null(Host(window, hostName).Content);
                main.SelectedNavIndex = index;
                Pump(window);
                Assert.Equal("Unsaved draft", Page(window, hostName).FindControl<TextBox>(fieldName)!.Text);
                Assert.NotNull(SelectedItem(main, index));
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(1, "PageJobsHost", "JobNameInput")]
    [InlineData(2, "PageProjectsHost", "ProjectNameInput")]
    [InlineData(3, "PageSkillsHost", "SkillNameInput")]
    [InlineData(4, "PageAgentsHost", "LumiNameInput")]
    [InlineData(5, "PageMemoriesHost", "MemoryKeyInput")]
    [InlineData(6, "PageMcpServersHost", "McpNameInput")]
    public async Task ANewUnsavedItemSurvivesLeavingAndReopening(int index, string hostName, string fieldName)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var main = CreateMain();
            var window = Show(main);
            try
            {
                main.SelectedNavIndex = index;
                switch (index)
                {
                    case 1: main.JobsVM.NewJobCommand.Execute(null); break;
                    case 2: main.ProjectsVM.NewProjectCommand.Execute(null); break;
                    case 3: main.SkillsVM.NewSkillCommand.Execute(null); break;
                    case 4: main.AgentsVM.NewAgentCommand.Execute(null); break;
                    case 5: main.MemoriesVM.NewMemoryCommand.Execute(null); break;
                    case 6: main.McpServersVM.NewServerCommand.Execute(null); break;
                }
                Pump(window);
                Page(window, hostName).FindControl<TextBox>(fieldName)!.Text = "New unsaved item";
                Pump(window);
                main.SelectedNavIndex = 0;
                Pump(window);
                main.SelectedNavIndex = index;
                Pump(window);
                Assert.Equal("New unsaved item", Page(window, hostName).FindControl<TextBox>(fieldName)!.Text);
                Assert.Null(SelectedItem(main, index));
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReleasingTheJobEditorDoesNotChangeNumericOrTextBuffers()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var main = CreateMain();
            var window = Show(main);
            try
            {
                main.SelectedNavIndex = 1;
                main.JobsVM.SelectedJob = main.DataStore.Data.BackgroundJobs[0];
                main.JobsVM.IsEditing = true;
                main.JobsVM.EditName = "Unsent job edit";
                main.JobsVM.EditIntervalMinutes = 37;
                main.JobsVM.EditMonthlyDay = 23;
                Pump(window);
                main.SelectedNavIndex = 0;
                Pump(window);
                Assert.Equal("Unsent job edit", main.JobsVM.EditName);
                Assert.Equal(37, main.JobsVM.EditIntervalMinutes);
                Assert.Equal(23, main.JobsVM.EditMonthlyDay);
                Assert.True(main.JobsVM.IsEditing);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheSaveShortcutSavesAJobOnlyWhenItWasEdited()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var main = CreateMain();
            var chat = new Chat { Id = Guid.NewGuid(), Title = "Builds" };
            main.DataStore.Data.Chats.Add(chat);
            var job = main.DataStore.Data.BackgroundJobs[0];
            job.ChatId = chat.Id;
            job.Prompt = "Check the nightly build.";
            var stored = job.UpdatedAt;
            var window = Show(main);
            try
            {
                main.SelectedNavIndex = 1;
                main.JobsVM.SelectedJob = job;
                Pump(window);
                var page = Page(window, "PageJobsHost");

                // Saving moves a job's schedule anchor, so an unedited job is left as it is.
                PressSave(page);
                Assert.Equal(stored, job.UpdatedAt);
                Assert.False(main.JobsVM.ShowSavedToast);

                main.JobsVM.EditName = "Nightly build check";
                PressSave(page);
                Assert.Equal("Nightly build check", job.Name);
                Assert.False(main.JobsVM.HasUnsavedWork);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LibraryKeepsItsSelectionAndTwoWayFilters()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            using var main = CreateMain();
            main.DataStore.Data.Chats.Add(new Chat
            {
                Title = "Sources",
                Messages = [new ChatMessage
                {
                    Role = "assistant", Content = "A reference.",
                    Sources = [new SearchSource { Url = "https://example.com/reference", Title = "Reference" }]
                }]
            });
            await main.LibraryVM.RefreshCommand.ExecuteAsync(null);
            var window = Show(main);
            try
            {
                main.SelectedNavIndex = 8;
                main.LibraryVM.SelectedSortIndex = 1;
                main.LibraryVM.SelectedTimeRangeOption = main.LibraryVM.TimeRanges[1];
                var selected = Assert.Single(main.LibraryVM.Items);
                main.LibraryVM.SelectedItem = selected;
                Pump(window);
                main.SelectedNavIndex = 0;
                Pump(window);
                Assert.Same(selected, main.LibraryVM.SelectedItem);
                Assert.Equal(1, main.LibraryVM.SelectedSortIndex);
                Assert.Same(main.LibraryVM.TimeRanges[1], main.LibraryVM.SelectedTimeRangeOption);
                main.SelectedNavIndex = 8;
                Pump(window);
                Assert.Same(selected, Page(window, "PageLibraryHost").FindControl<ListBox>("LibraryList")!.SelectedItem);
                Assert.Equal(1, main.LibraryVM.SelectedSortIndex);
                Assert.Same(main.LibraryVM.TimeRanges[1], main.LibraryVM.SelectedTimeRangeOption);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task InactiveSidebarLetsGoOfRowsWithoutClearingTheSelection()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var main = CreateMain();
            var window = Show(main);
            try
            {
                var sidebar = window.FindControl<Panel>("SidebarProjects")!;
                var list = window.FindControl<ListBox>("ProjectsListBox")!;
                Assert.Null(sidebar.DataContext);
                Assert.Empty(list.Items);
                main.SelectedNavIndex = 2;
                SelectDetail(main, 2);
                main.ProjectsVM.SearchQuery = "Project";
                Pump(window);
                var selected = main.ProjectsVM.SelectedProject;
                Assert.Same(main.ProjectsVM, sidebar.DataContext);
                Assert.Same(selected, list.SelectedItem);
                Assert.NotEmpty(list.Items);
                main.SelectedNavIndex = 0;
                Pump(window);
                Assert.Null(sidebar.DataContext);
                Assert.Empty(list.Items);
                Assert.Same(selected, main.ProjectsVM.SelectedProject);
                Assert.Equal("Project", main.ProjectsVM.SearchQuery);
                main.SelectedNavIndex = 2;
                Pump(window);
                Assert.Same(selected, list.SelectedItem);
                Assert.Equal("Project", window.FindControl<TextBox>("ProjectsSidebarSearchBox")!.Text);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReopeningTheSameDetailRestoresScrollButAnotherItemStartsAtTheTop()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var main = CreateMain();
            var window = Show(main);
            try
            {
                main.SelectedNavIndex = 2;
                SelectDetail(main, 2);
                Pump(window);
                var scroll = Page(window, "PageProjectsHost").FindControl<ScrollViewer>("ProjectDetailScroll")!;
                scroll.Offset = new Vector(0, 250);
                Pump(window);
                var offset = scroll.Offset.Y;
                Assert.True(offset > 100);
                main.SelectedNavIndex = 0;
                Pump(window);
                main.SelectedNavIndex = 2;
                Pump(window);
                Assert.InRange(Page(window, "PageProjectsHost").FindControl<ScrollViewer>("ProjectDetailScroll")!.Offset.Y,
                    offset - 1, offset + 1);
                main.SelectedNavIndex = 0;
                Pump(window);
                main.ProjectsVM.SelectedProject = main.ProjectsVM.Projects[1];
                main.SelectedNavIndex = 2;
                Pump(window);
                Assert.Equal(0, Page(window, "PageProjectsHost").FindControl<ScrollViewer>("ProjectDetailScroll")!.Offset.Y);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AnInactivePageCannotBeConstructedBeforeNavigation()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var main = CreateMain();
            var window = Show(main);
            try
            {
                main.SelectedNavIndex = 2;
                Pump(window);
                typeof(MainWindow).GetMethod("EnsurePageViewLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [MainViewModel.LibraryNavIndex]);
                Pump(window);
                Assert.Equal(2, main.SelectedNavIndex);
                Assert.Null(Host(window, "PageLibraryHost").Content);
                Assert.Null(window.FindControl<Panel>("SidebarLibrary")!.DataContext);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    private static MainViewModel CreateMain()
    {
        Loc.Load("en");
        var instructions = string.Join('\n', Enumerable.Repeat("Keep the project instructions and the current draft.", 100));
        var data = new AppData
        {
            Settings = new UserSettings
            {
                IsOnboarded = true, AutoSaveChats = false, EnableMemoryAutoSave = false,
                EnableDailyBriefs = false, AllowAppSetupSuggestions = false, ShowAnimations = false,
            },
            Projects = [new Project { Name = "Project one", Instructions = instructions }, new Project { Name = "Project two", Instructions = instructions }],
            Skills = [new Skill { Name = "Skill", Content = "# Instructions" }],
            Agents = [new LumiAgent { Name = "Agent" }],
            Memories = [new Memory { Key = "Memory", Content = "A saved fact" }],
            McpServers = [new McpServer { Name = "Connector", Command = "example" }],
            BackgroundJobs = [new BackgroundJob { Name = "Watcher", IsEnabled = false }],
        };
        return new MainViewModel(new DataStore(data), TestCopilot.Shared, new UpdateService(),
            startBackgroundJobs: false, initializeCopilotOnStartup: false);
    }

    private static MainWindow Show(MainViewModel main)
    {
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 820 };
        window.Show();
        return window;
    }

    private static ContentControl Host(MainWindow window, string name) => window.FindControl<ContentControl>(name)!;
    private static Control Page(MainWindow window, string hostName) => (Control)Host(window, hostName).Content!;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object? PageModel(MainWindow window, string hostName) => Page(window, hostName).DataContext;

    private static void SelectDetail(MainViewModel main, int index)
    {
        switch (index)
        {
            case 1: main.JobsVM.SelectedJob = main.JobsVM.Jobs[0]; break;
            case 2: main.ProjectsVM.SelectedProject = main.ProjectsVM.Projects[0]; break;
            case 3: main.SkillsVM.SelectedSkill = main.SkillsVM.Skills[0]; break;
            case 4: main.AgentsVM.SelectedAgent = main.AgentsVM.Agents[0]; break;
            case 5: main.MemoriesVM.SelectedMemory = main.MemoriesVM.Memories[0]; break;
            case 6: main.McpServersVM.SelectedServer = main.McpServersVM.Servers[0]; break;
        }
    }

    private static object? SelectedItem(MainViewModel main, int index) => index switch
    {
        1 => main.JobsVM.SelectedJob,
        2 => main.ProjectsVM.SelectedProject, 3 => main.SkillsVM.SelectedSkill,
        4 => main.AgentsVM.SelectedAgent, 5 => main.MemoriesVM.SelectedMemory,
        6 => main.McpServersVM.SelectedServer, _ => null
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] LeavePage(MainWindow window, MainViewModel main, string hostName)
    {
        var page = Page(window, hostName);
        var child = page.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var references = child is null ? new[] { new WeakReference(page) } : [new WeakReference(page), new WeakReference(child)];
        main.SelectedNavIndex = 0;
        Pump(window);
        return references;
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void PressSave(Control page) => page.RaiseEvent(new KeyEventArgs
    {
        RoutedEvent = InputElement.KeyDownEvent,
        Key = Key.S,
        KeyModifiers = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control
    });

    private static async Task CollectAsync()
    {
        for (var pass = 0; pass < 8; pass++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(40);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
