using System.Collections.Concurrent;
using GitHub.Copilot;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class NativeMcpSessionRecoveryTests
{
    [Theory]
    [InlineData("MCP error -32001: Session not found")]
    [InlineData("Error calling tool: MCP error -32600: Session terminated")]
    [InlineData("mcp ERROR -32001: SESSION NOT FOUND \r\n")]
    public void Matcher_AcceptsOnlyKnownSessionLossSignatures(string message)
        => Assert.True(McpStdioServerConnection.IsRecoverableSessionLossMessage(message));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Session not found")]
    [InlineData("MCP error -32001: Request timed out")]
    [InlineData("MCP error -32000: Session not found")]
    [InlineData("MCP error -32600: Invalid request")]
    [InlineData("MCP error -32001: Session not found; another error")]
    public void Matcher_RejectsUnrelatedFailures(string? message)
        => Assert.False(McpStdioServerConnection.IsRecoverableSessionLossMessage(message));

    [Theory]
    [InlineData("stdio", "workspace:agency-mail", "Agency Mail", "workspace:agency-mail")]
    [InlineData("stdio", null, "Agency Mail", "Agency Mail")]
    [InlineData("stdio", "", "Agency Mail", "Agency Mail")]
    [InlineData("stdio", null, null, null)]
    [InlineData("http", "proxy-agency-mail", "Agency Mail", null)]
    [InlineData("sse", "remote-mail", "Remote Mail", null)]
    [InlineData(null, "unverified-mail", "Mail", null)]
    public void ServerIdentity_RequiresNativeStdioProvenance(
        string? transport, string? configuredName, string? displayName, string? expectedName)
    {
        var start = new ToolExecutionStartData
        {
            ToolCallId = "call",
            ToolName = "send_mail",
            McpTransport = transport is null ? null : new McpServerTransport(transport),
            McpConfigServerName = configuredName,
            McpServerName = displayName
        };

        Assert.Equal(expectedName, ChatViewModel.GetNativeMcpServerName(start));
    }

    [Theory]
    [InlineData("MCP error -32001: Session not found")]
    [InlineData("MCP error -32600: Session terminated")]
    public async Task FailedNativeCall_RestartsOnlyItsServerAndConsumesMapping(string message)
    {
        var calls = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        calls["failed"] = "workspace:agency-mail";
        calls["other"] = "workspace:agency-calendar";
        var restarts = new List<string>();
        var cooldown = new Dictionary<string, long>(StringComparer.Ordinal);
        var completion = FailedCall("failed", message);

        await Recover(completion, calls, cooldown, restarts);
        await Recover(completion, calls, cooldown, restarts);

        Assert.Equal(["workspace:agency-mail"], restarts);
        Assert.False(calls.ContainsKey("failed"));
        Assert.Equal("workspace:agency-calendar", calls["other"]);
    }

    [Theory]
    [InlineData(true, "MCP error -32001: Session not found")]
    [InlineData(false, "MCP error -32001: Request timed out")]
    [InlineData(false, null)]
    public async Task SuccessfulOrUnrelatedCall_DoesNotRestart(bool success, string? message)
    {
        var calls = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        calls["call"] = "agency";
        var completion = FailedCall("call", message);
        completion.Success = success;
        var restarts = new List<string>();

        await Recover(completion, calls, new(StringComparer.Ordinal), restarts);

        Assert.Empty(restarts);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task UntrackedCall_DoesNotRestartAnyServer()
    {
        var restarts = new List<string>();
        await Recover(FailedCall("http-or-local-tool"), new(StringComparer.Ordinal),
            new(StringComparer.Ordinal), restarts);

        Assert.Empty(restarts);
    }

    [Fact]
    public async Task Cooldown_IsPerServerAndAllowsAnotherAttemptAfterThirtySeconds()
    {
        var calls = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var cooldown = new Dictionary<string, long>(StringComparer.Ordinal);
        var restarts = new List<string>();
        foreach (var (id, server) in new[] { ("first", "mail"), ("second", "mail"), ("third", "calendar") })
        {
            calls[id] = server;
            await Recover(FailedCall(id), calls, cooldown, restarts);
        }
        Assert.Equal(["mail", "calendar"], restarts);

        cooldown["mail"] = Environment.TickCount64 - 30_001;
        calls["after-cooldown"] = "mail";
        await Recover(FailedCall("after-cooldown"), calls, cooldown, restarts);

        Assert.Equal(["mail", "calendar", "mail"], restarts);
    }

    [Fact]
    public async Task ConcurrentFailures_StartOnlyOneRestartForTheSameSessionServer()
    {
        var calls = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        calls["first"] = "mail";
        calls["second"] = "mail";
        var cooldown = new Dictionary<string, long>(StringComparer.Ordinal);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restartCount = 0;
        Task Restart(string server)
        {
            Assert.Equal("mail", server);
            Interlocked.Increment(ref restartCount);
            return finish.Task;
        }

        var first = ChatViewModel.RestartNativeMcpServerAfterSessionLossAsync(
            FailedCall("first"), calls, cooldown, Restart);
        var second = ChatViewModel.RestartNativeMcpServerAfterSessionLossAsync(
            FailedCall("second"), calls, cooldown, Restart);
        Assert.Equal(1, restartCount);
        Assert.False(first.IsCompleted);
        Assert.True(second.IsCompleted);
        finish.SetResult();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task SeparateSessions_DoNotShareRestartCooldown()
    {
        var restarts = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var calls = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            calls["call"] = "mail";
            await Recover(FailedCall("call"), calls, new(StringComparer.Ordinal), restarts);
        }

        Assert.Equal(["mail", "mail"], restarts);
    }

    [Fact]
    public async Task FailedRestart_DoesNotFaultTheEventSubscriberOrRetryTheOperation()
    {
        var calls = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        calls["call"] = "mail";
        calls["next-call"] = "mail";
        var cooldown = new Dictionary<string, long>(StringComparer.Ordinal);
        var attempts = 0;
        Task Fail(string server)
        {
            attempts++;
            throw new InvalidOperationException("Synthetic restart failure");
        }

        await ChatViewModel.RestartNativeMcpServerAfterSessionLossAsync(
            FailedCall("call"), calls, cooldown, Fail);
        await ChatViewModel.RestartNativeMcpServerAfterSessionLossAsync(
            FailedCall("next-call"), calls, cooldown, Fail);

        Assert.Equal(1, attempts);
        Assert.Empty(calls);
    }

    private static ToolExecutionCompleteData FailedCall(
        string id, string? message = "MCP error -32001: Session not found")
        => new()
        {
            ToolCallId = id,
            Success = false,
            Error = message is null ? null : new ToolExecutionCompleteError { Message = message }
        };

    private static Task Recover(
        ToolExecutionCompleteData completion,
        ConcurrentDictionary<string, string> calls,
        Dictionary<string, long> cooldown,
        List<string> restarts)
        => ChatViewModel.RestartNativeMcpServerAfterSessionLossAsync(
            completion, calls, cooldown, name =>
            {
                restarts.Add(name);
                return Task.CompletedTask;
            });
}
