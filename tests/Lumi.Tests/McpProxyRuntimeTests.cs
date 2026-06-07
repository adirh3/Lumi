using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class McpProxyRuntimeTests
{
    [SkippableFact]
    public async Task Proxy_StartsStdioServerLazilyAndReusesSingletonProcess()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PowerShell fake MCP server is Windows-only.");

        var root = Path.Combine(Path.GetTempPath(), "lumi-mcp-proxy-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "fake-mcp.ps1");
            var logPath = Path.Combine(root, "starts.log");
            await File.WriteAllTextAsync(scriptPath, """
                [System.IO.File]::AppendAllText($env:MCP_TEST_LOG, "$PID`n")
                function Write-Json($obj) {
                    [Console]::Out.WriteLine(($obj | ConvertTo-Json -Compress -Depth 30))
                    [Console]::Out.Flush()
                }
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    $msg = $line | ConvertFrom-Json
                    if ($msg.method -eq "initialize") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ protocolVersion = "2025-06-18"; capabilities = @{ tools = @{ listChanged = $false } }; serverInfo = @{ name = "proxy-test-mcp"; version = "1" } } }
                    } elseif ($msg.method -eq "tools/list") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ tools = @(@{ name = "emit_marker"; description = "Emit marker"; inputSchema = @{ type = "object"; properties = @{ value = @{ type = "string" } }; required = @("value") } }) } }
                    } elseif ($msg.method -eq "tools/call") {
                        $value = [string]$msg.params.arguments.value
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ content = @(@{ type = "text"; text = "PROXY_MARKER:$value" }) } }
                    }
                }
                """);

            await using var runtime = new McpProxyRuntime();
            var remote = runtime.Register(new McpProxyServerDefinition(
                "test:proxy",
                "proxy-test",
                new McpStdioServerConfig
                {
                    Command = GetPowerShellPath(),
                    Args = ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                    WorkingDirectory = root,
                    Env = new Dictionary<string, string>
                    {
                        ["MCP_TEST_LOG"] = logPath
                    },
                    Tools = ["*"]
                }));

            Assert.StartsWith("http://127.0.0.1:", remote.Url, StringComparison.Ordinal);
            Assert.False(File.Exists(logPath));

            using var http = new HttpClient();
            using var initialize = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}
                """);
            Assert.Equal("init", initialize.RootElement.GetProperty("id").GetString());
            Assert.True(initialize.RootElement.TryGetProperty("result", out _));

            using var list = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"list","method":"tools/list","params":{}}
                """);
            Assert.Equal("list", list.RootElement.GetProperty("id").GetString());
            Assert.Equal("emit_marker", list.RootElement.GetProperty("result").GetProperty("tools")[0].GetProperty("name").GetString());

            using var call = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"call","method":"tools/call","params":{"name":"emit_marker","arguments":{"value":"OK"}}}
                """);
            Assert.Equal("call", call.RootElement.GetProperty("id").GetString());
            var text = call.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
            Assert.Equal("PROXY_MARKER:OK", text);

            var starts = await File.ReadAllLinesAsync(logPath);
            Assert.Single(starts);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [SkippableFact]
    public async Task Proxy_ResolvesPathCommandBeforeStartingStdioServer()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows PATH command resolution is Windows-specific.");

        var root = Path.Combine(Path.GetTempPath(), "lumi-mcp-proxy-path-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var binDir = Path.Combine(root, "bin");
            var workDir = Path.Combine(root, "work");
            Directory.CreateDirectory(binDir);
            Directory.CreateDirectory(workDir);

            var scriptPath = Path.Combine(binDir, "fake-mcp.ps1");
            var shimPath = Path.Combine(binDir, "fake-npx.cmd");
            var logPath = Path.Combine(root, "starts.log");
            await File.WriteAllTextAsync(scriptPath, """
                [System.IO.File]::AppendAllText($env:MCP_TEST_LOG, "$PID|$PWD`n")
                function Write-Json($obj) {
                    [Console]::Out.WriteLine(($obj | ConvertTo-Json -Compress -Depth 30))
                    [Console]::Out.Flush()
                }
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    $msg = $line | ConvertFrom-Json
                    if ($msg.method -eq "initialize") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ protocolVersion = "2025-06-18"; capabilities = @{ tools = @{ listChanged = $false } }; serverInfo = @{ name = "path-test-mcp"; version = "1" } } }
                    } elseif ($msg.method -eq "tools/call") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ content = @(@{ type = "text"; text = "PATH_MARKER" }) } }
                    }
                }
                """);
            await File.WriteAllTextAsync(shimPath, $"""
                @echo off
                "{GetPowerShellPath()}" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0fake-mcp.ps1"
                """);

            await using var runtime = new McpProxyRuntime();
            var remote = runtime.Register(new McpProxyServerDefinition(
                "test:path",
                "path-test",
                new McpStdioServerConfig
                {
                    Command = "fake-npx",
                    WorkingDirectory = workDir,
                    Env = new Dictionary<string, string>
                    {
                        ["MCP_TEST_LOG"] = logPath,
                        ["PATH"] = binDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                        ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD"
                    },
                    Tools = ["*"]
                }));

            using var http = new HttpClient();
            using var initialize = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}
                """);
            Assert.True(initialize.RootElement.TryGetProperty("result", out _));

            using var call = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"call","method":"tools/call","params":{"name":"emit_marker","arguments":{}}}
                """);
            Assert.Equal("PATH_MARKER", call.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());

            var start = Assert.Single(await File.ReadAllLinesAsync(logPath));
            Assert.Contains(workDir, start);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [SkippableFact]
    public async Task Proxy_ReturnsRawStartupOutputWhenServerWritesNonJson()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PowerShell fake MCP server is Windows-only.");

        var root = Path.Combine(Path.GetTempPath(), "lumi-mcp-proxy-diagnostics-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "bad-mcp.ps1");
            await File.WriteAllTextAsync(scriptPath, """
                [Console]::Error.WriteLine('The argument ''${workspaceFolder}/run-mcp.ps1'' is not recognized as the name of a script file.')
                [Console]::Error.WriteLine('Authorization: Bearer super-secret-token')
                [Console]::Error.Flush()
                Start-Sleep -Milliseconds 200
                [Console]::Out.WriteLine('Usage: pwsh[.exe] [-File] ${workspaceFolder}/run-mcp.ps1 token=super-secret-token')
                [Console]::Out.Flush()
                exit 64
                """);

            await using var runtime = new McpProxyRuntime();
            var remote = runtime.Register(new McpProxyServerDefinition(
                "test:diagnostics",
                "diagnostics-test",
                new McpStdioServerConfig
                {
                    Command = GetPowerShellPath(),
                    Args = ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                    WorkingDirectory = root,
                    Tools = ["*"]
                }));

            using var http = new HttpClient();
            using var initialize = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}
                """);

            var error = initialize.RootElement.GetProperty("error");
            Assert.Equal(-32000, error.GetProperty("code").GetInt32());
            var message = error.GetProperty("message").GetString();
            Assert.Contains("non-JSON output", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Usage: pwsh", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("${workspaceFolder}/run-mcp.ps1", message, StringComparison.Ordinal);
            Assert.Contains("token=[redacted]", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Authorization: [redacted]", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("super-secret-token", message, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [SkippableFact]
    public async Task Proxy_ReturnsActionableMessageWhenCommandIsMissing()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows command-not-found message is Windows-specific.");

        await using var runtime = new McpProxyRuntime();
        var remote = runtime.Register(new McpProxyServerDefinition(
            "test:missing-command",
            "missing-command-test",
            new McpStdioServerConfig
            {
                Command = "lumi-missing-mcp-command-" + Guid.NewGuid().ToString("N"),
                WorkingDirectory = Path.GetTempPath(),
                Tools = ["*"]
            }));

        using var http = new HttpClient();
        using var initialize = await PostJsonAsync(http, remote.Url, """
            {"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}
            """);

        var message = initialize.RootElement.GetProperty("error").GetProperty("message").GetString();
        Assert.Contains("Command 'lumi-missing-mcp-command-", message, StringComparison.Ordinal);
        Assert.Contains("was not found", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("add it to the PATH used by Lumi", message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Proxy_RestartsStdioServerAfterUnexpectedExit()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PowerShell fake MCP server is Windows-only.");

        var root = Path.Combine(Path.GetTempPath(), "lumi-mcp-proxy-restart-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "fake-mcp.ps1");
            var logPath = Path.Combine(root, "starts.log");
            var stoppedPath = Path.Combine(root, "stopped.log");
            await File.WriteAllTextAsync(scriptPath, """
                [System.IO.File]::AppendAllText($env:MCP_TEST_LOG, "$PID`n")
                function Write-Json($obj) {
                    [Console]::Out.WriteLine(($obj | ConvertTo-Json -Compress -Depth 30))
                    [Console]::Out.Flush()
                }
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    $msg = $line | ConvertFrom-Json
                    if ($msg.method -eq "initialize") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ protocolVersion = "2025-06-18"; capabilities = @{ tools = @{ listChanged = $false } }; serverInfo = @{ name = "restart-test-mcp"; version = "1" } } }
                    } elseif ($msg.method -eq "tools/list") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ tools = @(@{ name = "emit_marker"; description = "Emit marker"; inputSchema = @{ type = "object"; properties = @{ value = @{ type = "string" } }; required = @("value") } }) } }
                    } elseif ($msg.method -eq "tools/call") {
                        $value = [string]$msg.params.arguments.value
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ content = @(@{ type = "text"; text = "RESTART_MARKER:$value" }) } }
                        if ($value -eq "EXIT") {
                            [System.IO.File]::AppendAllText($env:MCP_STOPPED_LOG, "$PID`n")
                            exit 0
                        }
                    }
                }
                """);

            await using var runtime = new McpProxyRuntime();
            var remote = runtime.Register(new McpProxyServerDefinition(
                "test:restart",
                "restart-test",
                new McpStdioServerConfig
                {
                    Command = GetPowerShellPath(),
                    Args = ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                    WorkingDirectory = root,
                    Env = new Dictionary<string, string>
                    {
                        ["MCP_TEST_LOG"] = logPath,
                        ["MCP_STOPPED_LOG"] = stoppedPath
                    },
                    Tools = ["*"]
                }));

            using var http = new HttpClient();
            using var exitCall = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"exit-call","method":"tools/call","params":{"name":"emit_marker","arguments":{"value":"EXIT"}}}
            """);
            Assert.Equal("RESTART_MARKER:EXIT", exitCall.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
            await WaitForFileAsync(stoppedPath);
            var firstStart = Assert.Single(await File.ReadAllLinesAsync(logPath));
            await WaitForProcessExitAsync(int.Parse(firstStart, System.Globalization.CultureInfo.InvariantCulture));

            using var secondCall = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"second-call","method":"tools/call","params":{"name":"emit_marker","arguments":{"value":"OK"}}}
                """);
            Assert.Equal("RESTART_MARKER:OK", secondCall.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());

            var starts = await File.ReadAllLinesAsync(logPath);
            Assert.Equal(2, starts.Length);
            Assert.NotEqual(starts[0], starts[1]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [SkippableFact]
    public async Task Proxy_HandlesConcurrentFirstUseWithSingleProcess()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PowerShell fake MCP server is Windows-only.");

        var root = Path.Combine(Path.GetTempPath(), "lumi-mcp-proxy-concurrent-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "fake-mcp.ps1");
            var logPath = Path.Combine(root, "starts.log");
            var callsPath = Path.Combine(root, "calls.log");
            await File.WriteAllTextAsync(scriptPath, """
                [System.IO.File]::AppendAllText($env:MCP_TEST_LOG, "$PID`n")
                function Write-Json($obj) {
                    [Console]::Out.WriteLine(($obj | ConvertTo-Json -Compress -Depth 30))
                    [Console]::Out.Flush()
                }
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    $msg = $line | ConvertFrom-Json
                    if ($msg.method -eq "initialize") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ protocolVersion = "2025-06-18"; capabilities = @{ tools = @{ listChanged = $false } }; serverInfo = @{ name = "concurrent-test-mcp"; version = "1" } } }
                    } elseif ($msg.method -eq "tools/call") {
                        $value = [string]$msg.params.arguments.value
                        [System.IO.File]::AppendAllText($env:MCP_CALLS_LOG, "$value`n")
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ content = @(@{ type = "text"; text = "CONCURRENT_MARKER:$value" }) } }
                    }
                }
                """);

            await using var runtime = new McpProxyRuntime();
            var remote = runtime.Register(new McpProxyServerDefinition(
                "test:concurrent",
                "concurrent-test",
                new McpStdioServerConfig
                {
                    Command = GetPowerShellPath(),
                    Args = ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                    WorkingDirectory = root,
                    Env = new Dictionary<string, string>
                    {
                        ["MCP_TEST_LOG"] = logPath,
                        ["MCP_CALLS_LOG"] = callsPath
                    },
                    Tools = ["*"]
                }));

            using var http = new HttpClient();
            var calls = Enumerable.Range(0, 24)
                .Select(i => PostJsonAsync(
                    http,
                    remote.Url,
                    "{\"jsonrpc\":\"2.0\",\"id\":\"call-" + i + "\",\"method\":\"tools/call\",\"params\":{\"name\":\"emit_marker\",\"arguments\":{\"value\":\"" + i + "\"}}}"))
                .ToArray();

            using var allResponses = new CompositeJsonDocuments(await Task.WhenAll(calls));
            var returned = allResponses.Documents
                .Select(doc => doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString())
                .OrderBy(value => int.Parse(value!["CONCURRENT_MARKER:".Length..], System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();

            Assert.Equal(
                Enumerable.Range(0, 24).Select(i => "CONCURRENT_MARKER:" + i),
                returned);
            Assert.Single(await File.ReadAllLinesAsync(logPath));
            Assert.Equal(24, (await File.ReadAllLinesAsync(callsPath)).Length);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [SkippableFact]
    public async Task Proxy_DisposeStopsRunningProcessAndRejectsFurtherRegistrations()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PowerShell fake MCP server is Windows-only.");

        var root = Path.Combine(Path.GetTempPath(), "lumi-mcp-proxy-dispose-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "fake-mcp.ps1");
            var logPath = Path.Combine(root, "starts.log");
            await File.WriteAllTextAsync(scriptPath, """
                [System.IO.File]::AppendAllText($env:MCP_TEST_LOG, "$PID`n")
                function Write-Json($obj) {
                    [Console]::Out.WriteLine(($obj | ConvertTo-Json -Compress -Depth 30))
                    [Console]::Out.Flush()
                }
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    $msg = $line | ConvertFrom-Json
                    if ($msg.method -eq "initialize") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ protocolVersion = "2025-06-18"; capabilities = @{ tools = @{ listChanged = $false } }; serverInfo = @{ name = "dispose-test-mcp"; version = "1" } } }
                    } elseif ($msg.method -eq "tools/call") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ content = @(@{ type = "text"; text = "DISPOSE_MARKER" }) } }
                    }
                }
                """);

            var definition = CreateBasicDefinition("test:dispose", "dispose-test", scriptPath, root, logPath);
            var runtime = new McpProxyRuntime();
            var remote = runtime.Register(definition);

            using var http = new HttpClient();
            using var initialize = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}
                """);
            Assert.True(initialize.RootElement.TryGetProperty("result", out _));

            var pid = int.Parse(Assert.Single(await File.ReadAllLinesAsync(logPath)), System.Globalization.CultureInfo.InvariantCulture);
            await runtime.DisposeAsync();

            await WaitForProcessExitAsync(pid);
            Assert.Throws<ObjectDisposedException>(() => runtime.Register(definition));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [SkippableFact]
    public async Task Proxy_RetiresRemovedUserRegistrationAndStopsProcess()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PowerShell fake MCP server is Windows-only.");

        var root = Path.Combine(Path.GetTempPath(), "lumi-mcp-proxy-retire-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "fake-mcp.ps1");
            var logPath = Path.Combine(root, "starts.log");
            await File.WriteAllTextAsync(scriptPath, """
                [System.IO.File]::AppendAllText($env:MCP_TEST_LOG, "$PID`n")
                function Write-Json($obj) {
                    [Console]::Out.WriteLine(($obj | ConvertTo-Json -Compress -Depth 30))
                    [Console]::Out.Flush()
                }
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    $msg = $line | ConvertFrom-Json
                    if ($msg.method -eq "initialize") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ protocolVersion = "2025-06-18"; capabilities = @{ tools = @{ listChanged = $false } }; serverInfo = @{ name = "retire-test-mcp"; version = "1" } } }
                    }
                }
                """);

            var serverId = Guid.NewGuid();
            await using var runtime = new McpProxyRuntime();
            var remote = runtime.Register(CreateBasicDefinition("lumi:" + serverId, "retire-test", scriptPath, root, logPath));

            using var http = new HttpClient();
            using var initialize = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}
                """);
            Assert.True(initialize.RootElement.TryGetProperty("result", out _));

            var pid = int.Parse(Assert.Single(await File.ReadAllLinesAsync(logPath)), System.Globalization.CultureInfo.InvariantCulture);
            runtime.RetireUserRegistrationsExcept([]);
            await WaitForProcessExitAsync(pid);

            using var response = await http.PostAsync(
                remote.Url,
                new StringContent("""
                    {"jsonrpc":"2.0","id":"after-retire","method":"initialize","params":{}}
                    """, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [SkippableFact]
    public async Task Proxy_DisposeCancelsInFlightRequestAndStopsProcess()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PowerShell fake MCP server is Windows-only.");

        var root = Path.Combine(Path.GetTempPath(), "lumi-mcp-proxy-dispose-inflight-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "fake-mcp.ps1");
            var logPath = Path.Combine(root, "starts.log");
            var callStartedPath = Path.Combine(root, "call-started.log");
            await File.WriteAllTextAsync(scriptPath, """
                [System.IO.File]::AppendAllText($env:MCP_TEST_LOG, "$PID`n")
                function Write-Json($obj) {
                    [Console]::Out.WriteLine(($obj | ConvertTo-Json -Compress -Depth 30))
                    [Console]::Out.Flush()
                }
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    $msg = $line | ConvertFrom-Json
                    if ($msg.method -eq "initialize") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ protocolVersion = "2025-06-18"; capabilities = @{ tools = @{ listChanged = $false } }; serverInfo = @{ name = "dispose-inflight-test-mcp"; version = "1" } } }
                    } elseif ($msg.method -eq "tools/call") {
                        [System.IO.File]::AppendAllText($env:MCP_CALL_STARTED, "$PID`n")
                        Start-Sleep -Seconds 30
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ content = @(@{ type = "text"; text = "SHOULD_NOT_COMPLETE" }) } }
                    }
                }
                """);

            var runtime = new McpProxyRuntime();
            var remote = runtime.Register(new McpProxyServerDefinition(
                "test:dispose-inflight",
                "dispose-inflight-test",
                new McpStdioServerConfig
                {
                    Command = GetPowerShellPath(),
                    Args = ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                    WorkingDirectory = root,
                    Env = new Dictionary<string, string>
                    {
                        ["MCP_TEST_LOG"] = logPath,
                        ["MCP_CALL_STARTED"] = callStartedPath
                    },
                    Tools = ["*"]
                }));

            using var http = new HttpClient();
            var inFlight = PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"slow","method":"tools/call","params":{"name":"emit_marker","arguments":{"value":"SLOW"}}}
                """);
            await WaitForFileAsync(callStartedPath);
            var pid = int.Parse(Assert.Single(await File.ReadAllLinesAsync(logPath)), System.Globalization.CultureInfo.InvariantCulture);

            var disposeTask = runtime.DisposeAsync().AsTask();
            var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(disposeTask, completed);
            await disposeTask;
            await WaitForProcessExitAsync(pid);

            try
            {
                using var ignored = await inFlight.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
            {
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [SkippableFact]
    public async Task Proxy_ReplacesChangedRegistrationAfterInFlightRequestCompletes()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PowerShell fake MCP server is Windows-only.");

        var root = Path.Combine(Path.GetTempPath(), "lumi-mcp-proxy-replace-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "fake-mcp.ps1");
            var logPath = Path.Combine(root, "starts.log");
            var callStartedPath = Path.Combine(root, "call-started.log");
            await File.WriteAllTextAsync(scriptPath, """
                [System.IO.File]::AppendAllText($env:MCP_TEST_LOG, "$($env:MCP_MARKER)|$PID`n")
                function Write-Json($obj) {
                    [Console]::Out.WriteLine(($obj | ConvertTo-Json -Compress -Depth 30))
                    [Console]::Out.Flush()
                }
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    $msg = $line | ConvertFrom-Json
                    if ($msg.method -eq "initialize") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ protocolVersion = "2025-06-18"; capabilities = @{ tools = @{ listChanged = $false } }; serverInfo = @{ name = "replace-test-mcp"; version = "1" } } }
                    } elseif ($msg.method -eq "tools/list") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ tools = @(@{ name = "emit_marker"; description = "Emit marker"; inputSchema = @{ type = "object"; properties = @{ value = @{ type = "string" } }; required = @("value") } }) } }
                    } elseif ($msg.method -eq "tools/call") {
                        $value = [string]$msg.params.arguments.value
                        [System.IO.File]::AppendAllText($env:MCP_CALL_STARTED, "$($env:MCP_MARKER)|$value`n")
                        if ($value -eq "SLOW") { Start-Sleep -Milliseconds 800 }
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ content = @(@{ type = "text"; text = "$($env:MCP_MARKER):$value" }) } }
                    }
                }
                """);

            await using var runtime = new McpProxyRuntime();
            var first = runtime.Register(CreateReplaceDefinition("test:replace", "replace-test", scriptPath, root, logPath, callStartedPath, "FIRST"));

            using var http = new HttpClient();
            using var initialize = await PostJsonAsync(http, first.Url, """
                {"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}
                """);
            Assert.True(initialize.RootElement.TryGetProperty("result", out _));

            var inFlightCall = PostJsonAsync(http, first.Url, """
                {"jsonrpc":"2.0","id":"slow","method":"tools/call","params":{"name":"emit_marker","arguments":{"value":"SLOW"}}}
                """);
            await WaitForFileAsync(callStartedPath);

            var second = runtime.Register(CreateReplaceDefinition("test:replace", "replace-test", scriptPath, root, logPath, callStartedPath, "SECOND"));
            Assert.Equal(first.Url, second.Url);

            using var firstCall = await inFlightCall;
            var firstText = firstCall.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
            Assert.Equal("FIRST:SLOW", firstText);

            using var secondInitialize = await PostJsonAsync(http, second.Url, """
                {"jsonrpc":"2.0","id":"second-init","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}
                """);
            Assert.True(secondInitialize.RootElement.TryGetProperty("result", out _));

            using var secondCall = await PostJsonAsync(http, second.Url, """
                {"jsonrpc":"2.0","id":"second-call","method":"tools/call","params":{"name":"emit_marker","arguments":{"value":"OK"}}}
                """);
            var secondText = secondCall.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
            Assert.Equal("SECOND:OK", secondText);

            var starts = await File.ReadAllLinesAsync(logPath);
            Assert.Contains(starts, line => line.StartsWith("FIRST|", StringComparison.Ordinal));
            Assert.Contains(starts, line => line.StartsWith("SECOND|", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    private static async Task<JsonDocument> PostJsonAsync(HttpClient http, string url, string json)
    {
        using var response = await http.PostAsync(
            url,
            new StringContent(json, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    private static McpProxyServerDefinition CreateReplaceDefinition(
        string key,
        string name,
        string scriptPath,
        string root,
        string logPath,
        string callStartedPath,
        string marker)
        => new(
            key,
            name,
            new McpStdioServerConfig
            {
                Command = GetPowerShellPath(),
                Args = ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                WorkingDirectory = root,
                Env = new Dictionary<string, string>
                {
                    ["MCP_TEST_LOG"] = logPath,
                    ["MCP_CALL_STARTED"] = callStartedPath,
                    ["MCP_MARKER"] = marker
                },
                Tools = ["*"]
            });

    private static McpProxyServerDefinition CreateBasicDefinition(
        string key,
        string name,
        string scriptPath,
        string root,
        string logPath)
        => new(
            key,
            name,
            new McpStdioServerConfig
            {
                Command = GetPowerShellPath(),
                Args = ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                WorkingDirectory = root,
                Env = new Dictionary<string, string>
                {
                    ["MCP_TEST_LOG"] = logPath
                },
                Tools = ["*"]
            });

    private sealed class CompositeJsonDocuments(JsonDocument[] documents) : IDisposable
    {
        public JsonDocument[] Documents { get; } = documents;

        public void Dispose()
        {
            foreach (var document in Documents)
                document.Dispose();
        }
    }

    private static async Task WaitForFileAsync(string path)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
                return;

            await Task.Delay(50);
        }

        Assert.Fail($"Timed out waiting for {path}.");
    }

    private static async Task WaitForProcessExitAsync(int processId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(processId);
                if (process.HasExited)
                    return;
            }
            catch (ArgumentException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Timed out waiting for process {processId} to exit.");
    }

    private static string GetPowerShellPath()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Assert.True(File.Exists(path), $"PowerShell not found at {path}");
        return path;
    }
}
