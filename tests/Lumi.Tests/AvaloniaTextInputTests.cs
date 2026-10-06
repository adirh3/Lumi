using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class AvaloniaTextInputTests
{
    [Theory]
    [InlineData(8, 18, "short", "shortx")]
    [InlineData(18, 8, "short", "shortx")]
    [InlineData(3, 18, "short", "shox")]
    [InlineData(8, 18, "", "x")]
    public async Task TextReplacement_NotifiesInputMethodOnlyAfterSelectionIsValid(
        int selectionStart, int selectionEnd, string replacement, string expected)
    {
        using var session = HeadlessTestSession.Start(typeof(AvaloniaTextInputTestApp));
        await session.Dispatch(async () =>
        {
            var input = new TextBox { Text = "abcdefghijklmnopqrst" };
            var window = new Window { Width = 360, Height = 180, Content = input };
            window.Show();
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                input.Focus();
                input.CaretIndex = 0;

                var request = new TextInputMethodClientRequestedEventArgs
                {
                    RoutedEvent = InputElement.TextInputMethodClientRequestedEvent
                };
                input.RaiseEvent(request);
                var client = Assert.IsAssignableFrom<TextInputMethodClient>(request.Client);
                client.Selection = new TextSelection(selectionStart, selectionEnd);

                var inputSent = false;
                var invalidSelectionObserved = false;
                client.SelectionChanged += (_, _) =>
                {
                    var text = client.SurroundingText;
                    var selection = client.Selection;
                    invalidSelectionObserved |= Math.Min(selection.Start, selection.End) < 0
                        || Math.Max(selection.Start, selection.End) > text.Length;
                    if (inputSent)
                        return;

                    inputSent = true;
                    input.RaiseEvent(new TextInputEventArgs
                    {
                        RoutedEvent = InputElement.TextInputEvent,
                        Text = "x"
                    });
                };

                input.Text = replacement;

                Assert.True(inputSent);
                Assert.False(invalidSelectionObserved);
                Assert.Equal(expected, input.Text);
                Assert.InRange(input.CaretIndex, 0, expected.Length);
                Assert.InRange(input.SelectionStart, 0, expected.Length);
                Assert.InRange(input.SelectionEnd, 0, expected.Length);

                input.Undo();
                Assert.Equal(replacement, input.Text);
                input.Redo();
                Assert.Equal(expected, input.Text);
                Assert.False(invalidSelectionObserved);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }
}

public sealed class AvaloniaTextInputTestApp : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<AvaloniaTextInputTestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
