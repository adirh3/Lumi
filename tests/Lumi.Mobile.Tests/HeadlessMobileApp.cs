using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless;
using Lumi.Mobile;
using Lumi.Mobile.Services;

namespace Lumi.Mobile.Tests;

/// <summary>
/// The real mobile <see cref="App"/> (so tests load the real styles, theme and resources) with the
/// window creation skipped: headless tests build the views they want themselves.
/// </summary>
public sealed class HeadlessMobileApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        // Deliberately does not call base: no shell, no window, no network. Tests compose their own.
    }
}

internal sealed class HeadlessMobileSession : IDisposable
{
    private readonly HeadlessUnitTestSession _inner;
    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "Lumi.Mobile.Tests", Guid.NewGuid().ToString("N"));

    private HeadlessMobileSession(HeadlessUnitTestSession inner) => _inner = inner;

    public static HeadlessMobileSession Start() =>
        new(HeadlessUnitTestSession.StartNew(typeof(HeadlessMobileApp), AvaloniaTestIsolationLevel.PerTest));

    /// <summary>
    /// A throwaway settings store, deleted when the session ends.
    ///
    /// Every <see cref="MobileShellViewModel"/> built in a test MUST take one of these. The default
    /// <see cref="MobileSettingsStore"/> resolves to the real per-user <c>%APPDATA%\LumiMobile</c>
    /// folder, so a test that omits it reads (and can overwrite) the developer's own pairing token:
    /// pairing the simulator on this machine turned two shell tests red because the view model
    /// loaded that live <c>connection.json</c> and started up already paired.
    /// </summary>
    public MobileSettingsStore NewStore() =>
        new(Path.Combine(_scratch, Guid.NewGuid().ToString("N")));

    /// <summary>
    /// Avalonia's session swallows exceptions thrown inside the dispatched body, so callers must
    /// capture results inside and assert outside (or use <see cref="Run"/>, which re-throws).
    /// </summary>
    public Task Dispatch(Action action, CancellationToken cancellationToken) =>
        _inner.Dispatch(action, cancellationToken);

    public Task Dispatch(Func<Task> action, CancellationToken cancellationToken) =>
        _inner.Dispatch(action, cancellationToken);

    public void Dispose()
    {
        try
        {
            _inner.Dispose();
        }
        catch (NullReferenceException)
        {
            // Avalonia.Headless can throw during PerTest teardown after the body completed.
        }

        try
        {
            if (Directory.Exists(_scratch))
                Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a green test over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
