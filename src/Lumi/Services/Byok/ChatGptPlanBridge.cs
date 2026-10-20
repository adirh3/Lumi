using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.Services.Byok;

/// <summary>
/// Serves ChatGPT plan connections to the Copilot runtime from a private loopback endpoint. The
/// runtime only ever holds this run's bridge key: the bridge adds the account's current access
/// token (renewing it with the rotating refresh token), keeps each request within the plan
/// route's contract and turns plan errors into messages the chat can show.
/// </summary>
/// <remarks>
/// Request contract: developers.openai.com/siwc/token-sharing-open-source/preview-limitations.
/// The access token never reaches the runtime or its session files, so it can rotate hourly
/// without reconfiguring sessions.
/// </remarks>
public sealed class ChatGptPlanBridge : IAsyncDisposable
{
    private static readonly Lazy<ChatGptPlanBridge> LazyShared = new(() => new ChatGptPlanBridge());

    /// <summary>The bridge every ChatGPT connection in this process shares.</summary>
    public static ChatGptPlanBridge Shared => LazyShared.Value;

    internal const string RoutePrefix = "chatgpt";
    internal const string ResponsesLiteHeader = "x-openai-internal-codex-responses-lite";
    internal const int MaximumRequestBytes = 64 * 1024 * 1024;
    private const int MaximumErrorBytes = 64 * 1024;
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(3);

    private readonly ChatGptAuthService _auth;
    private readonly bool _ownsAuth;
    private readonly HttpClient _upstream;
    private readonly Uri _upstreamBase;
    private readonly TimeProvider _time;
    private readonly byte[] _secret = Encoding.ASCII.GetBytes(ProviderOAuthService.Base64Url(RandomNumberGenerator.GetBytes(32)));
    private readonly ConcurrentDictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _failures = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private int _port;
    private bool _disposed;

    /// <summary>Raised off the UI thread with an endpoint id whose sign-in can no longer be renewed.</summary>
    public event Action<string>? SignInRequired;

    public ChatGptPlanBridge() : this(null, null, null, null) { }

