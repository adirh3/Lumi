using System.Collections.Generic;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.ViewModels;
using Xunit;
using RpcMcpServer = GitHub.Copilot.Rpc.McpServer;

// Lumi.csproj already suppresses GHCP001 for the SDK's experimental types; the tests exercise the same
// types, so suppress it here too rather than for the whole test assembly.
#pragma warning disable GHCP001

namespace Lumi.Tests;

/// <summary>
/// Covers the classification the first-turn MCP settle loop depends on. Remote servers connect and
/// authenticate asynchronously inside the Copilot runtime, so the first prompt must wait for them —
/// otherwise the model is handed none of their tools until the second turn.
/// </summary>
public sealed class McpSettleTests
{
    [Theory]
    [InlineData(true, true, "resume")]
    [InlineData(true, false, "resume")]
    [InlineData(false, true, "mcp")]
    [InlineData(false, false, null)]
    public void ResolveInitialSessionSetupStatus_DescribesResumeBeforeMcpConnection(
        bool hasPersistedSession,
        bool hasMcpServers,
        string? expected)
    {
        var status = ChatViewModel.ResolveInitialSessionSetupStatus(hasPersistedSession, hasMcpServers);

        Assert.Equal(
            expected switch
            {
                "resume" => Loc.Status_Resuming,
                "mcp" => Loc.Status_ConnectingMcp,
                _ => null
            },
            status);
    }

    [Theory]
    // NotConfigured is what a remote server reports before its transport is initialized. Treating it as
    // terminal is exactly what let the first prompt go out with no remote tools.
    [InlineData("not_configured")]
    [InlineData("pending")]
    public void IsMcpStatusSettling_TreatsUninitializedStatusesAsStillSettling(string status)
    {
        Assert.True(ChatViewModel.IsMcpStatusSettling(new McpServerStatus(status)));
    }

    [Theory]
    [InlineData("connected")]
    [InlineData("failed")]
    [InlineData("disabled")]
    // NeedsAuth is terminal for classification purposes: the loop must act on it (drive OAuth) rather
    // than merely keep polling.
    [InlineData("needs-auth")]
    public void IsMcpStatusSettling_TreatsResolvedStatusesAsSettled(string status)
    {
        Assert.False(ChatViewModel.IsMcpStatusSettling(new McpServerStatus(status)));
    }

    [Fact]
    public void HasRemoteMcpServers_TrueWhenAnyServerIsHttp()
    {
        var servers = new Dictionary<string, McpServerConfig>
        {
            ["local"] = new McpStdioServerConfig { Command = "node" },
            ["remote"] = new McpHttpServerConfig { Url = "https://mcp.management.azure.com" },
        };

        Assert.True(ChatViewModel.HasRemoteMcpServers(servers));
    }

    [Fact]
    public void HasRemoteMcpServers_FalseForStdioOnlySessions()
    {
        var servers = new Dictionary<string, McpServerConfig>
        {
            ["local"] = new McpStdioServerConfig { Command = "node" },
        };

        // stdio-only sessions must not pay any extra send latency.
        Assert.False(ChatViewModel.HasRemoteMcpServers(servers));
    }

    [Fact]
    public void HasRemoteMcpServers_FalseWhenNoServersConfigured()
    {
        Assert.False(ChatViewModel.HasRemoteMcpServers(null));
        Assert.False(ChatViewModel.HasRemoteMcpServers(new Dictionary<string, McpServerConfig>()));
    }

