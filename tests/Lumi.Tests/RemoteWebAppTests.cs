using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lumi.Services.Remote;
using Xunit;

namespace Lumi.Tests;

public sealed class RemoteWebAppTests
{
    [Fact]
    public void AppShellRequestsTheInstallManifestWithAuthentication()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "BrowserAssets", "index.html"));
        var link = Assert.Single(Regex.Matches(html, @"<link\b[^>]*>", RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Select(match => match.Value),
            tag => Regex.IsMatch(tag, """\brel\s*=\s*["']manifest["']""", RegexOptions.IgnoreCase));

        Assert.Matches("""\bcrossorigin\s*=\s*["']use-credentials["']""", link);
    }

    [Theory]
    [InlineData("GET", "/lumi/snapshot", "navigate", "document", true)]
    [InlineData("GET", "/lumi/events", "navigate", "document", true)]
    [InlineData("GET", "/lumi/transcript", "navigate", "document", true)]
    [InlineData("POST", "/lumi/command", "navigate", "document", false)]
    [InlineData("GET", "/lumi/snapshot", "cors", "empty", false)]
    [InlineData("GET", "/lumi/snapshot", "same-origin", "empty", false)]
    [InlineData("GET", "/lumi/snapshot", "navigate", "iframe", false)]
    [InlineData("GET", "/lumi/snapshot", null, null, false)]
    [InlineData("GET", "/app/manifest.webmanifest", "navigate", "document", false)]
    [InlineData("GET", "/unrelated", "navigate", "document", false)]
    public void OnlyTopLevelApiGetNavigationsReturnToThePwa(
        string method, string path, string? mode, string? destination, bool expected)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (mode is not null)
            headers.Add("Sec-Fetch-Mode", mode);
        if (destination is not null)
            headers.Add("Sec-Fetch-Dest", destination);
        var request = new RemoteHttpRequest(method, path, "", headers, "", false);

        Assert.Equal(expected, RemoteWebAppHandler.IsApiDocumentNavigation(request));
    }

    [Fact]
    public void PwaUsesAndroidArtworkAndSeparateMaskableIcons()
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "BrowserAssets");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(assets, "manifest.webmanifest")));
        Assert.Equal("./", manifest.RootElement.GetProperty("start_url").GetString());
        Assert.Equal("./", manifest.RootElement.GetProperty("scope").GetString());

        var icons = manifest.RootElement.GetProperty("icons").EnumerateArray().ToArray();
        var normal = icons.Where(icon => icon.GetProperty("purpose").GetString() == "any").ToArray();
        Assert.Equal(2, normal.Length);
        Assert.Contains(normal, icon => icon.GetProperty("sizes").GetString() == "192x192");
        Assert.Contains(normal, icon => icon.GetProperty("sizes").GetString() == "512x512");
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(assets, "android-launcher.png")),
            File.ReadAllBytes(Path.Combine(assets, "icons", "lumi-192.png")));

        var maskable = Assert.Single(icons, icon => icon.GetProperty("purpose").GetString() == "maskable");
        Assert.Equal("./icons/lumi-maskable-512.png", maskable.GetProperty("src").GetString());
        using var bitmap = SkiaSharp.SKBitmap.Decode(Path.Combine(assets, "icons", "lumi-maskable-512.png"));
        Assert.NotNull(bitmap);
        Assert.Equal(512, bitmap.Width);
        Assert.Equal(512, bitmap.Height);
        var foregroundPixels = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            Assert.Equal(255, pixel.Alpha);
            if (pixel.Red >= 200 && pixel.Green >= 175 && pixel.Blue >= 225)
            {
                foregroundPixels++;
                var dx = x + 0.5 - bitmap.Width / 2.0;
                var dy = y + 0.5 - bitmap.Height / 2.0;
                Assert.True(dx * dx + dy * dy <= Math.Pow(bitmap.Width * 0.4, 2),
                    "Maskable foreground must remain inside the Android safe-area circle.");
            }
        }
        Assert.True(foregroundPixels > 1000);
    }

    [Fact]
    public void DefaultAssetRootUsesExecutableDirectoryInsteadOfExtractionDirectory()
    {
        var executableDirectory = Path.Combine(Path.GetTempPath(), "installed-lumi");
        var extractionDirectory = Path.Combine(Path.GetTempPath(), "single-file-extraction");
        var executablePath = Path.Combine(executableDirectory, "Lumi.exe");

        Assert.Equal(
            Path.Combine(executableDirectory, "remote-web"),
            RemoteWebAssetProvider.ResolveDefaultRoot(executablePath, extractionDirectory));
    }

    [Fact]
    public async Task AppShellIsServedWithPwaSecurityHeaders()
    {
        using var fixture = new WebFixture();
        fixture.Write("index.html", "<!doctype html><title>Lumi</title>");

        var response = await fixture.RequestAsync("GET", "/app/");

        Assert.StartsWith("HTTP/1.1 200 OK", response.Headers);
        Assert.Contains("Content-Type: text/html; charset=utf-8", response.Headers);
        Assert.Contains("Cache-Control: no-cache", response.Headers);
        Assert.Contains("Content-Security-Policy:", response.Headers);
        Assert.Contains("X-Content-Type-Options: nosniff", response.Headers);
        Assert.Equal("<!doctype html><title>Lumi</title>", response.Body);
    }

    [Fact]
    public async Task HeadReturnsHeadersWithoutStaticBody()
    {
        using var fixture = new WebFixture();
        fixture.Write("_framework/runtime.abc12345.js", "console.log('runtime');");

        var response = await fixture.RequestAsync("HEAD", "/app/_framework/runtime.abc12345.js");

        Assert.StartsWith("HTTP/1.1 200 OK", response.Headers);
        Assert.Contains("Cache-Control: public, max-age=31536000, immutable", response.Headers);
        Assert.Empty(response.Body);
    }

    [Fact]
    public async Task MatchingEtagReturnsNotModified()
    {
        using var fixture = new WebFixture();
        fixture.Write("main.abc12345.js", "console.log('cached');");

        var first = await fixture.RequestAsync("GET", "/app/main.abc12345.js");
        var etag = first.Header("ETag");
        var second = await fixture.RequestAsync(
            "GET",
            "/app/main.abc12345.js",
            new Dictionary<string, string> { ["If-None-Match"] = etag });

        Assert.StartsWith("HTTP/1.1 304 Not Modified", second.Headers);
        Assert.Empty(second.Body);
    }

    [Fact]
    public async Task ExtensionlessRouteFallsBackButMissingAssetDoesNot()
    {
        using var fixture = new WebFixture();
        fixture.Write("index.html", "shell");

        var route = await fixture.RequestAsync("GET", "/app/chat/123");
        var missingAsset = await fixture.RequestAsync("GET", "/app/missing.js");

        Assert.Equal("shell", route.Body);
        Assert.StartsWith("HTTP/1.1 404 Not Found", missingAsset.Headers);
    }

    [Theory]
    [InlineData("/app/../secret.txt")]
    [InlineData("/app/%2e%2e/secret.txt")]
    [InlineData("/app/folder%5csecret.txt")]
    public async Task TraversalAndEncodedSeparatorsAreRejected(string path)
    {
        using var fixture = new WebFixture();
        fixture.Write("index.html", "shell");

        var response = await fixture.RequestAsync("GET", path);

        Assert.StartsWith("HTTP/1.1 404 Not Found", response.Headers);
    }

    [Fact]
    public async Task RootRedirectsToApp()
    {
        using var fixture = new WebFixture();

        var response = await fixture.RequestAsync("GET", "/");

        Assert.StartsWith("HTTP/1.1 302 Found", response.Headers);
        Assert.Contains("Location: /app/", response.Headers);
    }

    [Fact]
    public async Task AndroidInstallerAssetsAreServedAsExactStaticFiles()
    {
        using var fixture = new WebFixture();
        fixture.Write("index.html", "shell");
        fixture.Write("android.html", "<title>Install Lumi</title>");
        fixture.Write("android-install.js", "console.log('installer');");

        var page = await fixture.RequestAsync("GET", "/app/android.html");
        var script = await fixture.RequestAsync("GET", "/app/android-install.js");

        Assert.Equal("<title>Install Lumi</title>", page.Body);
        Assert.Contains("Content-Type: text/html; charset=utf-8", page.Headers);
        Assert.Equal("console.log('installer');", script.Body);
        Assert.Contains("Content-Type: text/javascript; charset=utf-8", script.Headers);
    }

    [Fact]
    public void AssetProviderUsesBrotliAndFallsBackToRawWhenGzipIsNotPackaged()
    {
        using var fixture = new WebFixture();
        fixture.Write("main.js", "raw");
        fixture.Write("main.js.br", "brotli");
        var provider = new RemoteWebAssetProvider(fixture.Root);

        var brotliRequest = new RemoteHttpRequest(
            "GET",
            "/app/main.js",
            "",
            new Dictionary<string, string> { ["Accept-Encoding"] = "gzip, br" },
            "",
            KeepAlive: false);
        var gzipOnlyRequest = brotliRequest with
        {
            Headers = new Dictionary<string, string> { ["Accept-Encoding"] = "gzip" }
        };

        Assert.True(provider.TryGet("main.js", brotliRequest, out var brotli));
        Assert.Equal("br", brotli.ContentEncoding);
        Assert.Equal(Path.Combine(fixture.Root, "main.js.br"), brotli.FullPath);

        Assert.True(provider.TryGet("main.js", gzipOnlyRequest, out var raw));
        Assert.Null(raw.ContentEncoding);
        Assert.Equal(Path.Combine(fixture.Root, "main.js"), raw.FullPath);
    }

    private sealed class WebFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "Lumi-web-tests",
            Guid.NewGuid().ToString("N"));

        public WebFixture() => Directory.CreateDirectory(_root);

        public string Root => _root;

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public async Task<Response> RequestAsync(
            string method,
            string path,
            Dictionary<string, string>? headers = null)
        {
            var request = new RemoteHttpRequest(
                method,
                path,
                "",
                headers ?? new Dictionary<string, string>(),
                "",
                KeepAlive: false);
            await using var output = new MemoryStream();
            var context = new RemoteHttpContext(
                request,
                output,
                new IPEndPoint(IPAddress.Loopback, 12345),
                new IPEndPoint(IPAddress.Loopback, 47653));
            var handler = new RemoteWebAppHandler(new RemoteWebAssetProvider(_root));

            await handler.HandleAsync(context, CancellationToken.None);

            var bytes = output.ToArray();
            var separator = Encoding.UTF8.GetBytes("\r\n\r\n");
            var headerEnd = bytes.AsSpan().IndexOf(separator);
            Assert.True(headerEnd >= 0);
            return new Response(
                Encoding.UTF8.GetString(bytes, 0, headerEnd),
                Encoding.UTF8.GetString(bytes, headerEnd + separator.Length, bytes.Length - headerEnd - separator.Length));
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }

    private sealed record Response(string Headers, string Body)
    {
        public string Header(string name) =>
            Headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2)
                .First(parts => string.Equals(parts[0], name, StringComparison.OrdinalIgnoreCase))[1]
                .Trim();
    }
}
