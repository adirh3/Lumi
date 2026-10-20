using System.Text;

namespace Lumi.Services;

/// <summary>
/// The live output of a running shell command, put together from the SDK's <c>tool.shell_output</c> chunks.
/// A long output keeps only its latest part: a terminal card renders just its tail, and handing all of a long
/// output to the UI on every chunk would cost more with every chunk.
/// </summary>
internal sealed class LiveShellOutput
{
    /// <summary>Twice what a terminal card renders (<c>StrataTerminalPreview.MaxRenderedOutputLength</c>).</summary>
    internal const int MaxLength = 16 * 1024;

    private readonly StringBuilder _text = new();

    /// <summary>
    /// True once earlier output has been let go: the text no longer starts where the output did, so it
    /// replaces what the card shows instead of extending it.
    /// </summary>
    public bool IsTail { get; private set; }

    /// <summary>Adds the next chunk and returns the output as it now stands.</summary>
    public string Append(string? chunk)
    {
        _text.Append(chunk);
        if (_text.Length <= MaxLength)
            return _text.ToString();

        var text = _text.ToString();
        // The kept part starts at the beginning of a line, never mid-line.
        var cut = text.Length - MaxLength;
        var lineStart = text.IndexOf('\n', cut - 1) + 1;
        var tail = lineStart > 0 && lineStart < text.Length ? text[lineStart..] : text[cut..];
        _text.Clear().Append(tail);
        IsTail = true;
        return tail;
    }
}