    private static ChatViewModel.McpSettleEvaluation Evaluate(
        IEnumerable<RpcMcpServer> servers,
        IReadOnlyDictionary<string, McpServerStatus>? handled = null,
        IReadOnlySet<string>? handedOff = null)
        => ChatViewModel.EvaluateMcpSettle(
            servers,
            handled ?? new Dictionary<string, McpServerStatus>(StringComparer.OrdinalIgnoreCase),
            handedOff ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static RpcMcpServer Server(string name, string status)
        => new() { Name = name, Status = new McpServerStatus(status) };

    [Fact]
    public void EvaluateMcpSettle_KeepsWaitingWhileRemoteServerIsUninitialized()
    {
        var evaluation = Evaluate([Server("azure-arm", "not_configured")]);

        Assert.True(evaluation.KeepWaiting);
        // Nothing to report yet — the server hasn't reached a status worth showing the user.
        Assert.Empty(evaluation.ToHandle);
    }

    [Fact]
    public void EvaluateMcpSettle_KeepsWaitingOnNeedsAuthSoCachedTokenReconnectIsNotMissed()
    {
        var evaluation = Evaluate([Server("azure-arm", "needs-auth")]);

        Assert.True(evaluation.KeepWaiting);
        Assert.Single(evaluation.ToHandle);
        Assert.Equal("azure-arm", evaluation.ToHandle[0].Name);
    }

    [Fact]
    public void EvaluateMcpSettle_StopsWaitingOnceEveryServerIsConnected()
    {
        var evaluation = Evaluate([Server("github-mcp-server", "connected"), Server("azure-arm", "connected")]);

        Assert.False(evaluation.KeepWaiting);
        Assert.Equal(2, evaluation.ToHandle.Count);
    }

    [Fact]
    public void EvaluateMcpSettle_StopsWaitingOnFailureRatherThanBurningTheBudget()
    {
        var evaluation = Evaluate([Server("azure-arm", "failed")]);

        Assert.False(evaluation.KeepWaiting);
        Assert.Single(evaluation.ToHandle);
    }

    [Fact]
    public void EvaluateMcpSettle_DoesNotRepostAChipForAnUnchangedStatus()
    {
        var handled = new Dictionary<string, McpServerStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["azure-arm"] = new McpServerStatus("needs-auth"),
        };

        var evaluation = Evaluate([Server("azure-arm", "needs-auth")], handled);

        Assert.Empty(evaluation.ToHandle);
        // Critically, suppressing the duplicate chip must not stop the loop waiting for the reconnect.
        Assert.True(evaluation.KeepWaiting);
    }

    [Fact]
    public void EvaluateMcpSettle_HandlesServerAgainWhenItsStatusChanges()
    {
        var handled = new Dictionary<string, McpServerStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["azure-arm"] = new McpServerStatus("needs-auth"),
        };

        var evaluation = Evaluate([Server("azure-arm", "connected")], handled);

        Assert.Single(evaluation.ToHandle);
        Assert.False(evaluation.KeepWaiting);
    }

    [Fact]
    public void EvaluateMcpSettle_StopsWaitingForServersPendingBrowserConsent()
    {
        var handedOff = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "azure-arm" };

        var evaluation = Evaluate([Server("azure-arm", "needs-auth")], handedOff: handedOff);

        // Only the user can finish an interactive sign-in, so the first prompt must not block on it.
        Assert.False(evaluation.KeepWaiting);
        Assert.Empty(evaluation.ToHandle);
    }

    [Fact]
    public void EvaluateMcpSettle_StopsWaitingWhenSignInCannotBeStarted()
    {
        // A server whose identity provider can't be signed into automatically is handed off exactly like
        // a browser-pending one. Without this the loop would burn the whole budget on every session.
        var handedOff = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "broken-idp" };

        var evaluation = Evaluate([Server("broken-idp", "needs-auth")], handedOff: handedOff);

        Assert.False(evaluation.KeepWaiting);
    }

    [Fact]
    public void EvaluateMcpSettle_KeepsWaitingForAHealthyServerWhileAnotherAwaitsConsent()
    {
        var handedOff = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "azure-arm" };

        var evaluation = Evaluate(
            [Server("azure-arm", "needs-auth"), Server("other", "not_configured")],
            handedOff: handedOff);

        Assert.True(evaluation.KeepWaiting);
    }

    [Fact]
    public void EvaluateMcpSettle_ReadsFinalStateWhenStatusesChangeFasterThanPolling()
    {
        // The runtime was observed raising needs-auth and connected a millisecond apart. Because the
        // decision is made from the server list rather than reconstructed from the event stream, the
        // intermediate status simply never appears and the final one is acted on.
        var evaluation = Evaluate([Server("azure-arm", "connected")]);

        Assert.False(evaluation.KeepWaiting);
        Assert.Equal(new McpServerStatus("connected"), evaluation.ToHandle[0].Status);
    }

    [Fact]
    public void EvaluateMcpSettle_MatchesServerNamesCaseInsensitively()
    {
        var handled = new Dictionary<string, McpServerStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["Azure-ARM"] = new McpServerStatus("connected"),
        };

        var evaluation = Evaluate([Server("azure-arm", "connected")], handled);

        Assert.Empty(evaluation.ToHandle);
    }

    [Fact]
    public void EvaluateMcpSettle_StopsWaitingWhenNoServersAreReported()
    {
        var evaluation = Evaluate([]);

        Assert.False(evaluation.KeepWaiting);
        Assert.Empty(evaluation.ToHandle);
    }
}
