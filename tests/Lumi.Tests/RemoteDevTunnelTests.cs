using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Remote;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class RemoteDevTunnelTests
{
    [Fact]
    public void NewTunnelArgumentsCannotGrantAccessOrReuseAnotherTunnel()
    {
        const string requestedTunnelId = "lumi-0123456789abcdef0123456789abcdef";
        Assert.Equal(
            ["create", requestedTunnelId, "--expiration", "30d", "--description",
                RemoteDevTunnelHost.TunnelDescription,
                "--host-header", "localhost", "--origin-header", "unchanged", "--request-timeout", "0", "--json"],
            RemoteDevTunnelHost.CreateArguments(requestedTunnelId));
        Assert.Equal(
            ["host", "owned-tunnel.uks1", "--host-header", "localhost", "--origin-header", "unchanged"],
            RemoteDevTunnelHost.HostArguments("owned-tunnel.uks1"));
        Assert.False(new UserSettings().RemoteUseDevTunnel);
    }

    [Fact]
    public void ClusterQualifiedTunnelRoutePinsRecreationAndRejectsRelocation()
    {
        const string baseId = "lumi-0123456789abcdef0123456789abcdef";
        const string routedId = baseId + ".uks1";
        Assert.Equal(
            ["create", baseId, "--service-uri", "https://uks1.rel.tunnels.api.visualstudio.com",
                "--expiration", "30d", "--description", RemoteDevTunnelHost.TunnelDescription,
                "--host-header", "localhost", "--origin-header", "unchanged", "--request-timeout", "0", "--json"],
            RemoteDevTunnelHost.CreateArguments(routedId));
        Assert.Equal(routedId, RemoteDevTunnelHost.RequireExpectedTunnelId(routedId, routedId));
        Assert.Throws<InvalidOperationException>(() =>
            RemoteDevTunnelHost.RequireExpectedTunnelId(baseId + ".euw", routedId));
        Assert.Throws<InvalidOperationException>(() =>
            RemoteDevTunnelHost.CreateArguments(routedId + ".invalid"));
        Assert.Equal(
            "https://lumi-0123456789abcdef0123456789abcdef-47654.uks1.devtunnels.ms",
            RemoteDevTunnelHost.FindWebOrigin(
                "Hosting port 47654 at https://lumi-0123456789abcdef0123456789abcdef-47654.uks1.devtunnels.ms/",
                47654));
    }

    [Fact]
    public async Task RejectedRelocatedTunnelIsDeletedWithoutBeingUsed()
    {
        const string baseId = "lumi-0123456789abcdef0123456789abcdef";
        const string requestedRoute = baseId + ".uks1";
        const string returnedRoute = baseId + ".euw";
        var calls = new List<string[]>();
        Task<string> Run(string[] arguments, CancellationToken token)
        {
            calls.Add(arguments);
            return Task.FromResult(arguments[0] == "create"
                ? JsonSerializer.Serialize(new { tunnel = new { tunnelId = returnedRoute } })
                : "{}");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RemoteDevTunnelHost.CreateProfileTunnelAsync(requestedRoute, Run, CancellationToken.None));

        Assert.Equal(2, calls.Count);
        Assert.Equal(RemoteDevTunnelHost.CreateArguments(requestedRoute), calls[0]);
        Assert.Equal(["delete", returnedRoute], calls[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidProfileTunnelSurvivesStoppingAndCancellation(bool cancel)
    {
        const string route = "lumi-0123456789abcdef0123456789abcdef.uks1";
        using var cancellation = new CancellationTokenSource();
        var steps = new List<string>();
        Task<string> Run(string[] arguments, CancellationToken token)
        {
            steps.Add(arguments[0]);
            return Task.FromResult(arguments[0] switch
            {
                "list" => """{"tunnels":[]}""",
                "create" => JsonSerializer.Serialize(new { tunnel = new { tunnelId = route } }),
                "access" => """{"accessControlEntries":[]}""",
                "port" when arguments[1] == "list" => """{"ports":[]}""",
                _ => "{}"
            });
        }
        Task Use(string tunnelId, CancellationToken token)
        {
            steps.Add("persist");
            Assert.Equal(route, tunnelId);
            Assert.Equal(cancellation.Token, token);
            if (cancel)
                cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        var operation = RemoteDevTunnelHost.PrepareProfileTunnelAsync(
            route, 47654, Run, Use, cancellation.Token);
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        else
            await operation;

        Assert.Contains("create", steps);
        Assert.Contains("persist", steps);
        Assert.DoesNotContain("delete", steps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProfilePreparationReusesTheRouteAndVerifiesBothAccessPolicies(bool exists)
    {
        const string route = "lumi-0123456789abcdef0123456789abcdef.uks1";
        var calls = new List<string[]>();
        var persisted = new List<string>();
        Task<string> Run(string[] args, CancellationToken token)
        {
            calls.Add(args);
            return Task.FromResult(args[0] switch
            {
                "list" => exists
                    ? $$"""{"tunnels":[{"tunnelId":"{{route}}","description":"Lumi private web app"}]}"""
                    : """{"tunnels":[]}""",
                "create" => JsonSerializer.Serialize(new { tunnel = new { tunnelId = route } }),
                "access" => """{"accessControlEntries":[]}""",
                "port" when args[1] == "list" => exists
                    ? """{"ports":[{"portNumber":47654,"protocol":"http"}]}"""
                    : """{"ports":[]}""",
                _ => "{}"
            });
        }
        var actual = await RemoteDevTunnelHost.PrepareProfileTunnelAsync(
            route, 47654, Run,
            (id, _) => { persisted.Add(id); return Task.CompletedTask; },
            CancellationToken.None);
        Assert.Equal(route, actual);
        Assert.Equal([route], persisted);
        Assert.DoesNotContain(calls, args => args[0] == "delete");
        Assert.Equal(exists ? 0 : 1, calls.Count(args => args[0] == "create"));
        Assert.Contains(calls, args =>
            args[0] == "port" && args[1] == (exists ? "update" : "create")
            && args.Contains("--request-timeout") && args.Contains("0"));
        Assert.Contains(calls, args => args.SequenceEqual(RemoteDevTunnelHost.RefreshArguments(route)));
        Assert.True(calls.Count(args => args[0] == "access" && args.Contains("-p")) >= 1);
        Assert.True(calls.Count(args => args[0] == "access" && !args.Contains("-p")) >= 1);
        Assert.DoesNotContain(calls.SelectMany(args => args), arg =>
            arg is "--allow-anonymous" or "--access-token");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingTunnelOrPortGrantsCannotBeRepairedIntoAHostedTunnel(bool portGrant)
    {
        const string route = "lumi-0123456789abcdef0123456789abcdef.uks1";
        var mutatedPort = false;
        Task<string> Run(string[] args, CancellationToken token)
        {
            if (args[0] == "access")
                return Task.FromResult(args.Contains("-p") == portGrant
                    ? """{"accessControlEntries":[{"type":"Anonymous","scopes":["connect"]}]}"""
                    : """{"accessControlEntries":[]}""");
            if (args[0] == "port" && args[1] != "list")
                mutatedPort = true;
            return Task.FromResult(args[0] == "list"
                ? $$"""{"tunnels":[{"tunnelId":"{{route}}","description":"Lumi private web app"}]}"""
                : """{"ports":[{"portNumber":47654,"protocol":"http"}]}""");
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RemoteDevTunnelHost.PrepareProfileTunnelAsync(
                route, 47654, Run, (_, _) => Task.CompletedTask, CancellationToken.None));
        Assert.False(mutatedPort);
    }

    [Theory]
    [InlineData("""{"ports":[{"portNumber":1234,"protocol":"http"}]}""")]
    [InlineData("""{"ports":[{"portNumber":47654,"protocol":"https"}]}""")]
    [InlineData("""{"ports":[{"portNumber":47654,"protocol":"http"},{"portNumber":1234,"protocol":"http"}]}""")]
    [InlineData("{}")]
    public void UnexpectedPortsNeverReachTheHost(string json) =>
        Assert.Throws<InvalidOperationException>(() => RemoteDevTunnelHost.HasExpectedPort(json, 47654));

    [Fact]
    public void OfficialEmptyListWarningsAreRecognizedOnlyForTheirExactCommandAndRoute()
    {
        const string route = "lumi-0123456789abcdef0123456789abcdef.uks1";
        Assert.Null(RemoteDevTunnelHost.FindExistingProfileTunnelId(
            """{"warning":"No tunnels found."}""", route));
        Assert.False(RemoteDevTunnelHost.HasExpectedPort(
            """{"warning":"No ports found for tunnel lumi-0123456789abcdef0123456789abcdef."}""",
            47654, route));
        Assert.Throws<InvalidOperationException>(() => RemoteDevTunnelHost.FindExistingProfileTunnelId(
            """{"warning":"Authentication unavailable."}""", route));
        Assert.Throws<InvalidOperationException>(() => RemoteDevTunnelHost.HasExpectedPort(
            """{"warning":"No ports found for tunnel somebody-else."}""", 47654, route));
        Assert.Throws<InvalidOperationException>(() => RemoteDevTunnelHost.FindExistingProfileTunnelId(
            """{"warning":"No tunnels found.","error":"Permission denied."}""", route));
        Assert.Throws<InvalidOperationException>(() => RemoteDevTunnelHost.RequireOwnerOnlyAccess(
            """{"warning":"No access grants."}"""));
    }

    [Fact]
    public async Task TransientFailuresReconnectWithBoundedBackoff()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        await RemoteDevTunnelHost.RunWithRecoveryAsync(
            _ => ++attempts < 8
                ? Task.FromException(new RemoteDevTunnelCliException("Relay unavailable."))
                : Task.CompletedTask,
            (_, delay) => delays.Add(delay), CancellationToken.None,
            (_, _) => Task.CompletedTask);
        Assert.Equal(8, attempts);
        Assert.Equal([5, 10, 20, 40, 60, 60, 60], delays.Select(delay => (int)delay.TotalSeconds));
    }

    [Fact]
    public async Task CancelingReconnectCannotStartAnotherHost()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RemoteDevTunnelHost.RunWithRecoveryAsync(
                _ => { attempts++; throw new IOException("Disconnected."); },
                (_, _) => { }, cancellation.Token,
                (_, _) => { cancellation.Cancel(); return Task.CompletedTask; }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task PolicyAndAuthenticationFailuresNeverAutomaticallySignInOrRetry()
    {
        Task Delay(TimeSpan delay, CancellationToken token) =>
            throw new InvalidOperationException("A fatal failure must not retry.");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RemoteDevTunnelHost.RunWithRecoveryAsync(
                _ => throw new InvalidOperationException("Owner access could not be verified."),
                (_, _) => { }, CancellationToken.None, Delay));
        await Assert.ThrowsAsync<RemoteDevTunnelCliException>(() =>
            RemoteDevTunnelHost.RunWithRecoveryAsync(
                _ => throw new RemoteDevTunnelCliException("Sign in again.", requiresSignIn: true),
                (_, _) => { }, CancellationToken.None, Delay));
        await Assert.ThrowsAsync<RemoteDevTunnelCliException>(() =>
            RemoteDevTunnelHost.RunWithRecoveryAsync(
                _ => throw new RemoteDevTunnelCliException("Unsupported CLI option.", canRetry: false),
                (_, _) => { }, CancellationToken.None, Delay));
    }

    [Fact]
    public async Task StartingAReplacementPublishesOnlyStartingAndCancelsItsPredecessor()
    {
        var host = new RemoteDevTunnelHost();
        var states = new ConcurrentQueue<bool>();
        var firstStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.StateChanged += () => states.Enqueue(host.State.IsStarting);
        try
        {
            host.Start(lifetime =>
            {
                firstStarted.SetResult(lifetime.Token);
                return Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
            });
            var first = await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal([true], states.ToArray());
            states.Clear();

            host.Start(lifetime =>
            {
                secondStarted.SetResult(lifetime.Token);
                return Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
            });
            var second = await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(first.IsCancellationRequested);
            Assert.Equal([true], states.ToArray());
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(second.IsCancellationRequested);
        }
        finally
        {
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ConcurrentReconnectsCannotOrphanAWorkerOrBlockShutdown()
    {
        var host = new RemoteDevTunnelHost();
        using var overlappingStops = new Barrier(2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shuttingDown = false;
        var active = 0;
        host.StateChanged += () =>
        {
            if (!Volatile.Read(ref shuttingDown) && !host.State.IsStarting)
                Assert.True(overlappingStops.SignalAndWait(TimeSpan.FromSeconds(5)));
        };
        async Task Run(CancellationTokenSource lifetime)
        {
            Interlocked.Increment(ref active);
            started.TrySetResult();
            try
            {
                await release.Task.WaitAsync(lifetime.Token);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
        Task? shutdown = null;
        try
        {
            await Task.WhenAll(Task.Run(() => host.Start(Run)), Task.Run(() => host.Start(Run)))
                .WaitAsync(TimeSpan.FromSeconds(5));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref shuttingDown, true);
            shutdown = host.DisposeAsync().AsTask();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, Volatile.Read(ref active));
        }
        finally
        {
            Volatile.Write(ref shuttingDown, true);
            release.TrySetResult();
            await (shutdown ?? host.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task HostingUsesTheCliReportedUrlAndStopsItsOwnedProcess()
    {
        const string origin = "https://bright-river-47654.uks1.devtunnels.ms";
        var host = new RemoteDevTunnelHost();
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.StateChanged += () =>
        {
            if (host.State.Origin is { } value)
                ready.TrySetResult(value);
        };
        try
        {
            host.Start(lifetime => host.HostAsync(
                HostedProcessFixture(origin, exitAfterReady: false),
                47654, "Fixture owner", lifetime, lifetime.Token));
            Assert.Equal(origin, await ready.Task.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.False(host.State.IsStarting);
            Assert.Equal("Fixture owner", host.State.Account);
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(host.State.Origin);
        }
        finally
        {
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task UnexpectedHostingProcessExitReconnectsThroughTheProductionWorker()
    {
        const string origin = "https://bright-river-47654.uks1.devtunnels.ms";
        var host = new RemoteDevTunnelHost();
        var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        var attempts = 0;
        var retryCount = 0;
        host.StateChanged += () =>
        {
            if (host.State.Origin is not null && Interlocked.Increment(ref readyCount) == 2)
                secondReady.TrySetResult();
        };
        try
        {
            host.Start(lifetime => RemoteDevTunnelHost.RunWithRecoveryAsync(
                token => host.HostAsync(
                    HostedProcessFixture(origin, exitAfterReady: Interlocked.Increment(ref attempts) == 1),
                    47654, "Fixture owner", lifetime, token),
                (_, _) => Interlocked.Increment(ref retryCount),
                lifetime.Token,
                (_, _) => Task.CompletedTask));
            await secondReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(2, attempts);
            Assert.Equal(1, retryCount);
            Assert.Equal(origin, host.State.Origin);
        }
        finally
        {
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData("devtunnel: Error connecting host tunnel session: Unauthorized (401).", true)]
    [InlineData("Error connecting host tunnel session: Network unavailable.", true)]
    [InlineData("HostSSH: Error running client SSH session: Connection lost.", false)]
    [InlineData("ClientSSH: Error running client SSH session: Error connecting host tunnel session: unrelated.", false)]
    [InlineData("Connection to host tunnel relay closed. Reconnecting.", false)]
    public void OnlyTerminalHostErrorsInterruptTheHostingProcess(string line, bool expected) =>
        Assert.Equal(expected, RemoteDevTunnelHost.IsTerminalHostFailure(line));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthenticationFailureReacquiresCredentialsOnlyForAPreviouslyReadyHost(bool wasReady)
    {
        var failure = RemoteDevTunnelHost.CreateHostingFailure(
            "Unauthorized (401). Provide a fresh tunnel access token with 'host' scope.", wasReady);
        Assert.Equal(!wasReady, failure.RequiresSignIn);
        Assert.Equal(wasReady, failure.CanRetry);
    }

    [Theory]
    [InlineData("Connection to host tunnel relay closed. Another host for the tunnel has connected.", true)]
    [InlineData("devtunnel: Error connecting host tunnel session: Cannot retry connection because another host for this tunnel has connected. Only one host connection at a time is supported.", true)]
    [InlineData("devtunnel: Error connecting host tunnel session: Cannot retry connection because another host for this tunnel has connected. Only one host connection at a time is supported.", false)]
    public void HostConflictsNeverTriggerAutomaticRehostingOrSignIn(string message, bool wasReady)
    {
        var failure = RemoteDevTunnelHost.CreateHostingFailure(message, wasReady);
        Assert.False(failure.CanRetry);
        Assert.False(failure.RequiresSignIn);
        Assert.Equal(Lumi.Localization.Loc.Get("Remote_DevTunnelHostConflict"), failure.Message);
    }

    [Fact]
    public void ClientConnectionLimitsAreNotMistakenForAHostConflict()
    {
        var failure = RemoteDevTunnelHost.CreateHostingFailure("Too many client connections.", wasReady: true);
        Assert.True(failure.CanRetry);
        Assert.NotEqual(Lumi.Localization.Loc.Get("Remote_DevTunnelHostConflict"), failure.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermanentHostConflictStopsWithoutStartingAReplacement(bool whileReconnecting)
    {
        const string origin = "https://bright-river-47654.uks1.devtunnels.ms";
        var host = new RemoteDevTunnelHost();
        var stopped = new TaskCompletionSource<RemoteDevTunnelCliException>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var retries = 0;
        var sawReconnecting = false;
        host.StateChanged += () => sawReconnecting |= host.State.IsReconnecting;
        try
        {
            host.Start(async lifetime =>
            {
                try
                {
                    await RemoteDevTunnelHost.RunWithRecoveryAsync(
                        token =>
                        {
                            Interlocked.Increment(ref attempts);
                            return host.HostAsync(
                                HostedProcessFixture(
                                    origin, exitAfterReady: false,
                                    outputAfterReady:
                                        (whileReconnecting
                                            ? "Connection to host tunnel relay closed. Connection lost. Reconnecting.\n"
                                            : "")
                                        + "Connection to host tunnel relay closed. Another host for the tunnel has connected."),
                                47654, "Fixture owner", lifetime, token,
                                reconnectTimeout: TimeSpan.FromMilliseconds(200));
                        },
                        (_, _) => Interlocked.Increment(ref retries),
                        lifetime.Token,
                        (_, _) => Task.CompletedTask);
                    stopped.TrySetException(new InvalidOperationException("Expected a terminal host conflict."));
                }
                catch (RemoteDevTunnelCliException ex)
                {
                    stopped.TrySetResult(ex);
                }
            });
            var failure = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(failure.CanRetry);
            Assert.False(failure.RequiresSignIn);
            Assert.Equal(1, attempts);
            Assert.Equal(0, retries);
            Assert.Equal(whileReconnecting, sawReconnecting);
            Assert.Equal(Lumi.Localization.Loc.Get("Remote_DevTunnelHostConflict"), failure.Message);
        }
        finally
        {
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task RunningHostAuthenticationFailureRehostsWithoutWaitingForProcessExit()
    {
        const string origin = "https://bright-river-47654.uks1.devtunnels.ms";
        var files = Directory.CreateTempSubdirectory("LumiDevTunnelRecovery-");
        var release = Path.Combine(files.FullName, "release");
        var pidFile = Path.Combine(files.FullName, "host.pid");
        var host = new RemoteDevTunnelHost();
        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new List<RemoteDevTunnelCliException>();
        var attempts = 0;
        var readyCount = 0;
        host.StateChanged += () =>
        {
            if (host.State.Origin is not null)
            {
                if (Interlocked.Increment(ref readyCount) == 1)
                    firstReady.TrySetResult();
                else
                    secondReady.TrySetResult();
            }
        };
        try
        {
            host.Start(lifetime => RemoteDevTunnelHost.RunWithRecoveryAsync(
                token =>
                {
                    var first = Interlocked.Increment(ref attempts) == 1;
                    var info = HostedProcessFixture(
                        origin, exitAfterReady: false,
                        errorAfterReady: first
                            ? "devtunnel: Error connecting host tunnel session: Not authorized (401). Refreshed tunnel access token is not valid."
                            : null,
                        readySignalFile: first ? release : null);
                    if (first)
                        info.Environment["LUMI_TEST_HOST_PID_FILE"] = pidFile;
                    return host.HostAsync(info, 47654, "Fixture owner", lifetime, token);
                },
                (error, _) => failures.Add(Assert.IsType<RemoteDevTunnelCliException>(error)),
                lifetime.Token,
                (_, _) => Task.CompletedTask));
            await firstReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var firstProcess = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidFile)));
            Assert.False(firstProcess.HasExited);
            await File.WriteAllTextAsync(release, "report the terminal error but stay alive");
            await secondReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await firstProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, attempts);
            var failure = Assert.Single(failures);
            Assert.False(failure.RequiresSignIn);
            Assert.True(failure.CanRetry);
            Assert.Equal(origin, host.State.Origin);
        }
        finally
        {
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            files.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RelayLossAndRestorationUpdateReadinessWithoutRestartingTheCli()
    {
        const string origin = "https://bright-river-47654.uks1.devtunnels.ms";
        var host = new RemoteDevTunnelHost();
        var states = new ConcurrentQueue<RemoteDevTunnelState>();
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        host.StateChanged += () =>
        {
            var state = host.State;
            states.Enqueue(state);
            if (state.Origin is not null && Interlocked.Increment(ref readyCount) == 2)
                restored.TrySetResult();
        };
        try
        {
            host.Start(lifetime => host.HostAsync(
                HostedProcessFixture(
                    origin, exitAfterReady: false,
                    outputAfterReady: "Connection to host tunnel relay closed. Connection lost. Reconnecting.\n"
                        + "Connection to host tunnel relay restored."),
                47654, "Fixture owner", lifetime, lifetime.Token));
            await restored.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var disconnected = Assert.Single(states, state => state.IsReconnecting);
            Assert.True(disconnected.IsStarting);
            Assert.Null(disconnected.Origin);
            Assert.Equal("Fixture owner", disconnected.Account);
            Assert.False(disconnected.RequiresSignIn);
            Assert.Equal(origin, host.State.Origin);
            Assert.False(host.State.IsStarting);
            Assert.False(host.State.IsReconnecting);
        }
        finally
        {
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task StalledCliRecoveryRehostsTheSameRouteWithinItsDeadline()
    {
        const string origin = "https://bright-river-47654.uks1.devtunnels.ms";
        var host = new RemoteDevTunnelHost();
        var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new List<RemoteDevTunnelCliException>();
        var readyCount = 0;
        var attempts = 0;
        host.StateChanged += () =>
        {
            if (host.State.Origin is not null && Interlocked.Increment(ref readyCount) == 2)
                secondReady.TrySetResult();
        };
        try
        {
            host.Start(lifetime => RemoteDevTunnelHost.RunWithRecoveryAsync(
                token => host.HostAsync(
                    HostedProcessFixture(
                        origin, exitAfterReady: false,
                        outputAfterReady: Interlocked.Increment(ref attempts) == 1
                            ? "Connection to host tunnel relay closed. Connection lost. Reconnecting."
                            : null),
                    47654, "Fixture owner", lifetime, token,
                    reconnectTimeout: TimeSpan.FromMilliseconds(200)),
                (error, _) => failures.Add(Assert.IsType<RemoteDevTunnelCliException>(error)),
                lifetime.Token,
                (_, _) => Task.CompletedTask));
            await secondReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(2, attempts);
            var failure = Assert.Single(failures);
            Assert.False(failure.RequiresSignIn);
            Assert.True(failure.CanRetry);
            Assert.Equal(origin, host.State.Origin);
        }
        finally
        {
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task CancelingDuringCliRecoveryCannotStartAnotherHost()
    {
        const string origin = "https://bright-river-47654.uks1.devtunnels.ms";
        var host = new RemoteDevTunnelHost();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var retries = 0;
        host.StateChanged += () =>
        {
            if (host.State.IsReconnecting)
                disconnected.TrySetResult();
        };
        try
        {
            host.Start(lifetime => RemoteDevTunnelHost.RunWithRecoveryAsync(
                token =>
                {
                    Interlocked.Increment(ref attempts);
                    return host.HostAsync(
                        HostedProcessFixture(
                            origin, exitAfterReady: false,
                            outputAfterReady: "Connection to host tunnel relay closed. Connection lost. Reconnecting."),
                        47654, "Fixture owner", lifetime, token);
                },
                (_, _) => Interlocked.Increment(ref retries),
                lifetime.Token,
                (_, _) => Task.CompletedTask));
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, attempts);
            Assert.Equal(0, retries);
            Assert.Null(host.State.Origin);
            Assert.False(host.State.IsReconnecting);
        }
        finally
        {
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static ProcessStartInfo HostedProcessFixture(
        string origin,
        bool exitAfterReady,
        string? outputAfterReady = null,
        string? errorAfterReady = null,
        string? readySignalFile = null)
    {
        var executable = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe")
            : "/bin/sh";
        var arguments = OperatingSystem.IsWindows()
            ? new[] { "-NoProfile", "-NonInteractive", "-Command",
                "if ($env:LUMI_TEST_HOST_PID_FILE) { [IO.File]::WriteAllText($env:LUMI_TEST_HOST_PID_FILE, [string]$PID) }; " +
                "[Console]::Out.WriteLine('Hosting port 47654 at ' + $env:LUMI_TEST_HOST_ORIGIN + '/'); " +
                "[Console]::Out.WriteLine('Ready to accept connections for tunnel: lumi-profile.uks1'); " +
                "if ($env:LUMI_TEST_HOST_RELEASE) { while (!(Test-Path -LiteralPath $env:LUMI_TEST_HOST_RELEASE)) { Start-Sleep -Milliseconds 10 } }; " +
                "if ($env:LUMI_TEST_HOST_OUTPUT) { [Console]::Out.WriteLine($env:LUMI_TEST_HOST_OUTPUT) }; " +
                "if ($env:LUMI_TEST_HOST_ERROR) { [Console]::Error.WriteLine($env:LUMI_TEST_HOST_ERROR) }; " +
                (exitAfterReady
                    ? "[Console]::Error.WriteLine('Connection reset by peer.'); exit 1"
                    : "while ($true) { Start-Sleep -Milliseconds 50 }") }
            : ["-c", "if [ -n \"$LUMI_TEST_HOST_PID_FILE\" ]; then printf '%s' \"$$\" > \"$LUMI_TEST_HOST_PID_FILE\"; fi; " +
                "printf 'Hosting port 47654 at %s/\\n' \"$LUMI_TEST_HOST_ORIGIN\"; " +
                "printf 'Ready to accept connections for tunnel: lumi-profile.uks1\\n'; " +
                "if [ -n \"$LUMI_TEST_HOST_RELEASE\" ]; then while [ ! -f \"$LUMI_TEST_HOST_RELEASE\" ]; do sleep 0.01; done; fi; " +
                "if [ -n \"$LUMI_TEST_HOST_OUTPUT\" ]; then printf '%s\\n' \"$LUMI_TEST_HOST_OUTPUT\"; fi; " +
                "if [ -n \"$LUMI_TEST_HOST_ERROR\" ]; then printf '%s\\n' \"$LUMI_TEST_HOST_ERROR\" >&2; fi; " +
                (exitAfterReady
                    ? "printf 'Connection reset by peer.\\n' >&2; exit 1"
                    : "while true; do sleep 1; done")];
        var info = RemoteDevTunnelCli.CreateStartInfo(executable, arguments);
        info.Environment["LUMI_TEST_HOST_ORIGIN"] = origin;
        info.Environment["LUMI_TEST_HOST_OUTPUT"] = outputAfterReady ?? "";
        info.Environment["LUMI_TEST_HOST_ERROR"] = errorAfterReady ?? "";
        info.Environment["LUMI_TEST_HOST_RELEASE"] = readySignalFile ?? "";
        info.Environment["LUMI_TEST_HOST_PID_FILE"] = "";
        return info;
    }

    [Fact]
    public async Task EffectiveListenerPortIsPersistedEvenWhenTunnelIdIsAlreadyKnown()
    {
        const string route = "lumi-0123456789abcdef0123456789abcdef.uks1";
        var data = new AppData { Settings = new UserSettings { RemoteDevTunnelId = route } };
        var store = new DataStore(data);
        using var main = new MainViewModel(store, TestCopilot.Shared, new UpdateService(),
            initializeCopilotOnStartup: false, startBackgroundJobs: false);
        await using var server = new LumiRemoteServer(store, main);
        await server.PersistDevTunnelRouteAsync(route, 53421, CancellationToken.None);
        Assert.Equal(route, data.Settings.RemoteDevTunnelId);
        Assert.Equal(53421, data.Settings.RemoteAccessPort);
        Assert.Equal(53421, AppDataSnapshotFactory.CreateIndexSnapshot(data).Settings.RemoteAccessPort);
    }

    [Fact]
    public async Task FailedRouteSaveMustBeRetriedBeforeTheSameRouteCanBeUsed()
    {
        const string route = "lumi-0123456789abcdef0123456789abcdef.uks1";
        var store = new DataStore(new AppData());
        var saves = 0;
        store.IndexSaved += () =>
        {
            if (++saves == 1)
                throw new IOException("Fixture save failed.");
        };
        using var main = new MainViewModel(store, TestCopilot.Shared, new UpdateService(),
            initializeCopilotOnStartup: false, startBackgroundJobs: false);
        await using var server = new LumiRemoteServer(store, main);
        var reconnects = 0;
        var hosted = false;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RemoteDevTunnelHost.RunWithRecoveryAsync(
                async token =>
                {
                    await server.PersistDevTunnelRouteAsync(route, 53421, token);
                    hosted = true;
                },
                (_, _) => reconnects++,
                CancellationToken.None,
                (_, _) => Task.CompletedTask));
        Assert.IsType<IOException>(error.InnerException);
        Assert.Contains("Fixture save failed.", error.Message);
        Assert.False(hosted);
        Assert.Equal(0, reconnects);
        await server.PersistDevTunnelRouteAsync(route, 53421, CancellationToken.None);
        Assert.Equal(2, saves);
    }

    [Fact]
    public async Task OccupiedSavedPortIsActionableAndNeverChangesThePhoneUrl()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        var data = new AppData
        {
            Settings = new UserSettings
            {
                RemoteUseDevTunnel = true,
                RemoteAccessPort = port,
                RemoteDevTunnelId = "lumi-0123456789abcdef0123456789abcdef.uks1"
            }
        };
        var store = new DataStore(data);
        using var main = new MainViewModel(store, TestCopilot.Shared, new UpdateService(),
            initializeCopilotOnStartup: false, startBackgroundJobs: false);
        await using var server = new LumiRemoteServer(store, main);
        server.Start();
        Assert.False(server.IsRunning);
        Assert.Contains(port.ToString(), server.StartError);
        Assert.Equal(port, data.Settings.RemoteAccessPort);
        Assert.Empty(server.ListenAddresses);
    }

    [SkippableFact]
    public async Task RealPrivateRelayKeepsItsUrlAndOwnerGateAcrossRehosting()
    {
        var cli = Environment.GetEnvironmentVariable("LUMI_DEVTUNNEL_TEST_CLI");
        Skip.If(string.IsNullOrWhiteSpace(cli) || !File.Exists(cli),
            "Set LUMI_DEVTUNNEL_TEST_CLI to an authenticated Microsoft CLI to run the isolated relay smoke test.");
        using (var identity = JsonDocument.Parse(
                   await RemoteDevTunnelCli.RunAsync(cli!, ["user", "show", "--json"], CancellationToken.None)))
        {
            Skip.If(identity.RootElement.GetProperty("status").GetString() != "Logged in"
                || identity.RootElement.GetProperty("provider").GetString() != "microsoft",
                "Cached Microsoft sign-in is unavailable. This smoke test never starts a login.");
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var listener = new RemoteHttpListener(
            (context, token) => context.WriteTextAsync("lumi-private-relay-fixture", token));
        listener.Start(0, loopbackOnly: true);
        var requested = RemoteDevTunnelHost.CreateProfileTunnelId();
        string? ownedRoute = null;
        string? previousOrigin = null;
        Task<string> Run(string[] args, CancellationToken token) => RemoteDevTunnelCli.RunAsync(cli!, args, token);
        try
        {
            for (var iteration = 0; iteration < 2; iteration++)
            {
                var route = await RemoteDevTunnelHost.PrepareProfileTunnelAsync(
                    ownedRoute ?? requested, listener.Port, Run,
                    (id, _) => { ownedRoute = id; return Task.CompletedTask; },
                    deadline.Token);
                using (var metadata = JsonDocument.Parse(await Run(["show", route, "--json"], deadline.Token)))
                {
                    var expirationText = metadata.RootElement.GetProperty("tunnel")
                        .GetProperty("tunnelExpiration").GetString();
                    Assert.Equal("30 days", expirationText);
                }
                var host = new RemoteDevTunnelHost();
                var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                host.StateChanged += () =>
                {
                    if (host.State.Origin is { } value)
                        ready.TrySetResult(value);
                };
                try
                {
                    host.Start(async lifetime =>
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                            lifetime.Token, deadline.Token);
                        try
                        {
                            await host.HostAsync(
                                RemoteDevTunnelCli.CreateStartInfo(cli!, RemoteDevTunnelHost.HostArguments(route)),
                                listener.Port, "Relay smoke fixture", lifetime, linked.Token);
                        }
                        catch (Exception ex)
                        {
                            ready.TrySetException(ex);
                            throw;
                        }
                    });
                    var origin = await ready.Task.WaitAsync(deadline.Token);
                    if (previousOrigin is not null)
                        Assert.Equal(previousOrigin, origin);
                    previousOrigin = origin;
                    using var http = new HttpClient(new HttpClientHandler
                    {
                        AllowAutoRedirect = false,
                        UseProxy = false,
                        UseCookies = false
                    });
                    http.DefaultRequestHeaders.Add("X-Tunnel-Skip-AntiPhishing-Page", "true");
                    using var response = await http.GetAsync(origin + "/app/", deadline.Token);
                    Assert.True(response.StatusCode is HttpStatusCode.Redirect
                        or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect
                        or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                        $"The gateway must require owner authentication, not return the fixture ({response.StatusCode}).");
                }
                finally
                {
                    await host.DisposeAsync();
                }
            }
        }
        finally
        {
            if (ownedRoute is not null)
                await Run(["delete", ownedRoute], CancellationToken.None);
        }
    }

    [Fact]
    public void ProfileTunnelIdIsStableFormatAndOnlyMatchesLumiOwnedTunnel()
    {
        var requestedTunnelId = RemoteDevTunnelHost.CreateProfileTunnelId();
        Assert.True(RemoteDevTunnelHost.IsValidProfileTunnelId(requestedTunnelId));
        Assert.True(RemoteDevTunnelHost.IsValidProfileTunnelId(requestedTunnelId + ".uks1"));
        Assert.False(RemoteDevTunnelHost.IsValidProfileTunnelId(""));
        Assert.False(RemoteDevTunnelHost.IsValidProfileTunnelId("lumi-not-a-guid"));
        Assert.False(RemoteDevTunnelHost.IsValidProfileTunnelId(requestedTunnelId + ".uks1.invalid"));

        var json = $$"""
            {
              "tunnels": [
                {
                  "tunnelId": "{{requestedTunnelId}}.uks1",
                  "description": "Someone else's tunnel"
                },
                {
                  "tunnelId": "other-tunnel.uks1",
                  "description": "Lumi private web app"
                },
                {
                  "tunnelId": "{{requestedTunnelId}}.uks1",
                  "description": "Lumi private web app"
                }
              ]
            }
            """;

        Assert.Equal(
            $"{requestedTunnelId}.uks1",
            RemoteDevTunnelHost.FindExistingProfileTunnelId(json, requestedTunnelId));
        Assert.Equal(
            $"{requestedTunnelId}.uks1",
            RemoteDevTunnelHost.FindExistingProfileTunnelId(json, $"{requestedTunnelId}.uks1"));
        Assert.Null(
            RemoteDevTunnelHost.FindExistingProfileTunnelId(json, $"{requestedTunnelId}.euw"));
        Assert.Null(RemoteDevTunnelHost.FindExistingProfileTunnelId(json, "lumi-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
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
            Settings = new UserSettings
            {
                RemoteUseDevTunnel = true,
                RemoteDevTunnelId = "lumi-0123456789abcdef0123456789abcdef"
            }
        };
        if (hasChats)
            persisted.Chats.Add(new Chat());
        var snapshot = AppDataSnapshotFactory.CreateIndexSnapshot(persisted);
        Assert.True(snapshot.Settings.RemoteUseDevTunnel);
        Assert.Equal(persisted.Settings.RemoteDevTunnelId, snapshot.Settings.RemoteDevTunnelId);
        var merged = AppDataSnapshotFactory.MergeChatIndexChanges(
            new AppData(), snapshot, new HashSet<Guid>(), new HashSet<Guid>(), false);
        Assert.True(merged.Settings.RemoteUseDevTunnel);
        Assert.Equal(persisted.Settings.RemoteDevTunnelId, merged.Settings.RemoteDevTunnelId);

        var store = new DataStore(new AppData());
        store.ApplyRemoteSecuritySnapshot(snapshot.Settings);
        Assert.True(store.Data.Settings.RemoteUseDevTunnel);
        Assert.Equal(persisted.Settings.RemoteDevTunnelId, store.Data.Settings.RemoteDevTunnelId);
    }
}
