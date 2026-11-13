#pragma warning disable GHCP001 // Handles inference locally without a real Gemini account.
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Remote;
using Microsoft.Extensions.AI;
using Xunit;
using Xunit.Abstractions;

namespace Lumi.Tests;

[Trait("Category", "Integration")]
public sealed class GeminiProviderSessionIntegrationTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task DirectHttpPreservesGoogleOpenAiPath()
    {
        Skip.If(Environment.GetEnvironmentVariable("LUMI_INTEGRATION_TESTS") != "1",
            "Set LUMI_INTEGRATION_TESTS=1 to run the isolated native runtime test.");
        var root = Path.Combine(Path.GetTempPath(), $"Lumi-gemini-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var paths = new ConcurrentQueue<string>();
        using var server = new RemoteHttpListener(async (context, ct) =>
        {
            paths.Enqueue(context.Request.Path);
            var bytes = Encoding.UTF8.GetBytes(
                """data: {"id":"gemini-test","object":"chat.completion.chunk","created":1,"model":"gemini-2.5-flash","choices":[{"index":0,"delta":{"role":"assistant","content":"LUMI_GEMINI_HTTP_OK"},"finish_reason":"stop"}]}"""
                + "\n\ndata: [DONE]\n\n");
            using var stream = new MemoryStream(bytes);
            await context.WriteStreamAsync(stream, bytes.Length, "text/event-stream", new Dictionary<string, string>(), ct);
        });
        server.Start(0, loopbackOnly: true);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await using var client = new CopilotClient(new CopilotClientOptions
            {
                Mode = CopilotClientMode.Empty,
                BaseDirectory = Path.Combine(root, "copilot"),
                WorkingDirectory = root,
                UseLoggedInUser = false,
                Connection = RuntimeConnection.ForStdio(),
                LogLevel = CopilotLogLevel.Error
            });
            await client.StartAsync(timeout.Token);
            await using var session = await client.CreateSessionAsync(new SessionConfig
            {
                Model = "gemini-2.5-flash", Streaming = true,
                Provider = ByokConfigHelper.BuildProviderConfig(new ByokEndpoint
                {
                    Name = "Gemini HTTP fixture", ProviderType = "openai",
                    BaseUrl = $"http://127.0.0.1:{server.Port}/v1beta/openai",
                    ApiKeyMode = ByokApiKeyMode.Stored, ApiKey = "synthetic-gemini-key"
                }),
                AvailableTools = [], EnableSessionStore = false, EnableSessionTelemetry = false,
                EnableSkills = false, EnableFileHooks = false, RequestExtensions = false
            }, timeout.Token);
            var result = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = "Reply with LUMI_GEMINI_HTTP_OK." }, TimeSpan.FromSeconds(30), timeout.Token);
            Assert.Equal("LUMI_GEMINI_HTTP_OK", result?.Data.Content);
            Assert.Equal("/v1beta/openai/chat/completions", Assert.Single(paths));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableTheory]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai/")]
    public async Task ConnectionToolCheckAndResumedChatUseGoogleOpenAiRoute(string baseUrl)
    {
        Skip.If(Environment.GetEnvironmentVariable("LUMI_INTEGRATION_TESTS") != "1",
            "Set LUMI_INTEGRATION_TESTS=1 to run the isolated native runtime test.");
        var root = Path.Combine(Path.GetTempPath(), $"Lumi-gemini-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var inference = new GeminiInference();
        var toolCalled = false;
        var tool = AIFunctionFactory.Create(() =>
        {
            toolCalled = true;
            return "LUMI_CONNECTION_OK";
        }, "lumi_connection_check", "Read-only connection check.");
        var endpoint = new ByokEndpoint
        {
            Name = "Gemini API", BaseUrl = baseUrl, ProviderType = "openai", WireApi = "completions",
            ApiKeyMode = ByokApiKeyMode.Stored, ApiKey = "synthetic-gemini-key"
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;
        try
        {
            await using var client = new CopilotClient(new CopilotClientOptions
            {
                Mode = CopilotClientMode.Empty,
                BaseDirectory = Path.Combine(root, "copilot"),
                WorkingDirectory = root,
                UseLoggedInUser = false,
                Connection = RuntimeConnection.ForStdio(),
                RequestHandler = inference,
                LogLevel = CopilotLogLevel.Error
            });
            await client.StartAsync(ct);
            var session = await client.CreateSessionAsync((SessionConfig)Configure(resume: false), ct);
            try
            {
                var response = await session.SendAndWaitAsync(
                    new MessageOptions { Prompt = "Verify the connection by calling lumi_connection_check." },
                    TimeSpan.FromSeconds(30), ct);
                Assert.True(toolCalled);
                Assert.Equal("LUMI_CONNECTION_OK", response?.Data.Content);
                var sessionId = session.SessionId;
                await session.DisposeAsync();
                session = await client.ResumeSessionAsync(sessionId, (ResumeSessionConfig)Configure(resume: true), ct);
                response = await session.SendAndWaitAsync(
                    new MessageOptions { Prompt = "Reply with LUMI_GEMINI_CHAT_OK." }, TimeSpan.FromSeconds(30), ct);
                Assert.Equal("LUMI_GEMINI_CHAT_OK", response?.Data.Content);
                Assert.Equal(3, inference.Requests.Count);
                var requests = inference.Requests.ToArray();
                Assert.All(requests, request =>
                {
                    output.WriteLine(request.Url);
                    Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", request.Url);
                    using var body = JsonDocument.Parse(request.Body);
                    Assert.Equal("gemini-3.5-flash-lite", body.RootElement.GetProperty("model").GetString());
                    foreach (var message in body.RootElement.GetProperty("messages").EnumerateArray())
                        if (message.TryGetProperty("tool_calls", out _))
                            output.WriteLine("Assistant tool history: " + message.GetRawText());
                });
                using var warmRequest = JsonDocument.Parse(requests[1].Body);
                var assistant = Assert.Single(warmRequest.RootElement.GetProperty("messages").EnumerateArray(),
                    message => message.TryGetProperty("tool_calls", out _));
                Assert.False(assistant.TryGetProperty("refusal", out var refusal) && refusal.ValueKind == JsonValueKind.Null);
                var call = Assert.Single(assistant.GetProperty("tool_calls").EnumerateArray());
                Assert.Equal("synthetic-gemini-thought-signature",
                    call.GetProperty("extra_content").GetProperty("google").GetProperty("thought_signature").GetString());
            }
            finally
            {
                await session.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        SessionConfigBase Configure(bool resume)
        {
            SessionConfigBase config = resume
                ? SessionConfigBuilder.BuildForResume(
                    "Synthetic Gemini check.", null, root, null, [], [], [tool], null, null, null, null,
                    enableCapabilityDiscovery: false)
                : SessionConfigBuilder.Build(
                    "Synthetic Gemini check.", null, root, null, [], [], [tool], null, null, null, null,
                    enableCapabilityDiscovery: false);
            config.ConfigDirectory = Path.Combine(root, "config");
            config.Model = "gemini-3.5-flash-lite";
            config.Provider = ByokConfigHelper.BuildProviderConfig(endpoint);
            config.EnableSessionStore = false;
            config.EnableSessionTelemetry = false;
            config.EnableFileHooks = false;
            config.EnableSkills = false;
            config.RequestExtensions = false;
            config.AvailableTools = ["lumi_connection_check"];
            return config;
        }
    }

    private sealed class GeminiInference : CopilotRequestHandler
    {
        public ConcurrentQueue<(string Url, string Body)> Requests { get; } = new();
        private int _requestCount;

        protected override async Task<HttpResponseMessage> SendRequestAsync(
            HttpRequestMessage request, CopilotRequestContext context)
        {
            Assert.Equal("synthetic-gemini-key", request.Headers.Authorization?.Parameter);
            Requests.Enqueue((request.RequestUri!.AbsoluteUri,
                await request.Content!.ReadAsStringAsync(context.CancellationToken)));
            var number = Interlocked.Increment(ref _requestCount);
            Assert.InRange(number, 1, 3);
            var delta = number == 1
                ? """{"role":"assistant","tool_calls":[{"index":0,"id":"gemini-check","type":"function","function":{"name":"lumi_connection_check","arguments":"{}"},"extra_content":{"google":{"thought_signature":"synthetic-gemini-thought-signature"}}}]}"""
                : $$$"""{"role":"assistant","content":"{{{(number == 2 ? "LUMI_CONNECTION_OK" : "LUMI_GEMINI_CHAT_OK")}}}"}""";
            var chunk = $$$"""{"id":"gemini-test","object":"chat.completion.chunk","created":1,"model":"gemini-3.5-flash-lite","choices":[{"index":0,"delta":{{{delta}}},"finish_reason":"{{{(number == 1 ? "tool_calls" : "stop")}}}"}]}""";
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent($"data: {chunk}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
