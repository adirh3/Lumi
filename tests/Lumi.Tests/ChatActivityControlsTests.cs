using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using GitHub.Copilot.Rpc;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

#pragma warning disable GHCP001 // The SDK exposes task snapshots through its typed experimental API.

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ChatActivityControlsTests
{
    [Theory]
    [InlineData(600)]
    [InlineData(1000)]
    public async Task BackgroundActivity_IsCompactBesideContext_AndExpandsOutsideComposer(double width)
    {
        var session = HeadlessTestSession.Start();
        try
        {
            await session.Dispatch(() =>
            {
                Loc.Load("en");
                var chat = new Chat
                {
                    IsSessionActive = true,
                    Messages = [new ChatMessage { Role = "assistant", Content = "Ready." }]
                };
                var data = new AppData
                {
                    Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false },
                    Chats = [chat]
                };
                using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared) { CurrentChat = chat };
                var view = new ChatView { DataContext = vm };
                var window = new Window { Width = width, Height = 700, Content = view };
                window.Show();
                try
                {
                    Dispatcher.UIThread.RunJobs();
                    var activity = view.FindControl<Button>("BackgroundActivityButton")!;
                    var context = view.FindControl<Button>("ContextWindowButton")!;
                    var composer = view.FindControl<StrataChatComposer>("Composer")!;
                    var flyout = Assert.IsType<Flyout>(activity.Flyout);
                    var details = Assert.IsType<StackPanel>(flyout.Content);

                    Assert.True(activity.IsVisible);
                    Assert.False(flyout.IsOpen);
                    Assert.Equal(22, activity.Bounds.Height);
                    Assert.InRange(activity.Bounds.Width, 30, 160);
                    Assert.Same(context.GetLogicalParent(), activity.GetLogicalParent());
                    Assert.DoesNotContain(activity, composer.GetLogicalDescendants());
                    Assert.False(Assert.IsType<Grid>(composer.StatusContent).IsVisible);

                    var activityPosition = activity.TranslatePoint(default, view)!.Value;
                    var contextPosition = context.TranslatePoint(default, view)!.Value;
                    var composerPosition = composer.TranslatePoint(default, view)!.Value;
                    Assert.InRange(contextPosition.X - activityPosition.X - activity.Bounds.Width, 5, 7);
                    Assert.Equal(contextPosition.Y, activityPosition.Y);
                    Assert.True(activityPosition.Y + activity.Bounds.Height <= composerPosition.Y);

                    flyout.ShowAt(activity);
                    Dispatcher.UIThread.RunJobs();

                    Assert.True(flyout.IsOpen);
                    Assert.Same(vm, details.DataContext);
                    var stop = details.GetLogicalDescendants().OfType<Button>()
                        .Single(button => button.Name == "StopBackgroundSession");
                    Assert.Same(vm.StopGenerationCommand, stop.Command);

                    vm.ApplyBackgroundActivitySnapshot(
                    [
                        new TaskInfoShell
                        {
                            Id = "preview",
                            Description = "Local app preview",
                            Command = "dotnet run",
                            Status = GitHub.Copilot.Rpc.TaskStatus.Running,
                            ExecutionMode = TaskExecutionMode.Background,
                            AttachmentMode = TaskShellInfoAttachmentMode.Attached,
                            StartedAt = DateTimeOffset.UtcNow
                        }
                    ], DateTimeOffset.UtcNow);
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(flyout.IsOpen);
                    var list = details.GetLogicalDescendants().OfType<ItemsControl>()
                        .Single(control => control.Name == "RunningSessionActivities");
                    Assert.Same(vm.RunningSessionActivities, list.ItemsSource);
                    var count = activity.GetLogicalDescendants().OfType<TextBlock>()
                        .Single(control => control.Name == "RunningSessionActivityCount");
                    Assert.Equal("1", count.Text);
                    Assert.True(count.IsVisible);

                    vm.IsBusy = true;
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(activity.IsVisible);
                    Assert.False(flyout.IsOpen);

                    vm.IsBusy = false;
                    flyout.ShowAt(activity);
                    Dispatcher.UIThread.RunJobs();
                    vm.CurrentChat = new Chat { IsSessionActive = true };
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(flyout.IsOpen);
                    Assert.Empty(vm.RunningSessionActivities);
                    Assert.False(count.IsVisible);

                    flyout.ShowAt(activity);
                    Dispatcher.UIThread.RunJobs();
                    vm.IsSessionActive = false;
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(activity.IsVisible);
                    Assert.False(flyout.IsOpen);
                }
                finally
                {
                    window.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Run(session.Dispose);
        }
    }

    [Fact]
    public void BackgroundActivity_KeepsTheStripAvailableWithoutContextUsage()
    {
        var data = new AppData
        {
            Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false }
        };
        using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        vm.IsSessionActive = true;
        Assert.False(vm.HasTokenUsage);
        Assert.True(vm.ShowInfoStrip);
        Assert.Contains(nameof(ChatViewModel.ShowInfoStrip), notifications);

        notifications.Clear();
        vm.IsBusy = true;
        Assert.False(vm.ShowInfoStrip);
        Assert.Contains(nameof(ChatViewModel.ShowInfoStrip), notifications);

        vm.IsBusy = false;
        Assert.True(vm.ShowInfoStrip);
        vm.IsSessionActive = false;
        Assert.False(vm.ShowInfoStrip);
    }
}
