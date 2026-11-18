using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Text.Json;
using Lumi.Models;
using Lumi.Localization;
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
    public async Task AutomaticClipboardRead_KeepsUiResponsiveWhileClipboardOwnerIsBlocked()
    {
        using var session = HeadlessTestSession.Start();
        using var releaseRead = new ManualResetEventSlim();
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remainedResponsive = false;
        var uiThread = 0;
        var clipboardThread = 0;
        int? notificationThread = null;
        var snippet = ShareCode.Encode(CapabilityPackWriter.ForSkill(new Skill
        {
            Name = "Slow clipboard",
            Content = "Reply clearly."
        }).Text);

        var probe = Task.Run(async () =>
        {
            try
            {
                await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Dispatcher.UIThread.Post(() => heartbeat.SetResult());
                remainedResponsive = await Task.WhenAny(heartbeat.Task, Task.Delay(1000)) == heartbeat.Task;
            }
            finally
            {
                releaseRead.Set();
            }
        });

        await session.Dispatch(async () =>
        {
            uiThread = Environment.CurrentManagedThreadId;
            var store = new DataStore(new AppData
            {
                Settings = new UserSettings { OfferCopiedCapabilities = true }
            });
            var notice = new CapabilityNoticeViewModel(store,
                () => ClipboardHelper.GetTextForNoticeAsync(() =>
                {
                    clipboardThread = Environment.CurrentManagedThreadId;
                    readStarted.SetResult();
                    if (!releaseRead.Wait(TimeSpan.FromSeconds(15)))
                        throw new TimeoutException("The clipboard responsiveness probe did not finish.");
                    return snippet;
                }), static () => true);
            notice.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(CapabilityNoticeViewModel.IsShown) && notice.IsShown)
                    notificationThread = Environment.CurrentManagedThreadId;
            };

            var check = notice.CheckClipboardAsync();
            Assert.False(notice.IsShown);
            await notice.CheckClipboardAsync();
            await check;
            Assert.True(notice.IsShown);
            Assert.Equal("Slow clipboard", notice.Card!.Name);
        }, CancellationToken.None);

        await probe;
        Assert.True(remainedResponsive, "The UI dispatcher was blocked by the clipboard read.");
        Assert.NotEqual(uiThread, clipboardThread);
        Assert.Equal(uiThread, notificationThread);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyForChat_ShareSheetAndQuickCopyPublishRichHtmlWithTheSamePlainText(bool quickCopy)
    {
        using var session = HeadlessTestSession.Start(typeof(ClipboardHeadlessTestApp));
        await session.Dispatch(async () =>
        {
            using var lifetime = Assert.IsType<ClassicDesktopStyleApplicationLifetime>(Application.Current!.ApplicationLifetime);
            var window = new Window { Width = 400, Height = 300 };
            lifetime.MainWindow = window;
            try
            {
                window.Show();
                var skill = new Skill { Name = "Clipboard share", Content = "Reply with a friendly greeting." };
                var share = CapabilityPackWriter.ForSkill(skill);
                var code = ShareCode.Encode(CapabilityPackWriter.WriteCompact(share));
                var (text, html) = ShareSheetViewModel.ComposeChatSnippet(share.Pack, code);
                var sheet = new ShareSheetViewModel(new DataStore(new AppData()), static () => []);
                if (quickCopy)
                    Assert.NotNull(await sheet.QuickCopyForChatAsync(skill));
                else
                {
                    sheet.OpenFor(skill);
                    await sheet.CopyForChatCommand.ExecuteAsync(null);
                    Assert.False(sheet.IsStatusError);
                }

                var clipboard = window.Clipboard!;
                Assert.Equal(text, await clipboard.TryGetTextAsync());
                using var data = await clipboard.TryGetDataAsync();
                Assert.NotNull(data);
                Assert.Equal(html, await data.TryGetValueAsync(DataFormat.CreateStringPlatformFormat("text/html")));
                Assert.Equal(text, await data.TryGetTextAsync());
                if (OperatingSystem.IsWindows())
                    Assert.Equal(ChatClipboardData.BuildWindowsHtmlClipboardBytes(html),
                        await data.TryGetValueAsync(DataFormat.CreateBytesPlatformFormat("HTML Format")));
                else if (OperatingSystem.IsMacOS())
                    Assert.Equal(html, await data.TryGetValueAsync(DataFormat.CreateStringPlatformFormat("public.html")));
            }
            finally
            {
                await window.Clipboard!.ClearAsync();
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CrossBlockRightClick_KeepsTheHighlight_AndCopiesTheWholeSelection()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var first = new SelectableTextBlock { Text = "Alpha bravo" };
            var last = new SelectableTextBlock { Text = "Charlie delta" };
            var bubble = new StrataChatMessage
            {
                Content = new StackPanel { Spacing = 12, Children = { first, last } }
            };
            var view = new ChatView();
            Assert.IsAssignableFrom<Panel>(view.Content).Children.Add(bubble);
            var window = new Window { Width = 800, Height = 480, Content = view };
            window.Show();
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                first.Focus();
                Point At(SelectableTextBlock text, int index)
                {
                    var caret = text.TextLayout.HitTestTextPosition(index);
                    return text.TranslatePoint(new Point(caret.X + 0.1, caret.Center.Y), window)!.Value;
                }
                var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
                first.RaiseEvent(new PointerPressedEventArgs(first, pointer, window, At(first, 6), 0,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                    KeyModifiers.None));
                first.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, first, pointer, window, At(last, 7), 1,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
                first.RaiseEvent(new PointerReleasedEventArgs(first, pointer, window, At(last, 7), 2,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                    KeyModifiers.None, MouseButton.Left));
                last.RaiseEvent(new PointerPressedEventArgs(last, pointer, window, At(last, 3), 3,
                    new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed),
                    KeyModifiers.None));
                last.RaiseEvent(new PointerReleasedEventArgs(last, pointer, window, At(last, 3), 4,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.RightButtonReleased),
                    KeyModifiers.None, MouseButton.Right));
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                Assert.Equal("bravo", first.SelectedText);
                Assert.Equal("Charlie", last.SelectedText);
                var menu = Assert.IsType<ContextMenu>(bubble.ContextMenu);
                Assert.True(menu.IsOpen);
                var copyAs = menu.ItemsSource!.OfType<MenuItem>().Single(item => Equals(item.Header, Loc.Chat_CopyAs));
                Assert.Equal(3, copyAs.Items.Count);
                Assert.DoesNotContain(menu.ItemsSource.OfType<MenuItem>(), item => Equals(item.Header, Loc.Chat_CopySelection));
                Assert.DoesNotContain(menu.ItemsSource.OfType<MenuItem>(), item => Equals(item.Header, Loc.Chat_CopyTurn));
                var copy = copyAs.Items.OfType<MenuItem>().Single(item => Equals(item.Header, Loc.Chat_CopyRichText));
                copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Assert.Equal("bravo\n\nCharlie", await window.Clipboard!.TryGetTextAsync());
                Assert.Equal("bravo", first.SelectedText);
                Assert.Equal("Charlie", last.SelectedText);
                menu.Close();
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [SkippableFact]
    public async Task RichCopy_ExportsARenderedMermaidImageAndReadableTextFallback()
    {
        using var session = HeadlessTestSession.Start(typeof(SkiaHeadlessTestApp));
        await session.Dispatch(async () =>
        {
            const string content = "```mermaid\nflowchart LR\n A[Start] --> B[Finish]\n```";
            var markdown = new StrataMarkdown { Markdown = content, IsInline = true };
            var window = new Window { Width = 800, Height = 480, Content = markdown };
            window.Show();
            try
            {
                await Task.Delay(100);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                window.UpdateLayout();
                Assert.True(Assert.Single(markdown.GetVisualDescendants().OfType<StrataMermaid>()).Bounds.Width > 0);
                DataTransfer data;
                try
                {
                    data = ChatClipboardData.CreateRichText(content, markdown);
                }
                catch (InvalidCastException error) when (error.Message.Contains("HeadlessGlyphRunStub", StringComparison.Ordinal))
                {
                    Skip.If(true, "The drawing-free font cache is already initialized. Run this Skia test in a fresh test process.");
                    return;
                }
                using (data)
                {
                    var item = Assert.Single(data.Items);
                    var html = Assert.IsType<string>(item.TryGetRaw(DataFormat.CreateStringPlatformFormat("text/html")));
                    Assert.Contains("data:image/png;base64,", html);
                    Assert.DoesNotContain("<pre", html);
                    Assert.Contains("A[Start] --> B[Finish]", Assert.IsType<string>(item.TryGetRaw(DataFormat.Text)));
                }
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(StrataCopyFormat.Text)]
    [InlineData(StrataCopyFormat.Markdown)]
    [InlineData(StrataCopyFormat.RichText)]
    public async Task MessageCopy_ConvertsSpecialBlocks_WithoutASecondClipboardWrite(StrataCopyFormat format)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            const string content = "```card\n{\"header\":\"Report\",\"summary\":\"A **bold** result.\",\"detail\":\"Full details.\"}\n```";
            var item = new AssistantMessageItem(
                new ChatMessageViewModel(new ChatMessage
                {
                    Role = "assistant", Content = content,
                    ActiveSkills = [new SkillReference { Name = "Example skill" }]
                }), showTimestamps: false);
            item.ApplyExtras(null);
            var bubble = new StrataChatMessage { DataContext = item, Content = new SelectableTextBlock { Text = content } };
            var view = new ChatView();
            Assert.IsAssignableFrom<Panel>(view.Content).Children.Add(bubble);
            var window = new Window { Width = 800, Height = 480, Content = view };
            window.Show();
            try
            {
                var request = new StrataCopyRequestedEventArgs(StrataChatMessage.CopyRequestedEvent, content, false, format);
                bubble.RaiseEvent(request);
                Assert.NotNull(request.ClipboardWriteTask);
                await request.ClipboardWriteTask!;
                var text = await window.Clipboard!.TryGetTextAsync();
                Assert.Contains("Report", text);
                Assert.Contains("Full details.", text);
                Assert.DoesNotContain("```card", text);
                Assert.DoesNotContain("\"header\"", text);
                using var data = await window.Clipboard.TryGetDataAsync();
                Assert.Equal(format == StrataCopyFormat.RichText, data!.Formats.Any(item => item.Identifier == "text/html"));
                Assert.Equal(format == StrataCopyFormat.Markdown, data.Formats.Any(item => item.Identifier == "text/markdown"));
                var context = await data.TryGetValueAsync(DataFormat.CreateStringApplicationFormat("lumi-chat-context-v1"));
                using var json = JsonDocument.Parse(Assert.IsType<string>(context));
                Assert.DoesNotContain("```card", json.RootElement.GetProperty("Text").GetString());
                Assert.Equal("Example skill", json.RootElement.GetProperty("SkillNames")[0].GetString());
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SelectedTextCopy_LeavesTheSharedRangeAndFormattingToStrata()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var item = new AssistantMessageItem(
                new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "Whole answer" }), showTimestamps: false);
            var bubble = new StrataChatMessage { DataContext = item, Content = "Whole answer" };
            var view = new ChatView();
            Assert.IsAssignableFrom<Panel>(view.Content).Children.Add(bubble);
            var window = new Window { Width = 800, Height = 480, Content = view };
            window.Show();
            try
            {
                var request = new StrataCopyRequestedEventArgs(
                    StrataChatMessage.CopyRequestedEvent, "Selected **range**", true, StrataCopyFormat.RichText);
                bubble.RaiseEvent(request);
                Assert.True(request.Handled);
                Assert.Null(request.ClipboardWriteTask);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

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
                    IsOnboarded = true, AutoSaveChats = false, EnableMemoryAutoSave = false, OfferCopiedCapabilities = false
                }
            }), TestCopilot.Shared, new UpdateService(), startBackgroundJobs: false);
            Assert.True(main.IsOnboarded);
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
    [InlineData("text/html")]
    [InlineData("HTML Format")]
    [InlineData("public.html")]
    [InlineData("text/markdown")]
    [InlineData("Rich Text Format")]
    public async Task Paste_RichContentPreservesLinksAndFormattingAlongsideAnOfficePreview(string format)
    {
        using var session = HeadlessTestSession.Start(typeof(SkiaHeadlessTestApp));
        await session.Dispatch(async () =>
        {
            using var vm = new ChatViewModel(new DataStore(new AppData
            {
                Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false }
            }), TestCopilot.Shared);
            var view = new ChatView { DataContext = vm };
            var window = new Window { Width = 1100, Height = 820, Content = view };
            using var clipboard = new DataTransfer();
            var item = new DataTransferItem();
            item.SetText("Read Lumi docs");
            const string html = "<p>Read <strong><a href=\"https://example.com/docs\">Lumi docs</a></strong></p>";
            const string markdown = "Read **[Lumi docs](https://example.com/docs)**";
            if (format == "HTML Format")
                item.Set(DataFormat.CreateBytesPlatformFormat(format), ChatClipboardData.BuildWindowsHtmlClipboardBytes(html));
            else if (format == "Rich Text Format")
                item.Set(DataFormat.CreateBytesPlatformFormat(format), System.Text.Encoding.ASCII.GetBytes(
                    """{\rtf1\ansi Read {\b {\field{\*\fldinst HYPERLINK "https://example.com/docs"}{\fldrslt Lumi docs}}}}"""));
            else
                item.Set(DataFormat.CreateStringPlatformFormat(format), format == "text/markdown" ? markdown : html);
            using var bitmap = new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96));
            using var image = new MemoryStream();
            bitmap.Save(image, new PngBitmapEncoderOptions());
            item.Set(DataFormat.CreateBytesPlatformFormat("image/png"), image.ToArray());
            clipboard.Add(item);
            window.Show();
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                await window.Clipboard!.SetDataAsync(clipboard);
                var composer = view.FindControl<StrataChatComposer>("Composer")!;
                var input = composer.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "PART_Input");
                input.Text = "before selected after";
                input.SelectionStart = 15;
                input.SelectionEnd = 7;
                input.Focus();
                input.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent, Key = Key.V,
                    KeyModifiers = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control
                });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (input.Text == "before selected after")
                    await Task.Delay(10, timeout.Token);

                Assert.Equal($"before {markdown} after", input.Text);
                Assert.Equal(input.Text, vm.PromptText);
                Assert.Empty(vm.PendingAttachments);
                Assert.Equal(7 + markdown.Length, input.CaretIndex);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Paste_GoogleDocsNormalWeightWrapperKeepsOnlyAuthoredEmphasis()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var window = new Window();
            window.Show();
            try
            {
                using var clipboard = ChatClipboardData.CreateHtml(
                    """
                    <b id="docs-internal-guid-check" style="font-weight:normal">
                    <p><span style="font-weight:400">Normal text</span>
                    <span style="font-weight:700">bold text</span> and
                    <b style="font-weight: 400; font-style: italic">italic text</b>;
                    <strong>semantic bold</strong></p></b>
                    """, "Normal text bold text and italic text; semantic bold");
                await window.Clipboard!.SetDataAsync(clipboard);
                using var data = await window.Clipboard.TryGetDataAsync();
                Assert.Equal("Normal text **bold text** and *italic text*; **semantic bold**",
                    await ChatClipboardText.ReadAsync(data!, await data!.TryGetTextAsync()));
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paste_CodeEditorHtmlKeepsOriginalSourceIncludingWhitespaceAndMarkdown(bool hasEditorMetadata)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var window = new Window();
            window.Show();
            try
            {
                const string source = "if ready:\r\n\tRun()\r\n\r\n**bold** and [docs](https://example.com)\r\n";
                using var clipboard = new DataTransfer();
                var item = new DataTransferItem();
                item.SetText(source);
                item.Set(DataFormat.CreateStringPlatformFormat("text/html"),
                    hasEditorMetadata
                        ? "<div><span>if ready:</span><br><span>    Run()</span><br><span>**bold** and [docs](https://example.com)</span></div>"
                        : """
                          <div style="color:#cccccc;font-family:Consolas;white-space: pre;">
                          <div><span>if ready:</span></div><div><span>    Run()</span></div>
                          <div><span>**bold** and [docs](https://example.com)</span></div></div>
                          """);
                if (hasEditorMetadata)
                    item.Set(DataFormat.CreateStringPlatformFormat("vscode-editor-data"), """{"version":1,"mode":"markdown"}""");
                clipboard.Add(item);
                await window.Clipboard!.SetDataAsync(clipboard);
                using var data = await window.Clipboard.TryGetDataAsync();
                Assert.Equal(source, await ChatClipboardText.ReadAsync(data!, await data!.TryGetTextAsync()));
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Paste_HtmlKeepsDocumentStructureAndInlineStylesWithoutExecutingOrIncludingScripts()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var window = new Window();
            window.Show();
            try
            {
                using var clipboard = ChatClipboardData.CreateHtml(
                    """
                    <div style="white-space:pre">
                    <h2>Plan</h2>
                    <p><span style="font-weight:700">Bold</span> and <span style="font-style:italic">italic</span>
                    <del>old</del> <code>a*b</code> &amp; text</p>
                    <ul><li>First<ul><li>Nested</li></ul></li><li>Second</li></ul>
                    <ol start="3"><li>Third</li></ol>
                    <blockquote><p>Quoted</p></blockquote>
                    <div style="white-space:pre">An embedded preformatted note</div>
                    <pre><code class="language-csharp">if (ready) {
                        Run();
                    }</code></pre>
                    <table><tr><th>Name</th><th>Link</th></tr>
                    <tr><td>Lumi</td><td><a href="https://example.com/?a=1&amp;b=2">Docs</a></td></tr></table>
                    <script>unwanted()</script><style>.noise { color:red }</style>
                    </div>
                    """, "unformatted fallback");
                await window.Clipboard!.SetDataAsync(clipboard);
                using var data = await window.Clipboard.TryGetDataAsync();
                var text = await ChatClipboardText.ReadAsync(data!, "unformatted fallback");
                Assert.NotNull(text);
                Assert.Contains("## Plan", text);
                Assert.Contains("**Bold**", text);
                Assert.Contains("*italic*", text);
                Assert.Contains("~~old~~", text);
                Assert.Contains("`a*b`", text);
                Assert.Contains("& text", text);
                Assert.Contains("- First\n  - Nested", text);
                Assert.Contains("3. Third", text);
                Assert.Contains("> Quoted", text);
                Assert.Contains("```csharp\nif (ready) {\n    Run();\n}", text);
                Assert.Contains("| Name | Link |", text);
                Assert.Contains("[Docs](https://example.com/?a=1&b=2)", text);
                Assert.DoesNotContain("unwanted", text);
                Assert.DoesNotContain("color:red", text);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public void WindowsHtml_UsesUtf8ByteOffsetsAndOnlyTheSelectedFragment()
    {
        const string fragment = "<p>שלום <a href=\"https://example.com/\">🌟 docs</a></p>";
        var bytes = ChatClipboardData.BuildWindowsHtmlClipboardBytes(fragment);
        Assert.Equal(fragment, ChatClipboardText.ExtractWindowsHtml(bytes));
        var withoutOffsets = System.Text.Encoding.UTF8.GetBytes(
            $"Version:1.0\r\nStartHTML:-1\r\nEndHTML:-1\r\nStartFragment:-1\r\nEndFragment:-1\r\n" +
            $"<html><body>outside<!--StartFragment-->{fragment}<!--EndFragment-->outside</body></html>\0");
        Assert.Equal(fragment, ChatClipboardText.ExtractWindowsHtml(withoutOffsets));
    }

    [Theory]
    [InlineData("<p></p>")]
    [InlineData("<script>not content</script><style>body { color:red }</style>")]
    public async Task Paste_EmptyRichRepresentationKeepsThePlainTextFallback(string html)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var window = new Window();
            window.Show();
            try
            {
                using var clipboard = ChatClipboardData.CreateHtml(html, "copied text");
                await window.Clipboard!.SetDataAsync(clipboard);
                using var data = await window.Clipboard.TryGetDataAsync();
                Assert.Equal("copied text", await ChatClipboardText.ReadAsync(data!, "copied text"));
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paste_MarkdownRemainsVerbatimAndExplicitMarkdownWinsOverHtml(bool explicitMarkdown)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var composer = new StrataChatComposer();
            var window = new Window { Width = 700, Height = 400, Content = composer };
            window.Show();
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                const string markdown = "# Heading\r\n\r\n**bold** [docs](https://example.com)\r\n\r\n```cs\r\n  Run();\r\n```\r\n";
                using var clipboard = new DataTransfer();
                var item = new DataTransferItem();
                item.SetText(explicitMarkdown ? "Heading bold docs" : markdown);
                if (explicitMarkdown)
                {
                    item.Set(DataFormat.CreateStringPlatformFormat("text/markdown"), markdown);
                    item.Set(DataFormat.CreateStringPlatformFormat("text/html"), "<p>Not the Markdown source</p>");
                }
                clipboard.Add(item);
                await window.Clipboard!.SetDataAsync(clipboard);
                var input = composer.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "PART_Input");
                input.Focus();
                input.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent, Key = Key.V,
                    KeyModifiers = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control
                });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (string.IsNullOrEmpty(input.Text))
                    await Task.Delay(10, timeout.Token);
                Assert.Equal(markdown, composer.PromptText);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PasteAsPlainText_KeyboardAndContextMenuBypassRichTextAndHostImports(bool contextMenu)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var composer = new StrataChatComposer { ClipboardPasteInterceptFormats = new[] { DataFormat.Text } };
            var intercepted = false;
            composer.ClipboardImagePasteRequested += (_, _) => intercepted = true;
            var window = new Window { Width = 700, Height = 400, Content = composer };
            window.Show();
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                using var clipboard = ChatClipboardData.CreateHtml("<p><b>formatted</b></p>", "plain fallback");
                await window.Clipboard!.SetDataAsync(clipboard);
                var input = composer.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "PART_Input");
                input.Focus();
                if (contextMenu)
                {
                    var paste = input.ContextMenu!.Items.OfType<MenuItem>()
                        .Single(item => Equals(item.Header, InteractiveLabels.Current["pasteAsPlainText"]));
                    paste.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                }
                else
                    input.RaiseEvent(new KeyEventArgs
                    {
                        RoutedEvent = InputElement.KeyDownEvent, Key = Key.V,
                        KeyModifiers = (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) | KeyModifiers.Shift
                    });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (string.IsNullOrEmpty(input.Text))
                    await Task.Delay(10, timeout.Token);
                Assert.Equal("plain fallback", composer.PromptText);
                Assert.False(intercepted);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
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

public sealed class ClipboardHeadlessTestApp : Application
{
    public override void RegisterServices()
    {
        ApplicationLifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        base.RegisterServices();
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<ClipboardHeadlessTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
