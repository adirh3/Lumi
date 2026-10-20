using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Capabilities;
using Xunit;

namespace Lumi.Tests;

public sealed class McpConnectorIssuesTests
{
    private static readonly McpServerConfig Local = new McpStdioServerConfig { Command = "npx", Args = ["-y", "@example/server"] };
    private static readonly McpServerConfig Remote = new McpHttpServerConfig { Url = "https://example.test/mcp" };

    // Summaries are compared with the localized string they come from, so the cases hold in any UI language.
    public static TheoryData<string, bool, McpConnectorIssueKind, string, string?> Failures => new()
    {
        { "spawn npx ENOENT", false, McpConnectorIssueKind.CommandNotFound, nameof(Loc.Connector_Issue_CommandNotFound), "npx" },
        { "'node' is not recognized as an internal or external command,\noperable program or batch file.", false, McpConnectorIssueKind.CommandNotFound, nameof(Loc.Connector_Issue_CommandNotFound), "node" },
        { "/bin/sh: uvx: command not found", false, McpConnectorIssueKind.CommandNotFound, nameof(Loc.Connector_Issue_CommandNotFound), "uvx" },
        { "Error: Cannot find module 'C:\\srv\\index.js'\nMCP error -32000: Connection closed", false, McpConnectorIssueKind.ClosedOnStart, nameof(Loc.Connector_Issue_ClosedOnStart), null },
        { "MCP error -32000: Connection closed\nstderr: GET https://registry.example/pkg 404: Not Found", false, McpConnectorIssueKind.ClosedOnStart, nameof(Loc.Connector_Issue_ClosedOnStart), null },
        // A local command has no address: npm's own 404 or offline error is not the connector's.
        { "npm error 404 Not Found - GET https://registry.npmjs.org/example-server - Not found\nMCP error -32000: Connection closed", false, McpConnectorIssueKind.ClosedOnStart, nameof(Loc.Connector_Issue_ClosedOnStart), null },
        { "npm error getaddrinfo ENOTFOUND registry.npmjs.org\nMCP error -32000: Connection closed", false, McpConnectorIssueKind.ClosedOnStart, nameof(Loc.Connector_Issue_ClosedOnStart), null },
        { "MCP server initialization timed out after 30000ms", false, McpConnectorIssueKind.TimedOut, nameof(Loc.Connector_Issue_TimedOut), null },
        { "Streamable HTTP error: Error POSTing to endpoint (HTTP 401): Unauthorized", true, McpConnectorIssueKind.AccessDenied, nameof(Loc.Connector_Issue_AccessDenied), "401" },
        { "Error POSTing to endpoint (HTTP 503): upstream unavailable", true, McpConnectorIssueKind.HttpError, nameof(Loc.Connector_Issue_HttpError), "503" },
        { "fetch failed: connect ECONNREFUSED 127.0.0.1:9", true, McpConnectorIssueKind.Unreachable, nameof(Loc.Connector_Issue_Unreachable), null },
        { "failed to initialize MCP client: JSON-RPC error: -32000: The configured endpoint is incorrect.({\"expected\":\"/mcp\"})", true, McpConnectorIssueKind.Failed, nameof(Loc.Connector_Issue_ConnectFailedBecause), "The configured endpoint is incorrect." },
        { "", true, McpConnectorIssueKind.Failed, nameof(Loc.Connector_Issue_ConnectFailed), null },
        { "", false, McpConnectorIssueKind.Failed, nameof(Loc.Connector_Issue_StartFailed), null },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void Describe_ExplainsCommonFailuresInPlainWordsAndKeepsTheFullError(
        string rawError,
        bool remote,
        McpConnectorIssueKind expectedKind,
        string summaryKey,
        string? summaryArgument)
    {
        var issue = McpConnectorIssues.Describe(McpServerStatus.Failed, rawError, remote ? Remote : Local);

        Assert.Equal(expectedKind, issue.Kind);
        Assert.True(issue.IsProblem);
        Assert.Equal(summaryArgument is null ? Loc.Get(summaryKey) : Loc.Get(summaryKey, summaryArgument), issue.Summary);
        Assert.DoesNotContain('\n', issue.Summary);
        if (rawError.Length > 0)
            Assert.Contains(rawError.Replace("\r", ""), issue.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_UsesTheConnectorDefinitionWhenALocalConnectorIsProxiedOverHttp()
    {
        var proxied = new McpHttpServerConfig { Url = "http://127.0.0.1:5000/mcp/route/abc" };
        var definition = new McpServer { Name = "Tools", ServerType = "local", Command = "node", Args = ["server.js"] };

        var issue = McpConnectorIssues.Describe(McpServerStatus.Failed, "MCP error -32000: Connection closed", proxied, definition);

        Assert.Equal(McpConnectorIssueKind.ClosedOnStart, issue.Kind);
        Assert.Equal(Loc.Connector_Issue_ClosedOnStart, issue.Summary);
        Assert.Contains("Command: node server.js", issue.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1:5000", issue.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_PendingStatesAreNeutralButALocalSignInIsAProblem()
    {
        var remoteSignIn = McpConnectorIssues.Describe(McpServerStatus.NeedsAuth, "", Remote);
        var localSignIn = McpConnectorIssues.Describe(McpServerStatus.NeedsAuth, "", Local);
        var opened = McpConnectorIssues.SignInOpened(remoteSignIn, "Lumi opened your browser.");
        var windowOpened = McpConnectorIssues.SignInWindowOpened(remoteSignIn, "A Microsoft sign-in window opened.");
        var failed = McpConnectorIssues.SignInFailed(remoteSignIn, "Couldn't start sign-in.", new InvalidOperationException("no registration"));
        var starting = McpConnectorIssues.StillStarting("Tools", TimeSpan.FromSeconds(10), Local, null);
        var retrying = McpConnectorIssues.Retrying(McpConnectorIssues.Describe(McpServerStatus.Failed, "spawn npx ENOENT", Local));

        Assert.False(remoteSignIn.IsProblem);
        Assert.True(localSignIn.IsProblem);
        Assert.False(opened.IsProblem);
        Assert.False(windowOpened.IsProblem);
        Assert.Equal(Loc.Connector_Issue_SignInWindowOpened, windowOpened.Summary);
        Assert.Contains("A Microsoft sign-in window opened.", windowOpened.Details, StringComparison.Ordinal);
        Assert.True(failed.IsProblem);
        Assert.Contains("no registration", failed.Details, StringComparison.Ordinal);
        Assert.False(starting.IsProblem);
        Assert.False(retrying.IsProblem);
        Assert.Contains("spawn npx ENOENT", retrying.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_ExplainsConnectorsCopilotLoadsByTheTransportItReports()
    {
        // A plugin's or the user's own Copilot connector reaches Lumi without a config or definition.
        const string dropped = "MCP error -32000: Connection closed";

        Assert.False(McpConnectorIssues.Describe(McpServerStatus.NeedsAuth, "", config: null, transport: "http").IsProblem);
        Assert.False(McpConnectorIssues.Describe(McpServerStatus.NeedsAuth, "", config: null).IsProblem);
        Assert.True(McpConnectorIssues.Describe(McpServerStatus.NeedsAuth, "", config: null, transport: "stdio").IsProblem);
        Assert.Equal(
            Loc.Get(nameof(Loc.Connector_Issue_ConnectFailedBecause), "Connection closed."),
            McpConnectorIssues.Describe(McpServerStatus.Failed, dropped, config: null, transport: "http").Summary);
        Assert.Equal(
            McpConnectorIssueKind.ClosedOnStart,
            McpConnectorIssues.Describe(McpServerStatus.Failed, dropped, config: null, transport: "stdio").Kind);
        Assert.NotEqual(
            McpConnectorIssueKind.ClosedOnStart,
            McpConnectorIssues.Describe(McpServerStatus.Failed, dropped, config: null).Kind);
    }

    [Fact]
    public void Describe_RemovesCredentialsPassedAsArguments()
    {
        var definition = new McpServer
        {
            Name = "Docs",
            Command = "uvx",
            Args = ["docs-mcp", "--api-key", "3f9a1c7e5b2d4a6f8e0c1b3d5f7a9c2e", "--token=fixture-inline-argument-secret"],
        };
        var raw = "docs-mcp: error: unrecognized arguments: --api-key 3f9a1c7e5b2d4a6f8e0c1b3d5f7a9c2e --token=fixture-inline-argument-secret";

        var issue = McpConnectorIssues.Describe(McpServerStatus.Failed, raw, Local, definition);

        Assert.DoesNotContain("3f9a1c7e5b2d4a6f8e0c1b3d5f7a9c2e", issue.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-inline-argument-secret", issue.Details, StringComparison.Ordinal);
        Assert.Contains("unrecognized arguments: --api-key", issue.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_RemovesCredentialsButKeepsOrdinaryValues()
    {
        var definition = new McpServer
        {
            Name = "GitHub",
            Command = "npx",
            Args = ["-y", "@example/github", "--log-level", "debug"],
            Env = new() { ["GITHUB_TOKEN"] = "fixture-env-credential-value", ["LOG_LEVEL"] = "debug" },
        };
        var raw = "Startup failed using fixture-env-credential-value\n"
                  + "Authorization: Bearer fixture-bearer-credential\n"
                  + "token=fixture-pair-credential and https://example.test/mcp?api_key=fixture-url-credential\n"
                  + "log level debug";

        var issue = McpConnectorIssues.Describe(McpServerStatus.Failed, raw, Local, definition);

        foreach (var secret in new[] { "fixture-env-credential-value", "fixture-bearer-credential", "fixture-pair-credential", "fixture-url-credential" })
            Assert.DoesNotContain(secret, issue.Details, StringComparison.Ordinal);
        Assert.Contains("log level debug", issue.Details, StringComparison.Ordinal);
        Assert.Contains("--log-level debug", issue.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRepairReport_CarriesTheFullErrorAndSettingsWithoutCredentials()
    {
        var definition = new McpServer
        {
            Name = "Workspace Tools",
            Command = "node",
            Args = ["server.js", "--token", "fixture-argument-credential"],
            Env = new() { ["API_KEY"] = "fixture-env-credential", ["OPTIONAL_FLAG"] = "" },
        };
        var capability = new CapabilityDescriptor
        {
            Kind = CapabilityKind.McpServer,
            Name = definition.Name,
            Origin = CapabilityOrigin.Workspace,
            SourcePath = @"C:\repo\.vscode\mcp.json",
            McpDefinition = definition,
        };
        var details = "Line one\n```\nfenced output fixture-env-credential\n```\n" + new string('x', 3000) + "\nFinal line";

        var report = McpConnectorIssues.BuildRepairReport(
            definition.Name, "Couldn't start: it closed right after launching.", details, definition,
            new McpStdioServerConfig { Command = "node", WorkingDirectory = @"C:\repo" }, capability, @"C:\repo");

        Assert.Contains(new string('x', 3000) + "\nFinal line", report, StringComparison.Ordinal);
        Assert.Contains("````text", report, StringComparison.Ordinal);
        Assert.Contains(@"C:\repo\.vscode\mcp.json", report, StringComparison.Ordinal);
        Assert.Contains("API_KEY (set), OPTIONAL_FLAG (empty)", report, StringComparison.Ordinal);
        Assert.Contains("node server.js --token", report, StringComparison.Ordinal);
        Assert.Contains("KEY=[REDACTED]", report, StringComparison.Ordinal);
        Assert.Contains("plugin's installed files", report, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-argument-credential", report, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-env-credential", report, StringComparison.Ordinal);
        Assert.Equal("fixture-env-credential", definition.Env["API_KEY"]);
    }

    [Theory]
    [InlineData("GitHub", "GitHub")]
    [InlineData("a/b:c*d?", "a-b-c-d")]
    [InlineData("///", "connector")]
    public void ReportFileName_IsSafeOnEveryPlatform(string connector, string expectedSafeName)
        => Assert.Equal(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, Loc.Connector_RepairReportFileName, expectedSafeName),
            McpConnectorIssues.ReportFileName(connector));
}
