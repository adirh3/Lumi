using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lumi.Services;

internal sealed partial class McpStdioServerConnection : IAsyncDisposable
{
    private void RestartProcessForExpiredSession()
    {
        var retiredGeneration = Volatile.Read(ref _processGeneration);
        RetireCurrentProcess(
            retiredGeneration,
            new McpServerSessionRetiredException(_definition.Name, retiredGeneration));
    }

    private void RetireProcessAfterFailedInitialization()
    {
        var retiredGeneration = Volatile.Read(ref _processGeneration);
        RetireCurrentProcess(
            retiredGeneration,
            new IOException($"MCP server '{_definition.Name}' initialization failed."));
    }

    private void RetireCurrentProcess(int retiredGeneration, Exception pendingError)
    {
        var process = _process;
        var oldIoCts = _ioCts;
        var oldStdoutTask = _stdoutTask;
        var oldStderrTask = _stderrTask;

        _process = null;
        _stdin = null;
        _stdoutTask = null;
        _stderrTask = null;
        _initializeResult = null;
        _ioCts = new CancellationTokenSource();
        CompletePendingWithErrorForGeneration(retiredGeneration, pendingError);

        oldIoCts.Cancel();
        TrackRetiredProcess(process, terminate: true, oldIoCts, oldStdoutTask, oldStderrTask);
    }

    private void StartProcess()
    {
        ThrowIfDisposed();
        if (_process is { HasExited: false })
            return;

        if (string.IsNullOrWhiteSpace(_definition.Config.Command))
            throw new InvalidOperationException($"MCP server '{_definition.Name}' does not have a command.");

        var configuredCommand = _definition.Config.Command;
        var startInfo = CreateStartInfo();

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start MCP server '{_definition.Name}'.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(BuildWin32ProcessStartErrorMessage(startInfo, configuredCommand, ex), ex);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidOperationException(BuildProcessStartErrorMessage(startInfo, configuredCommand), ex);
        }

