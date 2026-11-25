using System.Text.Json;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Tests;

public sealed class RemoteTranscriptReuseTests
{
    [Fact]
    public void ReconstructsLatestWindowWhenHistoryFallsOutAndAnExistingRowChanges()
    {
        var old = Row("older", new string('a', 8192));
        var user = Row("user", new string('b', 8192));
        var step = Row("step", "Running");
        var finished = Row("step", "Done");
        var reply = Row("answer", "Here are the results.");
        var baseline = new[] { old, user, step }.ToDictionary(RemoteTranscriptReuse.Key);
        var transcript = new RemoteTranscript
        {
            WindowStartMessageIndex = 12,
            Turns = [new RemoteTranscriptTurn { Id = "turn-0", Items = [user, finished, reply], FinalAnswerId = reply.Id }]
        };
        var expected = Serialize(transcript);

        RemoteTranscriptReuse.OmitKnownItems(transcript, baseline.Keys.ToHashSet());
        Assert.Equal(["step", "answer"], Assert.Single(transcript.Turns).Items.Select(item => item.Id));
        var wire = Serialize(transcript);
        Assert.True(wire.Length < expected.Length / 4);

        var received = JsonSerializer.Deserialize(wire, RemoteJsonContext.Default.RemoteTranscript)!;
        Assert.False(RemoteTranscriptReuse.TryRestore(received, new Dictionary<string, RemoteTranscriptItem>()));
        received = JsonSerializer.Deserialize(wire, RemoteJsonContext.Default.RemoteTranscript)!;
        Assert.True(RemoteTranscriptReuse.TryRestore(received, baseline));
        Assert.Equal(expected, Serialize(received));
    }

    private static string Serialize(RemoteTranscript transcript) =>
        JsonSerializer.Serialize(transcript, RemoteJsonContext.Default.RemoteTranscript);

    private static RemoteTranscriptItem Row(string id, string text) => new() { Id = id, Text = text };
}
