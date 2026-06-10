using System;
using System.Net;
using System.Text;
using GitHub.Copilot;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class McpStatusDiagnosticsTests
{
    [Theory]
    [InlineData(401, "Authentication is required")]
    [InlineData(404, "Endpoint was not found")]
    public async Task BuildMcpStatusErrorMessageAsync_ProbesHttpEndpoint(int statusCode, string expectedHint)
    {
        using var listener = new HttpListener();
        var url = $"http://127.0.0.1:{GetFreeLoopbackPort()}/mcp/";
        listener.Prefixes.Add(url);
        listener.Start();

        var serverTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var body = Encoding.UTF8.GetBytes("diagnostic response token=super-secret-token");
            context.Response.ContentType = "text/plain";
            context.Response.StatusCode = statusCode;
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        });

        var message = await ChatViewModel.BuildMcpStatusErrorMessageAsync(
            "remote-test",
            McpServerStatus.Failed,
            "Streamable HTTP error: Error POSTing to endpoint.",
            new McpHttpServerConfig { Url = url },
            CancellationToken.None);

        await serverTask;

        Assert.Contains($"POST {url} returned {statusCode}", message, StringComparison.Ordinal);
        Assert.Contains(expectedHint, message, StringComparison.Ordinal);
        Assert.Contains("diagnostic response", message, StringComparison.Ordinal);
        Assert.Contains("token=[redacted]", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("super-secret-token", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildMcpStatusErrorMessageAsync_ReportsNeedsAuthWithoutProbe()
    {
        var message = await ChatViewModel.BuildMcpStatusErrorMessageAsync(
            "icm",
            McpServerStatus.NeedsAuth,
            "",
            new McpHttpServerConfig { Url = "https://user:password@example.com/mcp?token=super-secret-token" },
            CancellationToken.None);

        Assert.Contains("needs sign-in", message, StringComparison.Ordinal);
        Assert.Contains("https://example.com/mcp", message, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-token", message, StringComparison.Ordinal);
        Assert.DoesNotContain("user:password", message, StringComparison.Ordinal);
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
