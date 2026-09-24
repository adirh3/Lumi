using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
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
    bool RequiresInstallConfirmation = false);

internal sealed class RemoteDevTunnelHost : IAsyncDisposable
{
    internal const string TunnelDescription = "Lumi private web app";

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
            "--expiration", "1d", "--description", TunnelDescription,
            "--host-header", "localhost", "--origin-header", "unchanged", "--json"
        ]);
        return arguments.ToArray();
    }

    internal static string[] HostArguments(string tunnelId) =>
        ["host", tunnelId, "--host-header", "localhost", "--origin-header", "unchanged"];

    internal static string[] BrowserSignInArguments =>
        ["user", "login", "--entra", "--use-browser-auth", "--json"];

    internal static string[] IntegratedWindowsSignInArguments =>
        ["user", "login", "--entra", "--use-integrated-windows-auth", "--json"];

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
        Func<string, CancellationToken, Task> persistTunnelId)
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
                await RunAsync(port, requestedTunnelId, persistTunnelId, lifetime).ConfigureAwait(false);
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

    private async Task RunAsync(
        int port,
        string requestedTunnelId,
        Func<string, CancellationToken, Task> persistTunnelId,
        CancellationTokenSource lifetime)
    {
        string? tunnelId = null;
        string? account = null;
        string? executablePath = null;
        try
        {
            var token = lifetime.Token;
            token.ThrowIfCancellationRequested();
            executablePath = await RemoteDevTunnelCli.EnsureAvailableAsync(
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
                    token,
                    preferIntegratedWindowsAuth: OperatingSystem.IsWindows())
                .ConfigureAwait(false);
            account = identity.Username;
            Publish(lifetime, new RemoteDevTunnelState(IsStarting: true, Account: account));

            var existingTunnelId = FindExistingProfileTunnelId(
                await RemoteDevTunnelCli.RunAsync(
                    executablePath, ["list", "--json"], token).ConfigureAwait(false),
                requestedTunnelId);
            if (existingTunnelId is not null)
            {
                await RemoteDevTunnelCli.RunAsync(
                        executablePath, ["delete", existingTunnelId], token)
                    .ConfigureAwait(false);
            }

            var requestedRoute = existingTunnelId ?? requestedTunnelId;

            // Recreate the profile-owned route from scratch so stale relay state cannot survive a
            // reboot. The cluster is pinned, but ACLs and ports are verified anew on every launch.
            using (var created = JsonDocument.Parse(
                       await RemoteDevTunnelCli.RunAsync(
                           executablePath, CreateArguments(requestedRoute), token).ConfigureAwait(false)))
            {
                if (!created.RootElement.TryGetProperty("tunnel", out var tunnel)
                    || !tunnel.TryGetProperty("tunnelId", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || id.GetString() is not { Length: > 0 } value)
                {
                    throw new InvalidOperationException(Loc.Get("Remote_DevTunnelInvalidResponse"));
                }
                tunnelId = RequireExpectedTunnelId(value, requestedRoute);
            }
            await persistTunnelId(tunnelId, token).ConfigureAwait(false);

            await RemoteDevTunnelCli.RunAsync(executablePath,
                    ["port", "create", tunnelId, "-p", port.ToString(CultureInfo.InvariantCulture),
                        "--protocol", "http", "--host-header", "localhost",
                        "--origin-header", "unchanged", "--json"],
                    token)
                .ConfigureAwait(false);
            RequireOwnerOnlyAccess(
                await RemoteDevTunnelCli.RunAsync(
                    executablePath, ["access", "list", tunnelId, "--json"], token).ConfigureAwait(false));
            RequireOwnerOnlyAccess(
                await RemoteDevTunnelCli.RunAsync(executablePath,
                        ["access", "list", tunnelId, "-p", port.ToString(CultureInfo.InvariantCulture), "--json"],
                        token)
                    .ConfigureAwait(false));

            var currentIdentity = ParseMicrosoftIdentity(
                await RemoteDevTunnelCli.RunAsync(executablePath, ["user", "show", "--json"], token).ConfigureAwait(false));
            if (currentIdentity.ObjectId != identity.ObjectId || currentIdentity.TenantId != identity.TenantId)
                throw new InvalidOperationException(Loc.Get("Remote_DevTunnelAccountChanged"));

            await HostAsync(executablePath, tunnelId, port, account, lifetime).ConfigureAwait(false);
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
            if (tunnelId is not null && executablePath is not null)
            {
                try
                {
                    await RemoteDevTunnelCli.RunAsync(executablePath, ["delete", tunnelId], CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException
                                               or OperationCanceledException)
                {
                    // The relay has already stopped; an undeleted private tunnel also expires after a day.
                    Trace.TraceWarning($"[Remote] Could not remove private Dev Tunnel {tunnelId}: {ex.Message}");
                }
            }
            lock (_gate)
            {
                if (ReferenceEquals(_lifetime, lifetime))
                    _lifetime = null;
                lifetime.Dispose();
            }
        }
    }

    private async Task HostAsync(
        string executablePath,
        string tunnelId,
        int port,
        string account,
        CancellationTokenSource lifetime)
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
                    Publish(lifetime, new RemoteDevTunnelState(Account: account, Origin: origin));
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
        CancellationToken cancellationToken,
        bool preferIntegratedWindowsAuth = false)
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
                if (preferIntegratedWindowsAuth)
                {
                    try
                    {
                        await runCli(IntegratedWindowsSignInArguments, cancellationToken).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException) when (!cancellationToken.IsCancellationRequested)
                    {
                        await runCli(BrowserSignInArguments, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        await runCli(BrowserSignInArguments, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    await runCli(BrowserSignInArguments, cancellationToken).ConfigureAwait(false);
                }
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

    internal static string? FindExistingProfileTunnelId(string json, string requestedTunnelId)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("tunnels", out var tunnels)
            || tunnels.ValueKind != JsonValueKind.Array)
        {
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
