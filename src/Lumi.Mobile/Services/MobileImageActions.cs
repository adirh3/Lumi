using System.Text.RegularExpressions;
using StrataTheme.Controls;

namespace Lumi.Mobile.Services;

/// <summary>
/// What the phone adds to the full-screen viewer of a chat's pictures: the share sheet, and saving the
/// phone's way — into its photos where it has a library Lumi can save to, as a download in a browser, and
/// through the save dialog otherwise. The pictures are the phone's private copies of the PC's, so their
/// place on disk is not offered.
/// </summary>
internal sealed partial class MobileImageActions : IStrataImageActionHost
{
    private static IProducedFileOpener Opener => MobilePlatformServices.ProducedFileOpener;

    public bool CanShare => Opener.CanShare;

    public StrataImageSaveStyle SaveStyle =>
        Opener.CanSaveToPhotos ? StrataImageSaveStyle.SaveToPhotos
        : Opener.OpensBySaving ? StrataImageSaveStyle.Download
        : StrataImageSaveStyle.SaveAs;

    public bool OffersFileLocation => false;

    /// <summary>A phone's clipboard takes text from Lumi, not pictures.</summary>
    public bool CanCopyImage => false;

    public Task<bool> ShareAsync(StrataImageFile image, CancellationToken cancellationToken) =>
        WithLocalFileAsync(image, cancellationToken, path =>
            Opener.TryShareAsync(path, DisplayName(image), cancellationToken));

    public Task<bool> SaveAsync(StrataImageFile image, CancellationToken cancellationToken) =>
        SaveStyle == StrataImageSaveStyle.SaveToPhotos
            ? WithLocalFileAsync(image, cancellationToken, path =>
                Opener.TrySaveToPhotosAsync(path, DisplayName(image), cancellationToken))
            // A download lets go of what it downloaded once it is done, and the chat still shows this
            // picture, so the download always gets a copy of its own.
            : WithCopyAsync(image, cancellationToken, path =>
                Opener.TryOpenAsync(path, DisplayName(image), cancellationToken));

    /// <summary>The platform takes files: a picture from the web is handed over as a copy on the phone.</summary>
    private static Task<bool> WithLocalFileAsync(
        StrataImageFile image, CancellationToken cancellationToken, Func<string, Task<bool>> action) =>
        image.LocalPath is { } path && File.Exists(path)
            ? action(path)
            : WithCopyAsync(image, cancellationToken, action);

    private static async Task<bool> WithCopyAsync(
        StrataImageFile image, CancellationToken cancellationToken, Func<string, Task<bool>> action)
    {
        var folder = Path.Combine(Path.GetTempPath(), "LumiMobile", "image-copies");
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, $"{Guid.NewGuid():N}{Path.GetExtension(image.FileName)}");
        try
        {
            await File.WriteAllBytesAsync(copy, await image.ReadAllBytesAsync(cancellationToken), cancellationToken);
            return await action(copy);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    /// <summary>
    /// The picture's own name. A copy brought from the PC is cached as "chat-message-index-name.png", and
    /// the app it goes to should see only "name.png".
    /// </summary>
    internal static string DisplayName(StrataImageFile image)
    {
        var name = CachedImagePrefix().Replace(image.FileName, string.Empty);
        return name.Length > 0 ? name : image.FileName;
    }

    [GeneratedRegex("^[0-9a-f]{32}-[0-9a-f]{32}-[0-9]+-", RegexOptions.CultureInvariant)]
    private static partial Regex CachedImagePrefix();
}
