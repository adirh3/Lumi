using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Lumi.Mobile.Behaviors;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class NativeTextInputOverlaySessionTests
{
    /// <summary>
    /// Every headless test session runs on its own UI thread (a later one may or may not reuse an
    /// earlier one's). A sheet another session opened belongs to that session's thread, so a session
    /// checking whether its own field is covered must not touch it. Doing so threw "a different
    /// thread owns it" at random: whenever a sheet an earlier test left open had not been collected
    /// yet and the next test ran on another thread. Keeping the first session alive guarantees both.
    /// </summary>
    [Fact]
    public void ASheetOpenInAnotherSessionDoesNotReachThisSessionsFields()
    {
        RuntimeHelpers.RunClassConstructor(typeof(NativeTextInputOverlay).TypeHandle);
        using var first = HeadlessMobileSession.Start();
        using var second = HeadlessMobileSession.Start();
        StrataBottomSheet? sheet = null;
        int? firstThread = null;
        first.Dispatch(() =>
        {
            firstThread = Environment.CurrentManagedThreadId;
            sheet = new StrataBottomSheet { IsOpen = true };
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
        Assert.NotNull(sheet);

        ExceptionDispatchInfo? failure = null;
        int? secondThread = null;
        second.Dispatch(() =>
        {
            var window = new Window { Width = 400, Height = 300, Content = new TextBox { Width = 200, Height = 40 } };
            try
            {
                secondThread = Environment.CurrentManagedThreadId;
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Assert.NotNull(NativeTextInputOverlay.GetVisiblePlacement((TextBox)window.Content!, window));
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
        Assert.NotEqual(firstThread, secondThread);
        failure?.Throw();
        GC.KeepAlive(sheet);
    }
}
