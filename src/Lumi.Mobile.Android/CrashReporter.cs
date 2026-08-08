using System.Text;
using Android.Runtime;
using Lumi.Mobile.Services;
using Lumi.Remote.Protocol;

namespace Lumi.Mobile.Android;

/// <summary>
/// Last-resort crash capture.
///
/// <para>A phone gives you nothing when a managed exception escapes: the process disappears and the
/// stack trace is only in logcat, which needs a cable or wireless debugging to read. That makes a
/// crash on a real device far more expensive to diagnose than the same crash on the desktop
/// simulator. Writing the exception somewhere the user can reach turns "it crashes" into an actual
/// stack trace.</para>
///
/// <para>The report is retained in the app's external files directory for <c>adb pull</c> and sent
/// best-effort to the paired desktop, where it is easier to retrieve.</para>
/// </summary>
internal static class CrashReporter
{
    private const string Tag = "LumiMobile";

    internal static void Install()
    {
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
        {
            Write("AndroidEnvironment", e.Exception);
            // Left unhandled on purpose: swallowing it would leave the process in an unknown state.
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("AppDomain", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("UnobservedTask", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Where the report lands, or null when the platform gives us nowhere to write.</summary>
    internal static string? ReportPath
    {
        get
        {
            var dir = global::Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
            return dir is null ? null : Path.Combine(dir, "lumi-crash.txt");
        }
    }

    private static void Write(string source, Exception? ex)
    {
        if (ex is null)
            return;

        // logcat first: it is the one sink that cannot fail because of storage permissions.
        global::Android.Util.Log.Error(Tag, $"[{source}] {ex}");

        // Pulled out of the interpolation: inside a hole, the ':' of 'global::' is parsed as the
        // start of a format specifier.
        var sdk = global::Android.OS.Build.VERSION.SdkInt;
        var model = global::Android.OS.Build.Model;

        var report = new StringBuilder()
            .AppendLine($"Lumi mobile crash — {DateTimeOffset.Now:u}")
            .AppendLine($"Source: {source}")
            .AppendLine($"Android: {sdk} on {model}")
            .AppendLine()
            .AppendLine(ex.ToString())
            .AppendLine()
            .AppendLine(new string('-', 60))
            .ToString();

        try
        {
            // Appended, because the interesting crash is often the FIRST one — a later failure during
            // teardown would otherwise overwrite the cause with a symptom.
            if (ReportPath is { } path)
                File.AppendAllText(path, report);
        }
        catch (Exception writeFailure)
        {
            global::Android.Util.Log.Error(Tag, $"Could not persist crash report: {writeFailure.Message}");
        }

#if DEBUG
        SendToPairedDesktop(report);
#endif
    }

    /// <summary>
    /// Pushes the report to the paired PC, reusing the existing upload route.
    ///
    /// <para>Since Android 11 the app's own external files directory is no longer browsable from the
    /// phone's Files app, so writing a file there does not actually put it within the user's reach —
    /// and reading logcat needs a cable or wireless debugging. The phone already knows its desktop's
    /// address and token, so the report can simply travel back to the machine that built the app.</para>
    ///
    /// <para>Best effort and time-boxed: the process is already going down, and a crash reporter that
    /// hangs on a dead socket is worse than no crash reporter.</para>
    /// </summary>
    private static void SendToPairedDesktop(string report)
    {
        try
        {
            var settings = new MobileSettingsStore().Load();
            if (settings.BaseUrl is not { Length: > 0 } baseUrl || settings.Token is not { Length: > 0 } token)
                return;

            var requestUri = new Uri(baseUrl + RemoteProtocol.Routes.Upload);
            RemoteRouteSecurity.EnsureTrusted(requestUri, RemotePlatformServices.RouteVerifier);
            using var http = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false
            })
            {
                Timeout = TimeSpan.FromSeconds(5)
            };
            http.DefaultRequestHeaders.Add(RemoteProtocol.DeviceTokenHeader, token);

            var fileName = $"lumi-crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(report));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                "application/octet-stream");
            http.DefaultRequestHeaders.TryAddWithoutValidation(
                RemoteProtocol.UploadFileNameHeader,
                Convert.ToBase64String(Encoding.UTF8.GetBytes(fileName)));

            // Run the socket work on the pool because Android forbids network I/O on the main
            // thread, but synchronously observe its completion (including HttpClient's timeout)
            // before disposing the client and request content. A timed-out Wait used to return while
            // PostAsync was still alive, leaving its eventual exception unobserved.
            using var response = Task.Run(
                    () => http.PostAsync(baseUrl + RemoteProtocol.Routes.Upload, content))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error(Tag, $"Could not send crash report: {ex.Message}");
        }
    }
}
