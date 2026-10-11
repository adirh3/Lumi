using System.Globalization;
using System.Threading;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Sharing;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class AssistantMessageActionsTests
{
#if DEBUG
    [Fact]
    public void ActivityFixture_RecordsSyntheticModelsForEveryAssistantMessage()
    {
        var answers = DebugAgentHarness.CreateActivityTrailFixtureMessages()
            .Where(message => message.Role == "assistant").ToList();
        Assert.NotEmpty(answers);
        Assert.All(answers, message => Assert.Equal("gpt-5.5", message.Model));
    }
#endif

    [Fact]
    public void TimestampToolTip_IncludesFullDateSecondsAndTheDisplayedTimesOffset()
    {
        var timestamp = new DateTimeOffset(2026, 10, 11, 12, 55, 40, TimeSpan.FromHours(3));
        var source = new ChatMessageViewModel(new ChatMessage { Timestamp = timestamp });
        var assistant = new AssistantMessageItem(source, showTimestamps: true);
        var user = new UserMessageItem(source, showTimestamps: true);

        Assert.Equal("12:55", assistant.TimestampText);
        Assert.Equal($"{timestamp.ToString("F", CultureInfo.CurrentCulture)} (+03:00)", assistant.TimestampToolTip);
        Assert.Equal(assistant.TimestampToolTip, user.TimestampToolTip);

        assistant.TimestampText = "";
        user.TimestampText = "";
        Assert.Null(assistant.TimestampToolTip);
        Assert.Null(user.TimestampToolTip);
    }

    [Fact]
    public void ModelCaption_UsesTheRecordedModelAndExplicitlyMarksMissingMetadata()
    {
        Loc.Load("en");
        var source = new ChatMessageViewModel(new ChatMessage { Author = "Coding Lumi", Model = "gpt-5.5" });
        var assistant = new AssistantMessageItem(source, showTimestamps: false);

        Assert.Equal(ChatViewModel.FormatModelDisplay("gpt-5.5"), assistant.HeaderLabel);
        Assert.Equal("Coding Lumi", assistant.Author);
        Assert.Null(assistant.TimestampToolTip);

        source.Message.Model = null;
        var legacy = new AssistantMessageItem(source, showTimestamps: false);
        Assert.Equal(Loc.Chat_ModelNotRecorded, legacy.HeaderLabel);
        Assert.Null(legacy.ModelName);
    }

    [Fact]
    public void StreamingCompletion_RefreshesTheModelCaption()
    {
        var source = new ChatMessageViewModel(new ChatMessage
        {
            Author = "Lumi", Content = "Answer", IsStreaming = true
        });
        var assistant = new AssistantMessageItem(source, showTimestamps: true);
        var changes = new List<string?>();
        assistant.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        source.Message.Model = "gpt-5.5";
        source.NotifyStreamingEnded();

        Assert.Equal(ChatViewModel.FormatModelDisplay("gpt-5.5"), assistant.HeaderLabel);
        Assert.Contains(nameof(AssistantMessageItem.HeaderLabel), changes);
    }

    [Fact]
    public async Task FooterCopyAndMoreMenu_PreserveSelectedTextAndResetConfirmation()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var text = new SelectableTextBlock { Text = "Alpha beta gamma" };
            var message = new StrataChatMessage { Author = "Lumi", Content = text };
            var window = new Window { Width = 480, Height = 320, Content = message };
            window.Show();
            try
            {
                await PumpAsync();
                text.SelectionStart = 6;
                text.SelectionEnd = 10;
                var copy = FindButton(message, "PART_CopyButton");
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await PumpAsync();
                Assert.Equal("beta", await window.Clipboard!.TryGetTextAsync());

                FindButton(message, "PART_MoreButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var menu = Assert.IsType<ContextMenu>(message.ContextMenu);
                var selectedCopy = menu.ItemsSource!.OfType<MenuItem>().Single(item => Equals(item.Header, message.CopySelectionLabel));
                text.SelectionStart = text.SelectionEnd = 0;
                selectedCopy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await PumpAsync();
                Assert.Equal("beta", await window.Clipboard.TryGetTextAsync());
                menu.Close();

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (message.Classes.Contains(":copied"))
                    await Task.Delay(25, timeout.Token);
                Assert.Equal(message.CopyLabel, ToolTip.GetTip(copy));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(StrataCopyFormat.Markdown)]
    [InlineData(StrataCopyFormat.RichText)]
    public async Task MoreMenu_ProducesTheRequestedClipboardFormat(StrataCopyFormat format)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            const string markdown = "**Bold** answer with `code`.";
            var message = new StrataChatMessage
            {
                Author = "Lumi",
                Content = new StrataMarkdown { Markdown = markdown, IsInline = true }
            };
            var window = new Window { Width = 480, Height = 320, Content = message };
            window.Show();
            try
            {
                await PumpAsync();
                FindButton(message, "PART_MoreButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var menu = Assert.IsType<ContextMenu>(message.ContextMenu);
                var label = format == StrataCopyFormat.Markdown ? message.CopyMarkdownLabel : message.CopyRichTextLabel;
                menu.ItemsSource!.OfType<MenuItem>().Single(item => Equals(item.Header, label))
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await PumpAsync();

                using var data = await window.Clipboard!.TryGetDataAsync();
                Assert.NotNull(data);
                Assert.Equal(markdown, await data!.TryGetTextAsync());
                if (format == StrataCopyFormat.Markdown)
                    Assert.Equal(markdown, await data.TryGetValueAsync(DataFormat.CreateStringPlatformFormat("text/markdown")));
                else
                    Assert.Contains("<strong>Bold</strong>",
                        await data.TryGetValueAsync(DataFormat.CreateStringPlatformFormat("text/html")));
                menu.Close();
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MoreMenu_StillWorksAfterTheMessageIsDetachedAndReattached()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var message = new StrataChatMessage
            {
                Author = "Lumi",
                Content = "Answer",
                CanReply = true,
                AreActionsRevealed = true
            };
            var window = new Window { Width = 480, Height = 320, Content = message };
            window.Show();
            try
            {
                await PumpAsync();
                FindButton(message, "PART_MoreButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var menu = Assert.IsType<ContextMenu>(message.ContextMenu);
                Assert.True(menu.IsOpen);
                Assert.Contains(":menu-open", message.Classes);
                Assert.Contains(menu.ItemsSource!.OfType<MenuItem>(), item => Equals(item.Header, "Copy as Markdown"));
                menu.Close();
                Assert.DoesNotContain(":menu-open", message.Classes);

                window.Content = null;
                window.Content = message;
                await PumpAsync();
                FindButton(message, "PART_MoreButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(menu.IsOpen);
                Assert.Contains(":menu-open", message.Classes);
                menu.Close();
                Assert.DoesNotContain(":menu-open", message.Classes);
                Assert.Equal(PlacementMode.Pointer, menu.Placement);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReplyStrip_GroupsWorkingActionsBesideTheModelAndCopiesTheAnswer()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var answer = new ChatMessage
            {
                Role = "assistant",
                Author = "Lumi",
                Content = "Here is the answer.",
                Model = "gpt-5.5",
                Timestamp = new DateTimeOffset(2026, 10, 11, 12, 55, 40, TimeSpan.FromHours(3))
            };
            var chat = new Chat { Title = "Message actions" };
            chat.Messages.AddRange([new ChatMessage { Role = "user", Content = "Question" }, answer]);
            var data = new AppData
            {
                Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false, ShowTimestamps = true }
            };
            data.Chats.Add(chat);
            var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            var view = new ChatView { DataContext = vm };
            var window = new Window { Width = 1000, Height = 800, Content = view };
            window.Show();
            try
            {
                await vm.LoadChatAsync(chat);
                StrataChatMessage? message = null;
                for (var i = 0; i < 40 && message is null; i++)
                {
                    await PumpAsync();
                    message = view.GetVisualDescendants().OfType<StrataChatMessage>()
                        .FirstOrDefault(control => control.Role == StrataChatRole.Assistant);
                }

                Assert.NotNull(message);
                Assert.Equal(ChatViewModel.FormatModelDisplay(answer.Model), message!.Author);
                Assert.False(message.IsEditable);
                Assert.False(message.CanRegenerate);
                var parts = message.GetVisualDescendants().OfType<Control>().Where(control => control.Name is not null).ToList();
                var header = Assert.IsType<WrapPanel>(parts.Single(control => control.Name == "PART_HeaderRow"));
                var actions = Assert.IsType<Border>(parts.Single(control => control.Name == "PART_ActionLayer"));
                var timestamp = Assert.IsType<TextBlock>(parts.Single(control => control.Name == "PART_Timestamp"));
                var bubble = Assert.IsType<Border>(parts.Single(control => control.Name == "PART_Bubble"));
                Assert.Same(header, actions.Parent);
                var bubbleEnd = bubble.TranslatePoint(default, message)!.Value.Y + bubble.Bounds.Height;
                Assert.True(header.TranslatePoint(default, message)!.Value.Y >= bubbleEnd);
                Assert.True(actions.IsHitTestVisible);
                Assert.Equal(message.TimestampToolTip, ToolTip.GetTip(timestamp));
                ToolTip.SetIsOpen(timestamp, true);
                await PumpAsync();
                Assert.True(ToolTip.GetIsOpen(timestamp));
                ToolTip.SetIsOpen(timestamp, false);
                var initialHeight = message.Bounds.Height;
                message.AreActionsRevealed = true;
                await PumpAsync();

                Assert.True(actions.IsEffectivelyVisible);
                Assert.True(actions.IsHitTestVisible);
                Assert.Equal(initialHeight, message.Bounds.Height);
                Assert.False(FindButton(message, "PART_EditButton").IsVisible);
                Assert.False(FindButton(message, "PART_RegenerateButton").IsVisible);
                Assert.True(FindButton(message, "PART_CopyButton").IsVisible);
                Assert.True(FindButton(message, "PART_ReplyButton").IsVisible);
                Assert.True(FindButton(message, "PART_MoreButton").IsVisible);
                var share = FindButton(message, "PART_ShareButton");
                Assert.True(share.IsVisible);
                Assert.True(actions.IsVisualAncestorOf(share));
                Assert.Equal(Loc.Prompt_ShareTitle, ToolTip.GetTip(share));

                PromptShareSource? sharedSource = null;
                vm.SharePromptRequested += (snapshot, _) => sharedSource = snapshot;
                share.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.NotNull(sharedSource);
                Assert.Contains("Question", sharedSource!.Text);
                Assert.Contains(answer.Content, sharedSource.Text);

                var copy = FindButton(message, "PART_CopyButton");
                Assert.Equal(Loc.Chat_CopyMessage, AutomationProperties.GetName(copy));
                Assert.Equal(Loc.Chat_Reply, AutomationProperties.GetName(FindButton(message, "PART_ReplyButton")));
                Assert.Equal(Loc.Chat_MoreMessageActions, AutomationProperties.GetName(FindButton(message, "PART_MoreButton")));
                copy.Focus();
                copy.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                copy.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Enter });
                await PumpAsync();
                Assert.Equal(answer.Content, await window.Clipboard!.TryGetTextAsync());
                Assert.Contains(":copied", message.Classes);
                Assert.Equal(Loc.Chat_Copied, ToolTip.GetTip(copy));
                Assert.False(parts.Single(control => control.Name == "PART_CopyIcon").IsVisible);
                Assert.True(parts.Single(control => control.Name == "PART_CopiedIcon").IsVisible);

                FindButton(message, "PART_ReplyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await PumpAsync();
                Assert.Equal(answer.Id, vm.PendingReply!.MessageId);
                Assert.Equal(answer.Content, vm.PendingReply.Quote);
                vm.CancelReplyCommand.Execute(null);

                FindButton(message, "PART_MoreButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await PumpAsync();
                var menu = Assert.IsType<ContextMenu>(message.ContextMenu);
                Assert.True(menu.IsOpen);
                Assert.Contains(":menu-open", message.Classes);
                var labels = menu.ItemsSource!.OfType<MenuItem>().Select(item => item.Header).ToList();
                Assert.Contains(Loc.Chat_CopyMarkdown, labels);
                Assert.Contains(Loc.Chat_CopyRichText, labels);
                Assert.Contains(Loc.Chat_CopyTurn, labels);
                Assert.Contains(Loc.Menu_ForkFromHere, labels);
                menu.Close();
                Assert.DoesNotContain(":menu-open", message.Classes);
                Assert.Equal(PlacementMode.Pointer, menu.Placement);

                var assistant = Assert.IsType<AssistantMessageItem>(message.DataContext);
                assistant.ModelName = "An intentionally long assistant model caption";
                message.Width = 300;
                await PumpAsync();
                window.UpdateLayout();
                Assert.Equal(assistant.ModelName, message.Author);
                Assert.Equal(300, message.Bounds.Width);
                foreach (var button in actions.GetVisualDescendants().OfType<Button>().Where(button => button.IsVisible))
                {
                    var origin = button.TranslatePoint(default, message)!.Value;
                    Assert.InRange(origin.X, 0, message.Bounds.Width);
                    Assert.True(origin.X + button.Bounds.Width <= message.Bounds.Width,
                        $"{button.Name} extends beyond the 300px footer.");
                }

                assistant.IsStreaming = true;
                await PumpAsync();
                Assert.False(share.IsVisible);
                assistant.IsStreaming = false;
                await PumpAsync();
                Assert.True(share.IsVisible);
            }
            finally
            {
                window.Close();
                vm.Dispose();
            }
        }, CancellationToken.None);
    }

    private static Button FindButton(StrataChatMessage message, string name)
        => message.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
