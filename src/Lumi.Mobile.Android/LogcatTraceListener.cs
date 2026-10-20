using System.Diagnostics;

namespace Lumi.Mobile.Android;

/// <summary>
/// Forwards the shared library's <c>[Mobile]</c> trace lines (connection failures, transport
/// summaries written when the app goes to the background) to logcat, where
/// <c>adb logcat -s LumiMobile</c> can read them on a real phone.
/// </summary>
internal sealed class LogcatTraceListener : TraceListener
{
    private const string Tag = "LumiMobile";

    public override void TraceEvent(
        TraceEventCache? eventCache,
        string source,
        TraceEventType eventType,
        int id,
        string? message)
    {
        if (string.IsNullOrEmpty(message) || !message.StartsWith("[Mobile]", StringComparison.Ordinal))
            return;

        if (eventType <= TraceEventType.Error)
            global::Android.Util.Log.Error(Tag, message);
        else if (eventType == TraceEventType.Warning)
            global::Android.Util.Log.Warn(Tag, message);
        else
            global::Android.Util.Log.Info(Tag, message);
    }

    public override void TraceEvent(
        TraceEventCache? eventCache,
        string source,
        TraceEventType eventType,
        int id,
        string? format,
        params object?[]? args) =>
        TraceEvent(eventCache, source, eventType, id,
            args is { Length: > 0 } && format is not null ? string.Format(format, args) : format);

    public override void Write(string? message)
    {
    }

    public override void WriteLine(string? message)
    {
    }
}
