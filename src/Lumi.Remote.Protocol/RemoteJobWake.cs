namespace Lumi.Remote.Protocol;

/// <summary>The desktop job-wake presentation, shared with clients reading older transcripts.</summary>
public sealed record RemoteJobWake(
    string JobName,
    string Instructions,
    string WakeSignal,
    string ExitCode,
    string StartedText,
    string CompletedText,
    string OutputText)
{
    private const string AuthorPrefix = "Lumi Job - ";
    private const string ContentPrefix = "Background job triggered:";

    public static bool IsJobWake(string? author, string? content) =>
        author?.StartsWith(AuthorPrefix, StringComparison.OrdinalIgnoreCase) == true
        && content?.StartsWith(ContentPrefix, StringComparison.OrdinalIgnoreCase) == true;

    public static RemoteJobWake Parse(string? author, string content)
    {
        var firstLine = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var name = author?.StartsWith(AuthorPrefix, StringComparison.OrdinalIgnoreCase) == true
            ? author[AuthorPrefix.Length..].Trim()
            : firstLine.StartsWith(ContentPrefix, StringComparison.OrdinalIgnoreCase)
                ? firstLine[ContentPrefix.Length..].Trim()
                : "Background job";
        var context = Section(content, "Trigger context:", "Respond as Lumi");
        var outputIndex = context.IndexOf("Full script output:", StringComparison.OrdinalIgnoreCase);
        var signalSource = outputIndex < 0 ? context : context[..outputIndex];
        var signal = Lines(signalSource).FirstOrDefault(line =>
            !line.StartsWith("Wake script exited", StringComparison.OrdinalIgnoreCase)
            && !line.StartsWith("Started:", StringComparison.OrdinalIgnoreCase)
            && !line.StartsWith("Completed:", StringComparison.OrdinalIgnoreCase));
        var exitLine = Lines(context).FirstOrDefault(line =>
            line.StartsWith("Wake script exited with code", StringComparison.OrdinalIgnoreCase));
        var exitCode = exitLine is null
            ? LineValue(context, "Exit code:")
            : exitLine["Wake script exited with code".Length..].Trim().TrimEnd('.');

        return new RemoteJobWake(
            name,
            Section(content, "Job instructions:", "Trigger context:"),
            string.IsNullOrWhiteSpace(signal) ? "Wake signal received." : Preview(signal, 260),
            exitCode,
            LineValue(context, "Started:"),
            LineValue(context, "Completed:"),
            Section(context, "Full script output:", null));
    }

    private static string Section(string content, string startMarker, string? endMarker)
    {
        var start = content.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return "";
        start += startMarker.Length;
        var end = endMarker is null ? content.Length : content.IndexOf(endMarker, start, StringComparison.OrdinalIgnoreCase);
        return content[start..(end < 0 ? content.Length : end)].Trim();
    }

    private static string[] Lines(string content) =>
        content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string LineValue(string content, string prefix) =>
        Lines(content).FirstOrDefault(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..].Trim() ?? "";

    private static string Preview(string text, int limit) =>
        text.Trim().Length <= limit ? text.Trim() : text.Trim()[..(limit - 1)].TrimEnd() + "...";
}
