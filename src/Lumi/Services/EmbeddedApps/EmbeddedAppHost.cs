using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.Services.EmbeddedApps;

/// <summary>What the app viewer loads: an MCP App's interface and tool exchange, or a canvas's address.</summary>
internal sealed record EmbeddedAppLaunch(
    EmbeddedAppKind Kind,
    string Title,
    string? Url = null,
    string? Html = null,
    string? MetadataJson = null,
    string? InputJson = null,
    string? ResultJson = null);

/// <summary>
/// Serves one embedded app to the viewer from loopback. The host origin runs the standard MCP Apps
/// bridge (or frames a canvas); a second origin is the sandbox that loads an MCP App's HTML under its
/// declared content security policy. The bridge's requests come back here and go to the owning chat:
/// every path carries a random secret, and only the host page's own origin may call the bridge.
/// </summary>
internal sealed class EmbeddedAppHost : IAsyncDisposable
{
    private const int MaximumRequestBytes = 4 * 1024 * 1024;
    private const int MaximumFailureChars = 2000;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<int, Task> _requests = new();
    private readonly EmbeddedAppLaunch _launch;
    private readonly JsonObject _hostContext;
    private readonly Func<string, JsonElement, CancellationToken, Task<JsonNode?>> _dispatch;
    private readonly string _prefix = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "/";
    private readonly string _resourceCsp = "";
    private readonly string _frameOrigin;
    private readonly Task _listenTask;
    private int _requestId;
    private bool _disposed;

    internal EmbeddedAppHost(EmbeddedAppLaunch launch, JsonObject hostContext,
        Func<string, JsonElement, CancellationToken, Task<JsonNode?>> dispatch)
    {
        _launch = launch;
        _hostContext = hostContext;
        _dispatch = dispatch;
        if (launch.Kind == EmbeddedAppKind.Canvas)
            _frameOrigin = ValidateWebUrl(launch.Url).GetLeftPart(UriPartial.Authority);
        else
        {
            if (string.IsNullOrEmpty(launch.Html))
                throw new InvalidOperationException(Loc.Apps_InterfaceMissing);
            _resourceCsp = BuildResourceCsp(launch.MetadataJson);
        }

        for (var attempt = 0; ; attempt++)
        {
            HostOrigin = "http://127.0.0.1:" + FreePort();
            SandboxOrigin = "http://127.0.0.1:" + FreePort();
            if (HostOrigin == SandboxOrigin)
                continue;
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add(HostOrigin + "/");
            _listener.Prefixes.Add(SandboxOrigin + "/");
            try
            {
                _listener.Start();
                break;
            }
            catch (HttpListenerException) when (attempt < 4) { }
        }
        _frameOrigin ??= SandboxOrigin;
        _listenTask = ListenAsync();
    }

    internal string HostOrigin { get; } = "";
    internal string SandboxOrigin { get; } = "";
    internal string Url => HostOrigin + _prefix + "index";

    /// <summary>The app is initialized and showing (raised off the UI thread).</summary>
    internal event Action? Ready;

    /// <summary>The app could not be loaded; the message says why (raised off the UI thread).</summary>
    internal event Action<string>? Failed;

