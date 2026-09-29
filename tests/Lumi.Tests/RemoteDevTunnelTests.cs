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
    public void NewTunnelArgumentsReservePrivateAccessForThirtyDays()
    {
        Assert.Equal(
            ["create", "--expiration", "30d", "--description", "Lumi private web app",
                "--host-header", "localhost", "--origin-header", "unchanged", "--json"],
            RemoteDevTunnelHost.CreateArguments);
        Assert.Equal(
            ["host", "owned-tunnel.uks1", "--host-header", "localhost", "--origin-header", "unchanged"],
            RemoteDevTunnelHost.HostArguments("owned-tunnel.uks1"));
        Assert.False(new UserSettings().RemoteUseDevTunnel);
    }

    private static readonly (string Username, string ObjectId, string TenantId) Owner =
        ("owner@example.com", "owner-id", "tenant-id");

    private static RemoteDevTunnelRegistration SavedTunnel(string id = "owned-tunnel.uks1") => new()
    {
        TunnelId = id,
        OwnerObjectId = Owner.ObjectId,
        OwnerTenantId = Owner.TenantId,
        Port = 49001
    };

    private static string SuccessfulCliResponse(string[] arguments) => arguments[0] switch
    {
        "create" => """{"tunnel":{"tunnelId":"owned-tunnel.uks1"}}""",
        "show" => JsonSerializer.Serialize(new
        {
            tunnel = new
            {
                tunnelId = arguments[1],
                ports = new[] { new { portNumber = 49001, protocol = "http" } }
            }
        }),
        "access" => """{"accessControlEntries":[]}""",
        "user" =>
            """{"status":"Logged in","provider":"microsoft","username":"owner@example.com","objectId":"owner-id","tenantId":"tenant-id"}""",
        "port" or "update" or "delete" => "{}",
        _ => throw new InvalidOperationException($"Unexpected CLI command: {arguments[0]}")
    };

    [Fact]
    public async Task SavedTunnelAndDeviceTokenSurviveNewHostAndSettingsRoundTrip()
    {
        var store = new DataStore(new AppData
        {
            Settings = new UserSettings
            {
                RemoteUseDevTunnel = true,
                RemotePairedDevices = [new RemotePairedDevice { DeviceId = "phone", Token = "same-token" }]
            }
        });
        var commands = new List<string>();
        Task<string> RunCli(string[] arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            commands.Add(string.Join(' ', arguments));
            return Task.FromResult(SuccessfulCliResponse(arguments));
        }

        await using (var firstHost = new RemoteDevTunnelHost(store))
        {
            var first = await firstHost.PrepareTunnelAsync(49001, Owner, RunCli, CancellationToken.None);
            Assert.Equal(SavedTunnel(), first.Registration);
            Assert.False(first.Replaced);
        }
        var json = JsonSerializer.Serialize(store.CreateIndexSnapshot(), AppDataJsonContext.Default.AppData);
        var restored = new DataStore(JsonSerializer.Deserialize(json, AppDataJsonContext.Default.AppData)!);
        commands.Clear();

        await using (var restartedHost = new RemoteDevTunnelHost(restored))
        {
            var restarted = await restartedHost.PrepareTunnelAsync(49001, Owner, RunCli, CancellationToken.None);
            Assert.Equal(SavedTunnel(), restarted.Registration);
            Assert.False(restarted.Replaced);
        }

        Assert.Equal(
            [
                "show owned-tunnel.uks1 --json",
                "access list owned-tunnel.uks1 --json",
                "access list owned-tunnel.uks1 -p 49001 --json",
                "user show --json",
                "update owned-tunnel.uks1 --expiration 30d --json"
            ], commands);
        Assert.Equal("same-token", Assert.Single(restored.SnapshotRemotePairedDevices()).Token);
    }

    [Theory]
    [InlineData("other-owner", "tenant-id", 49001)]
    [InlineData("owner-id", "other-tenant", 49001)]
    [InlineData("owner-id", "tenant-id", 49002)]
    public async Task DifferentAccountOrPortCannotReplaceASavedTunnel(string owner, string tenant, int port)
    {
        var store = new DataStore(new AppData { Settings = new UserSettings { RemoteDevTunnel = SavedTunnel() } });
        await using var host = new RemoteDevTunnelHost(store);
        var commands = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.PrepareTunnelAsync(port, ("owner@example.com", owner, tenant), (arguments, token) =>
            {
                commands++;
                return Task.FromResult(SuccessfulCliResponse(arguments));
            }, CancellationToken.None));

        Assert.Equal(0, commands);
        Assert.Equal(SavedTunnel(), store.SnapshotRemoteDevTunnel());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReuseRechecksTunnelAndPortAccessBeforeAnyUpdate(bool portAccess)
    {
        var store = new DataStore(new AppData { Settings = new UserSettings { RemoteDevTunnel = SavedTunnel() } });
        await using var host = new RemoteDevTunnelHost(store);
        var commands = new List<string>();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.PrepareTunnelAsync(49001, Owner, (arguments, token) =>
            {
                commands.Add(arguments[0]);
                return Task.FromResult(arguments[0] == "access" && arguments.Contains("-p") == portAccess
                    ? """{"accessControlEntries":[{"type":"Anonymous","scopes":["connect"]}]}"""
                    : SuccessfulCliResponse(arguments));
            }, CancellationToken.None));

        Assert.DoesNotContain("create", commands);
        Assert.DoesNotContain("update", commands);
        Assert.DoesNotContain("delete", commands);
        Assert.Equal(SavedTunnel(), store.SnapshotRemoteDevTunnel());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCanceledLookupDoesNotReplaceTheSavedLink(bool canceled)
    {
        var store = new DataStore(new AppData { Settings = new UserSettings { RemoteDevTunnel = SavedTunnel() } });
        await using var host = new RemoteDevTunnelHost(store);
        var commands = new List<string>();
        var preparation = host.PrepareTunnelAsync(49001, Owner, (arguments, token) =>
        {
            commands.Add(arguments[0]);
            return Task.FromException<string>(canceled
                ? new OperationCanceledException()
                : new HttpRequestException("Connection unavailable"));
        }, CancellationToken.None);
        if (canceled)
            await Assert.ThrowsAsync<OperationCanceledException>(() => preparation);
        else
            await Assert.ThrowsAsync<HttpRequestException>(() => preparation);

        Assert.Equal(["show"], commands);
        Assert.Equal(SavedTunnel(), store.SnapshotRemoteDevTunnel());
    }

    [Fact]
    public async Task ConfirmedMissingTunnelReplacesOnlyTheLinkNotThePairing()
    {
        var store = new DataStore(new AppData
        {
            Settings = new UserSettings
            {
                RemoteDevTunnel = SavedTunnel("expired-tunnel.uks1"),
                RemotePairedDevices = [new RemotePairedDevice { DeviceId = "phone", Token = "same-token" }]
            }
        });
        await using var host = new RemoteDevTunnelHost(store);
        var result = await host.PrepareTunnelAsync(49001, Owner, (arguments, token) =>
            arguments[0] == "show"
                ? Task.FromException<string>(new RemoteDevTunnelNotFoundException("Tunnel not found"))
                : Task.FromResult(SuccessfulCliResponse(arguments)), CancellationToken.None);

        Assert.True(result.Replaced);
        Assert.Equal(SavedTunnel(), result.Registration);
        Assert.Equal(SavedTunnel(), store.SnapshotRemoteDevTunnel());
        Assert.Equal("same-token", Assert.Single(store.SnapshotRemotePairedDevices()).Token);
    }

    [Fact]
    public async Task AccountSwitchDuringReuseDoesNotUpdateOrSaveTheTunnel()
    {
        var store = new DataStore(new AppData { Settings = new UserSettings { RemoteDevTunnel = SavedTunnel() } });
        await using var host = new RemoteDevTunnelHost(store);
        var saved = false;
        store.IndexSaved += () => saved = true;
        var commands = new List<string>();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.PrepareTunnelAsync(49001, Owner, (arguments, token) =>
            {
                commands.Add(arguments[0]);
                var result = SuccessfulCliResponse(arguments);
                return Task.FromResult(arguments[0] == "user" ? result.Replace("owner-id", "other-owner") : result);
            }, CancellationToken.None));

        Assert.False(saved);
        Assert.DoesNotContain("update", commands);
        Assert.Equal(SavedTunnel(), store.SnapshotRemoteDevTunnel());
    }

    [Fact]
    public async Task FailedFirstSetupCleansUpOnlyItsUnregisteredTunnel()
    {
        var store = new DataStore(new AppData());
        await using var host = new RemoteDevTunnelHost(store);
        var commands = new List<string[]>();
        await Assert.ThrowsAsync<IOException>(() =>
            host.PrepareTunnelAsync(49001, Owner, (arguments, token) =>
            {
                commands.Add(arguments);
                return arguments[0] == "port"
                    ? Task.FromException<string>(new IOException("Port setup failed"))
                    : Task.FromResult(SuccessfulCliResponse(arguments));
            }, CancellationToken.None));

        Assert.Equal(["create", "port", "delete"], commands.Select(arguments => arguments[0]).ToArray());
        Assert.Equal(["delete", "owned-tunnel.uks1"], commands[^1]);
        Assert.Null(store.SnapshotRemoteDevTunnel());
    }

    [SkippableFact]
    public async Task LivePrivateTunnelKeepsTheSameOriginAfterStoppingAndRestarting()
    {
        Skip.If(Environment.GetEnvironmentVariable("LUMI_DEVTUNNEL_INTEGRATION") != "1",
            "Opt-in: requires an installed, Microsoft-authenticated CLI; creates and deletes one private test tunnel.");
        var cli = await RemoteDevTunnelCli.EnsureAvailableAsync(
            _ => Task.FromResult(false), _ => { }, CancellationToken.None);
        Assert.NotNull(cli);
        _ = RemoteDevTunnelHost.ParseMicrosoftIdentity(
            await RemoteDevTunnelCli.RunAsync(cli, ["user", "show", "--json"], CancellationToken.None));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var store = new DataStore(new AppData
        {
            Settings = new UserSettings
            {
                RemoteUseDevTunnel = true,
                RemotePairedDevices = [new RemotePairedDevice { DeviceId = "test-phone", Token = "synthetic-token" }]
            }
        });
        try
        {
            string firstOrigin;
            await using (var first = new RemoteDevTunnelHost(store))
            {
                first.Start(port);
                firstOrigin = await WaitForOriginAsync(first);
            }
            var saved = Assert.IsType<RemoteDevTunnelRegistration>(store.SnapshotRemoteDevTunnel());
            RemoteDevTunnelHost.RequireSavedTunnelPort(
                await RemoteDevTunnelCli.RunAsync(cli, ["show", saved.TunnelId, "--json"], CancellationToken.None),
                saved);
            var json = JsonSerializer.Serialize(store.CreateIndexSnapshot(), AppDataJsonContext.Default.AppData);
            var restored = new DataStore(JsonSerializer.Deserialize(json, AppDataJsonContext.Default.AppData)!);

            await using (var restarted = new RemoteDevTunnelHost(restored))
            {
                var restoredPort = LumiRemoteServer.ResolveListenPort(restored.Data.Settings);
                Assert.Equal(port, restoredPort);
                restarted.Start(restoredPort);
                Assert.Equal(firstOrigin, await WaitForOriginAsync(restarted));
                Assert.Equal(saved, restored.SnapshotRemoteDevTunnel());
                Assert.Equal("synthetic-token", Assert.Single(restored.SnapshotRemotePairedDevices()).Token);

                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                using var response = await http.GetAsync(firstOrigin + "/app/");
                Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            }
            RemoteDevTunnelHost.RequireSavedTunnelPort(
                await RemoteDevTunnelCli.RunAsync(cli, ["show", saved.TunnelId, "--json"], CancellationToken.None),
                saved);
        }
        finally
        {
            if (store.SnapshotRemoteDevTunnel() is { } created)
                await RemoteDevTunnelCli.RunAsync(cli, ["delete", created.TunnelId], CancellationToken.None);
        }

        static async Task<string> WaitForOriginAsync(RemoteDevTunnelHost host)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
            while (DateTime.UtcNow < deadline)
            {
                var state = host.State;
                Assert.Null(state.Error);
                Assert.False(state.RequiresInstallConfirmation);
                if (state.Origin is { } origin)
                    return origin;
                await Task.Delay(100);
            }
            throw new TimeoutException("The private test tunnel did not become ready.");
        }
    }

    [Theory]
    [InlineData("""{"tunnel":{"tunnelId":"other.uks1","ports":[{"portNumber":49001,"protocol":"http"}]}}""")]
    [InlineData("""{"tunnel":{"tunnelId":"owned-tunnel.uks1","ports":[]}}""")]
    [InlineData("""{"tunnel":{"tunnelId":"owned-tunnel.uks1","ports":[{"portNumber":49002,"protocol":"http"}]}}""")]
    [InlineData("""{"tunnel":{"tunnelId":"owned-tunnel.uks1","ports":[{"portNumber":49001,"protocol":"http"},{"portNumber":22,"protocol":"ssh"}]}}""")]
    public void ChangedTunnelOrExtraPortsAreNotHosted(string json) =>
        Assert.Throws<InvalidOperationException>(() =>
            RemoteDevTunnelHost.RequireSavedTunnelPort(json, SavedTunnel()));

    [Fact]
    public void OnlyAnExplicitMissingTunnelResponseAllowsReplacement()
    {
        Assert.Throws<RemoteDevTunnelNotFoundException>(() => RemoteDevTunnelCli.ParseCommandResult(
            ["show", "owned-tunnel.uks1", "--json"], 2, "", "Tunnel not found in uks1: owned-tunnel"));
        Assert.Throws<InvalidOperationException>(() => RemoteDevTunnelCli.ParseCommandResult(
            ["show", "owned-tunnel.uks1", "--json"], 1, "", "Network unavailable"));
        Assert.Throws<InvalidOperationException>(() => RemoteDevTunnelCli.ParseCommandResult(
            ["show", "owned-tunnel.uks1", "--json"], 2, "", "Access denied"));
        Assert.Throws<InvalidOperationException>(() => RemoteDevTunnelCli.ParseCommandResult(
            ["access", "list", "owned-tunnel.uks1"], 2, "", "Tunnel not found in uks1: owned-tunnel"));
    }

    [Fact]
    public void PrivateLinkReusesItsSavedPortWithoutChangingOtherTransports()
    {
        var settings = new UserSettings { RemoteUseDevTunnel = true, RemoteDevTunnel = SavedTunnel() };
        Assert.Equal(49001, LumiRemoteServer.ResolveListenPort(settings));
        settings.RemoteAccessPort = 49002;
        Assert.Equal(49002, LumiRemoteServer.ResolveListenPort(settings));
        settings.RemoteAccessPort = 0;
        settings.RemoteUseDevTunnel = false;
        Assert.Equal(Lumi.Remote.Protocol.RemoteProtocol.DefaultPort, LumiRemoteServer.ResolveListenPort(settings));
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
