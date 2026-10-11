using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lumi.Services.Byok;

public enum ProviderOAuthKind { OpenRouter, NanoGpt, Poe }

public sealed record ProviderOAuthOptions(ProviderOAuthKind Kind, string? ClientId = null);

public sealed record ProviderOAuthCredential(string ApiKey, DateTimeOffset? ExpiresAt = null)
{
    public override string ToString() => "ProviderOAuthCredential (redacted)";
}

/// <summary>A provider sign-in failure whose message is safe to display without exposing credentials.</summary>
public sealed class ProviderOAuthException : Exception
{
    internal ProviderOAuthException(string message) : base(message) { }
}

/// <summary>Obtains app-specific API keys through the providers' official public-client PKCE flows.</summary>
/// <remarks>
/// An injected client is not owned and must have automatic redirects disabled.
/// Official contracts: openrouter.ai/docs/guides/overview/auth/oauth.md,
/// docs.nano-gpt.com/api-reference/miscellaneous/oauth-pkce.md, and
/// creator.poe.com/docs/external-applications/sign-in-with-poe.
/// </remarks>
public sealed class ProviderOAuthService : IDisposable
{
    internal const string CallbackPath = "/easy-connect/callback";
    internal const int MaximumResponseBytes = 64 * 1024;
    private const int MaximumHeaderBytes = 8 * 1024;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _authorizationTimeout;
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public ProviderOAuthService(HttpClient? httpClient = null)
        : this(httpClient, TimeSpan.FromMinutes(4)) { }

