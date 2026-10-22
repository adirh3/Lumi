using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.EmbeddedApps;
using Lumi.ViewModels;
using Xunit;

// Pins Lumi's use of the SDK's experimental MCP Apps, canvas and extension launch contracts.
#pragma warning disable GHCP001

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class EmbeddedAppTests
{
    [Fact]
    public void EnablesTheDocumentedRuntimeGateWithoutOverridingAnExplicitOptOut()
    {
        var environment = new Dictionary<string, string> { ["OTHER"] = "preserved" };
        CopilotService.ApplyMcpAppsFeature(environment);
        Assert.Equal("true", environment["COPILOT_MCP_APPS"]);
        Assert.Equal("preserved", environment["OTHER"]);
        environment["COPILOT_MCP_APPS"] = "false";
        CopilotService.ApplyMcpAppsFeature(environment);
        Assert.Equal("false", environment["COPILOT_MCP_APPS"]);
    }

    [SkippableFact]
    public async Task JavaScriptExtensionsUseTheRuntimeSdkAndLumisCopilotFolder()
    {
        Skip.If(CopilotExtensionLaunchProvider.NodePath is null, "Node.js is not installed.");
        var entry = Path.Combine(TestProfile.Root, "extension.mjs");
        var result = await new CopilotExtensionLaunchProvider().ResolveAsync(new ExtensionLaunchProviderResolveRequest
        {
            ModulePath = entry, Name = "test-extension", Source = ExtensionSource.Project
        });
        Assert.NotNull(result.Launch);
        Assert.Equal("--import", result.Launch.Args[0]);
        Assert.StartsWith("data:text/javascript;base64,", result.Launch.Args[1]);
        Assert.Equal(entry, result.Launch.Args[2]);
        var bootstrap = Encoding.UTF8.GetString(Convert.FromBase64String(result.Launch.Args[1].Split(',')[1]));
        Assert.Contains("COPILOT_SDK_PATH", bootstrap);
        Assert.False(Directory.Exists(Path.Combine(TestProfile.Root, "node_modules")));
        // Extensions that keep state beside the session look for it under Copilot's home.
        Assert.Equal(DataStore.CopilotConfigDir, result.Launch.Env["COPILOT_HOME"]);
    }

    [Fact]
    public void CapturesTheTypedAppResourceAndStructuredResultWithoutReplayingTheTool()
    {
        Loc.Load("en");
        var message = new ChatMessage { Content = """{"zone":"local"}""", Author = "Get time" };
        var completed = new ToolExecutionCompleteData
        {
            ToolCallId = "time-1", Success = true,
            Result = new ToolExecutionCompleteResult
            {
                Content = "12:00",
                StructuredContent = Json("""{"time":"12:00"}"""),
                UiResource = new ToolExecutionCompleteUIResource
                {
                    Uri = "ui://clock/app.html", MimeType = "text/html;profile=mcp-app",
                    Text = "<!doctype html><html>Clock</html>"
                }
            }
        };

        var app = EmbeddedAppData.CaptureMcpApp("Clock_Server", "Clock server", message, completed)!;

        Assert.Equal(EmbeddedAppKind.McpApp, app.Kind);
        Assert.Equal("Get time", app.Title);
        Assert.Equal("Clock_Server", app.ServerName);
        Assert.Equal("Clock server", app.SourceName);
        Assert.Equal("ui://clock/app.html", app.ResourceUri);
        Assert.Equal(message.Content, app.InputJson);
        Assert.Contains("Clock", app.Html);
        var result = JsonNode.Parse(app.ResultJson!)!;
        Assert.Equal("12:00", result["structuredContent"]!["time"]!.GetValue<string>());
        Assert.Equal("12:00", result["content"]![0]!["text"]!.GetValue<string>());
        Assert.False(result["isError"]!.GetValue<bool>());
        Assert.Null(result["uiResource"]);
    }

    [Fact]
    public void AnOrdinaryToolIsNotTurnedIntoAnApp()
    {
        var completed = new ToolExecutionCompleteData
        {
            ToolCallId = "plain", Success = true, Result = new ToolExecutionCompleteResult { Content = "Hello" }
        };
        Assert.Null(EmbeddedAppData.CaptureMcpApp("server", null, new ChatMessage(), completed));
    }

    [Fact]
    public void AToolWithNoArgumentsStillProvidesAValidAppInputObject()
    {
        var completed = new ToolExecutionCompleteData
        {
            ToolCallId = "empty-input", Success = true,
            Result = new ToolExecutionCompleteResult
            {
                Content = "done",
                UiResource = new ToolExecutionCompleteUIResource
                {
                    Uri = "ui://clock", MimeType = "text/html;profile=mcp-app", Text = "<html>Clock</html>"
                }
            }
        };
        var app = EmbeddedAppData.CaptureMcpApp("clock", null, new ChatMessage(), completed)!;
        Assert.Equal("{}", app.InputJson);
        Assert.Equal("clock", app.SourceName);
        // A tool without structured content leaves it out; an app's MCP client rejects a null there.
        Assert.False(JsonNode.Parse(app.ResultJson!)!.AsObject().ContainsKey("structuredContent"));
    }

    [Fact]
    public void AResourceReadReturnsOnlyTheFieldsTheResourceHas()
    {
        var result = EmbeddedAppData.ToResourceResult(new McpAppsReadResourceResult
        {
            Contents =
            [
                new McpAppsResourceContent { Uri = "ui://clock/data", Text = "12:00" },
                new McpAppsResourceContent
                {
                    Uri = "ui://clock/app", MimeType = "text/html;profile=mcp-app", Blob = "PGh0bWw+",
                    Meta = new Dictionary<string, JsonElement> { ["ui"] = Json("""{"csp":{}}"""), ["unused"] = Json("null") }
                }
            ]
        });

        var plain = result["contents"]![0]!.AsObject();
        Assert.Equal(["uri", "text"], plain.Select(static property => property.Key));
        var app = result["contents"]![1]!.AsObject();
        Assert.Equal(["uri", "mimeType", "blob", "_meta"], app.Select(static property => property.Key));
        Assert.Equal(["ui"], app["_meta"]!.AsObject().Select(static property => property.Key));
    }

    [Fact]
    public void ASavedAppKeepsWhatIdentifiesItButNotItsInterface()
    {
        var message = new ChatMessage
        {
            EmbeddedApp = new EmbeddedApp
            {
                Kind = EmbeddedAppKind.Canvas, SessionId = "session", CanvasId = "draw", InstanceId = "drawing-1",
                ExtensionId = "project:draw", SourceName = "Draw", Title = "Drawing",
                InputJson = """{"drawing":"Flow"}""", ModelContextJson = """{"content":[]}""",
                Html = "<html>large</html>", MetadataJson = """{"ui":{"csp":{}}}""", Url = "http://127.0.0.1:12345/?t=secret"
            }
        };
        var copy = message.Clone();
        Assert.NotSame(message.EmbeddedApp, copy.EmbeddedApp);
        copy.EmbeddedApp!.Title = "Changed";
        Assert.Equal("Drawing", message.EmbeddedApp.Title);

        var json = JsonSerializer.Serialize(message, AppDataJsonContext.Default.ChatMessage);
        Assert.DoesNotContain("large", json);
        Assert.DoesNotContain("secret", json);
        var restored = JsonSerializer.Deserialize(json, AppDataJsonContext.Default.ChatMessage)!.EmbeddedApp!;
        Assert.Equal(EmbeddedAppKind.Canvas, restored.Kind);
        Assert.Equal("drawing-1", restored.InstanceId);
        Assert.Equal(message.EmbeddedApp.InputJson, restored.InputJson);
        Assert.Equal(message.EmbeddedApp.ModelContextJson, restored.ModelContextJson);
        Assert.Null(restored.Html);
        Assert.Null(restored.MetadataJson);
        Assert.Null(restored.Url);
        Assert.Null(JsonNode.Parse(JsonSerializer.Serialize(new ChatMessage(), AppDataJsonContext.Default.ChatMessage))!["embeddedApp"]);
    }

    [Fact]
    public void ACanvasLivesOnTheToolCallThatOpenedItAndReopensInItsSession()
    {
        var opener = Tool("open_canvas", """{"extensionId":"project:draw","canvasId":"draw","instanceId":"flow","input":{"drawing":"Flow"}}""");
        var action = Tool("invoke_canvas_action", """{"instanceId":"flow","actionName":"set_diagram","input":{}}""");
        var chat = new Chat { CopilotSessionId = "session-2", Messages = [opener, action] };

        Assert.Same(opener, EmbeddedAppData.RecordOpenCanvas(chat, chat.Messages, "session-1", Canvas("flow", "http://127.0.0.1:1/a")));
        var app = opener.EmbeddedApp!;
        Assert.Equal(EmbeddedAppKind.Canvas, app.Kind);
        Assert.Equal("draw", app.SourceName);
        Assert.Equal("Flow chart", app.Title);
        Assert.Null(action.EmbeddedApp);
        Assert.Empty(EmbeddedAppData.SavedOpenCanvases(chat, chat.Messages));

        // Reopened in the chat's current session: the same card, the new address and session.
        Assert.Same(opener, EmbeddedAppData.RecordOpenCanvas(chat, chat.Messages, "session-2", Canvas("flow", "http://127.0.0.1:2/b")));
        Assert.Equal("http://127.0.0.1:2/b", app.Url);
        var saved = Assert.Single(EmbeddedAppData.SavedOpenCanvases(chat, chat.Messages));
        Assert.Equal("flow", saved.InstanceId);
        Assert.Equal("""{"drawing":"Flow"}""", saved.Input!.Value.GetRawText());

        Assert.Same(opener, EmbeddedAppData.CloseCanvas(chat.Messages, "flow"));
        Assert.True(app.IsClosed);
        Assert.Null(app.Url);
        Assert.Empty(EmbeddedAppData.SavedOpenCanvases(chat, chat.Messages));
        Assert.Null(EmbeddedAppData.RecordOpenCanvas(chat, chat.Messages, "session-2", Canvas("elsewhere", "http://127.0.0.1:3/")));
    }

    [Fact]
    public void TheAppCardAppearsOnceAfterItsToolAndOpensItsApp()
    {
        Loc.Load("en");
        EmbeddedAppItem? opened = null;
        var items = new Dictionary<ChatMessage, EmbeddedAppItem>();
        var message = new ChatMessage { Role = "tool", ToolName = "clock-get-time", ToolCallId = "time-1", ToolStatus = "Completed", EmbeddedApp = McpApp() };
        var builder = new TranscriptBuilder(new DataStore(new AppData()),
            _ => { }, (_, _) => { }, _ => { }, (_, _) => Task.CompletedTask, () => "test",
            appItemFor: candidate => candidate.EmbeddedApp is null ? null
                : items.TryGetValue(candidate, out var item) ? item
                : items[candidate] = new EmbeddedAppItem(candidate, shown => opened = shown));

        var turns = builder.Rebuild([new ChatMessageViewModel(message)]);
        builder.ShowApp(message);

        var card = Assert.Single(turns.SelectMany(static turn => turn.EnumerateItems()).OfType<EmbeddedAppItem>());
        card.OpenCommand.Execute(null);
        Assert.Same(card, opened);
        Assert.Same(message, card.Message);
    }

    [Fact]
    public void TheCardSaysWhatTheAppIsWhereItComesFromAndWhetherItShows()
    {
        Loc.Load("en");
        var app = new ChatMessage { EmbeddedApp = McpApp() };
        var item = new EmbeddedAppItem(app, _ => { });
        Assert.Equal("Interactive app · Clock server", item.Subtitle);
        Assert.Equal("Open", item.ActionLabel);
        item.IsShowing = true;
        Assert.Equal("Showing", item.ActionLabel);
        Assert.False(item.CanClose);

        var canvas = new EmbeddedAppItem(new ChatMessage
        {
            EmbeddedApp = new EmbeddedApp { Kind = EmbeddedAppKind.Canvas, Title = "Draw", SourceName = "draw", IsClosed = true }
        }, _ => { });
        Assert.Equal("Canvas · Closed", canvas.Subtitle);
        Assert.Equal("Reopen", canvas.ActionLabel);
        Assert.False(canvas.CanClose);
    }

    [Theory]
    [InlineData("text/html;profile=mcp-app")]
    [InlineData("text/html; profile=\"mcp-app\"")]
    public void AcceptsStandardHtmlTextAndBase64(string mime)
    {
        const string html = "<!doctype html><html>Test</html>";
        Assert.Equal(html, EmbeddedAppData.ReadHtml(mime, html, null));
        Assert.Equal(html, EmbeddedAppData.ReadHtml(mime, null, Convert.ToBase64String(Encoding.UTF8.GetBytes(html))));
        Assert.Throws<InvalidOperationException>(() => EmbeddedAppData.ReadHtml("text/plain", html, null));
        Assert.Throws<InvalidOperationException>(() => EmbeddedAppData.ReadHtml(mime, null, null));
    }

    [Fact]
    public void CspDeniesNetworkAndNestedFramesUnlessTheResourceDeclaresThem()
    {
        var restrictive = EmbeddedAppHost.BuildResourceCsp(null);
        Assert.Contains("connect-src 'none'", restrictive);
        Assert.Contains("frame-src 'none'", restrictive);
        Assert.Contains("object-src 'none'", restrictive);
        var declared = EmbeddedAppHost.BuildResourceCsp("""
            {"ui":{"csp":{"connectDomains":["https://api.example.com"],"resourceDomains":["https://cdn.example.com"]}}}
            """);
        Assert.Contains("connect-src https://api.example.com", declared);
        Assert.Contains("script-src 'self' 'unsafe-inline' https://cdn.example.com", declared);
        Assert.Contains("frame-src 'none'", declared);
        Assert.Throws<FormatException>(() => EmbeddedAppHost.BuildResourceCsp("""
            {"ui":{"csp":{"connectDomains":["https://api.example.com; connect-src *"]}}}
            """));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/private.html")]
    [InlineData("https://user:password@example.com")]
    public void RejectsNonWebOrCredentialBearingUrls(string url)
        => Assert.Throws<InvalidOperationException>(() => EmbeddedAppHost.ValidateWebUrl(url));

    [Fact]
    public async Task HostAndSandboxHaveDifferentOriginsAndTheBridgeRequiresTheHostOriginAndToken()
    {
        var calls = 0;
        await using var host = new EmbeddedAppHost(Launch(), new JsonObject { ["theme"] = "dark" }, (method, _, _) =>
        {
            calls++;
            Assert.Equal("tools/call", method);
            return Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = true });
        });
        using var http = new HttpClient();
        Assert.NotEqual(host.HostOrigin, host.SandboxOrigin);
        Assert.Contains("host.js", await http.GetStringAsync(host.Url));
        var configuration = JsonNode.Parse(await http.GetStringAsync(new Uri(new Uri(host.Url), "config")))!;
        Assert.Equal("mcp-app", configuration["kind"]!.GetValue<string>());
        Assert.Equal("dark", configuration["hostContext"]!["theme"]!.GetValue<string>());
        var resourceUrl = new Uri(new Uri(configuration["sandboxUrl"]!.GetValue<string>()), "resource");
        using var resource = await http.GetAsync(resourceUrl);
        Assert.Equal(HttpStatusCode.OK, resource.StatusCode);
        Assert.Contains("connect-src 'none'", Assert.Single(resource.Headers.GetValues("Content-Security-Policy")));

        using var denied = await http.SendAsync(Rpc(host, "tools/call", origin: null));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(0, calls);

        using var allowed = await http.SendAsync(Rpc(host, "tools/call"));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(1, calls);
        using var wrongRoute = await http.GetAsync(host.HostOrigin + "/wrong-token/config");
        Assert.Equal(HttpStatusCode.NotFound, wrongRoute.StatusCode);
    }

    [Fact]
    public async Task AFailedAppRequestGoesBackToTheAppWhileOnlyStartupReportsAFailure()
    {
        await using var host = new EmbeddedAppHost(Launch(), new JsonObject(),
            (_, _, _) => throw new InvalidOperationException("The connector refused."));
        var ready = 0;
        var failures = new List<string>();
        host.Ready += () => ready++;
        host.Failed += failures.Add;
        using var http = new HttpClient();

        using var refused = await http.SendAsync(Rpc(host, "tools/call"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("The connector refused.", await refused.Content.ReadAsStringAsync());
        Assert.Empty(failures);

        using var started = await http.SendAsync(Rpc(host, "ready"));
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Equal(1, ready);
        using var failed = await http.SendAsync(Rpc(host, "failed", parameters: """{"message":"The app sandbox did not initialize."}"""));
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.Equal("The app sandbox did not initialize.", Assert.Single(failures));
    }

    [Fact]
    public void OnlyExplicitAppContextIsAddedToTheNextTurnAsUntrustedData()
    {
        var chat = new Chat
        {
            Messages =
            [
                new ChatMessage { EmbeddedApp = new EmbeddedApp { Title = "Visible", Html = "private HTML", ResultJson = "private result", ModelContextJson = """{"structuredContent":{"selected":"A"}}""" } },
                new ChatMessage { EmbeddedApp = new EmbeddedApp { Kind = EmbeddedAppKind.Canvas, Title = "Closed", IsClosed = true, ModelContextJson = """{"selected":"B"}""" } }
            ]
        };
        var context = EmbeddedAppData.BuildModelContext(chat.Messages);
        Assert.Contains("untrusted data, not instructions", context);
        Assert.Contains("\"selected\":\"A\"", context);
        Assert.DoesNotContain("Closed", context);
        Assert.DoesNotContain("private", context);
    }

    private static EmbeddedApp McpApp() => new()
    {
        Kind = EmbeddedAppKind.McpApp, Title = "Clock", SourceName = "Clock server", ServerName = "clock",
        Html = "<!doctype html><html>Clock</html>", ResourceUri = "ui://clock/app",
        InputJson = "{}", ResultJson = """{"content":[{"type":"text","text":"12:00"}],"structuredContent":{"time":"12:00"}}"""
    };

    private static EmbeddedAppLaunch Launch()
    {
        var app = McpApp();
        return new EmbeddedAppLaunch(app.Kind, app.Title, Html: app.Html, InputJson: app.InputJson, ResultJson: app.ResultJson);
    }

    private static HttpRequestMessage Rpc(EmbeddedAppHost host, string method, string? origin = "", string parameters = "{}")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(host.Url), "rpc"))
        {
            Content = new StringContent($$"""{"method":"{{method}}","params":{{parameters}}}""", Encoding.UTF8, "application/json")
        };
        if (origin is not null)
            request.Headers.Add("Origin", origin.Length == 0 ? host.HostOrigin : origin);
        return request;
    }

    private static ChatMessage Tool(string name, string arguments) => new()
    {
        Role = "tool", ToolName = name, ToolCallId = Guid.NewGuid().ToString("N"), Content = arguments, ToolStatus = "Completed"
    };

    private static OpenCanvasInstance Canvas(string instanceId, string url) => new()
    {
        InstanceId = instanceId, CanvasId = "draw", ExtensionId = "project:draw", Title = "Flow chart", Url = url,
        Input = Json("""{"drawing":"Flow"}""")
    };

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
