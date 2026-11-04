using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Lumi.Models;
using Lumi.Remote.Protocol;

namespace Lumi.Services.Remote;

internal enum WebPushOutcome
{
    /// <summary>The push service took the message.</summary>
    Delivered,

    /// <summary>The browser's subscription no longer exists, so it is forgotten.</summary>
    Expired,

    /// <summary>The push service could not be reached or refused this message.</summary>
    Failed
}

internal readonly record struct WebPushResult(WebPushOutcome Outcome, string? Error = null);

/// <summary>
/// Standard Web Push (RFC 8030) from this PC straight to a browser's push service. Each message is
/// encrypted for the one browser that subscribed (RFC 8291) and signed with this profile's key (VAPID,
/// RFC 8292), so the push services browsers already use — Google's for Chrome and Edge, Apple's and
/// Mozilla's — carry it without Lumi running a server or holding an account.
/// </summary>
internal static class WebPush
{
    private const int KeyLength = 65;
    private const int AuthLength = 16;
    private const int SaltLength = 16;
    private const int TagLength = 16;
    private const int RecordSize = 4096;
    private const int HeaderLength = SaltLength + 4 + 1 + KeyLength;

    /// <summary>The most one message carries: a single record less its header, delimiter and tag.</summary>
    public const int MaxPayloadBytes = RecordSize - HeaderLength - 1 - TagLength;

    /// <summary>The contact push services ask a sender for. Apple's requires one.</summary>
    internal const string Subject = "https://github.com/adirh3/Lumi";

    /// <summary>
    /// The push services browsers use. A paired phone names the address to push to, so the PC only ever
    /// contacts these.
    /// </summary>
    private static readonly string[] PushServiceDomains =
    [
        "fcm.googleapis.com",        // Chrome, Edge, Samsung Internet and other Chromium browsers
        "push.apple.com",            // Safari on iPhone, iPad and Mac
        "push.services.mozilla.com", // Firefox
        "notify.windows.com"         // Edge on Windows
    ];

    private static KeyCache? _publicKey;

