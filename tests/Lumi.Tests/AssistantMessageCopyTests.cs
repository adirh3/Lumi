using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.Localization;
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

    [Theory]
    [InlineData("en", "Copy message", "Copy card", "Toggle card details")]
    [InlineData("he", "העתק הודעה", "העתק כרטיס", "הצג או הסתר פרטי כרטיס")]
    public async Task MessageAndMarkdownCardActions_UseTheLoadedLanguage(
        string language, string messageLabel, string cardLabel, string toggleLabel)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load(language);
            var window = CreateWindow(CardResponse);
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(200);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                var message = window.GetVisualDescendants().OfType<StrataChatMessage>().Single();
                var card = window.GetVisualDescendants().OfType<StrataCard>().Single();
                AssertLabel(FindButton(message, "PART_CopyButton"), messageLabel);
                AssertLabel(FindButton(card, "PART_CopyButton"), cardLabel);
                AssertLabel(FindButton(card, "PART_ExpandButton"), toggleLabel);
                Assert.Equal(Loc.Chat_CopySelection, message.CopySelectionLabel);
                Assert.Equal(Loc.Chat_CopyTurn, message.CopyTurnLabel);
            }
            finally
            {
                window.Close();
                Loc.Load("en");
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
        var window = new Window { Width = 900, Height = 600, Content = chatView };
        window.Styles.Add(App.CreateChatActionTextStyles());
        return window;
    }

    private static void AssertLabel(Button button, string expected)
    {
        Assert.Equal(expected, ToolTip.GetTip(button));
        Assert.Equal(expected, AutomationProperties.GetName(button));
    }

    private static Button FindButton(Control root, string name) =>
        root.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Name == name && ReferenceEquals(button.TemplatedParent, root));
}
