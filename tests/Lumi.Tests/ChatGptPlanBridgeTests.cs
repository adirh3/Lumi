using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services.Byok;
using Xunit;

namespace Lumi.Tests;

public sealed class ChatGptPlanBridgeTests
{
    [Fact]
    public void Normalize_KeepsRequestsWithinThePlanContract()
    {
        var source = JsonNode.Parse("""
            {"model":"gpt-5.5","store":true,"stream":false,"max_output_tokens":100,"temperature":0.2,"top_p":1,
             "previous_response_id":"resp_1","metadata":{"a":"b"},"user":"u","truncation":"auto",
             "prompt_cache_key":"key","instructions":"Be brief.","reasoning":{"effort":"high"},
             "input":[{"type":"message","role":"system","content":"Rules"},{"role":"user","content":"Hi"}],
             "tools":[{"type":"function","name":"read"},{"type":"image_generation"},{"type":"web_search"}]}
            """)!.AsObject();

        var body = ChatGptRequestNormalizer.Normalize(source, lite: false);

        Assert.False(body["store"]!.GetValue<bool>());
        Assert.True(body["stream"]!.GetValue<bool>());
        foreach (var field in new[] { "max_output_tokens", "temperature", "top_p", "previous_response_id", "metadata", "user", "truncation" })
            Assert.False(body.ContainsKey(field), field);
        Assert.Equal("key", body["prompt_cache_key"]!.GetValue<string>());
        Assert.Equal("Be brief.", body["instructions"]!.GetValue<string>());
        Assert.Equal("developer", body["input"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("user", body["input"]![1]!["role"]!.GetValue<string>());
        Assert.Equal(["read", null], body["tools"]!.AsArray().Select(tool => tool!["name"]?.GetValue<string>()));
        // The request the runtime sent is left untouched for retries.
        Assert.True(source["store"]!.GetValue<bool>());
    }

    [Fact]
    public void Normalize_TextInputBecomesAMessage()
    {
        var body = ChatGptRequestNormalizer.Normalize(JsonNode.Parse("""{"model":"m","input":"Hello"}""")!.AsObject(), lite: false);
        var message = Assert.Single(body["input"]!.AsArray())!;
        Assert.Equal("user", message["role"]!.GetValue<string>());
        Assert.Equal("Hello", message["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_DropsTheRuntimesCacheBreakpoints()
    {
        // As the runtime sends instructions to GPT-5.6 and newer models.
        var source = JsonNode.Parse("""
            {"model":"gpt-6.1-sol","prompt_cache_key":"key","input":[
              {"type":"message","role":"developer","prompt_cache_breakpoint":{"mode":"explicit"},"content":[
                {"type":"input_text","text":"Rules","prompt_cache_breakpoint":{"mode":"explicit"}}]},
              {"type":"message","role":"user","content":[{"type":"input_text","text":"Hi"}]}]}
            """)!.AsObject();

        foreach (var lite in new[] { false, true })
        {
            var body = ChatGptRequestNormalizer.Normalize(source, lite);

            Assert.DoesNotContain("prompt_cache_breakpoint", body.ToJsonString());
            Assert.Equal("Rules", body["input"]![0]!["content"]![0]!["text"]!.GetValue<string>());
            Assert.Equal("key", body["prompt_cache_key"]!.GetValue<string>());
        }
    }

    [Fact]
    public void Normalize_Lite_MovesToolsIntoAdditionalToolsLikeCodex()
    {
        var source = JsonNode.Parse("""
            {"model":"gpt-6.1-sol","parallel_tool_calls":true,
             "input":[{"role":"user","content":"Hi"},
                      {"type":"function_call","name":"read","call_id":"c1","arguments":"{}"},
                      {"type":"function_call_output","call_id":"c1","output":"ok"}],
             "tools":[{"type":"function","name":"read"},{"type":"custom","name":"apply_patch"},{"type":"web_search"}]}
            """)!.AsObject();

        var body = ChatGptRequestNormalizer.Normalize(source, lite: true);

        Assert.False(body.ContainsKey("tools"));
        Assert.False(body["parallel_tool_calls"]!.GetValue<bool>());
        var input = body["input"]!.AsArray();
        var additional = input[0]!;
        Assert.Equal("additional_tools", additional["type"]!.GetValue<string>());
        Assert.Equal("developer", additional["role"]!.GetValue<string>());
        var functions = additional["tools"]![0]!;
        Assert.Equal("namespace", functions["type"]!.GetValue<string>());
        Assert.Equal("functions", functions["name"]!.GetValue<string>());
        Assert.Equal(["read", "apply_patch"], functions["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()));
        Assert.Equal("web_search", additional["tools"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("functions", input[2]!["namespace"]!.GetValue<string>());
    }

    [Fact]
    public async Task Bridge_AddsTheCurrentTokenAndStreamsTheReply()
    {
        await using var harness = new Harness();
        var config = harness.Connect("ep1", seed: true);

        using var response = await harness.PostAsync(config, """{"model":"gpt-5.5","store":true,"input":[]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Harness.Events, await response.Content.ReadAsStringAsync());
        var sent = Assert.Single(harness.Upstream);
        Assert.Equal("Bearer access-seed", sent.Authorization);
        Assert.Equal("https://api.openai.com/v1/responses", sent.Url);
        Assert.False(JsonNode.Parse(sent.Body)!["store"]!.GetValue<bool>());
        Assert.StartsWith("http://127.0.0.1:", config.BaseUrl);
        Assert.NotEqual("access-seed", config.ApiKey);
    }

    [Fact]
    public async Task Bridge_RejectsCallersWithoutItsKey()
    {
        await using var harness = new Harness();
        var config = harness.Connect("ep1", seed: true);

        using var wrongKey = await harness.PostAsync(config, "{}", key: "guess");
        using var noRoute = await harness.PostAsync(config, "{}", path: "/models");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongKey.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noRoute.StatusCode);
        Assert.Empty(harness.Upstream);
    }

    [Fact]
    public async Task Bridge_RenewsRotatingTokensAndRetriesARejectedOne()
    {
        await using var harness = new Harness();
        await harness.Keys.SetAsync(SecureKeyStoreFactory.EndpointKey("ep2"), "refresh-0");
        var config = harness.Connect("ep2", seed: false, stored: true);
        harness.Respond = attempt => attempt == 1
            ? FakeChatGpt.Json("""{"error":{"code":"invalid_token","message":"expired"}}""", HttpStatusCode.Unauthorized)
            : null;

        using var response = await harness.PostAsync(config, """{"model":"gpt-5.5","input":[]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Bearer access-1", "Bearer access-2"], harness.Upstream.Select(request => request.Authorization));
        Assert.Equal(["refresh-0", "refresh-1"], harness.Auth.Grants("refresh_token").Select(grant => grant.Form["refresh_token"]));
        // Each replacement is stored at once: a spent refresh token can never be presented again.
        Assert.Equal("refresh-2", await harness.Keys.GetAsync(SecureKeyStoreFactory.EndpointKey("ep2")));
    }

    [Fact]
    public async Task Bridge_UsesAdditionalToolsWhenThePlanRouteRejectsTopLevelTools()
    {
        await using var harness = new Harness();
        var config = harness.Connect("ep1", seed: true);
        harness.Respond = attempt => attempt == 1
            ? FakeChatGpt.Json("""{"error":{"code":"subscription_sharing_unsupported_capability","param":"tools[0]"}}""",
                HttpStatusCode.BadRequest)
            : null;
        const string request = """{"model":"gpt-6.1-sol","input":[],"tools":[{"type":"function","name":"read"}]}""";

        using var first = await harness.PostAsync(config, request);
        using var second = await harness.PostAsync(config, request);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var bodies = harness.Upstream.Select(sent => JsonNode.Parse(sent.Body)!).ToList();
        Assert.Equal(3, bodies.Count);
        Assert.NotNull(bodies[0]["tools"]);
        // The model is remembered, so later requests go out in the accepted form directly.
        Assert.All(bodies.Skip(1), body =>
        {
            Assert.Null(body["tools"]);
            Assert.Equal("additional_tools", body["input"]![0]!["type"]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task Bridge_SendsCatalogLiteModelsInCodexFormFromTheStart()
    {
        await using var harness = new Harness();
        harness.Auth.ModelsJson = """
            {"models":[{"slug":"gpt-lite","visibility":"list","use_responses_lite":true},{"slug":"gpt-std","visibility":"list"}]}
            """;
        var config = harness.Connect("ep1", seed: true);

        using var lite = await harness.PostAsync(config,
            """{"model":"gpt-lite","reasoning":{"effort":"low"},"input":[],"tools":[{"type":"function","name":"read"}]}""");
        using var standard = await harness.PostAsync(config,
            """{"model":"gpt-std","input":[],"tools":[{"type":"function","name":"read"}]}""");

        var sent = harness.Upstream.ToList();
        Assert.Equal(2, sent.Count);
        var liteBody = JsonNode.Parse(sent[0].Body)!;
        Assert.True(sent[0].LiteHeader);
        Assert.Null(liteBody["tools"]);
        Assert.Equal("additional_tools", liteBody["input"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("all_turns", liteBody["reasoning"]!["context"]!.GetValue<string>());
        Assert.False(sent[1].LiteHeader);
        Assert.NotNull(JsonNode.Parse(sent[1].Body)!["tools"]);
        Assert.Single(harness.Auth.Requests, request => request.Url == ChatGptAuthService.ApiBaseUrl + "/models");
    }

    [Fact]
    public async Task Bridge_ExplainsAnUnknownRefusalWithChatGptsOwnWords()
    {
        Loc.Load("en");
        await using var harness = new Harness();
        var config = harness.Connect("ep1", seed: true);
        harness.Respond = _ => FakeChatGpt.Json(
            """{"error":{"code":"model_not_found","message":"The model does not exist."}}""", HttpStatusCode.NotFound);

        using var response = await harness.PostAsync(config, """{"model":"gpt-5.5","input":[]}""");

        var expected = string.Format(Loc.Get("ChatGpt_Refused"), "The model does not exist.");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(expected, JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!["message"]!.GetValue<string>());
        Assert.Equal(expected, harness.Bridge.TakeFailure("ep1"));
        Assert.Null(harness.Bridge.TakeFailure("ep1"));
    }

    [Fact]
    public async Task Bridge_WordsAReachedPlanLimitAndAsksNotToRetry()
    {
        Loc.Load("en");
        await using var harness = new Harness();
        var config = harness.Connect("ep1", seed: true);
        harness.Respond = _ => FakeChatGpt.Json(
            """{"error":{"code":"subscription_sharing_usage_limit_exceeded","message":"limit"}}""", HttpStatusCode.TooManyRequests);

        using var response = await harness.PostAsync(config, """{"model":"gpt-5.5","input":[]}""");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("false", response.Headers.GetValues("x-should-retry").Single());
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!;
        Assert.Equal(Loc.Get("ChatGpt_UsageLimit"), error["message"]!.GetValue<string>());
        Assert.Equal("subscription_sharing_usage_limit_exceeded", error["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Bridge_StopsAndAsksToSignInWhenRenewalIsRefused()
    {
        Loc.Load("en");
        await using var harness = new Harness();
        await harness.Keys.SetAsync(SecureKeyStoreFactory.EndpointKey("ep3"), "refresh-0");
        harness.Auth.RefreshResponse = () => FakeChatGpt.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        var config = harness.Connect("ep3", seed: false, stored: true);
        var signInRequired = new ConcurrentBag<string>();
        harness.Bridge.SignInRequired += signInRequired.Add;

        using var first = await harness.PostAsync(config, """{"model":"gpt-5.5","input":[]}""");
        using var second = await harness.PostAsync(config, """{"model":"gpt-5.5","input":[]}""");

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Equal(Loc.Get("ChatGpt_SignInExpired"),
            JsonNode.Parse(await first.Content.ReadAsStringAsync())!["error"]!["message"]!.GetValue<string>());
        Assert.Equal(["ep3"], signInRequired);
        Assert.Single(harness.Auth.Grants("refresh_token"));
        Assert.Empty(harness.Upstream);
    }

    [Fact]
    public async Task Events_RewordPlanFailuresThatArriveMidStream()
    {
        Loc.Load("en");
        const string failed = "data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"subscription_sharing_usage_limit_exceeded\",\"message\":\"raw\"}}}\n";
        var stream = "event: response.created\ndata: {\"type\":\"response.created\"}\n\nevent: response.failed\n" + failed + "\n";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(stream));
        using var destination = new MemoryStream();

        await ChatGptPlanErrors.CopyEventsAsync(source, destination, CancellationToken.None);

        var lines = Encoding.UTF8.GetString(destination.ToArray()).Split('\n');
        Assert.Equal("data: {\"type\":\"response.created\"}", lines[1]);
        var rewritten = JsonNode.Parse(lines[4]["data: ".Length..])!;
        Assert.Equal(Loc.Get("ChatGpt_UsageLimit"), rewritten["response"]!["error"]!["message"]!.GetValue<string>());
        Assert.Equal(stream.Length - failed.Length, Encoding.UTF8.GetString(destination.ToArray()).Length - lines[4].Length - 1);
    }

    [Fact]
    public async Task Events_GiveTheFinalResponseTheItemsThePlanRouteLeftOut()
    {
        // As the plan route ends a turn: every item streamed, then "output":[] in response.completed.
        const string reasoning = """{"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":"enc"}""";
        const string call = """{"type":"function_call","id":"fc_1","call_id":"call_1","name":"powershell","arguments":"{}","status":"completed"}""";
        var stream = string.Join("\n\n",
            "event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"id\":\"r\",\"output\":[]}}",
            "event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\",\"output_index\":1,\"item\":" + call + "}",
            "event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":" + reasoning + "}",
            "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"output\":[]}}") + "\n\n";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(stream));
        using var destination = new MemoryStream();

        await ChatGptPlanErrors.CopyEventsAsync(source, destination, CancellationToken.None);

        var events = Encoding.UTF8.GetString(destination.ToArray()).Split('\n').Where(line => line.StartsWith("data: ")).ToList();
        Assert.Equal(4, events.Count);
        Assert.Equal(stream.Split('\n').Where(line => line.StartsWith("data: ")).Take(3), events.Take(3));
        var completed = JsonNode.Parse(events[3]["data: ".Length..])!["response"]!;
        Assert.Equal("completed", completed["status"]!.GetValue<string>());
        Assert.Equal(["rs_1", "fc_1"], completed["output"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()));
        Assert.Equal("powershell", completed["output"]![1]!["name"]!.GetValue<string>());
        Assert.Equal("data: {\"type\":\"response.created\",\"response\":{\"id\":\"r\",\"output\":[]}}", events[0]);
    }

    [Fact]
    public void Events_LeaveAFinalResponseThatHasItsOutput()
    {
        var items = new SortedDictionary<int, JsonNode> { [0] = JsonNode.Parse("""{"type":"message","id":"m_stale"}""")! };
        var line = Encoding.UTF8.GetBytes("data: {\"type\":\"response.completed\",\"response\":{\"output\":[{\"type\":\"message\",\"id\":\"m_1\"}]}}\n");

        Assert.Null(ChatGptPlanErrors.CompleteOutput(line, items));
    }

    [Fact]
    public async Task Events_CompleteAFinalResponseThatMentionsAnotherEventName()
    {
        // The final response echoes the request's tools, whose descriptions may name any event.
        const string item = """{"type":"message","id":"msg_1","role":"assistant","content":[]}""";
        var stream = "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":" + item + "}\n\n"
            + "data: {\"type\":\"response.completed\",\"response\":{\"output\":[],\"tools\":[{\"type\":\"function\","
            + "\"name\":\"probe\",\"description\":\"Reads response.output_item.done events.\"}]}}\n\n";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(stream));
        using var destination = new MemoryStream();

        await ChatGptPlanErrors.CopyEventsAsync(source, destination, CancellationToken.None);

        var completed = Encoding.UTF8.GetString(destination.ToArray()).Split('\n')
            .Last(line => line.StartsWith("data: ") && line.Contains("response.completed"));
        var output = Assert.Single(JsonNode.Parse(completed["data: ".Length..])!["response"]!["output"]!.AsArray());
        Assert.Equal("msg_1", output!["id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("/chatgpt/abc123/v1/responses", true)]
    [InlineData("/chatgpt/abc123/v1/models", false)]
    [InlineData("/chatgpt/../v1/responses", false)]
    [InlineData("/other/abc123/v1/responses", false)]
    public void Routes_OnlyServeResponsesForAConnection(string path, bool valid)
        => Assert.Equal(valid, ChatGptPlanBridge.TryParseRoute(path, out _));

    /// <summary>A bridge whose ChatGPT auth and plan endpoints are in-process fakes.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public const string Events = "event: response.created\ndata: {\"type\":\"response.created\"}\n\n"
            + "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n";

        private readonly HttpClient _authHttp;
        private readonly ChatGptAuthService _auth;
        private readonly HttpClient _client = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
        private int _attempts;

        public Harness()
        {
            Auth = new FakeChatGpt();
            _authHttp = new HttpClient(Auth);
            _auth = new ChatGptAuthService(_authHttp);
            Bridge = new ChatGptPlanBridge(_auth, new Handler(this), null, null);
        }

        public FakeChatGpt Auth { get; }
        public ChatGptPlanBridge Bridge { get; }
        public FakeSecureKeyStore Keys { get; } = new();
        public ConcurrentQueue<Sent> Upstream { get; } = new();
        public Func<int, HttpResponseMessage?>? Respond { get; set; }

        public sealed record Sent(string Url, string? Authorization, string Body, bool LiteHeader);

        public GitHub.Copilot.ProviderConfig Connect(string id, bool seed, bool stored = false)
        {
            var endpoint = new ByokEndpoint
            {
                Id = id, ConnectionProviderId = ProviderCatalog.ChatGptId, Account = new ByokAccount { ClientId = "oaiapp_test" },
                ApiKeyMode = stored ? ByokApiKeyMode.CredentialStore : ByokApiKeyMode.None,
            };
            if (seed)
                Bridge.Seed(id, new ChatGptAccount("oaiapp_test", "user-123", null), new ChatGptTokens
                {
                    AccessToken = "access-seed", RefreshToken = "refresh-seed", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                    Scopes = [ChatGptAuthService.PlanScope]
                });
            return Bridge.CreateProviderConfig(endpoint, Keys);
        }

        public async Task<HttpResponseMessage> PostAsync(GitHub.Copilot.ProviderConfig config, string body,
            string? key = null, string path = "/responses")
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, config.BaseUrl + path)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key ?? config.ApiKey);
            return await _client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            await Bridge.DisposeAsync();
            _client.Dispose();
            _auth.Dispose();
            _authHttp.Dispose();
            Auth.Dispose();
        }

        private sealed class Handler(Harness owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Upstream.Enqueue(new Sent(request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
                    await request.Content!.ReadAsStringAsync(cancellationToken),
                    request.Headers.TryGetValues(ChatGptPlanBridge.ResponsesLiteHeader, out var lite) && lite.Single() == "true"));
                return owner.Respond?.Invoke(Interlocked.Increment(ref owner._attempts))
                    ?? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(Events, Encoding.UTF8, "text/event-stream")
                    };
            }
        }
    }
}
