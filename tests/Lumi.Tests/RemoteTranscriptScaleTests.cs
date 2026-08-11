using System.Text;
using System.Text.Json;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.Services.Remote;
using Xunit;

namespace Lumi.Tests;

public sealed class RemoteTranscriptScaleTests
{
    private const int UserMessageCount = 28;
    private const int AssistantMessageCount = 1_069;
    private const int ReasoningMessageCount = 1_150;
    private const int ToolMessageCount = 10_151;
    private const int TotalMessageCount =
        UserMessageCount + AssistantMessageCount + ReasoningMessageCount + ToolMessageCount;
    private const int LargestToolContentLength = 393_675;

    [Fact]
    public void ReproductionShape_DefaultWindowIsBoundedAndComfortablyMobileSized()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Synthetic scale", MessageCount = TotalMessageCount };
        var messages = CreateReproductionShape();

        var window = RemoteProjector.SelectTranscriptWindow(messages, beforeMessageIndex: null);
        var transcript = Build(chat, window);

        Assert.Equal(TotalMessageCount, messages.Count);
        Assert.Equal(TotalMessageCount, window.TotalMessageCount);
        Assert.Equal(TotalMessageCount, window.EndMessageIndex);
        Assert.InRange(window.Messages.Count, 1, RemoteProtocol.TranscriptWindowRawMessageLimit);
        Assert.True(window.IsLatestWindow);
        Assert.Equal(window.StartMessageIndex > 0, window.HasEarlierMessages);
        Assert.False(window.HasLaterMessages);

        Assert.Equal(window.StartMessageIndex, transcript.WindowStartMessageIndex);
        Assert.Equal(window.EndMessageIndex, transcript.WindowEndMessageIndex);
        Assert.Equal(TotalMessageCount, transcript.TotalRawMessageCount);
        Assert.Equal(window.HasEarlierMessages, transcript.HasEarlierMessages);
        Assert.False(transcript.HasLaterMessages);
        Assert.True(transcript.IsLatestWindow);

        var items = transcript.Turns.SelectMany(turn => turn.Items).ToList();
        var tools = items.SelectMany(item => item.Tools ?? []).ToList();
        Assert.InRange(items.Count, 1, window.Messages.Count);
        Assert.InRange(tools.Count, 0, RemoteProtocol.TranscriptWindowRawMessageLimit);
        Assert.All(tools, AssertToolStringsAreBounded);
        Assert.All(items, AssertItemStringsAreBounded);

