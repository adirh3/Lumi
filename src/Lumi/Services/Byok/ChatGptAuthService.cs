using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Localization;

namespace Lumi.Services.Byok;

/// <summary>The ChatGPT account a connection signed in with. Holds no secrets.</summary>
public sealed record ChatGptAccount(string ClientId, string Subject, string? Email);

/// <summary>A renewable ChatGPT plan credential: an hour-long access token and a rotating refresh token.</summary>
public sealed class ChatGptTokens
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = [];
    public bool CanUsePlan => Scopes.Contains(ChatGptAuthService.PlanScope, StringComparer.Ordinal);
    public override string ToString() => "ChatGptTokens (redacted)";
}

public sealed record ChatGptSignIn(ChatGptAccount Account, ChatGptTokens Tokens)
{
    public override string ToString() => "ChatGptSignIn (redacted)";
}

/// <summary>A plan's models, and the ones that take tools in the "responses lite" request form.</summary>
public sealed record ChatGptCatalog(IReadOnlyList<ProviderModel> Models, IReadOnlySet<string> ResponsesLiteModels);

/// <summary>A failed renewal. <see cref="RequiresSignIn"/> means this token set can never be renewed again.</summary>
public sealed class ChatGptRefreshException(string message, bool requiresSignIn) : Exception(message)
{
    public bool RequiresSignIn { get; } = requiresSignIn;
}

/// <summary>
/// Sign in with ChatGPT for open-source apps: registers Lumi as an agent of the user's ChatGPT
/// account and obtains renewable OAuth tokens that bill eligible Responses API requests to the
/// user's ChatGPT plan instead of an API key.
/// </summary>
/// <remarks>
/// Contract: developers.openai.com/siwc/token-sharing-open-source (sign-in, accounts and sessions,
/// token reference, errors and recovery). Endpoints match auth.openai.com's discovery document.
/// An injected client is not owned and must have automatic redirects disabled.
/// </remarks>
public sealed class ChatGptAuthService : IDisposable
{
    public const string Issuer = "https://auth.openai.com";
    internal const string AuthorizeEndpoint = Issuer + "/api/accounts/authorize";
    internal const string TokenEndpoint = Issuer + "/api/accounts/oauth/token";
    internal const string RevocationEndpoint = Issuer + "/api/accounts/oauth/revoke";
    internal const string JwksEndpoint = Issuer + "/.well-known/jwks.json";
    public const string ApiBaseUrl = "https://api.openai.com/v1";
    public const string UsageUrl = "https://chatgpt.com/settings/usage";
    public const string PlanScope = "chatgpt.tokens.use.direct";
    internal const string DynamicClientId = "dynamic_agent_client";
    internal const string AgentName = "Lumi";
    internal const string Scope = "openid profile email offline_access resource.invoke " + PlanScope;
    internal const string CallbackPath = "/auth/callback";
    internal const int MaximumResponseBytes = 256 * 1024;

    // Codex's catalog lists efforts beyond the runtime's (e.g. "ultra"); only send ones Lumi can select.
    private static readonly string[] EffortOrder = ["minimal", "low", "medium", "high", "xhigh", "max"];
    private static readonly HashSet<string> TerminalRefreshErrors = new(StringComparer.Ordinal)
    {
        "invalid_grant", "invalid_refresh_token", "token_expired", "refresh_token_expired",
        "refresh_token_invalidated", "refresh_token_reused", "invalid_client"
    };

