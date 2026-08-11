using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.ViewModels;

namespace Lumi.Services.Remote;

/// <summary>
/// Exposes this Lumi desktop to the Lumi mobile app over the local network.
/// </summary>
/// <remarks>
/// Security model, in order of defence:
/// <list type="number">
/// <item>Opt-in. The listener only starts when <see cref="UserSettings.RemoteAccessEnabled"/> is on.</item>
/// <item>Pairing. A device must present a short-lived code that the desktop shows on screen, and
/// exchanges it once for a long-lived random token.</item>
/// <item>Token auth. Every non-handshake request needs that token, compared in constant time.</item>
/// <item>Tailscale by default. Plain private-LAN callers are accepted only after the user explicitly
/// enables unencrypted LAN access in Settings.</item>
/// </list>
/// </remarks>
public sealed class LumiRemoteServer : IAsyncDisposable
{
    internal const int PairingFailedAttemptLimit = 5;
    internal const int MaxTrackedCommandRequestsPerDevice = 512;
    internal const int MaxTrackedCommandRequestsTotal = 4096;
    internal static readonly TimeSpan MobileUploadRetention = TimeSpan.FromDays(7);
    internal static readonly TimeSpan CommandResultRetention = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan IncompleteCommandRetention = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan TailscaleRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly DataStore _dataStore;
    private readonly MainViewModel _main;
    private readonly RemoteCommandRouter _router;
    private readonly RemoteHttpListener _listener;
    private readonly Func<IReadOnlySet<IPAddress>> _tailscaleAddressProvider;
    private readonly ConcurrentDictionary<Guid, RemoteEventClient> _streams = new();
    private readonly CancellationTokenSource _cts = new();
    private string _instanceId = "";
    private readonly object _pairingGate = new();
    private readonly object _deviceAuthorizationGate = new();
    private readonly object _commandGate = new();
    private readonly SemaphoreSlim _uploadGate = new(1, 1);
    private readonly Dictionary<CommandDedupKey, CommandDedupEntry> _commandRequests = [];
    private IReadOnlySet<IPAddress> _tailscaleAddresses = new HashSet<IPAddress>();
    private readonly SemaphoreSlim _networkPolicyGate = new(1, 1);

    private RemoteEventHub? _hub;
    private RemoteDiscoveryResponder? _discovery;
    private Timer? _tailscaleRefreshTimer;
    private FileStream? _serverOwnershipLock;
    private readonly bool _ownsPersistentSecurityState;
    private string? _pairingCode;
    private DateTimeOffset _pairingCodeExpiresAt;
    private int _pairingFailedAttempts;
    private volatile bool _securityStateReady;
    private bool _disposed;

    private sealed record TranscriptCapture(
        Chat Chat,
        IReadOnlyList<ChatMessage>? LoadedMessages,
        RemoteChatStatus Status,
        bool ShowReasoning,
        bool ShowToolCalls,
        long Revision,
        string RevisionEpoch,
        string WorkingDirectory,
        IReadOnlySet<string> RunningBackgroundToolCallIds);

    private sealed record ActivityCapture(
        Chat Chat,
        IReadOnlyList<ChatMessage>? LoadedMessages,
        string WorkingDirectory,
        IReadOnlySet<string> RunningBackgroundToolCallIds);

    private sealed record FileMessageCapture(Chat? Chat, string? Content);

    public LumiRemoteServer(DataStore dataStore, MainViewModel main)
        : this(dataStore, main, GetVerifiedTailscaleAddresses)
    {
    }

    internal LumiRemoteServer(
        DataStore dataStore,
        MainViewModel main,
        Func<IReadOnlySet<IPAddress>> tailscaleAddressProvider)
    {
        _dataStore = dataStore;
        _main = main;
        _tailscaleAddressProvider = tailscaleAddressProvider;
        _router = new RemoteCommandRouter(dataStore, main);
        _listener = new RemoteHttpListener(HandleAsync, PreflightRequest);
        _ownsPersistentSecurityState = !dataStore.UsesPersistentStorage || TryAcquireServerOwnership();
        _securityStateReady = !dataStore.UsesPersistentStorage;
    }

    public int Port { get; private set; }
    internal RemoteEventHub? EventHub => _hub;

    public bool IsRunning { get; private set; }

    public bool CanManageSecurityState => _ownsPersistentSecurityState;
    public bool IsSecurityStateReady => _securityStateReady;
    internal IReadOnlySet<IPAddress> VerifiedTailscaleAddresses => Volatile.Read(ref _tailscaleAddresses);

    internal bool HasTrackedCommandRequest(string deviceId, string requestId)
    {
        lock (_commandGate)
            return _commandRequests.ContainsKey(new CommandDedupKey(deviceId, requestId));
    }

    /// <summary>The pairing code currently displayed to the user, if one is active.</summary>
    public string? ActivePairingCode
    {
        get => GetPairingDisplayState(DateTimeOffset.UtcNow).Code;
    }

    internal (string? Code, DateTimeOffset? ExpiresAt) GetPairingDisplayState(DateTimeOffset now)
    {
        lock (_pairingGate)
        {
            var code = GetActivePairingCodeLocked(now);
            return (code, code is null ? null : _pairingCodeExpiresAt);
        }
    }

    /// <summary>Every currently allowed <c>http://ip:port</c> address a phone could reach.</summary>
    public IReadOnlyList<string> ListenAddresses =>
        GetLocalAddresses()
            .Where(address =>
                _dataStore.Data.Settings.RemoteAllowInsecureLan
                || _tailscaleAddresses.Contains(NormalizeAddress(IPAddress.Parse(address))))
            .Select(address => $"http://{address}:{Port}")
            .ToList();

