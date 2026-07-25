using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Guards the "new model shows up without restarting Lumi" behaviour at the control level: opening
/// the picker must ask the host to refresh the catalog, and a model appended to the bound collection
/// must appear immediately — including while the popup is already open.
/// </summary>
[Collection("Headless UI")]
public sealed class StrataModelPickerRefreshTests
{
    [Fact]
    public async Task OpeningPicker_RunsPickerOpenedCommand()
    {
        using var session = HeadlessTestSession.Start();

        var invocations = 0;

        // Assertions are captured inside the dispatched body and checked outside it, because
        // HeadlessUnitTestSession.Dispatch swallows exceptions thrown in an async body.
        await session.Dispatch(async () =>
        {
            var picker = new StrataModelPicker
            {
                Models = new ObservableCollection<string> { "gpt-5.4" },
                SelectedModel = "gpt-5.4",
                PickerOpenedCommand = new DelegateCommand(() => invocations++)
            };

            var window = new Window { Width = 420, Height = 320, Content = picker };
            window.Show();
            await PumpAsync();

            ClickPickerButton(window, picker);
            await PumpAsync();

            window.Close();
        }, CancellationToken.None);

        Assert.Equal(1, invocations);
    }

    [Fact]
    public async Task ModelAddedWhilePopupOpen_AppearsWithoutReopening()
    {
        using var session = HeadlessTestSession.Start();

        var rowsBefore = Array.Empty<string>();
        var rowsAfter = Array.Empty<string>();

        await session.Dispatch(async () =>
        {
            var models = new ObservableCollection<string> { "gpt-5.4" };
            var picker = new StrataModelPicker
            {
                Models = models,
                SelectedModel = "gpt-5.4"
            };

            var window = new Window { Width = 420, Height = 320, Content = picker };
            window.Show();
            await PumpAsync();

            ClickPickerButton(window, picker);
            await PumpAsync();
            rowsBefore = ReadModelRows(picker);

            // Simulates the catalog refresh discovering a newly released model.
            models.Add("claude-opus-5");
            await PumpAsync();
            rowsAfter = ReadModelRows(picker);

            window.Close();
        }, CancellationToken.None);

        Assert.Equal(["gpt-5.4"], rowsBefore);
        Assert.Contains("claude-opus-5", rowsAfter);
        Assert.Contains("gpt-5.4", rowsAfter);
    }

    private static string[] ReadModelRows(StrataModelPicker picker)
    {
        var list = FindPart<StackPanel>(picker, "PART_ModelPickerList");
        return list is null
            ? []
            : list.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(text => text.Classes.Contains("model-name"))
                .Select(text => text.Text ?? string.Empty)
                .ToArray();
    }

    private static void ClickPickerButton(Window window, StrataModelPicker picker)
    {
        var button = FindPart<Button>(picker, "PART_ModelPickerButton")
            ?? throw new InvalidOperationException("PART_ModelPickerButton was not found.");

        var topLeft = button.TranslatePoint(new Point(0, 0), window)
            ?? throw new InvalidOperationException("PART_ModelPickerButton is not attached to the test window.");
        var point = topLeft + new Point(button.Bounds.Width / 2, button.Bounds.Height / 2);

        window.MouseDown(point, Avalonia.Input.MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, Avalonia.Input.MouseButton.Left, RawInputModifiers.None);
    }

    private static T? FindPart<T>(StrataModelPicker picker, string name) where T : Control
    {
        // Once opened the popup content lives in its own visual root, so the popup child is searched
        // separately from the picker's own visual descendants.
        var candidates = picker.GetVisualDescendants()
            .OfType<T>()
            .Concat(picker.GetVisualDescendants()
                .OfType<Popup>()
                .Where(popup => popup.IsOpen)
                .SelectMany(popup => popup.Child?.GetSelfAndVisualDescendants() ?? [])
                .OfType<T>())
            .ToList();

        return candidates.FirstOrDefault(control => control.Name == name)
            ?? candidates.FirstOrDefault();
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private sealed class DelegateCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }
}
