using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Lumi.Localization;
using Lumi.Models;
using Microsoft.Copilot;

namespace Lumi.Services.Byok;

internal sealed class FoundryEntraCredential(TokenCredential credential, FoundryIdentity identity)
{
    private static readonly TokenRequestContext Request = new(["https://ai.azure.com/.default"]);
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private AccessToken _token;

    public string RoutingIdentity => $"{identity.TenantId}/{identity.UserId}/{identity.ClientId}";

    public static bool IsFoundryHost(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.HostNameType == UriHostNameType.Dns
           && (uri.IdnHost.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
               || uri.IdnHost.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase));

    public static bool IsAllowedEndpoint(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsFoundryHost(uri)
           && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
           && uri.AbsolutePath.TrimEnd('/') == "/openai/v1";

    public static async Task<FoundryIdentity> ConnectAsync(
        string endpointId, string? tenantId, string? clientId, FoundryIdentity? previous, CancellationToken ct)
    {
        try
        {
            if (clientId is null)
            {
                var token = await CreateCli(previous?.TenantId).GetTokenAsync(Request, ct).ConfigureAwait(false);
                return Identify(token, previous);
            }

            var browser = CreateBrowser(endpointId, tenantId!, clientId);
            var account = await browser.AuthenticateAsync(Request, ct).ConfigureAwait(false);
            var access = await browser.GetTokenAsync(Request, ct).ConfigureAwait(false);
            using var record = new MemoryStream();
            await account.SerializeAsync(record, ct).ConfigureAwait(false);
            return Identify(access, previous) with
            {
                ClientId = clientId,
                BrowserAccount = System.Text.Encoding.UTF8.GetString(record.ToArray())
            };
        }
        catch (Exception ex) when (ex is AuthenticationFailedException or CredentialUnavailableException)
        {
            throw new ProviderConnectionException(Loc.Get(clientId is null
                ? "EasyConnect_FoundryCliFailed" : "EasyConnect_FoundryBrowserFailed"));
        }
    }

    public static FoundryEntraCredential For(ByokEndpoint endpoint)
    {
        var identity = endpoint.FoundryIdentity!;
        if (identity.ClientId is null)
            return new(CreateCli(identity.TenantId), identity);
        using var record = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(identity.BrowserAccount!));
        return new(CreateBrowser(endpoint.Id, identity.TenantId, identity.ClientId,
            AuthenticationRecord.Deserialize(record)), identity);
    }

    public Task<string> GetProviderTokenAsync(ProviderTokenArgs _) => GetTokenAsync();

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        await _refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(5))
            {
                var token = await credential.GetTokenAsync(Request, ct).ConfigureAwait(false);
                Identify(token, identity);
                _token = token;
            }
            return _token.Token;
        }
        catch (Exception ex) when (ex is AuthenticationFailedException or CredentialUnavailableException)
        {
            throw new ProviderConnectionException(Loc.Get(identity.ClientId is null
                ? "EasyConnect_FoundryCliFailed" : "EasyConnect_FoundryBrowserFailed"));
        }
        finally
        {
            _refresh.Release();
        }
    }

    private static AzureCliCredential CreateCli(string? tenantId)
        => new(new AzureCliCredentialOptions { TenantId = tenantId });

    private static InteractiveBrowserCredential CreateBrowser(
        string endpointId, string tenantId, string clientId, AuthenticationRecord? record = null)
        => new(new InteractiveBrowserCredentialOptions
        {
            TenantId = tenantId,
            ClientId = clientId,
            RedirectUri = new Uri("http://localhost"),
            AuthenticationRecord = record,
            DisableAutomaticAuthentication = true,
            TokenCachePersistenceOptions = new() { Name = "Lumi.Foundry." + endpointId }
        });

    // This is an account consistency check, not token validation: Foundry validates the token.
    // An app-only credential, or a different user after an Azure CLI login, must not send a chat.
    internal static FoundryIdentity Identify(AccessToken token, FoundryIdentity? previous)
    {
        try
        {
            var payload = token.Token.Split('.')[1].Replace('-', '+').Replace('_', '/');
            using var claims = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
            var root = claims.RootElement;
            if (root.TryGetProperty("scp", out var scopes) && !string.IsNullOrWhiteSpace(scopes.GetString())
                && Guid.TryParse(root.GetProperty("tid").GetString(), out var tenant)
                && Guid.TryParse(root.GetProperty("oid").GetString(), out var user)
                && (previous is null || (previous.TenantId == tenant.ToString() && previous.UserId == user.ToString())))
                return new(tenant.ToString(), user.ToString());
        }
        catch (Exception ex) when (ex is JsonException or FormatException or IndexOutOfRangeException
                                      or KeyNotFoundException or InvalidOperationException)
        {
            // Tokens without readable delegated-account claims cannot be bound to the chosen account.
        }
        throw new ProviderConnectionException(Loc.Get("EasyConnect_FoundryWrongAccount"));
    }
}
