#if DEBUG
#pragma warning disable IL2026 // Test-only HTTP fixture; never compiled into shipping builds.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Lumi.Qa;

internal sealed class QaModelServer : IAsyncDisposable
{
    internal const string Model = "lumi-qa-fixture";
    internal static string FileText => "Lumi QA verified this write." + Environment.NewLine;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<string> _errors = new();
    private readonly Task _loop;
    private int _requests;

    internal string Url { get; }
    internal string Workspace { get; }
    internal int Requests => Volatile.Read(ref _requests);
    internal IReadOnlyList<string> Errors => _errors.ToArray();
    internal TaskCompletionSource ReleaseStream { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal QaModelServer(string workspace)
    {
        Workspace = workspace;
        Directory.CreateDirectory(workspace);
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var origin = $"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}";
        Url = origin + "/v1";
        port.Stop();
        _listener.Prefixes.Add(origin + "/");
        _listener.Start();
        _loop = RunAsync();
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().WaitAsync(_stop.Token); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (HttpListenerException) when (_stop.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { break; }

            try
            {
                await ReplyAsync(context);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                context.Response.Abort();
            }
            catch (Exception error)
            {
                _errors.Enqueue(error.ToString());
                Console.WriteLine("[qa-model] " + error);
                context.Response.Abort();
            }
        }
    }

    private async Task ReplyAsync(HttpListenerContext context)
    {
        if (context.Request.HttpMethod == "GET" && context.Request.Url!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
        {
            context.Response.ContentType = "application/json";
            await using var output = new StreamWriter(context.Response.OutputStream);
            await output.WriteAsync(JsonSerializer.Serialize(new
            {
                data = new[] { new { id = Model, @object = "model", owned_by = "local-qa" } }
            }, JsonOptions));
            return;
        }

        if (context.Request.HttpMethod != "POST"
            || !context.Request.Url!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected fixture request: {context.Request.HttpMethod} {context.Request.Url}");

        using var input = new StreamReader(context.Request.InputStream);
        using var request = JsonDocument.Parse(await input.ReadToEndAsync(_stop.Token));
        var root = request.RootElement;
        if (root.GetProperty("model").GetString() != Model || !root.GetProperty("stream").GetBoolean())
            throw new InvalidOperationException("The QA model must be selected and streamed.");

        var messages = root.GetProperty("messages").EnumerateArray().ToArray();
        var userIndex = Array.FindLastIndex(messages, message => message.GetProperty("role").GetString() == "user");
        if (userIndex < 0)
            throw new InvalidOperationException("The composer did not send a user message.");
        var prompt = messages[userIndex].GetProperty("content").GetRawText();
        var completedTool = messages.Skip(userIndex + 1).Any(message => message.GetProperty("role").GetString() == "tool");
        var sequence = Interlocked.Increment(ref _requests);
        Console.WriteLine($"[qa-model] Request {sequence}, tool result={completedTool}");

        context.Response.ContentType = "text/event-stream";
        context.Response.SendChunked = true;
        await using var writer = new StreamWriter(context.Response.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };
        async Task Chunk(object delta, string? finish = null)
        {
            var packet = new
            {
                id = "qa-" + sequence, @object = "chat.completion.chunk",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), model = Model,
                choices = new[] { new { index = 0, delta, finish_reason = finish } }
            };
            await writer.WriteAsync("data: " + JsonSerializer.Serialize(packet, JsonOptions) + "\n\n");
        }

        if (prompt.Contains("QA_STREAM", StringComparison.Ordinal))
        {
            await Chunk(new { role = "assistant", content = "LUMI_QA_" });
            await ReleaseStream.Task.WaitAsync(_stop.Token);
            await Chunk(new { content = "STREAM" });
        }
        else if (prompt.Contains("QA_DENY", StringComparison.Ordinal) || prompt.Contains("QA_ALLOW", StringComparison.Ordinal))
        {
            var deny = prompt.Contains("QA_DENY", StringComparison.Ordinal);
            if (completedTool)
            {
                await Chunk(new { role = "assistant", content = deny ? "LUMI_QA_DENIED" : "LUMI_QA_ALLOWED" });
            }
            else
            {
                var name = root.GetProperty("tools").EnumerateArray()
                    .Select(tool => tool.GetProperty("function").GetProperty("name").GetString())
                    .Single(name => name is not null && name.Split('.').Last() == "create")!;
                var arguments = JsonSerializer.Serialize(new
                {
                    path = Path.Combine(Workspace, deny ? "denied.txt" : "allowed.txt"),
                    file_text = FileText
                }, JsonOptions);
                await Chunk(new
                {
                    role = "assistant",
                    tool_calls = new[]
                    {
                        new { index = 0, id = "qa-call-" + sequence, type = "function", function = new { name, arguments } }
                    }
                });
                await Chunk(new { }, "tool_calls");
                await writer.WriteAsync("data: [DONE]\n\n");
                return;
            }
        }
        else
        {
            throw new InvalidOperationException("An unplanned AI request reached the QA fixture.");
        }

        await Chunk(new { }, "stop");
        await writer.WriteAsync("data: [DONE]\n\n");
    }

    public async ValueTask DisposeAsync()
    {
        ReleaseStream.TrySetResult();
        await _stop.CancelAsync();
        _listener.Stop();
        await _loop;
        _listener.Close();
        _stop.Dispose();
    }
}
#endif
