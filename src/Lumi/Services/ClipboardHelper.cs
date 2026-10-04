using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace Lumi.Services;

/// <summary>
/// Resolves the active window's clipboard and storage provider so lightweight view models
/// (e.g. file attachment chips shown in the transcript and workspace rail) can copy text or the
/// file itself without holding a reference to a view. Mirrors the Avalonia 12 <see cref="DataTransfer"/>
/// clipboard API already used by <c>ChatView</c>.
/// </summary>
public static class ClipboardHelper
{
    private static TopLevel? ActiveTopLevel()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.Windows.FirstOrDefault(static w => w.IsActive)
                   ?? desktop.MainWindow
                   ?? desktop.Windows.FirstOrDefault();
        }

        return null;
    }

    /// <summary>Copies plain text to the system clipboard.</summary>
    public static async Task CopyTextAsync(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var clipboard = ActiveTopLevel()?.Clipboard;
        if (clipboard is null)
            return;

        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(text));
            await clipboard.SetDataAsync(data);
        }
        catch
        {
            /* clipboard can be transiently locked by another process — ignore */
        }
    }

    /// <summary>
    /// Copies text together with an HTML rendering of it, so rich editors (Teams, Outlook, Slack)
    /// paste the formatted version — for example a real code block — and everything else gets the
    /// plain text. <paramref name="htmlFragment"/> is body content, not a full document.
    /// </summary>
    public static async Task CopyTextAndHtmlAsync(string text, string htmlFragment)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var clipboard = ActiveTopLevel()?.Clipboard;
        if (clipboard is null)
            return;

        try
        {
            var item = DataTransferItem.CreateText(text);
            if (OperatingSystem.IsWindows())
                item.Set(DataFormat.CreateBytesPlatformFormat("HTML Format"), BuildWindowsHtmlClipboard(htmlFragment));
            else if (OperatingSystem.IsMacOS())
                item.Set(DataFormat.CreateStringPlatformFormat("public.html"), htmlFragment);
            else
                item.Set(DataFormat.CreateBytesPlatformFormat("text/html"), System.Text.Encoding.UTF8.GetBytes(htmlFragment));

            var data = new DataTransfer();
            data.Add(item);
            await clipboard.SetDataAsync(data);
        }
        catch
        {
            // A rich copy that fails (clipboard locked, format refused) still leaves the user able to copy text.
            await CopyTextAsync(text);
        }
    }

    /// <summary>
    /// Windows' CF_HTML clipboard format: a header of byte offsets (UTF-8) around the fragment, as
    /// every Windows editor expects when it reads "HTML Format".
    /// </summary>
    internal static byte[] BuildWindowsHtmlClipboard(string fragment)
    {
        const string headerTemplate = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string documentStart = "<html><body>\r\n<!--StartFragment-->";
        const string documentEnd = "<!--EndFragment-->\r\n</body></html>";

        var utf8 = System.Text.Encoding.UTF8;
        var startHtml = utf8.GetByteCount(string.Format(System.Globalization.CultureInfo.InvariantCulture, headerTemplate, 0, 0, 0, 0));
        var startFragment = startHtml + utf8.GetByteCount(documentStart);
        var endFragment = startFragment + utf8.GetByteCount(fragment);
        var endHtml = endFragment + utf8.GetByteCount(documentEnd);

        var header = string.Format(System.Globalization.CultureInfo.InvariantCulture, headerTemplate, startHtml, endHtml, startFragment, endFragment);
        return utf8.GetBytes(header + documentStart + fragment + documentEnd);
    }

    /// <summary>Reads plain text from the system clipboard; null when it has none or cannot be read.</summary>
    public static async Task<string?> GetTextAsync()
    {
        var clipboard = ActiveTopLevel()?.Clipboard;
        if (clipboard is null)
            return null;

        try
        {
            return await ClipboardExtensions.TryGetTextAsync(clipboard);
        }
        catch
        {
            /* clipboard can be transiently locked by another process — treat as empty */
            return null;
        }
    }

    /// <summary>
    /// Copies the file itself to the clipboard so it can be pasted into a folder (Windows Explorer,
    /// Finder, etc.). Falls back to copying the path as text when the file can't be resolved.
    /// </summary>
    public static async Task CopyFileAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return;

        var topLevel = ActiveTopLevel();
        var clipboard = topLevel?.Clipboard;
        if (clipboard is null)
            return;

        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(filePath));

            var storageProvider = topLevel?.StorageProvider;
            if (storageProvider is not null && File.Exists(filePath))
            {
                var item = await storageProvider.TryGetFileFromPathAsync(filePath);
                if (item is not null)
                    data.Add(DataTransferItem.CreateFile(item));
            }

            await clipboard.SetDataAsync(data);
        }
        catch
        {
            /* ignore */
        }
    }
}
