#if DEBUG
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using Lumi.ViewModels;

namespace Lumi.UiPerf;

/// <summary>
/// Spins up a set of concurrently "running" chats — each genuinely streaming through Lumi's real
/// streaming primitives — so the harness can measure UX responsiveness under the load a real user
/// experiences when several agents are working at once. This reproduces what a static two-chat
/// switch test cannot: N background streaming flushes per second hammering the UI thread, N animated
/// sidebar busy indicators, chat-list re-sorting on activity, and the displayed chat re-rendering its
/// live markdown — all competing with whatever action is being measured.
/// </summary>
internal sealed class UiStreamingLoad
{
    private readonly MainViewModel _mainVm;
    private readonly List<ChatViewModel> _liveSurfaces = new();

    public UiStreamingLoad(MainViewModel mainVm) => _mainVm = mainVm;

    public int Count => _liveSurfaces.Count;

    /// <summary>
    /// Opens each chat (so we capture its real, store-managed surface) and starts a simulated stream
    /// on it. Each surface stays pinned/live for the duration; only the currently-displayed one does
    /// UI render work on flush, exactly like production.
    /// </summary>
    public async Task StartAsync(IReadOnlyList<Guid> liveChatIds)
    {
        foreach (var chatId in liveChatIds)
        {
            await OnUiAsync(async () =>
            {
                await _mainVm.OpenChatByIdAsync(chatId);
                var surface = _mainVm.ChatVM;
                surface.DebugStartSimulatedStreaming(() => ReferenceEquals(_mainVm.ChatVM, surface));
                _liveSurfaces.Add(surface);
            });

            // Let the first throttled flush land while this chat is still displayed, so its streaming
            // turn is built into the transcript (a later switch back then re-realizes it, as in real use).
            await Task.Delay(90);
        }
    }

    public async Task StopAsync()
    {
        foreach (var surface in _liveSurfaces)
            await OnUiAsync(() => surface.DebugStopSimulatedStreaming());
        _liveSurfaces.Clear();
    }

    private static async Task OnUiAsync(Func<Task> func)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            await func().ConfigureAwait(true);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(func);
    }

    private static Task OnUiAsync(Action action)
        => OnUiAsync(() => { action(); return Task.CompletedTask; });
}
#endif
