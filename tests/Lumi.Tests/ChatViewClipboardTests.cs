using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Sharing;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ChatViewClipboardTests
{
    [Fact]
    public async Task SharedPromptCode_PasteOpensReviewWithoutChangingTheCurrentDraft()
    {
        using var session = HeadlessTestSession.Start(typeof(SkiaHeadlessTestApp));
        var reviewOpened = false;
        string? draft = null;
        var inputCount = 0;
        await session.Dispatch(async () =>
        {
            using var main = new MainViewModel(new DataStore(new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false, EnableMemoryAutoSave = false, OfferCopiedCapabilities = false
                }
            }), TestCopilot.Shared, new UpdateService(), startBackgroundJobs: false, skipOnboarding: true);
            var view = new ChatView { DataContext = main.ChatVM };
            var window = new Window { Width = 1100, Height = 820, Content = view };
            window.Show();
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                var prompt = new SharedPrompt
                {
                    Name = "Create a report", Description = "A portable report task.",
                    Content = "Create a report from {{source_file}} without changing the source.",
                    Inputs = [new("source_file", "Source file", "Your own file.", PromptInputKind.File)]
                };
                var pack = CapabilityPackWriter.ForPrompt(prompt);
                var snippet = ShareSheetViewModel.ComposeChatSnippet(pack.Pack, ShareCode.Encode(CapabilityPackWriter.WriteCompact(pack)));
                using var clipboard = new DataTransfer();
                clipboard.Add(DataTransferItem.CreateText(snippet.Text));
                await window.Clipboard!.SetDataAsync(clipboard);

                var composer = view.FindControl<StrataChatComposer>("Composer")!;
                var input = composer.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "PART_Input");
                input.Text = "My unsent draft";
                input.Focus();
                input.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent, Key = Key.V,
                    KeyModifiers = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control
                });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!main.ImportVM.IsOpen)
                    await Task.Delay(10, timeout.Token);
                reviewOpened = main.ImportVM.IsPrompt && main.ImportVM.IsReviewing;
                inputCount = main.ImportVM.PromptInputs.Count;
                draft = input.Text;
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
        Assert.True(reviewOpened);
        Assert.Equal(1, inputCount);
        Assert.Equal("My unsent draft", draft);
    }

    [Fact]
    public async Task SharedPromptCopy_ReportsAnUnavailableClipboardInsteadOfSuccess()
    {
        using var session = HeadlessTestSession.Start(typeof(SkiaHeadlessTestApp));
        var error = false;
        var copied = false;
        await session.Dispatch(async () =>
        {
            var sheet = new ShareSheetViewModel(new DataStore(new AppData()), static () => []);
            await sheet.OpenForPromptAsync(new PromptShareSource("Visible context", 2, false), (_, _) =>
                Task.FromResult(new SharedPrompt { Name = "Task", Description = "", Content = "Create a useful report." }));
            await sheet.CopyForChatCommand.ExecuteAsync(null);
            error = sheet.IsStatusError;
            copied = sheet.IsChatCopied;
            sheet.CloseCommand.Execute(null);
        }, CancellationToken.None);
        Assert.True(error);
        Assert.False(copied);
    }

    [Theory]
    [InlineData("Text copied from PowerPoint", true, false)]
    [InlineData("Plain text", false, false)]
    [InlineData(null, true, false)]
    [InlineData("", true, false)]
    [InlineData("Copied file", true, true)]
    public async Task Paste_PrioritizesFilesThenTextThenImages(string? text, bool includeImage, bool includeFile)
    {
        using var session = HeadlessTestSession.Start(typeof(SkiaHeadlessTestApp));

        await session.Dispatch(async () =>
        {
            var data = new AppData
            {
                Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false }
            };
            using var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            var view = new ChatView { DataContext = viewModel };
            var window = new Window { Width = 1100, Height = 820, Content = view };
            using var bitmap = new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96));
            using var clipboardData = new DataTransfer();
            var item = new DataTransferItem();
            if (text is not null)
                item.Set(DataFormat.Text, text);
            if (includeImage)
            {
                using var image = new MemoryStream();
                bitmap.Save(image, new PngBitmapEncoderOptions());
                item.Set(DataFormat.CreateBytesPlatformFormat("image/png"), image.ToArray());
            }
            clipboardData.Add(item);

            string? filePath = null;
            window.Show();
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                if (includeFile)
                {
                    filePath = Path.GetTempFileName();
                    var file = await window.StorageProvider.TryGetFileFromPathAsync(filePath);
                    Assert.NotNull(file);
                    item.Set(DataFormat.File, file);
                }
                await window.Clipboard!.SetDataAsync(clipboardData);

                var composer = view.FindControl<StrataChatComposer>("Composer")!;
                var input = composer.GetVisualDescendants().OfType<TextBox>()
                    .Single(control => control.Name == "PART_Input");
                input.Text = "before selected after";
                input.SelectionStart = 7;
                input.SelectionEnd = 15;
                input.Focus();
                input.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.V,
                    KeyModifiers = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control
                });

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (input.Text == "before selected after" && viewModel.PendingAttachments.Count == 0)
                    await Task.Delay(10, timeout.Token);

                if (includeFile)
                {
                    Assert.Equal("before selected after", input.Text);
                    Assert.Equal(filePath, Assert.Single(viewModel.PendingAttachments));
                }
                else if (!string.IsNullOrEmpty(text))
                {
                    Assert.Equal($"before {text} after", input.Text);
                    Assert.Empty(viewModel.PendingAttachments);
                }
                else
                {
                    Assert.Equal("before selected after", input.Text);
                    var attachment = Assert.Single(viewModel.PendingAttachments);
                    Assert.True(File.Exists(attachment));
                    using var pastedImage = new Bitmap(attachment);
                    Assert.Equal(bitmap.PixelSize, pastedImage.PixelSize);
                }
            }
            finally
            {
                window.Close();
                foreach (var attachment in viewModel.PendingAttachments)
                    File.Delete(attachment);
                if (filePath is not null)
                    File.Delete(filePath);
            }
        }, CancellationToken.None);
    }
}
