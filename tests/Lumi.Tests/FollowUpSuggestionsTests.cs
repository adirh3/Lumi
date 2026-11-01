using System;
using System.Collections.Generic;
using System.Linq;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public class FollowUpSuggestionsTests
{
    private static FollowUpSuggestionContext Context(
        string reply,
        string? user = "Can you fix the crash?",
        IReadOnlyList<SuggestionHistoryRanker.UsualReply>? usual = null)
        => new() { AssistantMessage = reply, UserMessage = user, UsualReplies = usual ?? [] };

    private static SuggestionHistoryRanker.UsualReply Usual(string text, double weight)
        => new(text, weight, 5);

    // ── The question ──────────────────────────────────────────────────────────

    [Fact]
    public void Question_IncludesItsRulesAndOnlyTheSuppliedContext()
    {
        var question = FollowUpSuggestions.BuildQuestion(Context("The soup needs another ten minutes.", "When is dinner ready?"));

        Assert.StartsWith(FollowUpSuggestions.SystemPrompt, question);
        Assert.Contains("the first message asks it to continue", FollowUpSuggestions.SystemPrompt);
        Assert.Contains($"Latest user message:{Environment.NewLine}When is dinner ready?", question);
        Assert.Contains($"Latest assistant reply:{Environment.NewLine}The soup needs another ten minutes.", question);
        Assert.DoesNotContain("Project:", question);
        Assert.DoesNotContain("Earlier in this chat", question);
        Assert.DoesNotContain("(their own words, most likely first)", question);
    }

    [Fact]
    public void Question_KeepsTheEndOfALongReply_WhereItsOfferIs()
    {
        var reply = "Opening paragraph about the investigation. " + new string('a', 4000)
            + " Want me to fix the 4 failing tests separately so CI goes green?";

        var question = FollowUpSuggestions.BuildQuestion(Context(reply));

        Assert.Contains("Opening paragraph about the investigation.", question);
        Assert.Contains("Want me to fix the 4 failing tests separately so CI goes green?", question);
        Assert.Contains("[…]", question);
        Assert.True(question.Length < FollowUpSuggestions.SystemPrompt.Length + 3200, $"question is {question.Length} characters");
    }

    [Fact]
    public void Question_DoesNotSplitSurrogatePairsWhereItCutsAReply()
    {
        var emoji = "\U0001F600";
        var reply = new string('a', 499) + emoji + new string('b', 3000) + emoji + new string('c', 1499);

        var question = FollowUpSuggestions.BuildQuestion(Context(reply));

        _ = new System.Text.UTF8Encoding(false, true).GetBytes(question);
    }

    [Fact]
    public void Question_ListsTheProjectEarlierTurnsAndUsualReplies()
    {
        var context = Context("Done — the build passes.", "Rename the setting") with
        {
            ProjectName = "Lumi",
            EarlierTurns = [new FollowUpTurn("Find where the setting is read", "It is read in SettingsViewModel.")],
            UsualReplies = [Usual("Commit and push to main", 40), Usual("Run code review", 30)],
        };

        var question = FollowUpSuggestions.BuildQuestion(context);

        Assert.Contains($"Project: Lumi", question);
        Assert.Contains("User: Find where the setting is read", question);
        Assert.Contains("Assistant: It is read in SettingsViewModel.", question);
        Assert.Contains($"- Commit and push to main{Environment.NewLine}- Run code review", question);
        Assert.True(question.IndexOf("Earlier in this chat", StringComparison.Ordinal)
                    < question.IndexOf("Latest user message", StringComparison.Ordinal));
    }

    [Fact]
    public void Question_ShowsTheLatestEarlierTurnsAndTheTopUsualRepliesWithinItsLimits()
    {
        var context = Context("Done.") with
        {
            EarlierTurns = Enumerable.Range(1, FollowUpSuggestions.EarlierTurnsShown + 2)
                .Select(static i => new FollowUpTurn($"request {i}", $"reply {i}")).ToList(),
            UsualReplies = Enumerable.Range(1, FollowUpSuggestions.UsualRepliesShown + 2)
                .Select(static i => Usual($"usual {i}", 10 - i)).ToList(),
        };

        var question = FollowUpSuggestions.BuildQuestion(context);

        Assert.DoesNotContain("User: request 2", question);
        Assert.Contains($"User: request {FollowUpSuggestions.EarlierTurnsShown + 2}", question);
        Assert.Contains($"- usual {FollowUpSuggestions.UsualRepliesShown}", question);
        Assert.DoesNotContain($"- usual {FollowUpSuggestions.UsualRepliesShown + 1}", question);
    }

    // ── Reading the model's answer ────────────────────────────────────────────

    [Theory]
    [InlineData("[\"Fix it\", \"Show the diff\", \"Run code review\"]")]
    [InlineData("```json\n[\"Fix it\", \"Show the diff\", \"Run code review\"]\n```")]
    [InlineData("Here you go: [\"Fix it\", \"Show the   diff\", \"Run code review\", \"Extra\"] Hope that helps.")]
    public void Parse_ReadsTheJsonArray(string raw)
        => Assert.Equal(["Fix it", "Show the diff", "Run code review"], FollowUpSuggestions.Parse(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I can't help with that.")]
    [InlineData("[\"  \", \"\"]")]
    public void Parse_ReturnsNullWithoutSuggestions(string? raw)
        => Assert.Null(FollowUpSuggestions.Parse(raw));
}
