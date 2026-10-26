#pragma warning disable GHCP001 // Exercises the experimental native provider and its existing runtime APIs.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Capabilities;
using Xunit;

namespace Lumi.Tests;

[Trait("Category", "Integration")]
public sealed class NativeSkillProviderIntegrationTests
{
    [Theory]
    [Trait("Category", "CoreQA")]
    [InlineData(false, "keep")]
    [InlineData(true, "keep")]
    [InlineData(false, "replace")]
    [InlineData(false, "clear")]
    public async Task ResumeSession_WithInstalledPlugin_PreservesSession(
        bool releaseSession, string pluginChange)
    {
        Assert.True(DataStore.UsesIsolatedProfile);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = deadline.Token;
        var root = Path.Combine(TestProfile.Root, $"plugin-resume-{Guid.NewGuid():N}");
        var runtimeHome = Path.Combine(root, "runtime-home");
        var pluginRoot = Path.Combine(root, "plugin");
        var skillRoot = Path.Combine(pluginRoot, "skills", "resume-control");
        var pluginName = $"lumi-resume-control-{Guid.NewGuid():N}";
        var receipt = $"PluginResume{Guid.NewGuid():N}";
        Directory.CreateDirectory(skillRoot);
        await File.WriteAllTextAsync(Path.Combine(pluginRoot, "plugin.json"),
            $$"""{"name":"{{pluginName}}","version":"1.0.0","description":"Isolated session resume control."}""", ct);
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "SKILL.md"),
            "---\nname: resume-control\ndescription: Isolated resume control.\n---\n\nRESUME_CONTROL_BODY", ct);
        await using var client = new CopilotClient(new CopilotClientOptions
        {
            BaseDirectory = runtimeHome,
            WorkingDirectory = root,
            UseLoggedInUser = false,
            Connection = RuntimeConnection.ForStdio(),
            RequestHandler = new PluginResumeInference(receipt),
            LogLevel = CopilotLogLevel.Error,
        });
        await client.StartAsync(ct);
        await client.Rpc.Plugins.InstallAsync(pluginRoot, null, ct);
        var capabilities = await new CopilotSdkCapabilityProvider(() => client, () => runtimeHome)
            .LoadAsync(new CapabilityQuery([root]), ct);
        Assert.True(capabilities.IsAvailable);
        Assert.NotEmpty(capabilities.SessionPluginRoots!);
        var installed = Assert.Single((await client.Rpc.Plugins.ListAsync(ct)).Plugins,
            plugin => plugin.Name == pluginName);
        var provider = new GitHub.Copilot.ProviderConfig
        {
            Type = "openai",
            WireApi = "completions",
            BaseUrl = "https://lumi-plugin-test.invalid/v1",
            ApiKey = "synthetic-test-key",
        };
        var created = SessionConfigBuilder.Build(
            "Isolated plugin resume control.", "gpt-4o-mini", root, null, [], [], [], null,
            null, null, null, provider: provider, pluginDirectories: capabilities.SessionPluginRoots);
        created.EnableFileHooks = false;
        created.RequestExtensions = false;
        created.AvailableTools = [];
        var session = await client.CreateSessionAsync(created, ct);
        var sessionId = session.SessionId;
        try
        {
            Assert.NotNull(await session.SendAndWaitAsync(new MessageOptions
            {
                Prompt = $"Remember this test receipt: {receipt}. Reply only OK. Do not use tools."
            }, TimeSpan.FromSeconds(45), ct));
            var originalEvents = await session.GetEventsAsync(ct);
            Assert.Contains(originalEvents, item => item is SessionStartEvent);
            var workDir = Path.Combine(root, "new-workspace");
            Directory.CreateDirectory(workDir);
            var resumed = SessionConfigBuilder.BuildForResume(
                "Isolated plugin resume control.", "gpt-4o-mini", workDir, null, [], [], [], null,
                null, null, null, provider: provider, pluginDirectories: capabilities.SessionPluginRoots);
            if (pluginChange == "replace")
            {
                var replacementRoot = Path.Combine(root, "replacement");
                var replacementSkillRoot = Path.Combine(replacementRoot, "skills", "replacement-control");
                Directory.CreateDirectory(replacementSkillRoot);
                await File.WriteAllTextAsync(Path.Combine(replacementRoot, "plugin.json"),
                    """{"name":"lumi-resume-replacement","version":"1.0.0"}""", ct);
                await File.WriteAllTextAsync(Path.Combine(replacementSkillRoot, "SKILL.md"),
                    "---\nname: replacement-control\ndescription: Replacement resume control.\n---\n\nREPLACEMENT_BODY", ct);
                resumed.PluginDirectories = [replacementRoot];
            }
            else if (pluginChange == "clear")
                resumed.PluginDirectories = [];
            resumed.EnableFileHooks = false;
            resumed.RequestExtensions = false;
            resumed.AvailableTools = [];
            var requestedPlugins = resumed.PluginDirectories!.ToArray();
            if (releaseSession)
                await TestCopilot.Shared.ReleaseSessionAsync(session);
            else
                Assert.True(TestCopilot.Shared.TryDetachSessionFromSdkRegistry(session));
            // Use the raw SDK so host recovery cannot mask an upstream runtime regression.
            session = await client.ResumeSessionAsync(sessionId, resumed, ct);

            Assert.Equal(sessionId, session.SessionId);
            var resumedEvents = await session.GetEventsAsync(ct);
            Assert.All(originalEvents, original =>
                Assert.Contains(resumedEvents, item => item.Id == original.Id));
            Assert.Equal(requestedPlugins, resumed.PluginDirectories);
            var skills = (await session.Rpc.Skills.ListAsync(ct)).Skills;
            Assert.Equal(pluginChange == "keep",
                skills.Any(skill => skill.Name.Contains("resume-control", StringComparison.Ordinal)));
            Assert.Equal(pluginChange == "replace",
                skills.Any(skill => skill.Name.Contains("replacement-control", StringComparison.Ordinal)));
            var reply = await session.SendAndWaitAsync(new MessageOptions
            {
                Prompt = "What is the test receipt? Reply with only the receipt. Do not use tools."
            }, TimeSpan.FromSeconds(45), ct);
            Assert.NotNull(reply);
            Assert.Contains(receipt, reply.Data.Content, StringComparison.Ordinal);
        }
        finally
        {
            await session.DisposeAsync();
            await client.Rpc.Plugins.UninstallAsync(pluginName, installed.DirectSourceId);
        }
    }

    [SkippableFact]
    public async Task PersonalFolders_AreDiscoveredAndReachCreatedAndResumedSessions()
    {
        Skip.If(Environment.GetEnvironmentVariable("LUMI_INTEGRATION_TESTS") != "1",
            "Set LUMI_INTEGRATION_TESTS=1 to run the isolated native runtime test.");
        var root = Path.Combine(Path.GetTempPath(), $"Lumi-personal-skill-folders-{Guid.NewGuid():N}");
        var personalRoot = Path.Combine(root, "personal-skills");
        var skillFile = Path.Combine(personalRoot, "personal-control", "SKILL.md");
        const string body = "PERSONAL_FOLDER_BODY";
        Directory.CreateDirectory(Path.GetDirectoryName(skillFile)!);
        await File.WriteAllTextAsync(skillFile,
            $"---\nname: personal-control\ndescription: Canonical personal folder control.\n---\n\n{body}");
        var home = Path.Combine(root, "home");
        var runtimeHome = Path.Combine(home, ".copilot");
        var nativeRoot = Path.Combine(runtimeHome, "skills");
        var nativeSkillFile = Path.Combine(nativeRoot, "native-personal-control", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(nativeSkillFile)!);
        await File.WriteAllTextAsync(nativeSkillFile,
            "---\nname: native-personal-control\ndescription: Native personal control.\n---\n\nNATIVE_PERSONAL_BODY");
        var configured = new List<string> { personalRoot, personalRoot + Path.DirectorySeparatorChar, nativeRoot,
            Path.Combine(root, "missing") };
        var environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        environment["HOME"] = home;
        environment["USERPROFILE"] = home;
        environment["COPILOT_HOME"] = runtimeHome;
        environment["XDG_CONFIG_HOME"] = Path.Combine(home, ".config");
        var handler = new SkillInference();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;
        try
        {
            await using var client = new CopilotClient(new CopilotClientOptions
            {
                Mode = CopilotClientMode.Empty,
                BaseDirectory = runtimeHome,
                WorkingDirectory = root,
                Environment = environment,
                UseLoggedInUser = false,
                Connection = RuntimeConnection.ForStdio(),
                RequestHandler = handler,
                LogLevel = CopilotLogLevel.Error,
            });
            await client.StartAsync(ct);
            var provider = new CopilotSdkCapabilityProvider(
                () => client, () => runtimeHome, () => configured);
            var discovered = await provider.LoadAsync(new CapabilityQuery([root]), ct);
            Assert.True(discovered.IsAvailable);
            var skill = Assert.Single(discovered.Capabilities, item => item.Name == "personal-control");
            Assert.Equal("Canonical personal folder control.", skill.Description);
            Assert.Equal(skillFile, skill.SourcePath);
            var sessionRoots = discovered.SessionSkillRoots;
            Assert.NotNull(sessionRoots);
            Assert.Equal(1, sessionRoots.Count(path => path == personalRoot));
            Assert.Equal(1, sessionRoots.Count(path => path == nativeRoot));
            Assert.Contains(discovered.Capabilities, item => item.Name == "native-personal-control");

            void Configure(SessionConfigBase config)
            {
                config.WorkingDirectory = root;
                config.ConfigDirectory = Path.Combine(root, "session-config");
                config.Model = "gpt-4o-mini";
                config.Streaming = true;
                config.Provider = new GitHub.Copilot.ProviderConfig
                {
                    Type = "openai",
                    WireApi = "completions",
                    BaseUrl = "https://lumi-skill-test.invalid/v1",
                    ApiKey = "synthetic-test-key",
                    ModelId = "gpt-4o-mini",
                    WireModel = "gpt-4o-mini",
                };
                config.EnableConfigDiscovery = false;
                config.EnableSessionStore = false;
                config.EnableSessionTelemetry = false;
                config.EnableFileHooks = false;
                config.EnableSkills = true;
                config.IncludedBuiltinSkills = [];
                config.RequestExtensions = false;
                config.SkillDirectories = sessionRoots.ToList();
                config.AvailableTools = ["skill"];
                config.OnPermissionRequest = PermissionHandler.ApproveAll;
            }

            var created = new SessionConfig();
            Configure(created);
            var session = await client.CreateSessionAsync(created, ct);
            var sessionId = session.SessionId;
            try
            {
                await InvokeSkillAsync(session, handler, "personal-control", body, ct);
                var listed = await session.Rpc.Skills.ListAsync(ct);
                Assert.Contains(listed.Skills, item => item.Name == "native-personal-control");

                await session.DisposeAsync();
                var resumed = new ResumeSessionConfig();
                Configure(resumed);
                session = await client.ResumeSessionAsync(sessionId, resumed, ct);
                await InvokeSkillAsync(session, handler, "personal-control", body, ct);
            }
            finally
            {
                await session.DisposeAsync();
            }

            Assert.Contains(body, await File.ReadAllTextAsync(skillFile, ct));
            configured.Clear();
            var cleared = await provider.LoadAsync(new CapabilityQuery([root]), ct);
            Assert.DoesNotContain(cleared.Capabilities, item => item.Name == "personal-control");
            Assert.Contains(cleared.Capabilities, item => item.Name == "native-personal-control");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [SkippableTheory]
    [InlineData(false, "Code Helper")]
    [InlineData(true, "Code Helper")]
    [InlineData(true, "code-helper")]
    public async Task NativeProvider_LoadsLumiAndNativeSkills_ReloadsAndResumes_AndWorksInSubagents(
        bool collidingNativeSkill, string lumiSkillName)
    {
        Skip.If(Environment.GetEnvironmentVariable("LUMI_INTEGRATION_TESTS") != "1",
            "Set LUMI_INTEGRATION_TESTS=1 to run the isolated native runtime test.");
        var root = Path.Combine(Path.GetTempPath(), $"Lumi-native-skill-provider-{Guid.NewGuid():N}");
        var skillRoot = Path.Combine(root, "native-skills");
        Directory.CreateDirectory(Path.Combine(skillRoot, "native-control"));
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "native-control", "SKILL.md"),
            "---\nname: native-control\ndescription: Native file control.\n---\n\nNATIVE_FILE_BODY");
        if (collidingNativeSkill)
        {
            Directory.CreateDirectory(Path.Combine(skillRoot, "code-helper"));
            await File.WriteAllTextAsync(Path.Combine(skillRoot, "code-helper", "SKILL.md"),
                "---\nname: code-helper\ndescription: Native coding skill.\n---\n\nNATIVE_COLLIDING_BODY");
        }
        var skills = new List<Skill>
        {
            new() { Name = lumiSkillName, Description = "An in-memory skill.", Content = "LUMI_FIRST_BODY" },
        };
        var handler = new SkillInference();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
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
                LogLevel = CopilotLogLevel.Error,
            });
            await client.StartAsync(ct);
            using var capabilityCatalog = new CapabilityCatalog(
                new LumiCapabilityProvider(new DataStore(new AppData { Skills = skills })),
                new NativeFixtureProvider(collidingNativeSkill));
            await capabilityCatalog.LoadAsync(CapabilityQuery.Empty, cancellationToken: ct);
            var capabilities = capabilityCatalog.GetSnapshot(CapabilityQuery.Empty);
            var config = new SessionConfig();
            var runtimeSkillName = Configure(config, root, skillRoot, skills, capabilities);
            var session = await client.CreateSessionAsync(config, ct);
            var sessionId = session.SessionId;
            try
            {
                var catalog = await session.Rpc.Skills.ListAsync(ct);
                Assert.Contains(catalog.Skills, skill => skill.Name == runtimeSkillName);
                Assert.Contains(catalog.Skills, skill => skill.Name == "native-control");

                await InvokeSkillAsync(session, handler, runtimeSkillName, "LUMI_FIRST_BODY", ct);
                await InvokeSkillAsync(session, handler, "native-control", "NATIVE_FILE_BODY", ct);
                if (collidingNativeSkill)
                {
                    await InvokeSkillAsync(session, handler, "code-helper", "NATIVE_COLLIDING_BODY", ct);
                    var selected = Assert.IsType<GitHub.Copilot.Rpc.SlashCommandInvocationResultAgentPrompt>(
                        await session.Rpc.Commands.InvokeAsync("code-helper", "", cancellationToken: ct));
                    Assert.Contains("NATIVE_COLLIDING_BODY", selected.Prompt);
                    Assert.DoesNotContain("LUMI_FIRST_BODY", selected.Prompt);
                }

                skills[0].Content = "LUMI_UPDATED_BODY";
                var diagnostics = await session.Rpc.Skills.ReloadAsync(ct);
                Assert.Empty(diagnostics.Errors);
                await InvokeSkillAsync(session, handler, runtimeSkillName, "LUMI_UPDATED_BODY", ct);

                await session.DisposeAsync();
                var resume = new ResumeSessionConfig();
                Assert.Equal(runtimeSkillName, Configure(resume, root, skillRoot, skills, capabilities));
                session = await client.ResumeSessionAsync(sessionId, resume, ct);
                await InvokeSkillAsync(session, handler, runtimeSkillName, "LUMI_UPDATED_BODY", ct);

                handler.Begin(runtimeSkillName, useSubagent: true);
                await session.SendAndWaitAsync(
                    new MessageOptions { Prompt = "Delegate this synthetic skill check to skill-reader." },
                    TimeSpan.FromSeconds(60), ct);
                var childRequests = handler.Requests.Where(request => request.IsSubagent).ToArray();
                var toolResults = handler.Requests.Select(request =>
                {
                    using var document = JsonDocument.Parse(request.Body);
                    var results = document.RootElement.GetProperty("messages").EnumerateArray()
                        .Where(message => message.GetProperty("role").GetString() == "tool")
                        .Select(message => message.GetRawText());
                    return $"Agent {request.AgentId}, parent {request.ParentAgentId}: {string.Join("\n", results)}";
                });
                Assert.True(childRequests.Length > 0, string.Join("\n", toolResults));
                Assert.Contains("LUMI_UPDATED_BODY", childRequests[0].Body);
                Assert.Contains(childRequests, request => request.Body.Contains("LUMI_UPDATED_BODY", StringComparison.Ordinal));
                Assert.DoesNotContain(handler.Requests, request => request.Body.Contains("Unknown session", StringComparison.Ordinal));
            }
            finally
            {
                await session.DisposeAsync();
                await client.DeleteSessionAsync(sessionId, CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Configure(
        SessionConfigBase config,
        string root,
        string skillRoot,
        IReadOnlyList<Skill> skills,
        CapabilitySnapshot capabilities)
    {
        var runtimeSkillName = LumiSkillProvider.GetRuntimeNames(skills, capabilities)[skills[0].Id];
        config.WorkingDirectory = root;
        config.Model = "gpt-4o-mini";
        config.Provider = new GitHub.Copilot.ProviderConfig
        {
            Type = "openai",
            WireApi = "completions",
            BaseUrl = "https://lumi-skill-test.invalid/v1",
            ApiKey = "synthetic-test-key",
            ModelId = "gpt-4o-mini",
            WireModel = "gpt-4o-mini",
        };
        config.Streaming = true;
        config.EnableConfigDiscovery = false;
        config.EnableSessionStore = false;
        config.EnableSessionTelemetry = false;
        config.EnableFileHooks = false;
        config.EnableSkills = true;
        config.IncludedBuiltinSkills = [];
        config.RequestExtensions = false;
        config.SkillDirectories = [skillRoot];
        config.SkillProvider = new LumiSkillProvider(_ => Task.FromResult(skills), capabilities);
        config.AvailableTools = ["skill", "task"];
        config.OnPermissionRequest = PermissionHandler.ApproveAll;
        config.IncludeSubAgentStreamingEvents = true;
        config.CustomAgents =
        [
            new CustomAgentConfig
            {
                Name = "skill-reader",
                Description = "Runs the synthetic native skill check.",
                Prompt = $"Invoke the {runtimeSkillName} skill once, then finish.",
                Tools = ["skill"],
                Skills = [runtimeSkillName],
            },
        ];
        return runtimeSkillName;
    }

    private sealed class NativeFixtureProvider(bool collidingNativeSkill) : ICapabilityProvider
    {
        public string Id => "native-fixture";

        public Task<CapabilityProviderResult> LoadAsync(CapabilityQuery query, CancellationToken cancellationToken)
            => Task.FromResult(collidingNativeSkill
                ? new CapabilityProviderResult(
                [
                    new CapabilityDescriptor
                    {
                        Kind = CapabilityKind.Skill, Name = "code-helper",
                        SkillInvocationName = "code-helper", Origin = CapabilityOrigin.Project,
                    },
                ])
                : CapabilityProviderResult.Empty);
    }

    private static async Task InvokeSkillAsync(
        CopilotSession session,
        SkillInference handler,
        string name,
        string expectedBody,
        CancellationToken ct)
    {
        handler.Begin(name);
        var bodies = new ConcurrentQueue<string>();
        var completions = new ConcurrentQueue<bool>();
        using var skillSubscription = session.On<SkillInvokedEvent>(ev => bodies.Enqueue(ev.Data.Content ?? ""));
        using var toolSubscription = session.On<ToolExecutionCompleteEvent>(ev =>
        {
            if (ev.Data.ToolCallId.StartsWith(handler.CallPrefix, StringComparison.Ordinal))
                completions.Enqueue(ev.Data.Success == true);
        });
        await session.SendAndWaitAsync(
            new MessageOptions { Prompt = "Run the synthetic skill check." },
            TimeSpan.FromSeconds(30), ct);
        var requests = handler.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Contains(expectedBody, requests[1].Body);
        Assert.True(Assert.Single(completions), requests[1].Body);
        Assert.Contains(bodies, body => body.Contains(expectedBody, StringComparison.Ordinal));
        using var request = JsonDocument.Parse(requests[0].Body);
        var tools = request.RootElement.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("function").GetProperty("name").GetString()).ToArray();
        Assert.Single(tools, tool => tool == "skill");
        Assert.DoesNotContain("fetch_skill", tools);
    }

    private sealed class PluginResumeInference(string receipt) : CopilotRequestHandler
    {
        protected override async Task<HttpResponseMessage> SendRequestAsync(
            HttpRequestMessage request, CopilotRequestContext context)
        {
            Assert.EndsWith("/chat/completions", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            var body = await request.Content!.ReadAsStringAsync(context.CancellationToken);
            var chunk = JsonSerializer.Serialize(new
            {
                id = "plugin-resume-response",
                @object = "chat.completion.chunk",
                created = 1,
                model = "gpt-4o-mini",
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new
                        {
                            role = "assistant",
                            content = body.Contains(receipt, StringComparison.Ordinal) ? receipt : "MISSING_HISTORY",
                        },
                        finish_reason = "stop",
                    },
                },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"data: {chunk}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private sealed class SkillInference : CopilotRequestHandler
    {
        private readonly ConcurrentDictionary<string, int> _requestCounts = new();
        private string _skillName = "";
        private bool _useSubagent;
        public string CallPrefix { get; private set; } = "";
        public ConcurrentQueue<InferenceRequest> Requests { get; } = new();

        public void Begin(string skillName, bool useSubagent = false)
        {
            _skillName = skillName;
            _useSubagent = useSubagent;
            CallPrefix = $"skill-test-{Guid.NewGuid():N}-";
            _requestCounts.Clear();
            Requests.Clear();
        }

        protected override async Task<HttpResponseMessage> SendRequestAsync(
            HttpRequestMessage request,
            CopilotRequestContext context)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) != true)
                throw new InvalidOperationException($"Unexpected inference endpoint: {request.RequestUri}");
            var body = await request.Content!.ReadAsStringAsync(context.CancellationToken);
            var isSubagent = !string.IsNullOrEmpty(context.ParentAgentId);
            Requests.Enqueue(new InferenceRequest(isSubagent, context.AgentId, context.ParentAgentId, body));
            var key = context.AgentId ?? context.SessionId ?? "root";
            var number = _requestCounts.AddOrUpdate(key, 1, (_, previous) => previous + 1);
            object delta;
            if (number == 1)
            {
                var delegateTask = _useSubagent && !isSubagent;
                var arguments = delegateTask
                    ? JsonSerializer.Serialize(new
                    {
                        agent_type = "skill-reader",
                        description = "Check native skills",
                        prompt = $"Invoke {_skillName} and finish.",
                        mode = "sync",
                        name = "Skill provider child",
                    })
                    : JsonSerializer.Serialize(new { skill = _skillName });
                delta = new
                {
                    role = "assistant",
                    tool_calls = new[]
                    {
                        new
                        {
                            index = 0,
                            id = $"{CallPrefix}{key}",
                            type = "function",
                            function = new { name = delegateTask ? "task" : "skill", arguments },
                        },
                    },
                };
            }
            else
            {
                Assert.Equal(2, number);
                delta = new { role = "assistant", content = "NATIVE_SKILL_CHECK_COMPLETE" };
            }

            var chunk = JsonSerializer.Serialize(new
            {
                id = $"skill-test-response-{key}",
                @object = "chat.completion.chunk",
                created = 1,
                model = "gpt-4o-mini",
                choices = new[]
                {
                    new { index = 0, delta, finish_reason = number == 1 ? "tool_calls" : "stop" },
                },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"data: {chunk}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private sealed record InferenceRequest(bool IsSubagent, string? AgentId, string? ParentAgentId, string Body);
}
