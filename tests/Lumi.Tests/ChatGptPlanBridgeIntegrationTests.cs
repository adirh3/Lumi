using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Byok;
using Microsoft.Extensions.AI;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Runs a real Copilot runtime session against the ChatGPT plan bridge, with ChatGPT's Responses
/// endpoint replaced by a scripted in-process reply: the production path minus OpenAI's servers.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ChatGptPlanBridgeIntegrationTests
{
    [Theory]
    [InlineData("gpt-5.5", false)]
    [InlineData("gpt-6.1-sol", true)]
    public async Task CopilotSession_CompletesAToolCallThroughTheBridge(string model, bool lite)
    {
        using var auth = new FakeChatGpt
        {
            ModelsJson = """
                {"models":[{"slug":"gpt-6.1-sol","visibility":"list","use_responses_lite":true},
                           {"slug":"gpt-5.5","visibility":"list","use_responses_lite":false}]}
                """
        };
        using var authHttp = new HttpClient(auth);
        using var authService = new ChatGptAuthService(authHttp);
        var upstream = new ScriptedResponses();
        await using var bridge = new ChatGptPlanBridge(authService, upstream, null, null);
        bridge.Seed("plan", new ChatGptAccount("oaiapp_test", "user-123", null), new ChatGptTokens
        {
            AccessToken = "access-seed", RefreshToken = "refresh-seed", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Scopes = [ChatGptAuthService.PlanScope]
        });
        var provider = bridge.CreateProviderConfig(new ByokEndpoint
        {
            Id = "plan", ConnectionProviderId = ProviderCatalog.ChatGptId, Account = new ByokAccount { ClientId = "oaiapp_test" },
            ApiKeyMode = ByokApiKeyMode.None,
        });

        await TestCopilot.Shared.ConnectAsync();
        var called = false;
        var tool = AIFunctionFactory.Create(() =>
        {
            called = true;
            return "LUMI_CONNECTION_OK";
        }, "lumi_connection_check", "Read-only connection check. Returns the verification word.");
        var reply = await TestCopilot.Shared.UseLightweightSessionAsync(
            new LightweightSessionOptions
            {
                Model = model, Provider = provider, Streaming = true, Tools = [tool],
                SystemPrompt = "Call lumi_connection_check once, then respond with its exact returned word."
            },
            (session, ct) => session.SendAndWaitAsync(
                new MessageOptions { Prompt = "Verify the connection." }, TimeSpan.FromSeconds(90), ct),
            CancellationToken.None);

        Assert.True(called);
        Assert.Contains("LUMI_CONNECTION_OK", reply?.Data.Content);
        var requests = upstream.Requests.ToList();
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request =>
        {
            Assert.Equal("Bearer access-seed", request.Authorization);
            Assert.False(request.Body["store"]!.GetValue<bool>());
            Assert.True(request.Body["stream"]!.GetValue<bool>());
            Assert.Null(request.Body["max_output_tokens"]);
            Assert.Null(request.Body["previous_response_id"]);
            Assert.DoesNotContain(request.Body["input"]!.AsArray(), item => item?["role"]?.GetValue<string>() == "system");
            Assert.DoesNotContain("prompt_cache_breakpoint", request.Body.ToJsonString());
            Assert.Equal(lite, request.Body["tools"] is null);
            Assert.Equal(lite, request.Body["input"]![0]!["type"]?.GetValue<string>() == "additional_tools");
        });
        var call = Assert.Single(requests[1].Body["input"]!.AsArray(), item => item?["type"]?.GetValue<string>() == "function_call")!;
        Assert.Equal(lite ? "functions" : null, call["namespace"]?.GetValue<string>());
        Assert.Contains(requests[1].Body["input"]!.AsArray(),
            item => item?["type"]?.GetValue<string>() == "function_call_output");
    }

    [Fact]
    public async Task CopilotSession_ResumesThroughTheBridgeOfALaterLaunch()
    {
        using var auth = new FakeChatGpt();
        using var authHttp = new HttpClient(auth);
        using var authService = new ChatGptAuthService(authHttp);
        var endpoint = new ByokEndpoint
        {
            Id = "plan-resume", ConnectionProviderId = ProviderCatalog.ChatGptId,
            Account = new ByokAccount { ClientId = "oaiapp_test" }, ApiKeyMode = ByokApiKeyMode.None,
        };
        ProviderConfig Connect(ChatGptPlanBridge bridge)
        {
            bridge.Seed(endpoint.Id, new ChatGptAccount("oaiapp_test", "user-123", null), new ChatGptTokens
            {
                AccessToken = "access-seed", RefreshToken = "refresh-seed", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                Scopes = [ChatGptAuthService.PlanScope]
            });
            return bridge.CreateProviderConfig(endpoint);
        }
        var tool = AIFunctionFactory.Create(() => "LUMI_CONNECTION_OK", "lumi_connection_check",
            "Read-only connection check. Returns the verification word.");
        const string instructions = "Call lumi_connection_check once, then respond with its exact returned word.";
        var workingDirectory = Path.Combine(TestProfile.Root, "chatgpt-resume");
        Directory.CreateDirectory(workingDirectory);
        await TestCopilot.Shared.ConnectAsync();

        var firstLaunch = new ScriptedResponses();
        ProviderConfig before;
        string sessionId;
        await using (var bridge = new ChatGptPlanBridge(authService, firstLaunch, null, null))
        {
            before = Connect(bridge);
            var session = await TestCopilot.Shared.CreateSessionAsync(SessionConfigBuilder.Build(
                instructions, "gpt-5.5", workingDirectory, null, null, null, [tool], null, null, null, null,
                provider: before, enableCapabilityDiscovery: false));
            sessionId = session.SessionId;
            try
            {
                var reply = await session.SendAndWaitAsync(
                    new MessageOptions { Prompt = "Verify the connection." }, TimeSpan.FromSeconds(90));
                Assert.Contains("LUMI_CONNECTION_OK", reply?.Data.Content);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        // The next launch serves ChatGPT from a new port with a new key.
        var nextLaunch = new ScriptedResponses();
        await using var restarted = new ChatGptPlanBridge(authService, nextLaunch, null, null);
        var after = Connect(restarted);
        Assert.NotEqual(before.BaseUrl, after.BaseUrl);
        var resumed = await TestCopilot.Shared.ResumeSessionAsync(sessionId, SessionConfigBuilder.BuildForResume(
            instructions, "gpt-5.5", workingDirectory, null, null, null, [tool], null, null, null, null,
            provider: after, enableCapabilityDiscovery: false));
        try
        {
            var reply = await resumed.SendAndWaitAsync(
                new MessageOptions { Prompt = "Verify it again." }, TimeSpan.FromSeconds(90));
            Assert.Contains("LUMI_CONNECTION_OK", reply?.Data.Content);
        }
        finally
        {
            await TestCopilot.Shared.DisposeAndDeleteSessionAsync(resumed);
        }

        // The resumed turn reached the new bridge with the session's own history, tool call included.
        var request = Assert.Single(nextLaunch.Requests);
        Assert.Contains(request.Body["input"]!.AsArray(), item => item?["type"]?.GetValue<string>() == "function_call_output");
        // So a chat keeps its session across launches instead of replaying its transcript into a new one.
        Assert.Equal(ByokConfigHelper.BuildProviderSignature(before), ByokConfigHelper.BuildProviderSignature(after));
    }

    /// <summary>Answers like the Responses API: a tool call first, then the tool's word as text.</summary>
    private sealed class ScriptedResponses : HttpMessageHandler
    {
        private int _count;

        public ConcurrentQueue<(string? Authorization, JsonObject Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            Requests.Enqueue((request.Headers.Authorization?.ToString(), body));
            var id = "resp_" + Interlocked.Increment(ref _count);
            var answered = body["input"]!.AsArray().Any(item => item?["type"]?.GetValue<string>() == "function_call_output");
            var item = answered
                ? new JsonObject
                {
                    ["type"] = "message", ["id"] = "msg_" + id, ["role"] = "assistant", ["status"] = "completed",
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "output_text", ["text"] = "LUMI_CONNECTION_OK", ["annotations"] = new JsonArray()
                    })
                }
                : new JsonObject
                {
                    ["type"] = "function_call", ["id"] = "fc_" + id, ["call_id"] = "call_" + id,
                    ["name"] = "lumi_connection_check", ["arguments"] = "{}", ["status"] = "completed"
                };
            var events = new StringBuilder();
            void Add(string type, JsonObject data)
            {
                data["type"] = type;
                events.Append("event: ").Append(type).Append("\ndata: ").Append(data.ToJsonString()).Append("\n\n");
            }
            Add("response.created", new JsonObject
            {
                ["response"] = new JsonObject { ["id"] = id, ["object"] = "response", ["status"] = "in_progress", ["output"] = new JsonArray() }
            });
            Add("response.output_item.added", new JsonObject { ["output_index"] = 0, ["item"] = item.DeepClone() });
            if (answered)
                Add("response.output_text.delta", new JsonObject
                {
                    ["output_index"] = 0, ["item_id"] = "msg_" + id, ["content_index"] = 0, ["delta"] = "LUMI_CONNECTION_OK"
                });
            Add("response.output_item.done", new JsonObject { ["output_index"] = 0, ["item"] = item.DeepClone() });
            // As the plan route does: the final response leaves its output empty.
            Add("response.completed", new JsonObject
            {
                ["response"] = new JsonObject
                {
                    ["id"] = id, ["object"] = "response", ["status"] = "completed", ["output"] = new JsonArray(),
                    ["usage"] = new JsonObject { ["input_tokens"] = 20, ["output_tokens"] = 5, ["total_tokens"] = 25 }
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(events.ToString(), Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
