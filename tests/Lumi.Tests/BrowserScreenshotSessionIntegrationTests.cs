#pragma warning disable GHCP001 // Isolates inference through the SDK's request handler instead of a real account.
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using GitHub.Copilot;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Lumi.Tests;

[Trait("Category", "Integration")]
public sealed class BrowserScreenshotSessionIntegrationTests(ITestOutputHelper output)
{
    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==";
    private const string HiddenReason =
        "Tab tab-fixture is hidden. Switch to this tab and show the browser panel before capturing.";

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealSession_StartsInvokesScreenshotAndResumes_WithoutUserProfileOrNetwork(bool hidden)
    {
        Skip.If(Environment.GetEnvironmentVariable("LUMI_INTEGRATION_TESTS") != "1",
            "Set LUMI_INTEGRATION_TESTS=1 to run the isolated native runtime test.");
        var root = Path.Combine(Path.GetTempPath(), $"Lumi-screenshot-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var handler = new ScreenshotInference();
        var captureCount = 0;
        Task<BrowserScreenshot> Capture(string? tabId)
        {
            Assert.Equal("tab-fixture", tabId);
            Interlocked.Increment(ref captureCount);
            return hidden
                ? Task.FromException<BrowserScreenshot>(new InvalidOperationException(HiddenReason))
                : Task.FromResult(new BrowserScreenshot(
                    "tab-fixture", "https://example.test/canvas", 1, 1, Convert.FromBase64String(PngBase64)));
        }

        output.WriteLine($"JSON reflection enabled: {JsonSerializer.IsReflectionEnabledByDefault}");
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
                RequestHandler = handler,
                LogLevel = CopilotLogLevel.Error
            });
            await client.StartAsync(ct);
            var session = await client.CreateSessionAsync(
                (SessionConfig)Configure(root, Capture, resume: false), ct);
            var sessionId = session.SessionId;
            try
            {
                await SendCheck(session, ct);
                Assert.Equal(0, captureCount);
                AssertScreenshotIsAdvertised(Assert.Single(handler.Requests));

                handler.BeginCapture();
                var completions = new ConcurrentQueue<bool>();
                using (session.On<ToolExecutionCompleteEvent>(ev =>
                {
                    if (ev.Data.ToolCallId == ScreenshotInference.CallId)
                        completions.Enqueue(ev.Data.Success == true);
                }))
                {
                    await SendCheck(session, ct);
                }
                Assert.Equal(1, captureCount);
                Assert.Equal(!hidden, Assert.Single(completions));
                var requests = handler.Requests.ToArray();
                Assert.Equal(2, requests.Length);
                using var modelRequest = JsonDocument.Parse(requests[1]);
                var messages = modelRequest.RootElement.GetProperty("messages").EnumerateArray().ToArray();
                var result = Assert.Single(messages, message =>
                    message.GetProperty("role").GetString() == "tool" &&
                    message.GetProperty("tool_call_id").GetString() == ScreenshotInference.CallId);
                var text = result.GetProperty("content").GetString();
                var images = messages.Select(message => message.GetProperty("content"))
                    .Where(content => content.ValueKind == JsonValueKind.Array)
                    .SelectMany(content => content.EnumerateArray())
                    .Where(content => content.GetProperty("type").GetString() == "image_url").ToArray();
                if (hidden)
                {
                    Assert.Equal($"Error: Browser screenshot failed. {HiddenReason}", text);
                    Assert.Empty(images);
                }
                else
                {
                    Assert.Equal($"data:image/png;base64,{PngBase64}",
                        Assert.Single(images).GetProperty("image_url").GetProperty("url").GetString());
                    Assert.Contains("tab-fixture", text);
                    Assert.Contains("https://example.test/canvas", text);
                    Assert.Contains("1 \u00d7 1 pixels.", text);
                }

                await session.DisposeAsync();
                handler.BeginOrdinaryTurn();
                session = await client.ResumeSessionAsync(
                    sessionId, (ResumeSessionConfig)Configure(root, Capture, resume: true), ct);
                await SendCheck(session, ct);
                Assert.Equal(1, captureCount);
                AssertScreenshotIsAdvertised(Assert.Single(handler.Requests));
                output.WriteLine($"PASS: ordinary create/resume and {(hidden ? "actionable failure" : "PNG model input")}.");
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
    }

