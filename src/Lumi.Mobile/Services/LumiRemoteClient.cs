using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Lumi.Remote.Protocol;

namespace Lumi.Mobile.Services;

/// <summary>Snapshot of the link to a Lumi desktop, surfaced directly in the mobile chrome.</summary>
public enum RemoteLinkState
{
    Disconnected,
    Connecting,
    Connected,
    Unauthorized,
    Error
}

/// <summary>
/// The phone's half of the Lumi remote protocol: request/response over HTTP plus a resilient
/// Server-Sent Events subscription for live push. Everything is funnelled through
/// <see cref="RemoteJsonContext"/> so a trimmed/AOT mobile head never needs reflection.
/// </summary>
public sealed class LumiRemoteClient : IAsyncDisposable
{
    internal static readonly TimeSpan DefaultRequestDeadline = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan DefaultUploadDeadline = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan EventSilenceDeadline = TimeSpan.FromSeconds(90);
    private const string RequestTimeoutMessage = "Lumi took too long to answer.";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _streamGate = new(1, 1);
    private readonly SemaphoreSlim _transcriptGate = new(1, 1);
    private readonly TimeSpan _requestDeadline;
    private readonly TimeSpan _commandConfirmationDeadline;
    private readonly TimeSpan _uploadDeadline;
    private readonly IRemoteRouteVerifier _routeVerifier;

    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;
    private string? _compatibleBaseUrl;
    private int _compatibleProtocolVersion;
    private HashSet<string> _capabilities = new(StringComparer.Ordinal);
    private RemoteEventSubscription _eventSubscription = new();
    private bool _streamDesired;
    private long _streamGeneration;
    private volatile bool _hasCompatibleBootstrap;
    private bool _disposed;

    public LumiRemoteClient(string deviceId, string deviceName, HttpMessageHandler? handler = null)
        : this(deviceId, deviceName, handler, DefaultRequestDeadline, DefaultUploadDeadline)
    {
    }

    internal LumiRemoteClient(
        string deviceId,
        string deviceName,
        HttpMessageHandler? handler,
        TimeSpan requestDeadline,
        TimeSpan uploadDeadline,
        IRemoteRouteVerifier? routeVerifier = null,
        TimeSpan? commandConfirmationDeadline = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestDeadline, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(uploadDeadline, TimeSpan.Zero);

        DeviceId = deviceId;
        DeviceName = deviceName;
        _requestDeadline = requestDeadline;
        _commandConfirmationDeadline = commandConfirmationDeadline ?? requestDeadline;
        _uploadDeadline = uploadDeadline;
        _routeVerifier = routeVerifier ?? RemotePlatformServices.RouteVerifier;

        var transport = handler ?? CreateDefaultHandler();
        _http = new HttpClient(
            new RouteGuardHandler(transport, _routeVerifier),
            disposeHandler: true);

        // The SSE response never completes, so the overall timeout has to be infinite; per-request
        // deadlines are enforced with CancellationTokens instead.
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.Add(RemoteProtocol.DeviceIdHeader, deviceId);
    }