    internal static string UserAgent { get; } = "Lumi/" + (typeof(ChatGptAuthService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0");

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _authorizationTimeout;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _jwksGate = new(1, 1);
    private Dictionary<string, RSAParameters>? _signingKeys;
    private int _disposed;

    public ChatGptAuthService(HttpClient? http = null) : this(http, TimeSpan.FromMinutes(10)) { }

    internal ChatGptAuthService(HttpClient? http, TimeSpan authorizationTimeout)
    {
        if (authorizationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(authorizationTimeout));
        _authorizationTimeout = authorizationTimeout;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>
    /// Opens ChatGPT in the browser and completes the loopback sign-in. Without
    /// <paramref name="existing"/> the user registers Lumi as a new agent; with it, the account's
    /// issued client is reauthorized so its ChatGPT usage settings carry over.
    /// </summary>
    public async Task<ChatGptSignIn> SignInAsync(
        string hostId, ChatGptAccount? existing, Func<Uri, Task> openBrowser, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(openBrowser);
        cancellationToken.ThrowIfCancellationRequested();

        using var flow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        flow.CancelAfter(_authorizationTimeout);
        var listener = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        try
        {
            listener.Start(1);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var callback = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{CallbackPath}");
            var (verifier, challenge, state) = ProviderOAuthService.CreatePkceParameters();
            var nonce = ProviderOAuthService.Base64Url(RandomNumberGenerator.GetBytes(32));
            var authorization = CreateAuthorizationUri(callback, challenge, state, nonce, hostId, existing);

            var callbackTask = ProviderOAuthService.ReceiveCallbackAsync(
                listener, callback, state, ProviderOAuthKind.ChatGpt, flow);
            var browserTask = ProviderOAuthService.OpenBrowserAsync(openBrowser, authorization, flow);
            await Task.WhenAll(browserTask, callbackTask).ConfigureAwait(false);
            var values = await callbackTask.ConfigureAwait(false);

            var clientId = ResolveIssuedClientId(values, existing);
            var (tokens, idToken) = await ExchangeCodeAsync(clientId, values["code"], verifier, callback, flow.Token)
                .ConfigureAwait(false);
            if (idToken is null)
                throw new ProviderOAuthException(Loc.Get("ChatGpt_SignInInvalid"));
            var account = await ValidateIdTokenAsync(idToken, clientId, nonce, DateTimeOffset.UtcNow, flow.Token)
                .ConfigureAwait(false);
            if (existing is not null && !string.Equals(existing.Subject, account.Subject, StringComparison.Ordinal))
                throw new ProviderOAuthException(Loc.Get("ChatGpt_DifferentAccount"));

            // Scopes come from the token response; fall back to the callback's report of the grant.
            if (tokens.Scopes.Count == 0 && values.TryGetValue("scope", out var callbackScope))
                tokens = new ChatGptTokens
                {
                    AccessToken = tokens.AccessToken, RefreshToken = tokens.RefreshToken,
                    ExpiresAt = tokens.ExpiresAt, Scopes = SplitScopes(callbackScope)
                };
            if (!tokens.CanUsePlan)
                throw new ProviderOAuthException(Loc.Get("ChatGpt_PlanNotEnabled"));
            return new ChatGptSignIn(account, tokens);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            throw new OperationCanceledException(Loc.ChatGpt_SignInCanceled);
        }
        catch (OperationCanceledException) when (flow.IsCancellationRequested)
        {
            throw new ProviderOAuthException(Loc.Get("ChatGpt_SignInTimedOut"));
        }
        catch (SocketException)
        {
            throw new ProviderOAuthException(
                Loc.EasyConnect_CallbackUnavailable);
        }
        finally
        {
            flow.Cancel();
            listener.Stop();
        }
    }

    internal static Uri CreateAuthorizationUri(
        Uri callback, string challenge, string state, string nonce, string hostId, ChatGptAccount? existing)
    {
        var values = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", existing?.ClientId ?? DynamicClientId),
            new("redirect_uri", callback.AbsoluteUri),
            new("scope", Scope),
            new("resource", ApiBaseUrl),
            new("ext_agent_host_id", hostId),
            new("state", state),
            new("nonce", nonce),
            new("code_challenge", challenge),
            new("code_challenge_method", "S256"),
        };
        // The name hint belongs only to first registration; a returning account is identified by its email.
        if (existing is null)
            values.Add(new("agent_name_hint", AgentName));
        else if (!string.IsNullOrWhiteSpace(existing.Email))
            values.Add(new("login_hint", existing.Email));
        return new Uri(AuthorizeEndpoint + "?" + string.Join("&", values.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
    }

    /// <summary>
    /// A new registration must return the client ChatGPT issued to Lumi; a reauthorization keeps its
    /// own and rejects a callback naming any other.
    /// </summary>
    internal static string ResolveIssuedClientId(IReadOnlyDictionary<string, string> callback, ChatGptAccount? existing)
    {
        callback.TryGetValue("client_id", out var returned);
        if (existing is not null)
        {
            if (!string.IsNullOrEmpty(returned) && !string.Equals(returned, existing.ClientId, StringComparison.Ordinal))
                throw new ProviderOAuthException(Loc.Get("ChatGpt_DifferentAccount"));
            return existing.ClientId;
        }
        if (string.IsNullOrWhiteSpace(returned) || returned == DynamicClientId || returned.Length > 256
            || returned.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new ProviderOAuthException(Loc.Get("ChatGpt_RegistrationIncomplete"));
        return returned;
    }

    private async Task<(ChatGptTokens Tokens, string? IdToken)> ExchangeCodeAsync(
        string clientId, string code, string verifier, Uri callback, CancellationToken cancellationToken)
    {
        var (status, body) = await PostFormAsync(TokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = callback.AbsoluteUri,
            ["resource"] = ApiBaseUrl,
        }, cancellationToken, signIn: true).ConfigureAwait(false);
        if (status is not HttpStatusCode.OK)
            throw new ProviderOAuthException(ReadErrorCode(body) == "invalid_grant"
                ? Loc.Get("ChatGpt_CodeExpired")
                : string.Format(CultureInfo.CurrentCulture, Loc.Get("ChatGpt_SignInFailed"), (int)status));
        try
        {
            return ParseTokenResponse(body, DateTimeOffset.UtcNow, previousRefreshToken: null);
        }
        catch (FormatException)
        {
            throw new ProviderOAuthException(Loc.Get("ChatGpt_SignInInvalid"));
        }
    }

    /// <summary>
    /// Renews the access token. The response carries a replacement refresh token that must be
    /// stored before the old one is forgotten: refresh tokens rotate on every use.
    /// </summary>
    public async Task<ChatGptTokens> RefreshAsync(string clientId, string refreshToken, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        HttpStatusCode status;
        byte[] body;
        try
        {
            (status, body) = await PostFormAsync(TokenEndpoint, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["refresh_token"] = refreshToken,
                ["resource"] = ApiBaseUrl,
            }, cancellationToken, signIn: false).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new ChatGptRefreshException(Loc.Get("ChatGpt_Unreachable"), requiresSignIn: false);
        }

        if (status is HttpStatusCode.OK)
        {
            try
            {
                return ParseTokenResponse(body, DateTimeOffset.UtcNow, refreshToken).Tokens;
            }
            catch (FormatException)
            {
                throw new ChatGptRefreshException(Loc.Get("ChatGpt_Unreachable"), requiresSignIn: false);
            }
        }

        var code = ReadErrorCode(body);
        var terminal = status is HttpStatusCode.Unauthorized || (code is not null && TerminalRefreshErrors.Contains(code));
        throw new ChatGptRefreshException(
            Loc.Get(terminal ? "ChatGpt_SignInExpired" : "ChatGpt_Unreachable"), requiresSignIn: terminal);
    }

    /// <summary>
    /// Ends the renewable session. Returns false when revocation could not be confirmed; the user can
    /// still disconnect Lumi from ChatGPT's settings.
    /// </summary>
    public async Task<bool> RevokeAsync(string clientId, string refreshToken, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), cancellationToken).ConfigureAwait(false);
            try
            {
                var (status, _) = await PostFormAsync(RevocationEndpoint, new Dictionary<string, string>
                {
                    ["token"] = refreshToken,
                    ["token_type_hint"] = "refresh_token",
                    ["client_id"] = clientId,
                }, cancellationToken, signIn: false).ConfigureAwait(false);
                if ((int)status is >= 200 and < 300)
                    return true;
                if ((int)status < 500)
                    return false;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
            }
        }
        return false;
    }

