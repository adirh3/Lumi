using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Guards the gesture split introduced when file chips and workspace rows became native drag
/// sources (<see cref="FileDrag"/>): a press that travels turns into a file drag and must not also
/// fire the chip's open/diff action, while a plain click must keep working exactly as before.
/// </summary>
[Collection("Headless UI")]
public sealed class FileDragTests : IDisposable
{
    private static readonly string TempRoot =
        Path.Combine(Path.GetTempPath(), "lumi-filedrag-tests");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(TempRoot))
                Directory.Delete(TempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ClickOnFileChip_StillRaisesOpenRequested()
    {
        using var session = HeadlessTestSession.Start();

        var opened = 0;

        await session.Dispatch(async () =>
        {
            var (window, chip) = BuildChipWindow(count => opened = count);
            await PumpAsync();

            var center = Center(window, chip);
            window.MouseDown(center, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(center, MouseButton.Left, RawInputModifiers.None);
            await PumpAsync();

            window.Close();
        }, CancellationToken.None);

        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task DraggingFileChip_DoesNotRaiseOpenRequested()
    {
        using var session = HeadlessTestSession.Start();

        var opened = 0;

        await session.Dispatch(async () =>
        {
            var (window, chip) = BuildChipWindow(count => opened = count);
            await PumpAsync();

            var start = Center(window, chip);
            window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
            for (var step = 1; step <= 6; step++)
            {
                window.MouseMove(start + new Point(step * 20, step * 12), RawInputModifiers.LeftMouseButton);
                await PumpAsync();
            }

            window.MouseUp(start + new Point(120, 72), MouseButton.Left, RawInputModifiers.None);
            await PumpAsync();

            window.Close();
        }, CancellationToken.None);

        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task DragEnabledRow_StillInvokesItsClickAction()
    {
        using var session = HeadlessTestSession.Start();

        var clicks = 0;

        await session.Dispatch(async () =>
        {
            var (window, row) = BuildRowWindow(() => clicks++);
            await PumpAsync();

            var center = Center(window, row);
            window.MouseDown(center, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(center, MouseButton.Left, RawInputModifiers.None);
            await PumpAsync();

            window.Close();
        }, CancellationToken.None);

        Assert.Equal(1, clicks);
    }

    /// <summary>
    /// Headless has no platform drag source, so crossing the threshold must fail softly instead of
    /// surfacing an unhandled exception from the fire-and-forget drag task.
    /// </summary>
    [Fact]
    public async Task DraggingRow_WithoutPlatformDragSource_DoesNotThrow()
    {
        using var session = HeadlessTestSession.Start();

        Exception? unobserved = null;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            unobserved ??= e.Exception;
            e.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await session.Dispatch(async () =>
            {
                var (window, row) = BuildRowWindow(() => { });
                await PumpAsync();

                var start = Center(window, row);
                window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
                for (var step = 1; step <= 6; step++)
                {
                    window.MouseMove(start + new Point(step * 20, step * 12), RawInputModifiers.LeftMouseButton);
                    await PumpAsync();
                }

                window.MouseUp(start + new Point(120, 72), MouseButton.Left, RawInputModifiers.None);
                await PumpAsync();

                window.Close();
            }, CancellationToken.None);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        Assert.Null(unobserved);
    }

    private static (Window Window, StrataFileAttachment Chip) BuildChipWindow(Action<int> onOpened)
    {
        var opened = 0;
        var chip = new StrataFileAttachment
        {
            FileName = "Deal-Summary.md",
            FileSize = "103 B",
            IsRemovable = false,
            Width = 200,
            Height = 50
        };
        FileDrag.SetFilePath(chip, CreateTempFile());
        chip.OpenRequested += (_, _) => onOpened(++opened);

        var window = new Window { Width = 640, Height = 480, Content = chip };
        window.Show();
        return (window, chip);
    }

    private static (Window Window, Button Row) BuildRowWindow(Action onClick)
    {
        var row = new Button { Content = "TV-Shortlist.csv", Width = 220, Height = 44 };
        FileDrag.SetFilePath(row, CreateTempFile());
        row.Click += (_, _) => onClick();

        var window = new Window { Width = 640, Height = 480, Content = row };
        window.Show();
        return (window, row);
    }

    private static string CreateTempFile()
    {
        Directory.CreateDirectory(TempRoot);
        var path = Path.Combine(TempRoot, $"lumi-filedrag-{Guid.NewGuid():N}.md");
        File.WriteAllText(path, "fixture");
        return path;
    }

    private static Point Center(Window window, Control control)
    {
        var topLeft = control.TranslatePoint(new Point(0, 0), window)
            ?? throw new InvalidOperationException("Control is not attached to the test window.");
        return topLeft + new Point(control.Bounds.Width / 2, control.Bounds.Height / 2);
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
