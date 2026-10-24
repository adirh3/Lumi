using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.ViewModels;
using Lumi.Views;
using Lumi.Views.Controls;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class TranscriptTextContentHeadlessTests
{
    [Theory]
    [InlineData("הרץ dotnet run --project SomeVeryLongProjectName", FlowDirection.RightToLeft)]
    [InlineData("شغل dotnet run --project SomeVeryLongProjectName", FlowDirection.RightToLeft)]
    [InlineData("Run שלום עולם בדיקה ארוכה בעברית", FlowDirection.LeftToRight)]
    public async Task PlainAndMarkdownTranscriptText_UseTheSameLeadingDirection(
        string text, FlowDirection expected)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var control = new TranscriptTextContent { Text = text };
            var window = new Window
            {
                Width = 500,
                Height = 200,
                Content = new StrataChatMessage { Content = control },
            };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var plain = Assert.IsType<SelectableTextBlock>(control.Content);
                Assert.Equal(expected, plain.FlowDirection);
                Assert.Equal(expected == FlowDirection.RightToLeft ? TextAlignment.Right : TextAlignment.Left,
                    plain.TextAlignment);

                control.Text = "**" + text + "**";
                Assert.Equal(expected, Assert.IsType<StrataMarkdown>(control.Content).FlowDirection);

                control.PreferPlainText = true;
                Assert.Equal(expected, Assert.IsType<SelectableTextBlock>(control.Content).FlowDirection);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task StreamingMarkdown_UsesSelectablePlainTextUntilStreamingEnds()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var control = new TranscriptTextContent
            {
                Text = "## Heading\n\n- item",
                PreferPlainText = true
            };

            var plainText = Assert.IsType<SelectableTextBlock>(control.Content);
            Assert.Equal(control.Text, plainText.Text);

            control.PreferPlainText = false;

            var markdown = Assert.IsType<StrataMarkdown>(control.Content);
            Assert.Equal(control.Text, markdown.Markdown);

            await Task.Delay(80);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task OnlyAssistantAnswersHostLiveInteractions()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            const string fence = "```checklist\n{\"id\":\"pack\",\"title\":\"Pack\",\"items\":[{\"id\":\"a\",\"label\":\"Passport\"}]}\n```";
            var quoted = new TranscriptTextContent { Text = fence };
            var answer = new TranscriptTextContent
            {
                Text = fence,
                DataContext = new AssistantMessageItem(
                    new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = fence }), showTimestamps: false)
            };
            var window = new Window { Width = 640, Height = 480, Content = new StackPanel { Children = { quoted, answer } } };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.False(Assert.IsType<StrataMarkdown>(quoted.Content).InteractionsHosted);
                Assert.True(Assert.IsType<StrataMarkdown>(answer.Content).InteractionsHosted);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task StreamingReasoningMarkdown_CanRenderMarkdownWhenEnabled()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            var control = new TranscriptTextContent
            {
                Text = "## Heading\n\n- item",
                PreferPlainText = true,
                RenderMarkdownWhileStreaming = true
            };

            Assert.IsType<StrataMarkdown>(control.Content);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task StreamingMarkdown_HeadingFollowedByBody_PreservesBlockFormatting()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            StrataMarkdown.ResetDiagnostics();

            var markdown = new StrataMarkdown
            {
                Markdown = "## Heading",
                IsInline = true
            };

            var window = new Window
            {
                Width = 640,
                Height = 360,
                Content = markdown
            };

            window.Show();
            await Task.Delay(80);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            markdown.Markdown = "## Heading\n\nBody text";

            await Task.Delay(100);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var textBlock = Assert.Single(markdown.GetVisualDescendants().OfType<SelectableTextBlock>());
            var inlines = textBlock.Inlines;
            Assert.NotNull(inlines);
            var heading = Assert.Single(inlines.OfType<Run>(), run => run.Text == "Heading");
            var body = Assert.Single(inlines.OfType<Run>(), run => run.Text == "Body text");

            var headingIndex = inlines.IndexOf(heading);
            var bodyIndex = inlines.IndexOf(body);
            Assert.True(bodyIndex > headingIndex);
            Assert.Contains(inlines.Skip(headingIndex + 1).Take(bodyIndex - headingIndex - 1),
                inline => inline is LineBreak);
            Assert.True(heading.FontSize > body.FontSize);
            Assert.Equal(FontWeight.SemiBold, heading.FontWeight);
            Assert.Equal(FontWeight.Normal, body.FontWeight);
            Assert.True(StrataMarkdown.CaptureDiagnostics().IncrementalParseCount > 0);

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AssistantTemplate_RendersMarkdownWhileStreaming()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var chatView = new ChatView();
            var assistantMessage = new ChatMessage
            {
                Role = "assistant",
                Content = "## Heading\n\nBody text",
                IsStreaming = true
            };

            var assistantItem = new AssistantMessageItem(new ChatMessageViewModel(assistantMessage), showTimestamps: false);
            var template = chatView.DataTemplates.FirstOrDefault(candidate => candidate.Match(assistantItem));

            Assert.NotNull(template);

            var control = Assert.IsAssignableFrom<Control>(template!.Build(assistantItem));
            control.DataContext = assistantItem;
            var window = new Window
            {
                Width = 640,
                Height = 360,
                Content = control
            };

            window.Show();
            await Task.Delay(80);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var textContent = control.GetVisualDescendants().OfType<TranscriptTextContent>().Single();

            Assert.True(textContent.PreferPlainText);
            Assert.True(textContent.RenderMarkdownWhileStreaming);
            Assert.IsType<StrataMarkdown>(textContent.Content);

            window.Close();
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReasoningTemplate_RendersMarkdownWhileStreamingAndCapsLongContent(bool subagent)
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            UserControl host = subagent ? new SubagentRunView() : new ChatView();
            var reasoningMessage = new ChatMessage
            {
                Role = "reasoning",
                Content = "**Reasoning**\n\n- first\n- second",
                IsStreaming = true
            };

            var reasoningItem = new ReasoningItem(new ChatMessageViewModel(reasoningMessage), expandWhileStreaming: true);
            var template = host.DataTemplates.FirstOrDefault(candidate => candidate.Match(reasoningItem));

            Assert.NotNull(template);

            var control = Assert.IsAssignableFrom<Control>(template!.Build(reasoningItem));
            control.DataContext = reasoningItem;
            var window = new Window
            {
                Width = 640,
                Height = 360,
                Content = new StackPanel { Children = { control } }
            };

            window.Show();
            await Task.Delay(80);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var textContent = control.GetVisualDescendants().OfType<TranscriptTextContent>().Single();

            Assert.True(textContent.PreferPlainText);
            Assert.True(textContent.RenderMarkdownWhileStreaming);
            Assert.IsType<StrataMarkdown>(textContent.Content);

            var think = Assert.IsType<StrataThink>(control);
            var contentScrollViewer = Assert.Single(think.GetVisualDescendants().OfType<ScrollViewer>());
            Assert.Equal(240, think.MaxContentHeight);
            Assert.InRange(contentScrollViewer.Bounds.Height, 1, 239);

            reasoningItem.Content = string.Join("\n\n", Enumerable.Range(1, 40)
                .Select(index => $"**Step {index}:** Review the evidence before choosing the next action."));
            await Task.Delay(450);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();

            Assert.Equal(240, contentScrollViewer.Bounds.Height, precision: 1);
            Assert.True(contentScrollViewer.Extent.Height > contentScrollViewer.Viewport.Height);
            var pill = think.GetVisualDescendants().OfType<Border>()
                .Single(control => control.Name == "PART_Pill");
            Assert.InRange(pill.Bounds.Height, 240, 300);

            reasoningItem.IsActive = false;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            Assert.Equal(240, contentScrollViewer.Bounds.Height, precision: 1);

            window.Close();
        }, CancellationToken.None);
    }
}