    /// <summary>Lists the models the signed-in account's plan can use, in ChatGPT's own order.</summary>
    public async Task<IReadOnlyList<ProviderModel>> GetModelsAsync(string accessToken, CancellationToken cancellationToken)
        => (await GetCatalogAsync(accessToken, cancellationToken).ConfigureAwait(false)).Models;

    /// <summary>
    /// Reads the plan's model catalog: the models to offer, and which of them take tools in the
    /// "responses lite" form (an <c>additional_tools</c> input item), as Codex sends them.
    /// </summary>
    public async Task<ChatGptCatalog> GetCatalogAsync(string accessToken, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBaseUrl + "/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadBoundedAsync(response, 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ProviderConnectionException(
                ChatGptPlanErrors.Describe(response.StatusCode, body) ?? ProviderConnectionService.ErrorForStatus(response.StatusCode),
                response.StatusCode);
        try
        {
            using var json = JsonDocument.Parse(body);
            return new ChatGptCatalog(ParseModels(json.RootElement), ParseResponsesLiteModels(json.RootElement));
        }
        catch (JsonException)
        {
            throw new ProviderConnectionException(Loc.Get("EasyConnect_InvalidCatalog"));
        }
    }

    internal static IReadOnlySet<string> ParseResponsesLiteModels(JsonElement root)
    {
        var lite = new HashSet<string>(StringComparer.Ordinal);
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("models", out var models)
            && models.ValueKind == JsonValueKind.Array)
            foreach (var item in models.EnumerateArray())
                if (GetString(item, "slug") is { Length: > 0 } slug && item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("use_responses_lite", out var flag) && flag.ValueKind == JsonValueKind.True)
                    lite.Add(slug);
        return lite;
    }

    /// <summary>
    /// Reads ChatGPT's model catalog (<c>{"models":[{"slug","display_name","visibility",…}]}</c>),
    /// keeping the models it lists for display. A standard <c>data</c> list is accepted as well.
    /// </summary>
    internal static IReadOnlyList<ProviderModel> ParseModels(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array && !root.TryGetProperty("models", out _))
            return ProviderConnectionService.ParseModels(root, "/v1/responses");
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array)
            throw new ProviderConnectionException(Loc.Get("EasyConnect_InvalidCatalog"));

        var result = new List<ProviderModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in models.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || GetString(item, "slug") is not { Length: > 0 } slug
                || !seen.Add(slug))
                continue;
            if (GetString(item, "visibility") is { } visibility && visibility != "list")
                continue;
            int? context = null;
            foreach (var field in new[] { "context_window", "max_context_window" })
                if (context is null && item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number
                    && value.TryGetInt32(out var tokens) && tokens > 0)
                    context = tokens;

            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (item.TryGetProperty("supported_reasoning_levels", out var levels) && levels.ValueKind == JsonValueKind.Array)
                foreach (var level in levels.EnumerateArray())
                    if ((level.ValueKind == JsonValueKind.String ? level.GetString() : GetString(level, "effort")) is { } effort)
                        reported.Add(effort);
            var efforts = EffortOrder.Where(reported.Contains).ToArray();
            var defaultEffort = GetString(item, "default_reasoning_level");
            result.Add(new ProviderModel(slug, GetString(item, "display_name") is { Length: > 0 } name ? name : slug,
                context, SupportsTools: true, IsFree: false,
                SupportedReasoningEfforts: efforts.Length > 0 ? efforts : null,
                DefaultReasoningEffort: efforts.FirstOrDefault(effort =>
                    string.Equals(effort, defaultEffort, StringComparison.OrdinalIgnoreCase))));
        }
        return result;
    }

    internal static (ChatGptTokens Tokens, string? IdToken) ParseTokenResponse(
        ReadOnlyMemory<byte> body, DateTimeOffset receivedAt, string? previousRefreshToken)
    {
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException();
            var access = GetString(root, "access_token");
            var refresh = GetString(root, "refresh_token") ?? previousRefreshToken;
            if (!IsTokenValue(access) || !IsTokenValue(refresh))
                throw new FormatException();
            if (GetString(root, "token_type") is { } type && !type.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
                throw new FormatException();
            var lifetime = root.TryGetProperty("expires_in", out var expires) && expires.ValueKind == JsonValueKind.Number
                && expires.TryGetInt64(out var seconds) && seconds is > 0 and <= 7 * 86_400 ? seconds : 3600;
            return (new ChatGptTokens
            {
                AccessToken = access!,
                RefreshToken = refresh!,
                ExpiresAt = receivedAt.AddSeconds(lifetime),
                Scopes = SplitScopes(GetString(root, "scope")),
            }, GetString(root, "id_token"));
        }
        catch (JsonException)
        {
            throw new FormatException();
        }
    }

    private static bool IsTokenValue(string? value)
        => !string.IsNullOrEmpty(value) && value.Length <= 16_384 && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    private static IReadOnlyList<string> SplitScopes(string? scope)
        => (scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Validates an ID token as the open-source contract requires: an RS256 signature from OpenAI's
    /// published keys, then its issuer, audience (the issued client), expiry and this attempt's nonce.
    /// </summary>
    internal async Task<ChatGptAccount> ValidateIdTokenAsync(
        string idToken, string clientId, string nonce, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length != 3)
            throw InvalidIdToken();
        JsonElement header, payload;
        byte[] signature;
        try
        {
            using (var headerJson = JsonDocument.Parse(Base64UrlDecode(parts[0])))
                header = headerJson.RootElement.Clone();
            using (var payloadJson = JsonDocument.Parse(Base64UrlDecode(parts[1])))
                payload = payloadJson.RootElement.Clone();
            signature = Base64UrlDecode(parts[2]);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw InvalidIdToken();
        }
        if (GetString(header, "alg") != "RS256" || GetString(header, "kid") is not { Length: > 0 } kid)
            throw InvalidIdToken();
        var key = await GetSigningKeyAsync(kid, cancellationToken).ConfigureAwait(false);
        using var rsa = RSA.Create();
        rsa.ImportParameters(key);
        if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw InvalidIdToken();
        return ValidateIdTokenClaims(payload, clientId, nonce, now);
    }

    internal static ChatGptAccount ValidateIdTokenClaims(JsonElement payload, string clientId, string nonce, DateTimeOffset now)
    {
        if (payload.ValueKind != JsonValueKind.Object || GetString(payload, "iss") != Issuer)
            throw InvalidIdToken();
        var audienceMatches = payload.TryGetProperty("aud", out var audience) && audience.ValueKind switch
        {
            JsonValueKind.String => audience.GetString() == clientId,
            JsonValueKind.Array => audience.EnumerateArray().Any(item =>
                item.ValueKind == JsonValueKind.String && item.GetString() == clientId),
            _ => false
        };
        if (!audienceMatches)
            throw InvalidIdToken();
        if (!payload.TryGetProperty("exp", out var expiry) || expiry.ValueKind != JsonValueKind.Number
            || !expiry.TryGetInt64(out var expires) || DateTimeOffset.FromUnixTimeSeconds(expires) < now.AddMinutes(-5))
            throw InvalidIdToken();
        if (GetString(payload, "nonce") is not { } returnedNonce
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(returnedNonce), Encoding.UTF8.GetBytes(nonce)))
            throw InvalidIdToken();
        if (GetString(payload, "sub") is not { Length: > 0 } subject)
            throw InvalidIdToken();
        return new ChatGptAccount(clientId, subject, GetString(payload, "email"));
    }

    private async Task<RSAParameters> GetSigningKeyAsync(string kid, CancellationToken cancellationToken)
    {
        await _jwksGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Keys rotate: a kid the cached set lacks triggers one fresh download.
            if (_signingKeys is null || !_signingKeys.ContainsKey(kid))
                _signingKeys = await DownloadSigningKeysAsync(cancellationToken).ConfigureAwait(false);
            return _signingKeys.TryGetValue(kid, out var key) ? key : throw InvalidIdToken();
        }
        finally
        {
            _jwksGate.Release();
        }
    }

    private async Task<Dictionary<string, RSAParameters>> DownloadSigningKeysAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, JwksEndpoint);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadBoundedAsync(response, MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ProviderOAuthException(Loc.Get("ChatGpt_SignInInvalid"));
        return ParseSigningKeys(body);
    }

    internal static Dictionary<string, RSAParameters> ParseSigningKeys(ReadOnlyMemory<byte> body)
    {
        var keys = new Dictionary<string, RSAParameters>(StringComparer.Ordinal);
        try
        {
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("keys", out var list) || list.ValueKind != JsonValueKind.Array)
                return keys;
            foreach (var key in list.EnumerateArray())
            {
                if (GetString(key, "kty") != "RSA" || GetString(key, "kid") is not { Length: > 0 } kid
                    || GetString(key, "use") is { } use && use != "sig"
                    || GetString(key, "alg") is { } alg && alg != "RS256"
                    || GetString(key, "n") is not { } modulus || GetString(key, "e") is not { } exponent)
                    continue;
                keys[kid] = new RSAParameters { Modulus = Base64UrlDecode(modulus), Exponent = Base64UrlDecode(exponent) };
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
        }
        return keys;
    }

    private static ProviderOAuthException InvalidIdToken() => new(Loc.Get("ChatGpt_SignInInvalid"));

    internal static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += (base64.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException() };
        return Convert.FromBase64String(base64);
    }

    private async Task<(HttpStatusCode Status, byte[] Body)> PostFormAsync(
        string endpoint, Dictionary<string, string> fields, CancellationToken cancellationToken, bool signIn)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return (response.StatusCode, await ReadBoundedAsync(response, MaximumResponseBytes, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (Exception ex) when (signIn && (ex is HttpRequestException or IOException
                                              || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)))
        {
            throw new ProviderOAuthException(Loc.Get("ChatGpt_Unreachable"));
        }
    }

    internal static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > limit)
            throw new IOException("The response was too large.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > limit)
                throw new IOException("The response was too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>Reads an OAuth (<c>{"error":"code"}</c>) or API (<c>{"error":{"code"}}</c>) error code.</summary>
    internal static string? ReadErrorCode(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString();
                if (error.ValueKind == JsonValueKind.Object)
                    return GetString(error, "code") ?? GetString(error, "type");
            }
            return GetString(root, "code");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
           && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _jwksGate.Dispose();
        if (_ownsHttp)
            _http.Dispose();
    }
}
