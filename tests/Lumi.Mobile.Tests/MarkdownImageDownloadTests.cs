using System.Net;
using Lumi.Mobile.Services;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class MarkdownImageDownloadTests
{
    [Fact]
    public async Task ClientDownloadsAnAuthenticatedReferencedImageToItsCache()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var handler = new ImageHandler(bytes);
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            handler,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            new TrustedRouteVerifier());
        client.Configure("http://100.64.0.1:62135", "secret-token");
        var chatId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        string? path = null;
        try
        {
            path = await client.DownloadMarkdownImageAsync(
                chatId,
                messageId,
                2,
                "preview.png",
                CancellationToken.None);

            Assert.NotNull(path);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path!));
            Assert.Equal(
                $"/lumi/markdown-image?chatId={chatId}&messageId={messageId}&imageIndex=2",
                handler.RequestUri?.PathAndQuery);
            Assert.Equal("secret-token", handler.DeviceToken);
        }
        finally
        {
            if (path is not null && File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ConcurrentImageDownloadsAreGloballyBounded()
    {
        var handler = new ConcurrentImageHandler();
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            handler,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            new TrustedRouteVerifier());
        client.Configure("http://100.64.0.1:62135", "secret-token");
        var chatId = Guid.NewGuid();
        var tasks = Enumerable.Range(0, 9)
            .Select(index => client.DownloadMarkdownImageAsync(
                chatId,
                Guid.NewGuid(),
                index,
                $"preview-{index}.png",
                CancellationToken.None))
            .ToArray();
        var paths = await Task.WhenAll(tasks);
        try
        {
            Assert.True(
                handler.MaximumConcurrentRequests
                <= LumiRemoteClient.MaxConcurrentMarkdownImageDownloads);
        }
        finally
        {
            foreach (var path in paths)
            {
                if (path is not null && File.Exists(path))
                    File.Delete(path);
            }
        }
    }

    [Fact]
    public void MarkdownImageCachePruningEnforcesAgeAndSizeBudgets()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "LumiMobileTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var expired = Path.Combine(folder, "expired.png");
            var older = Path.Combine(folder, "older.png");
            var newest = Path.Combine(folder, "newest.png");
            File.WriteAllBytes(expired, new byte[10]);
            File.WriteAllBytes(older, new byte[10]);
            File.WriteAllBytes(newest, new byte[10]);
            File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-2));
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-2));
            File.SetLastWriteTimeUtc(newest, DateTime.UtcNow.AddMinutes(-1));

            LumiRemoteClient.PruneMarkdownImageCache(
                folder,
                DateTime.UtcNow.AddDays(-1),
                maxBytes: 15,
                preservedPath: newest);

            Assert.False(File.Exists(expired));
            Assert.False(File.Exists(older));
            Assert.True(File.Exists(newest));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class ImageHandler(byte[] bytes) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? DeviceToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            DeviceToken = request.Headers.TryGetValues(
                RemoteProtocol.DeviceTokenHeader,
                out var values)
                ? values.SingleOrDefault()
                : null;
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentLength = bytes.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
        }
    }

    private sealed class ConcurrentImageHandler : HttpMessageHandler
    {
        private int _currentRequests;
        private int _maximumConcurrentRequests;

        public int MaximumConcurrentRequests =>
            Volatile.Read(ref _maximumConcurrentRequests);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref _currentRequests);
            int observed;
            while (current > (observed = Volatile.Read(ref _maximumConcurrentRequests)))
            {
                if (Interlocked.CompareExchange(
                        ref _maximumConcurrentRequests,
                        current,
                        observed) == observed)
                {
                    break;
                }
            }

            try
            {
                await Task.Delay(75, cancellationToken);
                var content = new ByteArrayContent([1]);
                content.Headers.ContentLength = 1;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content
                };
            }
            finally
            {
                Interlocked.Decrement(ref _currentRequests);
            }
        }
    }

    private sealed class TrustedRouteVerifier : IRemoteRouteVerifier
    {
        public bool IsTrustedTailscaleRoute(IPAddress targetAddress) => true;
    }
}
