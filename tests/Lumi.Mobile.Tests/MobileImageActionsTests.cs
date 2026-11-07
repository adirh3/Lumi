using Avalonia.Controls;
using Avalonia.LogicalTree;
using Lumi.Mobile.Services;
using Lumi.Mobile.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Mobile.Tests;

/// <summary>
/// A chat's pictures open full screen with the phone's own Share and Save: into the photos where the phone
/// has a library, as a download in a browser, and through the save dialog otherwise.
/// </summary>
[Collection("Headless mobile UI")]
public sealed class MobileImageActionsTests
{
    [Fact]
    public async Task EveryMarkdownInTheShellUsesThePhonesImageActions()
    {
        using var session = HeadlessMobileSession.Start();
        await session.Dispatch(async () =>
        {
            var view = new MobileShellView();
            Assert.IsType<MobileImageActions>(StrataMarkdown.GetImageActionHost(view));
            var deepest = view.GetLogicalDescendants().OfType<Control>().Last();
            var host = Assert.IsType<MobileImageActions>(StrataMarkdown.GetImageActionHost(deepest));
            Assert.False(host.OffersFileLocation);
            Assert.False(host.CanCopyImage);
            await Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SavingFollowsWhatThePlatformCanDo()
    {
        var previous = MobilePlatformServices.ProducedFileOpener;
        var host = new MobileImageActions();
        try
        {
            var opener = new RecordingOpener { CanSaveToPhotos = true, CanShare = true };
            MobilePlatformServices.ProducedFileOpener = opener;
            Assert.Equal(StrataImageSaveStyle.SaveToPhotos, host.SaveStyle);
            Assert.True(host.CanShare);

            var cached = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{Guid.NewGuid():N}-2-sunset.png");
            File.WriteAllBytes(cached, [1, 2, 3]);
            var image = new StrataImageFile(
                Path.GetFileName(cached),
                cached,
                null,
                _ => Task.FromResult(new byte[] { 1, 2, 3 }));
            Assert.True(await host.SaveAsync(image, CancellationToken.None));
            Assert.True(await host.ShareAsync(image, CancellationToken.None));
            Assert.Equal(
                [("photos", cached, "sunset.png"), ("share", cached, "sunset.png")],
                opener.Calls.Select(call => (call.Action, call.Path, call.Name)));
            File.Delete(cached);

            // A picture from the web is shared as a copy on the phone, which goes once it is handed over.
            opener.Calls.Clear();
            var webImage = new StrataImageFile("chart.png", null, new Uri("https://example.com/chart.png"),
                _ => Task.FromResult(new byte[] { 4, 5 }));
            Assert.True(await host.ShareAsync(webImage, CancellationToken.None));
            var shared = Assert.Single(opener.Calls);
            Assert.Equal("chart.png", shared.Name);
            Assert.Equal(new byte[] { 4, 5 }, shared.Bytes);
            Assert.False(File.Exists(shared.Path));

            // A browser downloads a copy of its own, so the chat's picture stays where it is.
            opener = new RecordingOpener { OpensBySaving = true };
            MobilePlatformServices.ProducedFileOpener = opener;
            Assert.Equal(StrataImageSaveStyle.Download, host.SaveStyle);
            var browserImage = new StrataImageFile("Holiday photo.jpg", null, new Uri("blob:https://lumi.local/1234"),
                _ => Task.FromResult(new byte[] { 9, 8, 7 }));
            Assert.True(await host.SaveAsync(browserImage, CancellationToken.None));
            var download = Assert.Single(opener.Calls);
            Assert.Equal("Holiday photo.jpg", download.Name);
            Assert.NotEqual("blob:https://lumi.local/1234", download.Path);
            Assert.Equal(new byte[] { 9, 8, 7 }, download.Bytes);
            Assert.False(File.Exists(download.Path));

            MobilePlatformServices.ProducedFileOpener = new RecordingOpener();
            Assert.Equal(StrataImageSaveStyle.SaveAs, host.SaveStyle);
            Assert.False(host.CanShare);
        }
        finally
        {
            MobilePlatformServices.ProducedFileOpener = previous;
        }
    }

    private sealed class RecordingOpener : IProducedFileOpener
    {
        public List<(string Action, string Path, string Name, byte[]? Bytes)> Calls { get; } = [];

        public bool CanShare { get; init; }

        public bool CanSaveToPhotos { get; init; }

        public bool OpensBySaving { get; init; }

        public Task<bool> TryOpenAsync(string downloadedPath, string displayName, CancellationToken cancellationToken)
        {
            Calls.Add(("open", downloadedPath, displayName, File.Exists(downloadedPath) ? File.ReadAllBytes(downloadedPath) : null));
            return Task.FromResult(true);
        }

        public Task<bool> TryShareAsync(string downloadedPath, string displayName, CancellationToken cancellationToken)
        {
            Calls.Add(("share", downloadedPath, displayName, File.Exists(downloadedPath) ? File.ReadAllBytes(downloadedPath) : null));
            return Task.FromResult(true);
        }

        public Task<bool> TrySaveToPhotosAsync(string downloadedPath, string displayName, CancellationToken cancellationToken)
        {
            Calls.Add(("photos", downloadedPath, displayName, null));
            return Task.FromResult(true);
        }
    }
}
