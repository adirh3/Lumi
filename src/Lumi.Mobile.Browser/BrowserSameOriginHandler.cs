using System.Net;
using Lumi.Mobile.Services;
using Lumi.Remote.Protocol;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.Browser;

internal sealed class BrowserSameOriginHandler(Uri origin, HttpMessageHandler innerHandler)
    : DelegatingHandler(innerHandler)
{
    private static readonly HttpRequestOptionsKey<IDictionary<string, object>> FetchOptions =
        new("WebAssemblyFetchOptions");

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { IsAbsoluteUri: true } destination
            || destination.UserInfo.Length != 0
            || !string.Equals(destination.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(destination.IdnHost, origin.IdnHost, StringComparison.OrdinalIgnoreCase)
            || destination.Port != origin.Port)
        {
            throw new HttpRequestException(Strings.Get("BrowserSameOriginOnly"));
        }

        request.Options.TryGetValue(FetchOptions, out var configuredOptions);
        var options = configuredOptions is null
            ? new Dictionary<string, object>(StringComparer.Ordinal)
            : new Dictionary<string, object>(configuredOptions, StringComparer.Ordinal);
        options["credentials"] = "include";
        options["cache"] = "no-store";
        options["redirect"] = "manual";
        request.Options.Set(FetchOptions, options);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var isLumiResponse = response.Headers.TryGetValues(RemoteProtocol.ServerResponseHeader, out var source)
            && source.Contains(RemoteProtocol.ServerResponseValue, StringComparer.Ordinal);
        if (origin.Scheme == Uri.UriSchemeHttps
            && origin.IdnHost.EndsWith(".devtunnels.ms", StringComparison.Ordinal)
            && ((int)response.StatusCode == 0
                || response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                    or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                && !isLumiResponse))
        {
            response.Dispose();
            throw new RemoteGatewaySignInException(
                Strings.Get("BrowserGatewaySignIn"));
        }
        return response;
    }
}
