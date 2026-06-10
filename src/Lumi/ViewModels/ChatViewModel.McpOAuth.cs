using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using GitHub.Copilot;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

/// <summary>
/// Remote MCP OAuth handling. CLI 1.0.60 surfaces a remote server's sign-in requirement through
/// its needs-auth status (see <c>CheckMcpServerStatusAsync</c>) rather than reliably raising
/// <see cref="McpOauthRequiredEvent"/>, so login is driven from the status path: Lumi starts the
/// login (which returns an authorization URL), opens it in the browser, surfaces progress on the
/// server's composer chip, and monitors until the server reconnects. The
/// <see cref="McpOauthRequiredEvent"/>/<see cref="McpOauthCompletedEvent"/> handlers are retained
/// for SDK/CLI versions that do raise them; they avoid reloading an already-connected server,
/// which would tear down the session the CLI just authenticated.
/// </summary>
public partial class ChatViewModel
{
    private readonly object _mcpOauthGate = new();
    private readonly HashSet<string> _mcpOauthInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _mcpOauthRequestServer = new(StringComparer.Ordinal);

    internal static readonly bool McpOauthDebugEnabled =
        string.Equals(Environment.GetEnvironmentVariable("LUMI_MCP_OAUTH_DEBUG"), "1", StringComparison.Ordinal);

    private static readonly object _mcpOauthDebugGate = new();

    internal static void LogMcpOauthDebug(string message)
    {
        if (!McpOauthDebugEnabled)
            return;
        try
        {
            var line = $"{DateTime.UtcNow:HH:mm:ss.fff} [pid {Environment.ProcessId}] {message}{Environment.NewLine}";
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lumi-oauth-debug.log");
            lock (_mcpOauthDebugGate)
                System.IO.File.AppendAllText(path, line);
        }
        catch { /* diagnostics must never throw */ }
    }

