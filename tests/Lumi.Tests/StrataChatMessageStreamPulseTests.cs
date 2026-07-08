using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Regression guards for the streaming-pulse composition animation on <see cref="StrataChatMessage"/>.
///
/// A streaming message engages a <c>Forever</c> opacity "pulse" animation on its stream-bar composition
/// visual. If that animation is left running when the message detaches — or is (re)started on an
/// already-detached control by the dispatcher-posted <c>StartStreamPulse</c> racing with transcript
/// virtualization — it becomes an orphan that keeps ticking on the render thread indefinitely (no parent
/// re-sync ever stops an off-tree visual's animation). Over a long streaming session those orphans
/// accumulate and starve the compositor: animations break, the navigation menu stops compositing, and
/// the whole app slows down. These tests pin the fix: the pulse stops on detach and never engages while
/// detached.
/// </summary>
[Collection("Headless UI")]
public sealed class StrataChatMessageStreamPulseTests
{
    [Fact]
    public async Task StreamPulse_StopsWhenStreamingMessageDetaches()
    {
        using var session = HeadlessTestSession.Start();

        var activeWhileAttached = false;
        var activeAfterDetach = false;

        await session.Dispatch(async () =>
        {
            var message = new StrataChatMessage
            {
                Role = StrataChatRole.Assistant,
                IsStreaming = true,
                Content = "streaming response...",
            };

            var panel = new StackPanel();
            panel.Children.Add(message);
            var window = new Window { Width = 480, Height = 320, Content = panel };

            window.Show();
            await PumpAsync(); // attach + apply template + run the posted StartStreamPulse
            await PumpAsync();

            activeWhileAttached = message.IsStreamPulseActiveForTest;

            // Detach the still-streaming message (the transcript-virtualization equivalent).
            panel.Children.Remove(message);
            await PumpAsync();

            activeAfterDetach = message.IsStreamPulseActiveForTest;

            window.Close();
        }, CancellationToken.None);

        Assert.True(activeWhileAttached, "stream pulse should be engaged while streaming and attached");
        Assert.False(activeAfterDetach, "stream pulse must stop when the streaming message detaches");
    }

    [Fact]
    public async Task StreamPulse_DoesNotEngageWhileDetached()
    {
        using var session = HeadlessTestSession.Start();

        var activeWhileAttached = false;
        var activeAfterDetachedRestart = false;

        await session.Dispatch(async () =>
        {
            var message = new StrataChatMessage
            {
                Role = StrataChatRole.Assistant,
                IsStreaming = true,
                Content = "streaming response...",
            };

            var panel = new StackPanel();
            panel.Children.Add(message);
            var window = new Window { Width = 480, Height = 320, Content = panel };

            window.Show();
            await PumpAsync(); // attach + template -> _streamBar populated, pulse engaged
            await PumpAsync();

            activeWhileAttached = message.IsStreamPulseActiveForTest;

            // Detach: OnDetachedFromVisualTree stops the pulse and clears the attachment flag.
            panel.Children.Remove(message);
            await PumpAsync();

            // Simulate the posted/asynchronous StartStreamPulse racing after detach, exercised through the
            // real public API: a streaming-state re-toggle on the now-detached (but still templated) control
            // routes through OnStreamingChanged -> StartStreamPulse. The attachment guard must refuse to
            // engage a Forever animation on the off-tree visual.
            message.IsStreaming = false;
            message.IsStreaming = true;
            await PumpAsync();

            activeAfterDetachedRestart = message.IsStreamPulseActiveForTest;

            window.Close();
        }, CancellationToken.None);

        Assert.True(activeWhileAttached, "stream pulse should be engaged while streaming and attached");
        Assert.False(activeAfterDetachedRestart, "StartStreamPulse must not engage a Forever animation while detached");
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
