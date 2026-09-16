using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Services;
using Lumi.Remote.Protocol;
using System.Runtime.ExceptionServices;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Mobile.Tests;

[Collection("Headless mobile UI")]
public sealed class MobileGitChangesTests
{
    [Fact]
    public async Task GitStatisticsAppearForTheListAndTotalsAndStayHonestWhenIncomplete()
    {
        var sink = new GitSink
        {
            Files =
            [
                new() { Path = "first.cs", Kind = "Modified", LinesAdded = 12, LinesRemoved = 3 },
                new() { Path = "new.cs", Kind = "Untracked", LinesAdded = 7, LinesRemoved = 0 },
                new() { Path = "image.png", Kind = "Modified", IsBinary = true }
            ]
        };
        var vm = new MobileChatViewModel(sink);
        vm.Reset(Guid.NewGuid(), "Statistics");
        await vm.OpenGitChangesCommand.ExecuteAsync(null);
        Assert.Equal("+19", vm.GitTotalAdditionsText);
        Assert.Equal("−3", vm.GitTotalDeletionsText);
        Assert.Equal("Total text changes", vm.GitStatsScopeText);
        Assert.True(vm.HasGitLineStatistics);

        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(() =>
        {
            var view = new GitChangesView { DataContext = vm };
            var window = new Window { Width = 360, Height = 780, Content = view };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("+19", view.FindControl<TextBlock>("GitTotalAdditions")!.Text);
                Assert.Equal("−3", view.FindControl<TextBlock>("GitTotalDeletions")!.Text);
                var totals = view.FindControl<Grid>("GitTotalStats")!;
                var back = view.FindControl<Button>("GitBackButton")!;
                Assert.True(totals.Bounds.Width >= 300);
                Assert.True(totals.TranslatePoint(default, window)!.Value.Y >=
                    back.TranslatePoint(new Point(0, back.Bounds.Height), window)!.Value.Y);
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Name == "GitRowAdditions" && text.Text == "+12" && text.IsEffectivelyVisible);
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text == "Binary file" && text.IsEffectivelyVisible);
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally
            {
                view.DataContext = null;
                window.Close();
            }
        }, CancellationToken.None);
        failure?.Throw();

        vm.GitFilesTruncated = true;
        Assert.Contains("partial", vm.GitStatsScopeText);
        vm.GitFilesTruncated = false;
        vm.GitFiles = [new() { Path = "legacy.cs" }];
        Assert.False(vm.HasGitLineStatistics);
        Assert.Contains("partial", vm.GitStatsScopeText);
        vm.Reset(Guid.NewGuid(), "Other");
        Assert.False(vm.HasGitLineStatistics);
    }

    [Fact]
    public async Task SelectedPathProjection_NotifiesAndNeverReturnsNullAcrossNavigation()
    {
        var vm = new MobileChatViewModel(new GitSink());
        vm.Reset(Guid.NewGuid(), "Git chat");
        var paths = new List<string>();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MobileChatViewModel.SelectedGitFilePath))
                paths.Add(vm.SelectedGitFilePath);
        };
        Assert.Equal("", vm.SelectedGitFilePath);
        await vm.OpenGitChangesCommand.ExecuteAsync(null);
        await vm.OpenGitFileCommand.ExecuteAsync(vm.GitFiles[0]);
        vm.BackFromGitFileCommand.Execute(null);
        await vm.OpenGitFileCommand.ExecuteAsync(vm.GitFiles[0]);
        vm.CloseGitChangesCommand.Execute(null);
        Assert.Equal(new[] { "file.cs", "", "file.cs", "" }, paths);
        Assert.Equal("", vm.SelectedGitFilePath);
    }

    [Fact]
    public async Task GitView_RepeatedListFileBackAndClose_EmitsNoBindingWarnings()
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(() =>
        {
            var previousLogger = Logger.Sink;
            var logs = new BindingLogSink();
            Window? window = null;
            try
            {
                Logger.Sink = logs;
                var vm = new MobileChatViewModel(new GitSink());
                vm.Reset(Guid.NewGuid(), "Git chat");
                var view = new GitChangesView { DataContext = vm };
                var overlay = new Border { Child = view };
                window = new Window { Width = 412, Height = 892, Content = overlay };
                window.Show();
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    vm.OpenGitChangesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                    overlay.IsVisible = true;
                    Dispatcher.UIThread.RunJobs();
                    var path = view.FindControl<TextBlock>("GitSelectedFilePath")!;
                    Assert.Equal("", path.Text);
                    vm.OpenGitFileCommand.ExecuteAsync(vm.GitFiles[0]).GetAwaiter().GetResult();
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal("file.cs", path.Text);
                    Assert.True(path.IsVisible);
                    view.FindControl<Button>("GitBackButton")!.Command!.Execute(null);
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal("", path.Text);
                    Assert.False(path.IsVisible);
                    view.FindControl<Button>("GitCloseButton")!.Command!.Execute(null);
                    overlay.IsVisible = false;
                    Dispatcher.UIThread.RunJobs();
                }
                Assert.Empty(logs.Messages);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window?.Close();
                Logger.Sink = previousLogger;
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    [Fact]
    public async Task PreviousAndNextFiles_UpdateSelectionPositionAndCommandBoundaries()
    {
        var sink = new GitSink { Files = MultipleFiles() };
        var vm = new MobileChatViewModel(sink);
        vm.Reset(Guid.NewGuid(), "Files");
        await vm.OpenGitChangesCommand.ExecuteAsync(null);
        Assert.False(vm.PreviousGitFileCommand.CanExecute(null));
        Assert.False(vm.NextGitFileCommand.CanExecute(null));
        await vm.OpenGitFileCommand.ExecuteAsync(vm.GitFiles[0]);
        Assert.Equal("1 of 3", vm.GitFilePositionText);
        Assert.False(vm.PreviousGitFileCommand.CanExecute(null));
        Assert.True(vm.NextGitFileCommand.CanExecute(null));
        await vm.NextGitFileCommand.ExecuteAsync(null);
        Assert.Same(vm.GitFiles[1], vm.SelectedGitFile);
        Assert.Equal("Renamed", vm.SelectedGitFileStatus);
        await vm.NextGitFileCommand.ExecuteAsync(null);
        Assert.Equal("3 of 3", vm.GitFilePositionText);
        Assert.False(vm.NextGitFileCommand.CanExecute(null));
        await vm.PreviousGitFileCommand.ExecuteAsync(null);
        Assert.Equal("2 of 3", vm.GitFilePositionText);
        vm.NavigateGitBackCommand.Execute(null);
        Assert.False(vm.IsGitFileOpen);
        Assert.True(vm.IsGitChangesOpen);
        vm.NavigateGitBackCommand.Execute(null);
        Assert.False(vm.HasOpenSheet);
        Assert.Empty(sink.Commands);
    }

    [Fact]
    public async Task NextFileWhileLoading_CancelsAndDiscardsPreviousFileResult()
    {
        var stale = new TaskCompletionSource<RemoteGitDiff?>();
        var sink = new GitSink
        {
            Files = MultipleFiles(),
            DiffFactory = (chatId, scope, path) => path == "first.cs"
                ? stale.Task
                : Task.FromResult<RemoteGitDiff?>(new()
                {
                    ChatId = chatId, ScopeId = scope, Path = path, UnifiedDiff = "+latest file"
                })
        };
        var vm = new MobileChatViewModel(sink);
        vm.Reset(Guid.NewGuid(), "Files");
        await vm.OpenGitChangesCommand.ExecuteAsync(null);
        var first = vm.OpenGitFileCommand.ExecuteAsync(vm.GitFiles[0]);
        var firstToken = sink.LastToken;
        Assert.True(vm.IsGitDiffLoading);
        await vm.NextGitFileCommand.ExecuteAsync(null);
        Assert.True(firstToken.IsCancellationRequested);
        Assert.Equal("renamed.cs", vm.SelectedGitFilePath);
        Assert.Equal("+latest file", vm.GitDiffText);
        stale.SetException(new IOException("old file failure"));
        await first;
        Assert.Null(vm.GitError);
        Assert.Equal("+latest file", vm.GitDiffText);
        Assert.False(vm.IsGitLoading);
    }

    [Fact]
    public async Task BinaryTruncationAndRetry_KeepUsefulStatesWithoutParsingDiffs()
    {
        var sink = new GitSink { Files = MultipleFiles() };
        var vm = new MobileChatViewModel(sink);
        vm.Reset(Guid.NewGuid(), "Files");
        sink.ListResponse = Task.FromResult<RemoteGitChanges?>(new()
        {
            ChatId = vm.ChatId, ScopeId = "scope", IsRepository = true,
            IsTruncated = true, Files = [.. sink.Files]
        });
        await vm.OpenGitChangesCommand.ExecuteAsync(null);
        Assert.Contains("Limited", vm.GitFileCountText);
        sink.DiffFactory = (chatId, scope, path) => Task.FromResult<RemoteGitDiff?>(new()
        {
            ChatId = chatId, ScopeId = scope, Path = path,
            Message = "Binary file: no text diff."
        });
        await vm.OpenGitFileCommand.ExecuteAsync(vm.GitFiles[2]);
        Assert.True(vm.ShowGitTextPlaceholder);
        Assert.Equal("Binary file: no text diff.", vm.GitMessage);
        sink.DiffFactory = (_, _, _) => Task.FromException<RemoteGitDiff?>(new IOException("Try again"));
        await vm.RetryGitChangesCommand.ExecuteAsync(null);
        Assert.True(vm.HasGitError);
        Assert.False(vm.ShowGitTextPlaceholder);
        sink.DiffFactory = (chatId, scope, path) => Task.FromResult<RemoteGitDiff?>(new()
        {
            ChatId = chatId, ScopeId = scope, Path = path,
            UnifiedDiff = "+bounded", IsTruncated = true
        });
        await vm.RetryGitChangesCommand.ExecuteAsync(null);
        Assert.False(vm.HasGitError);
        Assert.Contains("truncated", vm.GitMessage);
        Assert.True(vm.HasGitDiffText);
        Assert.Equal("3 of 3", vm.GitFilePositionText);
    }

    [Theory]
    [InlineData(400, 800, false)]
    [InlineData(800, 900, true)]
    [InlineData(600, 320, false)]
    public async Task FullHeightLayout_UsesSharedRendererAndKeepsTouchHeaderInsideViewport(
        double width, double height, bool wide)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(() =>
        {
            Window? window = null;
            try
            {
                var vm = new MobileChatViewModel(new GitSink { Files = MultipleFiles() });
                vm.Reset(Guid.NewGuid(), "A deliberately long chat title that must not push header controls offscreen");
                var view = new GitChangesView { DataContext = vm };
                window = new Window { Width = width, Height = height, Content = view };
                window.Show();
                vm.OpenGitChangesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                Dispatcher.UIThread.RunJobs();
                Assert.True(view.FindControl<Border>("GitFileListPane")!.IsVisible);
                vm.OpenGitFileCommand.ExecuteAsync(vm.GitFiles[0]).GetAwaiter().GetResult();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(wide, vm.IsGitWideLayout);
                Assert.Equal(wide, view.FindControl<Border>("GitFileListPane")!.IsVisible);
                Assert.True(view.FindControl<Grid>("GitDetailPane")!.IsVisible);
                var diff = view.FindControl<StrataDiffView>("GitSharedDiff")!;
                Assert.True(diff.IsEffectivelyVisible);
                Assert.True(diff.Bounds.Height >= height - 220, $"Diff only has {diff.Bounds.Height}px at {width}×{height}.");
                Assert.True(double.IsNaN(diff.Height));
                Assert.True(double.IsPositiveInfinity(diff.MaxHeight));
                Assert.Equal(vm.GitDiffText, diff.UnifiedDiffText);
                Assert.Equal(vm.SelectedGitFilePath, diff.FilePath);
                Assert.True(diff.TouchMode);
                Assert.Equal(14, diff.CodeFontSize);
                Assert.Equal(height < 480, vm.IsGitShortLayout);
                var codeViewport = diff.FindControl<ScrollViewer>("DiffScroller")!;
                Assert.True(codeViewport.Bounds.Height >= (height < 480 ? 150 : height - 300),
                    $"Code viewport only has {codeViewport.Bounds.Height}px at {width}×{height}.");
                foreach (var name in new[]
                {
                    "GitBackButton", "GitCloseButton", "GitRefreshButton",
                    height < 480 ? "GitCompactPreviousFileButton" : "GitPreviousFileButton",
                    height < 480 ? "GitCompactNextFileButton" : "GitNextFileButton"
                })
                {
                    var button = view.FindControl<Button>(name)!;
                    var origin = button.TranslatePoint(default, view)!.Value;
                    Assert.True(button.Bounds.Width >= 48 && button.Bounds.Height >= 48, name);
                    Assert.True(origin.X >= 0 && origin.Y >= 0, name);
                    Assert.True(origin.X + button.Bounds.Width <= view.Bounds.Width + 1, name);
                    Assert.True(origin.Y + button.Bounds.Height <= view.Bounds.Height + 1, name);
                }
                if (wide)
                {
                    window.Width = 400;
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(vm.IsGitWideLayout);
                    Assert.False(view.FindControl<Border>("GitFileListPane")!.IsVisible);
                    Assert.Equal("first.cs", vm.SelectedGitFilePath);
                    window.Width = width;
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(vm.IsGitWideLayout);
                    Assert.True(view.FindControl<Border>("GitFileListPane")!.IsVisible);
                    Assert.Equal("first.cs", vm.SelectedGitFilePath);
                }
                view.FindControl<Button>("GitBackButton")!.Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.True(view.FindControl<Border>("GitFileListPane")!.IsVisible);
                Assert.Equal(wide, view.FindControl<Grid>("GitDetailPane")!.IsVisible);
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { window?.Close(); }
        }, CancellationToken.None);
        failure?.Throw();
    }

    [Fact]
    public async Task ChatOverlay_CoversViewportHonorsSafeAreaAndSuppressesNativeComposer()
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            Window? window = null;
            MobileShellViewModel? shell = null;
            try
            {
                shell = new MobileShellViewModel(store: session.NewStore(), post: action => action());
                shell.SafeArea = new Thickness(11, 23, 17, 31);
                shell.Chat.Reset(Guid.NewGuid(), "Git chat");
                shell.Chat.IsGitChangesOpen = true;
                var view = new ChatDetailView { DataContext = shell };
                window = new Window { Width = 400, Height = 800, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var overlay = view.FindControl<Border>("GitChangesOverlay")!;
                Assert.Equal(view.Bounds.Size, overlay.Bounds.Size);
                Assert.Equal(shell.SafeArea, overlay.Padding);
                Assert.Equal(255, Assert.IsAssignableFrom<ISolidColorBrush>(overlay.Background).Color.A);
                Assert.Equal(1, overlay.Opacity);
                Assert.True(shell.Chat.HasOpenSheet);
                Assert.False(ChatDetailView.ShouldShowNativeComposerEditor(shell));
                var chatSurface = view.FindControl<StrataChatShell>("ChatShell")!;
                Assert.False(chatSurface.IsVisible);
                shell.Chat.CloseGitChangesCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.False(overlay.IsVisible);
                Assert.False(shell.Chat.HasOpenSheet);
                Assert.True(chatSurface.IsVisible);
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally
            {
                window?.Close();
                if (shell is not null)
                    await shell.DisposeAsync();
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    private static IReadOnlyList<RemoteGitFile> MultipleFiles() =>
    [
        new() { Path = "first.cs", Kind = "Modified", Status = " M" },
        new() { Path = "renamed.cs", Kind = "Renamed", Status = "R " },
        new() { Path = "image.png", Kind = "Added", Status = "A " }
    ];

    [Fact]
    public async Task OlderPairedDesktop_WithoutGitCapability_DoesNotRequestNewRoutes()
    {
        using var handler = new RejectRequestsHandler();
        await using var client = new LumiRemoteClient("test", "Phone", handler);
        client.Configure("http://127.0.0.1:47653", "fixture-token");
        client.MarkProtocolCompatibleForTests();
        Assert.False(client.SupportsGitChanges);
        Assert.Null(await client.GetGitChangesAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Null(await client.GetGitDiffAsync(Guid.NewGuid(), "scope", "file", CancellationToken.None));
        Assert.Equal(0, handler.Calls);
        Assert.True(RemoteProtocol.HasRequiredCapabilities([RemoteProtocol.Capabilities.ScopedEventsV1]));
    }

    [Fact]
    public async Task OpenFileAndBack_PreservesList_ThenClosesSheet()
    {
        var sink = new GitSink();
        var vm = new MobileChatViewModel(sink);
        vm.Reset(Guid.NewGuid(), "Scoped chat");
        await vm.OpenGitChangesCommand.ExecuteAsync(null);
        Assert.True(vm.HasOpenSheet);
        Assert.Contains("Scoped chat", vm.GitScopeLabel);
        var file = Assert.Single(vm.GitFiles);
        await vm.OpenGitFileCommand.ExecuteAsync(file);
        Assert.True(vm.IsGitFileOpen);
        Assert.Contains("+new", vm.GitDiffText);
        Assert.True(vm.DismissTopmostSheet());
        Assert.False(vm.IsGitFileOpen);
        Assert.Single(vm.GitFiles);
        Assert.True(vm.IsGitChangesOpen);
        Assert.True(vm.DismissTopmostSheet());
        Assert.False(vm.HasOpenSheet);
        Assert.Empty(vm.GitDiffText);
        Assert.Empty(sink.Commands);
    }

    [Fact]
    public async Task SwitchingChat_CancelsListAndDiscardsLateResultsEvenWhenReturningToSameChat()
    {
        var pending = new TaskCompletionSource<RemoteGitChanges?>();
        var sink = new GitSink { ListResponse = pending.Task };
        var vm = new MobileChatViewModel(sink);
        var original = Guid.NewGuid();
        vm.Reset(original, "Original");
        var loading = vm.OpenGitChangesCommand.ExecuteAsync(null);
        var token = sink.LastToken;
        Assert.True(vm.IsGitLoading);
        vm.Reset(Guid.NewGuid(), "Other");
        vm.Reset(original, "Original");
        Assert.True(token.IsCancellationRequested);
        pending.SetResult(new RemoteGitChanges { ChatId = original, Files = [new() { Path = "secret.txt" }] });
        await loading;
        Assert.False(vm.IsGitChangesOpen);
        Assert.Empty(vm.GitFiles);
        Assert.False(vm.IsGitLoading);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackOrHostReset_DiscardsLateDiffAndErrors(bool fail)
    {
        var pending = new TaskCompletionSource<RemoteGitDiff?>();
        var sink = new GitSink { DiffResponse = pending.Task };
        var vm = new MobileChatViewModel(sink);
        vm.Reset(Guid.NewGuid(), "Original");
        await vm.OpenGitChangesCommand.ExecuteAsync(null);
        var loading = vm.OpenGitFileCommand.ExecuteAsync(vm.GitFiles[0]);
        var token = sink.LastToken;
        if (fail)
            vm.ResetHostState();
        else
            vm.BackFromGitFileCommand.Execute(null);
        Assert.True(token.IsCancellationRequested);
        if (fail)
            pending.SetException(new IOException("old failure"));
        else
            pending.SetResult(new RemoteGitDiff { UnifiedDiff = "old private code" });
        await loading;
        Assert.Empty(vm.GitDiffText);
        Assert.Null(vm.GitError);
        Assert.False(vm.IsGitFileOpen);
    }

    [Fact]
    public async Task UnsupportedEmptyAndErrors_AreReadableAndDoNotIssueCommands()
    {
        var sink = new GitSink { SupportsGitChanges = false };
        var vm = new MobileChatViewModel(sink);
        vm.Reset(Guid.NewGuid(), "Chat");
        await vm.OpenGitChangesCommand.ExecuteAsync(null);
        Assert.Contains("Update Lumi", vm.GitMessage);
        sink.SupportsGitChanges = true;
        sink.ListResponse = Task.FromResult<RemoteGitChanges?>(new()
        {
            ChatId = vm.ChatId, Message = "Not a repository", IsRepository = false
        });
        await vm.RefreshGitChangesCommand.ExecuteAsync(null);
        Assert.Empty(vm.GitFiles);
        Assert.Equal("Not a repository", vm.GitMessage);
        sink.ListResponse = Task.FromException<RemoteGitChanges?>(new IOException("Git unavailable"));
        await vm.RefreshGitChangesCommand.ExecuteAsync(null);
        Assert.Equal("Git unavailable", vm.GitError);
        Assert.False(vm.IsGitLoading);
        Assert.Empty(sink.Commands);
    }

    private sealed class RejectRequestsHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("No request should be sent to the older desktop.");
        }
    }

    private sealed class BindingLogSink : ILogSink
    {
        public List<string> Messages { get; } = [];
        public bool IsEnabled(LogEventLevel level, string area) =>
            area == LogArea.Binding && level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            if (IsEnabled(level, area))
                Messages.Add(messageTemplate);
        }
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate,
            params object?[] propertyValues)
        {
            if (IsEnabled(level, area))
                Messages.Add(messageTemplate + " " + string.Join(" ", propertyValues));
        }
    }

    private sealed class GitSink : IRemoteCommandSink, IRemoteGitChangesSink
    {
        public bool SupportsGitChanges { get; set; } = true;
        public Task<RemoteGitChanges?>? ListResponse { get; set; }
        public Task<RemoteGitDiff?>? DiffResponse { get; set; }
        public IReadOnlyList<RemoteGitFile> Files { get; set; } =
            [new() { Path = "file.cs", Kind = "Modified", Status = " M" }];
        public Func<Guid, string, string, Task<RemoteGitDiff?>>? DiffFactory { get; set; }
        public CancellationToken LastToken { get; private set; }
        public List<RemoteCommand> Commands { get; } = [];

        public Task<RemoteGitChanges?> GetGitChangesAsync(Guid chatId, CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            return ListResponse ?? Task.FromResult<RemoteGitChanges?>(new()
            {
                ChatId = chatId, ScopeId = "scope", ScopeLabel = "Project · Chat worktree", IsRepository = true,
                Files = [.. Files]
            });
        }

        public Task<RemoteGitDiff?> GetGitDiffAsync(
            Guid chatId, string scopeId, string path, CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            if (DiffFactory is not null)
                return DiffFactory(chatId, scopeId, path);
            return DiffResponse ?? Task.FromResult<RemoteGitDiff?>(new()
            {
                ChatId = chatId, ScopeId = scopeId, Path = path,
                UnifiedDiff = "@@ -1 +1 @@\n-old\n+new", LinesAdded = 1, LinesRemoved = 1
            });
        }

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command)
        {
            Commands.Add(command);
            return Task.FromResult(new RemoteCommandResult { Ok = true });
        }
        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            throw new NotSupportedException();
    }
}
