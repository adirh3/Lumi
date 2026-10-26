using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Lumi.Services.Sharing;
using StrataTheme.Controls;

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
    public static async Task CopyTextAsync(string? text, bool throwOnFailure = false)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var clipboard = ActiveTopLevel()?.Clipboard;
        if (clipboard is null && throwOnFailure)
            throw new InvalidOperationException("The system clipboard is unavailable.");
        if (clipboard is null)
            return;

        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(text));
            await clipboard.SetDataAsync(data);
        }
        catch when (!throwOnFailure)
        {
            /* clipboard can be transiently locked by another process — ignore */
        }
    }

    /// <summary>
    /// Copies text together with an HTML rendering of it, so rich editors (Teams, Outlook, Slack)
    /// paste the formatted version — for example a real code block — and everything else gets the
    /// plain text. <paramref name="htmlFragment"/> is body content, not a full document.
    /// </summary>
    public static async Task CopyTextAndHtmlAsync(string text, string htmlFragment, bool throwOnFailure = false)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var clipboard = ActiveTopLevel()?.Clipboard;
        if (clipboard is null && throwOnFailure)
            throw new InvalidOperationException("The system clipboard is unavailable.");
        if (clipboard is null)
            return;

        try
        {
            var data = ChatClipboardData.CreateHtml(htmlFragment, text);
            await clipboard.SetDataAsync(data);
        }
        catch
        {
            // A rich copy that fails (clipboard locked, format refused) still leaves the user able to copy text.
            await CopyTextAsync(text, throwOnFailure);
        }
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

    /// <summary>Inspects copied capabilities without blocking window activation on a Windows clipboard owner.</summary>
    public static Task<string?> GetTextForNoticeAsync()
    {
#if WINDOWS
        return GetTextForNoticeAsync(ReadWindowsText);
#else
        return GetTextAsync();
#endif
    }

    internal static Task<string?> GetTextForNoticeAsync(Func<string?> readText) => Task.Run(readText);

#if WINDOWS
    [SupportedOSPlatform("windows")]
    private static unsafe string? ReadWindowsText()
    {
        const uint unicodeText = 13;
        // Windows-only workaround for Avalonia 12.1.4's UI-bound, synchronous OLE read.
        // Keep this notice-only; remove after an Avalonia read passes the delayed-render
        // repro without blocking the UI (docs/development.md).
        if (!OpenClipboard(IntPtr.Zero))
        {
            Debug.WriteLine($"[Clipboard] Automatic read could not open the clipboard ({Marshal.GetLastWin32Error()}).");
            return null;
        }

        try
        {
            if (!IsClipboardFormatAvailable(unicodeText))
                return null;

            var handle = GetClipboardData(unicodeText);
            if (handle == IntPtr.Zero)
            {
                Debug.WriteLine($"[Clipboard] Automatic text read failed ({Marshal.GetLastWin32Error()}).");
                return null;
            }

            var characterCount = GlobalSize(handle) / sizeof(char);
            if (characterCount == 0)
                return null;

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                Debug.WriteLine($"[Clipboard] Automatic text read could not lock the data ({Marshal.GetLastWin32Error()}).");
                return null;
            }

            try
            {
                var length = (int)Math.Min(characterCount, (nuint)CapabilityPackReader.MaxTextLength + 1);
                var text = new ReadOnlySpan<char>((void*)pointer, length);
                var terminator = text.IndexOf('\0');
                if (terminator < 0)
                    return null;
                return new string(text[..terminator]);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(IntPtr handle);
#endif

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
