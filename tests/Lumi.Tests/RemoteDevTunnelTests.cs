using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Remote;
using Xunit;

namespace Lumi.Tests;

public sealed class RemoteDevTunnelTests
{
    [Fact]
    public void NewTunnelArgumentsCannotGrantAccessOrReuseAnotherTunnel()
    {
        Assert.Equal(
            ["create", "--expiration", "1d", "--description", "Lumi private web app",
                "--host-header", "localhost", "--origin-header", "unchanged", "--json"],
            RemoteDevTunnelHost.CreateArguments);
        Assert.Equal(
            ["host", "owned-tunnel.uks1", "--host-header", "localhost", "--origin-header", "unchanged"],
            RemoteDevTunnelHost.HostArguments("owned-tunnel.uks1"));
        Assert.False(new UserSettings().RemoteUseDevTunnel);
    }

    [Fact]
    public void PortableCliUsesTheExecutableDirectoryNotSingleFileExtraction()
    {
        var executableDirectory = Path.Combine(Path.GetTempPath(), "Lumi-installed");
        var extractionDirectory = Path.Combine(Path.GetTempPath(), ".net", "Lumi", "extraction");
        var cliName = OperatingSystem.IsWindows() ? "devtunnel.exe" : "devtunnel";
        Assert.Equal(
            Path.Combine(executableDirectory, "tools", "devtunnel", cliName),
            RemoteDevTunnelCli.ResolvePortableCliPath(
                Path.Combine(executableDirectory, "Lumi.exe"), extractionDirectory));
        Assert.Equal(
            Path.Combine(extractionDirectory, "tools", "devtunnel", cliName),
            RemoteDevTunnelCli.ResolvePortableCliPath(null, extractionDirectory));
    }

    [Fact]
    public void OfficialFirstRunNoticeDoesNotBreakJsonOrWeakenAccessChecks()
    {
        const string banner = "Welcome to dev tunnels!\r\nCLI version: 1.0.2030\r\n\r\n" +
            "Use 'devtunnel --help' to see available commands or visit: https://aka.ms/devtunnels/docs\r\n\r\n";
        const string identity =
            """{"status":"Logged in","provider":"microsoft","username":"owner@example.com","objectId":"owner-id","tenantId":"tenant-id"}""";
        Assert.Equal("owner@example.com",
            RemoteDevTunnelHost.ParseMicrosoftIdentity(
                RemoteDevTunnelCli.NormalizeOutput(banner + identity)).Username);
        RemoteDevTunnelHost.RequireOwnerOnlyAccess(
            RemoteDevTunnelCli.NormalizeOutput(banner + """{"accessControlEntries":[]}"""));
        Assert.Throws<InvalidOperationException>(() => RemoteDevTunnelHost.RequireOwnerOnlyAccess(
            RemoteDevTunnelCli.NormalizeOutput(
                banner + """{"accessControlEntries":[{"type":"Anonymous","scopes":["connect"]}]}""")));
        Assert.Throws<InvalidOperationException>(() =>
            RemoteDevTunnelCli.NormalizeOutput("Welcome to dev tunnels!\n{\"accessControlEntries\":[]}"));
        Assert.ThrowsAny<JsonException>(() => RemoteDevTunnelHost.RequireOwnerOnlyAccess(
            RemoteDevTunnelCli.NormalizeOutput("Unexpected warning\n{\"accessControlEntries\":[]}")));
    }

