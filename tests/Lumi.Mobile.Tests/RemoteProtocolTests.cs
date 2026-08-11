using System.Text.Json;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

/// <summary>
/// The wire contract is the only thing keeping two independently-deployed apps in agreement, so the
/// framing and serialization rules are pinned here.
/// </summary>
public class RemoteProtocolTests
{
    [Fact]
    public void EventFrame_RoundTripsThroughTheReader()
    {
        var frame = new RemoteEventFrame(RemoteProtocol.Events.ChatStatus, "{\"chatId\":\"x\"}");
        var reader = new RemoteEventFrame.Reader();

        RemoteEventFrame? parsed = null;
        foreach (var line in frame.ToWire().Split('\n'))
            parsed ??= reader.Push(line);

        Assert.NotNull(parsed);
        Assert.Equal(RemoteProtocol.Events.ChatStatus, parsed!.Value.Event);
        Assert.Equal("{\"chatId\":\"x\"}", parsed.Value.Data);
    }

    [Fact]
    public void EventFrame_PreservesMultiLinePayloads()
    {
        var payload = "line one\nline two\nline three";
        var reader = new RemoteEventFrame.Reader();

        RemoteEventFrame? parsed = null;
        foreach (var line in new RemoteEventFrame("snapshot", payload).ToWire().Split('\n'))
            parsed ??= reader.Push(line);

        Assert.Equal(payload, parsed!.Value.Data);
    }

    [Fact]
    public void EventFrame_IgnoresKeepAliveComments()
    {
        var reader = new RemoteEventFrame.Reader();

        Assert.Null(reader.Push(": keep-alive"));
        Assert.Null(reader.Push(""));
    }

    [Fact]
    public void EventFrame_ToleratesCarriageReturns()
    {
        var reader = new RemoteEventFrame.Reader();

        Assert.Null(reader.Push("event: chats"));
        Assert.Null(reader.Push("data: [1,2]"));
        var frame = reader.Push("");

        Assert.Equal("chats", frame!.Value.Event);
        Assert.Equal("[1,2]", frame.Value.Data);
    }

    [Fact]
    public void Snapshot_RoundTripsThroughTheSourceGeneratedContext()
    {
        var snapshot = new RemoteSnapshot
        {
            HostName = "LIGHTO-DESKTOP",
            IsConnected = true,
            ActiveChatId = Guid.NewGuid(),
            Chats = new RemoteChatPage
            {
                TotalCount = 1,
                Groups =
                [
                    new RemoteChatGroup
                    {
                        Label = "Today",
                        Chats = [new RemoteChat { Id = Guid.NewGuid(), Title = "שלום", Preview = "עברית" }]
                    }
                ]
            },
            Library = new RemoteLibrary { Skills = [new RemoteSkill { Name = "Doc", IconGlyph = "📄" }] },
            Settings = new RemoteSettings { UserName = "Adir", AvailableModels = ["a", "b"] }
        };

        var json = JsonSerializer.Serialize(snapshot, RemoteJsonContext.Default.RemoteSnapshot);
        var parsed = JsonSerializer.Deserialize(json, RemoteJsonContext.Default.RemoteSnapshot);

        Assert.NotNull(parsed);
        Assert.Equal(RemoteProtocol.Version, parsed!.ProtocolVersion);
        Assert.Equal("שלום", parsed.Chats.Groups[0].Chats[0].Title);
        Assert.Equal("📄", parsed.Library.Skills[0].IconGlyph);
        Assert.Equal(snapshot.ActiveChatId, parsed.ActiveChatId);
        Assert.Contains("\"hostName\"", json);
    }

    [Fact]
    public void Transcript_SurvivesAnUnknownItemKind()
    {
        // Forward compatibility: a newer desktop may emit kinds this build has never heard of.
        var json = """
        {"chatId":"00000000-0000-0000-0000-000000000001","title":"t","revision":3,
         "turns":[{"id":"t1","items":[{"id":"i1","kind":"hologram","text":"future"}]}]}
        """;

        var parsed = JsonSerializer.Deserialize(json, RemoteJsonContext.Default.RemoteTranscript);

        Assert.NotNull(parsed);
        Assert.Equal("hologram", parsed!.Turns[0].Items[0].Kind);

        var vm = TranscriptItemFactory.Create(parsed.Turns[0].Items[0]);

        // Degrade to plain readable text rather than dropping the row on the floor.
        var assistant = Assert.IsType<AssistantItemViewModel>(vm);
        Assert.Equal("future", assistant.Text);
    }

    [Fact]
    public void Command_RoundTripsListsAndTypedAccessors()
    {
        var chatId = Guid.NewGuid();
        var command = new RemoteCommand(RemoteProtocol.Actions.ConfigureFeature)
            .With("chatId", chatId.ToString())
            .With("isEnabled", "true")
            .With("timeout", "30")
            .WithList("toolNames", ["a", "b", "c"]);

        var json = JsonSerializer.Serialize(command, RemoteJsonContext.Default.RemoteCommand);
        var parsed = JsonSerializer.Deserialize(json, RemoteJsonContext.Default.RemoteCommand)!;

        Assert.Equal(chatId, parsed.GetGuid("chatId"));
        Assert.True(parsed.GetBool("isEnabled"));
        Assert.Equal(30, parsed.GetInt("timeout"));
        Assert.Equal(["a", "b", "c"], parsed.GetList("toolNames")!);
        Assert.Null(parsed.Get("missing"));
    }

    [Theory]
    [InlineData("192.168.1.5", "http://192.168.1.5:47653")]
    [InlineData("192.168.1.5:9000", "http://192.168.1.5:9000")]
    [InlineData("http://lumi-pc", "http://lumi-pc:47653")]
    [InlineData("http://192.168.1.5:8080/", "http://192.168.1.5:8080")]
    [InlineData("", "")]
    public void BaseUrlNormalization_AcceptsWhatAPersonWouldActuallyType(string input, string expected)
    {
        Assert.Equal(expected, LumiRemoteClient.NormalizeBaseUrl(input));
    }
}
