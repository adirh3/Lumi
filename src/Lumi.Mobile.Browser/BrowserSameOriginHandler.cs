using System.Net;
using Lumi.Mobile.Services;
using Lumi.Remote.Protocol;

namespace Lumi.Mobile.Browser;

internal sealed class BrowserSameOriginHandler(Uri origin, HttpMessageHandler innerHandler)
    : DelegatingHandler(innerHandler)
{
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
            throw new HttpRequestException("Lumi Web can only connect to the PC that served this app.");
        }
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
                "Microsoft sign-in needs attention. Reopen this web app and sign in with the tunnel owner's account. " +
                "Your Lumi pairing is kept; copy any unsent draft before reloading.");
        }
        return response;
    }
}
