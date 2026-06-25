using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
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
