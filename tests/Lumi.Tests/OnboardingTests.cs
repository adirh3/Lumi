using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Onboarding;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class OnboardingTests
{
    private static readonly string Projects = Path.Combine(Path.GetTempPath(), "lumi-onboarding-tests");

    private static readonly DiscoveredProject Lumi = new(
        "Lumi", Path.Combine(Projects, "Lumi"), DiscoveredProjectKind.Code, DateTimeOffset.Now.AddHours(-3),
        "C# · Avalonia", "adirh3/Lumi", "A personal agent for your desktop.");

    private static readonly DiscoveredProject Wedding = new(
        "Wedding", Path.Combine(Projects, "Documents", "Wedding"), DiscoveredProjectKind.Folder,
        DateTimeOffset.Now.AddDays(-2), DocumentCount: 8);

    private static readonly DiscoveredProject Old = new(
        "Old", Path.Combine(Projects, "Old"), DiscoveredProjectKind.Code, DateTimeOffset.Now.AddDays(-60), "Go");

    private static readonly OnboardingPersonalization Personalization = new(
        "You're building Lumi, and Krita says you draw too.",
        [
            new OnboardingQuestion(OnboardingSlots.Work, "What do you build?", ["Desktop apps", "Design", "Research"]),
            new OnboardingQuestion(OnboardingSlots.Help, "Where should I help most?", ["Code review", "Planning my day", "Writing & email"]),
            new OnboardingQuestion(OnboardingSlots.Interests, "And for fun?", ["Gaming", "Digital art", "Anime"]),
        ],
        [
            new OnboardingStarterIdea("Tour Lumi", "Walk me through how Lumi is organized.", "Lumi", "🧭"),
            new OnboardingStarterIdea("Plan my week", "Help me plan my week.", null, "🗓️"),
            new OnboardingStarterIdea("Revive Old", "Summarize where I left Old.", "Old", "🗂️"),
        ],
        IsPersonalized: true);

    [Fact]
    public async Task WalkthroughSetsUpProjectsRemembersAnswersAndStartsAFirstTask()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = new AppData();
            var discovery = new FakeDiscovery([Lumi, Wedding, Old]);
            var vm = CreateViewModel(data, discovery, withAi: true);
            OnboardingCompletion? completion = null;
            vm.OnboardingCompleted += result => completion = result;

            Assert.False(vm.ContinueCommand.CanExecute(null));
            vm.UserName = "Ada";
            vm.ChoosePronounCommand.Execute("she");
            await vm.ContinueCommand.ExecuteAsync(null);
            Assert.Equal(OnboardingStep.Capabilities, vm.Step);

            vm.SelectCapabilityCommand.Execute(vm.Capabilities[3]);
            Assert.Same(vm.Capabilities[3], vm.SelectedCapability);
            Assert.Single(vm.Capabilities, capability => capability.IsSelected);

            await vm.ContinueCommand.ExecuteAsync(null);
            await vm.ContinueCommand.ExecuteAsync(null);
            Assert.Equal(OnboardingStep.Workspace, vm.Step);
            Assert.True(vm.IsConsentPhase);
            Assert.Equal(Loc.Onboarding_LookAround, vm.PrimaryLabel);

            // Each source is opt-in: a source turned off is never read.
            vm.Sources.Single(source => source.Key == "sites").IsEnabled = false;
            await vm.ContinueCommand.ExecuteAsync(null);
            Assert.True(vm.IsResultsPhase);
            Assert.False(discovery.SitesRead);
            Assert.True(vm.Sources.Single(source => source.Key == "sites").IsSkipped);
            Assert.Equal(string.Format(Loc.Onboarding_FoundProjectsMany, 3), vm.Sources.Single(source => source.Key == "projects").Summary);
            Assert.Equal(new[] { "Lumi", "Wedding", "Old" }, vm.Candidates.Select(candidate => candidate.Name));
            Assert.Equal(new[] { true, true, false }, vm.Candidates.Select(candidate => candidate.IsSelected));
            Assert.Equal(string.Format(Loc.Onboarding_SetUpProjectsMany, 2), vm.PrimaryLabel);

            await vm.ContinueCommand.ExecuteAsync(null);
            Assert.Equal(OnboardingStep.KnowYou, vm.Step);
            Assert.Equal(2, data.Projects.Count);
            var lumi = Assert.Single(data.Projects, project => project.Name == "Lumi");
            Assert.Equal(Lumi.Path, lumi.WorkingDirectory);
            Assert.Contains("C# · Avalonia", lumi.Instructions);
            Assert.Contains("adirh3/Lumi", lumi.Instructions);
            Assert.Contains(data.Projects, project => project.Name == "Wedding" && project.WorkingDirectory == Wedding.Path);

            // Lumi opens the conversation with what it noticed, then asks.
            Assert.Equal(Personalization.Observation, vm.Conversation[0].Text);
            Assert.False(vm.ContinueCommand.CanExecute(null));
            foreach (var answer in new[] { "Desktop apps, Design", "Code review", "Gaming, Anime" })
            {
                var question = vm.Conversation.Last(item => item.IsQuestion && !item.IsAnswered);
                await vm.AnswerAsync(question, answer);
            }

            Assert.True(vm.IsConversationDone);
            Assert.Equal(3, vm.Conversation.Count(item => item.IsRemembered));
            Assert.Equal(3, data.Memories.Count(memory => memory.Source == "onboarding"));
            Assert.Contains(data.Memories, memory => memory.Content
                == string.Format(Loc.Onboarding_MemoryWork, "Ada", "desktop apps" + Loc.Onboarding_ListAnd + "design"));
            Assert.True(vm.Conversation[^1].IsLumi);

            await vm.ContinueCommand.ExecuteAsync(null);
            Assert.Equal(OnboardingStep.Ready, vm.Step);
            Assert.Equal(new[] { "Lumi", "Wedding" }, vm.ReadyProjects.Select(project => project.Name));
            Assert.Equal(3, vm.ReadyMemories.Count);
            Assert.Equal(3, vm.Starters.Count);
            Assert.Equal(lumi.Id, vm.Starters[0].ProjectId);
            Assert.DoesNotContain(vm.Starters, starter => starter.Title == "Revive Old");

            vm.ReadyMemories[2].ForgetCommand.Execute(null);
            Assert.Equal(2, vm.ReadyMemories.Count);
            Assert.Equal(2, data.Memories.Count);

            vm.Starters[0].StartCommand.Execute(null);
            Assert.NotNull(completion);
            Assert.Equal("Walk me through how Lumi is organized.", completion.StarterPrompt);
            Assert.Equal(lumi.Id, completion.StarterProjectId);
            Assert.True(data.Settings.IsOnboarded);
            Assert.Equal("Ada", data.Settings.UserName);
            Assert.Equal("female", data.Settings.UserSex);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SkippingEverythingWithoutAnAiStillFinishesCleanly()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = new AppData();
            var discovery = new FakeDiscovery([Lumi]);
            var vm = CreateViewModel(data, discovery, withAi: false);
            OnboardingCompletion? completion = null;
            vm.OnboardingCompleted += result => completion = result;

            vm.UserName = "Ada";
            for (var step = 0; step < 3; step++)
                await vm.ContinueCommand.ExecuteAsync(null);
            Assert.Equal(OnboardingStep.Workspace, vm.Step);
            Assert.Equal(Loc.Onboarding_ConnectMissing, vm.ConnectStatus);

            vm.SecondaryCommand.Execute(null);
            Assert.Equal(OnboardingStep.KnowYou, vm.Step);
            Assert.Equal(0, discovery.Scans);

            // Without an AI the conversation still happens, with Lumi's own questions.
            Assert.Equal(string.Format(Loc.Onboarding_ObservationDefault, "Ada"), vm.Conversation[0].Text);
            var question = vm.Conversation.Single(item => item.IsQuestion);
            Assert.Equal(Loc.Onboarding_QuestionWork, question.Text);

            vm.SecondaryCommand.Execute(null);
            Assert.Equal(OnboardingStep.Ready, vm.Step);
            Assert.True(question.IsExpired);
            Assert.False(vm.HasReadyProjects);
            Assert.False(vm.HasReadyMemories);
            Assert.False(vm.HasStarters);
            Assert.Equal(Loc.Onboarding_ReadyAiMissing, vm.AiStatus);

            await vm.ContinueCommand.ExecuteAsync(null);
            Assert.NotNull(completion);
            Assert.Null(completion.StarterPrompt);
            Assert.True(data.Settings.IsOnboarded);
            Assert.Null(data.Settings.UserSex);
            Assert.Empty(data.Projects);
            Assert.Empty(data.Memories);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task FoldersThatAreAlreadyProjectsAreNotAddedTwice()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = new AppData();
            data.Projects.Add(new Project { Name = "Mine", WorkingDirectory = Lumi.Path + Path.DirectorySeparatorChar });
            data.Projects.Add(new Project { Name = "Wedding", WorkingDirectory = Path.Combine(Projects, "Elsewhere") });
            var vm = CreateViewModel(data, new FakeDiscovery([Lumi, Wedding]), withAi: true);
            vm.UserName = "Ada";
            vm.CurrentStep = (int)OnboardingStep.Workspace;

            await vm.ContinueCommand.ExecuteAsync(null);
            var existing = vm.Candidates[0];
            Assert.True(existing.IsAdded);
            Assert.False(existing.CanToggle);
            existing.ToggleCommand.Execute(null);
            Assert.True(existing.IsSelected);
            Assert.Equal(Loc.Onboarding_SetUpProjectsOne, vm.PrimaryLabel);

            await vm.ContinueCommand.ExecuteAsync(null);
            Assert.Equal(3, data.Projects.Count);
            Assert.Single(data.Projects, project => project.WorkingDirectory?.TrimEnd(Path.DirectorySeparatorChar) == Lumi.Path);
            Assert.Contains(data.Projects, project => project.Name == "Wedding (Documents)" && project.WorkingDirectory == Wedding.Path);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChoosingAnotherLanguageAsksTheShellToRestartInIt()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var vm = CreateViewModel(new AppData(), new FakeDiscovery([]), withAi: false);
            var requested = new List<string>();
            vm.LanguageChangeRequested += requested.Add;
            var hebrew = Array.FindIndex(Loc.AvailableLanguages, language => language.Code == "he");
            var english = Array.FindIndex(Loc.AvailableLanguages, language => language.Code == "en");

            Assert.Equal(english, vm.LanguageIndex);
            vm.LanguageIndex = hebrew;
            vm.LanguageIndex = english;

            Assert.Equal(new[] { "he" }, requested);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheFirstWelcomeCannotBeClosed()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var vm = CreateViewModel(new AppData(), new FakeDiscovery([]), withAi: false);
            var dismissed = false;
            vm.Dismissed += () => dismissed = true;

            Assert.False(vm.IsReplay);
            Assert.True(vm.ShowLanguagePicker);
            vm.DismissCommand.Execute(null);
            Assert.False(dismissed);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReplayingTheWelcomeStartsFreshAndCanBeLeftEarly()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    IsOnboarded = true,
                    UserName = "Ada",
                    UserSex = "female",
                    IsDarkTheme = true,
                    MinimizeToTray = false,
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false,
                    ShowAnimations = false,
                },
            };
            using var shell = new MainViewModel(new DataStore(data), TestCopilot.Shared, new UpdateService(), startBackgroundJobs: false);
            var window = new MainWindow { DataContext = shell };
            try
            {
                window.Show();
                await PumpAsync();
                shell.SelectedNavIndex = 7;
                var original = shell.OnboardingVM;

                shell.SettingsVM.ReplayWelcomeCommand.Execute(null);
                await PumpAsync();

                var replay = shell.OnboardingVM;
                Assert.NotSame(original, replay);
                Assert.Null(original.LoginVM);
                Assert.False(shell.IsOnboarded);
                Assert.True(window.FindControl<Panel>("OnboardingPanel")!.IsVisible);
                var view = Assert.IsType<OnboardingView>(window.FindControl<ContentControl>("OnboardingHost")!.Content);
                Assert.Same(replay, view.DataContext);
                Assert.True(replay.IsReplay);
                Assert.Equal(OnboardingStep.Hello, replay.Step);
                Assert.Equal("Ada", replay.UserName);
                Assert.True(replay.IsShe);
                Assert.True(view.FindControl<Button>("OnboardingCloseButton")!.IsEffectivelyVisible);
                Assert.False(view.FindControl<ComboBox>("OnboardingLanguageCombo")!.IsEffectivelyVisible);

                // The theme switch in Settings follows a theme picked in the welcome.
                replay.ToggleThemeCommand.Execute(null);
                Assert.False(shell.SettingsVM.IsDarkTheme);
                Assert.False(shell.IsDarkTheme);
                Assert.False(data.Settings.IsDarkTheme);

                // Leaving early changes nothing that was not set up yet and returns to where the user was.
                replay.UserName = "Someone else";
                await replay.ContinueCommand.ExecuteAsync(null);
                replay.DismissCommand.Execute(null);
                await PumpAsync();
                Assert.True(shell.IsOnboarded);
                Assert.False(window.FindControl<Panel>("OnboardingPanel")!.IsVisible);
                Assert.Equal(7, shell.SelectedNavIndex);
                Assert.Equal("Ada", data.Settings.UserName);
                Assert.Equal("Ada", shell.UserName);

                // Every replay starts over, and finishing one leads to the chats.
                shell.SettingsVM.ReplayWelcomeCommand.Execute(null);
                await PumpAsync();
                var again = shell.OnboardingVM;
                Assert.NotSame(replay, again);
                Assert.Same(again, view.DataContext);
                Assert.Equal(OnboardingStep.Hello, again.Step);
                Assert.Equal("Ada", again.UserName);

                again.CurrentStep = (int)OnboardingStep.Ready;
                await again.ContinueCommand.ExecuteAsync(null);
                await PumpAsync();
                Assert.True(shell.IsOnboarded);
                Assert.True(data.Settings.IsOnboarded);
                Assert.Equal(0, shell.SelectedNavIndex);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EveryStepRendersAndQuestionCardsAnswerTheConversation()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var vm = CreateViewModel(new AppData(), new FakeDiscovery([Lumi, Wedding, Old]), withAi: true);
            var view = new OnboardingView { DataContext = vm };
            var window = new Window { Width = 1000, Height = 860, Content = view };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.True(view.FindControl<TextBox>("OnboardingNameBox")!.IsEffectivelyVisible);
                vm.UserName = "Ada";
                Assert.Equal(string.Format(Loc.Onboarding_NiceToMeet, "Ada"), view.FindControl<TextBlock>("NiceToMeetText")!.Text);

                await vm.ContinueCommand.ExecuteAsync(null);
                window.UpdateLayout();
                Assert.True(view.FindControl<Border>("CapabilityDemo")!.IsEffectivelyVisible);
                var steps = view.FindControl<ItemsControl>("DemoSteps")!;
                Assert.Equal(vm.Capabilities[0].Steps.Count, steps.ItemCount);
                vm.SelectCapabilityCommand.Execute(vm.Capabilities[3]);
                window.UpdateLayout();
                Assert.Equal(vm.Capabilities[3].Steps.Count, steps.ItemCount);

                await vm.ContinueCommand.ExecuteAsync(null);
                window.UpdateLayout();
                Assert.Contains("ready", view.FindControl<Border>("ConnectStatus")!.Classes);

                await vm.ContinueCommand.ExecuteAsync(null);
                window.UpdateLayout();
                Assert.True(view.FindControl<Border>("WorkspaceSources")!.IsEffectivelyVisible);
                Assert.True(view.FindControl<Grid>("WorkspacePrivacy")!.IsEffectivelyVisible);

                await vm.ContinueCommand.ExecuteAsync(null);
                window.UpdateLayout();
                var projects = view.FindControl<ItemsControl>("OnboardingProjectList")!;
                Assert.True(projects.IsEffectivelyVisible);
                Assert.Equal(3, projects.ItemCount);
                Assert.False(view.FindControl<Border>("WorkspaceSources")!.IsEffectivelyVisible);

                await vm.ContinueCommand.ExecuteAsync(null);
                window.UpdateLayout();
                var card = view.GetVisualDescendants().OfType<StrataQuestionCard>().Single(control => control.IsEffectivelyVisible);
                var options = card.GetVisualDescendants().OfType<Button>()
                    .Where(button => button.Classes.Contains("question-option"))
                    .ToList();
                options[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                options[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var confirm = card.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PART_MultiSubmit");
                Assert.True(confirm.IsVisible);
                confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();

                Assert.True(card.IsAnswered);
                Assert.False(confirm.IsVisible);
                Assert.Contains(vm.Conversation, item => item.IsAnswer && item.Text == "Desktop apps, Design");
                Assert.Contains(vm.Conversation, item => item.IsRemembered);

                while (vm.Conversation.LastOrDefault(item => item.IsQuestion && !item.IsAnswered) is { } next)
                    await vm.AnswerAsync(next, next.Options[0]);
                await vm.ContinueCommand.ExecuteAsync(null);
                window.UpdateLayout();
                Assert.Equal(string.Format(Loc.Onboarding_ReadyTitle, "Ada"), view.FindControl<TextBlock>("ReadyTitleText")!.Text);
                Assert.True(view.FindControl<StackPanel>("ReadyStarters")!.IsEffectivelyVisible);
                Assert.True(view.FindControl<StackPanel>("ReadyPlaces")!.IsEffectivelyVisible);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static OnboardingViewModel CreateViewModel(AppData data, IWorkspaceDiscovery discovery, bool withAi)
    {
        var vm = new OnboardingViewModel(new DataStore(data), TestCopilot.Shared, discovery,
            (_, _, _) => Task.FromResult(Personalization))
        {
            Pacing = TimeSpan.Zero,
            MinimumScanDuration = TimeSpan.Zero,
        };
        if (withAi)
            vm.LoginVM = new GitHubLoginViewModel(TestCopilot.Shared) { IsAuthenticated = true };
        return vm;
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private sealed class FakeDiscovery(IReadOnlyList<DiscoveredProject> projects) : IWorkspaceDiscovery
    {
        public int Scans { get; private set; }
        public bool SitesRead { get; private set; }

        public Task<IReadOnlyList<DiscoveredProject>> FindProjectsAsync(CancellationToken ct)
        {
            Scans++;
            return Task.FromResult(projects);
        }

        public Task<IReadOnlyList<string>> FindAppsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(["VS Code", "Krita"]);

        public Task<IReadOnlyList<string>> FindSitesAsync(CancellationToken ct)
        {
            SitesRead = true;
            return Task.FromResult<IReadOnlyList<string>>(["github.com"]);
        }

        public Task<WorkspaceDocuments> FindDocumentsAsync(CancellationToken ct)
            => Task.FromResult(new WorkspaceDocuments(Recent: 5, Pdfs: 1, Office: 2, Downloads: 40));
    }
}