    public event Action? StateChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!_ownsPersistentSecurityState || _disposed)
            return;

        await _dataStore.RefreshRemoteSecurityFromDiskAsync(cancellationToken).ConfigureAwait(false);
        _securityStateReady = true;
        StateChanged?.Invoke();

        if (_dataStore.Data.Settings.RemoteAccessEnabled)
            Start();
    }

    public void Start()
    {
        if (IsRunning || _disposed)
            return;

        if (!_ownsPersistentSecurityState)
            return;
        if (!_securityStateReady)
            return;

        _instanceId = Guid.NewGuid().ToString("N");
        var configured = _dataStore.Data.Settings.RemoteAccessPort;
        var port = configured > 0 ? configured : RemoteProtocol.DefaultPort;
        EnsurePrivateDirectory(GetMobileUploadRoot());

        try
        {
            try
            {
                _listener.Start(port);
            }
            catch (SocketException) when (configured <= 0)
            {
                // The per-profile ownership lock rules out another Lumi process. An unrelated
                // process may still own the default port, so fall back to an advertised ephemeral one.
                _listener.Start(0);
            }
        }
        catch
        {
            throw;
        }

        Port = _listener.Port;
        _hub = new RemoteEventHub(
            _dataStore,
            _main,
            () => _main.ChatVM.AvailableModels.ToList(),
            _instanceId);
        RefreshDiscovery();
        IsRunning = true;
        _tailscaleRefreshTimer = new Timer(
            _ => _ = RefreshTailscaleAddressesPeriodicallyAsync(),
            null,
            TailscaleRefreshInterval,
            TailscaleRefreshInterval);
        StateChanged?.Invoke();
        _ = InitializeRuntimeStateAsync();
    }

    public void Stop()
    {
        if (!IsRunning)
            return;

        IsRunning = false;
        _tailscaleRefreshTimer?.Dispose();
        _tailscaleRefreshTimer = null;
        _discovery?.Dispose();
        _discovery = null;

        foreach (var stream in _streams.Values)
            stream.Dispose();
        _streams.Clear();

        _hub?.Dispose();
        _hub = null;
        _listener.Dispose();
        StateChanged?.Invoke();
    }

    public void RefreshNetworkPolicy()
    {
        if (!IsRunning)
            return;

        StateChanged?.Invoke();
        _ = RefreshNetworkPolicyAsync();
    }

    private async Task InitializeRuntimeStateAsync()
    {
        try
        {
            await Task.WhenAll(
                    RefreshTailscaleAddressesAsync(_cts.Token),
                    Task.Run(
                        () => CleanupStaleMobileUploads(GetMobileUploadRoot(), DateTimeOffset.UtcNow),
                        _cts.Token))
                .ConfigureAwait(false);

            if (IsRunning && !_disposed)
                StateChanged?.Invoke();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Remote] Background initialization failed: {ex.Message}");
        }
    }

    private async Task RefreshNetworkPolicyAsync()
    {
        await _networkPolicyGate.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            await RefreshTailscaleAddressesAsync(_cts.Token).ConfigureAwait(false);
            if (!IsRunning || _disposed)
                return;

            RefreshDiscovery();
            foreach (var stream in _streams.Values)
                stream.Dispose();
            StateChanged?.Invoke();
        }

        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        finally
        {
            _networkPolicyGate.Release();
        }
    }

    internal async Task RefreshTailscaleAddressesNowAsync(CancellationToken cancellationToken = default)
    {
        await _networkPolicyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var changed = await RefreshTailscaleAddressesAsync(cancellationToken).ConfigureAwait(false);
            if (!changed || !IsRunning || _disposed)
                return;

            foreach (var stream in _streams.Values)
                stream.Dispose();
            StateChanged?.Invoke();
        }
        finally
        {
            _networkPolicyGate.Release();
        }
    }

    private async Task RefreshTailscaleAddressesPeriodicallyAsync()
    {
        if (!IsRunning || _disposed)
            return;

        try
        {
            await RefreshTailscaleAddressesNowAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Remote] Periodic Tailscale refresh failed: {ex.Message}");
        }
    }

    private void RefreshDiscovery()
    {
        _discovery?.Dispose();
        _discovery = null;
        if (!_dataStore.Data.Settings.RemoteAllowInsecureLan)
            return;

        _discovery = new RemoteDiscoveryResponder(
            _instanceId,
            () => Port,
            () => _dataStore.Data.Settings.UserName ?? "");
        _discovery.Start();
    }

    /// <summary>Issues (or reuses) the short-lived code the user types on their phone.</summary>
    public string BeginPairing()
    {
        string code;
        lock (_pairingGate)
        {
            if (GetActivePairingCodeLocked(DateTimeOffset.UtcNow) is { } active)
                return active;

            code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
            _pairingCode = code;
            _pairingCodeExpiresAt = DateTimeOffset.UtcNow + RemoteProtocol.PairingCodeLifetime;
            _pairingFailedAttempts = 0;
        }

        StateChanged?.Invoke();
        return code;
    }

    public void CancelPairing()
    {
        lock (_pairingGate)
        {
            _pairingCode = null;
            _pairingCodeExpiresAt = default;
            _pairingFailedAttempts = 0;
        }

        StateChanged?.Invoke();
    }

    // ── Request handling ────────────────────────────────────────────────────────────────────

    private RemoteHttpPreflightResult PreflightRequest(
        RemoteHttpRequest request,
        EndPoint? remoteEndPoint,
        EndPoint? localEndPoint)
    {
        if (!IsAllowedCaller(
                remoteEndPoint,
                localEndPoint,
                _dataStore.Data.Settings.RemoteAllowInsecureLan,
                _tailscaleAddresses))
        {
            return RemoteHttpPreflightResult.Reject(
                403,
                "Use Tailscale, or explicitly enable unencrypted LAN access in Lumi Settings.");
        }

        var path = request.Path.TrimEnd('/');
        if (path.Length == 0)
            path = "/";

        if (path is RemoteProtocol.Routes.Hello or RemoteProtocol.Routes.Pair
            || string.Equals(request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteHttpPreflightResult.Allow(RemoteProtocol.MaxHandshakeJsonBytes);
        }

        if (!TryAuthorize(request, out _))
            return RemoteHttpPreflightResult.Reject(401, "Pair this device with Lumi first.");

        if (path == RemoteProtocol.Routes.Upload)
        {
            if (request.ContentLength is < 0 or > RemoteProtocol.MaxUploadBytes)
                return RemoteHttpPreflightResult.Reject(413, "That file is too large to send.");
            if (request.Header(RemoteProtocol.UploadFileNameHeader) is not { Length: > 0 } encodedName)
                return RemoteHttpPreflightResult.Reject(400, "A file name is required.");
            if (encodedName.Length > 8 * 1024)
                return RemoteHttpPreflightResult.Reject(400, "The file name is too large.");
            return RemoteHttpPreflightResult.Allow(RemoteProtocol.MaxUploadBytes, streamBody: true);
        }

        return RemoteHttpPreflightResult.Allow(RemoteHttpListener.OrdinaryRequestBodyLimitBytes);
    }

    private async Task HandleAsync(RemoteHttpContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (string.Equals(context.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                await context.WriteTextAsync("", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!IsAllowedCaller(
                    context.RemoteEndPoint,
                    context.LocalEndPoint,
                    _dataStore.Data.Settings.RemoteAllowInsecureLan,
                    _tailscaleAddresses))
            {
                await WriteErrorAsync(
                        context,
                        403,
                        "Use Tailscale, or explicitly enable unencrypted LAN access in Lumi Settings.",
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var path = context.Request.Path.TrimEnd('/');
            if (path.Length == 0)
                path = "/";

            switch (path)
            {
                case RemoteProtocol.Routes.Hello:
                    await HandleHelloAsync(context, cancellationToken).ConfigureAwait(false);
                    return;
            }

            if (!_securityStateReady)
            {
                await WriteErrorAsync(context, 503, "Lumi is still loading phone security state.", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (path == RemoteProtocol.Routes.Pair)
            {
                await HandlePairAsync(context, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!TryAuthorize(context.Request, out var device))
            {
                await WriteErrorAsync(context, 401, "Pair this device with Lumi first.", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            _dataStore.TouchRemotePairedDevice(device.DeviceId, DateTimeOffset.Now);

            switch (path)
            {
                case RemoteProtocol.Routes.Snapshot:
                    await HandleSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
                    return;
                case RemoteProtocol.Routes.Chats:
                    await HandleChatsAsync(context, cancellationToken).ConfigureAwait(false);
                    return;
                case RemoteProtocol.Routes.LibraryItem:
                    await HandleLibraryItemAsync(context, cancellationToken).ConfigureAwait(false);
                    return;
                case RemoteProtocol.Routes.Transcript:
                    await HandleTranscriptAsync(context, cancellationToken).ConfigureAwait(false);
                    return;
                case RemoteProtocol.Routes.Activity:
                    await HandleActivityAsync(context, cancellationToken).ConfigureAwait(false);
                    return;
                case RemoteProtocol.Routes.File:
                    await HandleFileAsync(context, cancellationToken).ConfigureAwait(false);
                    return;
                case RemoteProtocol.Routes.Command:
                    await HandleCommandAsync(context, device, cancellationToken).ConfigureAwait(false);
                    return;
                case RemoteProtocol.Routes.Upload:
                    await HandleUploadAsync(context, device, cancellationToken).ConfigureAwait(false);
                    return;
                case RemoteProtocol.Routes.Subscription:
                    await HandleSubscriptionAsync(context, device, cancellationToken).ConfigureAwait(false);
                    return;
                case RemoteProtocol.Routes.Events:
                    await HandleEventsAsync(context, device, cancellationToken).ConfigureAwait(false);
                    return;
                default:
                    await WriteErrorAsync(context, 404, $"Unknown route '{path}'.", cancellationToken)
                        .ConfigureAwait(false);
                    return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceWarning($"[Remote] Request failed: {ex}");
            try
            {
#if DEBUG
                await WriteErrorAsync(context, 500, ex.ToString(), cancellationToken).ConfigureAwait(false);
#else
                await WriteErrorAsync(context, 500, ex.Message, cancellationToken).ConfigureAwait(false);
#endif
            }
            catch (Exception writeException) when (writeException is System.IO.IOException or ObjectDisposedException)
            {
                // Client already gone.
            }
        }
    }

    private async Task HandleHelloAsync(RemoteHttpContext context, CancellationToken cancellationToken)
    {
        var hello = new RemoteHello
        {
            Capabilities = [.. RemoteProtocol.Capabilities.Server],
            InstanceId = _instanceId,
            HostName = Environment.MachineName,
            UserName = _dataStore.Data.Settings.UserName ?? "",
            AppVersion = typeof(LumiRemoteServer).Assembly.GetName().Version?.ToString() ?? "",
            IsPaired = TryAuthorize(context.Request, out _)
        };

        await context.WriteJsonAsync(
            JsonSerializer.Serialize(hello, RemoteJsonContext.Default.RemoteHello),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task HandlePairAsync(RemoteHttpContext context, CancellationToken cancellationToken)
    {
        var request = Deserialize(context.Request.Body, RemoteJsonContext.Default.RemotePairRequest);
        var response = new RemotePairResponse
        {
            HostName = Environment.MachineName,
            UserName = _dataStore.Data.Settings.UserName ?? ""
        };
        var status = 401;
        var pairingStateChanged = false;

        if (request is null || string.IsNullOrWhiteSpace(request.DeviceId))
        {
            response.Error = "deviceId is required.";
        }
        else
        {
            switch (TryConsumePairingCode(request.Code))
            {
                case PairingCodeResult.NoActiveCode:
                    response.Error =
                        "No pairing code is active. Open Settings → Phone on your PC and tap Pair a device.";
                    break;
                case PairingCodeResult.Incorrect:
                    response.Error = "That pairing code is not correct.";
                    break;
                case PairingCodeResult.AttemptsExhausted:
                    response.Error = "Too many incorrect pairing attempts. Generate a new code on your PC.";
                    status = 429;
                    pairingStateChanged = true;
                    break;
                case PairingCodeResult.Accepted:
                {
                    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                    lock (_deviceAuthorizationGate)
                    {
                        DisposeDeviceStreamsLocked(request.DeviceId);
                        _dataStore.UpsertRemotePairedDevice(new RemotePairedDevice
                        {
                            DeviceId = request.DeviceId,
                            DeviceName = string.IsNullOrWhiteSpace(request.DeviceName) ? "Phone" : request.DeviceName,
                            Token = token,
                            PairedAt = DateTimeOffset.Now,
                            LastSeenAt = DateTimeOffset.Now
                        });
                    }
                    await _dataStore.SaveAsync(cancellationToken).ConfigureAwait(false);

                    response.Ok = true;
                    response.Token = token;
                    status = 200;
                    pairingStateChanged = true;
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        if (pairingStateChanged)
            StateChanged?.Invoke();

        await context.WriteJsonAsync(
            JsonSerializer.Serialize(response, RemoteJsonContext.Default.RemotePairResponse),
            cancellationToken,
            status).ConfigureAwait(false);
    }

    private async Task HandleSnapshotAsync(RemoteHttpContext context, CancellationToken cancellationToken)
    {
        var snapshot = await Dispatcher.UIThread.InvokeAsync(() =>
            RemoteProjector.BuildSnapshot(_dataStore, _main, _main.ChatVM.AvailableModels.ToList()));
        var json = JsonSerializer.Serialize(snapshot, RemoteJsonContext.Default.RemoteSnapshot);
        if (Encoding.UTF8.GetByteCount(json) > RemoteProtocol.MaxSnapshotJsonBytes)
        {
            await WriteErrorAsync(context, 507, "The mobile snapshot exceeds the protocol limit.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await context.WriteJsonAsync(
            json,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleChatsAsync(RemoteHttpContext context, CancellationToken cancellationToken)
    {
        var offset = ParseBoundedInt(context.Request.QueryValue("offset"), 0, 0, int.MaxValue);
        var limit = ParseBoundedInt(
            context.Request.QueryValue("limit"),
            RemoteProtocol.ChatPageSize,
            1,
            RemoteProtocol.MaxChatPageSize);
        var query = context.Request.QueryValue("q");
        var projectId = Guid.TryParse(context.Request.QueryValue("projectId"), out var parsedProjectId)
            ? parsedProjectId
            : (Guid?)null;
        var page = await Dispatcher.UIThread.InvokeAsync(() =>
            RemoteProjector.BuildChatPage(_dataStore, _main, offset, limit, query, projectId));
        await context.WriteJsonAsync(
            JsonSerializer.Serialize(page, RemoteJsonContext.Default.RemoteChatPage),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleLibraryItemAsync(RemoteHttpContext context, CancellationToken cancellationToken)
    {
        var resource = (context.Request.QueryValue("resource") ?? "").Trim().ToLowerInvariant();
        var identifier = context.Request.QueryValue("identifier");
        if (!Guid.TryParse(identifier, out var id))
        {
            await WriteErrorAsync(context, 400, "identifier is required.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var item = await Dispatcher.UIThread.InvokeAsync(() => BuildLibraryItem(resource, id));
        if (item is null)
        {
            await WriteErrorAsync(context, 404, "Library item not found.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var json = JsonSerializer.Serialize(item, RemoteJsonContext.Default.RemoteLibraryItem);
        if (Encoding.UTF8.GetByteCount(json) > RemoteProtocol.MaxLibraryItemJsonBytes)
        {
            await WriteErrorAsync(context, 413, "That library item is too large for mobile editing.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await context.WriteJsonAsync(json, cancellationToken).ConfigureAwait(false);
    }

    private RemoteLibraryItem? BuildLibraryItem(string resource, Guid id) => resource switch
    {
        RemoteProtocol.Resources.Projects => _dataStore.Data.Projects
            .Where(item => item.Id == id)
            .Select(item => new RemoteLibraryItem
            {
                Resource = resource,
                Identifier = item.Id.ToString(),
                Name = item.Name,
                Body = item.Instructions,
                WorkingDirectory = item.WorkingDirectory
            })
            .FirstOrDefault(),
        RemoteProtocol.Resources.Skills => _dataStore.Data.Skills
            .Where(item => item.Id == id)
            .Select(item => new RemoteLibraryItem
            {
                Resource = resource,
                Identifier = item.Id.ToString(),
                Name = item.Name,
                Description = item.Description,
                Body = item.Content,
                Glyph = item.IconGlyph
            })
            .FirstOrDefault(),
        RemoteProtocol.Resources.Lumis => _dataStore.Data.Agents
            .Where(item => item.Id == id)
            .Select(item => new RemoteLibraryItem
            {
                Resource = resource,
                Identifier = item.Id.ToString(),
                Name = item.Name,
                Description = item.Description,
                Body = item.SystemPrompt,
                Glyph = item.IconGlyph
            })
            .FirstOrDefault(),
        RemoteProtocol.Resources.Memories => _dataStore.Data.Memories
            .Where(item => item.Id == id)
            .Select(item => new RemoteLibraryItem
            {
                Resource = resource,
                Identifier = item.Id.ToString(),
                Name = item.Key,
                Description = item.Category,
                Body = item.Content
            })
            .FirstOrDefault(),
        RemoteProtocol.Resources.Mcps => _dataStore.Data.McpServers
            .Where(item => item.Id == id)
            .Select(item => new RemoteLibraryItem
            {
                Resource = resource,
                Identifier = item.Id.ToString(),
                Name = item.Name,
                Description = item.Description,
                Body = item.Command ?? item.Url
            })
            .FirstOrDefault(),
        RemoteProtocol.Resources.Jobs => _dataStore.SnapshotBackgroundJobs()
            .Where(item => item.Id == id)
            .Select(item => new RemoteLibraryItem
            {
                Resource = resource,
                Identifier = item.Id.ToString(),
                Name = item.Name,
                Description = item.Description,
                Body = item.Prompt
            })
            .FirstOrDefault(),
        _ => null
    };

    private async Task HandleFileAsync(RemoteHttpContext context, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.Request.QueryValue("chatId"), out var chatId)
            || !Guid.TryParse(context.Request.QueryValue("messageId"), out var messageId))
        {
            await WriteErrorAsync(context, 400, "chatId and messageId are required.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var capture = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var chat = _dataStore.Data.Chats.FirstOrDefault(candidate => candidate.Id == chatId);
            if (chat is null)
                return new FileMessageCapture(null, null);

            var owner = RemoteProjector.ResolveChatOwner(_main, chatId);
            var content = owner?.CurrentChat?.Id == chatId
                ? owner.Messages
                    .Select(item => item.Message)
                    .FirstOrDefault(message =>
                        message.Id == messageId
                        && string.Equals(message.ToolName, "announce_file", StringComparison.OrdinalIgnoreCase))
                    ?.Content
                : chat.Messages.FirstOrDefault(message =>
                    message.Id == messageId
                    && string.Equals(message.ToolName, "announce_file", StringComparison.OrdinalIgnoreCase))?.Content;
            return new FileMessageCapture(chat, content);
        });

        if (capture.Chat is null)
        {
            await WriteErrorAsync(context, 404, "Chat not found.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var content = capture.Content;
        if (content is null && capture.Chat.Messages.Count == 0)
        {
            var persisted = await _dataStore
                .ReadPersistedChatMessagesAsync(chatId, cancellationToken)
                .ConfigureAwait(false);
            content = persisted.FirstOrDefault(message =>
                message.Id == messageId
                && string.Equals(message.ToolName, "announce_file", StringComparison.OrdinalIgnoreCase))?.Content;
        }

        var path = RemoteProjector.ExtractJsonField(content, "filePath");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            await WriteErrorAsync(context, 404, "That produced file is no longer available.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var file = new FileInfo(path);
        if (file.Length > RemoteProtocol.MaxDownloadBytes)
        {
            await WriteErrorAsync(context, 413, "That produced file is too large to open on mobile.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await using var stream = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await context.WriteFileAsync(stream, file.Length, file.Name, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleTranscriptAsync(RemoteHttpContext context, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.Request.QueryValue("chatId"), out var chatId))
        {
            await WriteErrorAsync(context, 400, "chatId is required.", cancellationToken).ConfigureAwait(false);
            return;
        }

        int? beforeMessageIndex = null;
        if (context.Request.QueryValue("beforeMessageIndex") is { } beforeValue)
        {
            if (!int.TryParse(
                    beforeValue,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedBefore)
                || parsedBefore <= 0)
            {
                await WriteErrorAsync(
                    context,
                    400,
                    "beforeMessageIndex must be a positive exclusive raw-message index.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            beforeMessageIndex = parsedBefore;
        }

        var maxMessages = ResolveTranscriptWindowLimit(
            context.Request.QueryValue("limit"),
            beforeMessageIndex);
        var compact = string.Equals(
            context.Request.QueryValue("mode"),
            "compact",
            StringComparison.OrdinalIgnoreCase);

        // Capture mutable desktop state on the UI thread, then do the potentially large persisted
        // read and bounded projection away from it. A live/detached owner still wins so streaming and
        // unsaved messages remain visible without disturbing any desktop surface.
        var capture = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var chat = _dataStore.Data.Chats.FirstOrDefault(candidate => candidate.Id == chatId);
            if (chat is null)
                return null;

            var owner = RemoteProjector.ResolveChatOwner(_main, chatId);
            var isActive = owner?.CurrentChat?.Id == chatId;
            IReadOnlyList<ChatMessage>? loadedMessages = isActive
                ? owner!.Messages.Select(message => CloneMessageForRemote(message.Message)).ToList()
                : chat.Messages.Count > 0
                    ? chat.Messages.Select(CloneMessageForRemote).ToList()
                    : null;
            var settings = _dataStore.Data.Settings;
            var workingDirectory = ChatViewModel.ResolveEffectiveWorkingDirectory(
                _dataStore,
                chat.ProjectId,
                chat.WorktreePath);
            var runningBackgroundToolCallIds =
                (owner ?? _main.ChatVM).GetRunningBackgroundShellIds(chatId);

            return new TranscriptCapture(
                new Chat { Id = chat.Id, Title = chat.Title },
                loadedMessages,
                RemoteProjector.BuildStatus(_dataStore, owner ?? _main.ChatVM, chat),
                settings.ShowReasoning,
                settings.ShowToolCalls,
                _hub?.Revision ?? 0,
                _instanceId,
                workingDirectory,
                runningBackgroundToolCallIds);
        });

        if (capture is null)
        {
            await WriteErrorAsync(context, 404, "Chat not found.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var messages = capture.LoadedMessages
                       ?? await _dataStore
                           .ReadPersistedChatMessagesAsync(capture.Chat.Id, cancellationToken)
                           .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var window = RemoteProjector.SelectTranscriptWindow(
            messages,
            beforeMessageIndex,
            maxMessages);
        var transcript = RemoteProjector.BuildTranscript(
            capture.Chat,
            window,
            capture.Status,
            capture.ShowReasoning,
            capture.ShowToolCalls,
            capture.Revision,
            capture.RevisionEpoch,
            compact,
            capture.WorkingDirectory,
            messages,
            capture.RunningBackgroundToolCallIds);

        await context.WriteJsonAsync(
            JsonSerializer.Serialize(transcript, RemoteJsonContext.Default.RemoteTranscript),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleActivityAsync(RemoteHttpContext context, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.Request.QueryValue("chatId"), out var chatId))
        {
            await WriteErrorAsync(context, 400, "chatId is required.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var activityId = context.Request.QueryValue("activityId");
        if (string.IsNullOrWhiteSpace(activityId))
        {
            await WriteErrorAsync(context, 400, "activityId is required.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var capture = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var chat = _dataStore.Data.Chats.FirstOrDefault(candidate => candidate.Id == chatId);
            if (chat is null)
                return null;

            var owner = RemoteProjector.ResolveChatOwner(_main, chatId);
            var isActive = owner?.CurrentChat?.Id == chatId;
            IReadOnlyList<ChatMessage>? loadedMessages = isActive
                ? owner!.Messages.Select(message => CloneMessageForRemote(message.Message)).ToList()
                : chat.Messages.Count > 0
                    ? chat.Messages.Select(CloneMessageForRemote).ToList()
                    : null;
            var workingDirectory = ChatViewModel.ResolveEffectiveWorkingDirectory(
                _dataStore,
                chat.ProjectId,
                chat.WorktreePath);
            var runningBackgroundToolCallIds =
                (owner ?? _main.ChatVM).GetRunningBackgroundShellIds(chatId);

            return new ActivityCapture(
                new Chat { Id = chat.Id, Title = chat.Title },
                loadedMessages,
                workingDirectory,
                runningBackgroundToolCallIds);
        });

        if (capture is null)
        {
            await WriteErrorAsync(context, 404, "Chat not found.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var messages = capture.LoadedMessages
                       ?? await _dataStore
                           .ReadPersistedChatMessagesAsync(capture.Chat.Id, cancellationToken)
                           .ConfigureAwait(false);
        var details = RemoteProjector.BuildActivityDetails(
            capture.Chat,
            messages,
            activityId,
            capture.WorkingDirectory,
            capture.RunningBackgroundToolCallIds);
        if (details is null)
        {
            await WriteErrorAsync(context, 404, "Activity not found.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await context.WriteJsonAsync(
            JsonSerializer.Serialize(details, RemoteJsonContext.Default.RemoteActivityDetails),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleCommandAsync(
        RemoteHttpContext context,
        RemotePairedDevice device,
        CancellationToken cancellationToken)
    {
        var command = Deserialize(context.Request.Body, RemoteJsonContext.Default.RemoteCommand);
        if (command is null || string.IsNullOrWhiteSpace(command.Action))
        {
            await WriteErrorAsync(context, 400, "action is required.", cancellationToken).ConfigureAwait(false);
            return;
        }
        command.AuthenticatedDeviceId = device.DeviceId;
        if (!IsCompatibleCommand(command))
        {
            var mismatch = new RemoteCommandResult
            {
                Error = $"Remote protocol {RemoteProtocol.Version} is required.",
                RequestId = command.RequestId
            };
            await context.WriteJsonAsync(
                    JsonSerializer.Serialize(mismatch, RemoteJsonContext.Default.RemoteCommandResult),
                    cancellationToken,
                    409)
                .ConfigureAwait(false);
            return;
        }

        var result = command.Action == RemoteProtocol.Actions.RevokeDevice
            ? await RevokeRequestingDeviceAsync(device, cancellationToken).ConfigureAwait(false)
            : await ExecuteCommandDeduplicatedAsync(device, command).ConfigureAwait(false);

        await context.WriteJsonAsync(
            JsonSerializer.Serialize(result, RemoteJsonContext.Default.RemoteCommandResult),
            cancellationToken,
            result.Ok ? 200 : 400).ConfigureAwait(false);
    }

    internal static bool IsCompatibleCommand(RemoteCommand command) =>
        command.ProtocolVersion == RemoteProtocol.Version;

    private async Task<RemoteCommandResult> RevokeRequestingDeviceAsync(
        RemotePairedDevice device,
        CancellationToken cancellationToken)
    {
        return await RevokeDeviceAsync(device.DeviceId, cancellationToken).ConfigureAwait(false)
            ? new RemoteCommandResult { Ok = true, Message = "Device revoked." }
            : new RemoteCommandResult { Ok = false, Error = "Lumi could not persist the device revocation." };
    }

    public async Task<bool> RevokeDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (!CanManageSecurityState)
            return false;

        RemotePairedDevice? existing;
        lock (_deviceAuthorizationGate)
        {
            existing = _dataStore.SnapshotRemotePairedDevices().FirstOrDefault(candidate =>
                string.Equals(candidate.DeviceId, deviceId, StringComparison.Ordinal));
            if (existing is null || !_dataStore.RemoveRemotePairedDevice(deviceId))
                return false;

            DisposeDeviceStreamsLocked(deviceId);
        }

        try
        {
            await _dataStore.SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_deviceAuthorizationGate)
                _dataStore.UpsertRemotePairedDevice(existing);
            Trace.TraceWarning($"[Remote] Device revocation could not be persisted: {ex.Message}");
            return false;
        }

        StateChanged?.Invoke();
        return true;
    }

    private Task<RemoteCommandResult> ExecuteCommandDeduplicatedAsync(
        RemotePairedDevice device,
        RemoteCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.RequestId))
            return ExecuteRouterCommandAsync(command, requestId: null);

        var requestId = command.RequestId;
        if (requestId.Length > 128)
        {
            return Task.FromResult(new RemoteCommandResult
            {
                Error = "requestId must be 128 characters or fewer.",
                RequestId = requestId
            });
        }

        var key = new CommandDedupKey(device.DeviceId, requestId);
        var signature = BuildCommandSignature(command);
        TaskCompletionSource<RemoteCommandResult>? completion = null;
        CommandDedupEntry entry;

        lock (_commandGate)
        {
            PruneCommandRequestsLocked(DateTimeOffset.UtcNow);
            if (_commandRequests.TryGetValue(key, out entry!))
            {
                if (!string.Equals(entry.Signature, signature, StringComparison.Ordinal))
                {
                    return Task.FromResult(new RemoteCommandResult
                    {
                        Error = "That requestId was already used for a different command.",
                        RequestId = requestId
                    });
                }

                return entry.Task;
            }

            var deviceRequestCount = _commandRequests.Keys.Count(candidate =>
                string.Equals(candidate.DeviceId, device.DeviceId, StringComparison.Ordinal));
            if (deviceRequestCount >= MaxTrackedCommandRequestsPerDevice
                || _commandRequests.Count >= MaxTrackedCommandRequestsTotal)
            {
                return Task.FromResult(new RemoteCommandResult
                {
                    Error = "Lumi is still processing too many remote commands. Try again shortly.",
                    RequestId = requestId
                });
            }

            completion = new TaskCompletionSource<RemoteCommandResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            entry = new CommandDedupEntry(signature, completion.Task, DateTimeOffset.UtcNow);
            _commandRequests.Add(key, entry);
        }

        _ = CompleteCommandRequestAsync(key, entry, completion, command, requestId);
        return entry.Task;
    }

    private async Task CompleteCommandRequestAsync(
        CommandDedupKey key,
        CommandDedupEntry entry,
        TaskCompletionSource<RemoteCommandResult> completion,
        RemoteCommand command,
        string requestId)
    {
        RemoteCommandResult result;
        try
        {
            result = await ExecuteRouterCommandAsync(command, requestId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new RemoteCommandResult { Error = ex.Message, RequestId = requestId };
        }

        completion.TrySetResult(result);
        lock (_commandGate)
        {
            if (_commandRequests.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                current.CompletedAt = DateTimeOffset.UtcNow;
        }
    }

    private async Task<RemoteCommandResult> ExecuteRouterCommandAsync(
        RemoteCommand command,
        string? requestId)
    {
        // The operation is owned by the server, not by one HTTP response. If a phone times out or
        // drops its socket, the original command keeps running and a retry can await this same task.
        var result = await Dispatcher.UIThread
            .InvokeAsync(() => _router.ExecuteAsync(command, _cts.Token))
            .ConfigureAwait(false);
        result.RequestId = requestId;
        return result;
    }

    private void PruneCommandRequestsLocked(DateTimeOffset now)
    {
        foreach (var key in _commandRequests
                     .Where(pair => pair.Value.CompletedAt is { } completedAt
                                    && now - completedAt >= CommandResultRetention)
                     .Select(static pair => pair.Key)
                     .ToList())
        {
            _commandRequests.Remove(key);
        }

        foreach (var key in _commandRequests
                     .Where(pair => pair.Value.CompletedAt is null
                                    && now - pair.Value.CreatedAt >= IncompleteCommandRetention)
                     .Select(static pair => pair.Key)
                     .ToList())
        {
            _commandRequests.Remove(key);
        }

        // Never evict a completed result before the documented retention window. When the table is
        // full, new commands receive backpressure above instead of turning an outcome-unknown retry
        // into a duplicate send.
    }

    private static string BuildCommandSignature(RemoteCommand command)
    {
        var canonical = new StringBuilder();
        Append(command.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(command.Action);
        foreach (var (key, value) in command.Arguments.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            Append(key);
            Append(value);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));

        void Append(string? value)
        {
            if (value is null)
            {
                canonical.Append("-1:");
                return;
            }

            canonical.Append(value.Length).Append(':').Append(value);
        }
    }

    /// <summary>
    /// Receives a file from the phone and drops it somewhere Lumi can read.
    ///
    /// <para>Lumi runs on the PC and reads files by path, so "attach a file from my phone" means
    /// getting the bytes across and handing back a path. Files land in a per-run temp folder rather
    /// than anywhere the user keeps things, so an attachment can never overwrite real work, and the
    /// display name is kept as untrusted metadata, while the model-visible path uses an opaque
    /// generated basename so a hostile filename cannot become prompt text.</para>
    /// </summary>
    private async Task HandleUploadAsync(
        RemoteHttpContext context,
        RemotePairedDevice device,
        CancellationToken cancellationToken)
    {
        if (context.RequestBody is not { } requestBody)
        {
            await WriteErrorAsync(context, 400, "A raw upload body is required.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        string originalName;
        try
        {
            originalName = Encoding.UTF8.GetString(
                Convert.FromBase64String(
                    context.Request.Header(RemoteProtocol.UploadFileNameHeader) ?? ""));
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            await WriteErrorAsync(context, 400, "The file name header is invalid.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (originalName.Length > RemoteProtocol.MobileFileNameLimit)
        {
            await WriteErrorAsync(context, 400, "The file name is too large.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (context.Request.ContentLength > RemoteProtocol.MaxUploadBytes)
        {
            await WriteErrorAsync(context, 413, "That file is too large to send.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var displayName = SanitizeUploadDisplayName(originalName);
        var safeExtension = GetSafeUploadExtension(displayName);

        var root = GetMobileUploadRoot();
        var deviceFolder = Path.Combine(root, GetDeviceUploadFolderName(device.DeviceId));
        string target;
        await _uploadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsurePrivateDirectory(root);
            CleanupStaleMobileUploads(root, DateTimeOffset.UtcNow);
            EnsurePrivateDirectory(deviceFolder);

            var requestBytes = Math.Max(0, context.Request.ContentLength);
            var deviceBytes = GetDirectorySize(deviceFolder);
            var totalBytes = GetDirectorySize(root);
            if (!CanAcceptUpload(deviceBytes, totalBytes, requestBytes))
            {
                await WriteErrorAsync(
                        context,
                        507,
                        "Lumi's temporary mobile upload storage is full. Remove old attachments or try again later.",
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            target = await WriteMobileUploadAsync(
                    deviceFolder,
                    safeExtension,
                    requestBody,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _uploadGate.Release();
        }

        await context.WriteJsonAsync(
            JsonSerializer.Serialize(
                new RemoteUploadResponse { Ok = true, Path = target, FileName = displayName },
                RemoteJsonContext.Default.RemoteUploadResponse),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> WriteMobileUploadAsync(
        string folder,
        string safeExtension,
        RemoteRequestBody requestBody,
        CancellationToken cancellationToken)
    {
        const int maxNameAttempts = 8;
        for (var attempt = 1; attempt <= maxNameAttempts; attempt++)
        {
            var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
            var target = Path.Combine(folder, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{suffix}{safeExtension}");

            FileStream? stream = null;
            try
            {
                // CreateNew is the collision boundary. Two simultaneous uploads can never truncate
                // one another even if they carry the same leaf name and arrive in the same second.
                stream = new FileStream(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                SetPrivateFileMode(target);
                await requestBody.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
                return target;
            }
            catch (IOException ex) when (stream is null && File.Exists(target))
            {
                // A random-name collision is extraordinarily unlikely, but retry it atomically.
                if (attempt == maxNameAttempts)
                    throw new IOException("Lumi could not allocate a unique upload path.", ex);
            }
            catch
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    try { File.Delete(target); }
                    catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
                    {
                        // Best-effort cleanup of a canceled/failed partial upload that this request
                        // successfully created. Never delete a path whose CreateNew failed.
                    }
                }

                throw;
            }
        }

        throw new IOException("Lumi could not allocate a unique upload path.");
    }

    internal static string SanitizeUploadDisplayName(string originalName)
    {
        var normalized = originalName.Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        var leaf = separator >= 0 ? normalized[(separator + 1)..] : normalized;
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(leaf.Select(character =>
        {
            var category = char.GetUnicodeCategory(character);
            return char.IsControl(character)
                   || category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                   || invalid.Contains(character)
                ? '_'
                : character;
        }).ToArray());

        return string.IsNullOrWhiteSpace(sanitized) ? "attachment" : sanitized;
    }

    internal static string GetSafeUploadExtension(string displayName)
    {
        var extension = Path.GetExtension(displayName);
        if (extension.Length is < 2 or > 17)
            return "";

        for (var index = 1; index < extension.Length; index++)
        {
            var character = extension[index];
            if (character is not (>= 'a' and <= 'z')
                && character is not (>= 'A' and <= 'Z')
                && character is not (>= '0' and <= '9'))
            {
                return "";
            }
        }

        return extension.ToLowerInvariant();
    }

    private async Task HandleEventsAsync(
        RemoteHttpContext context,
        RemotePairedDevice device,
        CancellationToken cancellationToken)
    {
        if (_hub is not { } hub)
        {
            await WriteErrorAsync(context, 500, "Remote server is not running.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var subscription = ReadEventSubscription(context.Request);
        var snapshot = await Dispatcher.UIThread.InvokeAsync(() =>
            RemoteProjector.BuildSnapshot(
                _dataStore,
                _main,
                _main.ChatVM.AvailableModels.ToList(),
                includeChatList: true));
        var snapshotJson = JsonSerializer.Serialize(snapshot, RemoteJsonContext.Default.RemoteSnapshot);
        if (Encoding.UTF8.GetByteCount(snapshotJson) > RemoteProtocol.MaxSnapshotJsonBytes)
        {
            await WriteErrorAsync(context, 507, "The mobile snapshot exceeds the protocol limit.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var stream = await context.BeginEventStreamAsync(cancellationToken).ConfigureAwait(false);
        RemoteEventClient? client = null;

        try
        {
            client = await TryRegisterEventClientAsync(
                    hub,
                    stream,
                    device,
                    new RemoteEventFrame(RemoteProtocol.Events.Snapshot, snapshotJson),
                    context.RemoteEndPoint,
                    context.LocalEndPoint,
                    cancellationToken,
                    subscription)
                .ConfigureAwait(false);
            if (client is null)
            {
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            await client.RunAsync(linked.Token).ConfigureAwait(false);
        }

        finally
        {
            if (client is not null)
            {
                _streams.TryRemove(client.Id, out _);
                hub.RemoveClient(client);
            }

            await context.CompleteEventStreamAsync(stream).ConfigureAwait(false);
        }
    }

    private async Task HandleSubscriptionAsync(
        RemoteHttpContext context,
        RemotePairedDevice device,
        CancellationToken cancellationToken)
    {
        if (_hub is not { } hub)
        {
            await WriteErrorAsync(context, 500, "Remote server is not running.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var subscription = Deserialize(
            context.Request.Body,
            RemoteJsonContext.Default.RemoteEventSubscription);
        if (subscription is null || subscription.Generation < 0)
        {
            await WriteErrorAsync(context, 400, "A valid subscription is required.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await hub.UpdateSubscriptionAsync(device.DeviceId, subscription).ConfigureAwait(false);
        await context.WriteJsonAsync("{}", cancellationToken).ConfigureAwait(false);
    }

    private static RemoteEventSubscription ReadEventSubscription(RemoteHttpRequest request)
    {
        var subscription = new RemoteEventSubscription
        {
            IsForeground = !bool.TryParse(request.QueryValue("foreground"), out var foreground)
                           || foreground,
            IncludeChatList = bool.TryParse(request.QueryValue("chats"), out var chats) && chats,
            IncludeLibrary = bool.TryParse(request.QueryValue("library"), out var library) && library,
            CompactTranscript = bool.TryParse(
                request.QueryValue("compact"),
                out var compact) && compact
        };
        if (long.TryParse(
                request.QueryValue("generation"),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var generation)
            && generation >= 0)
        {
            subscription.Generation = generation;
        }
        if (Guid.TryParse(request.QueryValue("chatId"), out var chatId))
            subscription.ChatId = chatId;
        return subscription;
    }

    private static int ParseBoundedInt(string? raw, int fallback, int minimum, int maximum) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;

    internal static int ResolveTranscriptWindowLimit(string? raw, int? beforeMessageIndex) =>
        ParseBoundedInt(
            raw,
            beforeMessageIndex is null
                ? RemoteProtocol.InitialTranscriptWindowRawMessageLimit
                : RemoteProtocol.TranscriptWindowRawMessageLimit,
            1,
            RemoteProtocol.TranscriptWindowRawMessageLimit);

    // ── Auth ────────────────────────────────────────────────────────────────────────────────

    private string? GetActivePairingCodeLocked(DateTimeOffset now)
    {
        if (_pairingCode is not null && now < _pairingCodeExpiresAt)
            return _pairingCode;

        _pairingCode = null;
        _pairingCodeExpiresAt = default;
        _pairingFailedAttempts = 0;
        return null;
    }

    private PairingCodeResult TryConsumePairingCode(string? submittedCode)
    {
        lock (_pairingGate)
        {
            var expected = GetActivePairingCodeLocked(DateTimeOffset.UtcNow);
            if (expected is null)
                return PairingCodeResult.NoActiveCode;

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(expected),
                    Encoding.UTF8.GetBytes(submittedCode ?? "")))
            {
                _pairingFailedAttempts++;
                if (_pairingFailedAttempts < PairingFailedAttemptLimit)
                    return PairingCodeResult.Incorrect;

                _pairingCode = null;
                _pairingCodeExpiresAt = default;
                return PairingCodeResult.AttemptsExhausted;
            }

            // Validation and consumption happen under the same lock: concurrent connections can
            // observe at most one successful exchange for a single-use code.
            _pairingCode = null;
            _pairingCodeExpiresAt = default;
            _pairingFailedAttempts = 0;
            return PairingCodeResult.Accepted;
        }
    }

    private static string GetMobileUploadRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lumi",
            "mobile-uploads");

    private static string GetDeviceUploadFolderName(string deviceId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(deviceId));
        return $"device-{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    private static long GetDirectorySize(string folder)
    {
        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total = checked(total + new FileInfo(file).Length);
                }

                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            return total;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    internal static bool CanAcceptUpload(long deviceBytes, long totalBytes, long requestBytes) =>
        requestBytes >= 0
        && deviceBytes <= RemoteProtocol.MaxUploadBytesPerDevice - requestBytes
        && totalBytes <= RemoteProtocol.MaxUploadBytesTotal - requestBytes;

    private static void EnsurePrivateDirectory(string folder)
    {
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void SetPrivateFileMode(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    internal static void CleanupStaleMobileUploads(string folder, DateTimeOffset now)
    {
        var cutoff = now.UtcDateTime - MobileUploadRetention;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file still being written or held by another process can wait for the next
                    // server start/upload; cleanup must never make the remote server unavailable.
                }
            }
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            // The temp directory is best-effort and may disappear between existence checks.
        }

        try
        {
            foreach (var directory in Directory
                         .EnumerateDirectories(folder, "*", SearchOption.AllDirectories)
                         .OrderByDescending(static path => path.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private readonly record struct CommandDedupKey(string DeviceId, string RequestId);

    private sealed class CommandDedupEntry(
        string signature,
        Task<RemoteCommandResult> task,
        DateTimeOffset createdAt)
    {
        public string Signature { get; } = signature;
        public Task<RemoteCommandResult> Task { get; } = task;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset? CompletedAt { get; set; }
    }

    private enum PairingCodeResult
    {
        NoActiveCode,
        Incorrect,
        AttemptsExhausted,
        Accepted
    }

    private bool TryAuthorize(RemoteHttpRequest request, out RemotePairedDevice device)
    {
        var token = request.Header(RemoteProtocol.DeviceTokenHeader);
        if (string.IsNullOrWhiteSpace(token))
        {
            device = null!;
            return false;
        }

        lock (_deviceAuthorizationGate)
            return TryAuthorizeLocked(token, out device);
    }

    private bool TryAuthorizeLocked(string token, out RemotePairedDevice device)
    {
        device = null!;
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        foreach (var candidate in _dataStore.SnapshotRemotePairedDevices())
        {
            if (candidate.Token.Length == token.Length
                && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate.Token), tokenBytes))
            {
                device = candidate;
                return true;
            }
        }

        return false;
    }

    internal bool TryRegisterEventClient(
        RemoteEventHub hub,
        Stream stream,
        RemotePairedDevice device,
        RemoteEventFrame initialFrame,
        [NotNullWhen(true)]
        out RemoteEventClient? client,
        RemoteEventSubscription? subscription = null)
    {
        lock (_deviceAuthorizationGate)
        {
            if (!IsDeviceAuthorizedLocked(device))
            {
                client = null;
                return false;
            }

            client = hub.AddClient(stream, device.DeviceId, initialFrame, subscription);
            _streams[client.Id] = client;
            return true;
        }
    }

    internal async Task<RemoteEventClient?> TryRegisterEventClientAsync(
        RemoteEventHub hub,
        Stream stream,
        RemotePairedDevice device,
        RemoteEventFrame initialFrame,
        EndPoint? remoteEndPoint,
        EndPoint? localEndPoint,
        CancellationToken cancellationToken,
        RemoteEventSubscription? subscription = null)
    {
        await _networkPolicyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsAllowedCaller(
                    remoteEndPoint,
                    localEndPoint,
                    _dataStore.Data.Settings.RemoteAllowInsecureLan,
                    _tailscaleAddresses))
            {
                return null;
            }

            return TryRegisterEventClient(hub, stream, device, initialFrame, out var client, subscription)
                ? client
                : null;
        }
        finally
        {
            _networkPolicyGate.Release();
        }
    }

    private bool IsDeviceAuthorizedLocked(RemotePairedDevice device)
    {
        var current = _dataStore.SnapshotRemotePairedDevices().FirstOrDefault(candidate =>
            string.Equals(candidate.DeviceId, device.DeviceId, StringComparison.Ordinal));
        if (current is null || current.Token.Length != device.Token.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(current.Token),
            Encoding.UTF8.GetBytes(device.Token));
    }

    private void DisposeDeviceStreamsLocked(string deviceId)
    {
        foreach (var stream in _streams.Values.Where(stream =>
                     string.Equals(stream.DeviceId, deviceId, StringComparison.Ordinal)))
        {
            stream.Dispose();
        }
    }

    /// <summary>
    /// Refuses anything that is not loopback, a verified Tailscale socket, or an explicitly allowed
    /// private-network peer. RFC6598 addresses alone do not prove Tailscale: enterprise and carrier
    /// networks may route the same range without WireGuard protection.
    /// </summary>
    internal static bool IsPrivateCaller(EndPoint? endPoint)
    {
        if (endPoint is not IPEndPoint ipEndPoint)
            return false;

        var address = ipEndPoint.Address;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] switch
            {
                10 => true,
                100 => bytes[1] >= 64 && bytes[1] <= 127,
                127 => true,
                172 => bytes[1] >= 16 && bytes[1] <= 31,
                192 => bytes[1] == 168,
                169 => bytes[1] == 254,
                _ => false
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (address.GetAddressBytes()[0] & 0xFE) == 0xFC;

        return false;
    }

    internal static bool IsAllowedCaller(
        EndPoint? remoteEndPoint,
        EndPoint? localEndPoint,
        bool allowInsecureLan,
        IReadOnlySet<IPAddress>? verifiedTailscaleAddresses = null)
    {
        if (remoteEndPoint is not IPEndPoint remoteIpEndPoint)
            return false;

        var remoteAddress = NormalizeAddress(remoteIpEndPoint.Address);

        if (IPAddress.IsLoopback(remoteAddress))
            return true;

        var localAddress = localEndPoint is IPEndPoint localIpEndPoint
            ? NormalizeAddress(localIpEndPoint.Address)
            : null;
        var isVerifiedTailscaleSocket = localAddress is not null
                                        && verifiedTailscaleAddresses?.Contains(localAddress) == true
                                        && IsTailscaleAddress(remoteAddress);

        return isVerifiedTailscaleSocket
               || allowInsecureLan
               && IsPrivateCaller(new IPEndPoint(remoteAddress, remoteIpEndPoint.Port));
    }

    internal static bool IsTailscaleAddress(IPAddress address)
        => RemoteProtocol.IsTailscaleAddress(address);

    private static IPAddress NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private async Task<bool> RefreshTailscaleAddressesAsync(CancellationToken cancellationToken)
    {
        var addresses = await Task.Run(_tailscaleAddressProvider, cancellationToken).ConfigureAwait(false);
        var previous = Volatile.Read(ref _tailscaleAddresses);
        if (previous.SetEquals(addresses))
            return false;

        Interlocked.Exchange(ref _tailscaleAddresses, addresses);
        return true;
    }

    internal static IReadOnlySet<IPAddress> GetVerifiedTailscaleAddresses()
    {
        var addresses = new HashSet<IPAddress>();
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "tailscale",
                Arguments = "ip",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null)
                return addresses;

            if (!process.WaitForExit(2000))
            {
                process.Kill(entireProcessTree: true);
                return addresses;
            }
            var output = process.StandardOutput.ReadToEnd();

            foreach (var line in output.Split(
                         ['\r', '\n'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (IPAddress.TryParse(line, out var parsed) && IsTailscaleAddress(parsed))
                    addresses.Add(NormalizeAddress(parsed));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                   or IOException or UnauthorizedAccessException)
        {
            Trace.TraceInformation($"[Remote] Tailscale address verification unavailable: {ex.Message}");
        }

        return addresses;
    }

    internal static List<string> GetLocalAddresses()
    {
        var addresses = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    var endPoint = new IPEndPoint(info.Address, 0);
                    if (IsPrivateCaller(endPoint))
                        addresses.Add(info.Address.ToString());
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No usable adapters; the user can still type an address manually.
        }

        return addresses;
    }

    private bool TryAcquireServerOwnership()
    {
        try
        {
            _serverOwnershipLock = new FileStream(
                Path.Combine(DataStore.AppDirectory, "remote-server.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void ReleaseServerOwnership()
    {
        _serverOwnershipLock?.Dispose();
        _serverOwnershipLock = null;
    }

    private static ChatMessage CloneMessageForRemote(ChatMessage message) =>
        new()
        {
            Id = message.Id,
            Role = message.Role,
            Content = message.Content,
            Author = message.Author,
            Timestamp = message.Timestamp,
            ToolName = message.ToolName,
            ToolCallId = message.ToolCallId,
            ParentToolCallId = message.ParentToolCallId,
            ToolStatus = message.ToolStatus,
            ToolOutput = message.ToolOutput,
            ToolStartedAt = message.ToolStartedAt,
            ToolDurationMs = message.ToolDurationMs,
            LinkedChatId = message.LinkedChatId,
            LinkedChatTitle = message.LinkedChatTitle,
            QuestionId = message.QuestionId,
            QuestionText = message.QuestionText,
            QuestionOptions = message.QuestionOptions,
            QuestionAllowFreeText = message.QuestionAllowFreeText,
            QuestionAllowMultiSelect = message.QuestionAllowMultiSelect,
            IsStreaming = message.IsStreaming,
            Model = message.Model,
            ReasoningEffort = message.ReasoningEffort,
            ContextWindowTier = message.ContextWindowTier,
            AgentId = message.AgentId,
            SdkAgentName = message.SdkAgentName,
            HasAgentSelection = message.HasAgentSelection,
            ActiveMcpServerNames = [.. message.ActiveMcpServerNames],
            HasMcpSelection = message.HasMcpSelection,
            Attachments = [.. message.Attachments],
            Sources = [.. message.Sources.Select(static source => new SearchSource
            {
                Title = source.Title,
                Snippet = source.Snippet,
                Url = source.Url
            })],
            ActiveSkills = [.. message.ActiveSkills.Select(static skill => new SkillReference
            {
                Name = skill.Name,
                Glyph = skill.Glyph,
                Description = skill.Description,
                Content = skill.Content
            })],
            SteerDelivery = message.SteerDelivery
        };

    private static T? Deserialize<T>(string body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        if (string.IsNullOrWhiteSpace(body))
            return default;

        try
        {
            return JsonSerializer.Deserialize(body, typeInfo);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static Task WriteErrorAsync(RemoteHttpContext context, int status, string message, CancellationToken cancellationToken)
    {
        var payload = new RemoteCommandResult { Ok = false, Error = message };
        return context.WriteJsonAsync(
            JsonSerializer.Serialize(payload, RemoteJsonContext.Default.RemoteCommandResult),
            cancellationToken,
            status);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        _cts.Cancel();
        Stop();
        _listener.Dispose();
        ReleaseServerOwnership();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