        _process = process;
        _stdin = process.StandardInput;
        int generation;
        lock (_discoverySignalGate)
            generation = Interlocked.Increment(ref _processGeneration);
        _stdoutTask = Task.Run(() => ReadStdoutAsync(process, generation, _ioCts.Token), _ioCts.Token);
        _stderrTask = Task.Run(() => DrainStderrAsync(process.StandardError, _ioCts.Token), _ioCts.Token);
    }

    private ProcessStartInfo CreateStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(_definition.Config.WorkingDirectory))
            startInfo.WorkingDirectory = _definition.Config.WorkingDirectory;

        foreach (var arg in _definition.Config.Args ?? [])
            startInfo.ArgumentList.Add(arg);

        if (_definition.Config.Env is not null)
        {
            foreach (var (key, value) in _definition.Config.Env)
                startInfo.Environment[key] = value;
        }

        var configuredCommand = _definition.Config.Command;
        startInfo.FileName = configuredCommand;
        UnixShellPath.ApplyTo(startInfo);
        if (OperatingSystem.IsWindows())
            startInfo.FileName = ResolveCommandPath(configuredCommand, startInfo.Environment);

        return startInfo;
    }

    private string BuildProcessStartErrorMessage(ProcessStartInfo startInfo, string configuredCommand)
    {
        var cwd = string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
            ? Environment.CurrentDirectory
            : startInfo.WorkingDirectory;
        var pathEntryCount = CountPathEntries(startInfo.Environment);
        var pathContext = pathEntryCount is null
            ? ""
            : $" PATH entries searched: {pathEntryCount.Value}.";

        if (string.Equals(startInfo.FileName, configuredCommand, StringComparison.OrdinalIgnoreCase))
        {
            return $"Failed to start MCP server '{_definition.Name}'. Command '{configuredCommand}' was not found from working directory '{cwd}'. Install '{configuredCommand}' or add it to the PATH used by Lumi.{pathContext}";
        }

        return $"Failed to start MCP server '{_definition.Name}'. Command '{configuredCommand}' resolved to '{startInfo.FileName}' but could not be started from working directory '{cwd}'.{pathContext}";
    }

    private string BuildWin32ProcessStartErrorMessage(
        ProcessStartInfo startInfo,
        string configuredCommand,
        Win32Exception error)
    {
        var cwd = string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
            ? Environment.CurrentDirectory
            : startInfo.WorkingDirectory;
        var pathEntryCount = CountPathEntries(startInfo.Environment);
        var pathContext = pathEntryCount is null
            ? ""
            : $" PATH entries searched: {pathEntryCount.Value}.";

        return error.NativeErrorCode switch
        {
            2 or 3 => BuildProcessStartErrorMessage(startInfo, configuredCommand),
            5 => $"Failed to start MCP server '{_definition.Name}'. Access denied while starting command '{configuredCommand}' from working directory '{cwd}'.",
            193 => $"Failed to start MCP server '{_definition.Name}'. Command '{configuredCommand}' is not a valid executable for this platform.",
            _ => $"Failed to start MCP server '{_definition.Name}'. Command '{configuredCommand}' could not be started from working directory '{cwd}': {error.Message}.{pathContext}"
        };
    }

    private static string ResolveCommandPath(string command, IDictionary<string, string?> environment)
    {
        if (!OperatingSystem.IsWindows()
            || string.IsNullOrWhiteSpace(command)
            || HasDirectorySeparator(command)
            || Path.IsPathRooted(command))
        {
            return command;
        }

        var path = GetEnvironmentValue(environment, "PATH");
        if (string.IsNullOrWhiteSpace(path))
            return command;

        var candidateNames = GetWindowsCommandCandidateNames(command, environment);
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            var trimmedDirectory = directory.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(trimmedDirectory))
                continue;

            foreach (var candidateName in candidateNames)
            {
                var candidate = Path.Combine(trimmedDirectory, candidateName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return command;
    }

    private static bool HasDirectorySeparator(string command)
        => command.Contains(Path.DirectorySeparatorChar)
           || command.Contains(Path.AltDirectorySeparatorChar)
           || (OperatingSystem.IsWindows() && command.Contains('/'));

    private static IReadOnlyList<string> GetWindowsCommandCandidateNames(
        string command,
        IDictionary<string, string?> environment)
    {
        if (!string.IsNullOrEmpty(Path.GetExtension(command)))
            return [command];

        var pathExt = GetEnvironmentValue(environment, "PATHEXT");
        var extensions = string.IsNullOrWhiteSpace(pathExt)
            ? [".COM", ".EXE", ".BAT", ".CMD"]
            : pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(extension => extension.StartsWith('.') ? extension : "." + extension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        return extensions
            .Select(extension => command + extension)
            .Concat([command])
            .ToList();
    }

    private static string? GetEnvironmentValue(IDictionary<string, string?> environment, string key)
    {
        if (environment.TryGetValue(key, out var value))
            return value;

        foreach (var pair in environment)
        {
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return null;
    }

    private static int? CountPathEntries(IDictionary<string, string?> environment)
    {
        var path = GetEnvironmentValue(environment, "PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return path
            .Split(Path.PathSeparator)
            .Count(entry => !string.IsNullOrWhiteSpace(entry));
    }

    private void ResetStoppedProcess()
    {
        var process = _process;
        var retiredGeneration = Volatile.Read(ref _processGeneration);
        var oldIoCts = _ioCts;
        var oldStdoutTask = _stdoutTask;
        var oldStderrTask = _stderrTask;
        _process = null;
        _stdin = null;
        _stdoutTask = null;
        _stderrTask = null;
        _initializeResult = null;
        _ioCts = new CancellationTokenSource();
        CompletePendingWithErrorForGeneration(
            retiredGeneration,
            new IOException($"MCP server '{_definition.Name}' stopped."));
        oldIoCts.Cancel();
        TrackRetiredProcess(process, terminate: false, oldIoCts, oldStdoutTask, oldStderrTask);
    }

    private void TrackRetiredProcess(
        Process? process,
        bool terminate,
        CancellationTokenSource ioCts,
        Task? stdoutTask,
        Task? stderrTask)
    {
        _retiredProcessTasks.RemoveAll(static task => task.IsCompletedSuccessfully);
        _retiredProcessTasks.Add(DisposeRetiredProcessAsync(
            process,
            terminate,
            ioCts,
            stdoutTask,
            stderrTask));
    }

    private static async Task DisposeRetiredProcessAsync(
        Process? process,
        bool terminate,
        CancellationTokenSource ioCts,
        Task? stdoutTask,
        Task? stderrTask)
    {
        try
        {
            if (process is not null)
            {
                if (terminate)
                    await TerminateProcessTreeAndWaitAsync(process).ConfigureAwait(false);
                else
                    await process.WaitForExitAsync().ConfigureAwait(false);
            }

            await IgnoreAsync(stdoutTask).ConfigureAwait(false);
            await IgnoreAsync(stderrTask).ConfigureAwait(false);
        }
        finally
        {
            process?.Dispose();
            ioCts.Dispose();
        }
    }

    private static async Task TerminateProcessTreeAndWaitAsync(Process process)
    {
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().ConfigureAwait(false);
    }

    private static bool IsProcessRunning(Process? process)
    {
        if (process is null)
            return false;

        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task DrainStderrAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                AddDiagnosticLine(_recentStderr, line);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch { }
    }

    private InvalidOperationException CreateNonJsonStdoutException(string line, JsonException inner)
        => new(
            $"MCP server '{_definition.Name}' wrote non-JSON output to stdout during startup or initialization: {FormatDiagnosticLine(line)}{FormatCapturedOutput()}",
            inner);

    private IOException CreateServerStoppedException(Process process)
    {
        var builder = new StringBuilder($"MCP server '{_definition.Name}' stopped");
        if (TryGetExitCode(process) is { } exitCode)
            builder.Append(" with exit code ").Append(exitCode);
        builder.Append('.');
        builder.Append(FormatCapturedOutput());
        return new IOException(builder.ToString());
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void AddDiagnosticLine(Queue<string> target, string line)
    {
        var formatted = FormatDiagnosticLine(line);
        if (string.IsNullOrWhiteSpace(formatted))
            return;

        lock (_diagnosticOutputLock)
        {
            target.Enqueue(formatted);
            while (target.Count > DiagnosticLineLimit)
                target.Dequeue();
        }
    }

    private string FormatCapturedOutput()
    {
        string[] stdout;
        string[] stderr;
        lock (_diagnosticOutputLock)
        {
            stdout = _recentStdout.ToArray();
            stderr = _recentStderr.ToArray();
        }

        var parts = new List<string>();
        if (stdout.Length > 0)
            parts.Add("stdout: " + string.Join(" | ", stdout));
        if (stderr.Length > 0)
            parts.Add("stderr: " + string.Join(" | ", stderr));

        if (parts.Count == 0)
            return "";

        var text = " Recent output - " + string.Join("; ", parts);
        return text.Length <= DiagnosticTextMaxLength
            ? text
            : text[..DiagnosticTextMaxLength] + "...";
    }

    private static string FormatDiagnosticLine(string line)
    {
        var formatted = line.Replace('\r', ' ').Replace('\n', ' ').Trim();
        formatted = BearerDiagnosticPattern.Replace(formatted, "Bearer [redacted]");
        formatted = SensitiveDiagnosticPattern.Replace(formatted, "$1$2[redacted]");
        return formatted.Length <= DiagnosticLineMaxLength
            ? formatted
            : formatted[..DiagnosticLineMaxLength] + "...";
    }

    private static async Task IgnoreAsync(Task? task)
    {
        if (task is null)
            return;

        try { await task.ConfigureAwait(false); }
        catch { }
    }
}