    internal ChatGptPlanBridge(
        ChatGptAuthService? auth, HttpMessageHandler? upstreamHandler, Uri? upstreamBase, TimeProvider? time)
    {
        _ownsAuth = auth is null;
        _auth = auth ?? new ChatGptAuthService();
        _upstream = new HttpClient(upstreamHandler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        }, disposeHandler: upstreamHandler is null)
        {
            // Replies stream for as long as the model works; the runtime enforces its own limit.
            Timeout = Timeout.InfiniteTimeSpan
        };
        _upstreamBase = upstreamBase ?? new Uri(ChatGptAuthService.ApiBaseUrl + "/");
        _time = time ?? TimeProvider.System;
    }

    internal string Secret => Encoding.ASCII.GetString(_secret);

    /// <summary>
    /// Routes a ChatGPT connection through the bridge. The runtime receives only the loopback
    /// address and this run's bridge key; the stored refresh token is read here when needed.
    /// </summary>
    public ProviderConfig CreateProviderConfig(ByokEndpoint endpoint, ISecureKeyStore? keyStore = null)
    {
        Register(endpoint, keyStore);
        var port = EnsureStarted();
        return new ProviderConfig
        {
            Type = "openai",
            WireApi = "responses",
            BaseUrl = $"http://127.0.0.1:{port}/{RoutePrefix}/{endpoint.Id}/v1",
            ApiKey = Secret,
        };
    }

    /// <summary>Makes a saved connection's account known so its stored refresh token can be used.</summary>
    public void Register(ByokEndpoint endpoint, ISecureKeyStore? keyStore = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Account is not { ClientId.Length: > 0 } account)
            throw new InvalidOperationException(Loc.Get("ChatGpt_SignInRequired"));
        var state = _accounts.GetOrAdd(endpoint.Id, static _ => new Account());
        lock (state)
        {
            state.ClientId = account.ClientId;
            if (endpoint.ApiKeyMode == ByokApiKeyMode.CredentialStore)
            {
                state.KeyStore = keyStore ?? SecureKeyStoreFactory.Instance;
                state.Persisted = true;
            }
        }
    }

    /// <summary>Holds the tokens a sign-in just issued, so a connection works before it is saved.</summary>
    public void Seed(string endpointId, ChatGptAccount account, ChatGptTokens tokens)
    {
        var state = _accounts.GetOrAdd(endpointId, static _ => new Account());
        lock (state)
        {
            state.ClientId = account.ClientId;
            state.AccessToken = tokens.AccessToken;
            state.AccessExpiresAt = tokens.ExpiresAt;
            state.RefreshToken = tokens.RefreshToken;
            state.SignInRequired = false;
            state.Persisted = false;
        }
    }

    /// <summary>
    /// Stores the account's current refresh token so the connection survives a restart, and keeps
    /// storing each replacement from then on.
    /// </summary>
    public async Task PersistAsync(string endpointId, ISecureKeyStore keyStore, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keyStore);
        if (!_accounts.TryGetValue(endpointId, out var state))
            throw new ProviderConnectionException(Loc.Get("ChatGpt_SignInRequired"));
        // Holding the refresh gate means no rotation can land between reading and storing the token.
        await state.Refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? token;
            lock (state)
                token = state.RefreshToken;
            if (token is null)
                throw new ProviderConnectionException(Loc.Get("ChatGpt_SignInRequired"));
            await keyStore.SetAsync(SecureKeyStoreFactory.EndpointKey(endpointId), token).ConfigureAwait(false);
            lock (state)
            {
                state.KeyStore = keyStore;
                state.Persisted = true;
            }
        }
        finally
        {
            state.Refresh.Release();
        }
    }

    /// <summary>Stops serving an account and returns its refresh token, so the caller can revoke it.</summary>
    public async Task<string?> RemoveAsync(string endpointId, CancellationToken cancellationToken)
    {
        if (!_accounts.TryRemove(endpointId, out var state))
            return null;
        await state.Refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (state)
            {
                var token = state.RefreshToken;
                state.AccessToken = null;
                state.RefreshToken = null;
                state.SignInRequired = true;
                return token;
            }
        }
        finally
        {
            state.Refresh.Release();
        }
    }

    /// <summary>
    /// Returns a usable access token, renewing it near expiry or after the plan route rejected
    /// <paramref name="rejectedToken"/>. Concurrent callers share one renewal.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(string endpointId, string? rejectedToken, CancellationToken cancellationToken)
    {
        if (!_accounts.TryGetValue(endpointId, out var state))
            throw new ChatGptRefreshException(Loc.Get("ChatGpt_SignInRequired"), requiresSignIn: true);
        await state.Refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? refreshToken;
            string clientId;
            ISecureKeyStore? keyStore;
            bool persisted;
            lock (state)
            {
                if (state.AccessToken is { } current && state.AccessExpiresAt - _time.GetUtcNow() > RefreshMargin
                    && !string.Equals(current, rejectedToken, StringComparison.Ordinal))
                    return current;
                if (state.SignInRequired)
                    throw new ChatGptRefreshException(Loc.Get("ChatGpt_SignInExpired"), requiresSignIn: true);
                refreshToken = state.RefreshToken;
                clientId = state.ClientId;
                keyStore = state.KeyStore;
                persisted = state.Persisted;
            }

            if (refreshToken is null && persisted && keyStore is { IsSupported: true })
                refreshToken = await keyStore.GetAsync(SecureKeyStoreFactory.EndpointKey(endpointId)).ConfigureAwait(false);
            if (string.IsNullOrEmpty(refreshToken) || string.IsNullOrEmpty(clientId))
                throw RequireSignIn(endpointId, state, Loc.Get("ChatGpt_SignInRequired"));

            ChatGptTokens tokens;
            try
            {
                tokens = await _auth.RefreshAsync(clientId, refreshToken, cancellationToken).ConfigureAwait(false);
            }
            catch (ChatGptRefreshException ex) when (ex.RequiresSignIn)
            {
                throw RequireSignIn(endpointId, state, ex.Message);
            }

            lock (state)
            {
                state.AccessToken = tokens.AccessToken;
                state.AccessExpiresAt = tokens.ExpiresAt;
                state.RefreshToken = tokens.RefreshToken;
            }
            if (persisted && keyStore is { IsSupported: true })
            {
                // Refresh tokens rotate: the replacement must outlive this process or the next launch
                // would present a spent token. A failed write still leaves this session working.
                try { await keyStore.SetAsync(SecureKeyStoreFactory.EndpointKey(endpointId), tokens.RefreshToken).ConfigureAwait(false); }
                catch (Exception ex) { Trace.TraceWarning("ChatGPT refresh token could not be stored ({0}).", ex.GetType().Name); }
            }
            return tokens.AccessToken;
        }
        finally
        {
            state.Refresh.Release();
        }
    }

    /// <summary>Remembers which of the account's models take the "responses lite" form.</summary>
    public void UseCatalog(string endpointId, ChatGptCatalog catalog)
    {
        var state = _accounts.GetOrAdd(endpointId, static _ => new Account());
        foreach (var slug in catalog.ResponsesLiteModels)
            state.LiteModels[slug] = true;
        lock (state)
            state.CatalogTask ??= Task.CompletedTask;
    }

    /// <summary>Learns the request form of each model from the plan's catalog, once per account.</summary>
    private async Task EnsureCatalogAsync(Account state, string token, CancellationToken cancellationToken)
    {
        Task task;
        lock (state)
            task = state.CatalogTask ??= LoadCatalogAsync(state, token);
        try { await task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    private async Task LoadCatalogAsync(Account state, string token)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var catalog = await _auth.GetCatalogAsync(token, timeout.Token).ConfigureAwait(false);
            foreach (var slug in catalog.ResponsesLiteModels)
                state.LiteModels[slug] = true;
        }
        catch (Exception ex)
        {
            // Without the catalog, a refusal of top-level tools still switches the model's form.
            Trace.TraceWarning("ChatGPT model catalog unavailable ({0}).", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Returns and forgets the last refusal the plan route gave this connection, already worded for
    /// the user, so a caller whose request failed can say why instead of a generic error.
    /// </summary>
    public string? TakeFailure(string endpointId) => _failures.TryRemove(endpointId, out var failure) ? failure : null;

    private void RecordFailure(string endpointId, int status, string? code, string? param, string message, string? requestId)
    {
        _failures[endpointId] = message;
        Trace.TraceWarning("ChatGPT plan request failed: HTTP {0}, code {1}, param {2}, request {3}.",
            status, code ?? "-", param ?? "-", requestId ?? "-");
    }

    private ChatGptRefreshException RequireSignIn(string endpointId, Account state, string message)
    {
        lock (state)
        {
            state.SignInRequired = true;
            state.AccessToken = null;
            state.RefreshToken = null;
        }
        try { SignInRequired?.Invoke(endpointId); }
        catch (Exception ex) { Trace.TraceWarning("ChatGPT sign-in notification failed ({0}).", ex.GetType().Name); }
        return new ChatGptRefreshException(message, requiresSignIn: true);
    }

    private int EnsureStarted()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is { IsListening: true })
                return _port;

            Exception? lastError = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var port = GetFreeLoopbackPort();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    var cts = new CancellationTokenSource();
                    _listener = listener;
                    _listenerCts = cts;
                    _port = port;
                    _ = Task.Run(() => ListenAsync(listener, cts.Token));
                    return port;
                }
                catch (Exception ex) when (ex is HttpListenerException or SocketException)
                {
                    lastError = ex;
                    listener.Close();
                }
            }
            throw new InvalidOperationException(Loc.Get("ChatGpt_Unreachable"), lastError);
        }
    }

    private static int GetFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    private async Task ListenAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch when (cancellationToken.IsCancellationRequested || !listener.IsListening)
            {
                break;
            }
            catch (HttpListenerException)
            {
                continue;
            }
            _ = Task.Run(() => HandleAsync(context, cancellationToken));
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken lifetime)
    {
        try
        {
            await ServeAsync(context, lifetime).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException
                                   or OperationCanceledException)
        {
            // The runtime closed the request, for example because the user stopped the reply.
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("ChatGPT bridge request failed ({0}).", ex.GetType().Name);
            try { await WriteErrorAsync(context.Response, 502, "bridge_error", Loc.Get("ChatGpt_Unreachable")).ConfigureAwait(false); }
            catch { }
        }
        finally
        {
            try { context.Response.Close(); }
            catch { }
        }
    }

    private async Task ServeAsync(HttpListenerContext context, CancellationToken lifetime)
    {
        var request = context.Request;
        var response = context.Response;
        if (!IsAuthorized(request.Headers["Authorization"]))
        {
            await WriteErrorAsync(response, 401, "invalid_api_key", Loc.Get("ChatGpt_SignInRequired")).ConfigureAwait(false);
            return;
        }
        if (!TryParseRoute(request.Url?.AbsolutePath, out var endpointId) || request.HttpMethod != "POST")
        {
            await WriteErrorAsync(response, 404, "not_found", Loc.Get("ChatGpt_Unsupported")).ConfigureAwait(false);
            return;
        }
        if (!_accounts.TryGetValue(endpointId, out var state))
        {
            await WriteErrorAsync(response, 401, "sign_in_required", Loc.Get("ChatGpt_SignInRequired")).ConfigureAwait(false);
            return;
        }
        if (request.ContentLength64 > MaximumRequestBytes)
        {
            await WriteErrorAsync(response, 413, "request_too_large", Loc.Get("ChatGpt_RequestTooLarge")).ConfigureAwait(false);
            return;
        }

        JsonObject body;
        try
        {
            var bytes = await ReadRequestAsync(request.InputStream, lifetime).ConfigureAwait(false);
            if (bytes is null)
            {
                await WriteErrorAsync(response, 413, "request_too_large", Loc.Get("ChatGpt_RequestTooLarge")).ConfigureAwait(false);
                return;
            }
            body = JsonNode.Parse(bytes) as JsonObject ?? throw new JsonException();
        }
        catch (JsonException)
        {
            await WriteErrorAsync(response, 400, "invalid_request", Loc.Get("ChatGpt_Unsupported")).ConfigureAwait(false);
            return;
        }

        await ForwardAsync(response, endpointId, state, body, lifetime).ConfigureAwait(false);
    }

    private async Task ForwardAsync(
        HttpListenerResponse response, string endpointId, Account state, JsonObject body, CancellationToken lifetime)
    {
        var model = body["model"] is JsonValue modelValue && modelValue.TryGetValue<string>(out var id) ? id : null;
        string token;
        try
        {
            token = await GetAccessTokenAsync(endpointId, null, lifetime).ConfigureAwait(false);
        }
        catch (ChatGptRefreshException ex)
        {
            RecordFailure(endpointId, ex.RequiresSignIn ? 401 : 503, null, null, ex.Message, null);
            await WriteErrorAsync(response, ex.RequiresSignIn ? 401 : 503, ex.RequiresSignIn ? "sign_in_required" : "unavailable",
                ex.Message, retry: !ex.RequiresSignIn).ConfigureAwait(false);
            return;
        }
        await EnsureCatalogAsync(state, token, lifetime).ConfigureAwait(false);
        var lite = model is not null && state.LiteModels.ContainsKey(model);

        var refreshed = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var payload = ChatGptRequestNormalizer.Serialize(ChatGptRequestNormalizer.Normalize(body, lite));
            using var upstream = await SendUpstreamAsync(payload, token, lite, lifetime).ConfigureAwait(false);
            if (upstream.IsSuccessStatusCode)
            {
                await StreamAsync(upstream, response, lifetime).ConfigureAwait(false);
                return;
            }

            var errorBody = await ReadErrorAsync(upstream, lifetime).ConfigureAwait(false);
            var error = ChatGptPlanErrors.Read(errorBody);
            if (upstream.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
            {
                refreshed = true;
                try
                {
                    token = await GetAccessTokenAsync(endpointId, token, lifetime).ConfigureAwait(false);
                    continue;
                }
                catch (ChatGptRefreshException ex) when (!ex.RequiresSignIn)
                {
                }
                catch (ChatGptRefreshException ex)
                {
                    RecordFailure(endpointId, 401, "sign_in_required", null, ex.Message, null);
                    await WriteErrorAsync(response, 401, "sign_in_required", ex.Message, retry: false).ConfigureAwait(false);
                    return;
                }
            }
            // Some models take tools only through additional_tools input items (as Codex sends them).
            if (!lite && model is not null && error.IsUnsupportedTools)
            {
                lite = true;
                state.LiteModels[model] = true;
                continue;
            }

            var status = (int)upstream.StatusCode;
            var message = ChatGptPlanErrors.Describe(upstream.StatusCode, error)
                ?? (error.Message is { Length: > 0 } detail
                    ? string.Format(Loc.Get("ChatGpt_Refused"), detail.Length > 300 ? detail[..300] + "…" : detail)
                    : ProviderConnectionService.ErrorForStatus(upstream.StatusCode));
            var requestId = upstream.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
            RecordFailure(endpointId, status, error.Code, error.Param, message, requestId);
            await WriteErrorAsync(response, status, error.Code ?? "chatgpt_error", message, error.Param,
                retry: status is 408 or 409 or >= 500 && error.Code != "subscription_sharing_usage_limit_exceeded",
                requestId: requestId).ConfigureAwait(false);
            return;
        }
    }

    private async Task<HttpResponseMessage> SendUpstreamAsync(
        byte[] payload, string token, bool lite, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_upstreamBase, "responses"))
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.UserAgent.ParseAdd(ChatGptAuthService.UserAgent);
        // Codex marks requests in the lite form this way, so the route reads their additional_tools.
        if (lite)
            request.Headers.Add(ResponsesLiteHeader, "true");
        return await _upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task StreamAsync(HttpResponseMessage upstream, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        response.StatusCode = 200;
        response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "text/event-stream";
        response.SendChunked = true;
        response.Headers["Cache-Control"] = "no-store";
        if (upstream.Headers.TryGetValues("x-request-id", out var ids))
            response.Headers["x-request-id"] = ids.First();
        await using var source = await upstream.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await ChatGptPlanErrors.CopyEventsAsync(source, response.OutputStream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadRequestAsync(Stream input, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > MaximumRequestBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static async Task<byte[]> ReadErrorAsync(HttpResponseMessage upstream, CancellationToken cancellationToken)
    {
        try
        {
            return await ChatGptAuthService.ReadBoundedAsync(upstream, MaximumErrorBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return [];
        }
    }

    private bool IsAuthorized(string? header)
    {
        const string prefix = "Bearer ";
        if (header is null || !header.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(header[prefix.Length..]), _secret);
    }

    internal static bool TryParseRoute(string? path, out string endpointId)
    {
        endpointId = "";
        var segments = (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is not [RoutePrefix, var id, "v1", "responses"] || !IsEndpointId(id))
            return false;
        endpointId = id;
        return true;
    }

    /// <summary>
    /// Recognizes a provider address the bridge hands out (<c>http://127.0.0.1:{port}/chatgpt/{id}/v1</c>)
    /// and returns the connection it serves. The port and key change with every launch; the connection does not.
    /// </summary>
    internal static bool TryGetBridgedEndpoint(string? baseUrl, out string endpointId)
    {
        endpointId = "";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback
            || uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is not [RoutePrefix, var id, "v1"]
            || !IsEndpointId(id))
            return false;
        endpointId = id;
        return true;
    }

    private static bool IsEndpointId(string id)
        => id.Length <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static async Task WriteErrorAsync(
        HttpListenerResponse response, int status, string code, string message, string? param = null,
        bool retry = false, string? requestId = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("error");
            writer.WriteString("message", message);
            writer.WriteString("type", "chatgpt_plan_error");
            writer.WriteString("code", code);
            if (param is null) writer.WriteNull("param");
            else writer.WriteString("param", param);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        var bytes = buffer.ToArray();
        response.StatusCode = status;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        // OpenAI SDKs honor this hint, so a reached plan limit is not retried on the user's behalf.
        response.Headers["x-should-retry"] = retry ? "true" : "false";
        if (requestId is not null)
            response.Headers["x-request-id"] = requestId;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        HttpListener? listener;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            listener = _listener;
            cts = _listenerCts;
            _listener = null;
            _listenerCts = null;
        }
        cts?.Cancel();
        if (listener is not null)
        {
            try { listener.Stop(); } catch { }
            listener.Close();
        }
        cts?.Dispose();
        _upstream.Dispose();
        if (_ownsAuth)
            _auth.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private sealed class Account
    {
        /// <summary>Serializes renewals: refresh tokens rotate, so two renewals must never race.</summary>
        public readonly SemaphoreSlim Refresh = new(1, 1);
        public readonly ConcurrentDictionary<string, bool> LiteModels = new(StringComparer.Ordinal);
        public Task? CatalogTask;
        public string ClientId = "";
        public ISecureKeyStore? KeyStore;
        public bool Persisted;
        public string? AccessToken;
        public DateTimeOffset AccessExpiresAt;
        public string? RefreshToken;
        public bool SignInRequired;
    }
}

/// <summary>Keeps a runtime Responses request within the ChatGPT plan route's preview contract.</summary>
internal static class ChatGptRequestNormalizer
{
    internal const string FunctionNamespace = "functions";
    private const string CacheBreakpoint = "prompt_cache_breakpoint";

    private static readonly string[] UnsupportedFields =
    [
        "background", "conversation", "max_output_tokens", "max_tool_calls", "metadata", "moderation",
        "multi_agent", "prompt", "prompt_cache_retention", "safety_identifier", "temperature", "top_logprobs",
        "top_p", "truncation", "user", "previous_response_id"
    ];

    private static readonly HashSet<string> UnsupportedTools = new(StringComparer.Ordinal)
    {
        "image_generation", "file_search", "code_interpreter", "computer_use_preview", "computer_use", "computer",
        "mcp", "tool_search", "programmatic_tool_calling"
    };

    /// <summary>
    /// Returns a copy of <paramref name="source"/> that is stateless and streamed, carries only
    /// supported fields and tools, and uses developer rather than system messages. With
    /// <paramref name="lite"/>, function tools move into an <c>additional_tools</c> input item under
    /// the <c>functions</c> namespace, the form Codex uses for such models.
    /// </summary>
    public static JsonObject Normalize(JsonObject source, bool lite)
    {
        var body = (JsonObject)source.DeepClone();
        foreach (var field in UnsupportedFields)
            body.Remove(field);
        body["store"] = false;
        body["stream"] = true;

        if (body["input"] is JsonValue text && text.TryGetValue<string>(out var prompt))
            body["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = prompt })
            });
        var input = body["input"] as JsonArray;
        foreach (var item in input?.OfType<JsonObject>() ?? [])
        {
            if (Text(item["role"]) == "system" && Text(item["type"]) is null or "message")
                item["role"] = "developer";
            // The runtime marks explicit cache breakpoints for newer models; the plan route rejects them.
            item.Remove(CacheBreakpoint);
            foreach (var part in (item["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
                part.Remove(CacheBreakpoint);
        }

        if (body["tools"] is JsonArray tools)
        {
            foreach (var tool in tools.OfType<JsonObject>().Where(tool => UnsupportedTools.Contains(Text(tool["type"]) ?? "")).ToList())
                tools.Remove(tool);
            if (lite && input is not null)
                MoveToolsIntoInput(body, tools, input);
            else if (tools.Count == 0)
                body.Remove("tools");
        }
        if (lite && input is not null)
        {
            body["parallel_tool_calls"] = false;
            // As Codex does for these models: reasoning carried in history applies across turns.
            if (body["reasoning"] is JsonObject reasoning)
                reasoning["context"] = "all_turns";
            foreach (var item in input.OfType<JsonObject>())
                if (Text(item["type"]) is "function_call" or "custom_tool_call" && item["namespace"] is null)
                    item["namespace"] = FunctionNamespace;
        }
        return body;
    }

    private static void MoveToolsIntoInput(JsonObject body, JsonArray tools, JsonArray input)
    {
        var items = tools.ToArray();
        tools.Clear();
        body.Remove("tools");
        var functions = new JsonArray();
        var others = new List<JsonNode>();
        foreach (var tool in items.OfType<JsonObject>())
        {
            switch (Text(tool["type"]))
            {
                case "function" or "custom":
                    functions.Add((JsonNode)tool);
                    break;
                case "namespace" when Text(tool["name"]) == FunctionNamespace && tool["tools"] is JsonArray nested:
                    var children = nested.ToArray();
                    nested.Clear();
                    foreach (var child in children)
                        functions.Add(child);
                    break;
                default:
                    others.Add(tool);
                    break;
            }
        }
        var additional = new JsonArray();
        if (functions.Count > 0)
            additional.Add((JsonNode)new JsonObject
            {
                ["type"] = "namespace", ["name"] = FunctionNamespace, ["description"] = "", ["tools"] = functions
            });
        foreach (var other in others)
            additional.Add(other);
        if (additional.Count > 0)
            input.Insert(0, new JsonObject { ["type"] = "additional_tools", ["role"] = "developer", ["tools"] = additional });
    }

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>Writes UTF-8 JSON without reflection (trimmed builds) or escaping of non-ASCII text.</summary>
    public static byte[] Serialize(JsonNode node)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            node.WriteTo(writer);
        return buffer.ToArray();
    }
}

/// <summary>Reads ChatGPT plan errors and words them for the chat.</summary>
internal static class ChatGptPlanErrors
{
    internal readonly record struct PlanError(string? Code, string? Param, string? Message)
    {
        public bool IsUnsupportedTools => Code == "subscription_sharing_unsupported_capability"
            && Param is { } param && (param == "tools" || param.StartsWith("tools[", StringComparison.Ordinal)
                                      || param.StartsWith("tools.", StringComparison.Ordinal));
    }

    /// <summary>Reads a Responses error (<c>{"error":{…}}</c>) or a direct-admission one (<c>{"detail":…}</c>).</summary>
    public static PlanError Read(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return Read(json.RootElement);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static PlanError Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return default;
        var error = root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
        return new PlanError(String(error, "code"), String(error, "param"), String(error, "message") ?? String(root, "detail"));
    }

    public static string? Describe(HttpStatusCode status, ReadOnlyMemory<byte> body) => Describe(status, Read(body));

    public static string? Describe(HttpStatusCode status, PlanError error) => error.Code switch
    {
        "subscription_sharing_usage_limit_exceeded" => Loc.Get("ChatGpt_UsageLimit"),
        "subscription_sharing_user_not_eligible" => Loc.Get("ChatGpt_NotEligible"),
        "subscription_sharing_usage_unavailable" or "subscription_sharing_user_unavailable" => Loc.Get("ChatGpt_Unavailable"),
        "subscription_sharing_invalid_user" => Loc.Get("ChatGpt_InvalidUser"),
        "subscription_sharing_route_not_supported" or "chatpass_v2_scope_not_authorized"
            or "chatpass_v2_invalid_authorization_context" => Loc.Get("ChatGpt_NotAuthorized"),
        "subscription_sharing_unsupported_capability" => error.Param is { Length: > 0 } param
            ? string.Format(Loc.Get("ChatGpt_UnsupportedPart"), param) : Loc.Get("ChatGpt_Unsupported"),
        null when status == HttpStatusCode.Unauthorized => Loc.Get("ChatGpt_InvalidUser"),
        null when status == HttpStatusCode.Forbidden => Loc.Get("ChatGpt_NotAuthorized"),
        null when status == HttpStatusCode.ServiceUnavailable => Loc.Get("ChatGpt_Unavailable"),
        _ => null
    };

    private static ReadOnlySpan<byte> DataPrefix => "data:"u8;

    /// <summary>
    /// Streams server-sent events through as they arrive, except that a plan error arriving
    /// mid-stream (<c>response.failed</c> or <c>error</c>) gets the same wording as one returned up
    /// front, and a final response without its output gets the items the stream completed: the plan
    /// route ends with <c>"output":[]</c>, and the runtime reads the reply and tool calls from there.
    /// Complete lines are written as they arrive, so streaming is not delayed.
    /// </summary>
    public static async Task CopyEventsAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var items = new SortedDictionary<int, JsonNode>();
        using var pending = new MemoryStream();
        using var output = new MemoryStream();
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            var start = 0;
            for (var index = Array.IndexOf(buffer, (byte)'\n', 0, read); index >= 0;
                 index = Array.IndexOf(buffer, (byte)'\n', start, read - start))
            {
                if (pending.Length > 0)
                {
                    pending.Write(buffer, start, index + 1 - start);
                    WriteLine(output, pending.GetBuffer().AsSpan(0, (int)pending.Length), items);
                    pending.SetLength(0);
                }
                else
                    WriteLine(output, buffer.AsSpan(start, index + 1 - start), items);
                start = index + 1;
                if (start >= read)
                    break;
            }
            if (start < read)
                pending.Write(buffer, start, read - start);
            if (output.Length > 0)
            {
                await destination.WriteAsync(output.GetBuffer().AsMemory(0, (int)output.Length), cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.SetLength(0);
            }
        }
        if (pending.Length > 0)
        {
            WriteLine(output, pending.GetBuffer().AsSpan(0, (int)pending.Length), items);
            await destination.WriteAsync(output.GetBuffer().AsMemory(0, (int)output.Length), cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void WriteLine(MemoryStream output, ReadOnlySpan<byte> line, SortedDictionary<int, JsonNode> items)
    {
        if (line.StartsWith(DataPrefix))
        {
            // Both checks confirm the parsed event type, so an event that merely mentions another
            // event's name (completed events echo the tools, for example) is still handled as itself.
            if (line.IndexOf("response.output_item.done"u8) >= 0)
                CollectItem(line, items);
            if (items.Count > 0
                && (line.IndexOf("response.completed"u8) >= 0 || line.IndexOf("response.incomplete"u8) >= 0)
                && CompleteOutput(line, items) is { } completed)
            {
                output.Write(completed);
                return;
            }
            if ((line.IndexOf("subscription_sharing_"u8) >= 0 || line.IndexOf("chatpass_v2_"u8) >= 0)
                && RewriteEvent(line) is { } rewritten)
            {
                output.Write(rewritten);
                return;
            }
        }
        output.Write(line);
    }

    private static void CollectItem(ReadOnlySpan<byte> line, SortedDictionary<int, JsonNode> items)
    {
        if (ParseEvent(line, out _) is { } json && Text(json["type"]) == "response.output_item.done"
            && json["item"] is JsonObject item)
            items[json["output_index"] is JsonValue index && index.TryGetValue<int>(out var position) ? position : items.Count]
                = item.DeepClone();
    }

    /// <summary>Puts the completed items into a final response that arrived without output.</summary>
    internal static byte[]? CompleteOutput(ReadOnlySpan<byte> line, SortedDictionary<int, JsonNode> items)
    {
        if (ParseEvent(line, out var trailing) is not { } json
            || Text(json["type"]) is not ("response.completed" or "response.incomplete")
            || json["response"] is not JsonObject response
            || response["output"] is JsonArray { Count: > 0 })
            return null;
        response["output"] = new JsonArray(items.Values.Select(item => item.DeepClone()).ToArray());
        return [.. "data: "u8, .. ChatGptRequestNormalizer.Serialize(json), .. line[^trailing..]];
    }

    private static JsonObject? ParseEvent(ReadOnlySpan<byte> line, out int trailing)
    {
        var content = line[DataPrefix.Length..];
        var json = content.TrimEnd("\r\n"u8);
        trailing = content.Length - json.Length;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static byte[]? RewriteEvent(ReadOnlySpan<byte> line)
    {
        var content = line[DataPrefix.Length..];
        var trailing = content.Length - content.TrimEnd("\r\n"u8).Length;
        try
        {
            if (JsonNode.Parse(content.TrimEnd("\r\n"u8)) is not JsonObject json)
                return null;
            var error = Text(json["type"]) == "response.failed" ? json["response"]?["error"] as JsonObject
                : json["error"] as JsonObject ?? (Text(json["type"]) == "error" ? json : null);
            if (error is null)
                return null;
            var message = Describe(HttpStatusCode.OK, new PlanError(Text(error["code"]), Text(error["param"]), null));
            if (message is null)
                return null;
            error["message"] = message;
            return [.. "data: "u8, .. ChatGptRequestNormalizer.Serialize(json), .. content[^trailing..]];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string? String(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;
}
