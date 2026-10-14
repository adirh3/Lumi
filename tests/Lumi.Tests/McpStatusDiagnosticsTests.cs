using System;
using System.Net;
using System.Text;
using GitHub.Copilot;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class McpStatusDiagnosticsTests
{
    [Theory]
    [InlineData(401, McpConnectorIssueKind.AccessDenied, "(401)")]
    [InlineData(404, McpConnectorIssueKind.NotFound, "(404)")]
    public async Task DescribeMcpStatusAsync_ProbesLoopbackEndpointAndKeepsTheFullError(
        int statusCode,
        McpConnectorIssueKind expectedKind,
        string expectedSummaryCode)
    {
        using var listener = new HttpListener();
        var url = $"http://127.0.0.1:{GetFreeLoopbackPort()}/mcp/";
        listener.Prefixes.Add(url);
        listener.Start();

        var longBody = new string('x', 512);
        var serverTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var body = Encoding.UTF8.GetBytes($"diagnostic response token=super-secret-token\n{longBody}\nFinal response line");
            context.Response.ContentType = "text/plain";
            context.Response.StatusCode = statusCode;
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        });

        var issue = await ChatViewModel.DescribeMcpStatusAsync(
            McpServerStatus.Failed,
            "Streamable HTTP error: Error POSTing to endpoint.",
            new McpHttpServerConfig { Url = url },
            definition: null,
            transport: null,
            CancellationToken.None);

        await serverTask;

        Assert.Equal(expectedKind, issue.Kind);
        Assert.True(issue.IsProblem);
        Assert.Contains(expectedSummaryCode, issue.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(url, issue.Summary, StringComparison.Ordinal);
        Assert.Contains("Streamable HTTP error: Error POSTing to endpoint.", issue.Details, StringComparison.Ordinal);
        Assert.Contains($"POST {url} returned {statusCode}", issue.Details, StringComparison.Ordinal);
        Assert.Contains(longBody + "\nFinal response line", issue.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-token", issue.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescribeMcpStatusAsync_NeedsSignInIsNeutralAndKeepsCredentialsOut()
    {
        var issue = await ChatViewModel.DescribeMcpStatusAsync(
            McpServerStatus.NeedsAuth,
            "",
            new McpHttpServerConfig { Url = "https://fixture-user:fixture-password@example.com/mcp?token=super-secret-token" },
            definition: null,
            transport: null,
            CancellationToken.None);

        Assert.Equal(McpConnectorIssueKind.NeedsSignIn, issue.Kind);
        Assert.False(issue.IsProblem);
        Assert.Contains("example.com/mcp", issue.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-token", issue.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-password", issue.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescribeMcpStatusAsync_DoesNotProbeAProxiedLocalConnector()
    {
        using var listener = new HttpListener();
        var url = $"http://127.0.0.1:{GetFreeLoopbackPort()}/mcp/route/";
        listener.Prefixes.Add(url);
        listener.Start();
        var probed = listener.GetContextAsync();

        var issue = await ChatViewModel.DescribeMcpStatusAsync(
            McpServerStatus.Failed,
            "failed to initialize MCP client: JSON-RPC error: -32000: Failed to start MCP server 'Tools'. Command 'uvx' was not found from working directory 'C:\\work'.",
            new McpHttpServerConfig { Url = url },
            new Lumi.Models.McpServer { Name = "Tools", ServerType = "local", Command = "uvx", Args = ["tools-server"] },
            transport: null,
            CancellationToken.None);

        Assert.Equal(McpConnectorIssueKind.CommandNotFound, issue.Kind);
        Assert.Contains("uvx", issue.Summary, StringComparison.Ordinal);
        Assert.Contains("Command: uvx tools-server", issue.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("Lumi's check", issue.Details, StringComparison.Ordinal);
        Assert.False(probed.IsCompleted);
    }

    [Fact]
    public void McpOAuthMessagesUseOriginalDisplayName()
    {
        var opened = ChatViewModel.BuildMcpOAuthOpenedMessage("Azure ARM MCP");
        var failed = ChatViewModel.BuildMcpOAuthFailureMessage(
            "Azure ARM MCP",
            "dynamic registration unavailable");

        Assert.Contains("'Azure ARM MCP'", opened, StringComparison.Ordinal);
        Assert.Contains("'Azure ARM MCP'", failed, StringComparison.Ordinal);
        Assert.DoesNotContain("Azure_ARM_MCP", opened, StringComparison.Ordinal);
        Assert.DoesNotContain("Azure_ARM_MCP", failed, StringComparison.Ordinal);
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
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
}