    private async Task HandleMcpOauthRequiredAsync(CopilotSession session, McpOauthRequiredData? data, Guid chatId)
    {
        var serverName = data?.ServerName;
        LogMcpOauthDebug($"HandleMcpOauthRequiredAsync ENTER server={serverName ?? "(null)"} req={data?.RequestId ?? "(null)"}");
        if (string.IsNullOrWhiteSpace(serverName))
            return;

        if (!string.IsNullOrWhiteSpace(data!.RequestId))
        {
            lock (_mcpOauthGate)
                _mcpOauthRequestServer[data.RequestId] = serverName!;
        }

        await InitiateMcpOauthLoginAsync(session, serverName!, chatId).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts interactive OAuth sign-in for a remote MCP server: asks the CLI for an
    /// authorization URL and opens it in the browser, reflecting progress on the server's
    /// composer chip. Safe to call from either the <see cref="McpOauthRequiredEvent"/> handler
    /// or the needs-auth status poll — concurrent calls for the same server are de-duplicated.
    /// </summary>
    /// <remarks>
    /// Copilot CLI 1.0.60 surfaces a remote server's auth requirement via its <c>needs-auth</c>
    /// status (observed through <c>Mcp.ListAsync</c>) rather than reliably raising
    /// <see cref="McpOauthRequiredEvent"/>, so the status poll is the primary trigger and this
    /// method must not assume an event ever arrives.
    /// </remarks>
    private async Task InitiateMcpOauthLoginAsync(CopilotSession session, string serverName, Guid chatId)
    {
        lock (_mcpOauthGate)
        {
            // De-dupe: the same server can need auth across several sessions/triggers at once.
            if (!_mcpOauthInFlight.Add(serverName))
                return;
        }

        try
        {
            LogMcpOauthDebug($"InitiateMcpOauthLoginAsync calling LoginAsync for {serverName}");
            Dispatcher.UIThread.Post(() => SetMcpChipMessage(chatId, serverName,
                $"Sign-in required — opening your browser to authenticate '{serverName}'…"));

            var result = await session.Rpc.Mcp.Oauth.LoginAsync(
                serverName,
                forceReauth: null,
                clientName: "Lumi",
                callbackSuccessMessage: "Authentication complete. You can return to Lumi.",
                CancellationToken.None).ConfigureAwait(false);

            var url = result?.AuthorizationUrl;
            LogMcpOauthDebug($"LoginAsync returned for {serverName}: url={(string.IsNullOrWhiteSpace(url) ? "(empty)" : url)}");
            if (string.IsNullOrWhiteSpace(url) || !TryOpenUrl(url!))
            {
                lock (_mcpOauthGate)
                    ReleaseOauthServerLocked(serverName);
                Dispatcher.UIThread.Post(() => SetMcpChipMessage(chatId, serverName,
                    string.IsNullOrWhiteSpace(url)
                        ? $"Sign-in for '{serverName}' did not return a login URL."
                        : $"Sign-in for '{serverName}' was blocked: the server returned an unsupported login URL."));
                return;
            }

            // The browser is open. The CLI does not reliably raise McpOauthCompleted for this
            // flow, so poll the server status ourselves: once it reconnects we reload to surface
            // its tools and clear the chip. The monitor owns the dedup latch from here.
            _ = MonitorMcpOauthCompletionAsync(session, serverName, chatId);
        }
        catch (Exception ex)
        {
            LogMcpOauthDebug($"InitiateMcpOauthLoginAsync EXCEPTION for {serverName}: {ex}");
            lock (_mcpOauthGate)
                ReleaseOauthServerLocked(serverName);
            Dispatcher.UIThread.Post(() => SetMcpChipMessage(chatId, serverName,
                $"Sign-in failed for '{serverName}': {ex.Message}"));
        }
    }

    /// <summary>
    /// After a browser sign-in is launched, waits for the server to come back online and clears
    /// the sign-in chip. Copilot CLI 1.0.60 reconnects the server itself once the browser callback
    /// stores the token, so the tools are live for the next turn without any reload — issuing a
    /// reload here would instead tear down the freshly authenticated connection. Always releases
    /// the dedup latch when it finishes.
    /// </summary>
    private async Task MonitorMcpOauthCompletionAsync(CopilotSession session, string serverName, Guid chatId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

                McpServerStatus? status;
                try
                {
                    var list = await session.Rpc.Mcp.ListAsync(CancellationToken.None).ConfigureAwait(false);
                    status = list?.Servers?
                        .FirstOrDefault(s => string.Equals(s.Name, serverName, StringComparison.OrdinalIgnoreCase))?
                        .Status;
                }
                catch (Exception ex)
                {
                    LogMcpOauthDebug($"monitor {serverName} list error: {ex.Message}");
                    continue;
                }

                LogMcpOauthDebug($"monitor {serverName} status={status}");

                if (status == McpServerStatus.Connected)
                {
                    Dispatcher.UIThread.Post(() => ClearMcpChipError(chatId, serverName));
                    return;
                }

                // NeedsAuth or no status yet: sign-in is still in flight, keep waiting.
                if (status is null || status == McpServerStatus.NeedsAuth)
                    continue;

                // Any other terminal state (e.g. Failed): surface it and stop.
                Dispatcher.UIThread.Post(() => SetMcpChipMessage(chatId, serverName,
                    $"Sign-in for '{serverName}' did not complete (status: {status})."));
                return;
            }

            LogMcpOauthDebug($"monitor {serverName} timed out waiting for reconnect");
        }
        finally
        {
            lock (_mcpOauthGate)
                ReleaseOauthServerLocked(serverName);
        }
    }

    /// <summary>
    /// Releases the dedup latch and any request-id mappings for a server. Must be called while
    /// holding <see cref="_mcpOauthGate"/>.
    /// </summary>
    private void ReleaseOauthServerLocked(string serverName)
    {
        _mcpOauthInFlight.Remove(serverName);

        var staleRequestIds = _mcpOauthRequestServer
            .Where(pair => string.Equals(pair.Value, serverName, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .ToList();
        foreach (var requestId in staleRequestIds)
            _mcpOauthRequestServer.Remove(requestId);
    }

    private async Task HandleMcpOauthCompletedAsync(CopilotSession session, McpOauthCompletedData? data, Guid chatId)
    {
        string? serverName = null;
        lock (_mcpOauthGate)
        {
            if (data?.RequestId is { Length: > 0 } reqId
                && _mcpOauthRequestServer.TryGetValue(reqId, out var name))
            {
                serverName = name;
            }

            if (serverName is not null)
                ReleaseOauthServerLocked(serverName);
        }

        // When login was driven by the needs-auth status poll (the CLI 1.0.60 path) the request-id
        // mapping was never populated, so the server can't be identified here —
        // MonitorMcpOauthCompletionAsync already owns reconnect detection and chip clearing for it.
        if (serverName is null)
            return;

        // The CLI auto-reconnects the server itself once the OAuth callback lands. Reloading a
        // server that is already connected tears down that fresh session (see
        // MonitorMcpOauthCompletionAsync), so only reload if it did NOT come back on its own.
        McpServerStatus? status = null;
        try
        {
            var list = await session.Rpc.Mcp.ListAsync(CancellationToken.None).ConfigureAwait(false);
            status = list?.Servers?
                .FirstOrDefault(s => string.Equals(s.Name, serverName, StringComparison.OrdinalIgnoreCase))?
                .Status;
        }
        catch (Exception ex)
        {
            LogMcpOauthDebug($"completed {serverName} list error: {ex.Message}");
        }

        if (status != McpServerStatus.Connected)
        {
            try { await session.Rpc.Mcp.ReloadAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* best effort — the chip status check will resurface a persistent failure */ }
        }

        Dispatcher.UIThread.Post(() => ClearMcpChipError(chatId, serverName));
    }

    private void SetMcpChipMessage(Guid chatId, string serverName, string message)
    {
        if (CurrentChat?.Id != chatId)
            return;

        var chip = ActiveMcpChips.OfType<StrataComposerChip>()
            .FirstOrDefault(c => string.Equals(c.Name, serverName, StringComparison.OrdinalIgnoreCase));
        if (chip is null)
            return;

        var index = ActiveMcpChips.IndexOf(chip);
        if (index >= 0)
            ActiveMcpChips[index] = new StrataComposerChip(chip.Name, chip.Glyph, ErrorMessage: message);
    }

    private void ClearMcpChipError(Guid chatId, string serverName)
    {
        if (CurrentChat?.Id != chatId)
            return;

        var chip = ActiveMcpChips.OfType<StrataComposerChip>()
            .FirstOrDefault(c => string.Equals(c.Name, serverName, StringComparison.OrdinalIgnoreCase));
        if (chip is null || !chip.HasError)
            return;

        var index = ActiveMcpChips.IndexOf(chip);
        if (index >= 0)
            ActiveMcpChips[index] = new StrataComposerChip(
                chip.Name, chip.Glyph, ErrorMessage: null, chip.SecondaryText, chip.Value);
    }

    /// <summary>
    /// Opens an OAuth authorization URL in the user's browser. The URL originates from a
    /// third-party MCP server's OAuth metadata, so it is restricted to http/https to prevent
    /// <c>UseShellExecute</c> from launching local executables, UNC paths, or dangerous URI
    /// handlers (e.g. <c>file:</c>, <c>ms-msdt:</c>). Returns false if the URL is rejected or
    /// the shell cannot open it.
    /// </summary>
    private static bool TryOpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