    [Fact]
    public void OnlyAnAuthenticatedMicrosoftIdentityIsAccepted()
    {
        const string json =
            """{"status":"Logged in","provider":"microsoft","username":"owner@example.com","objectId":"owner-id","tenantId":"consumer-tenant"}""";
        var identity = RemoteDevTunnelHost.ParseMicrosoftIdentity(json);
        Assert.Equal("owner@example.com", identity.Username);
        Assert.Equal("owner-id", identity.ObjectId);
        Assert.Throws<InvalidOperationException>(() =>
            RemoteDevTunnelHost.ParseMicrosoftIdentity(json.Replace("microsoft", "github")));
        Assert.Throws<InvalidOperationException>(() =>
            RemoteDevTunnelHost.ParseMicrosoftIdentity("""{"status":"Not logged in"}"""));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"accessControlEntries":null}""")]
    [InlineData("""{"accessControlEntries":[{"type":"Anonymous","scopes":["connect"]}]}""")]
    [InlineData("""{"accessControlEntries":[{"type":"Organizations","scopes":["connect"]}]}""")]
    [InlineData("""{"accessControlEntries":[{"type":"Users","subjects":["someone-else"]}]}""")]
    public void MissingOrBroadenedAccessIsRejected(string json) =>
        Assert.Throws<InvalidOperationException>(() => RemoteDevTunnelHost.RequireOwnerOnlyAccess(json));

    [Fact]
    public void OnlyExplicitlyVerifiedEmptyAclIsAccepted()
    {
        RemoteDevTunnelHost.RequireOwnerOnlyAccess("""{"accessControlEntries":[]}""");
        Assert.ThrowsAny<JsonException>(() => RemoteDevTunnelHost.RequireOwnerOnlyAccess("not json"));
    }

    [Fact]
    public void WebOriginUsesOnlyTheHttpsPortUrlNotTheInspector()
    {
        const string output =
            "Hosting port 47654 at https://owned-tunnel.uks1.devtunnels.ms:47654/, " +
            "https://owned-tunnel-47654.uks1.devtunnels.ms/ and inspect it at " +
            "https://owned-tunnel-47654-inspect.uks1.devtunnels.ms/";
        Assert.Equal(
            "https://owned-tunnel-47654.uks1.devtunnels.ms",
            RemoteDevTunnelHost.FindWebOrigin(output, 47654));
        Assert.Null(RemoteDevTunnelHost.FindWebOrigin(output, 47655));
        Assert.Null(RemoteDevTunnelHost.FindWebOrigin(
            "http://owned-tunnel-47654.uks1.devtunnels.ms/", 47654));
        Assert.Null(RemoteDevTunnelHost.FindWebOrigin(
            "https://owned-tunnel-47654.uks1.devtunnels.ms.attacker.test/", 47654));
    }

    [Fact]
    public void TunnelMustBeReadyAndBrowserOriginMustMatchExactly()
    {
        const string origin = "https://owned-tunnel-47654.uks1.devtunnels.ms";
        Assert.True(RemoteDevTunnelHost.IsAllowedOrigin(null, origin));
        Assert.True(RemoteDevTunnelHost.IsAllowedOrigin(origin, origin));
        Assert.False(RemoteDevTunnelHost.IsAllowedOrigin(null, null));
        Assert.False(RemoteDevTunnelHost.IsAllowedOrigin(origin, null));
        Assert.False(RemoteDevTunnelHost.IsAllowedOrigin("null", origin));
        Assert.False(RemoteDevTunnelHost.IsAllowedOrigin("https://another-47654.uks1.devtunnels.ms", origin));
        Assert.False(RemoteDevTunnelHost.IsAllowedOrigin(origin + ".attacker.test", origin));
        Assert.False(RemoteDevTunnelHost.IsAllowedOrigin("http://localhost", origin));
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1", true)]
    [InlineData("::1", "::1", true)]
    [InlineData("127.0.0.1", "192.168.1.10", false)]
    [InlineData("192.168.1.20", "192.168.1.10", false)]
    [InlineData("100.64.0.20", "100.64.0.10", false)]
    [InlineData("203.0.113.20", "127.0.0.1", false)]
    public void TunnelModeNeverFallsBackToLanOrTailscale(string remote, string local, bool allowed)
    {
        var localAddress = IPAddress.Parse(local);
        Assert.Equal(allowed, LumiRemoteServer.IsAllowedCaller(
            new IPEndPoint(IPAddress.Parse(remote), 1234),
            new IPEndPoint(localAddress, 47654),
            allowInsecureLan: true,
            verifiedTailscaleAddresses: new HashSet<IPAddress> { localAddress },
            selectedLocalNetworkAddress: localAddress,
            useDevTunnel: true));
    }

    [Fact]
    public async Task TunnelListenerBindsOnlyLoopbackAndStillServesHttp()
    {
        using var listener = new RemoteHttpListener(
            (context, token) => context.WriteTextAsync("private-loopback", token));
        listener.Start(0, loopbackOnly: true);
        var socket = Assert.IsType<TcpListener>(
            typeof(RemoteHttpListener).GetField("_listener", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(listener));
        Assert.Equal(IPAddress.Loopback, Assert.IsType<IPEndPoint>(socket.LocalEndpoint).Address);

        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal("private-loopback",
            await http.GetStringAsync($"http://127.0.0.1:{listener.Port}/", timeout.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrivateTransportSurvivesSnapshotsAndUnrelatedSaves(bool hasChats)
    {
        var persisted = new AppData
        {
            Settings = new UserSettings { RemoteUseDevTunnel = true }
        };
        if (hasChats)
            persisted.Chats.Add(new Chat());
        var snapshot = AppDataSnapshotFactory.CreateIndexSnapshot(persisted);
        Assert.True(snapshot.Settings.RemoteUseDevTunnel);
        var merged = AppDataSnapshotFactory.MergeChatIndexChanges(
            new AppData(), snapshot, new HashSet<Guid>(), new HashSet<Guid>(), false);
        Assert.True(merged.Settings.RemoteUseDevTunnel);

        var store = new DataStore(new AppData());
        store.ApplyRemoteSecuritySnapshot(snapshot.Settings);
        Assert.True(store.Data.Settings.RemoteUseDevTunnel);
    }
}
