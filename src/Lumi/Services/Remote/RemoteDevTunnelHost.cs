using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lumi.Localization;

namespace Lumi.Services.Remote;

internal sealed record RemoteDevTunnelState(
    bool IsStarting = false,
    string? Account = null,
    string? Origin = null,
    string? Error = null,
    string? SetupMessage = null,
    bool RequiresInstallConfirmation = false,
    bool RequiresSignIn = false,
    bool IsSigningIn = false,
    RemoteDevTunnelSignIn? SignIn = null,
    bool IsReconnecting = false);

internal sealed record RemoteDevTunnelSignIn(string Url, string Code);

internal sealed class RemoteDevTunnelHost : IAsyncDisposable
{
    internal const string TunnelDescription = "Lumi private web app";
    private static readonly TimeSpan HostConnectionTimeout = TimeSpan.FromSeconds(90);

    private readonly object _gate = new();
    private CancellationTokenSource? _lifetime;
    private TaskCompletionSource<bool>? _installApproval;
    private Task _worker = Task.CompletedTask;
    private RemoteDevTunnelState _state = new();

    public RemoteDevTunnelState State => Volatile.Read(ref _state);
    public event Action? StateChanged;

    internal static string[] CreateArguments(string requestedTunnelId)
    {
        if (!IsValidProfileTunnelId(requestedTunnelId))
            throw new InvalidOperationException(Loc.Get("Remote_DevTunnelInvalidResponse"));

        var separator = requestedTunnelId.IndexOf('.');
        var baseId = separator < 0 ? requestedTunnelId : requestedTunnelId[..separator];
        var arguments = new List<string> { "create", baseId };
        if (separator >= 0)
        {
            var clusterId = requestedTunnelId[(separator + 1)..];
            arguments.AddRange([
                "--service-uri", $"https://{clusterId}.rel.tunnels.api.visualstudio.com"
            ]);
        }
        arguments.AddRange([
            "--expiration", "30d", "--description", TunnelDescription,
            "--host-header", "localhost", "--origin-header", "unchanged",
            "--request-timeout", "0", "--json"
        ]);
        return arguments.ToArray();
    }

    internal static string[] HostArguments(string tunnelId) =>
        ["host", tunnelId, "--host-header", "localhost", "--origin-header", "unchanged"];

    internal static string[] RefreshArguments(string tunnelId) =>
        ["update", tunnelId, "--expiration", "30d", "--host-header", "localhost",
            "--origin-header", "unchanged", "--request-timeout", "0", "--json"];

    internal static string[] DeviceSignInArguments =>
        ["user", "login", "--entra", "--use-device-code-auth", "--json"];

    internal static string[] DefaultSignInArguments =>
        ["user", "login", "--entra", "--json"];

    internal static string CreateProfileTunnelId() =>
        $"lumi-{Guid.NewGuid():N}";

    internal static bool IsValidProfileTunnelId(string? tunnelId) =>
        tunnelId is not null
        && Regex.IsMatch(
            tunnelId,
            "^lumi-[0-9a-f]{32}(?:\\.[a-z0-9]+)?$",
            RegexOptions.CultureInvariant);

    public void Start(
        int port,
        string requestedTunnelId,
        Func<string, CancellationToken, Task> persistTunnelId,
        bool signIn = false,
        bool useDeviceCode = false) =>
        Start(lifetime => RunAsync(port, requestedTunnelId, persistTunnelId, lifetime, signIn, useDeviceCode));

