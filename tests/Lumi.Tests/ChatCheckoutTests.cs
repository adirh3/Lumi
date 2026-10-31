using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Where a coding chat's checkout shows: a choice for a new chat's first message on its composer, then the
/// chat's state in the header's git pill.
/// </summary>
[Collection("Headless UI")]
public sealed class ChatCheckoutTests
{
    private static readonly string[] CheckoutStringKeys =
    [
        "ChatHeader_Running", "Chat_ContextWindow_RingToolTip", "Git_LocalChoice", "Git_LocalChoiceChanges",
        "Git_WorktreeChoice", "Git_NewWorktree", "Git_FromBranch", "Git_HeaderTooltip", "Git_DraftTooltip",
        "Git_LocalShortcut", "Git_WorktreeShortcut",
    ];

    [Fact]
    public async Task Checkout_IsAComposerChipBeforeTheFirstMessage_AndAHeaderPillAfter()
    {
        var session = HeadlessTestSession.Start();
        try
        {
            await session.Dispatch(() =>
            {
                Loc.Load("en");
                var project = new Project { Name = "Lumi" };
                var data = new AppData
                {
                    Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false },
                    Projects = [project]
                };
                using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
                var view = new ChatView { DataContext = vm };
                var window = new Window { Width = 1000, Height = 700, Content = view };
                window.Show();
                try
                {
                    vm.SelectedProjectName = project.Name;
                    vm.IsCodingProject = true;
                    vm.GitBranch = "feature/checkout";
                    vm.GitChangedFileCount = 1;
                    vm.GitChangedFiles.Add(Change("src/App.cs", added: 12, removed: 3));
                    Dispatcher.UIThread.RunJobs();

                    var composer = view.FindControl<StrataChatComposer>("Composer")!;
                    var chip = view.FindControl<Border>("ComposerGitChip")!;
                    var chipChanges = view.FindControl<Button>("ComposerGitChangesButton")!;
                    var pill = view.FindControl<Panel>("HeaderGitPill")!;
                    var pillChanges = view.FindControl<Button>("HeaderGitChangesButton")!;

                    // A new chat chooses where its first message works: a chip beside its project.
                    Assert.True(chip.IsEffectivelyVisible);
                    Assert.True(chipChanges.IsEffectivelyVisible);
                    Assert.Equal(["+12", "−3"], DiffTexts(chipChanges));
                    Assert.False(pill.IsVisible);
                    Assert.True(ProjectChip(composer).IsVisible);

                    var gitButton = view.FindControl<Button>("ComposerGitButton")!;
                    var card = Assert.IsType<MenuFlyout>(gitButton.Flyout);
                    card.ShowAt(gitButton);
                    Dispatcher.UIThread.RunJobs();
                    var choices = card.Items.OfType<MenuItem>().ToList();
                    Assert.Same(vm.SwitchToLocalPreChatCommand,
                        choices.Single(item => item.Name == "ComposerGitLocalItem").Command);
                    Assert.Same(vm.SwitchToWorktreePreChatCommand,
                        choices.Single(item => item.Name == "ComposerGitWorktreeItem").Command);
                    Assert.Equal("Your folder, with its 1 change", vm.GitLocalChoiceDescription);
                    card.Hide();

                    // A new worktree starts clean from the last commit, so it has no changes to show.
                    vm.IsWorktreeMode = true;
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(vm.IsWorktreePending);
                    Assert.False(chipChanges.IsVisible);
                    Assert.Contains(chip.GetVisualDescendants().OfType<TextBlock>(),
                        text => text.Text == "New worktree" && text.IsEffectivelyVisible);
                    vm.IsWorktreeMode = false;
                    Dispatcher.UIThread.RunJobs();

                    // Once the chat starts, its checkout is fixed: the header's git pill shows it. Its project can
                    // still change, moving the chat, so the project stays on the composer.
                    vm.CurrentChat = new Chat
                    {
                        ProjectId = project.Id,
                        Messages = [new ChatMessage { Role = "user", Content = "Go" }]
                    };
                    vm.IsCodingProject = true;
                    Dispatcher.UIThread.RunJobs();

                    Assert.False(chip.IsVisible);
                    Assert.True(pill.IsEffectivelyVisible);
                    Assert.True(pillChanges.IsEffectivelyVisible);
                    Assert.Equal(["+12", "−3"], DiffTexts(pillChanges));
                    Assert.Same(vm.ShowGitChangesCommand, pillChanges.Command);
                    Assert.Equal("Lumi", vm.SelectedProjectName);
                    Assert.True(ProjectChip(composer).IsEffectivelyVisible);
                }
                finally
                {
                    window.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Run(session.Dispose);
        }
    }

    [Fact]
    public void LocalChoice_CountsTheSettledChanges_NotTheRefreshInFlight()
    {
        Loc.Load("en");
        var data = new AppData
        {
            Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false }
        };
        using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared) { IsCodingProject = true };
        Assert.Equal("Your folder, as it is", vm.GitLocalChoiceDescription);

        vm.GitChangedFileCount = 2;
        vm.GitChangedFiles.Add(Change("a.cs", added: 1, removed: 0));
        vm.GitChangedFiles.Add(Change("b.cs", added: 0, removed: 4));
        Assert.Equal("Your folder, with its 2 changes", vm.GitLocalChoiceDescription);

        // A refresh empties the live list while git runs; the choice keeps the last answer.
        vm.IsRefreshingGitStatus = true;
        vm.GitChangedFileCount = 0;
        vm.GitChangedFiles.Clear();
        Assert.Equal("Your folder, with its 2 changes", vm.GitLocalChoiceDescription);
    }

