using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using Xunit;
using static Lumi.Tests.DesktopPreviewTests;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class DesktopPreviewUiTests
{
    [Fact]
    public async Task WorkspaceShowsLoadingBeforeCaptureAndKeepsComposerFocusOnUpdates()
    {
        var session = HeadlessTestSession.Start(typeof(SkiaHeadlessTestApp));
        try
        {
            await session.Dispatch(async () =>
            {
                Loc.Load("en");
                var store = CreateStore();
                var chat = new Chat();
                using var vm = new ChatViewModel(store, TestCopilot.Shared) { CurrentChat = chat };
                using var workspace = new ChatWorkspaceView { DataContext = vm, DataStore = store };
                var window = new Window { Content = workspace, Width = 1100, Height = 760 };
                var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var finishCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var captureCount = 0;
                var visibleWhenCaptureStarted = false;
                var navigationCount = 0;
                var refreshCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                workspace.EnsureChatVisible = () => navigationCount++;
                vm.DesktopPreviewRefreshAsync = () =>
                {
                    captureCount++;
                    visibleWhenCaptureStarted = IsDesktopShown(workspace) && vm.HasVisibleDesktopPreviewHost;
                    captureStarted.TrySetResult();
                    return finishCapture.Task;
                };
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ChatViewModel.IsRefreshingDesktopPreview)
                        && captureCount > 0 && !vm.IsRefreshingDesktopPreview)
                    {
                        refreshCompleted.TrySetResult();
                    }
                };
                window.Show();
                try
                {
                    var update = CreateUpdate(chat.Id);
                    vm.PublishDesktopPreview(update);
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(workspace.IsWorkspaceOpen);
                    Assert.False(vm.HasVisibleDesktopPreviewHost);
                    Assert.Equal(0, captureCount);
                    Assert.Null(workspace.FindControl<ContentControl>("WorkspacePageHost")!.Content);

                    workspace.FocusComposer();
                    Dispatcher.UIThread.RunJobs();
                    var focused = window.FocusManager!.GetFocusedElement();
                    Assert.NotNull(focused);
                    Assert.Contains(workspace.ChatView!.GetVisualDescendants(), control => ReferenceEquals(control, focused));
                    vm.RequestShowDesktop();
                    await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.True(visibleWhenCaptureStarted);

                    var view = Assert.IsType<DesktopPreviewView>(workspace.FindControl<ContentControl>("WorkspacePageHost")!.Content);
                    var panel = workspace.FindControl<Border>("WorkspacePanel")!;
                    Assert.True(vm.IsRefreshingDesktopPreview);
                    Assert.True(view.FindControl<StackPanel>("DesktopPreviewPlaceholder")!.IsVisible);
                    Assert.Equal(Loc.Desktop_Loading, view.FindControl<TextBlock>("DesktopPreviewPlaceholderText")!.Text);
                    Assert.True(workspace.FindControl<GridSplitter>("WorkspaceSplitter")!.IsVisible);
                    Assert.True(workspace.FindControl<Grid>("WorkspaceGrid")!.ColumnDefinitions[2].Width.IsStar);

                    var frame = CreateFrame();
                    vm.PublishDesktopPreview(update with { NewFrame = frame });
                    finishCapture.SetResult();
                    await refreshCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var bitmap = await WaitForBitmapAsync(view);
                    Assert.Equal(Stretch.Uniform, view.FindControl<Image>("DesktopPreviewImage")!.Stretch);
                    var capturedAtText = view.FindControl<TextBlock>("DesktopCaptureTime")!.Text;
                    vm.PublishDesktopPreview(update with
                    {
                        ActionText = "Read control",
                        StatusText = "Complete",
                        UpdatedAt = update.UpdatedAt.AddMinutes(1)
                    });
                    Dispatcher.UIThread.RunJobs();

                    Assert.Same(bitmap, view.FindControl<Image>("DesktopPreviewImage")!.Source);
                    Assert.Equal(capturedAtText, view.FindControl<TextBlock>("DesktopCaptureTime")!.Text);
                    Assert.Equal("Complete", view.FindControl<TextBlock>("DesktopStatusText")!.Text);
                    Assert.Same(focused, window.FocusManager.GetFocusedElement());
                    Assert.Equal(0, navigationCount);
                    Assert.Equal(1, captureCount);
                    Assert.Equal(Loc.Desktop_Title, workspace.FindControl<TextBlock>("WorkspaceTitleText")!.Text);

                    await vm.RefreshDesktopPreviewCommand.ExecuteAsync(null);
                    Assert.Equal(2, captureCount);
                    vm.HasPlan = true;
                    vm.PlanContent = "# Plan";
                    vm.OpenWorkspacePlanCommand.Execute(null);
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(vm.IsDesktopOpen);
                    Assert.False(vm.HasVisibleDesktopPreviewHost);
                    Assert.True(panel.IsVisible);
                    Assert.Null(view.FindControl<Image>("DesktopPreviewImage")!.Source);
                    Assert.Equal(WorkspacePage.Plan, workspace.WorkspacePage);

                    vm.PublishDesktopPreview(update with { UpdatedAt = update.UpdatedAt.AddMinutes(2) });
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(WorkspacePage.Plan, workspace.WorkspacePage);
                    Assert.Equal(2, captureCount);

                    vm.RequestShowDesktop();
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(IsDesktopShown(workspace));
                    ClickClose(workspace);
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(vm.IsDesktopOpen);
                    Assert.False(vm.HasVisibleDesktopPreviewHost);
                    Assert.False(workspace.IsWorkspaceOpen);
                    Assert.True(vm.HasDesktopPreview);
                }
                finally
                {
                    finishCapture.TrySetResult();
                    window.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Run(session.Dispose);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedMainSurfaceRestoresOnlyTheRememberedOpenState(bool dismissed)
    {
        var session = HeadlessTestSession.Start(typeof(SkiaHeadlessTestApp));
        try
        {
            await session.Dispatch(async () =>
            {
                Loc.Load("en");
                var first = new Chat { Title = "First" };
                var second = new Chat { Title = "Second" };
                var store = CreateStore(first, second);
                using var registry = new ChatSurfaceRegistry();
                var loadCount = 0;
                using var surfaces = new ChatSessionStore(store, TestCopilot.Shared, registry, (surface, chat) =>
                {
                    loadCount++;
                    surface.CurrentChat = chat;
                    return Task.CompletedTask;
                });
                using var main = new MainViewModel(store, TestCopilot.Shared, new UpdateService(),
                    startBackgroundJobs: false, chatSurfaceRegistry: registry, chatSessionStore: surfaces,
                    initializeCopilotOnStartup: false);
                var window = new MainWindow { DataContext = main, Width = 1200, Height = 820 };
                window.Show();
                try
                {
                    await main.OpenChatByIdAsync(first.Id);
                    // The visible chat must realize its scroll template before we drain Loaded
                    // work. Let the shell's 350ms startup focus finish on this session's dispatcher.
                    await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
                    await Task.Delay(450);
                    await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
                    var surface = main.ChatVM;
                    var captures = 0;
                    var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    surface.DesktopPreviewRefreshAsync = () =>
                    {
                        captures++;
                        captured.TrySetResult();
                        return Task.CompletedTask;
                    };
                    surface.PublishDesktopPreview(CreateUpdate(first.Id));
                    surface.RequestShowDesktop();
                    await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Dispatcher.UIThread.RunJobs();
                    var workspace = window.FindControl<ChatWorkspaceView>("ChatContentGrid")!;
                    Assert.True(IsDesktopShown(workspace));
                    Assert.True(surface.HasVisibleDesktopPreviewHost);
                    Assert.Equal(1, captures);
                    if (dismissed)
                    {
                        ClickClose(workspace);
                        Dispatcher.UIThread.RunJobs();
                        Assert.False(IsDesktopShown(workspace));
                        Assert.False(surface.HasVisibleDesktopPreviewHost);
                    }

                    await main.OpenChatByIdAsync(second.Id);
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(IsDesktopShown(workspace));
                    Assert.False(surface.HasVisibleDesktopPreviewHost);
                    Assert.Equal(!dismissed, surface.IsDesktopOpen);
                    await main.OpenChatByIdAsync(first.Id);
                    Dispatcher.UIThread.RunJobs();

                    Assert.Same(surface, main.ChatVM);
                    Assert.Equal(2, loadCount);
                    Assert.Equal(!dismissed, IsDesktopShown(workspace));
                    Assert.Equal(!dismissed, surface.IsDesktopOpen);
                    Assert.Equal(!dismissed, surface.HasVisibleDesktopPreviewHost);
                    Assert.True(surface.HasDesktopPreview);
                    Assert.Equal(1, captures);

                    main.SettingsVM.SelectedPageIndex = 0;
                    main.SelectedNavIndex = 7;
                    surface.PublishDesktopPreview(CreateUpdate(first.Id) with { StatusText = "Background update" });
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(7, main.SelectedNavIndex);
                    Assert.False(IsDesktopShown(workspace));
                    Assert.False(surface.HasVisibleDesktopPreviewHost);
                    Assert.True(surface.IsDisplayedSurface);
                    main.SelectedNavIndex = 0;
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(!dismissed, IsDesktopShown(workspace));
                    Assert.Equal(!dismissed, surface.HasVisibleDesktopPreviewHost);
                    Assert.Equal(1, captures);
                }
                finally
                {
                    window.Close();
                    await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
                }
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Run(session.Dispose);
        }
    }

    [Fact]
    public async Task DetachedHostsRestoreWithoutCaptureAndOwnSeparateBitmaps()
    {
        var session = HeadlessTestSession.Start(typeof(SkiaHeadlessTestApp));
        try
        {
            await session.Dispatch(async () =>
            {
                var store = CreateStore();
                var chat = new Chat();
                using var vm = new ChatViewModel(store, TestCopilot.Shared) { CurrentChat = chat };
                vm.PublishDesktopPreview(CreateUpdate(chat.Id, frame: CreateFrame()));
                vm.IsDesktopOpen = true;
                Assert.False(vm.HasVisibleDesktopPreviewHost);
                var captures = 0;
                vm.DesktopPreviewRefreshAsync = () => { captures++; return Task.CompletedTask; };
                using var firstVm = new ChatWindowViewModel(vm);
                using var secondVm = new ChatWindowViewModel(vm);
                var firstWindow = new ChatWindow(store, firstVm) { Width = 1000, Height = 720 };
                var secondWindow = new ChatWindow(store, secondVm) { Width = 1000, Height = 720 };
                firstWindow.Show();
                secondWindow.Show();
                try
                {
                    Dispatcher.UIThread.RunJobs();
                    var firstWorkspace = firstWindow.FindControl<ChatWorkspaceView>("DetachedChatView")!;
                    var secondWorkspace = secondWindow.FindControl<ChatWorkspaceView>("DetachedChatView")!;
                    Assert.True(IsDesktopShown(firstWorkspace));
                    Assert.True(IsDesktopShown(secondWorkspace));
                    Assert.True(vm.HasVisibleDesktopPreviewHost);
                    var firstView = Assert.IsType<DesktopPreviewView>(
                        firstWorkspace.FindControl<ContentControl>("WorkspacePageHost")!.Content);
                    var secondView = Assert.IsType<DesktopPreviewView>(
                        secondWorkspace.FindControl<ContentControl>("WorkspacePageHost")!.Content);
                    var firstBitmap = await WaitForBitmapAsync(firstView);
                    var secondBitmap = await WaitForBitmapAsync(secondView);
                    Assert.NotSame(firstBitmap, secondBitmap);
                    Assert.Equal(0, captures);

                    firstWindow.Close();
                    Assert.Null(firstView.FindControl<Image>("DesktopPreviewImage")!.Source);
                    Assert.Same(secondBitmap, secondView.FindControl<Image>("DesktopPreviewImage")!.Source);
                    Assert.Equal(1, secondBitmap.PixelSize.Width);
                    Assert.True(vm.IsDesktopOpen);
                    Assert.True(vm.HasVisibleDesktopPreviewHost);

                    secondWorkspace.CloseWorkspacePages();
                    secondWorkspace.CloseWorkspacePages();
                    Assert.True(vm.IsDesktopOpen);
                    Assert.False(vm.HasVisibleDesktopPreviewHost);
                    secondWorkspace.RestoreDesktopPanel();
                    secondWorkspace.RestoreDesktopPanel();
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(vm.HasVisibleDesktopPreviewHost);
                    Assert.Equal(0, captures);

                    secondWindow.Hide();
                    Assert.False(vm.HasVisibleDesktopPreviewHost);
                    Assert.True(vm.IsDesktopOpen);
                    secondWindow.Show();
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(vm.HasVisibleDesktopPreviewHost);

                    secondWindow.Close();
                    Assert.True(vm.IsDesktopOpen);
                    Assert.False(vm.HasVisibleDesktopPreviewHost);
                    vm.CloseDesktopPreview();
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(IsDesktopShown(secondWorkspace));
                    Assert.False(vm.HasVisibleDesktopPreviewHost);
                    Assert.Null(secondView.FindControl<Image>("DesktopPreviewImage")!.Source);
                    Assert.NotNull(vm.DesktopPreviewFrame);
                }
                finally
                {
                    firstWindow.Close();
                    secondWindow.Close();
                    Assert.False(vm.HasVisibleDesktopPreviewHost);
                }
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Run(session.Dispose);
        }
    }

    [Fact]
    public async Task ReplacedStaleAndDetachedDecodeResultsAreDisposed()
    {
        var session = HeadlessTestSession.Start(typeof(SkiaHeadlessTestApp));
        try
        {
            await session.Dispatch(async () =>
            {
                var first = new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
                var second = new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
                var third = new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
                var decodes = new Queue<Task<Bitmap>>([first.Task, second.Task, third.Task]);
                using var view = new DesktopPreviewView(_ => decodes.Dequeue());
                var window = new Window { Content = view, Width = 600, Height = 600 };
                window.Show();
                try
                {
                    var staleBitmap = CreateTrackedBitmap();
                    var acceptedBitmap = CreateTrackedBitmap();
                    var detachedBitmap = CreateTrackedBitmap();
                    var staleLoad = view.ShowFrameAsync(CreateFrame());
                    var acceptedLoad = view.ShowFrameAsync(CreateFrame());
                    second.SetResult(acceptedBitmap);
                    await acceptedLoad;
                    Assert.Same(acceptedBitmap, view.FindControl<Image>("DesktopPreviewImage")!.Source);

                    first.SetResult(staleBitmap);
                    await staleLoad;
                    Assert.Equal(1, staleBitmap.DisposalCount);
                    Assert.Equal(0, acceptedBitmap.DisposalCount);
                    Assert.Same(acceptedBitmap, view.FindControl<Image>("DesktopPreviewImage")!.Source);

                    var detachedLoad = view.ShowFrameAsync(CreateFrame());
                    Assert.Equal(1, acceptedBitmap.DisposalCount);
                    window.Content = null;
                    third.SetResult(detachedBitmap);
                    await detachedLoad;
                    Assert.Equal(1, detachedBitmap.DisposalCount);
                    Assert.Null(view.FindControl<Image>("DesktopPreviewImage")!.Source);
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
    public async Task InvalidFrameShowsAnErrorInsteadOfThePreviousImage()
    {
        var session = HeadlessTestSession.Start(typeof(SkiaHeadlessTestApp));
        try
        {
            await session.Dispatch(async () =>
            {
                Loc.Load("en");
                using var view = new DesktopPreviewView();
                await view.ShowFrameAsync(CreateFrame());
                Assert.NotNull(view.FindControl<Image>("DesktopPreviewImage")!.Source);

                await view.ShowFrameAsync(CreateFrame() with { PngBytes = ReadOnlyMemory<byte>.Empty });
                Assert.Null(view.FindControl<Image>("DesktopPreviewImage")!.Source);
                Assert.True(view.FindControl<StackPanel>("DesktopPreviewPlaceholder")!.IsVisible);
                Assert.Equal(Loc.Desktop_Unavailable, view.FindControl<TextBlock>("DesktopPreviewPlaceholderText")!.Text);
                Assert.False(string.IsNullOrWhiteSpace(view.FindControl<TextBlock>("DesktopPreviewStateText")!.Text));
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Run(session.Dispose);
        }
    }

    [Fact]
    public async Task DesktopBackHistoryRestoresOnlyItsHostAndRefreshes()
    {
        var session = HeadlessTestSession.Start(typeof(SkiaHeadlessTestApp));
        try
        {
            await session.Dispatch(async () =>
            {
                Loc.Load("en");
                var store = CreateStore();
                store.Data.Settings.WorkspacePanelOpen = true;
                using var vm = new ChatViewModel(store, TestCopilot.Shared) { CurrentChat = new Chat() };
                vm.HasPlan = true;
                vm.PlanContent = "# Plan";
                var update = CreateUpdate(vm.CurrentChat.Id);
                vm.PublishDesktopPreview(update);
                var firstCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var secondCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var captures = 0;
                vm.DesktopPreviewRefreshAsync = () =>
                {
                    if (++captures == 1)
                        firstCapture.TrySetResult();
                    else
                        secondCapture.TrySetResult();
                    return Task.CompletedTask;
                };
                using var first = new ChatWorkspaceView { DataContext = vm, DataStore = store };
                using var second = new ChatWorkspaceView { DataContext = vm, DataStore = store };
                var firstWindow = new Window { Content = first, Width = 1100, Height = 760 };
                var secondWindow = new Window { Content = second, Width = 1100, Height = 760 };
                firstWindow.Show();
                secondWindow.Show();
                try
                {
                    vm.RequestShowDesktop();
                    await firstCapture.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    vm.OpenWorkspacePlanCommand.Execute(null);
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(WorkspacePage.Plan, first.WorkspacePage);
                    Assert.Equal(WorkspacePage.Plan, second.WorkspacePage);
                    Assert.False(vm.HasVisibleDesktopPreviewHost);

                    ClickBack(first);
                    await secondCapture.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.True(IsDesktopShown(first));
                    Assert.Equal(Loc.Desktop_Title, first.FindControl<TextBlock>("WorkspaceTitleText")!.Text);
                    Assert.Equal(WorkspacePage.Plan, second.WorkspacePage);
                    Assert.Equal("Plan", second.FindControl<TextBlock>("WorkspaceTitleText")!.Text);
                    Assert.True(vm.HasVisibleDesktopPreviewHost);
                    Assert.Equal(2, captures);

                    ClickClose(first);
                    vm.PublishDesktopPreview(update with { UpdatedAt = update.UpdatedAt.AddMinutes(1) });
                    first.RestoreDesktopPanel();
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(IsDesktopShown(first));
                    Assert.Equal(WorkspacePage.Plan, second.WorkspacePage);
                    Assert.False(vm.HasVisibleDesktopPreviewHost);
                    Assert.False(vm.IsDesktopOpen);
                    Assert.Equal(2, captures);
                }
                finally
                {
                    firstWindow.Close();
                    secondWindow.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Run(session.Dispose);
        }
    }

    [Fact]
    public async Task HostsKeepLocalDesktopIntentAcrossBackCachingAndGlobalDismissal()
    {
        var session = HeadlessTestSession.Start(typeof(SkiaHeadlessTestApp));
        try
        {
            await session.Dispatch(async () =>
            {
                Loc.Load("en");
                var store = CreateStore();
                store.Data.Settings.WorkspacePanelOpen = true;
                using var vm = new ChatViewModel(store, TestCopilot.Shared) { CurrentChat = new Chat() };
                using var other = new ChatViewModel(store, TestCopilot.Shared) { CurrentChat = new Chat() };
                vm.PublishDesktopPreview(CreateUpdate(vm.CurrentChat.Id, frame: CreateFrame()));
                var captures = 0;
                var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                vm.DesktopPreviewRefreshAsync = () =>
                {
                    captures++;
                    captured.TrySetResult();
                    return Task.CompletedTask;
                };
                using var first = new ChatWorkspaceView { DataContext = vm, DataStore = store };
                using var second = new ChatWorkspaceView { DataContext = vm, DataStore = store };
                var firstWindow = new Window { Content = first, Width = 1100, Height = 760 };
                var secondWindow = new Window { Content = second, Width = 1100, Height = 760 };
                firstWindow.Show();
                secondWindow.Show();
                try
                {
                    vm.RequestShowDesktop();
                    await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var secondView = Assert.IsType<DesktopPreviewView>(
                        second.FindControl<ContentControl>("WorkspacePageHost")!.Content);
                    var secondBitmap = await WaitForBitmapAsync(secondView);

                    secondWindow.Hide();
                    Assert.True(vm.HasVisibleDesktopPreviewHost);
                    ClickBack(first);
                    first.RestoreDesktopPanel();
                    first.DataContext = other;
                    first.DataContext = vm;
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(WorkspacePage.Overview, first.WorkspacePage);
                    Assert.True(IsDesktopShown(second));
                    Assert.Equal(Loc.Workspace_Title, first.FindControl<TextBlock>("WorkspaceTitleText")!.Text);
                    Assert.Equal(Loc.Desktop_Title, second.FindControl<TextBlock>("WorkspaceTitleText")!.Text);
                    Assert.Same(secondBitmap, secondView.FindControl<Image>("DesktopPreviewImage")!.Source);
                    Assert.False(vm.HasVisibleDesktopPreviewHost);
                    Assert.False(vm.IsDesktopOpen);
                    Assert.Equal(1, captures);

                    secondWindow.Show();
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(vm.HasVisibleDesktopPreviewHost);
                    Assert.True(vm.IsDesktopOpen);
                    secondWindow.Hide();
                    Assert.False(vm.HasVisibleDesktopPreviewHost);
                    second.CloseWorkspacePages();
                    secondWindow.Show();
                    second.RestoreDesktopPanel();
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(IsDesktopShown(second));
                    Assert.Equal(WorkspacePage.Overview, first.WorkspacePage);
                    Assert.True(vm.HasVisibleDesktopPreviewHost);
                    Assert.Equal(1, captures);

                    // The second host is cached on another chat when the explicit dismissal arrives.
                    second.DataContext = other;
                    vm.CloseDesktopPreview();
                    Dispatcher.UIThread.RunJobs();
                    second.DataContext = vm;
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(IsDesktopShown(first));
                    Assert.False(IsDesktopShown(second));
                    Assert.False(vm.HasVisibleDesktopPreviewHost);
                    Assert.False(vm.IsDesktopOpen);
                    Assert.Equal(1, captures);
                }
                finally
                {
                    firstWindow.Close();
                    secondWindow.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Run(session.Dispose);
        }
    }

    private static bool IsDesktopShown(ChatWorkspaceView workspace)
        => workspace.IsWorkspaceOpen && workspace.WorkspacePage == WorkspacePage.Desktop;

    private static void ClickBack(ChatWorkspaceView workspace)
        => workspace.FindControl<Button>("WorkspaceBackButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void ClickClose(ChatWorkspaceView workspace)
        => workspace.FindControl<Button>("WorkspaceCloseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task<Bitmap> WaitForBitmapAsync(DesktopPreviewView view)
    {
        for (var attempt = 0; attempt < 250; attempt++)
        {
            if (view.FindControl<Image>("DesktopPreviewImage")!.Source is Bitmap bitmap)
                return bitmap;
            await Task.Delay(20);
        }
        throw new TimeoutException("The desktop preview did not decode its in-process frame.");
    }

    private static TrackedBitmap CreateTrackedBitmap()
    {
        using var stream = new MemoryStream(CreateFrame().PngBytes.ToArray(), writable: false);
        return new TrackedBitmap(stream);
    }

    private sealed class TrackedBitmap(Stream stream) : Bitmap(stream)
    {
        public int DisposalCount { get; private set; }

        public override void Dispose()
        {
            DisposalCount++;
            base.Dispose();
        }
    }
}
