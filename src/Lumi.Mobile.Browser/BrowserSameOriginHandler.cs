namespace Lumi.Mobile.Browser;

internal sealed class BrowserSameOriginHandler(Uri origin, HttpMessageHandler innerHandler)
    : DelegatingHandler(innerHandler)
{
    protected override Task<HttpResponseMessage> SendAsync(
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
        return base.SendAsync(request, cancellationToken);
    }
}
