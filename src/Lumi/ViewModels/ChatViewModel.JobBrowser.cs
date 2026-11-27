using System.Diagnostics;
using Lumi.Services;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    private readonly Dictionary<Guid, JobBrowserSession> _jobBrowserSessions = [];

    internal bool IsChatBusyForBackgroundJob(Guid chatId)
        => IsChatBusyForSend(chatId) || _jobBrowserSessions.ContainsKey(chatId);

    private sealed class JobBrowserSession(BrowserService browser, BrowserService? previous, bool wasOpen)
    {
        public BrowserService Browser { get; } = browser;
        public BrowserService? Previous { get; } = previous;
        public bool WasOpen { get; } = wasOpen;
        public bool IsAttached { get; set; } = true;
    }

    internal void BeginJobBrowserSession(Guid chatId)
    {
        if (_jobBrowserSessions.ContainsKey(chatId))
            throw new InvalidOperationException("The previous background job still owns a browser session.");

        _chatBrowserServices.TryGetValue(chatId, out var previous);
        // A separate coordinator shares the login profile, never the user's live tabs.
        // Its blank tab is cheap: no native controller is created until a browser tool runs.
        var browser = new BrowserService();
        _jobBrowserSessions.Add(chatId, new JobBrowserSession(
            browser, previous, CurrentChat?.Id == chatId && IsBrowserOpen));
        _chatBrowserServices[chatId] = browser;
    }

    private void DetachJobBrowserSession(Guid chatId)
    {
        if (!_jobBrowserSessions.TryGetValue(chatId, out var session) || !session.IsAttached)
            return;

        session.IsAttached = false;
        if (!_chatBrowserServices.TryGetValue(chatId, out var current)
            || !ReferenceEquals(current, session.Browser))
            return;

        if (session.Previous is { } previous)
            _chatBrowserServices[chatId] = previous;
        else
            _chatBrowserServices.TryRemove(chatId, out _);

        session.Browser.SetControllerVisible(false);
        if (_isDisposed || CurrentChat?.Id != chatId)
            return;

        HasUsedBrowser = _chatBrowserServices.ContainsKey(chatId);
        if (!IsBrowserOpen)
            return;

        if (session.Previous is not null && session.WasOpen)
            BrowserShowRequested?.Invoke(chatId);
        else
        {
            IsBrowserOpen = false;
            BrowserHideRequested?.Invoke();
        }
    }

    internal Task ReleaseJobBrowserSessionAsync(Guid chatId)
    {
        DetachJobBrowserSession(chatId);
        return _jobBrowserSessions.Remove(chatId, out var session)
            ? DisposeJobBrowserAsync(chatId, session.Browser)
            : Task.CompletedTask;
    }

    private static async Task DisposeJobBrowserAsync(Guid chatId, BrowserService browser)
    {
        try
        {
            // Dispose the coordinator, not its tabs one by one: closing the last tab normally
            // creates a replacement tab. Disposal instead releases every native controller.
            await browser.DisposeAsync();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"[JobBrowser] Could not release browser for chat {chatId}: {ex}");
        }
    }
}