        var json = JsonSerializer.Serialize(transcript, RemoteJsonContext.Default.RemoteTranscript);
        Assert.True(
            Encoding.UTF8.GetByteCount(json) < 1_500_000,
            $"Bounded transcript was still {Encoding.UTF8.GetByteCount(json):N0} bytes.");
    }

    [Fact]
    public void ReproductionShape_EveryRawMessageIsReachableWithoutGapsOrStalls()
    {
        var messages = CreateReproductionShape();
        var pagesNewestFirst = new List<TranscriptMessageWindow>();
        int? beforeMessageIndex = null;
        var expectedEnd = messages.Count;

        while (true)
        {
            var page = RemoteProjector.SelectTranscriptWindow(messages, beforeMessageIndex);
            pagesNewestFirst.Add(page);

            Assert.Equal(expectedEnd, page.EndMessageIndex);
            Assert.True(page.StartMessageIndex < page.EndMessageIndex);
            Assert.InRange(page.Messages.Count, 1, RemoteProtocol.TranscriptWindowRawMessageLimit);
            Assert.Equal(
                messages
                    .Skip(page.StartMessageIndex)
                    .Take(page.EndMessageIndex - page.StartMessageIndex)
                    .Select(message => message.Id),
                page.Messages.Select(message => message.Id));

            if (!page.HasEarlierMessages)
                break;

            expectedEnd = page.StartMessageIndex;
            beforeMessageIndex = page.StartMessageIndex;
        }

        var reachedOldestFirst = pagesNewestFirst
            .AsEnumerable()
            .Reverse()
            .SelectMany(page => page.Messages)
            .Select(message => message.Id);

        Assert.Equal(messages.Select(message => message.Id), reachedOldestFirst);
        Assert.Equal(0, pagesNewestFirst[^1].StartMessageIndex);
        Assert.Contains(
            pagesNewestFirst,
            page => page.Messages.Count == 1
                    && page.Messages[0].Content.Length == LargestToolContentLength);
    }

    [Fact]
    public void PathologicalToolAndTranscriptText_AreExplicitlyTruncated()
    {
        var payload = new string('x', LargestToolContentLength);
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Huge strings" };
        var tool = Message("tool", payload);
        tool.ToolName = "powershell";
        tool.ToolOutput = payload;

        var transcript = RemoteProjector.BuildTranscript(
            chat,
            [
                Message("user", payload),
                Message("assistant", payload),
                Message("reasoning", payload),
                tool
            ],
            new RemoteChatStatus { ChatId = chat.Id },
            showReasoning: true,
            showToolCalls: true,
            revision: 1);

        var items = transcript.Turns.SelectMany(turn => turn.Items).ToList();
        AssertTruncated(
            Assert.Single(items, item => item.Kind == RemoteProtocol.ItemKinds.User).Text,
            RemoteProtocol.MobileUserTextLimit);
        AssertTruncated(
            Assert.Single(items, item => item.Kind == RemoteProtocol.ItemKinds.Assistant).Text,
            RemoteProtocol.MobileAssistantTextLimit);
        AssertTruncated(
            Assert.Single(items, item => item.Kind == RemoteProtocol.ItemKinds.Reasoning).Text,
            RemoteProtocol.MobileReasoningTextLimit);

        var terminal = Assert.Single(items, item => item.Kind == RemoteProtocol.ItemKinds.Terminal);
        AssertTruncated(terminal.Text, RemoteProtocol.MobileTerminalTextLimit);
        var call = Assert.Single(terminal.Tools!);
        AssertTruncated(call.Input, RemoteProtocol.MobileToolInputLimit);
        AssertTruncated(call.Output, RemoteProtocol.MobileToolOutputLimit);
    }

    [Fact]
    public void PathologicalQuestionAndStatusMetadata_AreCountAndSizeBounded()
    {
        const int optionCount = 20_000;
        var huge = new string('x', 100_000);
        var question = Message("tool", huge);
        question.ToolName = "ask_question";
        question.QuestionId = huge;
        question.QuestionText = huge;
        question.QuestionOptions = OptionsJson(
            optionCount,
            new string('o', 256),
            firstOption: huge);
        question.ToolOutput = huge;
        question.ToolStatus = huge;

        var chat = new Chat { Id = Guid.NewGuid(), Title = huge };
        var status = new RemoteChatStatus
        {
            ChatId = chat.Id,
            StatusText = huge,
            Model = huge,
            PlanContent = huge,
            Quality = huge,
            ContextWindowTier = huge,
            AgentName = huge,
            AgentGlyph = huge,
            ProjectName = huge,
            Suggestions = Enumerable.Repeat(huge, 1_000).ToList(),
            QualityLevels = Enumerable.Repeat(huge, 1_000).ToList(),
            ContextWindowTiers = Enumerable.Repeat(huge, 1_000).ToList(),
            SkillNames = Enumerable.Repeat(huge, 1_000).ToList(),
            McpNames = Enumerable.Repeat(huge, 1_000).ToList()
        };

        var window = RemoteProjector.SelectTranscriptWindow([question], beforeMessageIndex: null);
        var transcript = RemoteProjector.BuildTranscript(
            chat,
            window,
            status,
            showReasoning: true,
            showToolCalls: true,
            revision: 1);

        Assert.Single(window.Messages);
        var item = Assert.Single(transcript.Turns.SelectMany(turn => turn.Items));
        var projectedQuestion = Assert.IsType<RemoteQuestion>(item.Question);
        Assert.Equal(RemoteProtocol.MobileQuestionOptionCountLimit, projectedQuestion.Options.Count);
        Assert.Contains("omitted", projectedQuestion.Options[^1], StringComparison.OrdinalIgnoreCase);
        AssertTruncated(
            projectedQuestion.Options[0],
            RemoteProtocol.MobileQuestionOptionLimit);
        AssertTruncated(projectedQuestion.Text, RemoteProtocol.MobileQuestionTextLimit);
        AssertTruncated(projectedQuestion.Answer, RemoteProtocol.MobileQuestionAnswerLimit);
        Assert.True(projectedQuestion.QuestionId.Length <= RemoteProtocol.MobileIdentifierLimit);
        AssertTruncated(transcript.Title, RemoteProtocol.MobileTranscriptTitleLimit);
        AssertTruncated(transcript.Status.StatusText, RemoteProtocol.MobileStatusTextLimit);
        AssertTruncated(transcript.Status.Model, RemoteProtocol.MobileStatusValueLimit);
        AssertTruncated(transcript.Status.PlanContent, RemoteProtocol.MobilePlanTextLimit);
        AssertTruncated(transcript.Status.Quality, RemoteProtocol.MobileStatusValueLimit);
        AssertTruncated(
            transcript.Status.ContextWindowTier,
            RemoteProtocol.MobileStatusValueLimit);
        AssertTruncated(transcript.Status.AgentName, RemoteProtocol.MobileMetadataTextLimit);
        AssertTruncated(transcript.Status.AgentGlyph, RemoteProtocol.MobileStatusValueLimit);
        AssertTruncated(transcript.Status.ProjectName, RemoteProtocol.MobileMetadataTextLimit);
        AssertStatusCollection(
            transcript.Status.Suggestions,
            RemoteProtocol.MobileMetadataTextLimit);
        AssertStatusCollection(
            transcript.Status.QualityLevels,
            RemoteProtocol.MobileStatusValueLimit);
        AssertStatusCollection(
            transcript.Status.ContextWindowTiers,
            RemoteProtocol.MobileStatusValueLimit);
        AssertStatusCollection(
            transcript.Status.SkillNames,
            RemoteProtocol.MobileMetadataTextLimit);
        AssertStatusCollection(
            transcript.Status.McpNames,
            RemoteProtocol.MobileMetadataTextLimit);

        AssertJsonIsComfortablyBounded(transcript);
    }

    [Fact]
    public void PathologicalSourcesAttachmentsAndToolMetadata_AreCountAndSizeBounded()
    {
        var huge = new string('m', 100_000);
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Aggregate metadata" };

        var user = Message("user", "attachments");
        user.Author = huge;
        for (var index = 0; index < 5_000; index++)
            user.Attachments.Add(huge);

        var assistant = Message("assistant", "sources");
        assistant.Author = huge;
        assistant.Model = huge;
        assistant.LinkedChatId = Guid.NewGuid();
        assistant.LinkedChatTitle = huge;
        for (var index = 0; index < 5_000; index++)
        {
            assistant.Sources.Add(new SearchSource
            {
                Title = huge,
                Snippet = huge,
                Url = huge
            });
        }

        var tools = new List<ChatMessage>();
        for (var index = 0; index < RemoteProtocol.TranscriptWindowRawMessageLimit - 2; index++)
        {
            var tool = Message("tool", "small");
            tool.ToolName = huge;
            tool.ToolCallId = huge;
            tool.ToolStatus = huge;
            tool.ToolOutput = "small";
            tool.Author = huge;
            tools.Add(tool);
        }

        var userTranscript = Build(
            chat,
            RemoteProjector.SelectTranscriptWindow([user], beforeMessageIndex: null));
        var userItems = userTranscript.Turns.SelectMany(turn => turn.Items).ToList();

        var projectedUser = Assert.Single(
            userItems,
            item => item.Kind == RemoteProtocol.ItemKinds.User);
        Assert.Equal(
            RemoteProtocol.MobileAttachmentCountLimit,
            projectedUser.Attachments!.Count);
        Assert.Contains(
            "omitted",
            projectedUser.Attachments[^1].FileName,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(projectedUser.Author!.Length <= RemoteProtocol.MobileMetadataTextLimit);
        Assert.All(projectedUser.Attachments, attachment =>
        {
            Assert.True(attachment.Path.Length <= RemoteProtocol.MobilePathLimit);
            Assert.True(attachment.FileName.Length <= RemoteProtocol.MobileFileNameLimit);
            if (attachment.Extension is not null)
                Assert.True(attachment.Extension.Length <= RemoteProtocol.MobileFileExtensionLimit);
        });

        var assistantTranscript = Build(
            chat,
            RemoteProjector.SelectTranscriptWindow([assistant], beforeMessageIndex: null));
        var assistantItems = assistantTranscript.Turns.SelectMany(turn => turn.Items).ToList();
        var projectedAssistant = Assert.Single(
            assistantItems,
            item => item.Kind == RemoteProtocol.ItemKinds.Assistant);
        Assert.Equal(RemoteProtocol.MobileSourceCountLimit, projectedAssistant.Sources!.Count);
        Assert.Contains(
            "omitted",
            projectedAssistant.Sources[^1].Title,
            StringComparison.OrdinalIgnoreCase);
        Assert.All(projectedAssistant.Sources, source =>
        {
            Assert.True(source.Title.Length <= RemoteProtocol.MobileSourceTitleLimit);
            if (source.Snippet is not null)
                Assert.True(source.Snippet.Length <= RemoteProtocol.MobileSourceSnippetLimit);
            if (source.Url is not null)
                Assert.True(source.Url.Length <= RemoteProtocol.MobileUrlLimit);
        });
        Assert.True(projectedAssistant.Author!.Length <= RemoteProtocol.MobileMetadataTextLimit);
        Assert.True(projectedAssistant.Model!.Length <= RemoteProtocol.MobileStatusValueLimit);
        Assert.True(projectedAssistant.Label!.Length <= RemoteProtocol.MobileMetadataTextLimit);

        var toolTranscript = Build(
            chat,
            RemoteProjector.SelectTranscriptWindow(tools, beforeMessageIndex: null));
        var toolItems = toolTranscript.Turns.SelectMany(turn => turn.Items).ToList();
        var group = Assert.Single(
            toolItems,
            item => item.Kind == RemoteProtocol.ItemKinds.ToolGroup);
        Assert.Equal(RemoteProtocol.MobileToolCallCountLimit, group.Tools!.Count);
        Assert.Contains(
            "omitted",
            group.Tools[^1].DisplayName,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(group.Label!.Length <= RemoteProtocol.MobileMetadataTextLimit);
        Assert.True(group.Status!.Length <= RemoteProtocol.MobileStatusValueLimit);
        Assert.All(group.Tools, AssertToolStringsAreBounded);

        AssertJsonIsComfortablyBounded(userTranscript);
        AssertJsonIsComfortablyBounded(assistantTranscript);
        AssertJsonIsComfortablyBounded(toolTranscript);
    }

    [Fact]
    public void HundredAlternatingMetadataHeavyMessages_HaveAHardAggregateJsonBound()
    {
        var huge = new string('\u05D0', 100_000);
        var chat = new Chat { Id = Guid.NewGuid(), Title = huge };
        var messages = new List<ChatMessage>(RemoteProtocol.TranscriptWindowRawMessageLimit);
        for (var index = 0; index < RemoteProtocol.TranscriptWindowRawMessageLimit; index++)
        {
            if (index % 2 == 0)
            {
                var tool = Message("tool", "");
                tool.ToolName = huge;
                tool.ToolCallId = huge;
                tool.ToolStatus = huge;
                tool.Author = huge;
                messages.Add(tool);
            }
            else
            {
                var assistant = Message("assistant", "");
                assistant.Author = huge;
                assistant.Model = huge;
                messages.Add(assistant);
            }
        }

        var window = RemoteProjector.SelectTranscriptWindow(messages, beforeMessageIndex: null);
        var transcript = Build(chat, window);
        var json = JsonSerializer.Serialize(transcript, RemoteJsonContext.Default.RemoteTranscript);
        var bytes = Encoding.UTF8.GetByteCount(json);

        Assert.Equal(RemoteProtocol.TranscriptWindowRawMessageLimit, window.Messages.Count);
        Assert.True(
            bytes <= RemoteProtocol.MobileTranscriptJsonByteLimit,
            $"Projected transcript was {bytes:N0} bytes.");
        Assert.EndsWith(
            RemoteProtocol.MobileTruncationMarker,
            transcript.Title,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedSingleMessage_StillMakesPagingProgress()
    {
        var huge = Message("tool", new string('x', RemoteProtocol.TranscriptWindowTextBudgetCharacters * 2));
        huge.ToolName = "view";
        var messages = new[]
        {
            Message("user", "first"),
            huge,
            Message("assistant", "last")
        };

        var latest = RemoteProjector.SelectTranscriptWindow(
            messages,
            beforeMessageIndex: null,
            maxMessages: 10,
            textBudgetCharacters: 32);
        var earlier = RemoteProjector.SelectTranscriptWindow(
            messages,
            beforeMessageIndex: latest.StartMessageIndex,
            maxMessages: 10,
            textBudgetCharacters: 32);

        Assert.Single(latest.Messages);
        Assert.Equal(messages[2].Id, latest.Messages[0].Id);
        Assert.Single(earlier.Messages);
        Assert.Equal(huge.Id, earlier.Messages[0].Id);
        Assert.True(earlier.StartMessageIndex < earlier.EndMessageIndex);
    }

    private static RemoteTranscript Build(Chat chat, TranscriptMessageWindow window) =>
        RemoteProjector.BuildTranscript(
            chat,
            window,
            new RemoteChatStatus { ChatId = chat.Id },
            showReasoning: true,
            showToolCalls: true,
            revision: 7);

    private static List<ChatMessage> CreateReproductionShape()
    {
        var normalContent = new string('c', 473);
        var normalToolOutput = new string('o', 64);
        var messages = new List<ChatMessage>(TotalMessageCount);
        var toolOrdinal = 0;

        for (var turn = 0; turn < UserMessageCount; turn++)
        {
            messages.Add(Message("user", normalContent));

            AddMessages(
                messages,
                "reasoning",
                BucketSize(ReasoningMessageCount, turn, UserMessageCount),
                normalContent);

            var toolsInTurn = BucketSize(ToolMessageCount, turn, UserMessageCount);
            for (var index = 0; index < toolsInTurn; index++, toolOrdinal++)
            {
                var content = toolOrdinal == ToolMessageCount / 2
                    ? new string('x', LargestToolContentLength)
                    : normalContent;
                var tool = Message("tool", content);
                tool.ToolName = "view";
                tool.ToolOutput = normalToolOutput;
                messages.Add(tool);
            }

            AddMessages(
                messages,
                "assistant",
                BucketSize(AssistantMessageCount, turn, UserMessageCount),
                normalContent);
        }

        return messages;
    }

    private static void AddMessages(
        ICollection<ChatMessage> messages,
        string role,
        int count,
        string content)
    {
        for (var index = 0; index < count; index++)
            messages.Add(Message(role, content));
    }

    private static int BucketSize(int total, int bucket, int bucketCount) =>
        total / bucketCount + (bucket < total % bucketCount ? 1 : 0);

    private static ChatMessage Message(string role, string content) =>
        new()
        {
            Id = Guid.NewGuid(),
            Role = role,
            Content = content,
            Timestamp = DateTimeOffset.UnixEpoch
        };

    private static void AssertItemStringsAreBounded(RemoteTranscriptItem item)
    {
        var limit = item.Kind switch
        {
            RemoteProtocol.ItemKinds.User => RemoteProtocol.MobileUserTextLimit,
            RemoteProtocol.ItemKinds.Reasoning => RemoteProtocol.MobileReasoningTextLimit,
            RemoteProtocol.ItemKinds.Terminal => RemoteProtocol.MobileTerminalTextLimit,
            _ => RemoteProtocol.MobileAssistantTextLimit
        };

        if (item.Text is not null)
            Assert.True(item.Text.Length <= limit);
    }

    private static void AssertToolStringsAreBounded(RemoteToolCall tool)
    {
        Assert.True(tool.Id.Length <= RemoteProtocol.MobileIdentifierLimit);
        Assert.True(tool.Name.Length <= RemoteProtocol.MobileMetadataTextLimit);
        if (tool.DisplayName is not null)
            Assert.True(tool.DisplayName.Length <= RemoteProtocol.MobileMetadataTextLimit);
        if (tool.Input is not null)
            Assert.True(tool.Input.Length <= RemoteProtocol.MobileToolInputLimit);
        if (tool.Output is not null)
            Assert.True(tool.Output.Length <= RemoteProtocol.MobileToolOutputLimit);
        Assert.True(tool.Status.Length <= RemoteProtocol.MobileStatusValueLimit);
    }

    private static void AssertTruncated(string? value, int limit)
    {
        Assert.NotNull(value);
        Assert.Equal(limit, value!.Length);
        Assert.EndsWith(RemoteProtocol.MobileTruncationMarker, value, StringComparison.Ordinal);
    }

    private static void AssertStatusCollection(IReadOnlyList<string> values, int stringLimit)
    {
        Assert.Equal(RemoteProtocol.MobileStatusCollectionCountLimit, values.Count);
        Assert.All(values, value =>
        {
            Assert.True(value.Length <= stringLimit);
            Assert.EndsWith(
                RemoteProtocol.MobileTruncationMarker,
                value,
                StringComparison.Ordinal);
        });
    }

    private static void AssertJsonIsComfortablyBounded(RemoteTranscript transcript)
    {
        var json = JsonSerializer.Serialize(transcript, RemoteJsonContext.Default.RemoteTranscript);
        var bytes = Encoding.UTF8.GetByteCount(json);
        Assert.True(
            bytes <= RemoteProtocol.MobileTranscriptJsonByteLimit,
            $"Bounded transcript was still {bytes:N0} bytes.");
    }

    private static string OptionsJson(
        int count,
        string option,
        string? firstOption = null)
    {
        var first = firstOption ?? option;
        var builder = new StringBuilder(
            (count * (option.Length + 3)) + Math.Max(0, first.Length - option.Length) + 2);
        builder.Append('[');
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
                builder.Append(',');
            builder.Append('"').Append(index == 0 ? first : option).Append('"');
        }

        return builder.Append(']').ToString();
    }
}
