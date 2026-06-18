using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls.ApplicationLifetimes;
using Lumi.Localization;

namespace Lumi.Services;

/// <summary>
/// Sends native OS desktop notifications.
/// Windows: In-process WinRT toast via CommunityToolkit (click activates window) + taskbar flash.
/// macOS: osascript. Linux: notify-send.
/// </summary>
public static class NotificationService
{
    private const string ChatActivationArgumentKey = "chatId";
    private static bool _compatListenerRegistered;

    /// <summary>Shows a native desktop notification if the main window is not active.</summary>
    public static void ShowIfInactive(string title, string body, Guid? chatId = null)
    {
        if (IsMainWindowActive())
            return;

        Show(title, body, chatId);

        if (OperatingSystem.IsWindows())
            FlashMainWindow();
    }

    /// <summary>Shows a native desktop notification unconditionally.</summary>
    public static void Show(string title, string body, Guid? chatId = null)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                ShowWindows(title, body, chatId);
            else if (OperatingSystem.IsMacOS())
                ShowMacOS(title, body);
            else if (OperatingSystem.IsLinux())
                ShowLinux(title, body);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Notification] Failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void ShowQuestion(string question, string? contextTitle = null, Guid? chatId = null)
        => Show(Loc.Notification_QuestionTitle, FormatQuestionNotificationBody(contextTitle, question), chatId);

    internal static string FormatQuestionNotificationBody(string? contextTitle, string question)
    {
        var body = string.IsNullOrWhiteSpace(question)
            ? Loc.Notification_QuestionBody
            : question.Trim();

        return string.IsNullOrWhiteSpace(contextTitle)
            ? body
            : $"{contextTitle.Trim()} — {body}";
    }

    private static bool IsMainWindowActive()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } w)
        {
            return w.IsVisible && w.IsActive
                && w.WindowState != Avalonia.Controls.WindowState.Minimized;
        }
        return true;
    }

    internal static string CreateActivationArguments(Guid? chatId)
    {
        if (chatId is not Guid targetChatId)
            return string.Empty;

        return $"{ChatActivationArgumentKey}={targetChatId:D}";
    }

    internal static Guid? ParseChatIdFromActivationArguments(string? activationArguments)
    {
        if (string.IsNullOrWhiteSpace(activationArguments))
            return null;

        foreach (var segment in activationArguments.Split('&',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = segment.Split('=', 2);
            if (parts.Length != 2
                || !string.Equals(parts[0], ChatActivationArgumentKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return Guid.TryParse(parts[1], out var chatId) ? chatId : null;
        }

        return null;
    }

    private static void ActivateMainWindow(Guid? chatId = null)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (Avalonia.Application.Current is App app)
                app.ShowMainWindow(chatId);
        });
    }

    // ── Windows toast ──────────────────────────────────────────────

    /// <summary>Ensures the CommunityToolkit compat layer is set up (AUMID + COM activation).
    /// Must be called before creating a notifier. Safe to call multiple times.</summary>
    private static void EnsureWindowsCompat()
    {
        if (_compatListenerRegistered)
            return;

        try
        {
            // The toolkit creates a Start Menu shortcut (e.g. Lumi.lnk) with the AUMID
            // derived from the executable path. If the build output path changes
            // (e.g. net10→net11, Debug→Release), the stale shortcut causes AUMID mismatches
            // that silently drop toast notifications. Delete it so the toolkit recreates it.
            CleanStaleShortcut();
            Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat.OnActivated += toastArgs =>
                ActivateMainWindow(ParseChatIdFromActivationArguments(toastArgs.Argument));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Notification] COM activation registration failed: {ex.Message}");
        }

        _compatListenerRegistered = true;
    }

    /// <summary>Removes stale Start Menu shortcut if its target doesn't match the current executable.</summary>
    private static void CleanStaleShortcut()
    {
        try
        {
            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe)) return;

            var shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs",
                Path.GetFileNameWithoutExtension(currentExe) + ".lnk");

            if (!File.Exists(shortcutPath))
                return;

            // If reading fails, delete anyway — a stale shortcut is worse than recreating.
            var targetPath = ReadShortcutTarget(shortcutPath);
            if (targetPath is null
                || !string.Equals(targetPath, currentExe, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(shortcutPath);
            }
        }
        catch { }
    }

    private static string? ReadShortcutTarget(string lnkPath)
    {
        try
        {
            var shellLink = (IShellLinkW)new CShellLink();
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)shellLink).Load(lnkPath, 0);
            var buf = new char[260];
            shellLink.GetPath(buf, buf.Length, nint.Zero, 0);
            return new string(buf).TrimEnd('\0');
        }
        catch { return null; }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszFile,
                      int cch, nint pfd, uint fFlags);
    }

    private static void ShowWindows(string title, string body, Guid? chatId)
    {
        EnsureWindowsCompat();

        var notifier = Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat
            .CreateToastNotifier();

        var toastXml = Windows.UI.Notifications.ToastNotificationManager
            .GetTemplateContent(Windows.UI.Notifications.ToastTemplateType.ToastText02);

        var textNodes = toastXml.GetElementsByTagName("text");
        textNodes[0].AppendChild(toastXml.CreateTextNode(title));
        textNodes[1].AppendChild(toastXml.CreateTextNode(body));

        var activationArguments = CreateActivationArguments(chatId);
        if (!string.IsNullOrWhiteSpace(activationArguments))
            toastXml.DocumentElement?.SetAttribute("launch", activationArguments);

        var toast = new Windows.UI.Notifications.ToastNotification(toastXml);
        toast.Activated += (_, _) => ActivateMainWindow(chatId);
        notifier.Show(toast);
    }

    // ── Taskbar flash ──────────────────────────────────────────────

    private static void FlashMainWindow()
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow?.TryGetPlatformHandle()?.Handle is { } hwnd
                && hwnd != nint.Zero)
            {
                var fi = new FLASHWINFO
                {
                    cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                    hwnd = hwnd,
                    dwFlags = 0x03 | 0x0C, // FLASHW_ALL | FLASHW_TIMERNOFG
                    uCount = 3,
                    dwTimeout = 0
                };
                FlashWindowEx(ref fi);
            }
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pfwi);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public nint hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    // ── macOS / Linux ──────────────────────────────────────────────

    private static void ShowMacOS(string title, string body)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "osascript",
            ArgumentList =
            {
                "-e",
                $"display notification \"{ShellEscape(body)}\" with title \"{ShellEscape(title)}\""
            },
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    private static void ShowLinux(string title, string body)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "notify-send",
            ArgumentList = { title, body },
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    private static string ShellEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
