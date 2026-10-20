using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lumi.Services.Byok;

/// <summary>macOS Keychain and Linux Secret Service storage; never substitutes file encryption.</summary>
internal sealed class NativeCredentialKeyStore : ISecureKeyStore
{
    internal const string KeyPrefix = "Lumi.Byok.";
    internal const string ServiceName = "Lumi.Byok";
    private const int MaximumOutputCharacters = 32 * 1024;
    private readonly NativePlatform _platform;
    private readonly string? _executable;
    private readonly Func<ProcessStartInfo, string?, CancellationToken, Task<ProcessResult>> _runProcess;
    private readonly TimeSpan _processTimeout;

    internal enum NativePlatform { Unsupported, MacOS, Linux }
    internal sealed record ProcessResult(int ExitCode, string StandardOutput = "", string StandardError = "")
    {
        public override string ToString() => $"Native credential command (exit {ExitCode})";
    }

    public NativeCredentialKeyStore()
        : this(OperatingSystem.IsMacOS() ? NativePlatform.MacOS :
               OperatingSystem.IsLinux() ? NativePlatform.Linux : NativePlatform.Unsupported,
            ResolveExecutable, RunProcessAsync) { }

    internal NativeCredentialKeyStore(
        NativePlatform platform,
        Func<string, string?> resolveExecutable,
        Func<ProcessStartInfo, string?, CancellationToken, Task<ProcessResult>> runProcess,
        TimeSpan? processTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(resolveExecutable);
        ArgumentNullException.ThrowIfNull(runProcess);
        _platform = platform;
        _runProcess = runProcess;
        _processTimeout = processTimeout ?? TimeSpan.FromSeconds(30);
        if (_processTimeout <= TimeSpan.Zero || _processTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(processTimeout));
        _executable = platform switch
        {
            NativePlatform.MacOS => resolveExecutable("/usr/bin/security"),
            NativePlatform.Linux => resolveExecutable("secret-tool"),
            _ => null,
        };
    }

    public bool IsSupported => !string.IsNullOrEmpty(_executable);

    public async Task<string?> GetAsync(string key)
    {
        var target = MakeTarget(key);
        if (!IsSupported)
            return null;
        var info = CreateStartInfo();
        if (_platform == NativePlatform.MacOS)
            AddArguments(info, "find-generic-password", "-s", ServiceName, "-a", target, "-w");
        else
            AddArguments(info, "lookup", "service", ServiceName, "account", target);
        var result = await ExecuteAsync(info, null).ConfigureAwait(false);
        if (IsMissing(result))
            return null;
        EnsureSuccess(result, "read");

        // security -w adds one newline; secret-tool adds none when stdout is redirected.
        var secret = _platform == NativePlatform.MacOS && result.StandardOutput.EndsWith('\n')
            ? result.StandardOutput[..^1]
            : result.StandardOutput;
        return string.IsNullOrEmpty(secret) ? null : secret;
    }

    public async Task SetAsync(string key, string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            await DeleteAsync(key).ConfigureAwait(false);
            return;
        }
        var target = MakeTarget(key);
        EnsureSupported();
        // security -w prints non-ASCII/non-printable passwords as hex, not their original
        // bytes. This store intentionally accepts printable API keys, not arbitrary blobs.
        if (secret.Length > 16 * 1024 || secret.Any(ch => ch < ' ' || ch > '~'))
            throw new ArgumentException("Native storage requires a printable API key of a supported length.", nameof(secret));

