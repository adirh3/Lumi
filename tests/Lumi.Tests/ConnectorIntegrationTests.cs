using System.Collections;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Copilot;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Capabilities;
using Lumi.ViewModels;
using Xunit;
using FakeMcp = Lumi.Tests.LazyMcpRuntimeTests.FakeMcp;

namespace Lumi.Tests;

[Collection("Headless UI")]
[Trait("Category", "ConnectorIntegration")]
public sealed class ConnectorIntegrationTests
{
    private const string Model = "lumi-connectors-test";
    private const string ConfiguredServer = "github-mcp-server";
    private const string DiscoveredServer = "connector-discovery-test";

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ConfiguredStdioConnectorWorksOnFirstSendAndAfterIdleResume(bool useProxy)
        => WithChatAsync(Source.Configured, useProxy, async (vm, chat, server, ct) =>
        {
            Assert.Contains(ConfiguredServer, vm.ActiveMcpServerNames);
            await SendAndCheckAsync(vm, chat, server, "first", invokeConnector: true, ct);
            var sessionId = EvictIdleSession(vm, chat);

            await SendAndCheckAsync(vm, chat, server, "resumed", invokeConnector: true, ct);
            Assert.Equal(sessionId, chat.CopilotSessionId);
        });

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DiscoveredHttpConnectorWorksOnFirstSendAndResumeAndRespectsOptOut(bool withProject)
        => WithChatAsync(withProject ? Source.Workspace : Source.Plugin, useProxy: false, async (vm, chat, server, ct) =>
        {
            Assert.DoesNotContain(DiscoveredServer, vm.ActiveMcpServerNames);

            await SendAndCheckAsync(vm, chat, server, "first", invokeConnector: true, ct);
            Assert.Contains(DiscoveredServer, vm.ActiveMcpServerNames);
            Assert.Contains(DiscoveredServer, Assert.Single(chat.Messages, message => message.Role == "user").ActiveMcpServerNames);
            var sessionId = EvictIdleSession(vm, chat);

            await SendAndCheckAsync(vm, chat, server, "resumed", invokeConnector: true, ct);
            Assert.Equal(sessionId, chat.CopilotSessionId);

            EvictIdleSession(vm, chat);
            vm.RemoveMcpByName(DiscoveredServer);
            await SendAndCheckAsync(vm, chat, server, "excluded", invokeConnector: false, ct);
            Assert.Equal(sessionId, chat.CopilotSessionId);
            Assert.DoesNotContain(DiscoveredServer, vm.ActiveMcpServerNames);
        });