    internal void Start(Func<CancellationTokenSource, Task> run)
    {
        lock (_gate)
        {
            _lifetime?.Cancel();
            _installApproval = null;
            var lifetime = new CancellationTokenSource();
            _lifetime = lifetime;
            Volatile.Write(ref _state, new RemoteDevTunnelState(IsStarting: true));
            var previous = _worker;
            _worker = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    lifetime.Token.ThrowIfCancellationRequested();
                    await run(lifetime).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_lifetime, lifetime))
                            _lifetime = null;
                        lifetime.Dispose();
                    }
                }
            });
        }
        StateChanged?.Invoke();
    }

    public void Stop() => Stop(new RemoteDevTunnelState());

    public void CancelSetup() => Stop(new RemoteDevTunnelState(
        SetupMessage: Loc.Get("Remote_DevTunnelSetupCanceled")));

    private void Stop(RemoteDevTunnelState state)
    {
        lock (_gate)
        {
            _lifetime?.Cancel();
            _lifetime = null;
            _installApproval = null;
            Volatile.Write(ref _state, state);
        }
        StateChanged?.Invoke();
    }

    public void RespondToInstallConfirmation(bool approved)
    {
        lock (_gate)
        {
            if (_lifetime is not { IsCancellationRequested: false }
                || _installApproval?.TrySetResult(approved) != true)
                return;
            Volatile.Write(ref _state, _state with { RequiresInstallConfirmation = false });
        }
        StateChanged?.Invoke();
    }

    private async Task<bool> RequestInstallConfirmationAsync(CancellationTokenSource lifetime)
    {
        var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            _installApproval = approval;
        }
        Publish(lifetime, new RemoteDevTunnelState(
            IsStarting: true,
            SetupMessage: Loc.Get("Remote_DevTunnelAwaitingInstall"),
            RequiresInstallConfirmation: true));
        try
        {
            return await approval.Task.WaitAsync(lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_installApproval, approval))
                    _installApproval = null;
            }
        }
    }

    private async Task RunAsync(
        int port,
        string requestedTunnelId,
        Func<string, CancellationToken, Task> persistTunnelId,
        CancellationTokenSource lifetime,
        bool signIn,
        bool useDeviceCode)
    {
        string? account = null;
        try
        {
            var token = lifetime.Token;
            token.ThrowIfCancellationRequested();
            var executablePath = await RemoteDevTunnelCli.EnsureAvailableAsync(
                    _ => RequestInstallConfirmationAsync(lifetime),
                    message => Publish(lifetime, new RemoteDevTunnelState(IsStarting: true, SetupMessage: message)),
                    token)
                .ConfigureAwait(false);
            if (executablePath is null)
            {
                Publish(lifetime, new RemoteDevTunnelState(
                    SetupMessage: Loc.Get("Remote_DevTunnelInstallCanceled")));
                return;
            }
            var promptOutput = new StringBuilder();
            var promptGate = new object();
            void ReportSignInOutput(string chunk)
            {
                RemoteDevTunnelSignIn? prompt;
                lock (promptGate)
                {
                    promptOutput.Append(chunk);
                    if (promptOutput.Length > 4096)
                        promptOutput.Remove(0, promptOutput.Length - 4096);
                    prompt = ParseDeviceSignIn(promptOutput.ToString());
                }
                if (prompt is not null && prompt != State.SignIn)
                {
                    Publish(lifetime, new RemoteDevTunnelState(
                        IsStarting: true, IsSigningIn: true, SignIn: prompt,
                        SetupMessage: Loc.Get("Remote_DevTunnelSigningIn")));
                }
            }

            Task<string> RunCli(string[] arguments, CancellationToken ct) =>
                RemoteDevTunnelCli.RunAsync(executablePath, arguments, ct);
            var identity = await EnsureMicrosoftIdentityAsync(
                    (arguments, ct) => RemoteDevTunnelCli.RunAsync(
                        executablePath, arguments, ct,
                        arguments is ["user", "login", ..] ? ReportSignInOutput : null),
                    () => Publish(lifetime, new RemoteDevTunnelState(
                        IsStarting: true, IsSigningIn: true,
                        SetupMessage: Loc.Get(useDeviceCode
                            ? "Remote_DevTunnelGettingSignInCode" : "Remote_DevTunnelDesktopSigningIn"))),
                    token,
                    signIn, useDeviceCode)
                .ConfigureAwait(false);
            if (identity is not { } owner)
            {
                Publish(lifetime, new RemoteDevTunnelState(
                    RequiresSignIn: true, SetupMessage: Loc.Get("Remote_DevTunnelSignInRequired")));
                return;
            }
            account = owner.Username;

            await RunWithRecoveryAsync(
                async ct =>
                {
                    Publish(lifetime, new RemoteDevTunnelState(
                        IsStarting: true, Account: account,
                        SetupMessage: Loc.Get("Remote_DevTunnelStarting")));
                    await RequireSameIdentityAsync(RunCli, owner, ct).ConfigureAwait(false);
                    requestedTunnelId = await PrepareProfileTunnelAsync(
                            requestedTunnelId, port, RunCli, persistTunnelId, ct)
                        .ConfigureAwait(false);
                    await RequireSameIdentityAsync(RunCli, owner, ct).ConfigureAwait(false);
                    await HostAsync(
                            RemoteDevTunnelCli.CreateStartInfo(executablePath, HostArguments(requestedTunnelId)),
                            port, account, lifetime, ct)
                        .ConfigureAwait(false);
                },
                (error, delay) =>
                {
                    Trace.TraceWarning($"[Remote] Dev Tunnel will reconnect: {error.Message}");
                    Publish(lifetime, new RemoteDevTunnelState(
                        IsStarting: true, Account: account, IsReconnecting: true,
                        SetupMessage: Loc.Get("Remote_DevTunnelReconnecting", (int)delay.TotalSeconds)));
                },
                token).ConfigureAwait(false);
        }
        catch (RemoteDevTunnelCliException ex) when (ex.RequiresSignIn)
        {
            Trace.TraceWarning("[Remote] Dev Tunnel requires Microsoft sign-in.");
            Publish(lifetime, new RemoteDevTunnelState(
                Account: account, RequiresSignIn: true,
                Error: State.IsSigningIn ? Loc.Get("Remote_DevTunnelSignInFailed") : null,
                SetupMessage: Loc.Get("Remote_DevTunnelSignInRequired")));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Win32Exception ex)
        {
            Trace.TraceWarning($"[Remote] Dev Tunnel CLI could not start: {ex.Message}");
            Publish(lifetime, new RemoteDevTunnelState(Account: account, Error: Loc.Get("Remote_DevTunnelStartFailed")));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException
                                       or OperationCanceledException or HttpRequestException
                                       or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            var signingIn = State.IsSigningIn;
            var message = ex is OperationCanceledException
                ? Loc.Get(signingIn ? "Remote_DevTunnelSignInTimeout" : "Remote_DevTunnelTimeout")
                : ex.Message;
            Trace.TraceWarning($"[Remote] Private Dev Tunnel failed: {message}");
            Publish(lifetime, new RemoteDevTunnelState(
                Account: account, Error: message, RequiresSignIn: signingIn));
        }
    }

    private static async Task RequireSameIdentityAsync(
        Func<string[], CancellationToken, Task<string>> runCli,
        (string Username, string ObjectId, string TenantId) owner,
        CancellationToken cancellationToken)
    {
        var current = await EnsureMicrosoftIdentityAsync(runCli, () => { }, cancellationToken)
            .ConfigureAwait(false);
        if (current is not { } identity)
            throw new RemoteDevTunnelCliException(Loc.Get("Remote_DevTunnelSignInRequired"), requiresSignIn: true);
        if (identity.ObjectId != owner.ObjectId || identity.TenantId != owner.TenantId)
            throw new RemoteDevTunnelCliException(
                Loc.Get("Remote_DevTunnelAccountChanged"), requiresSignIn: true, canRetry: false);
    }

    internal static async Task<string> PrepareProfileTunnelAsync(
        string requestedTunnelId,
        int port,
        Func<string[], CancellationToken, Task<string>> runCli,
        Func<string, CancellationToken, Task> persistTunnelId,
        CancellationToken cancellationToken)
    {
        var tunnelId = FindExistingProfileTunnelId(
            await runCli(["list", "--json"], cancellationToken).ConfigureAwait(false), requestedTunnelId);
        if (tunnelId is null)
            tunnelId = await CreateProfileTunnelAsync(requestedTunnelId, runCli, cancellationToken)
                .ConfigureAwait(false);
        await persistTunnelId(tunnelId, cancellationToken).ConfigureAwait(false);
        RequireOwnerOnlyAccess(
            await runCli(["access", "list", tunnelId, "--json"], cancellationToken).ConfigureAwait(false));
        var hasPort = HasExpectedPort(
            await runCli(["port", "list", tunnelId, "--json"], cancellationToken).ConfigureAwait(false),
            port, tunnelId);
        if (hasPort)
        {
            RequireOwnerOnlyAccess(
                await runCli(
                    ["access", "list", tunnelId, "-p", port.ToString(CultureInfo.InvariantCulture), "--json"],
                    cancellationToken).ConfigureAwait(false));
        }
        var arguments = new List<string>
        {
            "port", hasPort ? "update" : "create", tunnelId, "-p", port.ToString(CultureInfo.InvariantCulture)
        };
        if (!hasPort)
            arguments.AddRange(["--protocol", "http"]);
        arguments.AddRange([
            "--host-header", "localhost", "--origin-header", "unchanged", "--request-timeout", "0", "--json"
        ]);
        await runCli(arguments.ToArray(), cancellationToken).ConfigureAwait(false);
        await VerifyAccessAsync(tunnelId, port, runCli, cancellationToken).ConfigureAwait(false);
        await runCli(RefreshArguments(tunnelId), cancellationToken).ConfigureAwait(false);
        return tunnelId;
    }

    internal static bool HasExpectedPort(string json, int port, string? tunnelId = null)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("ports", out var ports) || ports.ValueKind != JsonValueKind.Array)
        {
            if (tunnelId is not null)
            {
                var separator = tunnelId.IndexOf('.');
                var baseId = separator < 0 ? tunnelId : tunnelId[..separator];
                if (IsOnlyWarning(document.RootElement, $"No ports found for tunnel {baseId}."))
                    return false;
            }
            throw new InvalidOperationException(Loc.Get("Remote_DevTunnelInvalidResponse"));
        }
        if (ports.GetArrayLength() == 0)
            return false;
        if (ports.GetArrayLength() != 1
            || !ports[0].TryGetProperty("portNumber", out var number) || !number.TryGetInt32(out var value) || value != port
            || !ports[0].TryGetProperty("protocol", out var protocol) || protocol.GetString() != "http")
        {
            throw new InvalidOperationException(Loc.Get("Remote_DevTunnelUnexpectedPorts"));
        }
        return true;
    }

    private static async Task VerifyAccessAsync(
        string tunnelId, int port,
        Func<string[], CancellationToken, Task<string>> runCli,
        CancellationToken cancellationToken)
    {
        RequireOwnerOnlyAccess(
            await runCli(["access", "list", tunnelId, "--json"], cancellationToken).ConfigureAwait(false));
        RequireOwnerOnlyAccess(
            await runCli(
                ["access", "list", tunnelId, "-p", port.ToString(CultureInfo.InvariantCulture), "--json"],
                cancellationToken).ConfigureAwait(false));
    }

    internal static async Task RunWithRecoveryAsync(
        Func<CancellationToken, Task> run,
        Action<Exception, TimeSpan> reportReconnect,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await run(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                && (ex is RemoteDevTunnelCliException { RequiresSignIn: false, CanRetry: true }
                    or IOException or HttpRequestException
                    || ex is OperationCanceledException))
            {
                var retryDelay = TimeSpan.FromSeconds(Math.Min(5 * (1 << Math.Min(attempt++, 4)), 60));
                reportReconnect(ex, retryDelay);
                await delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<string> CreateProfileTunnelAsync(
        string requestedRoute,
        Func<string[], CancellationToken, Task<string>> runCli,
        CancellationToken cancellationToken)
    {
        using var created = JsonDocument.Parse(
            await runCli(CreateArguments(requestedRoute), cancellationToken).ConfigureAwait(false));
        if (!created.RootElement.TryGetProperty("tunnel", out var tunnel)
            || !tunnel.TryGetProperty("tunnelId", out var id)
            || id.ValueKind != JsonValueKind.String
            || id.GetString() is not { Length: > 0 } tunnelId)
        {
            throw new InvalidOperationException(Loc.Get("Remote_DevTunnelInvalidResponse"));
        }
        try
        {
            return RequireExpectedTunnelId(tunnelId, requestedRoute);
        }
        catch (InvalidOperationException)
        {
            try
            {
                await runCli(["delete", tunnelId], CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException
                                           or OperationCanceledException)
            {
                Trace.TraceWarning($"[Remote] Could not remove rejected Dev Tunnel {tunnelId}: {ex.Message}");
            }
            throw;
        }
    }

    internal async Task HostAsync(
        ProcessStartInfo startInfo,
        int port,
        string account,
        CancellationTokenSource lifetime,
        CancellationToken cancellationToken,
        TimeSpan? reconnectTimeout = null)
    {
        using var hosting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        hosting.CancelAfter(HostConnectionTimeout);
        using var process = new Process
        {
            StartInfo = startInfo
        };
        if (!process.Start())
            throw new InvalidOperationException(Loc.Get("Remote_DevTunnelStartFailed"));
        using var registration = hosting.Token.Register(() => RemoteDevTunnelCli.StopProcess(process));
        var terminalError = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = RemoteDevTunnelCli.ReadErrorLinesAsync(process.StandardError, hosting.Token, line =>
        {
            if (IsTerminalHostFailure(line))
                terminalError.TrySetResult(line);
        });
        Task? pendingOutput = null;
        string? origin = null;
        var ready = false;
        var reconnecting = false;
        try
        {
            while (true)
            {
                var output = process.StandardOutput.ReadLineAsync(hosting.Token).AsTask();
                pendingOutput = output;
                await Task.WhenAny(pendingOutput, terminalError.Task, errors).ConfigureAwait(false);
                if (terminalError.Task.IsCompletedSuccessfully)
                    throw CreateHostingFailure(await terminalError.Task.ConfigureAwait(false), ready);
                if (errors.IsFaulted)
                    await errors.ConfigureAwait(false);
                var line = await output.ConfigureAwait(false);
                pendingOutput = null;
                hosting.Token.ThrowIfCancellationRequested();
                if (line is null)
                    break;
                origin ??= FindWebOrigin(line, port);
                if (origin is not null
                    && line.Contains("Ready to accept connections", StringComparison.Ordinal))
                {
                    ready = true;
                    reconnecting = false;
                    hosting.CancelAfter(Timeout.InfiniteTimeSpan);
                    Publish(lifetime, new RemoteDevTunnelState(Account: account, Origin: origin));
                }
                else if (line.Contains("Connection to host tunnel relay closed.", StringComparison.Ordinal))
                {
                    if (IsHostConflict(line))
                        throw CreateHostingFailure(line, ready);
                    if (ready && !reconnecting)
                    {
                        reconnecting = true;
                        hosting.CancelAfter(reconnectTimeout ?? HostConnectionTimeout);
                        Trace.TraceWarning("[Remote] Dev Tunnel relay disconnected; waiting for CLI recovery.");
                        Publish(lifetime, new RemoteDevTunnelState(
                            IsStarting: true, Account: account, IsReconnecting: true,
                            SetupMessage: Loc.Get("Remote_DevTunnelRestoring")));
                    }
                }
                else if (ready && reconnecting
                         && line.Contains("Connection to host tunnel relay restored.", StringComparison.Ordinal))
                {
                    reconnecting = false;
                    hosting.CancelAfter(Timeout.InfiniteTimeSpan);
                    Trace.TraceInformation("[Remote] Dev Tunnel relay connection restored.");
                    Publish(lifetime, new RemoteDevTunnelState(Account: account, Origin: origin));
                }
            }

            await process.WaitForExitAsync(hosting.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var error = (await errors.ConfigureAwait(false)).Trim();
            throw CreateHostingFailure(error, ready);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RemoteDevTunnelCliException(Loc.Get("Remote_DevTunnelTimeout"));
        }
        finally
        {
            hosting.Cancel();
            RemoteDevTunnelCli.StopProcess(process);
            try
            {
                await Task.WhenAll(errors, pendingOutput ?? Task.CompletedTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (hosting.IsCancellationRequested)
            {
            }
        }
    }

    internal static bool IsTerminalHostFailure(string line) =>
        Regex.IsMatch(
            line, @"^(?:[^:\r\n]+:\s*)?Error connecting host tunnel session:",
            RegexOptions.CultureInvariant);

    internal static RemoteDevTunnelCliException CreateHostingFailure(string error, bool wasReady)
    {
        if (IsHostConflict(error))
            return new RemoteDevTunnelCliException(Loc.Get("Remote_DevTunnelHostConflict"), canRetry: false);

        // A ready host may hold expired credentials; restart it before requiring sign-in.
        return new RemoteDevTunnelCliException(
            Loc.Get("Remote_DevTunnelStopped", error),
            requiresSignIn: !wasReady && RemoteDevTunnelCli.IsAuthenticationFailure(error),
            canRetry: wasReady || RemoteDevTunnelCli.IsTransientFailure(error));
    }

    private static bool IsHostConflict(string message) =>
        message.Contains("Another host for the tunnel has connected.", StringComparison.Ordinal)
        || message.Contains("another host for this tunnel has connected.", StringComparison.Ordinal);

    internal static async Task<(string Username, string ObjectId, string TenantId)?> EnsureMicrosoftIdentityAsync(
        Func<string[], CancellationToken, Task<string>> runCli,
        Action reportSigningIn,
        CancellationToken cancellationToken,
        bool signIn = false,
        bool useDeviceCode = false)
    {
        var json = await runCli(["user", "show", "--json"], cancellationToken).ConfigureAwait(false);
        using (var document = JsonDocument.Parse(json))
        {
            var root = document.RootElement;
            var signedOut = root.TryGetProperty("status", out var status)
                            && status.GetString() is "Not logged in" or "Login token expired";
            var github = status.ValueKind == JsonValueKind.String && status.GetString() == "Logged in"
                         && root.TryGetProperty("provider", out var provider) && provider.GetString() == "github";
            if (signIn)
            {
                reportSigningIn();
                await runCli(useDeviceCode ? DeviceSignInArguments : DefaultSignInArguments, cancellationToken)
                    .ConfigureAwait(false);
                json = await runCli(["user", "show", "--json"], cancellationToken).ConfigureAwait(false);
            }
            else if (signedOut || github)
                return null;
        }
        return ParseMicrosoftIdentity(json);
    }

    internal static RemoteDevTunnelSignIn? ParseDeviceSignIn(string output)
    {
        var code = Regex.Match(
            output, @"\benter\s+(?:the\s+)?code\s*:?\s*([A-Z0-9]{8,12})\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!code.Success)
            return null;
        foreach (Match match in Regex.Matches(output, @"https://[^\s<>""']+"))
        {
            if (IsAllowedSignInUrl(match.Value))
            {
                return new RemoteDevTunnelSignIn(match.Value, code.Groups[1].Value);
            }
        }
        return null;
    }

    internal static bool IsAllowedSignInUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && (uri.IdnHost is "microsoft.com" or "www.microsoft.com" && uri.AbsolutePath == "/devicelogin"
            || uri.IdnHost == "login.microsoft.com" && uri.AbsolutePath == "/device"
            || uri.IdnHost == "login.microsoftonline.com" && uri.AbsolutePath == "/common/oauth2/deviceauth");

    internal static (string Username, string ObjectId, string TenantId) ParseMicrosoftIdentity(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("status", out var status) || status.GetString() != "Logged in"
            || !root.TryGetProperty("provider", out var provider) || provider.GetString() != "microsoft"
            || !root.TryGetProperty("username", out var username) || username.GetString() is not { Length: > 0 } name
            || !root.TryGetProperty("objectId", out var objectId) || objectId.GetString() is not { Length: > 0 } id
            || !root.TryGetProperty("tenantId", out var tenantId) || tenantId.GetString() is not { Length: > 0 } tenant)
        {
            throw new InvalidOperationException(Loc.Get("Remote_DevTunnelSignIn"));
        }
        return (name, id, tenant);
    }

    internal static string? FindExistingProfileTunnelId(string json, string requestedTunnelId)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("tunnels", out var tunnels)
            || tunnels.ValueKind != JsonValueKind.Array)
        {
            if (IsOnlyWarning(document.RootElement, "No tunnels found."))
                return null;
            throw new InvalidOperationException(Loc.Get("Remote_DevTunnelInvalidResponse"));
        }

        foreach (var tunnel in tunnels.EnumerateArray())
        {
            if (!tunnel.TryGetProperty("tunnelId", out var id)
                || id.GetString() is not { Length: > 0 } tunnelId
                || !tunnel.TryGetProperty("description", out var description)
                || description.GetString() != TunnelDescription)
            {
                continue;
            }

            var requestedHasCluster = requestedTunnelId.IndexOf('.') >= 0;
            var separator = tunnelId.IndexOf('.');
            var baseId = separator < 0 ? tunnelId : tunnelId[..separator];
            if (IsValidProfileTunnelId(tunnelId)
                && (requestedHasCluster
                    ? string.Equals(tunnelId, requestedTunnelId, StringComparison.Ordinal)
                    : string.Equals(baseId, requestedTunnelId, StringComparison.Ordinal)))
                return tunnelId;
        }

        return null;
    }

    private static bool IsOnlyWarning(JsonElement root, string expected) =>
        root.ValueKind == JsonValueKind.Object
        && root.EnumerateObject().Count() == 1
        && root.TryGetProperty("warning", out var warning)
        && warning.ValueKind == JsonValueKind.String
        && string.Equals(warning.GetString(), expected, StringComparison.Ordinal);

    internal static string RequireExpectedTunnelId(string createdTunnelId, string requestedTunnelId)
    {
        if (!IsValidProfileTunnelId(createdTunnelId)
            || !IsValidProfileTunnelId(requestedTunnelId))
            throw new InvalidOperationException(Loc.Get("Remote_DevTunnelInvalidResponse"));

        var requestedSeparator = requestedTunnelId.IndexOf('.');
        var requestedBaseId = requestedSeparator < 0
            ? requestedTunnelId
            : requestedTunnelId[..requestedSeparator];
        var createdSeparator = createdTunnelId.IndexOf('.');
        var createdBaseId = createdSeparator < 0
            ? createdTunnelId
            : createdTunnelId[..createdSeparator];
        if (!string.Equals(createdBaseId, requestedBaseId, StringComparison.Ordinal)
            || requestedSeparator >= 0
            && !string.Equals(createdTunnelId, requestedTunnelId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(Loc.Get("Remote_DevTunnelInvalidResponse"));
        }

        return createdTunnelId;
    }

    internal static void RequireOwnerOnlyAccess(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("accessControlEntries", out var entries)
            || entries.ValueKind != JsonValueKind.Array
            || entries.GetArrayLength() != 0)
        {
            throw new InvalidOperationException(Loc.Get("Remote_DevTunnelUnsafeAccess"));
        }
    }

    internal static string? FindWebOrigin(string line, int port)
    {
        foreach (Match match in Regex.Matches(line, @"https://[a-zA-Z0-9.:-]+/?"))
        {
            if (Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
                && uri.IsDefaultPort
                && Regex.IsMatch(
                    uri.IdnHost,
                    $@"^[a-z0-9-]+-{port}\.[a-z0-9]+\.devtunnels\.ms$",
                    RegexOptions.CultureInvariant))
            {
                return uri.GetLeftPart(UriPartial.Authority);
            }
        }
        return null;
    }

    internal static bool IsAllowedOrigin(string? requestOrigin, string? tunnelOrigin) =>
        tunnelOrigin is { Length: > 0 }
        && (requestOrigin is null
            || string.Equals(requestOrigin, tunnelOrigin, StringComparison.OrdinalIgnoreCase));

    private void Publish(CancellationTokenSource lifetime, RemoteDevTunnelState state)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_lifetime, lifetime) || lifetime.IsCancellationRequested)
                return;
            Volatile.Write(ref _state, state);
        }
        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await _worker.ConfigureAwait(false);
    }
}