        var info = CreateStartInfo();
        string input;
        if (_platform == NativePlatform.MacOS)
        {
            // Apple's security -i consumes commands directly from stdin (no shell, no
            // process argv containing the password). Its parser honors backslash escapes
            // inside quotes and has a 4096-byte input buffer. Send exactly one command:
            // a second/blank command would overwrite the first command's exit status.
            AddArguments(info, "-i", "-q");
            input = "add-generic-password -U -s " + QuoteSecurityArgument(ServiceName) +
                " -a " + QuoteSecurityArgument(target) + " -w " + QuoteSecurityArgument(secret) + "\n";
            if (Encoding.UTF8.GetByteCount(input) >= 4096)
                throw new ArgumentException("The API key is too long for macOS native storage.", nameof(secret));
        }
        else
        {
            AddArguments(info, "store", "--label=Lumi BYOK API key", "service", ServiceName, "account", target);
            // secret-tool stores all stdin bytes through EOF, including any added newline.
            input = secret;
        }
        EnsureSuccess(await ExecuteAsync(info, input).ConfigureAwait(false), "store");
    }

    public async Task DeleteAsync(string key)
    {
        var target = MakeTarget(key);
        EnsureSupported();
        var info = CreateStartInfo();
        if (_platform == NativePlatform.MacOS)
            AddArguments(info, "delete-generic-password", "-s", ServiceName, "-a", target);
        else
            AddArguments(info, "clear", "service", ServiceName, "account", target);
        var result = await ExecuteAsync(info, null).ConfigureAwait(false);
        if (!IsMissing(result))
            EnsureSuccess(result, "delete");
    }

    private bool IsMissing(ProcessResult result)
        // macOS returns errSecItemNotFound (-25300), truncated to an 8-bit exit code.
        // libsecret lookup/clear return 1 without stderr only when no entry is found;
        // real Secret Service failures also return 1 but print an error.
        => _platform == NativePlatform.MacOS
            ? result.ExitCode == 44
            : result.ExitCode == 1 && result.StandardOutput.Length == 0 && result.StandardError.Length == 0;

    private void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Native credential storage could not {operation} the API key (exit {result.ExitCode}). " +
                (_platform == NativePlatform.MacOS
                    ? "Unlock your macOS keychain and try again."
                    : "Check that your desktop Secret Service is running, unlock your keyring, and try again."));
    }

    private void EnsureSupported()
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException(
                "Native credential storage is unavailable. Install the platform's keychain tools or explicitly choose an environment variable or stored-key mode.");
    }

    private static string MakeTarget(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length > 512 || key.Any(char.IsControl))
            throw new ArgumentException("The credential identifier is invalid.", nameof(key));
        return KeyPrefix + key;
    }

    internal static string QuoteSecurityArgument(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private ProcessStartInfo CreateStartInfo() => new(_executable!)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardInputEncoding = new UTF8Encoding(false),
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };

    private static void AddArguments(ProcessStartInfo info, params string[] arguments)
    {
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
    }

    private async Task<ProcessResult> ExecuteAsync(ProcessStartInfo info, string? input)
    {
        using var timeout = new CancellationTokenSource(_processTimeout);
        try
        {
            return await _runProcess(info, input, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("Native credential storage timed out. Unlock your keychain or keyring and try again.");
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("The native credential storage tool could not be started. Check its installation and try again.");
        }
        catch (IOException)
        {
            throw new InvalidOperationException("Communication with native credential storage failed. Try again.");
        }
    }

    private static string? ResolveExecutable(string executable)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            return null;
        var candidates = Path.IsPathRooted(executable)
            ? new[] { executable }
            : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Where(path => !string.IsNullOrEmpty(path) && Path.IsPathRooted(path))
                .Concat(new[] { "/usr/bin", "/bin" })
                .Select(path => Path.Combine(path, executable));
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) &&
                (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                return candidate;
        }
        return null;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        ProcessStartInfo info, string? input, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = info };
        if (!process.Start())
            throw new InvalidOperationException("The native credential storage tool could not be started.");
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput, cancellationToken);
            var error = ReadBoundedAsync(process.StandardError, cancellationToken);
            var write = WriteInputAsync(process.StandardInput, input, cancellationToken);
            await Task.WhenAll(output, error, write, process.WaitForExitAsync(cancellationToken))
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
        }
    }

    private static async Task WriteInputAsync(StreamWriter writer, string? input, CancellationToken cancellationToken)
    {
        try
        {
            if (input is not null)
                await writer.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { writer.Close(); }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var value = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (value.Length + read > MaximumOutputCharacters)
                throw new InvalidOperationException("The native credential storage tool returned too much data.");
            value.Append(buffer, 0, read);
        }
        return value.ToString();
    }
}
