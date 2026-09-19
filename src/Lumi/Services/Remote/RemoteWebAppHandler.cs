namespace Lumi.Services.Remote;

internal sealed class RemoteWebAppHandler(RemoteWebAssetProvider? assets)
{
    private const string AppPrefix = "/app/";
    private static readonly Dictionary<string, string> SecurityHeaders =
        new Dictionary<string, string>
        {
            ["Content-Security-Policy"] =
                "default-src 'self'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'; " +
                "form-action 'self'; connect-src 'self' blob:; img-src 'self' data: blob:; " +
                "font-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
                "script-src 'self' 'wasm-unsafe-eval'; worker-src 'self' blob:; manifest-src 'self'",
            ["X-Content-Type-Options"] = "nosniff",
            ["X-Frame-Options"] = "DENY",
            ["Referrer-Policy"] = "no-referrer",
            ["Cross-Origin-Resource-Policy"] = "same-origin",
            ["Permissions-Policy"] =
                "camera=(), microphone=(), geolocation=(), payment=(), usb=()"
        };

    public bool IsAvailable => assets is not null;

    public static bool IsWebPath(string path) =>
        path is "/" or "/app"
        || path.StartsWith(AppPrefix, StringComparison.Ordinal);

    // A sign-in return can navigate to an API URL, but a document cannot carry the PWA's device header.
    public static bool IsApiDocumentNavigation(RemoteHttpRequest request) =>
        request.Method == "GET"
        && request.Path.StartsWith("/lumi/", StringComparison.Ordinal)
        && request.Header("Sec-Fetch-Mode") == "navigate"
        && request.Header("Sec-Fetch-Dest") == "document";

    public async Task HandleAsync(RemoteHttpContext context, CancellationToken cancellationToken)
    {
        var method = context.Request.Method;
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            await context.WriteEmptyAsync(
                    405,
                    MergeHeaders(("Allow", "GET, HEAD"), ("Cache-Control", "no-store")),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var path = context.Request.Path;
        if (path == "/")
        {
            await context.WriteRedirectAsync("/app/", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (path == "/app")
        {
            await context.WriteRedirectAsync("/app/", cancellationToken, 308).ConfigureAwait(false);
            return;
        }
        if (assets is null)
        {
            await context.WriteTextAsync("Lumi Web App is not installed.", cancellationToken, 404)
                .ConfigureAwait(false);
            return;
        }

        var relative = context.Request.Path[AppPrefix.Length..];
        if (relative.Length == 0)
            relative = "index.html";

        var fileLike = Path.HasExtension(relative);
        if (!assets.TryGet(relative, context.Request, out var asset))
        {
            if (fileLike || !assets.TryGet("index.html", context.Request, out asset))
            {
                await context.WriteTextAsync("Not found.", cancellationToken, 404).ConfigureAwait(false);
                return;
            }
        }

        var headers = MergeHeaders(
            ("Cache-Control", asset.CacheControl),
            ("ETag", asset.ETag),
            ("Vary", "Accept-Encoding"));
        if (asset.ContentEncoding is { Length: > 0 } encoding)
            headers["Content-Encoding"] = encoding;

        if (string.Equals(context.Request.Header("If-None-Match"), asset.ETag, StringComparison.Ordinal))
        {
            await context.WriteEmptyAsync(304, headers, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var stream = File.OpenRead(asset.FullPath);
        await context.WriteStreamAsync(
                stream,
                asset.Length,
                asset.ContentType,
                headers,
                cancellationToken,
                headOnly: string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);
    }

    private static Dictionary<string, string> MergeHeaders(
        params (string Name, string Value)[] headers)
    {
        var result = new Dictionary<string, string>(SecurityHeaders, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
            result[name] = value;
        return result;
    }
}
