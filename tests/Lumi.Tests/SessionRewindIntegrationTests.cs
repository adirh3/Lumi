#pragma warning disable GHCP001 // Isolates inference through the SDK's request handler instead of a real account.
using System.Net;
using System.Text;
using System.Text.Json;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Lumi.Tests;

/// <summary>
/// Going back to before a turn on the real runtime, with no account: a chat's session (built as Lumi
/// builds it, so it tracks the files its agent changes) creates and edits files through the agent's
/// own tools; a bare handle, as the edit bar reads a session that isn't live, sees what an edit
/// would put back; then the edit's rewind puts the files back and leaves the person's own changes alone.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionRewindIntegrationTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task RealSession_PutsBackTheFilesItsAgentChanged_AndKeepsThePersonsOwnChanges()
    {
        Skip.If(Environment.GetEnvironmentVariable("LUMI_INTEGRATION_TESTS") != "1",
            "Set LUMI_INTEGRATION_TESTS=1 to run the isolated native runtime test.");
        var root = Path.Combine(Path.GetTempPath(), $"Lumi-rewind-session-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "work");
        Directory.CreateDirectory(workspace);
        var notes = Path.Combine(workspace, "notes.md");
        var app = Path.Combine(workspace, "app.txt");
        var draft = Path.Combine(workspace, "draft.txt");
        File.WriteAllText(app, "original\n");
        var handler = new ScriptedEdits(notes, app, draft);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        try
        {
            // The default mode, unlike the empty one, offers the runtime's own file tools.
            await using var client = new CopilotClient(new CopilotClientOptions
            {
                BaseDirectory = Path.Combine(root, "copilot"),
                WorkingDirectory = workspace,
                UseLoggedInUser = false,
                Connection = RuntimeConnection.ForStdio(),
                RequestHandler = handler,
                LogLevel = CopilotLogLevel.Error
            });
            await client.StartAsync(ct);
            var session = await client.CreateSessionAsync(Configure(root, workspace), ct);
            var sessionId = session.SessionId;
            try
            {
                await Send(session, "TURN 1", ct);
                await Send(session, "TURN 2", ct);
                Assert.Equal("first\n", Read(notes));
                Assert.Equal("changed\n", Read(app));
                Assert.Equal("draft\n", Read(draft));

                // The edit of the second message: what it would put back.
                var secondTurn = await FindTurnAsync(session, userMessagesBefore: 1, ct);
                var preview = await SessionRewind.PreviewFilesAsync(session, secondTurn, ct);
                Assert.False(preview.IsBusy);
                Assert.Equal(
                    [new RewindFile(app, RewindFileChange.Modified, 1, 1), new RewindFile(draft, RewindFileChange.Created, 1, 0)],
                    preview.Files.OrderBy(static file => file.Path, StringComparer.Ordinal));

                // An edit in a chat whose session isn't live reads it through a bare handle, with no
                // prompt, tools or connectors: it sees the same files.
                await session.DisposeAsync();
                var bareConfig = SessionConfigBuilder.BuildForReadOnlyResume(Provider());
                bareConfig.ConfigDirectory = Path.Combine(root, "config");
                var bare = await client.ResumeSessionAsync(sessionId, bareConfig, ct);
                try
                {
                    var barePreview = await SessionRewind.PreviewFilesAsync(bare, secondTurn, ct);
                    Assert.Equal(
                        preview.Files.OrderBy(static file => file.Path, StringComparer.Ordinal),
                        barePreview.Files.OrderBy(static file => file.Path, StringComparer.Ordinal));
                }
                finally
                {
                    await bare.DisposeAsync();
                }

                // Sending the edit resumes the session as a chat does, then goes back.
                session = await client.ResumeSessionAsync(sessionId, Resume(root, workspace), ct);

                // The person changes a file the agent created after it did: going back leaves it.
                File.WriteAllText(draft, "my own words\n");
                var rewind = await SessionRewind.RewindAsync(session, secondTurn, preview, ct);

                Assert.True(rewind.HistoryRewound);
                var files = rewind.Files!;
                Assert.Equal(RewindFilesStatus.Restored, files.Status);
                Assert.Equal([new RewindFile(app, RewindFileChange.Modified, 1, 1)], files.Restored);
                Assert.Equal(
                    [new RewindKeptFile(new RewindFile(draft, RewindFileChange.Created, 1, 0), RewindKeptReason.ChangedSince)],
                    files.Kept);
                Assert.Equal("original\n", Read(app));
                Assert.Equal("my own words\n", Read(draft));
                Assert.Equal("first\n", Read(notes));
                Assert.Equal(1, CountTurns(await session.GetEventsAsync(ct)));

                // Going back without the files cuts only the conversation.
                var firstTurn = await FindTurnAsync(session, userMessagesBefore: 0, ct);
                var conversationOnly = await SessionRewind.RewindAsync(session, firstTurn, restore: null, ct);

                Assert.True(conversationOnly.HistoryRewound);
                Assert.Null(conversationOnly.Files);
                Assert.Equal("first\n", Read(notes));
                Assert.Equal(0, CountTurns(await session.GetEventsAsync(ct)));
                output.WriteLine("PASS: preview (live and bare), file restore with a kept change, and a conversation-only rewind.");
            }
            finally
            {
                await session.DisposeAsync();
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<string> FindTurnAsync(CopilotSession session, int userMessagesBefore, CancellationToken ct)
        => PendingTurnRecoveryAnalyzer.SelectEditTruncationTarget(await session.GetEventsAsync(ct), userMessagesBefore)
               ?.Id.ToString()
           ?? throw new InvalidOperationException($"No user turn after {userMessagesBefore} user messages.");

    // The person's turns, as an edit lines them up with the chat: injected messages aren't theirs.
    private static int CountTurns(IReadOnlyList<SessionEvent> events)
        => events.OfType<UserMessageEvent>().Count(static message => string.IsNullOrEmpty(message.Data?.Source));

    // The runtime's tools write the platform's line endings.
    private static string Read(string path) => File.ReadAllText(path).ReplaceLineEndings("\n");

    private static SessionConfig Configure(string root, string workspace)
    {
        var config = SessionConfigBuilder.Build(
            "Synthetic rewind check.", null, workspace, null, [], [], [], null, null, null, null,
            enableCapabilityDiscovery: false);
        Assert.True(config.EnableFileChangeTracking);
        config.ConfigDirectory = Path.Combine(root, "config");
        // A neutral model id: the runtime gives GPT-family models apply_patch instead of create and edit.
        config.Model = "lumi-rewind-model";
        config.Provider = Provider();
        config.InfiniteSessions = new InfiniteSessionConfig { Enabled = false };
        config.EnableSessionTelemetry = false;
        config.EnableFileHooks = false;
        config.EnableSkills = false;
        config.RequestExtensions = false;
        config.AvailableTools = new ToolSet().AddBuiltIn("create").AddBuiltIn("edit");
        return config;
    }

    private static ResumeSessionConfig Resume(string root, string workspace)
    {
        var config = SessionConfigBuilder.BuildForResume(
            "Synthetic rewind check.", null, workspace, null, [], [], [], null, null, null, null,
            provider: Provider(), enableCapabilityDiscovery: false);
        config.ConfigDirectory = Path.Combine(root, "config");
        config.Model = "lumi-rewind-model";
        return config;
    }

    private static ProviderConfig Provider() => new()
    {
        Type = "openai", WireApi = "completions",
        BaseUrl = "https://lumi-rewind-test.invalid/v1",
        ApiKey = "synthetic-test-key", ModelId = "lumi-rewind-model", WireModel = "lumi-rewind-model"
    };

    private static async Task Send(CopilotSession session, string prompt, CancellationToken ct)
    {
        var response = await session.SendAndWaitAsync(new MessageOptions { Prompt = prompt }, TimeSpan.FromSeconds(60), ct);
        Assert.NotNull(response);
        Assert.Contains("REWIND_TURN_OK", response.Data.Content);
    }

    // Uses the real SDK/CLI transport and tools, but answers inference with a fixed script: the first
    // turn creates notes.md; the second edits app.txt and creates draft.txt.
    private sealed class ScriptedEdits(string notes, string app, string draft) : CopilotRequestHandler
    {
        protected override async Task<HttpResponseMessage> SendRequestAsync(
            HttpRequestMessage request, CopilotRequestContext context)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(context.CancellationToken));
            var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToList();
            var turnStart = messages.FindLastIndex(static message =>
                message.GetProperty("role").GetString() == "user" && Text(message).Contains("TURN", StringComparison.Ordinal));
            var secondTurn = turnStart >= 0 && Text(messages[turnStart]).Contains("TURN 2", StringComparison.Ordinal);
            var done = messages.Skip(turnStart + 1).Count(static message => message.GetProperty("role").GetString() == "tool");
            (string Tool, (string Name, string Value)[] Arguments)[] script = secondTurn
                ? [("edit", [("path", app), ("old_str", "original"), ("new_str", "changed")]),
                   ("create", [("path", draft), ("file_text", "draft\n")])]
                : [("create", [("path", notes), ("file_text", "first\n")])];

            var calling = done < script.Length;
            var chunk = Chunk(writer =>
            {
                writer.WriteString("role", "assistant");
                if (!calling)
                {
                    writer.WriteString("content", "REWIND_TURN_OK");
                    return;
                }

                writer.WriteStartArray("tool_calls");
                writer.WriteStartObject();
                writer.WriteNumber("index", 0);
                writer.WriteString("id", $"rewind-call-{(secondTurn ? 2 : 1)}-{done}");
                writer.WriteString("type", "function");
                writer.WriteStartObject("function");
                writer.WriteString("name", script[done].Tool);
                writer.WriteString("arguments", Json(arguments =>
                {
                    foreach (var (name, value) in script[done].Arguments)
                        arguments.WriteString(name, value);
                }));
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndArray();
            }, calling ? "tool_calls" : "stop");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"data: {chunk}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            };
        }

        private static string Chunk(Action<Utf8JsonWriter> writeDelta, string finishReason)
            => Json(writer =>
            {
                writer.WriteString("id", "rewind-test");
                writer.WriteString("object", "chat.completion.chunk");
                writer.WriteNumber("created", 1);
                writer.WriteString("model", "lumi-rewind-model");
                writer.WriteStartArray("choices");
                writer.WriteStartObject();
                writer.WriteNumber("index", 0);
                writer.WriteStartObject("delta");
                writeDelta(writer);
                writer.WriteEndObject();
                writer.WriteString("finish_reason", finishReason);
                writer.WriteEndObject();
                writer.WriteEndArray();
            });

        private static string Json(Action<Utf8JsonWriter> writeProperties)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writeProperties(writer);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static string Text(JsonElement message)
        {
            var content = message.GetProperty("content");
            return content.ValueKind switch
            {
                JsonValueKind.String => content.GetString() ?? "",
                JsonValueKind.Array => string.Concat(content.EnumerateArray()
                    .Where(static part => part.TryGetProperty("text", out _))
                    .Select(static part => part.GetProperty("text").GetString())),
                _ => "",
            };
        }
    }
}
