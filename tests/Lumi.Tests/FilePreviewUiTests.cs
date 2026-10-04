using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;
using Microsoft.Extensions.AI;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class FilePreviewUiTests
{
    [SkippableFact]
    public async Task HtmlBrowserInitializationFailureShowsAnErrorInsteadOfSourceCode()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var path = Path.Combine(Path.GetTempPath(), $"lumi-html-preview-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(path, "<h1>Rendered HTML</h1>");
        var session = HeadlessTestSession.Start();
        try
        {
            await session.Dispatch(async () =>
            {
                var store = new DataStore(new AppData());
                var chat = new Chat();
                using var vm = new ChatViewModel(store, TestCopilot.Shared) { CurrentChat = chat };
                var pageHost = new ContentControl();
                using var controller = new WorkspacePanelController(
                    new Border(), store, vm,
                    new WorkspacePanelParts(new Grid(), new Border(), null, new Border(), new Border(), pageHost));

                // There is deliberately no native window for WebView2 in this headless test.
                var show = typeof(WorkspacePanelController).GetMethod(
                    "ShowFilePreviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                await ((Task)show.Invoke(controller, [path, null])!).WaitAsync(TimeSpan.FromSeconds(10));

                Assert.True(vm.HasUsedBrowser);
                Assert.NotNull(vm.GetBrowserServiceForChat(chat.Id));
                Assert.Equal(WorkspacePage.FilePreview, controller.Page);
                Assert.False(vm.IsBrowserOpen);
                Assert.True(vm.IsFilePreviewOpen);
                Assert.Equal(path, vm.PreviewFilePath);
                var view = Assert.IsType<FilePreviewView>(pageHost.Content);
                var content = Assert.IsType<StackPanel>(
                    view.FindControl<ContentControl>("FilePreviewContentHost")!.Content);
                Assert.Contains(content.Children.OfType<SelectableTextBlock>(),
                    text => text.Text?.Contains("no parent HWND available", StringComparison.Ordinal) == true);
                Assert.DoesNotContain(content.Children, control => control is StrataCodeBlock);
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Run(session.Dispose);
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MarkdownPreviewLoadsSiblingImagesFromTheDocumentDirectory()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"lumi-document-preview-{Guid.NewGuid():N}"));
        var document = Path.Combine(directory.FullName, "report.md");
        var image = Path.Combine(directory.FullName, "chart one.png");
        File.WriteAllBytes(image, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9ZQMcAAAAASUVORK5CYII="));
        File.WriteAllText(document, "# Report\n\n![Chart](chart%20one.png)\n\nInline ![Chart](chart%20one.png) example.");
        var session = HeadlessTestSession.Start();
        Window? window = null;
        FilePreviewView? view = null;
        Task? load = null;
        var loaded = false;
        var workingDirectory = Directory.GetCurrentDirectory();
        try
        {
            await session.Dispatch(() =>
            {
                view = new FilePreviewView();
                window = new Window { Content = view, Width = 600, Height = 600 };
                window.Show();
                load = view.ShowFileAsync(document);
            }, CancellationToken.None);
            await load!.WaitAsync(TimeSpan.FromSeconds(10));
            for (var attempt = 0; attempt < 100 && !loaded; attempt++)
            {
                await session.Dispatch(() =>
                {
                    var markdown = Assert.Single(view!.GetVisualDescendants().OfType<StrataMarkdown>());
                    Assert.Equal(directory.FullName, markdown.ImageBaseDirectory);
                    var frames = markdown.GetVisualDescendants().OfType<Border>()
                        .Where(border => border.Classes.Contains("strata-md-image")).ToArray();
                    loaded = frames.Length == 2 && frames.All(frame =>
                        Equals(image, ToolTip.GetTip(frame))
                        && frame.GetVisualDescendants().OfType<Image>().Any(content => content.Source is not null));
                }, CancellationToken.None);
                if (!loaded)
                    await Task.Delay(20);
            }
            Assert.True(loaded, "Both block and inline sibling images should render from the document directory.");
            Assert.Equal(workingDirectory, Directory.GetCurrentDirectory());
        }
        finally
        {
            await session.Dispatch(() => { view?.Dispose(); window?.Close(); }, CancellationToken.None);
            await Task.Run(session.Dispose);
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PreviewCanReplaceContentAndClearWithoutRetainingTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "Lumi-preview-ui-" + Guid.NewGuid().ToString("N") + ".cs");
        var oldPath = path + ".txt";
        await File.WriteAllTextAsync(path, "class Updated {}");
        await File.WriteAllTextAsync(oldPath, new string('x', FilePreviewContent.MaxCharacters));
        var session = HeadlessTestSession.Start();
        string? rendered = null;
        Exception? loadFailure = null;
        var cleared = false;
        try
        {
            await session.Dispatch(async () =>
            {
                try
                {
                    using var view = new FilePreviewView();
                    var oldLoad = view.ShowFileAsync(oldPath);
                    var latestLoad = view.ShowFileAsync(path);
                    await Task.WhenAll(oldLoad, latestLoad);
                    var host = view.FindControl<ContentControl>("FilePreviewContentHost")!;
                    rendered = ((host.Content as ScrollViewer)?.Content as StrataCodeBlock)?.Text;
                    view.Clear();
                    cleared = host.Content is null;
                }
                catch (Exception ex)
                {
                    loadFailure = ex;
                }
            }, CancellationToken.None);
            Assert.True(loadFailure is null, loadFailure?.ToString());
            Assert.Equal("class Updated {}", rendered);
            Assert.True(cleared);
            using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            // The headless dispatcher can complete the awaiting test inline on its own thread.
            // Its blocking Dispose must run elsewhere, or it waits for itself to exit.
            await Task.Run(session.Dispose);
            File.Delete(path);
            File.Delete(oldPath);
        }

    }

    [Fact]
    public async Task PreviewButtonIsOptInAndDoesNotOpenTheFile()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var chip = new StrataFileAttachment { FileName = "notes.md", IsRemovable = false };
            var window = new Window { Content = chip };
            window.Show();
            window.UpdateLayout();
            var button = chip.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_PreviewButton");
            var divider = chip.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_PreviewDivider");
            Assert.False(button.IsVisible);
            Assert.False(divider.IsVisible);
            chip.CanPreview = true;
            window.UpdateLayout();
            Assert.True(button.IsVisible);
            Assert.True(divider.IsVisible);
            Assert.Equal(1, divider.Width);
            Assert.Equal(20, divider.Height);
            Assert.Equal("Open preview", ToolTip.GetTip(button));
            var glyph = Assert.Single(button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>());
            Assert.NotNull(glyph.Data);
            Assert.Equal(button.Foreground, glyph.Stroke);
            var surface = button.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Surface");
            var restingForeground = button.Foreground;
            var restingBackground = surface.Background;
            window.MouseMove(button.TranslatePoint(
                new Avalonia.Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value);
            Assert.NotEqual(restingForeground, button.Foreground);
            Assert.Equal(button.Foreground, glyph.Stroke);
            Assert.NotEqual(restingBackground, surface.Background);
            window.MouseMove(new Avalonia.Point(0, 0));
            Assert.Equal(restingForeground, button.Foreground);
            Assert.Equal(restingBackground, surface.Background);
            var previews = 0;
            var opens = 0;
            chip.PreviewRequested += (_, _) => previews++;
            chip.OpenRequested += (_, _) => opens++;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, previews);
            Assert.Equal(0, opens);
            chip.IsEdited = true;
            chip.EditedLabel = "Edited";
            Assert.Equal("Edited", chip.StatusText);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ClearingChatClearsPreviewState()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var vm = new ChatViewModel(new DataStore(new AppData()), TestCopilot.Shared);
            string? requested = null;
            var hidden = false;
            vm.FilePreviewShowRequested += path => requested = path;
            vm.FilePreviewHideRequested += () => hidden = true;
            vm.OpenFilePreview("example.md");
            Assert.Equal("example.md", requested);
            Assert.True(vm.IsFilePreviewOpen);
            vm.ClearChat();
            Assert.True(hidden);
            Assert.False(vm.IsFilePreviewOpen);
            Assert.Null(vm.PreviewFilePath);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AnnouncementPreviewOpensOnlyForTheExecutingChat()
    {
        var path = Path.Combine(Path.GetTempPath(), "Lumi-preview-tool-" + Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(path, "# Deliverable");
        var session = HeadlessTestSession.Start();
        var shown = 0;
        string? openedPath = null;
        var stayedClosedAfterSwitch = false;
        try
        {
            await session.Dispatch(async () =>
            {
                var chat = new Chat { Title = "Preview tool test" };
                using var vm = new ChatViewModel(new DataStore(new AppData()), TestCopilot.Shared) { CurrentChat = chat };
                vm.FilePreviewShowRequested += file => { shown++; openedPath = file; };
                var tool = (AIFunction)typeof(ChatViewModel)
                    .GetMethod("BuildAnnounceFileTool", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, [chat.Id])!;
                var arguments = new AIFunctionArguments { ["filePath"] = path, ["preview"] = true };
                await tool.InvokeAsync(arguments);
                Dispatcher.UIThread.RunJobs();
                vm.CloseFilePreview();
                await tool.InvokeAsync(arguments);
                vm.CurrentChat = new Chat();
                Dispatcher.UIThread.RunJobs();
                stayedClosedAfterSwitch = !vm.IsFilePreviewOpen;
            }, CancellationToken.None);
            Assert.Equal(1, shown);
            Assert.Equal(path, openedPath);
            Assert.True(stayedClosedAfterSwitch);
        }
        finally
        {
            await Task.Run(session.Dispose);
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReopeningTheWorkspaceRestoresVisibilityAfterAnimationCancellation()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var store = new DataStore(new AppData());
            using var vm = new ChatViewModel(store, TestCopilot.Shared);
            // The panel was left mid-slide by a cancelled animation.
            var panel = new Border
            {
                IsVisible = true, Opacity = 0,
                RenderTransform = new Avalonia.Media.TranslateTransform(40, 0)
            };
            using var controller = new WorkspacePanelController(
                new Border(), store, vm,
                new WorkspacePanelParts(new Grid(), new Border(), null, panel, new Border(), new ContentControl()));
            controller.ShowAgents();
            Assert.True(panel.IsVisible);
            Assert.Equal(1, panel.Opacity);
            Assert.Null(panel.RenderTransform);
            Assert.Equal(WorkspacePage.Agents, vm.WorkspacePage);
            Assert.True(vm.IsWorkspacePanelOpen);
        }, CancellationToken.None);
    }
}
