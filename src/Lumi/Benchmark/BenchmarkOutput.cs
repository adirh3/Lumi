using System;
using System.IO;
using System.Text;

namespace Lumi.Benchmark;

/// <summary>
/// Clean output abstraction for benchmark results.
/// Collects all output in memory and can flush to a file.
/// Designed to be replaceable with a logging framework later.
/// </summary>
internal sealed class BenchmarkOutput
{
    private readonly StringBuilder _buffer = new();

    public void WriteLine(string text = "")
    {
        _buffer.AppendLine(text);
    }

    public void WriteError(string text)
    {
        _buffer.Append("[ERROR] ");
        _buffer.AppendLine(text);
    }

    public void WriteWarning(string text)
    {
        _buffer.Append("[WARN]  ");
        _buffer.AppendLine(text);
    }

    /// <summary>Get the full accumulated output text.</summary>
    public string GetText() => _buffer.ToString();

    /// <summary>Write accumulated output to a text file.</summary>
    public void WriteToFile(string path) => File.WriteAllText(path, _buffer.ToString());
}
