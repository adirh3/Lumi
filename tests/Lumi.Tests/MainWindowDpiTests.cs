using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class MainWindowDpiTests
{
    [Fact]
    public async Task MaximizedWindowCanFitLaptopWorkAreaAfterScalingFrom100To300Percent()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var window = new MainWindow { Width = 1320, Height = 864 };
            window.Show();
            try
            {
                window.WindowState = WindowState.Maximized;
                window.Width = 2560;
                window.Height = 1392;
                await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Background);

                // A 2880 x 1800 panel at 300%, with 48 logical pixels reserved for the taskbar.
                var laptopWorkArea = new PixelSize(2880, 1656);
                window.SetRenderScaling(3);
                var available = laptopWorkArea.ToSize(window.RenderScaling);
                window.Width = available.Width;
                window.Height = available.Height;
                await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Background);

                Assert.Equal(WindowState.Maximized, window.WindowState);
                Assert.Equal(laptopWorkArea, PixelSize.FromSize(window.ClientSize, window.RenderScaling));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }
}