    /// <summary>Accepts only web addresses without embedded credentials (canvases and links an app opens).</summary>
    internal static Uri ValidateWebUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException(Loc.Apps_WebLinksOnly);
        return uri;
    }

    /// <summary>The resource's declared CSP (MCP Apps <c>_meta.ui.csp</c>), or the spec's restrictive default.</summary>
    internal static string BuildResourceCsp(string? metadataJson)
    {
        using var metadata = JsonDocument.Parse(metadataJson ?? "{}");
        var csp = metadata.RootElement.TryGetProperty("ui", out var ui) && ui.TryGetProperty("csp", out var declared)
            ? declared : default;

        string Domains(string name, string fallback)
        {
            if (csp.ValueKind == JsonValueKind.Undefined || !csp.TryGetProperty(name, out var domains))
                return fallback;
            if (domains.ValueKind != JsonValueKind.Array)
                throw new FormatException(Loc.Apps_CspArrayRequired);
            var sources = new List<string>();
            foreach (var domain in domains.EnumerateArray())
            {
                var source = domain.GetString() ?? "";
                if (source.Any(char.IsWhiteSpace) || source.IndexOfAny(['\'', '"', ';', '\\', '<', '>']) >= 0
                    || !Uri.TryCreate(source.Replace("*.", "wildcard.", StringComparison.Ordinal), UriKind.Absolute, out var uri)
                    || uri.Scheme is not ("http" or "https" or "ws" or "wss")
                    || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query)
                    || !string.IsNullOrEmpty(uri.Fragment))
                    throw new FormatException(Loc.Apps_CspDomainInvalid);
                sources.Add(source);
            }
            return sources.Count == 0 ? fallback : string.Join(' ', sources);
        }

        var resources = Domains("resourceDomains", "");
        return "default-src 'none'; script-src 'self' 'unsafe-inline' " + resources
            + "; style-src 'self' 'unsafe-inline' " + resources
            + "; img-src 'self' data: " + resources
            + "; font-src 'self' data: " + resources
            + "; media-src 'self' data: " + resources
            + "; connect-src " + Domains("connectDomains", "'none'")
            + "; frame-src " + Domains("frameDomains", "'none'")
            + "; base-uri " + Domains("baseUriDomains", "'self'")
            + "; object-src 'none';";
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_lifetime.Token).ConfigureAwait(false);
                var id = Interlocked.Increment(ref _requestId);
                var request = ServeAsync(context);
                _requests[id] = request;
                _ = request.ContinueWith(_ => _requests.TryRemove(id, out var removed),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception error) when (_lifetime.IsCancellationRequested
            && error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        var isBridgeRequest = false;
        try
        {
            var request = context.Request;
            var response = context.Response;
            var origin = request.Url?.GetLeftPart(UriPartial.Authority);
            if (origin is null || request.Headers["Host"] != new Uri(origin).Authority
                || !request.Url!.AbsolutePath.StartsWith(_prefix, StringComparison.Ordinal))
            {
                response.StatusCode = 404;
                return;
            }
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["Referrer-Policy"] = "strict-origin";
            response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), clipboard-write=()";
            var route = request.Url.AbsolutePath[_prefix.Length..];
            if (origin == HostOrigin)
            {
                response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'unsafe-inline'; "
                    + "connect-src 'self'; frame-src " + _frameOrigin + "; object-src 'none'; base-uri 'none'; frame-ancestors 'none';";
                if (request.HttpMethod == "GET")
                {
                    switch (route)
                    {
                        case "index":
                            await WriteAsync(response, Page("<iframe id=\"view\" title=\"" + WebUtility.HtmlEncode(_launch.Title) + "\"></iframe>",
                                "host.js"), "text/html").ConfigureAwait(false);
                            return;
                        case "host.js":
                            await WriteAssetAsync(response, "host.js").ConfigureAwait(false);
                            return;
                        case "config":
                            await WriteAsync(response, Configuration().ToJsonString(), "application/json").ConfigureAwait(false);
                            return;
                    }
                }
                if (route == "rpc" && request.HttpMethod == "POST"
                    && request.Headers["Origin"] == HostOrigin
                    && request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
                {
                    isBridgeRequest = true;
                    await DispatchAsync(request, response).ConfigureAwait(false);
                    return;
                }
            }
            else if (origin == SandboxOrigin && _launch.Kind == EmbeddedAppKind.McpApp && request.HttpMethod == "GET")
            {
                response.Headers["Content-Security-Policy"] = (route == "resource" ? _resourceCsp
                    : "default-src 'none'; script-src 'self'; style-src 'unsafe-inline'; frame-src 'self'; object-src 'none'; base-uri 'none';")
                    + " frame-ancestors " + HostOrigin + " " + SandboxOrigin + ";";
                switch (route)
                {
                    case "sandbox":
                        await WriteAsync(response, Page("", "sandbox.js"), "text/html").ConfigureAwait(false);
                        return;
                    case "sandbox.js":
                        await WriteAssetAsync(response, "sandbox.js").ConfigureAwait(false);
                        return;
                    case "resource":
                        await WriteAsync(response, _launch.Html!, "text/html").ConfigureAwait(false);
                        return;
                }
            }
            response.StatusCode = 404;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            // A failed bridge request is the app's to handle; anything else means the app cannot load.
            Trace.TraceWarning("[EmbeddedApp] {0}: {1}", _launch.Title, error);
            if (!isBridgeRequest)
                Failed?.Invoke(error.Message);
            try
            {
                context.Response.StatusCode = 400;
                await WriteAsync(context.Response, new JsonObject { ["error"] = error.Message }.ToJsonString(),
                    "application/json").ConfigureAwait(false);
            }
            catch (Exception writeError) when (writeError is IOException or HttpListenerException or ObjectDisposedException
                or InvalidOperationException)
            {
                Trace.TraceWarning("[EmbeddedApp] Could not deliver the error: {0}", writeError.Message);
            }
        }
        finally
        {
            context.Response.Close();
        }
    }

    private async Task DispatchAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.ContentLength64 > MaximumRequestBytes)
            throw new InvalidOperationException(Loc.Apps_RequestTooLarge);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await request.InputStream.ReadAsync(bytes, _lifetime.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaximumRequestBytes)
                throw new InvalidOperationException(Loc.Apps_RequestTooLarge);
            buffer.Write(bytes, 0, count);
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        var method = document.RootElement.GetProperty("method").GetString()
            ?? throw new FormatException(Loc.Apps_RequestMethodMissing);
        var parameters = document.RootElement.TryGetProperty("params", out var supplied) ? supplied : default;
        JsonNode? result = new JsonObject();
        switch (method)
        {
            case "ready":
                Ready?.Invoke();
                break;
            case "failed":
                var message = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("message", out var text)
                    ? text.GetString() : null;
                Failed?.Invoke(string.IsNullOrWhiteSpace(message) ? Loc.Apps_LoadStopped
                    : message.Length > MaximumFailureChars ? message[..MaximumFailureChars] : message);
                break;
            default:
                result = await _dispatch(method, parameters, _lifetime.Token).ConfigureAwait(false);
                break;
        }
        await WriteAsync(response, new JsonObject { ["result"] = result }.ToJsonString(), "application/json").ConfigureAwait(false);
    }

    private JsonObject Configuration() => new()
    {
        ["kind"] = _launch.Kind == EmbeddedAppKind.Canvas ? "canvas" : "mcp-app",
        ["title"] = _launch.Title,
        ["hostVersion"] = typeof(EmbeddedAppHost).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
        ["url"] = _launch.Url,
        ["sandboxUrl"] = SandboxOrigin + _prefix + "sandbox",
        ["html"] = _launch.Html,
        ["input"] = JsonNode.Parse(_launch.InputJson ?? "{}"),
        ["result"] = JsonNode.Parse(_launch.ResultJson ?? "{}"),
        ["metadata"] = JsonNode.Parse(_launch.MetadataJson ?? "{}"),
        ["hostContext"] = _hostContext.DeepClone(),
    };

    private string Page(string content, string script)
    {
        var labels = new JsonObject
        {
            ["startFailed"] = Loc.Apps_StartFailed,
            ["loadFailed"] = Loc.Apps_LoadHttpFailed,
            ["canvasMissing"] = Loc.Apps_CanvasInterfaceMissing,
            ["interfaceMissing"] = Loc.Apps_InterfaceMissing,
            ["sandboxFailed"] = Loc.Apps_SandboxStartFailed,
            ["requestFailed"] = Loc.Apps_BridgeRequestFailed,
            ["sandboxOrigin"] = Loc.Apps_SandboxOriginInvalid
        };
        return $"<!doctype html><html lang=\"{Loc.Culture.Name}\" dir=\"{(Loc.IsRightToLeft ? "rtl" : "ltr")}\"><head>"
        + $"<title>{WebUtility.HtmlEncode(_launch.Title)}</title><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">"
        + "<style>:root{color-scheme:light dark}html,body{height:100%;margin:0;background:transparent}"
        + $"iframe{{display:block;width:100%;height:100%;border:0}}</style></head><body data-lumi-labels=\"{WebUtility.HtmlEncode(labels.ToJsonString())}\">"
        + content + "<script type=\"module\" src=\"./" + script + "\"></script></body></html>";
    }

    private static async Task WriteAssetAsync(HttpListenerResponse response, string name)
    {
        await using var asset = typeof(EmbeddedAppHost).Assembly.GetManifestResourceStream("Lumi.Assets.EmbeddedApps." + name)
            ?? throw new InvalidOperationException(Loc.Apps_BridgeMissing);
        response.ContentType = "text/javascript; charset=utf-8";
        await asset.CopyToAsync(response.OutputStream).ConfigureAwait(false);
    }

    private static async Task WriteAsync(HttpListenerResponse response, string text, string contentType)
    {
        response.ContentType = contentType + "; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        Ready = null;
        Failed = null;
        _lifetime.Cancel();
        _listener.Close();
        await _listenTask.ConfigureAwait(false);
        await Task.WhenAll(_requests.Values).ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
