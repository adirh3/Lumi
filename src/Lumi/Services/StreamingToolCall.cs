using System;
using System.Text;

namespace Lumi.Services;

/// <summary>
/// A file tool call whose arguments the model is still writing (the SDK's <c>assistant.tool_call_delta</c>):
/// the file it will write, how much it has written, and since when. Once it is long enough to keep someone
/// waiting it asks to be shown, and again whenever its path arrives or another kilobyte is written.
/// </summary>
internal sealed class StreamingToolCall(string toolName, DateTimeOffset startedAt)
{
    /// <summary>A short write starts at once; only a longer one is worth a row of its own.</summary>
    internal const int ShowAfterLength = 1024;

    // The path is one of the first arguments; past this it is not looked for again.
    private const int PathSearchLength = 8192;

    private readonly StringBuilder _input = new();
    private int _shownKilobytes = -1;
    private bool _pathShown;

    public string ToolName { get; } = toolName;
    public DateTimeOffset StartedAt { get; } = startedAt;
    public string? FilePath { get; private set; }
    public int Length => _input.Length;

    /// <summary>Adds the next fragment of the arguments; true when the row should appear or change.</summary>
    public bool Append(string? delta)
    {
        if (string.IsNullOrEmpty(delta))
            return false;

        _input.Append(delta);
        if (FilePath is null && _input.Length <= PathSearchLength)
            FilePath = ToolDisplayHelper.ExtractStreamingFilePath(_input.ToString());

        if (_input.Length < ShowAfterLength)
            return false;

        var kilobytes = _input.Length / 1024;
        var pathArrived = FilePath is not null && !_pathShown;
        if (kilobytes == _shownKilobytes && !pathArrived)
            return false;

        _shownKilobytes = kilobytes;
        _pathShown |= FilePath is not null;
        return true;
    }
}
