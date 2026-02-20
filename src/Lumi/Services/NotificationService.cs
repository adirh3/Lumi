using System;
using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;

namespace Lumi.Services;

/// <summary>
/// Sends native OS desktop notifications.
/// Windows: In-process WinRT toast via CommunityToolkit (click activates window).
/// macOS: osascript. Linux: notify-send.
/// </summary>
public static class NotificationService
{
    private static bool _compatListenerRegistered;

    /// <summary>Shows a native desktop notification if the main window is not active.</summary>
    public static void ShowIfInactive(string title, string body)
    {
        if (IsMainWindowActive())
            return;

        Show(title, body);
    }

    /// <summary>Shows a native desktop notification unconditionally.</summary>
    public static void Show(string title, string body)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                ShowWindows(title, body);
            else if (OperatingSystem.IsMacOS())
                ShowMacOS(title, body);
            else if (OperatingSystem.IsLinux())
                ShowLinux(title, body);
        }
        catch
        {
            // Notification is best-effort
        }
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

    private static void ShowWindows(string title, string body)
    {
        // Register the static OnActivated handler once — CommunityToolkit routes
        // toast clicks through COM activation for unpackaged apps.
        if (!_compatListenerRegistered)
        {
            _compatListenerRegistered = true;
            Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat.OnActivated += _ =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (Avalonia.Application.Current is App app)
                        app.ShowMainWindow();
                });
            };
        }

        var notifier = Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat
            .CreateToastNotifier();

        var toastXml = Windows.UI.Notifications.ToastNotificationManager
            .GetTemplateContent(Windows.UI.Notifications.ToastTemplateType.ToastText02);

        var textNodes = toastXml.GetElementsByTagName("text");
        textNodes[0].AppendChild(toastXml.CreateTextNode(title));
        textNodes[1].AppendChild(toastXml.CreateTextNode(body));

        var toast = new Windows.UI.Notifications.ToastNotification(toastXml);
        toast.Activated += (_, _) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (Avalonia.Application.Current is App app)
                    app.ShowMainWindow();
            });
        };
        notifier.Show(toast);
    }

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
