using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.Services.Remote;

internal sealed record RemoteDevTunnelState(
    bool IsStarting = false,
    string? Account = null,
    string? Origin = null,
    string? Error = null,
    string? SetupMessage = null,
    bool RequiresInstallConfirmation = false);

internal sealed class RemoteDevTunnelHost : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly DataStore _dataStore;
    private CancellationTokenSource? _lifetime;
    private TaskCompletionSource<bool>? _installApproval;
    private Task _worker = Task.CompletedTask;
    private RemoteDevTunnelState _state = new();

    public RemoteDevTunnelHost(DataStore dataStore) => _dataStore = dataStore;

    public RemoteDevTunnelState State => Volatile.Read(ref _state);
    public event Action? StateChanged;

    internal static string[] CreateArguments =>
    [
        "create", "--expiration", "30d", "--description", "Lumi private web app",
        "--host-header", "localhost", "--origin-header", "unchanged", "--json"
    ];

    internal static string[] HostArguments(string tunnelId) =>
        ["host", tunnelId, "--host-header", "localhost", "--origin-header", "unchanged"];

    internal static string[] SignInArguments =>
        ["user", "login", "--entra", "--use-browser-auth", "--json"];

    public void Start(int port)
    {
        Stop();
        lock (_gate)
        {
            var lifetime = new CancellationTokenSource();
            _lifetime = lifetime;
            Volatile.Write(ref _state, new RemoteDevTunnelState(IsStarting: true));
            var previous = _worker;
            _worker = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                await RunAsync(port, lifetime).ConfigureAwait(false);
            });
        }
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _lifetime?.Cancel();
            _lifetime = null;
            _installApproval = null;
            Volatile.Write(ref _state, new RemoteDevTunnelState());
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

    private async Task RunAsync(int port, CancellationTokenSource lifetime)
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
            var identity = await EnsureMicrosoftIdentityAsync(
                    (arguments, ct) => RemoteDevTunnelCli.RunAsync(executablePath, arguments, ct),
                    () => Publish(lifetime, new RemoteDevTunnelState(
                        IsStarting: true, SetupMessage: Loc.Get("Remote_DevTunnelSigningIn"))),
                    token)
                .ConfigureAwait(false);
            account = identity.Username;
            Publish(lifetime, new RemoteDevTunnelState(IsStarting: true, Account: account));

            var prepared = await PrepareTunnelAsync(
                    port, identity,
                    (arguments, ct) => RemoteDevTunnelCli.RunAsync(executablePath, arguments, ct),
                    token)
                .ConfigureAwait(false);
            await HostAsync(executablePath, prepared.Registration.TunnelId, port, account, lifetime,
                    prepared.Replaced ? Loc.Get("Remote_DevTunnelReplaced") : null)
                .ConfigureAwait(false);
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
            var message = ex is OperationCanceledException
                ? Loc.Get("Remote_DevTunnelTimeout")
                : ex.Message;
            Trace.TraceWarning($"[Remote] Private Dev Tunnel failed: {message}");
            Publish(lifetime, new RemoteDevTunnelState(Account: account, Error: message));
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
    }

    internal async Task<(RemoteDevTunnelRegistration Registration, bool Replaced)> PrepareTunnelAsync(
        int port,
        (string Username, string ObjectId, string TenantId) identity,
        Func<string[], CancellationToken, Task<string>> runCli,
        CancellationToken cancellationToken)
    {
        var registration = _dataStore.SnapshotRemoteDevTunnel();
        var reused = false;
        var replaced = false;
        string? createdTunnelId = null;
        var portText = port.ToString(CultureInfo.InvariantCulture);
        try
        {
            if (registration is not null)
            {
                if (registration.OwnerObjectId != identity.ObjectId || registration.OwnerTenantId != identity.TenantId)
                    throw new InvalidOperationException(Loc.Get("Remote_DevTunnelSavedAccount"));
                if (registration.Port != port)
                    throw new InvalidOperationException(Loc.Get("Remote_DevTunnelSavedPort", registration.Port));
                if (string.IsNullOrWhiteSpace(registration.TunnelId))
                    throw new InvalidOperationException(Loc.Get("Remote_DevTunnelInvalidResponse"));

                try
                {
                    var json = await runCli(["show", registration.TunnelId, "--json"], cancellationToken)
                        .ConfigureAwait(false);
                    RequireSavedTunnelPort(json, registration);
                    reused = true;
                }
                catch (RemoteDevTunnelNotFoundException ex)
                {
                    Trace.TraceWarning($"[Remote] The saved private tunnel expired or was deleted: {ex.Message}");
                    registration = null;
                    replaced = true;
                }
            }

            if (registration is null)
            {
                using var created = JsonDocument.Parse(
                    await runCli(CreateArguments, cancellationToken).ConfigureAwait(false));
                if (!created.RootElement.TryGetProperty("tunnel", out var tunnel)
                    || !tunnel.TryGetProperty("tunnelId", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || id.GetString() is not { Length: > 0 } value)
                {
                    throw new InvalidOperationException(Loc.Get("Remote_DevTunnelInvalidResponse"));
                }
                createdTunnelId = value;
                registration = new RemoteDevTunnelRegistration
                {
                    TunnelId = value,
                    OwnerObjectId = identity.ObjectId,
                    OwnerTenantId = identity.TenantId,
                    Port = port
                };
                await runCli(
                        ["port", "create", value, "-p", portText, "--protocol", "http",
                            "--host-header", "localhost", "--origin-header", "unchanged", "--json"],
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            RequireOwnerOnlyAccess(
                await runCli(["access", "list", registration.TunnelId, "--json"], cancellationToken)
                    .ConfigureAwait(false));
            RequireOwnerOnlyAccess(
                await runCli(["access", "list", registration.TunnelId, "-p", portText, "--json"], cancellationToken)
                    .ConfigureAwait(false));
            var currentIdentity = ParseMicrosoftIdentity(
                await runCli(["user", "show", "--json"], cancellationToken).ConfigureAwait(false));
            if (currentIdentity.ObjectId != identity.ObjectId || currentIdentity.TenantId != identity.TenantId)
                throw new InvalidOperationException(Loc.Get("Remote_DevTunnelAccountChanged"));

            if (reused)
            {
                await runCli(["update", registration.TunnelId, "--expiration", "30d", "--json"], cancellationToken)
                    .ConfigureAwait(false);
            }
            _dataStore.SetRemoteDevTunnel(registration);
            await _dataStore.SaveAsync(cancellationToken).ConfigureAwait(false);
            return (registration, replaced);
        }
        finally
        {
            if (createdTunnelId is not null && _dataStore.SnapshotRemoteDevTunnel()?.TunnelId != createdTunnelId)
            {
                try
                {
                    await runCli(["delete", createdTunnelId], CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException
                                               or OperationCanceledException or HttpRequestException)
                {
                    Trace.TraceWarning($"[Remote] Could not remove an unregistered private tunnel: {ex.Message}");
                }
            }
        }
    }

    internal static void RequireSavedTunnelPort(string json, RemoteDevTunnelRegistration registration)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("tunnel", out var tunnel)
            || !tunnel.TryGetProperty("tunnelId", out var id)
            || id.ValueKind != JsonValueKind.String
            || id.GetString() != registration.TunnelId
            || !tunnel.TryGetProperty("ports", out var ports)
            || ports.ValueKind != JsonValueKind.Array
            || ports.GetArrayLength() != 1
            || !ports[0].TryGetProperty("portNumber", out var number)
            || number.ValueKind != JsonValueKind.Number
            || !number.TryGetInt32(out var port)
            || port != registration.Port
            || !ports[0].TryGetProperty("protocol", out var protocol)
            || protocol.ValueKind != JsonValueKind.String
            || protocol.GetString() != "http")
        {
            throw new InvalidOperationException(Loc.Get("Remote_DevTunnelChangedPort"));
        }
    }

    private async Task HostAsync(
        string executablePath,
        string tunnelId,
        int port,
        string account,
        CancellationTokenSource lifetime,
        string? setupMessage)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        startup.CancelAfter(TimeSpan.FromSeconds(90));
        using var process = new Process
        {
            StartInfo = RemoteDevTunnelCli.CreateStartInfo(executablePath, HostArguments(tunnelId))
        };
        if (!process.Start())
            throw new InvalidOperationException(Loc.Get("Remote_DevTunnelStartFailed"));
        using var registration = startup.Token.Register(() => RemoteDevTunnelCli.StopProcess(process));
        var errors = RemoteDevTunnelCli.ReadErrorsAsync(process.StandardError, startup.Token);
        string? origin = null;
        var ready = false;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(startup.Token).ConfigureAwait(false) is { } line)
            {
                origin ??= FindWebOrigin(line, port);
                if (!ready && origin is not null
                    && line.Contains("Ready to accept connections", StringComparison.Ordinal))
                {
                    ready = true;
                    startup.CancelAfter(Timeout.InfiniteTimeSpan);
                    Publish(lifetime, new RemoteDevTunnelState(
                        Account: account, Origin: origin, SetupMessage: setupMessage));
                }
            }

            await process.WaitForExitAsync(startup.Token).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                Loc.Get("Remote_DevTunnelStopped", (await errors.ConfigureAwait(false)).Trim()));
        }
        finally
        {
            RemoteDevTunnelCli.StopProcess(process);
        }
    }

    internal static async Task<(string Username, string ObjectId, string TenantId)> EnsureMicrosoftIdentityAsync(
        Func<string[], CancellationToken, Task<string>> runCli,
        Action reportSigningIn,
        CancellationToken cancellationToken)
    {
        var json = await runCli(["user", "show", "--json"], cancellationToken).ConfigureAwait(false);
        using (var document = JsonDocument.Parse(json))
        {
            var root = document.RootElement;
            var signedOut = root.TryGetProperty("status", out var status)
                            && status.GetString() == "Not logged in";
            var github = status.ValueKind == JsonValueKind.String && status.GetString() == "Logged in"
                         && root.TryGetProperty("provider", out var provider) && provider.GetString() == "github";
            if (signedOut || github)
            {
                reportSigningIn();
                await runCli(SignInArguments, cancellationToken).ConfigureAwait(false);
                json = await runCli(["user", "show", "--json"], cancellationToken).ConfigureAwait(false);
            }
        }
        return ParseMicrosoftIdentity(json);
    }

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

internal sealed class RemoteDevTunnelNotFoundException(string message) : InvalidOperationException(message)
{
}
