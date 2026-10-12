using System.Reflection;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using StrataTheme.Animation;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Mobile.Tests;

[Collection("Headless mobile UI")]
public sealed class BatteryAnimationTests
{
    [Fact]
    public async Task MobilePresenceKeepsEveryStateFreeOfContinuousDecorativeMotion()
    {
        using var session = HeadlessMobileSession.Start();
        await session.Dispatch(async () =>
        {
            await using var model = new MobileShellViewModel(store: session.NewStore(), post: Post);
            model.IsPaired = model.IsConnected = model.IsHostReady = true;
            var view = new MobileShellView { DataContext = model };
            var window = new Window { Width = 412, Height = 892, Content = view };
            try
            {
                window.Show();
                Pump(window);
                var presence = view.GetVisualDescendants().OfType<StrataPresence>().Single();
                Assert.False(presence.AnimateWhileWorking);
                AssertNoLoop(PresenceState.Dormant);
                model.Chat.Reset(Guid.NewGuid(), "Battery check");
                AssertNoLoop(PresenceState.Idle);
                model.Chat.IsBusy = true;
                AssertNoLoop(PresenceState.Thinking);
                model.Chat.IsStreaming = true;
                AssertNoLoop(PresenceState.Streaming);
                model.Chat.IsStreaming = model.Chat.IsBusy = false;
                AssertNoLoop(PresenceState.Idle);
                var question = new QuestionItemViewModel(new RemoteTranscriptItem
                {
                    Id = "question", Kind = RemoteProtocol.ItemKinds.Question, Text = "Ready?"
                });
                var turn = new TranscriptTurnViewModel("question-turn");
                turn.Items.Add(question);
                model.Chat.Turns.Add(turn);
                AssertNoLoop(PresenceState.Attention);
                question.IsAnswered = true;
                AssertNoLoop(PresenceState.Idle);

                void AssertNoLoop(PresenceState expected)
                {
                    Pump(window);
                    Assert.Equal(expected, presence.State);
                    Assert.False(presence.HasContinuousAnimationForTest);
                }
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TypingIndicatorStopsWhenItsAncestorIsHiddenAndResumesOnlyWhileActive()
    {
        using var session = HeadlessMobileSession.Start();
        await session.Dispatch(() =>
        {
            var typing = new StrataTypingIndicator { IsActive = true };
            var host = new Border { Child = typing };
            var window = new Window { Width = 320, Height = 200, Content = host };
            try
            {
                window.Show();
                Pump(window);
                var timer = Private<DispatcherTimer>(typing, "_pulseTimer");
                Assert.True(timer.IsEnabled);
                host.IsVisible = false;
                Pump(window);
                Assert.False(timer.IsEnabled);
                host.IsVisible = true;
                Pump(window);
                Assert.True(timer.IsEnabled);
                LifecycleOpacityPulse.SetIsPaused(host, true);
                Pump(window);
                Assert.True(typing.IsActive);
                Assert.True(typing.IsVisible);
                Assert.False(timer.IsEnabled);
                LifecycleOpacityPulse.SetIsPaused(host, false);
                Pump(window);
                Assert.True(timer.IsEnabled);
                typing.IsActive = false;
                Pump(window);
                Assert.False(timer.IsEnabled);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CachedChatActivityPausesOffPageAndInTheBackgroundWithoutLosingTheDraft()
    {
        using var session = HeadlessMobileSession.Start();
        await session.Dispatch(async () =>
        {
            await using var model = new MobileShellViewModel(store: session.NewStore(), post: Post);
            model.IsPaired = model.IsConnected = model.IsHostReady = true;
            model.Chat.Reset(Guid.NewGuid(), "Lifecycle check");
            var view = new MobileShellView { DataContext = model };
            var window = new Window { Width = 412, Height = 892, Content = view };
            try
            {
                window.Show();
                Pump(window);
                var chat = view.GetVisualDescendants().OfType<ChatDetailView>().Single();
                var controller = Private<MobilePresenceController>(chat, "_presence");
                var typing = chat.GetVisualDescendants().OfType<StrataTypingIndicator>()
                    .Single(control => control.Name == "ChatTyping");
                model.Chat.PromptText = "Keep this unsent draft";
                model.Chat.IsBusy = true;
                Pump(window);
                var follow = Private<DispatcherTimer>(controller, "_followTimer");
                var typingTimer = Private<DispatcherTimer>(typing, "_pulseTimer");
                Assert.True(follow.IsEnabled);
                Assert.True(typingTimer.IsEnabled);
                model.ShowPageCommand.Execute("Settings");
                Pump(window);
                Assert.False(chat.IsVisible);
                Assert.False(follow.IsEnabled);
                Assert.False(typingTimer.IsEnabled);
                model.GoBackCommand.Execute(null);
                Pump(window);
                Assert.Same(chat, view.GetVisualDescendants().OfType<ChatDetailView>().Single());
                Assert.True(follow.IsEnabled);
                Assert.True(typingTimer.IsEnabled);

                view.NotifyApplicationDeactivated();
                Pump(window);
                Assert.True(LifecycleOpacityPulse.GetIsPaused(typing));
                Assert.False(follow.IsEnabled);
                Assert.False(typingTimer.IsEnabled);
                Assert.Equal("Keep this unsent draft", model.Chat.PromptText);
                view.NotifyApplicationActivated();
                Pump(window);
                Assert.False(LifecycleOpacityPulse.GetIsPaused(typing));
                Assert.True(typingTimer.IsEnabled);
                Assert.Equal("Keep this unsent draft", model.Chat.PromptText);

                model.Chat.IsBusy = false;
                await Task.Delay(1700);
                Pump(window);
                Assert.False(follow.IsEnabled);
                Assert.False(typingTimer.IsEnabled);
                Assert.False(controller.Visual.HasContinuousAnimationForTest);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task StreamingAndLoadingVisualsHonorTheInheritedPauseWithoutEndingTheWork()
    {
        using var session = HeadlessMobileSession.Start();
        await session.Dispatch(() =>
        {
            var message = new StrataChatMessage { IsStreaming = true };
            var progress = new ProgressBar { IsIndeterminate = true, Width = 200 };
            var pulse = new Border { Width = 10, Height = 10 };
            LifecycleOpacityPulse.SetIsActive(pulse, true);
            var host = new StackPanel { Children = { message, progress, pulse } };
            var window = new Window { Width = 320, Height = 240, Content = host };
            try
            {
                window.Show();
                Pump(window);
                var streamBar = message.GetVisualDescendants().OfType<Border>()
                    .Single(control => control.Name == "PART_StreamBar");
                var indicator = progress.GetVisualDescendants().OfType<Border>()
                    .Single(control => control.Name == "PART_Indicator");
                AssertRunning(true);
                LifecycleOpacityPulse.SetIsPaused(host, true);
                Pump(window);
                Assert.True(message.IsStreaming);
                Assert.True(progress.IsIndeterminate);
                AssertRunning(false);
                LifecycleOpacityPulse.SetIsPaused(host, false);
                Pump(window);
                AssertRunning(true);
                host.IsVisible = false;
                Pump(window);
                AssertRunning(false);

                void AssertRunning(bool running)
                {
                    Assert.Equal(running, LifecycleOpacityPulse.IsRunning(streamBar));
                    Assert.Equal(running, LifecycleOpacityPulse.IsRunning(pulse));
                    Assert.Equal(running, LifecycleOffsetSweep.IsRunning(indicator));
                }
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public void MobileStylesDoNotDeclareUnboundedAnimations()
    {
        var source = FindMobileSource();
        XNamespace avalonia = "https://github.com/avaloniaui";
        var violations = Directory.EnumerateFiles(source, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(path => XDocument.Load(path).Descendants(avalonia + "Animation")
                .Where(animation => string.Equals(
                    (string?)animation.Attribute("IterationCount"), "Infinite", StringComparison.OrdinalIgnoreCase))
                .Select(_ => Path.GetRelativePath(source, path))).ToArray();
        Assert.Empty(violations);
    }

    private static T Private<T>(object owner, string name) where T : class
    {
        var field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(owner));
    }

    private static string FindMobileSource()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var source = Path.Combine(directory.FullName, "src", "Lumi.Mobile");
            if (Directory.Exists(source))
                return source;
        }
        throw new DirectoryNotFoundException("Could not locate the Lumi.Mobile source directory.");
    }

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
    }
}
