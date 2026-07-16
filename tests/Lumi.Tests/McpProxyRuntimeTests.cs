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
    [Theory]
    [InlineData(-32001, "Session not found", true)]
    [InlineData(-32001, " session NOT found ", true)]
    [InlineData(-32600, "Session terminated", true)]
    [InlineData(-32600, "session TERMINATED", true)]
    [InlineData(-32001, "Session terminated", false)]
    [InlineData(-32600, "Session not found", false)]
    [InlineData(-32000, "Session not found", false)]
    [InlineData(-32600, "Invalid Request", false)]
    public void SessionLossClassifierRecognizesKnownSdkSignatures(int code, string message, bool expected)
    {
        var response = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 1,
            error = new { code, message }
        });

        Assert.Equal(expected, McpStdioServerConnection.IsRecoverableSessionLossResponse(response));
    }

    [Theory]
    [InlineData(-32000, true)]
    [InlineData(-32001, true)]
    [InlineData(-32099, true)]
    [InlineData(-31999, false)]
    [InlineData(-32100, false)]
    [InlineData(-32600, false)]
    [InlineData(-32603, false)]
    public void InitializeRetryClassifierRecognizesImplementationDefinedServerErrors(int code, bool expected)
    {
        var response = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 1,
            error = new { code, message = "Initialization failed" }
        });

        Assert.Equal(expected, McpStdioServerConnection.IsRetryableInitializeErrorResponse(response));
    }

    [SkippableFact]
    public async Task Proxy_RetriesTransientServerErrorDuringInitialHandshake()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PowerShell fake MCP server is Windows-only.");

        var root = Path.Combine(Path.GetTempPath(), "lumi-mcp-proxy-initialize-retry-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "fake-mcp.ps1");
            var logPath = Path.Combine(root, "starts.log");
            var initializeCallsPath = Path.Combine(root, "initialize-calls.log");
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
                        [System.IO.File]::AppendAllText($env:MCP_INITIALIZE_CALLS, "$($msg.params.clientInfo.name)`n")
                        $callNumber = [System.IO.File]::ReadAllLines($env:MCP_INITIALIZE_CALLS).Length
                        if ($callNumber -eq 1) {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; error = @{ code = -32001; message = "Remote connection error: error sending request for url" } }
                        } else {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ protocolVersion = "2025-06-18"; capabilities = @{ tools = @{ listChanged = $false } }; serverInfo = @{ name = "initialize-retry-test-mcp"; version = "1" } } }
                        }
                    } elseif ($msg.method -eq "tools/list") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ tools = @(@{ name = "retry_tool"; description = "Retry tool"; inputSchema = @{ type = "object"; properties = @{} } }) } }
                    }
                }
                """);

            await using var runtime = new McpProxyRuntime();
            var remote = runtime.Register(new McpProxyServerDefinition(
                "test:initialize-retry",
                "initialize-retry-test",
                new McpStdioServerConfig
                {
                    Command = GetPowerShellPath(),
                    Args = ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                    WorkingDirectory = root,
                    Env = new Dictionary<string, string>
                    {
                        ["MCP_TEST_LOG"] = logPath,
                        ["MCP_INITIALIZE_CALLS"] = initializeCallsPath
                    },
                    Tools = ["*"]
                }));

            using var http = new HttpClient();
            using var initialize = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"custom-client","version":"1"}}}
                """);
            Assert.True(initialize.RootElement.TryGetProperty("result", out _));

            using var list = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"list","method":"tools/list","params":{}}
                """);
            Assert.Equal("retry_tool", list.RootElement.GetProperty("result").GetProperty("tools")[0].GetProperty("name").GetString());
            Assert.Single(await File.ReadAllLinesAsync(logPath));
            Assert.Equal(["custom-client", "custom-client"], await File.ReadAllLinesAsync(initializeCallsPath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [SkippableFact]
    public async Task Proxy_RetiresProcessWhenInitialHandshakeFails()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PowerShell fake MCP server is Windows-only.");

        var root = Path.Combine(Path.GetTempPath(), "lumi-mcp-proxy-initialized-notification-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "fake-mcp.ps1");
            var logPath = Path.Combine(root, "starts.log");
            await File.WriteAllTextAsync(scriptPath, """
                [System.IO.File]::AppendAllText($env:MCP_TEST_LOG, "$PID`n")
                $startNumber = [System.IO.File]::ReadAllLines($env:MCP_TEST_LOG).Length
                function Write-Json($obj) {
                    [Console]::Out.WriteLine(($obj | ConvertTo-Json -Compress -Depth 30))
                    [Console]::Out.Flush()
                }
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    $msg = $line | ConvertFrom-Json
                    if ($msg.method -eq "initialize") {
                        if ($startNumber -eq 1) {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; error = @{ code = -32000; message = "Persistent initialization failure" } }
                        } else {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ protocolVersion = "2025-06-18"; capabilities = @{ tools = @{ listChanged = $false } }; serverInfo = @{ name = "initialized-notification-test-mcp"; version = "1" } } }
                        }
                    }
                }
                """);

            await using var runtime = new McpProxyRuntime();
            var remote = runtime.Register(new McpProxyServerDefinition(
                "test:initialized-notification",
                "initialized-notification-test",
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

            using var http = new HttpClient();
            using var failed = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"failed","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}
                """);
            Assert.Equal(-32000, failed.RootElement.GetProperty("error").GetProperty("code").GetInt32());

            var failedProcessId = int.Parse(
                Assert.Single(await File.ReadAllLinesAsync(logPath)),
                System.Globalization.CultureInfo.InvariantCulture);
            await WaitForProcessExitAsync(failedProcessId);

            using var recovered = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"recovered","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}
                """);
            Assert.True(recovered.RootElement.TryGetProperty("result", out _));
            Assert.Equal(2, (await File.ReadAllLinesAsync(logPath)).Length);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

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
    public async Task Proxy_RestartsStdioServerWhenUpstreamSessionExpires()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PowerShell fake MCP server is Windows-only.");

        var root = Path.Combine(Path.GetTempPath(), "lumi-mcp-proxy-expired-session-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "fake-mcp.ps1");
            var logPath = Path.Combine(root, "starts.log");
            var expiredOncePath = Path.Combine(root, "expired-once.flag");
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
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ protocolVersion = "2025-06-18"; capabilities = @{ tools = @{ listChanged = $false } }; serverInfo = @{ name = "expired-session-test-mcp"; version = "1" } } }
                    } elseif ($msg.method -eq "tools/list") {
                        if (-not [System.IO.File]::Exists($env:MCP_EXPIRED_ONCE)) {
                            [System.IO.File]::WriteAllText($env:MCP_EXPIRED_ONCE, "expired")
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; error = @{ code = -32001; message = "Session not found" } }
                        } else {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ tools = @(@{ name = "recovered_tool"; description = "Recovered tool"; inputSchema = @{ type = "object"; properties = @{} } }) } }
                        }
                    }
                }
                """);

            await using var runtime = new McpProxyRuntime();
            var remote = runtime.Register(new McpProxyServerDefinition(
                "test:expired-session",
                "expired-session-test",
                new McpStdioServerConfig
                {
                    Command = GetPowerShellPath(),
                    Args = ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                    WorkingDirectory = root,
                    Env = new Dictionary<string, string>
                    {
                        ["MCP_TEST_LOG"] = logPath,
                        ["MCP_EXPIRED_ONCE"] = expiredOncePath
                    },
                    Tools = ["*"]
                }));

            using var http = new HttpClient();
            using var list = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"list","method":"tools/list","params":{}}
                """);

            Assert.Equal("list", list.RootElement.GetProperty("id").GetString());
            Assert.Equal("recovered_tool", list.RootElement.GetProperty("result").GetProperty("tools")[0].GetProperty("name").GetString());

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
    public async Task Proxy_RecoversExpiredSessionAfterReplayLimitIsReached()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PowerShell fake MCP server is Windows-only.");

        var root = Path.Combine(Path.GetTempPath(), "lumi-mcp-proxy-replay-limit-recovery-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "fake-mcp.ps1");
            var logPath = Path.Combine(root, "starts.log");
            await File.WriteAllTextAsync(scriptPath, """
                [System.IO.File]::AppendAllText($env:MCP_TEST_LOG, "$PID`n")
                $startNumber = [System.IO.File]::ReadAllLines($env:MCP_TEST_LOG).Length
                function Write-Json($obj) {
                    [Console]::Out.WriteLine(($obj | ConvertTo-Json -Compress -Depth 30))
                    [Console]::Out.Flush()
                }
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    $msg = $line | ConvertFrom-Json
                    if ($msg.method -eq "initialize") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ protocolVersion = "2025-06-18"; capabilities = @{ tools = @{ listChanged = $false } }; serverInfo = @{ name = "replay-limit-recovery-test-mcp"; version = "1" } } }
                    } elseif ($msg.method -eq "tools/list") {
                        if ($startNumber -le 2) {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; error = @{ code = -32001; message = "Session not found" } }
                        } else {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ tools = @(@{ name = "healthy_tool"; description = "Healthy tool"; inputSchema = @{ type = "object"; properties = @{} } }) } }
                        }
                    }
                }
                """);

            await using var runtime = new McpProxyRuntime();
            var remote = runtime.Register(new McpProxyServerDefinition(
                "test:replay-limit-recovery",
                "replay-limit-recovery-test",
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

            using var http = new HttpClient();
            using var exhausted = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"exhausted","method":"tools/list","params":{}}
                """);
            Assert.Equal("exhausted", exhausted.RootElement.GetProperty("id").GetString());
            Assert.Equal(-32001, exhausted.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal(3, (await File.ReadAllLinesAsync(logPath)).Length);

            using var healthy = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"healthy","method":"tools/list","params":{}}
                """);
            Assert.Equal("healthy_tool", healthy.RootElement.GetProperty("result").GetProperty("tools")[0].GetProperty("name").GetString());
            Assert.Equal(3, (await File.ReadAllLinesAsync(logPath)).Length);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [SkippableFact]
    public async Task Proxy_RetriesTransientServerErrorWhileReinitializingExpiredSession()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PowerShell fake MCP server is Windows-only.");

        var root = Path.Combine(Path.GetTempPath(), "lumi-mcp-proxy-recovery-initialize-retry-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "fake-mcp.ps1");
            var logPath = Path.Combine(root, "starts.log");
            var initializeCallsPath = Path.Combine(root, "initialize-calls.log");
            var expiredOncePath = Path.Combine(root, "expired-once.flag");
            await File.WriteAllTextAsync(scriptPath, """
                [System.IO.File]::AppendAllText($env:MCP_TEST_LOG, "$PID`n")
                $startNumber = [System.IO.File]::ReadAllLines($env:MCP_TEST_LOG).Length
                function Write-Json($obj) {
                    [Console]::Out.WriteLine(($obj | ConvertTo-Json -Compress -Depth 30))
                    [Console]::Out.Flush()
                }
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    $msg = $line | ConvertFrom-Json
                    if ($msg.method -eq "initialize") {
                        [System.IO.File]::AppendAllText($env:MCP_INITIALIZE_CALLS, "$startNumber`n")
                        $processInitializeCount = ([System.IO.File]::ReadAllLines($env:MCP_INITIALIZE_CALLS) | Where-Object { $_ -eq [string]$startNumber }).Count
                        if ($startNumber -eq 2 -and $processInitializeCount -eq 1) {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; error = @{ code = -32001; message = "Remote connection error: error sending request for url" } }
                        } else {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ protocolVersion = "2025-06-18"; capabilities = @{ tools = @{ listChanged = $false } }; serverInfo = @{ name = "recovery-initialize-retry-test-mcp"; version = "1" } } }
                        }
                    } elseif ($msg.method -eq "tools/list") {
                        if (-not [System.IO.File]::Exists($env:MCP_EXPIRED_ONCE)) {
                            [System.IO.File]::WriteAllText($env:MCP_EXPIRED_ONCE, "expired")
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; error = @{ code = -32001; message = "Session not found" } }
                        } else {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ tools = @(@{ name = "recovered_tool"; description = "Recovered tool"; inputSchema = @{ type = "object"; properties = @{} } }) } }
                        }
                    }
                }
                """);

            await using var runtime = new McpProxyRuntime();
            var remote = runtime.Register(new McpProxyServerDefinition(
                "test:recovery-initialize-retry",
                "recovery-initialize-retry-test",
                new McpStdioServerConfig
                {
                    Command = GetPowerShellPath(),
                    Args = ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                    WorkingDirectory = root,
                    Env = new Dictionary<string, string>
                    {
                        ["MCP_TEST_LOG"] = logPath,
                        ["MCP_INITIALIZE_CALLS"] = initializeCallsPath,
                        ["MCP_EXPIRED_ONCE"] = expiredOncePath
                    },
                    Tools = ["*"]
                }));

            using var http = new HttpClient();
            using var list = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"list","method":"tools/list","params":{}}
                """);

            Assert.Equal("recovered_tool", list.RootElement.GetProperty("result").GetProperty("tools")[0].GetProperty("name").GetString());
            Assert.Equal(2, (await File.ReadAllLinesAsync(logPath)).Length);
            Assert.Equal(["1", "2", "2"], await File.ReadAllLinesAsync(initializeCallsPath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [SkippableFact]
    public async Task Proxy_RetiresReplacementProcessWhenSessionRecoveryInitializationFails()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PowerShell fake MCP server is Windows-only.");

        var root = Path.Combine(Path.GetTempPath(), "lumi-mcp-proxy-failed-recovery-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "fake-mcp.ps1");
            var logPath = Path.Combine(root, "starts.log");
            var pendingSeenPath = Path.Combine(root, "pending-seen.flag");
            await File.WriteAllTextAsync(scriptPath, """
                [System.IO.File]::AppendAllText($env:MCP_TEST_LOG, "$PID`n")
                $startNumber = [System.IO.File]::ReadAllLines($env:MCP_TEST_LOG).Length
                function Write-Json($obj) {
                    [Console]::Out.WriteLine(($obj | ConvertTo-Json -Compress -Depth 30))
                    [Console]::Out.Flush()
                }
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    $msg = $line | ConvertFrom-Json
                    if ($msg.method -eq "initialize") {
                        if ($startNumber -eq 2) {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; error = @{ code = -32000; message = "Recovery initialization failed" } }
                        } elseif ($msg.params.clientInfo.name -ne "custom-client") {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; error = @{ code = -32000; message = "Original initialization parameters were lost" } }
                        } else {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ protocolVersion = "2025-06-18"; capabilities = @{ tools = @{ listChanged = $false } }; serverInfo = @{ name = "failed-recovery-test-mcp"; version = "1" } } }
                        }
                    } elseif ($msg.method -eq "tools/list") {
                        if ($startNumber -eq 1) {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; error = @{ code = -32001; message = "Session not found" } }
                        } else {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ tools = @(@{ name = "recovered_tool"; description = "Recovered tool"; inputSchema = @{ type = "object"; properties = @{} } }) } }
                        }
                    } elseif ($msg.method -eq "resources/list") {
                        if ($startNumber -eq 1) {
                            [System.IO.File]::WriteAllText($env:MCP_PENDING_SEEN, "seen")
                        } else {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ resources = @() } }
                        }
                    }
                }
                """);

            await using var runtime = new McpProxyRuntime();
            var remote = runtime.Register(new McpProxyServerDefinition(
                "test:failed-recovery",
                "failed-recovery-test",
                new McpStdioServerConfig
                {
                    Command = GetPowerShellPath(),
                    Args = ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                    WorkingDirectory = root,
                    Env = new Dictionary<string, string>
                    {
                        ["MCP_TEST_LOG"] = logPath,
                        ["MCP_PENDING_SEEN"] = pendingSeenPath
                    },
                    Tools = ["*"]
                }));

            using var http = new HttpClient();
            using var initialized = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{"sampling":{}},"clientInfo":{"name":"custom-client","version":"1"}}}
                """);
            Assert.True(initialized.RootElement.TryGetProperty("result", out _));

            var pendingCall = PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"pending","method":"resources/list","params":{}}
                """);
            await WaitForFileAsync(pendingSeenPath);
            var expiringCall = PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"failed","method":"tools/list","params":{}}
                """);

            using var failedResponses = new CompositeJsonDocuments(await Task.WhenAll(pendingCall, expiringCall));
            Assert.All(
                failedResponses.Documents,
                response => Assert.Equal(-32000, response.RootElement.GetProperty("error").GetProperty("code").GetInt32()));
            Assert.Equal(2, (await File.ReadAllLinesAsync(logPath)).Length);

            var failedRecoveryPid = int.Parse(
                (await File.ReadAllLinesAsync(logPath))[1],
                System.Globalization.CultureInfo.InvariantCulture);
            await WaitForProcessExitAsync(failedRecoveryPid);

            using var recovered = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"recovered","method":"tools/list","params":{}}
                """);
            Assert.Equal(
                "recovered_tool",
                recovered.RootElement.GetProperty("result").GetProperty("tools")[0].GetProperty("name").GetString());
            Assert.Equal(3, (await File.ReadAllLinesAsync(logPath)).Length);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [SkippableFact]
    public async Task Proxy_RetriesConcurrentIdempotentRequestsInterruptedByExpiredSessionRecovery()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PowerShell fake MCP server is Windows-only.");

        var root = Path.Combine(Path.GetTempPath(), "lumi-mcp-proxy-concurrent-recovery-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "fake-mcp.ps1");
            var logPath = Path.Combine(root, "starts.log");
            var callsPath = Path.Combine(root, "calls.log");
            var slowSeenPath = Path.Combine(root, "slow-seen.flag");
            var expiredOncePath = Path.Combine(root, "expired-once.flag");
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
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ protocolVersion = "2025-06-18"; capabilities = @{ tools = @{ listChanged = $false } }; serverInfo = @{ name = "concurrent-recovery-test-mcp"; version = "1" } } }
                    } elseif ($msg.method -eq "resources/list") {
                        [System.IO.File]::AppendAllText($env:MCP_CALLS_LOG, "resources/list`n")
                        if (-not [System.IO.File]::Exists($env:MCP_EXPIRED_ONCE)) {
                            [System.IO.File]::WriteAllText($env:MCP_SLOW_SEEN, "seen")
                        } else {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ resources = @() } }
                        }
                    } elseif ($msg.method -eq "tools/list") {
                        [System.IO.File]::AppendAllText($env:MCP_CALLS_LOG, "tools/list`n")
                        if (-not [System.IO.File]::Exists($env:MCP_EXPIRED_ONCE)) {
                            [System.IO.File]::WriteAllText($env:MCP_EXPIRED_ONCE, "expired")
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; error = @{ code = -32001; message = "Session not found" } }
                        } else {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ tools = @(@{ name = "recovered_tool"; description = "Recovered tool"; inputSchema = @{ type = "object"; properties = @{} } }) } }
                        }
                    }
                }
                """);

            await using var runtime = new McpProxyRuntime();
            var remote = runtime.Register(new McpProxyServerDefinition(
                "test:concurrent-recovery",
                "concurrent-recovery-test",
                new McpStdioServerConfig
                {
                    Command = GetPowerShellPath(),
                    Args = ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                    WorkingDirectory = root,
                    Env = new Dictionary<string, string>
                    {
                        ["MCP_TEST_LOG"] = logPath,
                        ["MCP_CALLS_LOG"] = callsPath,
                        ["MCP_SLOW_SEEN"] = slowSeenPath,
                        ["MCP_EXPIRED_ONCE"] = expiredOncePath
                    },
                    Tools = ["*"]
                }));

            using var http = new HttpClient();
            var slowCall = PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"slow","method":"resources/list","params":{}}
                """);
            await WaitForFileAsync(slowSeenPath);
            var expiringCall = PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"expire","method":"tools/list","params":{}}
                """);

            using var responses = new CompositeJsonDocuments(await Task.WhenAll(slowCall, expiringCall));
            Assert.Empty(responses.Documents[0].RootElement.GetProperty("result").GetProperty("resources").EnumerateArray());
            Assert.Equal(
                "recovered_tool",
                responses.Documents[1].RootElement.GetProperty("result").GetProperty("tools")[0].GetProperty("name").GetString());
            Assert.Equal(2, (await File.ReadAllLinesAsync(logPath)).Length);
            var calls = await File.ReadAllLinesAsync(callsPath);
            Assert.Equal(2, calls.Count(value => value == "resources/list"));
            Assert.Equal(2, calls.Count(value => value == "tools/list"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [SkippableFact]
    public async Task Proxy_DoesNotReplayToolCallInterruptedByExpiredSessionRecovery()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PowerShell fake MCP server is Windows-only.");

        var root = Path.Combine(Path.GetTempPath(), "lumi-mcp-proxy-unsafe-recovery-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "fake-mcp.ps1");
            var logPath = Path.Combine(root, "starts.log");
            var sideEffectsPath = Path.Combine(root, "side-effects.log");
            var toolSeenPath = Path.Combine(root, "tool-seen.flag");
            var expiredOncePath = Path.Combine(root, "expired-once.flag");
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
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ protocolVersion = "2025-06-18"; capabilities = @{ tools = @{ listChanged = $false } }; serverInfo = @{ name = "unsafe-recovery-test-mcp"; version = "1" } } }
                    } elseif ($msg.method -eq "tools/call") {
                        [System.IO.File]::AppendAllText($env:MCP_SIDE_EFFECTS_LOG, "performed`n")
                        [System.IO.File]::WriteAllText($env:MCP_TOOL_SEEN, "seen")
                        if ([System.IO.File]::Exists($env:MCP_EXPIRED_ONCE)) {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ content = @(@{ type = "text"; text = "REPLAYED" }) } }
                        }
                    } elseif ($msg.method -eq "tools/list") {
                        if (-not [System.IO.File]::Exists($env:MCP_EXPIRED_ONCE)) {
                            [System.IO.File]::WriteAllText($env:MCP_EXPIRED_ONCE, "expired")
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; error = @{ code = -32001; message = "Session not found" } }
                        } else {
                            Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ tools = @() } }
                        }
                    }
                }
                """);

            await using var runtime = new McpProxyRuntime();
            var remote = runtime.Register(new McpProxyServerDefinition(
                "test:unsafe-recovery",
                "unsafe-recovery-test",
                new McpStdioServerConfig
                {
                    Command = GetPowerShellPath(),
                    Args = ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                    WorkingDirectory = root,
                    Env = new Dictionary<string, string>
                    {
                        ["MCP_TEST_LOG"] = logPath,
                        ["MCP_SIDE_EFFECTS_LOG"] = sideEffectsPath,
                        ["MCP_TOOL_SEEN"] = toolSeenPath,
                        ["MCP_EXPIRED_ONCE"] = expiredOncePath
                    },
                    Tools = ["*"]
                }));

            using var http = new HttpClient();
            var toolCall = PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"tool","method":"tools/call","params":{"name":"perform_side_effect","arguments":{}}}
                """);
            await WaitForFileAsync(toolSeenPath);
            var expiringCall = PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"expire","method":"tools/list","params":{}}
                """);

            using var responses = new CompositeJsonDocuments(await Task.WhenAll(toolCall, expiringCall));
            var toolError = responses.Documents[0].RootElement.GetProperty("error");
            Assert.Equal(-32000, toolError.GetProperty("code").GetInt32());
            Assert.Contains("outcome is unknown", toolError.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Empty(responses.Documents[1].RootElement.GetProperty("result").GetProperty("tools").EnumerateArray());
            Assert.Equal(2, (await File.ReadAllLinesAsync(logPath)).Length);
            Assert.Single(await File.ReadAllLinesAsync(sideEffectsPath));
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