    internal ProviderOAuthService(HttpClient? httpClient, TimeSpan authorizationTimeout)
    {
        if (authorizationTimeout <= TimeSpan.Zero || authorizationTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(authorizationTimeout));

        _authorizationTimeout = authorizationTimeout;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        }) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<ProviderOAuthCredential> AuthenticateAsync(
        ProviderOAuthOptions options,
        Func<Uri, Task> openBrowser,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ValidateOptions(options);
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
            var (verifier, challenge, state) = CreatePkceParameters();
            var authorizationUri = CreateAuthorizationUri(options, callback, challenge, state);

            // Start receiving before invoking the callback: tests and browser launchers may
            // await the local HTTP response themselves.
            var codeTask = ReceiveCodeAsync(listener, callback, state, options.Kind, flow);
            var browserTask = OpenBrowserAsync(openBrowser, authorizationUri, flow);
            await Task.WhenAll(browserTask, codeTask).ConfigureAwait(false);
            return await ExchangeAsync(options, callback, await codeTask.ConfigureAwait(false), verifier, flow.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            throw new OperationCanceledException("Provider sign-in was canceled.");
        }
        catch (OperationCanceledException) when (flow.IsCancellationRequested)
        {
            throw new ProviderOAuthException("Provider sign-in timed out. Start again and approve the connection in the browser.");
        }
        catch (SocketException)
        {
            throw new ProviderOAuthException(
                "Lumi could not open the local sign-in callback. Check local networking or firewall settings and try again.");
        }
        finally
        {
            flow.Cancel();
            listener.Stop();
        }
    }

    private static async Task OpenBrowserAsync(
        Func<Uri, Task> openBrowser, Uri authorizationUri, CancellationTokenSource flow)
    {
        try
        {
            await openBrowser(authorizationUri).WaitAsync(flow.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (flow.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            flow.Cancel();
            throw new ProviderOAuthException("The provider sign-in page could not be opened. Start sign-in again.");
        }
    }

    internal static (string Verifier, string Challenge, string State) CreatePkceParameters()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (verifier, CreateCodeChallenge(verifier), Base64Url(RandomNumberGenerator.GetBytes(32)));
    }

    internal static string CreateCodeChallenge(string verifier)
        => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static Uri CreateAuthorizationUri(
        ProviderOAuthOptions options, Uri callback, string challenge, string state)
    {
        ValidateOptions(options);
        var values = new Dictionary<string, string>
        {
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        };
        string endpoint;
        switch (options.Kind)
        {
            case ProviderOAuthKind.OpenRouter:
                endpoint = "https://openrouter.ai/auth";
                values["callback_url"] = callback.AbsoluteUri;
                values["key_label"] = "Lumi";
                break;
            case ProviderOAuthKind.NanoGpt:
                // NanoGPT's documented shortcut creates the callback client for local apps.
                endpoint = "https://nano-gpt.com/auth";
                values["callback_url"] = callback.AbsoluteUri;
                values["scope"] = "api.use models.read";
                values["client_name"] = "Lumi";
                break;
            case ProviderOAuthKind.Poe:
                endpoint = "https://poe.com/oauth/authorize";
                values["client_id"] = options.ClientId!;
                values["redirect_uri"] = callback.AbsoluteUri;
                values["response_type"] = "code";
                values["scope"] = "apikey:create";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options));
        }

        return new Uri(endpoint + "?" + string.Join("&", values.Select(
            pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
    }

    private static void ValidateOptions(ProviderOAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Kind))
            throw new ArgumentOutOfRangeException(nameof(options), "This provider does not support Easy Connect.");
        if (options.Kind == ProviderOAuthKind.Poe &&
            (string.IsNullOrWhiteSpace(options.ClientId) || options.ClientId.Length > 1024 ||
             options.ClientId.Any(char.IsControl)))
            throw new ProviderOAuthException(
                "Poe sign-in requires a public Client ID registered at poe.com/api/clients. Configure it or enter an API key instead.");
    }

    private static async Task<string> ReceiveCodeAsync(
        TcpListener listener, Uri callback, string state, ProviderOAuthKind kind, CancellationTokenSource flow)
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync(flow.Token).ConfigureAwait(false);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(flow.Token);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var stream = client.GetStream();
            string code;
            try
            {
                var headers = await ReadHeadersAsync(stream, requestTimeout.Token).ConfigureAwait(false);
                code = ValidateCallback(headers, callback, state, kind);
            }
            catch (ProviderOAuthException)
            {
                await WriteCallbackResponseAsync(stream, false, requestTimeout.Token).ConfigureAwait(false);
                throw;
            }

            await WriteCallbackResponseAsync(stream, true, requestTimeout.Token).ConfigureAwait(false);
            return code;
        }
        catch (OperationCanceledException) when (!flow.IsCancellationRequested)
        {
            flow.Cancel();
            throw new ProviderOAuthException("The local sign-in callback took too long to respond. Start sign-in again.");
        }
        catch (IOException)
        {
            flow.Cancel();
            throw new ProviderOAuthException("The local sign-in callback was interrupted. Start sign-in again.");
        }
        catch (Exception)
        {
            flow.Cancel();
            throw;
        }
        finally
        {
            // A flow owns exactly one callback. Replays cannot reach another exchange.
            listener.Stop();
        }
    }

    private static async Task<string[]> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[MaximumHeaderBytes];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            length += read;
            var end = bytes.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end < 0)
                continue;
            for (var i = 0; i < end; i++)
                if (bytes[i] > 127)
                    throw InvalidCallback();
            return Encoding.ASCII.GetString(bytes, 0, end).Split("\r\n", StringSplitOptions.None);
        }

        throw new ProviderOAuthException("The local sign-in callback was incomplete or too large. Start sign-in again.");
    }

    private static string ValidateCallback(string[] headers, Uri callback, string expectedState, ProviderOAuthKind kind)
    {
        var request = headers[0].Split(' ');
        if (request.Length != 3 || request[0] != "GET" || request[2] is not ("HTTP/1.1" or "HTTP/1.0"))
            throw InvalidCallback();

        var hostCount = 0;
        foreach (var header in headers.Skip(1))
        {
            var colon = header.IndexOf(':');
            if (colon <= 0)
                throw InvalidCallback();
            var name = header[..colon];
            var value = header[(colon + 1)..].Trim();
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                hostCount++;
                if (value != callback.Authority)
                    throw InvalidCallback();
            }
            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && value != "0"))
                throw InvalidCallback();
        }
        if (hostCount != 1)
            throw InvalidCallback();

        var target = request[1];
        var queryStart = target.IndexOf('?');
        if (queryStart < 0 || target[..queryStart] != CallbackPath || target.Contains('#'))
            throw InvalidCallback();

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in target[(queryStart + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var name = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
            if (!values.TryAdd(name, value))
                throw new ProviderOAuthException("The provider returned repeated callback parameters. Start sign-in again.");
        }

        // OpenRouter explicitly does not return state on denial. Such a callback can
        // only fail the flow, never authorize a key exchange.
        if (kind == ProviderOAuthKind.OpenRouter && values.ContainsKey("error") && !values.ContainsKey("state"))
            throw AuthorizationDenied(kind);
        if (!values.TryGetValue("state", out var state) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
            throw new ProviderOAuthException("The provider sign-in state did not match. Close this page and start sign-in again.");
        if (values.ContainsKey("error"))
            throw AuthorizationDenied(kind);
        if (!values.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code) ||
            code.Length > 2048 || code.Any(char.IsControl))
            throw new ProviderOAuthException("The provider did not return an authorization code. Start sign-in again.");
        return code;
    }

    private static ProviderOAuthException InvalidCallback()
        => new("The provider returned an invalid local sign-in callback. Start sign-in again.");

    private static ProviderOAuthException AuthorizationDenied(ProviderOAuthKind kind)
        => new($"{ProviderName(kind)} authorization was not approved. Start sign-in again if you want to connect.");

    private static Task WriteCallbackResponseAsync(NetworkStream stream, bool accepted, CancellationToken cancellationToken)
    {
        var body = accepted
            ? "<!doctype html><title>Lumi Easy Connect</title><p>Authorization received. Return to Lumi to finish connecting.</p>"
            : "<!doctype html><title>Lumi Easy Connect</title><p>Authorization could not be completed. Return to Lumi and try again.</p>";
        var response = $"HTTP/1.1 {(accepted ? "200 OK" : "400 Bad Request")}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\nConnection: close\r\nCache-Control: no-store\r\n" +
            "Referrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'\r\n" +
            $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
        return stream.WriteAsync(Encoding.UTF8.GetBytes(response), cancellationToken).AsTask();
    }

    private async Task<ProviderOAuthCredential> ExchangeAsync(
        ProviderOAuthOptions options, Uri callback, string code, string verifier, CancellationToken cancellationToken)
    {
        var endpoint = options.Kind switch
        {
            ProviderOAuthKind.OpenRouter => "https://openrouter.ai/api/v1/auth/keys",
            ProviderOAuthKind.NanoGpt => "https://nano-gpt.com/api/v1/auth/keys",
            ProviderOAuthKind.Poe => "https://api.poe.com/token",
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (options.Kind == ProviderOAuthKind.Poe)
        {
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = options.ClientId!,
                ["code"] = code,
                ["redirect_uri"] = callback.AbsoluteUri,
                ["code_verifier"] = verifier,
            });
        }
        else
        {
            using var body = new MemoryStream();
            using (var writer = new Utf8JsonWriter(body))
            {
                writer.WriteStartObject();
                if (options.Kind == ProviderOAuthKind.NanoGpt)
                    writer.WriteString("grant_type", "authorization_code");
                writer.WriteString("code", code);
                writer.WriteString("code_verifier", verifier);
                if (options.Kind == ProviderOAuthKind.OpenRouter)
                    writer.WriteString("code_challenge_method", "S256");
                writer.WriteEndObject();
                writer.Flush();
            }
            request.Content = new ByteArrayContent(body.ToArray());
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw ExchangeFailure(options.Kind, response.StatusCode);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw InvalidResponse(options.Kind);
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int read;
            while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + read > MaximumResponseBytes)
                    throw InvalidResponse(options.Kind);
                buffer.Write(chunk, 0, read);
            }
            return ParseCredential(options.Kind, buffer.ToArray(), DateTimeOffset.UtcNow);
        }
        catch (HttpRequestException)
        {
            throw new ProviderOAuthException($"{ProviderName(options.Kind)} could not be reached. Check your connection and start sign-in again.");
        }
        catch (IOException)
        {
            throw new ProviderOAuthException($"{ProviderName(options.Kind)} interrupted the key exchange. Start sign-in again.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderOAuthException($"{ProviderName(options.Kind)} did not respond in time. Check your connection and start sign-in again.");
        }
    }

    internal static ProviderOAuthCredential ParseCredential(
        ProviderOAuthKind kind, ReadOnlyMemory<byte> response, DateTimeOffset receivedAt)
    {
        if (response.Length > MaximumResponseBytes)
            throw InvalidResponse(kind);
        try
        {
            using var document = JsonDocument.Parse(response, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            var keyField = kind == ProviderOAuthKind.Poe ? "api_key" : "key";
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(keyField, out var keyValue) || keyValue.ValueKind != JsonValueKind.String)
                throw InvalidResponse(kind);
            var key = keyValue.GetString();
            if (string.IsNullOrWhiteSpace(key) || key.Length > 8192 || key.Any(char.IsWhiteSpace) || key.Any(char.IsControl))
                throw InvalidResponse(kind);

            DateTimeOffset? expiresAt = null;
            if (kind == ProviderOAuthKind.Poe)
            {
                if (!root.TryGetProperty("api_key_expires_in", out var expiry))
                    throw InvalidResponse(kind);
                if (expiry.ValueKind != JsonValueKind.Null)
                {
                    if (expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetInt64(out var seconds) || seconds <= 0)
                        throw InvalidResponse(kind);
                    expiresAt = receivedAt.AddSeconds(seconds);
                }
            }
            return new ProviderOAuthCredential(key, expiresAt);
        }
        catch (JsonException) { throw InvalidResponse(kind); }
        catch (ArgumentOutOfRangeException) { throw InvalidResponse(kind); }
    }

    private static ProviderOAuthException InvalidResponse(ProviderOAuthKind kind)
        => new($"{ProviderName(kind)} returned an invalid key response. Start sign-in again or create an API key on the provider's site.");

    private static ProviderOAuthException ExchangeFailure(ProviderOAuthKind kind, HttpStatusCode status)
    {
        var action = (int)status switch
        {
            >= 300 and < 400 => "The key exchange was redirected and was not followed. Start sign-in again or update Lumi.",
            400 or 401 or 403 => "Authorization expired or could not be verified. Start sign-in again.",
            404 or 405 => "The sign-in endpoint is unavailable. Update Lumi or enter an API key instead.",
            429 => "Too many sign-in attempts. Wait a little before trying again.",
            >= 500 => "The provider is temporarily unavailable. Try again later.",
            _ => "The key exchange failed. Start sign-in again or enter an API key instead.",
        };
        return new ProviderOAuthException($"{ProviderName(kind)} sign-in failed (HTTP {(int)status}). {action}");
    }

    private static string ProviderName(ProviderOAuthKind kind) => kind switch
    {
        ProviderOAuthKind.OpenRouter => "OpenRouter",
        ProviderOAuthKind.NanoGpt => "NanoGPT",
        ProviderOAuthKind.Poe => "Poe",
        _ => "The provider",
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _lifetime.Cancel();
        _lifetime.Dispose();
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