    // ── Keys ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A new signing key for this profile, as it is saved: P-256 PKCS#8 in base64.</summary>
    public static string CreateKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(key.ExportPkcs8PrivateKey());
    }

    /// <summary>
    /// The public half of a saved key, as browsers subscribe with it (applicationServerKey, base64url).
    /// Null when there is no usable key.
    /// </summary>
    public static string? PublicKeyOf(string? signingKey)
    {
        if (string.IsNullOrEmpty(signingKey))
            return null;

        if (Volatile.Read(ref _publicKey) is { } cached && cached.SigningKey == signingKey)
            return cached.PublicKey;

        try
        {
            using var key = ImportKey(signingKey);
            var publicKey = Base64Url.EncodeToString(PublicKeyBytes(key.ExportParameters(false)));
            Volatile.Write(ref _publicKey, new KeyCache(signingKey, publicKey));
            return publicKey;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            Trace.TraceWarning($"[Remote] The saved Web Push key is unusable: {ex.Message}");
            return null;
        }
    }

    internal static ECDsa ImportKey(string signingKey)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(signingKey), out _);
            if (key.KeySize != 256)
                throw new CryptographicException("The Web Push key is not a P-256 key.");
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static byte[] PublicKeyBytes(ECParameters parameters)
    {
        var bytes = new byte[KeyLength];
        bytes[0] = 4;
        parameters.Q.X!.CopyTo(bytes, 1);
        parameters.Q.Y!.CopyTo(bytes, 33);
        return bytes;
    }

    // ── Subscriptions ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the subscription a browser registers: an https address at a known push service, the
    /// browser's P-256 key and its 16-byte secret. Null when any part is unusable.
    /// </summary>
    public static RemoteWebPushSubscription? ReadSubscription(string? endpoint, string? p256dh, string? auth)
    {
        if (endpoint is not { Length: > 0 and <= 2048 }
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || !IsPushService(uri)
            || Decode(p256dh, KeyLength) is not { } browserKey
            || Decode(auth, AuthLength) is null)
        {
            return null;
        }

        try
        {
            using var key = BrowserKey(browserKey);
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            // Not a point on the curve. Windows reports that as an unsupported curve.
            return null;
        }

        return new RemoteWebPushSubscription { Endpoint = endpoint, P256dh = p256dh!, Auth = auth! };
    }

    /// <summary>Whether <paramref name="endpoint"/> is at a push service Lumi sends to.</summary>
    internal static bool IsPushService(Uri endpoint) =>
        endpoint.Scheme == Uri.UriSchemeHttps
        && endpoint.IsDefaultPort
        && endpoint.UserInfo.Length == 0
        && PushServiceDomains.Any(domain =>
            endpoint.IdnHost.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || endpoint.IdnHost.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    private static byte[]? Decode(string? value, int length)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128)
            return null;

        try
        {
            var bytes = Base64Url.DecodeFromChars(value.TrimEnd('='));
            return bytes.Length == length ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static ECDiffieHellman BrowserKey(byte[] key)
    {
        if (key[0] != 4)
            throw new CryptographicException("The browser key is not an uncompressed P-256 point.");

        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = key[1..33], Y = key[33..] }
        });
    }

    // ── Messages ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What a browser's service worker receives: the notification as compact JSON, which the service
    /// worker words in the phone's own language. The text is cut so it fits in one message.
    /// </summary>
    public static byte[] Payload(RemoteNotification notification, string hostName)
    {
        var text = notification.Text;
        var payload = WritePayload(notification, hostName, text);
        if (payload.Length <= MaxPayloadBytes)
            return payload;

        // The text takes the room the rest leaves. A character can be one byte or several, so the cut
        // keeps the share of characters that fits, less the closing ellipsis, until the message does.
        var rest = WritePayload(notification, hostName, "").Length;
        var room = MaxPayloadBytes - rest;
        while (payload.Length > MaxPayloadBytes && text.Length > 0)
        {
            var keep = (int)((long)text.Length * Math.Max(0, room - 3) / (payload.Length - rest));
            text = PhoneNotificationText.Truncate(text, Math.Min(keep, text.Length - 1));
            payload = WritePayload(notification, hostName, text);
        }

        return payload;
    }

    private static byte[] WritePayload(RemoteNotification notification, string hostName, string text)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("id", notification.Id);
            writer.WriteString("kind", notification.Kind);
            if (notification.ChatId != Guid.Empty)
                writer.WriteString("chatId", notification.ChatId.ToString("D"));
            writer.WriteString("title", notification.ChatTitle);
            if (!string.IsNullOrEmpty(notification.AgentName))
                writer.WriteString("agent", notification.AgentName);
            writer.WriteString("text", text);
            if (notification.Suggestions.Count > 0)
            {
                writer.WriteStartArray("suggestions");
                foreach (var suggestion in notification.Suggestions)
                    writer.WriteStringValue(suggestion);
                writer.WriteEndArray();
            }

            writer.WriteString("host", hostName);
            writer.WriteNumber("at", notification.CreatedAt.ToUnixTimeMilliseconds());
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Encrypts a message for one browser (the aes128gcm content coding of RFC 8291).</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> payload, RemoteWebPushSubscription subscription)
    {
        var browserKey = Decode(subscription.P256dh, KeyLength)
                         ?? throw new CryptographicException("The browser key is not valid.");
        var authSecret = Decode(subscription.Auth, AuthLength)
                         ?? throw new CryptographicException("The browser secret is not valid.");
        using var sender = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Encrypt(payload, browserKey, authSecret, sender, RandomNumberGenerator.GetBytes(SaltLength));
    }

    /// <summary>
    /// Encrypts with the given sender key and salt. Every real message takes fresh ones; tests pass the
    /// RFC's own to reproduce its example.
    /// </summary>
    internal static byte[] Encrypt(
        ReadOnlySpan<byte> payload,
        byte[] browserKey,
        byte[] authSecret,
        ECDiffieHellman sender,
        byte[] salt)
    {
        if (payload.Length > MaxPayloadBytes)
            throw new ArgumentException($"A push message holds at most {MaxPayloadBytes} bytes.", nameof(payload));

        var senderKey = PublicKeyBytes(sender.ExportParameters(false));
        byte[] sharedSecret;
        using (var browser = BrowserKey(browserKey))
            sharedSecret = sender.DeriveRawSecretAgreement(browser.PublicKey);

        var keyInfo = new byte[14 + 2 * KeyLength];
        "WebPush: info\0"u8.CopyTo(keyInfo);
        browserKey.CopyTo(keyInfo, 14);
        senderKey.CopyTo(keyInfo, 14 + KeyLength);
        var inputKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, authSecret, keyInfo);
        var contentKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256, inputKey, 16, salt, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.DeriveKey(
            HashAlgorithmName.SHA256, inputKey, 12, salt, "Content-Encoding: nonce\0"u8.ToArray());

        var record = new byte[payload.Length + 1];
        payload.CopyTo(record);
        // Marks the last record; no padding follows.
        record[^1] = 2;

        var body = new byte[HeaderLength + record.Length + TagLength];
        salt.CopyTo(body, 0);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(SaltLength), RecordSize);
        body[SaltLength + 4] = KeyLength;
        senderKey.CopyTo(body, SaltLength + 5);
        using var aes = new AesGcm(contentKey, TagLength);
        aes.Encrypt(
            nonce,
            record,
            body.AsSpan(HeaderLength, record.Length),
            body.AsSpan(HeaderLength + record.Length, TagLength));
        return body;
    }

    /// <summary>
    /// The Authorization header that shows a message comes from this PC (VAPID, RFC 8292): a token for
    /// the push service, signed with the profile's key, and the key's public half.
    /// </summary>
    internal static string Authorization(Uri endpoint, ECDsa key, DateTimeOffset now)
    {
        var claims = new ArrayBufferWriter<byte>(160);
        using (var writer = new Utf8JsonWriter(claims))
        {
            writer.WriteStartObject();
            writer.WriteString("aud", endpoint.GetLeftPart(UriPartial.Authority));
            writer.WriteNumber("exp", now.AddHours(12).ToUnixTimeSeconds());
            writer.WriteString("sub", Subject);
            writer.WriteEndObject();
        }

        var token = Base64Url.EncodeToString("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"u8)
                    + "."
                    + Base64Url.EncodeToString(claims.WrittenSpan);
        var signature = key.SignData(
            Encoding.ASCII.GetBytes(token),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var publicKey = Base64Url.EncodeToString(PublicKeyBytes(key.ExportParameters(false)));
        return $"vapid t={token}.{Base64Url.EncodeToString(signature)}, k={publicKey}";
    }

    private sealed record KeyCache(string SigningKey, string PublicKey);
}

