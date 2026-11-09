using Android.Views;
using Android.Webkit;
using Android.Widget;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Lumi.Mobile.Services;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.Android;

/// <summary>Renders an interactive HTML file in a WebView, isolated from Lumi's app state.</summary>
internal sealed class AndroidHtmlPreview(string path, string fileName) : NativeControlHost
{
    private CancellationTokenSource? _loadLifetime;
    private WebView? _webView;
    private bool _destroyed;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var context = (parent as AndroidViewControlHandle)?.View.Context
                      ?? throw new InvalidOperationException("The HTML preview needs an Android view parent.");
        _destroyed = false;
        var lifetime = _loadLifetime = new CancellationTokenSource();
        var container = new FrameLayout(context) { ContentDescription = fileName };
        container.AddView(StateText(context, Strings.Get("FilePreviewLoading")));
        _ = LoadAsync(container, context, lifetime.Token);
        return new AndroidViewControlHandle(container);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _destroyed = true;
        _loadLifetime?.Cancel();
        _loadLifetime?.Dispose();
        _loadLifetime = null;

        if (control is AndroidViewControlHandle { View: FrameLayout container })
            container.RemoveAllViews();

        if (_webView is { PeerReference.IsValid: true } webView)
        {
            webView.StopLoading();
            webView.WebChromeClient = null;
            webView.Destroy();
        }

        _webView = null;
        base.DestroyNativeControlCore(control);
    }

    private async Task LoadAsync(FrameLayout container, Android.Content.Context context, CancellationToken token)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_destroyed || token.IsCancellationRequested)
                    return;

                var webView = new WebView(context);
                Configure(webView);
                _webView = webView;
                container.RemoveAllViews();
                container.AddView(webView, new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    ViewGroup.LayoutParams.MatchParent));
                webView.LoadDataWithBaseURL(null, BuildDocument(bytes), "text/html", "UTF-8", null);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Could not load HTML preview '{fileName}': {ex}");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_destroyed || token.IsCancellationRequested)
                    return;

                container.RemoveAllViews();
                container.AddView(StateText(context, ex is IOException or UnauthorizedAccessException
                    ? Strings.Get("FilePreviewUnreadable")
                    : Strings.Get("FilePreviewFailed")));
            });
        }
    }

    private static void Configure(WebView webView)
    {
        var settings = webView.Settings!;
        settings.JavaScriptEnabled = true;
        settings.DomStorageEnabled = false;
        settings.DatabaseEnabled = false;
        settings.AllowFileAccess = false;
        settings.AllowContentAccess = false;
        settings.AllowFileAccessFromFileURLs = false;
        settings.AllowUniversalAccessFromFileURLs = false;
        settings.SupportMultipleWindows = false;
        settings.JavaScriptCanOpenWindowsAutomatically = false;
        settings.MixedContentMode = MixedContentHandling.NeverAllow;
        webView.SetBackgroundColor(Android.Graphics.Color.White);
        webView.WebViewClient = new BlockNavigationClient();
        webView.WebChromeClient = null;
    }

    private static TextView StateText(Android.Content.Context context, string text) => new(context)
    {
        Text = text,
        TextSize = 14,
        Gravity = GravityFlags.Center,
        LayoutParameters = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent)
    };

    private static string BuildDocument(byte[] bytes)
    {
        var encoded = Convert.ToBase64String(bytes);
        return $$"""
            <!doctype html>
            <html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"></head>
            <body style="margin:0;width:100vw;height:100vh;overflow:hidden">
            <iframe title="HTML preview" sandbox="allow-scripts allow-downloads" referrerpolicy="no-referrer"
                    style="display:block;width:100%;height:100%;border:0;background:white"></iframe>
            <script>
            const bytes = Uint8Array.from(atob("{{encoded}}"), c => c.charCodeAt(0));
            const html = new TextDecoder("utf-8").decode(bytes);
            const policy = `<meta http-equiv="Content-Security-Policy" content="default-src 'none'; base-uri 'none'; object-src 'none'; form-action 'none'; script-src 'unsafe-inline' https: blob:; style-src 'unsafe-inline' https:; img-src https: data: blob:; font-src https: data:; connect-src https:; media-src https: data: blob:">`;
            document.querySelector("iframe").srcdoc = "<!doctype html>" + policy + html;
            </script>
            </body></html>
            """;
    }

    private sealed class BlockNavigationClient : WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request) =>
            !IsLocalDocument(request?.Url?.ToString());

        public override bool ShouldOverrideUrlLoading(WebView? view, string? url) =>
            !IsLocalDocument(url);

        private static bool IsLocalDocument(string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, "about", StringComparison.OrdinalIgnoreCase);
    }
}