    private static SessionConfigBase Configure(
        string root, Func<string?, Task<BrowserScreenshot>> capture, bool resume)
    {
        var tool = ChatViewModel.BuildBrowserScreenshotTool(capture);
        SessionConfigBase config = resume
            ? SessionConfigBuilder.BuildForResume(
                "Synthetic screenshot check.", null, root, null, [], [], [tool], null, null, null, null,
                enableCapabilityDiscovery: false)
            : SessionConfigBuilder.Build(
                "Synthetic screenshot check.", null, root, null, [], [], [tool], null, null, null, null,
                enableCapabilityDiscovery: false);
        config.ConfigDirectory = Path.Combine(root, "config");
        config.Model = "gpt-4o-mini";
        config.Provider = new ProviderConfig
        {
            Type = "openai", WireApi = "completions",
            BaseUrl = "https://lumi-screenshot-test.invalid/v1",
            ApiKey = "synthetic-test-key", ModelId = "gpt-4o-mini", WireModel = "gpt-4o-mini"
        };
        config.ModelCapabilities = new()
        {
            Supports = new() { Vision = true },
            Limits = new()
            {
                Vision = new() { MaxPromptImages = 5, MaxPromptImageSize = 3 * 1024 * 1024, SupportedMediaTypes = ["image/png"] }
            }
        };
        config.EnableSessionStore = false;
        config.EnableSessionTelemetry = false;
        config.EnableFileHooks = false;
        config.EnableSkills = false;
        config.RequestExtensions = false;
        config.AvailableTools = [ToolDisplayHelper.BrowserScreenshotToolName];
        return config;
    }

    private static async Task SendCheck(CopilotSession session, CancellationToken ct)
    {
        var response = await session.SendAndWaitAsync(
            new MessageOptions { Prompt = "Run the synthetic startup check." }, TimeSpan.FromSeconds(30), ct);
        Assert.NotNull(response);
        Assert.Contains("SCREENSHOT_SESSION_OK", response.Data.Content);
    }

    private static void AssertScreenshotIsAdvertised(string body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.Single(document.RootElement.GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("function").GetProperty("name").GetString() == ToolDisplayHelper.BrowserScreenshotToolName);
    }

    // Uses the real SDK/CLI transport, but handles inference locally with synthetic fixtures.
    private sealed class ScreenshotInference : CopilotRequestHandler
    {
        public const string CallId = "screenshot-fixture-call";
        public ConcurrentQueue<string> Requests { get; } = new();
        private bool _capture;
        private int _requestCount;

        public void BeginCapture()
        {
            BeginOrdinaryTurn();
            _capture = true;
        }

        public void BeginOrdinaryTurn()
        {
            _capture = false;
            _requestCount = 0;
            Requests.Clear();
        }

        protected override async Task<HttpResponseMessage> SendRequestAsync(
            HttpRequestMessage request, CopilotRequestContext context)
        {
            Assert.Equal("https://lumi-screenshot-test.invalid/v1/chat/completions", request.RequestUri?.AbsoluteUri);
            Requests.Enqueue(await request.Content!.ReadAsStringAsync(context.CancellationToken));
            var number = Interlocked.Increment(ref _requestCount);
            Assert.InRange(number, 1, _capture ? 2 : 1);
            var invoke = _capture && number == 1;
            var delta = invoke
                ? $$$"""{"role":"assistant","tool_calls":[{"index":0,"id":"{{{CallId}}}","type":"function","function":{"name":"{{{ToolDisplayHelper.BrowserScreenshotToolName}}}","arguments":"{\"tabId\":\"tab-fixture\"}"}}]}"""
                : """{"role":"assistant","content":"SCREENSHOT_SESSION_OK"}""";
            var chunk = $$$"""{"id":"screenshot-test","object":"chat.completion.chunk","created":1,"model":"gpt-4o-mini","choices":[{"index":0,"delta":{{{delta}}},"finish_reason":"{{{(invoke ? "tool_calls" : "stop")}}}"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"data: {chunk}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