    private static async Task WithChatAsync(
        Source source, bool useProxy, Func<ChatViewModel, Chat, FakeMcp, CancellationToken, Task> exercise)
    {
        Assert.True(DataStore.UsesIsolatedProfile);
        var root = Path.Combine(TestProfile.Root, $"connectors-{Guid.NewGuid():N}");
        var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = deadline.Token;
        using var server = new FakeMcp();
        await using var remote = new McpProxyRuntime(Path.Combine(root, "remote-cache"));
        var definition = server.Definition(lazy: false);
        var project = source == Source.Workspace ? new Project { Name = "Connector test", WorkingDirectory = workspace } : null;

        // The planner normally injects a GitHub connector. Replace it with a local fixture so this
        // test cannot depend on GitHub reaching the runner or the runner having a GitHub account.
        var configured = new McpServer { Name = ConfiguredServer, IsEnabled = true, Timeout = 10_000 };
        var runtimeHome = Path.Combine(root, "copilot-home");
        var runtime = RuntimeConnection.ForSpawnedStdio(TestCopilot.BundledRuntimePath);
        runtime.BaseDirectory = runtimeHome;
        runtime.WorkingDirectory = workspace;
        runtime.SessionIdleTimeoutSeconds = 0;
        var env = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GITHUB_PERSONAL_ACCESS_TOKEN", "GITHUB_COPILOT_GITHUB_TOKEN" })
            env.Remove(key);
        env["COPILOT_HOME"] = runtimeHome;
        env["COPILOT_CACHE_HOME"] = Path.Combine(root, "copilot-cache");
        env["COPILOT_PKG_CACHE_HOME"] = Path.Combine(TestProfile.Root, "copilot-packages");
        env["HOME"] = root;
        env["USERPROFILE"] = root;
        env["XDG_CONFIG_HOME"] = Path.Combine(root, ".config");
        runtime.Environment = env;
        await using var client = new CopilotClient(new CopilotClientOptions
        {
            Mode = CopilotClientMode.CodingAgent,
            Runtime = runtime,
            GitHub = new ClientGitHubOptions { UseLoggedInUser = false },
            Models = new ClientModelOptions { RequestHandler = new ConnectorInference() },
            Observability = new ClientObservabilityOptions { LogLevel = CopilotLogLevel.Error },
        });
        await client.StartAsync(ct);

        if (source == Source.Configured)
        {
            configured.Command = definition.Config.Command;
            configured.Args = definition.Config.Args!.ToList();
            configured.Env = new Dictionary<string, string>(definition.Config.Env!);
        }
        else
        {
            var url = remote.Register(definition).Url;
            configured.ServerType = "remote";
            configured.Url = url;
            configured.Tools = ["other"];
            var config = JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    [DiscoveredServer] = new { type = "http", url, tools = new[] { "*" } }
                }
            });
            var configDirectory = source == Source.Workspace
                ? workspace
                : Directory.CreateDirectory(Path.Combine(root, "plugin")).FullName;
            await File.WriteAllTextAsync(Path.Combine(configDirectory, ".mcp.json"), config, ct);
            if (source == Source.Plugin)
            {
                await File.WriteAllTextAsync(Path.Combine(configDirectory, "plugin.json"),
                    """{"name":"connector-test","version":"1.0.0","description":"Isolated connector fixture.","mcpServers":"./.mcp.json"}""", ct);
                await client.Rpc.Plugins.InstallAsync(configDirectory, null, ct);
            }
        }

        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            // A standalone chat normally runs in the user's home. Use a throwaway checkout instead
            // so discovery can't load the developer's own workspace configuration.
            var chat = new Chat { ProjectId = project?.Id, WorktreePath = project is null ? workspace : null };
            var store = new DataStore(new AppData
            {
                Chats = [chat],
                Projects = project is null ? [] : [project],
                McpServers = [configured],
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    AutoGenerateTitles = false,
                    SuggestFollowUps = false,
                    EnableMemoryAutoSave = false,
                    UseMcpProxy = useProxy,
                    UseLazyMcpInitialization = useProxy,
                    UseBYOKOnly = true,
                    PreferredModel = "byok:fixture",
                    ByokEndpoints =
                    [
                        new ByokEndpoint
                        {
                            Id = "fixture", Name = "Connector test", ProviderType = "openai", WireApi = "completions",
                            BaseUrl = "https://lumi-connectors-test.invalid/v1", ApiKeyMode = ByokApiKeyMode.None,
                        }
                    ],
                    ByokModels =
                    [
                        new ByokModel { Id = "fixture", EndpointId = "fixture", ModelId = Model, DisplayName = Model }
                    ],
                }
            });
            await using var connection = new TestCopilot.SimulatedConnection(client);
            using var catalog = new CapabilityCatalog(new LumiCapabilityProvider(store),
                new CopilotSdkCapabilityProvider(() => client, () => runtimeHome),
                new WorkspaceMcpCapabilityProvider());
            using var vm = new ChatViewModel(store, TestCopilot.Shared, capabilityCatalog: catalog)
            {
                WorktreePath = chat.WorktreePath,
                IsWorktreeMode = chat.WorktreePath is not null,
                CurrentChat = chat,
            };
            await exercise(vm, chat, server, ct);
        }, ct);
    }

    private static async Task SendAndCheckAsync(
        ChatViewModel vm, Chat chat, FakeMcp server, string turn, bool invokeConnector, CancellationToken ct)
    {
        var marker = $"{turn}_{Guid.NewGuid():N}";
        var previousMessages = chat.Messages.Count;
        var previousCalls = server.Messages("tools/call").Length;
        var terminal = new TaskCompletionSource<ChatLifecycleEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEvent(ChatLifecycleEvent evt)
        {
            if (evt.ChatId == chat.Id && evt.EventType is
                ChatLifecycleEventTypes.Idle or ChatLifecycleEventTypes.Error or ChatLifecycleEventTypes.Aborted)
                terminal.TrySetResult(evt);
        }

        vm.ChatEvents.EventPublished += OnEvent;
        try
        {
            vm.PromptText = $"Check the connector: CONNECTOR_TEST {marker}";
            await vm.SendMessageCommand.ExecuteAsync(null).WaitAsync(ct);
            Assert.True(chat.Messages.Skip(previousMessages).Any(message => message.Role == "user"),
                $"Lumi did not send the connector test turn: {vm.StatusText}");
            var evt = await terminal.Task.WaitAsync(ct);
            var messages = chat.Messages.Skip(previousMessages).ToArray();
            var reply = string.Join("\n", messages.Where(message => message.Role == "assistant")
                .Select(message => message.Content)).Trim();
            Assert.True(evt.EventType == ChatLifecycleEventTypes.Idle,
                $"Connector turn ended with {evt.EventType}: {evt.Detail}\n{reply}");
            var calls = server.Messages("tools/call").Skip(previousCalls).ToArray();

            if (invokeConnector)
            {
                Assert.Contains($"CONNECTOR_RESULT tool_{marker}", reply, StringComparison.Ordinal);
                var call = Assert.Single(calls).GetProperty("params");
                Assert.Equal("echo", call.GetProperty("name").GetString());
                Assert.Equal($"tool_{marker}", call.GetProperty("arguments").GetProperty("value").GetString());
            }
            else
            {
                Assert.Equal("CONNECTOR_NOT_OFFERED", reply);
                Assert.Empty(calls);
            }
        }
        finally
        {
            vm.ChatEvents.EventPublished -= OnEvent;
        }
    }

    private static string EvictIdleSession(ChatViewModel vm, Chat chat)
    {
        var sessionId = Assert.IsType<string>(chat.CopilotSessionId);
        Assert.True(vm.HasLiveCopilotRuntimeForCurrentChat());
        vm.ReleaseIdleSessionRuntimes(DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromMinutes(1));
        Assert.False(vm.HasLiveCopilotRuntimeForCurrentChat());
        Assert.Equal(sessionId, chat.CopilotSessionId);
        return sessionId;
    }

    private enum Source { Configured, Workspace, Plugin }

    // Only inference is scripted. It can call the connector only if Copilot actually advertises
    // it, and can report success only from the tool result returned at the next inference request.
    private sealed class ConnectorInference : CopilotRequestHandler
    {
        protected override async Task<HttpResponseMessage> SendRequestAsync(
            HttpRequestMessage request, CopilotRequestContext context)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(context.CancellationToken));
            var root = body.RootElement;
            var messages = root.GetProperty("messages").EnumerateArray().ToArray();
            var lastUser = Array.FindLastIndex(messages, message => message.GetProperty("role").GetString() == "user"
                && message.GetProperty("content").GetRawText().Contains("CONNECTOR_TEST ", StringComparison.Ordinal));
            var prompt = messages[lastUser].GetProperty("content").GetRawText();
            var marker = System.Text.RegularExpressions.Regex.Match(prompt, "CONNECTOR_TEST ([a-z0-9_]+)").Groups[1].Value;
            var functions = root.GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("function")).ToArray();
            var echo = functions.SingleOrDefault(function =>
                function.GetProperty("description").GetString() == "Echo complete arguments");
            var offered = echo.ValueKind != JsonValueKind.Undefined;
            var result = messages.Skip(lastUser + 1).LastOrDefault(message => message.GetProperty("role").GetString() == "tool");
            var value = $"tool_{marker}";
            var excluded = marker.StartsWith("excluded_", StringComparison.Ordinal);
            var call = !excluded && offered && result.ValueKind == JsonValueKind.Undefined;
            object delta;
            if (call)
            {
                delta = new
                {
                    role = "assistant",
                    tool_calls = new[]
                    {
                        new
                        {
                            index = 0, id = marker, type = "function",
                            function = new { name = echo.GetProperty("name").GetString(), arguments = JsonSerializer.Serialize(new { value }) }
                        }
                    }
                };
            }
            else
            {
                var content = !offered ? "CONNECTOR_NOT_OFFERED"
                    : excluded ? "CONNECTOR_UNEXPECTEDLY_OFFERED"
                    : result.GetProperty("content").GetRawText().Contains(value, StringComparison.Ordinal)
                        ? $"CONNECTOR_RESULT {value}"
                        : $"CONNECTOR_BAD_RESULT {result.GetProperty("content").GetRawText()}";
                delta = new { role = "assistant", content };
            }

            var chunk = JsonSerializer.Serialize(new
            {
                id = "connector-" + marker, @object = "chat.completion.chunk", created = 1, model = Model,
                choices = new[] { new { index = 0, delta, finish_reason = call ? "tool_calls" : "stop" } }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"data: {chunk}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
