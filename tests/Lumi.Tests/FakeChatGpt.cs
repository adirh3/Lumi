using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Services.Byok;

namespace Lumi.Tests;

/// <summary>
/// In-process stand-in for OpenAI's Sign in with ChatGPT endpoints: token exchange and rotation,
/// revocation, signing keys (a real RSA key, so signatures are verified) and the plan's model list.
/// </summary>
internal sealed class FakeChatGpt : HttpMessageHandler
{
    public const string Kid = "lumi-test-key";
    public const string PlanScopes = "chatgpt.tokens.use.direct email offline_access openid profile resource.invoke";

    private readonly RSA _signer = RSA.Create(2048);
    private int _issued;

    public string ClientId { get; set; } = "oaiapp_test";
    public string Subject { get; set; } = "user-123";
    public string? Email { get; set; } = "ada@example.com";
    public string GrantedScopes { get; set; } = PlanScopes;
    /// <summary>The nonce the next ID token carries; tests copy it from the authorization URL.</summary>
    public string? Nonce { get; set; }
    public HttpStatusCode RevokeStatus { get; set; } = HttpStatusCode.OK;
    public string ModelsJson { get; set; } = """
        {"models":[
          {"slug":"gpt-6.1-sol","display_name":"GPT-6.1 Sol","visibility":"list","context_window":272000,
           "supported_reasoning_levels":[{"effort":"low"},{"effort":"medium"},{"effort":"high"},{"effort":"ultra"}],
           "default_reasoning_level":"medium"},
          {"slug":"codex-auto-review","display_name":"Review","visibility":"hide"},
          {"slug":"gpt-5.5","display_name":"GPT-5.5","visibility":"list","context_window":400000}
        ]}
        """;
    /// <summary>Answers refresh requests instead of issuing new tokens (e.g. a terminal error).</summary>
    public Func<HttpResponseMessage>? RefreshResponse { get; set; }

    public ConcurrentQueue<Recorded> Requests { get; } = new();

    public sealed record Recorded(string Url, string Method, Dictionary<string, string> Form, string? Authorization);

    public IEnumerable<Recorded> Grants(string grantType)
        => Requests.Where(request => request.Form.TryGetValue("grant_type", out var grant) && grant == grantType);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var form = request.Content?.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded"
            ? body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split('=', 2)).ToDictionary(
                pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
                pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "")
            : [];
        var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
        Requests.Enqueue(new Recorded(url, request.Method.Method, form, request.Headers.Authorization?.ToString()));

        switch (url)
        {
            case ChatGptAuthService.JwksEndpoint:
                return Json(Jwks());
            case ChatGptAuthService.TokenEndpoint when form.GetValueOrDefault("grant_type") == "authorization_code":
                return Json(TokenResponse(IdToken(form["client_id"], Nonce ?? "")));
            case ChatGptAuthService.TokenEndpoint when form.GetValueOrDefault("grant_type") == "refresh_token":
                return RefreshResponse?.Invoke() ?? Json(TokenResponse(idToken: null));
            case ChatGptAuthService.RevocationEndpoint:
                return new HttpResponseMessage(RevokeStatus);
            case ChatGptAuthService.ApiBaseUrl + "/models":
                return Json(ModelsJson);
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    public string TokenResponse(string? idToken)
    {
        var number = Interlocked.Increment(ref _issued);
        var fields = new Dictionary<string, object?>
        {
            ["access_token"] = $"access-{number}",
            ["refresh_token"] = $"refresh-{number}",
            ["token_type"] = "Bearer",
            ["expires_in"] = 3600,
            ["scope"] = GrantedScopes,
        };
        if (idToken is not null)
            fields["id_token"] = idToken;
        return JsonSerializer.Serialize(fields);
    }

    public string IdToken(string audience, string nonce, string? issuer = null, DateTimeOffset? expires = null,
        string? subject = null)
    {
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["alg"] = "RS256", ["kid"] = Kid, ["typ"] = "JWT"
        }));
        var claims = new Dictionary<string, object?>
        {
            ["iss"] = issuer ?? ChatGptAuthService.Issuer,
            ["aud"] = audience,
            ["sub"] = subject ?? Subject,
            ["nonce"] = nonce,
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["exp"] = (expires ?? DateTimeOffset.UtcNow.AddHours(1)).ToUnixTimeSeconds(),
        };
        if (Email is not null)
            claims["email"] = Email;
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signature = _signer.SignData(Encoding.ASCII.GetBytes(header + "." + payload),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + payload + "." + Encode(signature);
    }

    private string Jwks()
    {
        var key = _signer.ExportParameters(false);
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["keys"] = new[]
            {
                new Dictionary<string, string>
                {
                    ["kty"] = "RSA", ["kid"] = Kid, ["use"] = "sig", ["alg"] = "RS256",
                    ["n"] = Encode(key.Modulus!), ["e"] = Encode(key.Exponent!)
                }
            }
        });
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Encode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _signer.Dispose();
        base.Dispose(disposing);
    }
}
