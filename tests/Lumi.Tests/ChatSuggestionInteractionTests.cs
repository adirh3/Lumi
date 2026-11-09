using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ChatSuggestionInteractionTests
{
    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "An existing draft")]
    [InlineData(2, "Another draft")]
    public async Task RightClick_PutsTheFullSuggestionInTheComposerWithoutSending(int index, string draft)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var chat = new Chat
            {
                Messages = [new ChatMessage { Role = "assistant", Content = "Done." }]
            };
            using var vm = new ChatViewModel(new DataStore(new AppData
            {
                Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false },
                Chats = [chat]
            }), TestCopilot.Shared)
            {
                CurrentChat = chat,
                PromptText = draft,
                SuggestionA = "Walk me through exactly how the streaming token accumulator handles incremental UI updates during a very long assistant response",
                SuggestionB = "Summarize the root cause",
                SuggestionC = "Show the diff that fixed the freeze"
            };
            var view = new ChatView { DataContext = vm };
            var window = new Window { Width = 900, Height = 700, Content = view };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var row = view.FindControl<Panel>("SuggestionChipsRow")!;
                var buttons = row.Children.OfType<Button>().ToArray();
                var button = buttons[index];
                var suggestion = Assert.IsType<string>(button.CommandParameter);
                Assert.True(button.IsEffectivelyVisible);
                Assert.Same(vm.SelectSuggestionCommand, button.Command);
                var sendCount = 0;
                button.Command = new RelayCommand(() => sendCount++);

                Click(window, button, MouseButton.Right);
                Dispatcher.UIThread.RunJobs();

                var composer = view.FindControl<StrataChatComposer>("Composer")!;
                var input = composer.GetVisualDescendants().OfType<TextBox>()
                    .Single(control => control.Name == "PART_Input");
                Assert.Equal(suggestion, vm.PromptText);
                Assert.Equal(suggestion, input.Text);
                Assert.Same(input, window.FocusManager!.GetFocusedElement());
                Assert.Equal(suggestion.Length, input.CaretIndex);
                Assert.Equal(input.SelectionStart, input.SelectionEnd);
                Assert.Equal(0, sendCount);
                Assert.Single(chat.Messages);
                Assert.False(vm.IsBusy);
                Assert.True(vm.HasSuggestions);

                // The ordinary click still invokes the chip's existing send command.
                Click(window, button, MouseButton.Left);
                Assert.Equal(1, sendCount);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static void Click(Window window, Control control, MouseButton button)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, button);
        window.MouseUp(point, button);
    }
}
