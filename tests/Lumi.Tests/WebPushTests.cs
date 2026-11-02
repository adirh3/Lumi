using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.Services;
using Lumi.Services.Remote;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// A browser's side of Web Push, for tests: its key pair and secret, the subscription it hands the PC,
/// and the decryption it applies to what its push service delivers (RFC 8291).
/// </summary>
internal sealed class TestPushBrowser : IDisposable
{
    private readonly ECDiffieHellman _key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _auth = RandomNumberGenerator.GetBytes(16);

    public byte[] PublicKey
    {
        get
        {
            var parameters = _key.ExportParameters(false);
            return [4, .. parameters.Q.X!, .. parameters.Q.Y!];
        }
    }

    public string P256dh => Base64Url.EncodeToString(PublicKey);

    public string Auth => Base64Url.EncodeToString(_auth);

    public RemoteWebPushSubscription Subscription(string endpoint) =>
        new() { Endpoint = endpoint, P256dh = P256dh, Auth = Auth };

    public byte[] Decrypt(byte[] body) => Decrypt(body, _key, PublicKey, _auth);

    public static byte[] Decrypt(byte[] body, ECDiffieHellman browserKey, byte[] browserPublicKey, byte[] auth)
    {
        var salt = body[..16];
        var recordSize = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16));
        Assert.Equal(4096u, recordSize);
        var idLength = body[20];
        var senderKey = body[21..(21 + idLength)];

        using var sender = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = senderKey[1..33], Y = senderKey[33..] }
        });
        var secret = browserKey.DeriveRawSecretAgreement(sender.PublicKey);
        var keyInfo = Encoding.ASCII.GetBytes("WebPush: info\0").Concat(browserPublicKey).Concat(senderKey).ToArray();
        var inputKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, auth, keyInfo);
        var contentKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKey, 16, salt,
            Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKey, 12, salt,
            Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        var sealedRecord = body[(21 + idLength)..];
        var record = new byte[sealedRecord.Length - 16];
        using var aes = new AesGcm(contentKey, 16);
        aes.Decrypt(nonce, sealedRecord[..^16], sealedRecord[^16..], record);
        var end = record.Length - 1;
        while (record[end] == 0)
            end--;
        Assert.Equal(2, record[end]);
        return record[..end];
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>A push service for tests: records what the PC sends and answers with <see cref="Status"/>.</summary>
internal sealed class FakePushService : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<PushRequest> _requests = [];

    public HttpStatusCode Status { get; set; } = HttpStatusCode.Created;

    public IReadOnlyList<PushRequest> Requests
    {
        get { lock (_gate) return [.. _requests]; }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        lock (_gate)
        {
            _requests.Add(new PushRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase),
                request.Content?.Headers.ContentEncoding.ToList() ?? [],
                request.Content?.Headers.ContentType?.MediaType,
                body));
        }

        return new HttpResponseMessage(Status);
    }

    internal sealed record PushRequest(
        HttpMethod Method,
        Uri Uri,
        IReadOnlyDictionary<string, string> Headers,
        IReadOnlyList<string> ContentEncoding,
        string? ContentType,
        byte[] Body);
}

public sealed class WebPushTests
{
    private const string FcmEndpoint = "https://fcm.googleapis.com/fcm/send/dQw4w9WgXcQ:APA91bTestToken";

