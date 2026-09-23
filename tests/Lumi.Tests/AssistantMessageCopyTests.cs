using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class AssistantMessageCopyTests
{
    private const string CardResponse = """
        ```card
        {"header":"שני הטורים שלך","summary":"טור 1: 7, 13, 16, 25, 28, 32 — חזק: 5\n\nטור 2: 8, 11, 13, 19, 20, 27 — חזק: 6","detail":"Additional details"}
        ```
        """;

    [Fact]
    public async Task AssistantCopyButton_IsVisibleBelowTheResponseWithoutHover()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var window = CreateWindow("A response to copy.");
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(200);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                var message = window.GetVisualDescendants().OfType<StrataChatMessage>().Single();
                var actionLayer = message.GetVisualDescendants().OfType<Border>()
                    .Single(control => control.Name == "PART_ActionLayer");
                var bubble = message.GetVisualDescendants().OfType<Border>()
                    .Single(control => control.Name == "PART_Bubble");
                var copy = FindButton(message, "PART_CopyButton");

                Assert.False(message.IsPointerOver);
                Assert.True(actionLayer.IsEffectivelyVisible);
                Assert.Equal(1, actionLayer.Opacity);
                Assert.True(actionLayer.IsHitTestVisible);
                Assert.True(copy.IsEffectivelyVisible);
                Assert.Equal(1, Grid.GetRow(actionLayer));
                Assert.True(actionLayer.Bounds.Top >= bubble.Bounds.Bottom);
                Assert.False(FindButton(message, "PART_RegenerateButton").IsVisible);
                Assert.False(FindButton(message, "PART_EditButton").IsVisible);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("A response to copy.")]
    [InlineData("**A formatted response**\n\n- First item\n- Second item")]
    [InlineData(CardResponse)]
    public async Task AssistantCopyButton_CopiesTheWholeResponse(string response)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var window = CreateWindow(response);
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var message = window.GetVisualDescendants().OfType<StrataChatMessage>().Single();
                FindButton(message, "PART_CopyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                Assert.NotNull(window.Clipboard);
                Assert.Equal(response, await window.Clipboard.TryGetTextAsync());
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AssistantCopyButton_PreservesSelectedText()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var window = CreateWindow("Copy only these numbers: 7, 13, 16");
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var message = window.GetVisualDescendants().OfType<StrataChatMessage>().Single();
                var text = message.GetVisualDescendants().OfType<SelectableTextBlock>().Single();
                text.SelectionStart = text.Text!.IndexOf('7');
                text.SelectionEnd = text.Text!.Length;
                FindButton(message, "PART_CopyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                Assert.NotNull(window.Clipboard);
                Assert.Equal("7, 13, 16", await window.Clipboard.TryGetTextAsync());
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AssistantCard_CopyButton_CopiesReadableHebrewWithoutCopyingTheResponseSource()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var window = CreateWindow(CardResponse);
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var card = window.GetVisualDescendants().OfType<StrataCard>().Single();
                FindButton(card, "PART_CopyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                Assert.NotNull(window.Clipboard);
                var copiedText = await window.Clipboard.TryGetTextAsync();
                Assert.Contains("שני הטורים שלך", copiedText);
                Assert.Contains("טור 1: 7, 13, 16, 25, 28, 32 — חזק: 5", copiedText);
                Assert.Contains("טור 2: 8, 11, 13, 19, 20, 27 — חזק: 6", copiedText);
                Assert.Contains("Additional details", copiedText);
                Assert.DoesNotContain("```card", copiedText);
                Assert.DoesNotContain("\"summary\"", copiedText);
                Assert.False(card.IsExpanded);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static Window CreateWindow(string response)
    {
        var chatView = new ChatView();
        var item = new AssistantMessageItem(
            new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = response }),
            showTimestamps: false);
        var template = chatView.DataTemplates.First(candidate => candidate.Match(item));
        var control = Assert.IsAssignableFrom<Control>(template.Build(item));
        control.DataContext = item;
        chatView.FindControl<StrataChatShell>("ChatShell")!.Transcript = control;
        chatView.FindControl<Panel>("ChatPanel")!.Classes.Add("active");
        return new Window { Width = 900, Height = 600, Content = chatView };
    }

    private static Button FindButton(Control root, string name) =>
        root.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Name == name && ReferenceEquals(button.TemplatedParent, root));
}
