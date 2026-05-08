using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrataTheme.Controls;
using Xunit;
using ICommand = System.Windows.Input.ICommand;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class StrataChatComposerSendTests
{
    [Fact]
    public async Task SendButton_SendsOnFirstPhysicalMouseClick()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var command = new CaptureCommand();
            var sendRaised = 0;
            var composer = new StrataChatComposer
            {
                SendCommand = command,
            };
            composer.SendRequested += (_, _) => sendRaised++;

            var window = new Window
            {
                Width = 360,
                Height = 180,
                Content = composer,
            };

            window.Show();
            await PumpAsync();

            var input = composer.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(control => control.Name == "PART_Input");
            var sendButton = composer.GetVisualDescendants()
                .OfType<Button>()
                .Single(control => control.Name == "PART_SendButton");

            input.Text = "send from mounted input";
            Assert.Equal("send from mounted input", composer.PromptText);

            Click(window, sendButton);
            await PumpAsync();

            Assert.Equal(1, sendRaised);
            Assert.Equal("send from mounted input", command.Parameter);

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SendButton_SendsOnFirstPhysicalMouseClick_WhenAutocompleteIsOpen()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var command = new CaptureCommand();
            var sendRaised = 0;
            var composer = new StrataChatComposer
            {
                AvailableAgents = new[]
                {
                    new StrataComposerChip("Agent One"),
                },
                SendCommand = command,
            };
            composer.SendRequested += (_, _) => sendRaised++;

            var window = new Window
            {
                Width = 520,
                Height = 260,
                Content = composer,
            };

            window.Show();
            await PumpAsync();

            var input = composer.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(control => control.Name == "PART_Input");
            var sendButton = composer.GetVisualDescendants()
                .OfType<Button>()
                .Single(control => control.Name == "PART_SendButton");

            input.Text = "hello @agent";
            input.CaretIndex = input.Text.Length;
            await PumpAsync();

            Click(window, sendButton);
            await PumpAsync();

            Assert.Equal(1, sendRaised);
            Assert.Equal("hello @agent", command.Parameter);

            window.Close();
        }, CancellationToken.None);
    }

    private static void Click(Window window, Visual target)
    {
        var topLeft = target.TranslatePoint(new Point(0, 0), window)
            ?? throw new InvalidOperationException("Target is not attached to the test window.");
        var point = topLeft + new Point(target.Bounds.Width / 2, target.Bounds.Height / 2);

        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private sealed class CaptureCommand : ICommand
    {
        public object? Parameter { get; private set; }

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            Parameter = parameter;
        }
    }
}