    internal static HttpClientHandler CreateDefaultHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.GZip,
        AllowAutoRedirect = false,
        UseProxy = false
    };

    public string DeviceId { get; }

    public string DeviceName { get; }

    public string? BaseUrl { get; private set; }

    public string? Token { get; private set; }

    public RemoteLinkState State { get; private set; } = RemoteLinkState.Disconnected;

    public string? StateMessage { get; private set; }

    public int ConnectedProtocolVersion => _compatibleProtocolVersion;
    public bool SupportsScopedEvents =>
        _capabilities.Contains(RemoteProtocol.Capabilities.ScopedEventsV1);
    public bool SupportsCompactTranscript =>
        _capabilities.Contains(RemoteProtocol.Capabilities.CompactTranscriptV1);

    /// <summary>Raised for every SSE frame. Handlers are invoked off the UI thread.</summary>
    public event Action<RemoteEventFrame>? FrameReceived;

    /// <summary>
    /// Raised for every SSE frame with a flag identifying the guaranteed first snapshot for that
    /// connection. Internal consumers use this to distinguish bootstrap from later catalog pushes.
    /// </summary>
    internal event Action<RemoteEventFrame, bool>? StreamFrameReceived;

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    public event Action<RemoteLinkState, string?>? StateChanged;

    public void Configure(string baseUrl, string? token)
    {
        BaseUrl = NormalizeBaseUrl(baseUrl);
        Token = token;
        _hasCompatibleBootstrap = string.Equals(
            BaseUrl,
            _compatibleBaseUrl,
            StringComparison.OrdinalIgnoreCase)
            && IsSupportedProtocol(_compatibleProtocolVersion);
    }

    public static string NormalizeBaseUrl(string value)
    {
        var trimmed = (value ?? "").Trim().TrimEnd('/');
        if (trimmed.Length == 0)
            return "";

        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            trimmed = "http://" + trimmed;

        // A bare host means the well-known port.
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.IsDefaultPort
            ? $"{uri.Scheme}://{uri.Host}:{RemoteProtocol.DefaultPort}"
            : trimmed;
    }

    public async Task<RemoteHello?> HelloAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, normalizedBaseUrl + RemoteProtocol.Routes.Hello);
        ApplyAuth(request);
        var hello = await SendJsonAsync(
                request,
                RemoteJsonContext.Default.RemoteHello,
                cancellationToken)
            .ConfigureAwait(false);
        if (hello is not null
            && IsSupportedProtocol(hello.ProtocolVersion)
            && RemoteProtocol.HasRequiredCapabilities(hello.Capabilities))
        {
            _compatibleBaseUrl = normalizedBaseUrl;
            _compatibleProtocolVersion = hello.ProtocolVersion;
            ApplyCapabilities(hello.Capabilities);
            if (string.Equals(BaseUrl, normalizedBaseUrl, StringComparison.OrdinalIgnoreCase))
                _hasCompatibleBootstrap = true;
        }
        else if (hello is not null)
        {
            InvalidateCompatibility(normalizedBaseUrl);
            SetState(
                RemoteLinkState.Error,
                $"This phone requires Lumi remote protocol {RemoteProtocol.Version} with scoped events.");
        }

        return hello;
    }

    public async Task<RemotePairResponse> PairAsync(string baseUrl, string code, CancellationToken cancellationToken)
    {
        var payload = new RemotePairRequest { DeviceId = DeviceId, DeviceName = DeviceName, Code = code };
        using var request = new HttpRequestMessage(HttpMethod.Post, NormalizeBaseUrl(baseUrl) + RemoteProtocol.Routes.Pair)
        {
            Content = JsonContent(payload, RemoteJsonContext.Default.RemotePairRequest)
        };

        using var deadline = CreateDeadline(cancellationToken, _requestDeadline);
        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            var body = await ReadLimitedStringAsync(
                    response.Content,
                    RemoteProtocol.MaxHandshakeJsonBytes,
                    deadline.Token)
                .ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize(body, RemoteJsonContext.Default.RemotePairResponse);

            if (parsed is { Ok: true, Token: { Length: > 0 } token })
            {
                BaseUrl = NormalizeBaseUrl(baseUrl);
                Token = token;
            }

            return parsed ?? new RemotePairResponse { Error = "Lumi returned an unreadable response." };
        }
        catch (OperationCanceledException) when (IsDeadlineCancellation(cancellationToken, deadline))
        {
            return new RemotePairResponse { Error = RequestTimeoutMessage };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RemotePairResponse { Error = Describe(ex) };
        }
    }

    public async Task<RemoteSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = await GetAsync(
                RemoteProtocol.Routes.Snapshot,
                RemoteJsonContext.Default.RemoteSnapshot,
                cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
            return null;
        if (!IsSupportedProtocol(snapshot.ProtocolVersion)
            || !RemoteProtocol.HasRequiredCapabilities(snapshot.Capabilities))
        {
            _hasCompatibleBootstrap = false;
            SetState(
                RemoteLinkState.Error,
                $"This phone requires Lumi remote protocol {RemoteProtocol.Version} with scoped events.");
            return null;
        }

        _compatibleBaseUrl = BaseUrl;
        _compatibleProtocolVersion = snapshot.ProtocolVersion;
        ApplyCapabilities(snapshot.Capabilities);
        _hasCompatibleBootstrap = true;
        SetState(RemoteLinkState.Connected, null);
        return snapshot;
    }

    public Task<RemoteChatPage?> GetChatsAsync(
        int offset,
        int limit,
        string? query,
        Guid? projectId,
        CancellationToken cancellationToken)
    {
        var route =
            $"{RemoteProtocol.Routes.Chats}?offset={Math.Max(0, offset)}" +
            $"&limit={Math.Clamp(limit, 1, RemoteProtocol.MaxChatPageSize)}";
        if (!string.IsNullOrWhiteSpace(query))
            route += $"&q={Uri.EscapeDataString(query.Trim())}";
        if (projectId is { } id)
            route += $"&projectId={id}";

        return GetAsync(route, RemoteJsonContext.Default.RemoteChatPage, cancellationToken);
    }

    public Task<RemoteLibraryItem?> GetLibraryItemAsync(
        string resource,
        string identifier,
        CancellationToken cancellationToken) =>
        GetAsync(
            $"{RemoteProtocol.Routes.LibraryItem}?resource={Uri.EscapeDataString(resource)}" +
            $"&identifier={Uri.EscapeDataString(identifier)}",
            RemoteJsonContext.Default.RemoteLibraryItem,
            cancellationToken);

    public Task<RemoteTranscript?> GetTranscriptAsync(Guid chatId, CancellationToken cancellationToken) =>
        GetTranscriptAsync(chatId, beforeMessageIndex: null, cancellationToken);

    /// <summary>
    /// Fetches one bounded transcript window. Transcript requests are serialized through the entire
    /// body read and JSON deserialization so reconnect/pairing bursts can never materialize multiple
    /// large responses at once.
    /// </summary>
    public async Task<RemoteTranscript?> GetTranscriptAsync(
        Guid chatId,
        int? beforeMessageIndex,
        CancellationToken cancellationToken)
        => await GetTranscriptAsync(
            chatId,
            beforeMessageIndex,
            RemoteProtocol.TranscriptWindowRawMessageLimit,
            cancellationToken);

    public async Task<RemoteTranscript?> GetTranscriptAsync(
        Guid chatId,
        int? beforeMessageIndex,
        int maxMessages,
        CancellationToken cancellationToken)
    {
        await _transcriptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var route = $"{RemoteProtocol.Routes.Transcript}?chatId={chatId}";
            if (beforeMessageIndex is { } before)
                route += $"&beforeMessageIndex={before}";
            route += $"&limit={Math.Clamp(maxMessages, 1, RemoteProtocol.TranscriptWindowRawMessageLimit)}";
            if (SupportsCompactTranscript)
                route += "&mode=compact";

            return await GetAsync(
                    route,
                    RemoteJsonContext.Default.RemoteTranscript,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _transcriptGate.Release();
        }
    }

    public Task<RemoteActivityDetails?> GetActivityDetailsAsync(
        Guid chatId,
        string activityId,
        CancellationToken cancellationToken) =>
        GetAsync(
            $"{RemoteProtocol.Routes.Activity}?chatId={chatId}" +
            $"&activityId={Uri.EscapeDataString(activityId)}",
            RemoteJsonContext.Default.RemoteActivityDetails,
            cancellationToken);

    public async Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command, CancellationToken cancellationToken)
    {
        command.ProtocolVersion = IsSupportedProtocol(_compatibleProtocolVersion)
            ? _compatibleProtocolVersion
            : RemoteProtocol.Version;
        if (string.IsNullOrWhiteSpace(command.RequestId))
            command.RequestId = Guid.NewGuid().ToString("N");
        var requestId = command.RequestId;

        if (BaseUrl is not { Length: > 0 })
        {
            return new RemoteCommandResult
            {
                Error = "Not connected to a Lumi desktop.",
                RequestId = requestId
            };
        }
        if (!_hasCompatibleBootstrap)
        {
            return new RemoteCommandResult
            {
                Error = "Waiting for a compatible Lumi desktop connection.",
                RequestId = requestId
            };
        }

        var firstAttempt = await SendCommandAttemptAsync(
                command,
                requestId,
                _requestDeadline,
                cancellationToken)
            .ConfigureAwait(false);
        var result = firstAttempt.Result;
        if (firstAttempt.IsAuthoritative
            || cancellationToken.IsCancellationRequested
            || command.Action == RemoteProtocol.Actions.RevokeDevice)
            return result;

        // The desktop owns remote commands after accepting their request ID. A phone can time out
        // while the original command is still opening a large chat or reconnecting Copilot; retrying
        // the same ID does not send twice — it joins the server's existing task and retrieves the
        // authoritative result instead of showing a false "not sent" error.
        var confirmation = await SendCommandAttemptAsync(
                command,
                requestId,
                _commandConfirmationDeadline,
                cancellationToken)
            .ConfigureAwait(false);
        if (confirmation.IsAuthoritative)
            return confirmation.Result;

        // The server may still own and complete the original request. Keep the typed timeout and
        // request ID so the ViewModel can retry the same idempotent command instead of sending a
        // second logical message after a transient confirmation failure.
        return new RemoteCommandResult
        {
            Error = confirmation.Result.Error ?? result.Error ?? RequestTimeoutMessage,
            RequestId = requestId,
            IsTimeout = result.IsTimeout,
            IsOutcomeUnknown = true
        };
    }

    private async Task<CommandAttempt> SendCommandAttemptAsync(
        RemoteCommand command,
        string requestId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + RemoteProtocol.Routes.Command)
        {
            Content = JsonContent(command, RemoteJsonContext.Default.RemoteCommand)
        };
        ApplyAuth(request);

        using var deadline = CreateDeadline(cancellationToken, timeout);
        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            var body = await ReadLimitedStringAsync(
                    response.Content,
                    RemoteProtocol.MaxCommandResponseJsonBytes,
                    deadline.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                SetState(RemoteLinkState.Unauthorized, "This device is no longer paired with Lumi.");
                return new CommandAttempt(new RemoteCommandResult
                {
                    Error = "This device is no longer paired with Lumi.",
                    RequestId = requestId
                }, IsAuthoritative: true);
            }

            var result = JsonSerializer.Deserialize(body, RemoteJsonContext.Default.RemoteCommandResult);
            if (result is null)
            {
                return new CommandAttempt(new RemoteCommandResult
                {
                    Error = "Lumi returned an unreadable response.",
                    RequestId = requestId
                }, IsAuthoritative: false);
            }

            var echoedRequestId = result.RequestId;
            result.RequestId ??= requestId;
            return new CommandAttempt(
                result,
                IsAuthoritative: string.Equals(
                    echoedRequestId,
                    requestId,
                    StringComparison.Ordinal));
        }
        catch (OperationCanceledException) when (IsDeadlineCancellation(cancellationToken, deadline))
        {
            return new CommandAttempt(new RemoteCommandResult
            {
                Error = RequestTimeoutMessage,
                RequestId = requestId,
                IsTimeout = true
            }, IsAuthoritative: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CommandAttempt(new RemoteCommandResult
            {
                Error = Describe(ex),
                RequestId = requestId
            }, IsAuthoritative: false);
        }
    }

    private readonly record struct CommandAttempt(
        RemoteCommandResult Result,
        bool IsAuthoritative);

    internal void MarkProtocolCompatibleForTests(
        int protocolVersion = RemoteProtocol.Version,
        bool scopedEvents = true)
    {
        _compatibleBaseUrl = BaseUrl;
        _compatibleProtocolVersion = protocolVersion;
        _capabilities = scopedEvents
            ? new HashSet<string>([RemoteProtocol.Capabilities.ScopedEventsV1], StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        _hasCompatibleBootstrap = true;
    }

    private static bool IsSupportedProtocol(int protocolVersion) =>
        RemoteProtocol.IsCompatibleVersion(protocolVersion);

    private void ApplyCapabilities(IEnumerable<string>? capabilities)
    {
        _capabilities = capabilities is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(
                capabilities.Where(static capability => !string.IsNullOrWhiteSpace(capability)),
                StringComparer.Ordinal);
    }

    private void InvalidateCompatibility(string baseUrl)
    {
        var matchesConfiguredHost = string.Equals(
            BaseUrl,
            baseUrl,
            StringComparison.OrdinalIgnoreCase);
        var matchesCompatibleHost = string.Equals(
            _compatibleBaseUrl,
            baseUrl,
            StringComparison.OrdinalIgnoreCase);
        if (!matchesConfiguredHost && !matchesCompatibleHost)
            return;

        _compatibleBaseUrl = null;
        _compatibleProtocolVersion = 0;
        ApplyCapabilities(null);
        _hasCompatibleBootstrap = false;
    }

    private string BuildEventsRoute()
    {
        var subscription = Volatile.Read(ref _eventSubscription);
        var query = new List<string>
        {
            $"generation={subscription.Generation}",
            $"foreground={subscription.IsForeground.ToString().ToLowerInvariant()}",
            $"chats={subscription.IncludeChatList.ToString().ToLowerInvariant()}",
            $"library={subscription.IncludeLibrary.ToString().ToLowerInvariant()}",
            $"compact={subscription.CompactTranscript.ToString().ToLowerInvariant()}"
        };
        if (subscription.ChatId is { } chatId && chatId != Guid.Empty)
            query.Add($"chatId={chatId}");
        return $"{RemoteProtocol.Routes.Events}?{string.Join('&', query)}";
    }

    private static RemoteEventSubscription CopySubscription(RemoteEventSubscription subscription) => new()
    {
        Generation = subscription.Generation,
        ChatId = subscription.ChatId,
        IncludeChatList = subscription.IncludeChatList,
        IncludeLibrary = subscription.IncludeLibrary,
        CompactTranscript = subscription.CompactTranscript,
        IsForeground = subscription.IsForeground
    };

    private bool TryStoreEventSubscription(RemoteEventSubscription subscription)
    {
        while (true)
        {
            var current = Volatile.Read(ref _eventSubscription);
            if (subscription.Generation < current.Generation)
                return false;

            var replacement = CopySubscription(subscription);
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _eventSubscription, replacement, current),
                    current))
            {
                return true;
            }
        }
    }

    public async Task<string?> DownloadProducedFileAsync(
        Guid chatId,
        Guid messageId,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (BaseUrl is not { Length: > 0 })
            return null;

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{BaseUrl}{RemoteProtocol.Routes.File}?chatId={chatId}&messageId={messageId}");
        ApplyAuth(request);
        using var deadline = CreateDeadline(cancellationToken, _uploadDeadline);
        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                SetState(RemoteLinkState.Unauthorized, "This device is no longer paired with Lumi.");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadLimitedStringAsync(
                        response.Content,
                        RemoteProtocol.MaxCommandResponseJsonBytes,
                        deadline.Token)
                    .ConfigureAwait(false);
                SetRequestFailure(DescribeHttpFailure(response, body));
                return null;
            }

            if (response.Content.Headers.ContentLength is { } declared
                && declared > RemoteProtocol.MaxDownloadBytes)
            {
                SetRequestFailure("That produced file is too large to open on mobile.");
                return null;
            }

            var folder = Path.Combine(Path.GetTempPath(), "LumiMobile", "downloads");
            Directory.CreateDirectory(folder);
            var safeName = SafeDownloadFileName(fileName);
            var target = Path.Combine(folder, $"{Guid.NewGuid():N}-{safeName}");

            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            await using var destination = new FileStream(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous);
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            long total = 0;
            try
            {
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), deadline.Token)
                        .ConfigureAwait(false);
                    if (read == 0)
                        break;
                    total += read;
                    if (total > RemoteProtocol.MaxDownloadBytes)
                        throw new InvalidDataException("The produced file exceeds the mobile download limit.");
                    await destination.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                destination.Close();
                File.Delete(target);
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return target;
        }
        catch (OperationCanceledException) when (IsDeadlineCancellation(cancellationToken, deadline))
        {
            SetState(RemoteLinkState.Error, RequestTimeoutMessage);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetState(RemoteLinkState.Error, Describe(ex));
            return null;
        }
    }

    /// <summary>
    /// Sends a file to the PC and returns where it landed, so a message can point Lumi at it.
    ///
    /// <para>Takes a <see cref="ReadOnlyMemory{T}"/> rather than an array so the caller's read buffer
    /// can be passed straight through; on a phone a needless copy of a 64 MB file is the difference
    /// between working and being killed by the OS.</para>
    /// </summary>
    public async Task<RemoteUploadResponse> UploadAsync(
        string fileName,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        if (BaseUrl is not { Length: > 0 })
            return new RemoteUploadResponse { Error = "Not connected to a Lumi desktop." };

        if (content.Length > RemoteProtocol.MaxUploadBytes)
            return new RemoteUploadResponse { Error = "That file is too large to send." };

        using var deadline = CreateDeadline(cancellationToken, _uploadDeadline);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + RemoteProtocol.Routes.Upload)
            {
                Content = new ReadOnlyMemoryContent(content)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Headers.TryAddWithoutValidation(
                RemoteProtocol.UploadFileNameHeader,
                Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.GetFileName(fileName))));
            ApplyAuth(request);

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            var body = await ReadLimitedStringAsync(
                    response.Content,
                    RemoteProtocol.MaxCommandResponseJsonBytes,
                    deadline.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                SetState(RemoteLinkState.Unauthorized, "This device is no longer paired with Lumi.");
                return new RemoteUploadResponse { Error = "This device is no longer paired with Lumi." };
            }

            return JsonSerializer.Deserialize(body, RemoteJsonContext.Default.RemoteUploadResponse)
                   ?? new RemoteUploadResponse { Error = "Lumi returned an unreadable response." };
        }
        catch (OperationCanceledException) when (IsDeadlineCancellation(cancellationToken, deadline))
        {
            return new RemoteUploadResponse { Error = RequestTimeoutMessage };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RemoteUploadResponse { Error = Describe(ex) };
        }
    }

    /// <summary>Starts (or restarts) the push subscription. Safe to call repeatedly.</summary>
    public Task StartEventStreamAsync(RemoteEventSubscription? subscription = null) =>
        StartEventStreamCoreAsync(subscription, expectedGeneration: null);

    private async Task StartEventStreamCoreAsync(
        RemoteEventSubscription? subscription,
        long? expectedGeneration)
    {
        await _streamGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            if (expectedGeneration is { } expected)
            {
                if (!_streamDesired || expected != _streamGeneration)
                    return;
            }
            else
            {
                _streamDesired = true;
                _streamGeneration++;
            }
            await StopEventStreamCoreAsync().ConfigureAwait(false);

            if (BaseUrl is not { Length: > 0 } || Token is not { Length: > 0 })
                return;

            if (subscription is not null)
                TryStoreEventSubscription(subscription);
            _streamCts = new CancellationTokenSource();

            // Capture the token before handing it to the pump: the lambda must not read the field,
            // which a concurrent Stop/Dispose nulls out from under it.
            var token = _streamCts.Token;
            _streamTask = Task.Run(() => RunEventStreamAsync(token));
        }
        finally
        {
            _streamGate.Release();
        }
    }

    public async Task UpdateEventSubscriptionAsync(
        RemoteEventSubscription subscription,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (!TryStoreEventSubscription(subscription))
            return;
        var streamGeneration = Volatile.Read(ref _streamGeneration);
        if (!SupportsScopedEvents
            || BaseUrl is not { Length: > 0 }
            || Token is not { Length: > 0 })
        {
            return;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BaseUrl + RemoteProtocol.Routes.Subscription)
        {
            Content = JsonContent(subscription, RemoteJsonContext.Default.RemoteEventSubscription)
        };
        ApplyAuth(request);
        using var deadline = CreateDeadline(cancellationToken, _requestDeadline);
        var restartStream = false;
        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                SetState(RemoteLinkState.Unauthorized, "This device is no longer paired with Lumi.");
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                Trace.TraceWarning($"[Mobile] Subscription update failed: {(int)response.StatusCode}");
                restartStream = true;
            }
        }
        catch (OperationCanceledException) when (IsDeadlineCancellation(cancellationToken, deadline))
        {
            Trace.TraceWarning("[Mobile] Subscription update timed out.");
            restartStream = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceWarning($"[Mobile] Subscription update failed: {ex.Message}");
            restartStream = true;
        }

        if (restartStream && !cancellationToken.IsCancellationRequested && !_disposed && _streamDesired)
        {
            var latest = Volatile.Read(ref _eventSubscription);
            if (latest.Generation == subscription.Generation)
            {
                await StartEventStreamCoreAsync(latest, streamGeneration)
                    .ConfigureAwait(false);
            }
        }
    }

    public async Task StopEventStreamAsync()
    {
        await _streamGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            _streamDesired = false;
            _streamGeneration++;
            await StopEventStreamCoreAsync().ConfigureAwait(false);
            SetState(RemoteLinkState.Disconnected, null);
        }
        finally
        {
            _streamGate.Release();
        }
    }

    private async Task StopEventStreamCoreAsync()
    {
        var cts = _streamCts;
        var task = _streamTask;
        _streamCts = null;
        _streamTask = null;

        if (cts is not null)
        {
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Already torn down by a concurrent stop.
            }
        }

        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        // Dispose only after the pump has exited: the in-flight HTTP request holds a registration on
        // this source's token, and disposing underneath it throws ObjectDisposedException.
        cts?.Dispose();
    }

    private async Task RunEventStreamAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SetState(RemoteLinkState.Connecting, null);
                await PumpEventsAsync(cancellationToken).ConfigureAwait(false);

                // A clean end-of-stream means the desktop closed the connection: reconnect promptly.
                backoff = TimeSpan.FromSeconds(1);
                SetState(RemoteLinkState.Disconnected, "Lumi closed the connection.");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                SetState(RemoteLinkState.Unauthorized, "This device is no longer paired with Lumi.");
                return;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[Mobile] Event stream failed: {ex.Message}");
                SetState(RemoteLinkState.Error, Describe(ex));
            }

            try
            {
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Back off gently so a sleeping phone does not hammer the LAN, but stay responsive.
            backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 10_000));
        }
    }

    private async Task PumpEventsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + BuildEventsRoute());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyAuth(request);

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException();

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var frames = new RemoteEventFrame.Reader();
        var awaitingBootstrapSnapshot = true;
        await ReadEventLinesAsync(
            stream,
            line =>
            {
                if (frames.Push(line) is not { } frame)
                    return;

                if (awaitingBootstrapSnapshot)
                {
                    if (frame.Event != RemoteProtocol.Events.Snapshot
                        || JsonSerializer.Deserialize(
                               frame.Data,
                               RemoteJsonContext.Default.RemoteSnapshot) is not { } snapshot
                        || !IsSupportedProtocol(snapshot.ProtocolVersion)
                        || !RemoteProtocol.HasRequiredCapabilities(snapshot.Capabilities)
                        || snapshot.IsPartial)
                    {
                        _hasCompatibleBootstrap = false;
                        throw new InvalidDataException("Lumi did not send a valid bootstrap snapshot.");
                    }

                    _compatibleBaseUrl = BaseUrl;
                    _compatibleProtocolVersion = snapshot.ProtocolVersion;
                    ApplyCapabilities(snapshot.Capabilities);
                    _hasCompatibleBootstrap = true;
                    awaitingBootstrapSnapshot = false;
                    SetState(RemoteLinkState.Connected, null);
                    StreamFrameReceived?.Invoke(frame, true);
                    FrameReceived?.Invoke(frame);
                    return;
                }

                StreamFrameReceived?.Invoke(frame, false);
                FrameReceived?.Invoke(frame);
            },
            cancellationToken).ConfigureAwait(false);

        if (awaitingBootstrapSnapshot)
            throw new InvalidDataException("Lumi closed the stream before the bootstrap snapshot.");
    }

    private static async Task ReadEventLinesAsync(
        Stream stream,
        Action<string> consumeLine,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 16 * 1024,
            leaveOpen: true);
        var buffer = ArrayPool<char>.Shared.Rent(4096);
        var line = new StringBuilder();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(EventSilenceDeadline);
                int read;
                try
                {
                    read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), deadline.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("Lumi's live connection stopped responding.");
                }

                if (read == 0)
                    break;

                for (var index = 0; index < read; index++)
                {
                    var character = buffer[index];
                    if (character == '\n')
                    {
                        if (line.Length > 0 && line[^1] == '\r')
                            line.Length--;
                        if (Encoding.UTF8.GetByteCount(line.ToString()) > RemoteProtocol.MaxSseLineBytes)
                            throw new InvalidDataException("SSE line is too large.");
                        consumeLine(line.ToString());
                        line.Clear();
                        continue;
                    }

                    if (line.Length >= RemoteProtocol.MaxSseLineBytes)
                        throw new InvalidDataException("SSE line is too large.");
                    line.Append(character);
                }
            }

            if (line.Length > 0)
                throw new InvalidDataException("SSE stream ended with an unterminated line.");
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private async Task<T?> GetAsync<T>(string route, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        if (BaseUrl is not { Length: > 0 })
            return default;

        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + route);
        ApplyAuth(request);
        return await SendJsonAsync(request, typeInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> SendJsonAsync<T>(
        HttpRequestMessage request,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        using var deadline = CreateDeadline(cancellationToken, _requestDeadline);
        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                SetState(RemoteLinkState.Unauthorized, "This device is no longer paired with Lumi.");
                return default;
            }

            var body = await ReadLimitedStringAsync(
                    response.Content,
                    GetResponseLimit(request.RequestUri?.AbsolutePath),
                    deadline.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                SetRequestFailure(DescribeHttpFailure(response, body));
                return default;
            }

            if (State == RemoteLinkState.Connected)
                StateMessage = null;
            return JsonSerializer.Deserialize(body, typeInfo);
        }
        catch (OperationCanceledException) when (IsDeadlineCancellation(cancellationToken, deadline))
        {
            if (State == RemoteLinkState.Connected)
                StateMessage = RequestTimeoutMessage;
            else
                SetState(RemoteLinkState.Error, RequestTimeoutMessage);
            return default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetState(RemoteLinkState.Error, Describe(ex));
            return default;
        }
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        if (Token is { Length: > 0 } token)
            request.Headers.TryAddWithoutValidation(RemoteProtocol.DeviceTokenHeader, token);
    }

    private static StringContent JsonContent<T>(T value, JsonTypeInfo<T> typeInfo) =>
        new(JsonSerializer.Serialize(value, typeInfo), Encoding.UTF8, "application/json");

    internal static async Task<string> ReadLimitedStringAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } declared && declared > maxBytes)
            throw new InvalidDataException($"Response exceeds the {maxBytes:N0}-byte protocol limit.");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;
                if (output.Length + read > maxBytes)
                    throw new InvalidDataException($"Response exceeds the {maxBytes:N0}-byte protocol limit.");
                output.Write(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int GetResponseLimit(string? path) => path switch
    {
        RemoteProtocol.Routes.Hello or RemoteProtocol.Routes.Pair => RemoteProtocol.MaxHandshakeJsonBytes,
        RemoteProtocol.Routes.Snapshot => RemoteProtocol.MaxSnapshotJsonBytes,
        RemoteProtocol.Routes.Chats => RemoteProtocol.MaxChatsJsonBytes,
        RemoteProtocol.Routes.LibraryItem => RemoteProtocol.MaxLibraryItemJsonBytes,
        RemoteProtocol.Routes.Transcript => RemoteProtocol.MobileTranscriptJsonByteLimit + 64 * 1024,
        RemoteProtocol.Routes.Activity => RemoteProtocol.MaxActivityJsonBytes,
        _ => RemoteProtocol.MaxCommandResponseJsonBytes
    };

    private static string SafeDownloadFileName(string fileName)
    {
        var leaf = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(leaf))
            return "lumi-file";

        var invalid = Path.GetInvalidFileNameChars();
        return new string(leaf.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private sealed class ReadOnlyMemoryContent(ReadOnlyMemory<byte> content) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(content).AsTask();

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) =>
            stream.WriteAsync(content, cancellationToken).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = content.Length;
            return true;
        }

    }

    private sealed class RouteGuardHandler(
        HttpMessageHandler innerHandler,
        IRemoteRouteVerifier routeVerifier) : DelegatingHandler(innerHandler)
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is { } uri)
                RemoteRouteSecurity.EnsureTrusted(uri, routeVerifier);

            return base.SendAsync(request, cancellationToken);
        }
    }

    private static CancellationTokenSource CreateDeadline(
        CancellationToken cancellationToken,
        TimeSpan deadline)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(deadline);
        return linked;
    }

    private static bool IsDeadlineCancellation(
        CancellationToken callerToken,
        CancellationTokenSource deadline) =>
        !callerToken.IsCancellationRequested && deadline.IsCancellationRequested;

    private void SetState(RemoteLinkState state, string? message)
    {
        if (State == state && StateMessage == message)
            return;

        State = state;
        StateMessage = message;
        StateChanged?.Invoke(state, message);
    }

    private void SetRequestFailure(string message)
    {
        // A route-level 404/500 does not mean the live SSE transport disconnected. Preserve the
        // healthy link state while still exposing the request error to the caller/state machine.
        if (State == RemoteLinkState.Connected)
        {
            StateMessage = message;
            return;
        }

        SetState(RemoteLinkState.Error, message);
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException => "Can't reach Lumi. Check that your PC is awake and on the same Wi-Fi.",
        TaskCanceledException => RequestTimeoutMessage,
        _ => ex.Message
    };

    private static string DescribeHttpFailure(HttpResponseMessage response, string body)
    {
        try
        {
            if (JsonSerializer.Deserialize(
                    body,
                    RemoteJsonContext.Default.RemoteCommandResult) is { Error.Length: > 0 } error)
            {
                return RemoteProtocol.TruncateForMobile(
                           error.Error,
                           RemoteProtocol.MobileStatusTextLimit)
                       ?? error.Error;
            }
        }
        catch (JsonException)
        {
            // Fall through to the status-based message.
        }

        var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase)
            ? ""
            : $" ({response.ReasonPhrase})";
        return $"Lumi returned HTTP {(int)response.StatusCode}{reason}.";
    }

    public async ValueTask DisposeAsync()
    {
        await _streamGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            await StopEventStreamCoreAsync().ConfigureAwait(false);
            SetState(RemoteLinkState.Disconnected, null);
        }
        finally
        {
            _streamGate.Release();
        }

        // A caller should cancel its lifetime first (MobileShell does), but wait for the guarded
        // deserialize boundary before disposing HttpClient so teardown cannot race an active read.
        await _transcriptGate.WaitAsync().ConfigureAwait(false);
        _transcriptGate.Release();

        _http.Dispose();
    }
}
