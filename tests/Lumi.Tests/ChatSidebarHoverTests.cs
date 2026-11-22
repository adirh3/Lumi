using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ChatSidebarHoverTests
{
    [Theory]
    [InlineData("en", "Latest reply", "No assistant reply yet.", "Waiting for you")]
    [InlineData("he", "התשובה האחרונה", "עדיין אין תשובה מהעוזר.", "ממתין לך")]
    public async Task HoverPreviewsOnlyThePointedChatWithoutOpeningIt(
        string language, string latestReply, string noReply, string waiting)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load(language);
            var answered = new Chat
            {
                Title = "Deploy the blog",
                HasUnreadMessages = true,
                Messages =
                [
                    new() { Role = "user", Content = "Can you check this?" },
                    new() { Role = "assistant", Content = "**DNS switched.** Waiting for the certificate." },
                ],
            };
            var unanswered = new Chat
            {
                Title = "Investigate the warning",
                Messages = [new() { Role = "user", Content = "Is this warning new?" }],
            };
            var data = new AppData
            {
                Settings = new UserSettings { IsOnboarded = true, AutoSaveChats = false, ShowAnimations = false },
                Chats = [answered, unanswered],
            };
            using var vm = new MainViewModel(new DataStore(data), TestCopilot.Shared, new UpdateService(),
                startBackgroundJobs: false, initializeCopilotOnStartup: false);
            var window = new MainWindow
            {
                DataContext = vm,
                Width = 1100,
                Height = 820,
                FlowDirection = Loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            };

            window.Show();
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                window.UpdateLayout();
                var activeChat = vm.ChatVM.CurrentChat;
                var preview = window.FindControl<Popup>("ChatHoverPreview")!;
                var card = window.FindControl<Border>("ChatHoverCard")!;
                var excerpt = window.FindControl<TextBlock>("ChatHoverReplyText")!;
                var status = window.FindControl<TextBlock>("ChatHoverStatus")!;

                ListBoxItem RowFor(Chat chat) => window.GetVisualDescendants().OfType<ListBoxItem>()
                    .Single(item => ReferenceEquals(item.DataContext, chat));

                Point RowPoint(Chat chat)
                {
                    var row = RowFor(chat);
                    return row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
                }

                async Task WaitFor(Func<bool> condition)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    while (!condition())
                        await Task.Delay(20, timeout.Token);
                }

                async Task RenderCardAsync()
                {
                    for (var frame = 0; frame < 2; frame++)
                    {
                        using var bitmap = window.CaptureRenderedFrame();
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    }
                }

                window.MouseMove(RowPoint(answered));
                Assert.False(preview.IsOpen);
                window.MouseMove(RowPoint(unanswered));
                await WaitFor(() => preview.IsOpen);
                Assert.Same(unanswered, preview.PlacementTarget!.DataContext);
                Assert.Equal(noReply, excerpt.Text);
                await RenderCardAsync();

                window.MouseMove(RowPoint(answered));
                Assert.False(preview.IsOpen);
                Assert.True(card.IsAttachedToVisualTree(), "The previous preview has started fading.");
                await WaitFor(() => preview.IsOpen);
                Assert.Same(answered, preview.PlacementTarget!.DataContext);
                Assert.Equal("DNS switched. Waiting for the certificate.", excerpt.Text);
                Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == latestReply);
                Assert.False(card.IsHitTestVisible);
                Assert.Same(activeChat, vm.ChatVM.CurrentChat);
                Assert.True(answered.HasUnreadMessages);

                answered.IsAwaitingInput = true;
                Assert.Equal(waiting, status.Text);
                answered.IsAwaitingInput = false;
                answered.IsRunning = true;
                Assert.Equal("DNS switched. Waiting for the certificate.", excerpt.Text);
                answered.Messages.Add(new ChatMessage { Role = "assistant", Content = "The certificate is ready." });
                answered.IsRunning = false;
                await WaitFor(() => excerpt.Text == "The certificate is ready.");
                Assert.True(preview.IsOpen);
                Assert.False(status.IsVisible);

                var anchor = Assert.IsAssignableFrom<Control>(preview.PlacementTarget);
                await RenderCardAsync();
                var position = card.PointToScreen(default);

                window.MouseMove(new Point(window.Bounds.Width / 2, 60));
                Assert.False(preview.IsOpen);
                Assert.Same(anchor, preview.PlacementTarget);
                Assert.Equal("The certificate is ready.", excerpt.Text);
                using (window.CaptureRenderedFrame())
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Assert.True(card.IsAttachedToVisualTree(), "The position is checked during the exit animation.");
                Assert.Equal(position, card.PointToScreen(default));

                // Reproduce a new hover being displayed before the previous host has finished fading.
                var replacement = RowFor(unanswered);
                excerpt.Text = noReply;
                preview.PlacementTarget = replacement;
                Assert.False(card.IsAttachedToVisualTree());
                preview.IsOpen = true;
                await RenderCardAsync();
                Assert.Same(replacement, preview.PlacementTarget);
                Assert.Equal(noReply, excerpt.Text);

                preview.IsOpen = false;
                await WaitFor(() => !card.IsAttachedToVisualTree() && preview.PlacementTarget is null);
                Assert.Null(excerpt.Text);
            }
            finally
            {
                window.Close();
                Loc.Load("en");
            }
        }, CancellationToken.None);
    }
}
