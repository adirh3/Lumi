#if DEBUG || LUMI_MOBILE_SMOOTHNESS
using System.Text.Json;
using Lumi.Mobile.Smoothness;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class MobileSmoothnessFixtureTests
{
    [Fact]
    public void IndependentRunsHaveIdenticalWorkloads()
    {
        Assert.Equal(
            JsonSerializer.Serialize(MobileSmoothnessFixture.Snapshot(), RemoteJsonContext.Default.RemoteSnapshot),
            JsonSerializer.Serialize(MobileSmoothnessFixture.Snapshot(), RemoteJsonContext.Default.RemoteSnapshot));
        for (var index = 0; index < 4; index++)
            Assert.Equal(
                JsonSerializer.Serialize(MobileSmoothnessFixture.Transcript(MobileSmoothnessFixture.ChatId(index), 48),
                    RemoteJsonContext.Default.RemoteTranscript),
                JsonSerializer.Serialize(MobileSmoothnessFixture.Transcript(MobileSmoothnessFixture.ChatId(index), 48),
                    RemoteJsonContext.Default.RemoteTranscript));
    }

    [Fact]
    public void ChatSearchAndPagingDoNotMaterializeTheWholeDataset()
    {
        var first = MobileSmoothnessFixture.ChatPage(0, 50);
        Assert.Equal(1800, first.TotalCount);
        Assert.Equal(50, first.Groups.SelectMany(group => group.Chats).Count());
        Assert.True(first.HasMore);
        var distant = MobileSmoothnessFixture.ChatPage(1750, 50);
        Assert.Equal(50, distant.Groups.SelectMany(group => group.Chats).Count());
        Assert.False(distant.HasMore);
        var search = MobileSmoothnessFixture.ChatPage(0, 50, "Conversation 1751");
        Assert.Equal(1, search.TotalCount);
        Assert.Equal(MobileSmoothnessFixture.ChatId(1750), Assert.Single(Assert.Single(search.Groups).Chats).Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void InitialTranscriptRespectsTheProductionVisibleItemAndTextBudgets(int index)
    {
        var transcript = MobileSmoothnessFixture.Transcript(MobileSmoothnessFixture.ChatId(index),
            RemoteProtocol.InitialCompactTranscriptWindowVisibleItemLimit);
        Assert.InRange(transcript.Turns.Count, 1, 24);
        Assert.True(transcript.Turns.Sum(turn => turn.Items.Count) <= RemoteProtocol.InitialCompactTranscriptWindowVisibleItemLimit);
        Assert.True(transcript.Turns.Sum(turn => turn.Items.Sum(item => item.Text?.Length ?? 0))
            <= RemoteProtocol.CompactTranscriptWindowTextBudgetCharacters);
        Assert.True(transcript.TotalRawMessageCount >= 10000);
        Assert.True(transcript.HasEarlierMessages);
        Assert.True(transcript.IsLatestWindow);
        Assert.All(transcript.Turns, turn => Assert.NotEmpty(turn.Items));
    }

    [Fact]
    public void MixedTranscriptExercisesRealColdCardTemplatesWithoutRemovingPaging()
    {
        var transcript = MobileSmoothnessFixture.Transcript(MobileSmoothnessFixture.ChatId(3), 48);
        var kinds = transcript.Turns.SelectMany(turn => turn.Items).Select(item => item.Kind).ToHashSet();
        foreach (var kind in new[] { RemoteProtocol.ItemKinds.Question, RemoteProtocol.ItemKinds.Input,
                     RemoteProtocol.ItemKinds.ToolGroup, RemoteProtocol.ItemKinds.Terminal,
                     RemoteProtocol.ItemKinds.File, RemoteProtocol.ItemKinds.Activity })
            Assert.Contains(kind, kinds);
        Assert.Equal(48, transcript.Turns.Sum(turn => turn.Items.Count));
    }

    [Fact]
    public void UnknownChatsAndUnexpectedRoutesFailRatherThanPretendingToBeEmpty()
    {
        Assert.Throws<InvalidDataException>(() =>
            MobileSmoothnessFixture.Transcript(Guid.NewGuid(), 48));
    }

    [Fact]
    public void TheProtocolAndPickerCatalogAreRepresentative()
    {
        var snapshot = MobileSmoothnessFixture.Snapshot();
        Assert.True(RemoteProtocol.HasRequiredCapabilities(snapshot.Capabilities));
        Assert.Equal(72, snapshot.Settings.ModelOptions.Count);
        Assert.Equal(20, snapshot.Library.Projects.Count);
        Assert.Contains(snapshot.Settings.ModelOptions, model => model.IsPinned && model.Account is not null);
        Assert.Contains(snapshot.Settings.ModelOptions, model => model.Account is null);
        Assert.NotEmpty(snapshot.Library.Jobs);
        Assert.NotEmpty(snapshot.Library.Skills);
    }
}
#endif
