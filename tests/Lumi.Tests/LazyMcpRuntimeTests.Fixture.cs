using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitHub.Copilot;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed partial class LazyMcpRuntimeTests
{
    private static void AssertSuccess(JsonElement response)
        => Assert.True(response.TryGetProperty("result", out _) && !response.TryGetProperty("error", out _), response.GetRawText());

    private static void AssertError(JsonElement response)
        => Assert.True(response.TryGetProperty("error", out _) && !response.TryGetProperty("result", out _), response.GetRawText());

    private static void AssertJsonEqual(JsonElement expected, JsonElement actual)
        => Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected.GetRawText()), JsonNode.Parse(actual.GetRawText())),
            $"Expected {expected.GetRawText()}, got {actual.GetRawText()}");

    // Shared only with the gated CLI capture test: no SDK/LLM is needed for the proxy tests.
    internal sealed class FakeMcp : IDisposable
    {
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lumi-lazy-mcp-" + Guid.NewGuid().ToString("N"));
        public string CacheDirectory => Path.Combine(Root, "cache");
        private string ScriptPath => Path.Combine(Root, "fake-mcp.ps1");
        public string[] Starts => File.Exists(Path.Combine(Root, "starts.log"))
            ? ReadAllLinesShared(Path.Combine(Root, "starts.log")) : [];

        public FakeMcp(string behavior = "static")
        {
            Skip.IfNot(OperatingSystem.IsWindows(), "PowerShell fake MCP server is Windows-only.");
            Directory.CreateDirectory(Root);
            SetBehavior(behavior);
            File.WriteAllText(ScriptPath, """
                $ErrorActionPreference = "Stop"
                [System.IO.File]::AppendAllText($env:MCP_TEST_STARTS, "$PID`n")
                function Write-Json($obj) {
                    [Console]::Out.WriteLine(($obj | ConvertTo-Json -Compress -Depth 40))
                    [Console]::Out.Flush()
                }
                $startupMode = [System.IO.File]::ReadAllText($env:MCP_TEST_BEHAVIOR)
                if ($startupMode -in @("startup-logs", "startup-logs-timeout")) {
                    [Console]::Out.WriteLine("[NuGet Manager] [Info] Credential provider ready token=startup-secret")
                    [Console]::Out.Flush()
                } elseif ($startupMode -eq "malformed-stdout") {
                    [Console]::Out.WriteLine('{"jsonrpc":"2.0","id":')
                    [Console]::Out.Flush()
                }
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    [System.IO.File]::AppendAllText($env:MCP_TEST_MESSAGES, "$line`n")
                    $msg = $line | ConvertFrom-Json
                    $mode = [System.IO.File]::ReadAllText($env:MCP_TEST_BEHAVIOR)
                    if ($msg.id -eq "server-roots" -and $null -ne $msg.error -and $null -ne $pendingCall) {
                        Write-Json @{ jsonrpc = "2.0"; id = $pendingCall; error = @{ code = -32603; message = "Required roots request failed" } }
                        $pendingCall = $null
                        continue
                    }
                    if ($msg.method -eq "initialize") {
                        if ($mode -eq "startup-logs-timeout") { continue }
                        if ($mode -eq "initialize-error") {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; error = @{ code = -32000; message = "Synthetic startup failure" } }
                            continue
                        }
                        $capabilities = @{ tools = @{ listChanged = $false } }
                        if ($mode -eq "tools-empty") { $capabilities.tools = @{} }
                        if ($mode -eq "dynamic") { $capabilities.tools.listChanged = $true }
                        if ($mode -eq "resources") { $capabilities.resources = @{} }
                        $instructions = "Synthetic echo server"
                        if ($mode -eq "instructions-drift") { $instructions = "Changed instructions" }
                        $version = "1"
                        if ($mode -eq "version-drift") { $version = "2" }
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{
                            protocolVersion = $msg.params.protocolVersion; capabilities = $capabilities
                            serverInfo = @{ name = "lazy-test-mcp"; version = $version }; instructions = $instructions
                        } }
                    } elseif ($msg.method -eq "tools/list") {
                        if (($mode -eq "discovery-session-lost" -or $mode -eq "recovery-selected-missing") -and
                            -not [System.IO.File]::Exists("session-lost")) {
                            [System.IO.File]::WriteAllText("session-lost", "")
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; error = @{ code = -32001; message = "Session not found" } }
                            continue
                        }
                        if ($mode -eq "slow-list") {
                            $until = [DateTime]::UtcNow.AddSeconds(8)
                            while (-not [System.IO.File]::Exists("continue-list") -and [DateTime]::UtcNow -lt $until) {
                                Start-Sleep -Milliseconds 20
                            }
                        }
                        if ($mode -eq "list-error") {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; error = @{ code = -32603; message = "Synthetic discovery failure" } }
                            continue
                        }
                        if ($mode -eq "discovery-notification") {
                            Write-Json @{ jsonrpc = "2.0"; method = "notifications/tools/list_changed" }
                        }
                        if ($mode -eq "discovery-callback") {
                            Write-Json @{ jsonrpc = "2.0"; id = "server-roots"; method = "roots/list"; params = @{} }
                        }
                        $tool = @{
                            name = "echo"; description = "Echo complete arguments"
                            inputSchema = @{ type = "object"; properties = @{ value = @{ type = "string" } } }
                            annotations = @{ readOnlyHint = $true }
                            outputSchema = @{ type = "object" }
                            _meta = @{ test = "original" }
                        }
                        if ($mode -eq "description-drift") { $tool.description = "Changed description" }
                        if ($mode -eq "schema-drift") { $tool.inputSchema.properties.value.type = "integer" }
                        if ($mode -eq "annotations-drift") { $tool.annotations.readOnlyHint = $false }
                        if ($mode -eq "output-schema-drift") { $tool.outputSchema.type = "string" }
                        if ($mode -eq "meta-drift") { $tool._meta.test = "changed" }
                        $other = @{ name = "other"; description = "Unchanged tool"; inputSchema = @{ type = "object" } }
                        $result = @{ tools = @($tool, $other) }
                        if ($mode -eq "paginated-later-tool") {
                            if ($null -ne $msg.params.cursor) {
                                $result.tools = @($tool)
                            } else {
                                $result.tools = @($other)
                                $result.nextCursor = "page-2"
                            }
                        }
                        if ($mode -eq "list-reordered") { $result.tools = @($other, $tool) }
                        if ($mode -eq "unrelated-added") {
                            $result.tools += @{ name = "extra"; inputSchema = @{ type = "object" } }
                        }
                        if ($mode -eq "unrelated-removed") { $result.tools = @($tool) }
                        if ($mode -eq "selected-missing" -or $mode -eq "recovery-selected-missing") {
                            $result.tools = @($other)
                        }
                        if ($mode -eq "paginated") { $result.nextCursor = "page-2" }
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = $result }
                    } elseif ($msg.method -eq "tools/call") {
                        if ($mode -eq "call-session-lost" -and -not [System.IO.File]::Exists("session-lost")) {
                            [System.IO.File]::WriteAllText("session-lost", "")
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; error = @{ code = -32001; message = "Session not found" } }
                            continue
                        }
                        if ($mode -eq "slow-call") {
                            $until = [DateTime]::UtcNow.AddSeconds(8)
                            while (-not [System.IO.File]::Exists("continue-call") -and [DateTime]::UtcNow -lt $until) {
                                Start-Sleep -Milliseconds 20
                            }
                        }
                        if ($mode -eq "list-changed") {
                            Write-Json @{ jsonrpc = "2.0"; method = "notifications/tools/list_changed" }
                        }
                        if ($mode -eq "server-request") {
                            Write-Json @{ jsonrpc = "2.0"; id = "server-roots"; method = "roots/list"; params = @{} }
                        }
                        if ($mode -eq "server-request-failure") {
                            $pendingCall = $msg.id
                            Write-Json @{ jsonrpc = "2.0"; id = "server-roots"; method = "roots/list"; params = @{} }
                            continue
                        }
                        if ($mode -eq "call-error") {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; error = @{ code = -32603; message = "Synthetic call failure" } }
                        } else {
                            $echo = $msg.params.arguments | ConvertTo-Json -Compress -Depth 30
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ content = @(@{ type = "text"; text = $echo }) } }
                        }
                    } elseif ($msg.method -eq "ping") {
                        if ($mode -eq "exit-on-ping") { exit 0 }
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{} }
                    }
                }
                """);
        }

        public void SetBehavior(string behavior) => File.WriteAllText(Path.Combine(Root, "behavior.txt"), behavior);

        public McpProxyServerDefinition Definition(
            bool lazy = true,
            McpToolCallPreflightPolicy toolCallPreflightPolicy = McpToolCallPreflightPolicy.None) => new(
            "test:lazy",
            "lazy-test",
            new McpStdioServerConfig
            {
                Command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe"),
                Args = ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptPath],
                WorkingDirectory = Root,
                Env = new Dictionary<string, string>
                {
                    ["MCP_TEST_STARTS"] = Path.Combine(Root, "starts.log"),
                    ["MCP_TEST_MESSAGES"] = Path.Combine(Root, "messages.jsonl"),
                    ["MCP_TEST_BEHAVIOR"] = Path.Combine(Root, "behavior.txt")
                },
                Tools = ["*"],
                Timeout = 10_000
            },
            lazy,
            toolCallPreflightPolicy);

        public static JsonNode ClientInitialize() => JsonNode.Parse(
            """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}""")!;

        // Captured from the real Copilot CLI, without sending a prompt, on 2026-09-09.
        public static JsonNode CopilotClientInitialize() => JsonNode.Parse(
            """{"protocolVersion":"2025-11-25","capabilities":{"extensions":{"io.modelcontextprotocol/tasks":{},"io.modelcontextprotocol/ui":{"mimeTypes":["text/html;profile=mcp-app"]}},"sampling":{}},"clientInfo":{"name":"github-copilot-developer","version":"1.0.78"}}""")!;

        // Captured from Copilot CLI 1.0.84-5 before its legacy initialize fallback.
        public static JsonNode CopilotServerDiscover() => JsonNode.Parse(
            """{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"copilot-cli","version":"1.0.84-5"},"io.modelcontextprotocol/clientCapabilities":{"sampling":{}}}}""")!;

        public static JsonNode CopilotCliInitialize() => JsonNode.Parse(
            """{"protocolVersion":"2025-11-25","capabilities":{"sampling":{}},"clientInfo":{"name":"copilot-cli","version":"1.0.84-5"}}""")!;

        public Task<JsonElement> InitializeAsync(string url, JsonNode? client = null)
            => RequestAsync(url, "initialize", client ?? ClientInitialize());

        public async Task PrimeAsync(bool lazy = true)
        {
            await using var runtime = new McpProxyRuntime(CacheDirectory);
            var remote = runtime.Register(Definition(lazy));
            AssertSuccess(await InitializeAsync(remote.Url));
            AssertSuccess(await RequestAsync(remote.Url, "tools/list", new { }));
            Assert.Single(Starts);
        }

        public async Task<JsonElement> RequestAsync(string url, string method, object? parameters = null, string? id = null)
            => await RequestCoreAsync(url, method, parameters, id, CancellationToken.None);

        private async Task<JsonElement> RequestCoreAsync(
            string url,
            string method,
            object? parameters,
            string? id,
            CancellationToken cancellationToken)
        {
            var request = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0", ["id"] = id ?? Guid.NewGuid().ToString("N"), ["method"] = method
            };
            if (parameters is not null)
                request["params"] = parameters;
            using var body = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, body, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.Clone();
        }

        public async Task NotifyInitializedAsync(string url)
        {
            using var body = new StringContent(
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""", Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, body);
            response.EnsureSuccessStatusCode();
        }

        public async Task<HttpStatusCode> PostStatusAsync(string url, string message)
        {
            using var body = new StringContent(message, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, body);
            return response.StatusCode;
        }

        public async Task WaitForMessageCountAsync(string method, int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (Messages(method).Length < count)
                await Task.Delay(20, timeout.Token);
        }

        public async Task WaitForLatestProcessExitAsync()
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(int.Parse(Starts.Last()));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (ArgumentException)
            {
                // The process may already have been reaped.
            }
        }

        public JsonElement[] Messages(string? method = null)
        {
            var path = Path.Combine(Root, "messages.jsonl");
            if (!File.Exists(path))
                return [];

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            // A concurrent append is visible only once its complete line has been written.
            return text[..(text.LastIndexOf('\n') + 1)].Split('\n')
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .Where(message => method is null || (message.TryGetProperty("method", out var value) && value.GetString() == method))
                .ToArray();
        }

        private static string[] ReadAllLinesShared(string path)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
                lines.Add(line);
            return lines.ToArray();
        }

        public void Dispose()
        {
            _http.Dispose();
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