/// <summary>Sends push messages to browsers' push services.</summary>
internal sealed class WebPushSender : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    public WebPushSender(HttpMessageHandler? handler = null, TimeProvider? time = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            // A push service answers itself; a redirect would carry the message somewhere else.
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        })
        {
            Timeout = RequestTimeout
        };
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Every message is sent at high urgency: each one shows a notification the user is waiting for,
    /// and push services hold lower-urgency messages while a phone sleeps.
    /// </summary>
    /// <param name="timeToLive">How long the push service keeps the message for a phone that is offline.</param>
    public async Task<WebPushResult> SendAsync(
        RemoteWebPushSubscription subscription,
        string signingKey,
        byte[] payload,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(subscription.Endpoint, UriKind.Absolute, out var endpoint)
            || !WebPush.IsPushService(endpoint))
        {
            return new WebPushResult(WebPushOutcome.Expired, "Not a known push service.");
        }

        HttpRequestMessage request;
        try
        {
            using var key = WebPush.ImportKey(signingKey);
            request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(WebPush.Encrypt(payload, subscription))
            };
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                WebPush.Authorization(endpoint, key, _time.GetUtcNow()));
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException
                                       or FormatException or ArgumentException)
        {
            return new WebPushResult(WebPushOutcome.Failed, ex.Message);
        }

        using (request)
        {
            request.Headers.TryAddWithoutValidation(
                "TTL",
                ((long)timeToLive.TotalSeconds).ToString(CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation("Urgency", "high");
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentEncoding.Add("aes128gcm");
            try
            {
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return new WebPushResult(WebPushOutcome.Delivered);

                var status = $"HTTP {(int)response.StatusCode}";
                return response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone
                    ? new WebPushResult(WebPushOutcome.Expired, status)
                    : new WebPushResult(WebPushOutcome.Failed, status);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       && !cancellationToken.IsCancellationRequested)
            {
                return new WebPushResult(WebPushOutcome.Failed, ex.Message);
            }
        }
    }

    public void Dispose() => _http.Dispose();
}
