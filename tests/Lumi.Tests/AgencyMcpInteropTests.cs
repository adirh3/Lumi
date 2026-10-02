using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GitHub.Copilot;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class AgencyMcpInteropTests
{
    private const string AgencyPathVariable = "LUMI_AGENCY_INTEROP_EXE";

    [SkippableFact]
    [Trait("Category", "AgencyInterop")]
    public async Task LazyCombinedStack_RepairsBeforeDispatchAndNeverReplaysBusinessCalls()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Agency interop fixture is Windows-only.");
        var agencyPath = Environment.GetEnvironmentVariable(AgencyPathVariable);
        Skip.If(string.IsNullOrWhiteSpace(agencyPath), $"{AgencyPathVariable} is not configured.");
        Assert.True(File.Exists(agencyPath), $"Agency executable was not found: {agencyPath}");

        var root = Path.Combine(Path.GetTempPath(), "lumi-agency-interop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var upstream = new AgencyLoopbackFixture();
            await using var runtime = new McpProxyRuntime();
            var definition = new McpProxyServerDefinition(
                "test:agency-interop",
                "agency-interop",
                new McpStdioServerConfig
                {
                    Command = Path.GetFullPath(agencyPath!),
                    Args =
                    [
                        "--log-dir", Path.Combine(root, "agency-logs"),
                        "mcp", "remote",
                        "--url", upstream.Url,
                        "--no-aec"
                    ],
                    WorkingDirectory = root,
                    Tools = ["*"],
                    Timeout = 120_000
                },
                UseLazyInitialization: true,
                ToolCallPreflightPolicy: AgencyMcpSessionRecovery.Policy);

            using var first = runtime.AcquireSessionRegistration(definition);
            using var second = runtime.AcquireSessionRegistration(definition);
            Assert.Equal(
                new Uri(first.ServerConfig.Url).AbsolutePath,
                new Uri(second.ServerConfig.Url).AbsolutePath);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            await InitializeClientAsync(http, first.ServerConfig.Url);
            await InitializeClientAsync(http, second.ServerConfig.Url);
            using (var discovery = await PostJsonAsync(http, first.ServerConfig.Url, """
                       {"jsonrpc":"2.0","id":"list","method":"tools/list","params":{}}
                       """))
            {
                Assert.True(discovery.RootElement.TryGetProperty("result", out _));
            }

            using (var warm = await PostJsonAsync(http, first.ServerConfig.Url, """
                       {"jsonrpc":"2.0","id":"warm","method":"tools/call","params":{"name":"read_probe","arguments":{"marker":"warm"}}}
                       """))
            {
                Assert.True(warm.RootElement.TryGetProperty("result", out _));
            }
            Assert.Equal(1, upstream.CountRequests("tools/call", "warm"));

            upstream.ExpireSession();
            using (var expired = await PostJsonAsync(http, first.ServerConfig.Url, """
                       {"jsonrpc":"2.0","id":"expired","method":"tools/call","params":{"name":"read_probe","arguments":{"marker":"expired"}}}
                       """))
            {
                Assert.True(expired.RootElement.TryGetProperty("result", out _));
            }
            Assert.Equal(1, upstream.CountRequests("tools/call", "expired"));
            Assert.Equal(2, upstream.InitializeRequests);

            upstream.ExpireSession();
            var concurrent = await Task.WhenAll(
                PostJsonAsync(http, first.ServerConfig.Url, """
                    {"jsonrpc":"2.0","id":"concurrent-a","method":"tools/call","params":{"name":"read_probe","arguments":{"marker":"concurrent-a"}}}
                    """),
                PostJsonAsync(http, second.ServerConfig.Url, """
                    {"jsonrpc":"2.0","id":"concurrent-b","method":"tools/call","params":{"name":"read_probe","arguments":{"marker":"concurrent-b"}}}
                    """));
            try
            {
                Assert.All(concurrent, response =>
                    Assert.True(response.RootElement.TryGetProperty("result", out _)));
            }
            finally
            {
                foreach (var response in concurrent)
                    response.Dispose();
            }
            Assert.Equal(1, upstream.CountRequests("tools/call", "concurrent-a"));
            Assert.Equal(1, upstream.CountRequests("tools/call", "concurrent-b"));
            Assert.Equal(3, upstream.InitializeRequests);

            upstream.ExpireSession();
            upstream.FailNextValidToolsList();
            using (var failedValidation = await PostJsonAsync(http, first.ServerConfig.Url, """
                       {"jsonrpc":"2.0","id":"failed-validation","method":"tools/call","params":{"name":"read_probe","arguments":{"marker":"failed-validation"}}}
                       """))
            {
                Assert.True(failedValidation.RootElement.TryGetProperty("error", out _));
            }
            Assert.Equal(0, upstream.CountRequests("tools/call", "failed-validation"));

            using (var recovered = await PostJsonAsync(http, first.ServerConfig.Url, """
                       {"jsonrpc":"2.0","id":"after-failed-validation","method":"tools/call","params":{"name":"read_probe","arguments":{"marker":"after-failed-validation"}}}
                       """))
            {
                Assert.True(recovered.RootElement.TryGetProperty("result", out _));
            }
            Assert.Equal(1, upstream.CountRequests("tools/call", "after-failed-validation"));

            upstream.DropNextWriteResponse();
            using (var ambiguous = await PostJsonAsync(http, first.ServerConfig.Url, """
                       {"jsonrpc":"2.0","id":"ambiguous","method":"tools/call","params":{"name":"write_probe","arguments":{"marker":"ambiguous"}}}
                       """))
            {
                Assert.True(ambiguous.RootElement.TryGetProperty("error", out _));
            }
            Assert.Equal(1, upstream.WriteEffects);
            Assert.Equal(1, upstream.CountRequests("tools/call", "ambiguous"));
            Assert.False(upstream.AuthorizationObserved);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task InitializeClientAsync(HttpClient http, string url)
    {
        using var initialize = await PostJsonAsync(http, url, """
            {"jsonrpc":"2.0","id":"initialize","method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"lumi-agency-interop","version":"1"}}}
            """);
        Assert.True(initialize.RootElement.TryGetProperty("result", out _));
        using var initialized = await http.PostAsync(
            url,
            JsonContent("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        initialized.EnsureSuccessStatusCode();
    }

    private static async Task<JsonDocument> PostJsonAsync(HttpClient http, string url, string json)
    {
        using var response = await http.PostAsync(url, JsonContent(json));
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static StringContent JsonContent(string json)
        => new(json, Encoding.UTF8, "application/json");

    private sealed class AgencyLoopbackFixture : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly List<RequestRecord> _requests = [];
        private readonly Task _serverTask;
        private int _generation;
        private bool _expired;
        private bool _initialized;
        private bool _failNextValidToolsList;
        private bool _dropNextWriteResponse;
        private int _writeEffects;
        private bool _authorizationObserved;

        public AgencyLoopbackFixture()
        {
            var port = GetFreeLoopbackPort();
            Url = $"http://127.0.0.1:{port}/mcp/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _serverTask = Task.Run(ServeAsync);
        }

        public string Url { get; }
        public int InitializeRequests { get { lock (_gate) return _generation; } }
        public int WriteEffects { get { lock (_gate) return _writeEffects; } }
        public bool AuthorizationObserved { get { lock (_gate) return _authorizationObserved; } }

        public void ExpireSession()
        {
            lock (_gate)
                _expired = true;
        }

        public void FailNextValidToolsList()
        {
            lock (_gate)
                _failNextValidToolsList = true;
        }

        public void DropNextWriteResponse()
        {
            lock (_gate)
                _dropNextWriteResponse = true;
        }

        public int CountRequests(string method, params string[] markers)
        {
            lock (_gate)
            {
                var expected = markers.ToHashSet(StringComparer.Ordinal);
                return _requests.Count(request =>
                    request.Method == method
                    && (expected.Count == 0 || request.Marker is not null && expected.Contains(request.Marker)));
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _listener.Stop();
            _listener.Close();
            try { await _serverTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (HttpListenerException) { }
            _cancellation.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(_cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException) when (_cancellation.IsCancellationRequested)
                {
                    break;
                }

                _ = Task.Run(() => HandleAsync(context), _cancellation.Token);
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            JsonElement response;
            int statusCode;
            string? responseSession = null;
            var abortResponse = false;
            using (var document = await JsonDocument.ParseAsync(context.Request.InputStream))
            {
                var request = document.RootElement;
                var method = request.GetProperty("method").GetString()!;
                var marker = request.TryGetProperty("params", out var parameters)
                    && parameters.TryGetProperty("arguments", out var arguments)
                    && arguments.TryGetProperty("marker", out var markerElement)
                        ? markerElement.GetString()
                        : null;
                var requestSession = context.Request.Headers["Mcp-Session-Id"];
                lock (_gate)
                {
                    _authorizationObserved |= !string.IsNullOrWhiteSpace(
                        context.Request.Headers["Authorization"]);
                    statusCode = 200;
                    var expectedSession = $"synthetic-{_generation}";
                    if (method == "initialize")
                    {
                        _generation++;
                        _expired = false;
                        _initialized = false;
                        responseSession = $"synthetic-{_generation}";
                        response = JsonSerializer.SerializeToElement(new
                        {
                            jsonrpc = "2.0",
                            id = request.GetProperty("id").Clone(),
                            result = new
                            {
                                protocolVersion = "2025-03-26",
                                capabilities = new { tools = new { } },
                                serverInfo = new { name = "agency-loopback", version = "1" }
                            }
                        });
                    }
                    else if (_expired || !string.Equals(requestSession, expectedSession, StringComparison.Ordinal))
                    {
                        statusCode = 404;
                        response = Error(request, -32001, "Session not found");
                    }
                    else if (method == "notifications/initialized")
                    {
                        _initialized = true;
                        statusCode = 202;
                        response = default;
                    }
                    else if (method == "tools/list")
                    {
                        if (_initialized && _failNextValidToolsList)
                        {
                            _failNextValidToolsList = false;
                            response = Error(request, -32002, "Synthetic validation failure");
                        }
                        else
                        {
                            response = _initialized
                                ? JsonSerializer.SerializeToElement(new
                                {
                                    jsonrpc = "2.0",
                                    id = request.GetProperty("id").Clone(),
                                    result = new
                                    {
                                        tools = new object[]
                                        {
                                            new
                                            {
                                                name = "read_probe",
                                                description = "Synthetic unannotated read",
                                                inputSchema = new { type = "object" }
                                            },
                                            new
                                            {
                                                name = "write_probe",
                                                description = "Synthetic unannotated write",
                                                inputSchema = new { type = "object" }
                                            }
                                        }
                                    }
                                })
                                : Error(request, -32002, "Initialization notification missing");
                        }
                    }
                    else if (method == "tools/call")
                    {
                        var toolName = request.GetProperty("params").GetProperty("name").GetString();
                        if (toolName == "write_probe")
                        {
                            _writeEffects++;
                            abortResponse = _dropNextWriteResponse;
                            _dropNextWriteResponse = false;
                        }

                        response = abortResponse
                            ? default
                            : JsonSerializer.SerializeToElement(new
                            {
                                jsonrpc = "2.0",
                                id = request.GetProperty("id").Clone(),
                                result = new
                                {
                                    content = new[] { new { type = "text", text = toolName + "_ok" } },
                                    isError = false
                                }
                            });
                    }
                    else
                    {
                        response = Error(request, -32601, "Method not found");
                    }

                    _requests.Add(new RequestRecord(method, marker));
                }
            }

            if (abortResponse)
            {
                context.Response.Abort();
                return;
            }

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            if (responseSession is not null)
                context.Response.Headers["Mcp-Session-Id"] = responseSession;
            var body = statusCode == 202 ? [] : JsonSerializer.SerializeToUtf8Bytes(response);
            context.Response.ContentLength64 = body.Length;
            if (body.Length > 0)
                await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }

        private static JsonElement Error(JsonElement request, int code, string message)
            => JsonSerializer.SerializeToElement(new
            {
                jsonrpc = "2.0",
                id = request.TryGetProperty("id", out var id) ? id.Clone() : default,
                error = new { code, message }
            });

        private static int GetFreeLoopbackPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        private sealed record RequestRecord(string Method, string? Marker);
    }
}