    [Fact]
    public void Changes_OpenWhatTheyShow_WhileGitRefreshes()
    {
        Loc.Load("en");
        var data = new AppData
        {
            Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false }
        };
        using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared) { IsCodingProject = true };
        vm.GitChangedFileCount = 1;
        vm.GitChangedFiles.Add(Change("a.cs", added: 3, removed: 1));
        GitChangesViewModel? opened = null;
        vm.GitChangesShowRequested += changes => opened = changes;

        // Agent edits and every turn's end refresh git, which empties the live list until git answers. The
        // pill keeps showing the last answer, so a click opens that, rather than nothing.
        vm.IsRefreshingGitStatus = true;
        vm.GitChangedFileCount = 0;
        vm.GitChangedFiles.Clear();
        Assert.True(vm.ShowCheckoutChanges);
        vm.ShowGitChangesCommand.Execute(null);

        Assert.NotNull(opened);
        Assert.Equal("+3", opened.TotalAddedLabel);
    }

    [Fact]
    public async Task UnassignedChatKeepsItsSavedCheckoutWithoutInheritingTheProjectFilter()
    {
        using var session = HeadlessTestSession.Start();
        var root = Path.Combine(Path.GetTempPath(), $"lumi-unassigned-checkout-{Guid.NewGuid():N}");
        var repo = Path.Combine(root, "repo");
        var worktree = Path.Combine(root, "worktree");
        const string branch = "projectless-checkout";
        try
        {
            Directory.CreateDirectory(repo);
            File.WriteAllText(Path.Combine(repo, "readme.md"), "before\n");
            RunGit(repo, "init", "--quiet");
            RunGit(repo, "add", "-A");
            RunGit(repo, "-c", "user.name=Lumi Tests", "-c", "user.email=test@example.com",
                "commit", "--quiet", "-m", "initial");
            RunGit(repo, "worktree", "add", "--quiet", "-b", branch, worktree);
            File.AppendAllText(Path.Combine(worktree, "readme.md"), "an edit\n");

            await session.Dispatch(async () =>
            {
                var project = new Project { Name = "Lumi", WorkingDirectory = repo };
                var chat = new Chat
                {
                    Title = "Unassigned workspace",
                    WorktreePath = worktree,
                    Messages = [new ChatMessage { Role = "user", Content = "Check this workspace." }]
                };
                var data = new AppData
                {
                    Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false },
                    Projects = [project],
                    Chats = [chat]
                };
                using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
                vm.ActiveProjectFilterId = project.Id;
                await vm.LoadChatAsync(chat);
                await vm.RefreshCodingProjectState();

                Assert.True(vm.IsCodingProject);
                Assert.True(vm.IsWorktreeMode);
                Assert.Equal(worktree, vm.WorktreePath);
                Assert.Equal(branch, vm.GitBranch);
                Assert.Equal("readme.md", Assert.Single(vm.GitChangedFiles).RelativePath);
                Assert.Null(chat.ProjectId);
                Assert.Null(vm.SelectedProjectName);

                var view = new ChatView { DataContext = vm };
                var window = new Window { Width = 1000, Height = 700, Content = view };
                window.Show();
                try
                {
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(view.FindControl<Panel>("HeaderGitPill")!.IsEffectivelyVisible);
                    Assert.False(ProjectChip(view.FindControl<StrataChatComposer>("Composer")!).IsVisible);
                }
                finally
                {
                    window.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AnotherFoldersChanges_LeaveTheMomentTheCheckoutMoves()
    {
        using var session = HeadlessTestSession.Start();
        var root = Path.Combine(Path.GetTempPath(), $"lumi-checkout-folders-{Guid.NewGuid():N}");
        var edited = Path.Combine(root, "edited");
        var clean = Path.Combine(root, "clean");
        try
        {
            foreach (var repo in new[] { edited, clean })
            {
                Directory.CreateDirectory(repo);
                File.WriteAllText(Path.Combine(repo, "readme.md"), "hello\n");
                RunGit(repo, "init", "--quiet");
                RunGit(repo, "add", "-A");
                RunGit(repo, "-c", "user.name=Lumi Tests", "-c", "user.email=test@example.com",
                    "commit", "--quiet", "-m", "initial");
            }
            File.AppendAllText(Path.Combine(edited, "readme.md"), "an edit\n");

            bool showedEditedChanges = false, showedAfterMove = true, showedOnceSettled = true;
            await session.Dispatch(async () =>
            {
                Loc.Load("en");
                var first = new Project { Name = "Edited", WorkingDirectory = edited };
                var second = new Project { Name = "Clean", WorkingDirectory = clean };
                var data = new AppData
                {
                    Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false },
                    Projects = [first, second]
                };
                using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
                vm.SetProjectId(first.Id);
                await WaitForGitRefreshAsync(vm);
                showedEditedChanges = vm.ShowCheckoutChanges;

                // The edited folder's changes say nothing about the clean one: they leave at once, not when
                // git answers for the new folder.
                vm.SetProjectId(second.Id);
                showedAfterMove = vm.ShowCheckoutChanges;
                await WaitForGitRefreshAsync(vm);
                showedOnceSettled = vm.ShowCheckoutChanges;
            }, CancellationToken.None);

            Assert.True(showedEditedChanges);
            Assert.False(showedAfterMove);
            Assert.False(showedOnceSettled);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("he")]
    public void CheckoutAndHeaderStrings_ArePresent(string language)
    {
        using var strings = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(FindLumiSourceRoot(), "Resources", "Strings", $"{language}.json")));

        foreach (var key in CheckoutStringKeys)
        {
            var value = strings.RootElement.GetProperty(key).GetString();
            Assert.False(string.IsNullOrWhiteSpace(value), $"Missing {language} localization: {key}");
        }
    }

    private static GitFileChangeViewModel Change(string path, int added, int removed) => new(new GitFileChange
    {
        RelativePath = path,
        FullPath = Path.Combine(Path.GetTempPath(), path),
        Kind = GitChangeKind.Modified,
        StatusCode = "M",
        RepoRoot = Path.GetTempPath(),
        RepoRelativePath = path,
        LinesAdded = added,
        LinesRemoved = removed,
        HasLineStatistics = true,
    });

    private static string[] DiffTexts(Control segment) => segment.GetVisualDescendants()
        .OfType<TextBlock>()
        .Where(text => text.IsEffectivelyVisible
                       && (text.Classes.Contains("diff-added") || text.Classes.Contains("diff-removed")))
        .Select(text => text.Text ?? "")
        .ToArray();

    private static async Task WaitForGitRefreshAsync(ChatViewModel vm)
    {
        // SetProjectId already starts the refresh; a second one can outlive fixture cleanup.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (vm.IsRefreshingGitStatus)
            await Task.Delay(10, timeout.Token);
    }

    private static Border ProjectChip(StrataChatComposer composer) => composer.GetVisualDescendants()
        .OfType<Border>()
        .Single(border => border.Name == "PART_ProjectChip");

    private static void RunGit(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(directory, ".lumi-test-global-gitconfig");
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, error.Result);
    }

    private static string FindLumiSourceRoot([CallerFilePath] string sourceFilePath = "")
    {
        for (var directory = new FileInfo(sourceFilePath).Directory; directory is not null; directory = directory.Parent)
        {
            var sourceRoot = Path.Combine(directory.FullName, "src", "Lumi");
            if (File.Exists(Path.Combine(sourceRoot, "Lumi.csproj")))
                return sourceRoot;
        }

        throw new DirectoryNotFoundException("Could not locate the Lumi source tree.");
    }
}