    [Fact]
    public void EncryptionReproducesTheExampleFromRfc8291()
    {
        // RFC 8291, section 5 and appendix A.
        var browserPublic = Base64Url.DecodeFromChars(
            "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4");
        var auth = Base64Url.DecodeFromChars("BTBZMqHH6r4Tts7J_aSIgg");
        var senderPublic = Base64Url.DecodeFromChars(
            "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8");
        using var sender = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Base64Url.DecodeFromChars("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw"),
            Q = new ECPoint { X = senderPublic[1..33], Y = senderPublic[33..] }
        });
        var salt = Base64Url.DecodeFromChars("DGv6ra1nlYgDCS1FRnbzlw");

        var body = WebPush.Encrypt(
            "When I grow up, I want to be a watermelon"u8, browserPublic, auth, sender, salt);

        Assert.Equal(
            "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN",
            Base64Url.EncodeToString(body));
    }

    [Fact]
    public void EachMessageOpensOnlyForTheBrowserItWasSealedFor()
    {
        using var browser = new TestPushBrowser();
        using var stranger = new TestPushBrowser();
        var payload = Encoding.UTF8.GetBytes("{\"text\":\"שלום — done ✅\"}");

        var first = WebPush.Encrypt(payload, browser.Subscription(FcmEndpoint));
        var second = WebPush.Encrypt(payload, browser.Subscription(FcmEndpoint));

        Assert.Equal(payload, browser.Decrypt(first));
        Assert.Equal(payload, browser.Decrypt(second));
        // A fresh salt and sender key every time: the same words never look the same twice.
        Assert.NotEqual(first[..16], second[..16]);
        Assert.NotEqual(first[21..86], second[21..86]);
        Assert.ThrowsAny<CryptographicException>(() => stranger.Decrypt(first));
    }

    [Fact]
    public void TheVapidHeaderIsATokenThePushServiceCanVerify()
    {
        var signingKey = WebPush.CreateKey();
        using var key = WebPush.ImportKey(signingKey);
        var now = new DateTimeOffset(2026, 11, 1, 20, 0, 0, TimeSpan.Zero);

        var header = WebPush.Authorization(new Uri(FcmEndpoint), key, now);

        Assert.StartsWith("vapid t=", header, StringComparison.Ordinal);
        var parts = header["vapid ".Length..].Split(", ");
        var token = parts.Single(part => part.StartsWith("t=", StringComparison.Ordinal))[2..];
        var publicKey = parts.Single(part => part.StartsWith("k=", StringComparison.Ordinal))[2..];
        Assert.Equal(WebPush.PublicKeyOf(signingKey), publicKey);

        var segments = token.Split('.');
        Assert.Equal(3, segments.Length);
        using var jwtHeader = JsonDocument.Parse(Base64Url.DecodeFromChars(segments[0]));
        Assert.Equal("ES256", jwtHeader.RootElement.GetProperty("alg").GetString());
        Assert.Equal("JWT", jwtHeader.RootElement.GetProperty("typ").GetString());
        using var claims = JsonDocument.Parse(Base64Url.DecodeFromChars(segments[1]));
        Assert.Equal("https://fcm.googleapis.com", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal(now.AddHours(12).ToUnixTimeSeconds(), claims.RootElement.GetProperty("exp").GetInt64());
        Assert.Equal(WebPush.Subject, claims.RootElement.GetProperty("sub").GetString());

        var publicBytes = Base64Url.DecodeFromChars(publicKey);
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = publicBytes[1..33], Y = publicBytes[33..] }
        });
        Assert.True(verifier.VerifyData(
            Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}"),
            Base64Url.DecodeFromChars(segments[2]),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void TheKeyBrowsersSubscribeWithIsTheProfilesPublicKey()
    {
        var signingKey = WebPush.CreateKey();

        var publicKey = WebPush.PublicKeyOf(signingKey);

        var bytes = Base64Url.DecodeFromChars(publicKey!);
        Assert.Equal(65, bytes.Length);
        Assert.Equal(4, bytes[0]);
        Assert.Equal(publicKey, WebPush.PublicKeyOf(signingKey));
        Assert.NotEqual(publicKey, WebPush.PublicKeyOf(WebPush.CreateKey()));
        Assert.Null(WebPush.PublicKeyOf(""));
        Assert.Null(WebPush.PublicKeyOf("not a key"));
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc:def")]
    [InlineData("https://web.push.apple.com/QGuQyavXutnMH")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/gAAAAA")]
    [InlineData("https://wns2-par02p.notify.windows.com/w/?token=BQYAAAB")]
    public void SubscriptionsAtTheBrowsersPushServicesAreAccepted(string endpoint)
    {
        using var browser = new TestPushBrowser();

        var subscription = WebPush.ReadSubscription(endpoint, browser.P256dh, browser.Auth);

        Assert.NotNull(subscription);
        Assert.Equal(endpoint, subscription.Endpoint);
    }

    [Theory]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc")]
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/abc")]
    [InlineData("https://user@fcm.googleapis.com/fcm/send/abc")]
    [InlineData("https://fcm.googleapis.com.example.com/fcm/send/abc")]
    [InlineData("https://googleapis.com/fcm/send/abc")]
    [InlineData("https://127.0.0.1/fcm/send/abc")]
    [InlineData("https://permanently-removed.invalid/fcm/send/abc")]
    [InlineData("ftp://fcm.googleapis.com/abc")]
    [InlineData("fcm.googleapis.com/fcm/send/abc")]
    [InlineData("")]
    public void ThePcNeverPushesAnywhereElse(string endpoint)
    {
        using var browser = new TestPushBrowser();

        Assert.Null(WebPush.ReadSubscription(endpoint, browser.P256dh, browser.Auth));
    }

    [Fact]
    public void ASubscriptionNeedsTheBrowsersRealKeys()
    {
        using var browser = new TestPushBrowser();
        var notOnTheCurve = Base64Url.EncodeToString([4, .. new byte[64]]);
        var compressed = Base64Url.EncodeToString(browser.PublicKey[..33]);
        var shortSecret = Base64Url.EncodeToString(new byte[8]);

        Assert.Null(WebPush.ReadSubscription(FcmEndpoint, notOnTheCurve, browser.Auth));
        Assert.Null(WebPush.ReadSubscription(FcmEndpoint, compressed, browser.Auth));
        Assert.Null(WebPush.ReadSubscription(FcmEndpoint, browser.P256dh, shortSecret));
        Assert.Null(WebPush.ReadSubscription(FcmEndpoint, browser.P256dh, null));
        Assert.Null(WebPush.ReadSubscription(FcmEndpoint, "not base64!", browser.Auth));
        // Browsers send base64url; padding is tolerated.
        Assert.NotNull(WebPush.ReadSubscription(FcmEndpoint, browser.P256dh + "=", browser.Auth + "=="));
    }

    [Fact]
    public void TheMessageCarriesTheNotificationForTheServiceWorker()
    {
        var chatId = Guid.NewGuid();
        var notification = new RemoteNotification
        {
            Id = $"reply:{chatId:N}",
            Kind = RemoteProtocol.NotificationKinds.Reply,
            ChatId = chatId,
            ChatTitle = "Trip plan",
            AgentName = "Travel Lumi",
            Text = "Three hotels fit — \"Dan\" is closest.\nשלום",
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_793_000_000_000)
        };

        using var json = JsonDocument.Parse(WebPush.Payload(notification, "LIGHTO-DESKTOP"));

        var root = json.RootElement;
        Assert.Equal(notification.Id, root.GetProperty("id").GetString());
        Assert.Equal("reply", root.GetProperty("kind").GetString());
        Assert.Equal(chatId.ToString("D"), root.GetProperty("chatId").GetString());
        Assert.Equal("Trip plan", root.GetProperty("title").GetString());
        Assert.Equal("Travel Lumi", root.GetProperty("agent").GetString());
        Assert.Equal(notification.Text, root.GetProperty("text").GetString());
        Assert.Equal("LIGHTO-DESKTOP", root.GetProperty("host").GetString());
        Assert.Equal(1_793_000_000_000, root.GetProperty("at").GetInt64());
    }

    [Fact]
    public void ALongReplyIsCutToFitOneMessage()
    {
        var notification = new RemoteNotification
        {
            Id = "reply:" + Guid.NewGuid().ToString("N"),
            Kind = RemoteProtocol.NotificationKinds.Reply,
            ChatId = Guid.NewGuid(),
            ChatTitle = new string('題', RemoteProtocol.MobileNotificationTitleLimit),
            AgentName = new string('エ', RemoteProtocol.MobileNotificationTitleLimit),
            Text = string.Concat(Enumerable.Repeat("完成しました😀", RemoteProtocol.MobileNotificationTextLimit / 8))
        };

        var payload = WebPush.Payload(notification, "PC");

        Assert.InRange(payload.Length, WebPush.MaxPayloadBytes - 128, WebPush.MaxPayloadBytes);
        using var json = JsonDocument.Parse(payload);
        var text = json.RootElement.GetProperty("text").GetString()!;
        Assert.EndsWith("…", text, StringComparison.Ordinal);
        Assert.StartsWith("完成しました", text, StringComparison.Ordinal);
        Assert.Equal(notification.ChatTitle, json.RootElement.GetProperty("title").GetString());
        using var browser = new TestPushBrowser();
        Assert.Equal(payload, browser.Decrypt(WebPush.Encrypt(payload, browser.Subscription(FcmEndpoint))));
    }

    [Fact]
    public async Task TheSenderPostsAnEncryptedSignedMessageToThePushService()
    {
        using var browser = new TestPushBrowser();
        var service = new FakePushService();
        using var sender = new WebPushSender(service);
        var signingKey = WebPush.CreateKey();
        var payload = Encoding.UTF8.GetBytes("{\"kind\":\"question\"}");

        var result = await sender.SendAsync(
            browser.Subscription(FcmEndpoint), signingKey, payload, TimeSpan.FromHours(24));

        Assert.Equal(WebPushOutcome.Delivered, result.Outcome);
        var request = Assert.Single(service.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri(FcmEndpoint), request.Uri);
        Assert.Equal("86400", request.Headers["TTL"]);
        Assert.Equal("high", request.Headers["Urgency"]);
        Assert.StartsWith("vapid t=", request.Headers["Authorization"], StringComparison.Ordinal);
        Assert.EndsWith("k=" + WebPush.PublicKeyOf(signingKey), request.Headers["Authorization"], StringComparison.Ordinal);
        Assert.Equal(["aes128gcm"], request.ContentEncoding);
        Assert.Equal("application/octet-stream", request.ContentType);
        Assert.Equal(payload, browser.Decrypt(request.Body));
    }

    [Theory]
    [InlineData(HttpStatusCode.Created, nameof(WebPushOutcome.Delivered))]
    [InlineData(HttpStatusCode.Gone, nameof(WebPushOutcome.Expired))]
    [InlineData(HttpStatusCode.NotFound, nameof(WebPushOutcome.Expired))]
    [InlineData(HttpStatusCode.Forbidden, nameof(WebPushOutcome.Failed))]
    [InlineData(HttpStatusCode.TooManyRequests, nameof(WebPushOutcome.Failed))]
    [InlineData(HttpStatusCode.InternalServerError, nameof(WebPushOutcome.Failed))]
    public async Task ThePushServicesAnswerSaysWhatBecameOfTheMessage(HttpStatusCode status, string expected)
    {
        var outcome = Enum.Parse<WebPushOutcome>(expected);
        using var browser = new TestPushBrowser();
        using var sender = new WebPushSender(new FakePushService { Status = status });

        var result = await sender.SendAsync(
            browser.Subscription(FcmEndpoint), WebPush.CreateKey(), [1, 2, 3], TimeSpan.FromMinutes(10));

        Assert.Equal(outcome, result.Outcome);
        if (outcome != WebPushOutcome.Delivered)
            Assert.Equal($"HTTP {(int)status}", result.Error);
    }

    [Fact]
    public async Task AnUnreachablePushServiceIsAFailureNotACrash()
    {
        using var browser = new TestPushBrowser();
        using var sender = new WebPushSender(new ThrowingHandler());

        var result = await sender.SendAsync(
            browser.Subscription(FcmEndpoint), WebPush.CreateKey(), [1], TimeSpan.FromMinutes(1));

        Assert.Equal(WebPushOutcome.Failed, result.Outcome);
        Assert.Contains("unreachable", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ABrowserThatPairedAgainTakesItsSubscriptionAlong()
    {
        var subscription = new RemoteWebPushSubscription { Endpoint = FcmEndpoint, P256dh = "key", Auth = "secret" };
        var store = new DataStore(new AppData
        {
            Settings = new UserSettings
            {
                RemotePairedDevices =
                [
                    new RemotePairedDevice { DeviceId = "old", WebPush = subscription },
                    new RemotePairedDevice { DeviceId = "new" }
                ]
            }
        });

        Assert.True(store.SetRemoteWebPush("new", subscription));
        Assert.False(store.SetRemoteWebPush("gone", subscription));

        var devices = store.SnapshotRemotePairedDevices();
        Assert.Null(devices.Single(device => device.DeviceId == "old").WebPush);
        Assert.Equal(subscription, devices.Single(device => device.DeviceId == "new").WebPush);
        Assert.True(store.RemoveRemoteWebPush(FcmEndpoint));
        Assert.False(store.RemoveRemoteWebPush(FcmEndpoint));
        Assert.All(store.SnapshotRemotePairedDevices(), device => Assert.Null(device.WebPush));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("The push service is unreachable.");
    }
}
