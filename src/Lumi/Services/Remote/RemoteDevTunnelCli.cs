using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using Lumi.Localization;

namespace Lumi.Services.Remote;

internal sealed class RemoteDevTunnelCliException(
    string message, bool requiresSignIn = false, bool canRetry = true)
    : InvalidOperationException(message)
{
    public bool RequiresSignIn { get; } = requiresSignIn;
    public bool CanRetry { get; } = canRetry;
}

internal static class RemoteDevTunnelCli
{
    internal const long MaximumDownloadBytes = 128L * 1024 * 1024;
    internal static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);
    private const string DownloadRoot = "https://tunnelsassetsprod.blob.core.windows.net/cli/";
    private static readonly HttpClient DownloadClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static string ExecutableName => OperatingSystem.IsWindows() ? "devtunnel.exe" : "devtunnel";

    public static async Task<string?> EnsureAvailableAsync(
        Func<CancellationToken, Task<bool>> confirmInstall,
        Action<string> reportStatus,
        CancellationToken cancellationToken)
    {
        var cached = Path.Combine(DataStore.AppDirectory, "tools", "devtunnel", ExecutableName);
        var existing = FindExistingCli(
            ResolvePortableCliPath(Environment.ProcessPath, AppContext.BaseDirectory),
            cached,
            UnixShellPath.Augment(Environment.GetEnvironmentVariable("PATH")));
        return await ResolveWithConsentAsync(
                existing, confirmInstall,
                async token =>
                {
                    var platform = OperatingSystem.IsWindows() ? "windows"
                        : OperatingSystem.IsMacOS() ? "macos"
                        : OperatingSystem.IsLinux() ? "linux" : "";
                    await InstallAsync(
                            GetDownloadUri(platform, RuntimeInformation.OSArchitecture),
                            cached,
                            DownloadClient,
                            VerifyDownloadedCliAsync,
                            reportStatus,
                            token)
                        .ConfigureAwait(false);
                    return cached;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<string?> ResolveWithConsentAsync(
        string? existing,
        Func<CancellationToken, Task<bool>> confirmInstall,
        Func<CancellationToken, Task<string>> install,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (existing is not null)
            return existing;
        if (!await confirmInstall(cancellationToken).ConfigureAwait(false))
            return null;
        cancellationToken.ThrowIfCancellationRequested();
        return await install(cancellationToken).ConfigureAwait(false);
    }

    internal static string? FindExistingCli(string portablePath, string cachedPath, string? searchPath)
    {
        if (File.Exists(portablePath))
            return portablePath;
        if (!string.IsNullOrWhiteSpace(searchPath))
        {
            foreach (var entry in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
                if (!Path.IsPathRooted(directory))
                    continue;
                var candidate = Path.Combine(directory, ExecutableName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return File.Exists(cachedPath) ? cachedPath : null;
    }

    internal static string ResolvePortableCliPath(string? processPath, string appContextBaseDirectory) =>
        Path.Combine(
            Path.GetDirectoryName(processPath) ?? appContextBaseDirectory,
            "tools", "devtunnel", ExecutableName);

    internal static Uri GetDownloadUri(string platform, Architecture architecture)
    {
        var name = (platform, architecture) switch
        {
            ("windows", Architecture.X64 or Architecture.Arm64) => "devtunnel.exe",
            ("macos", Architecture.X64) => "osx-x64-devtunnel",
            ("macos", Architecture.Arm64) => "osx-arm64-devtunnel",
            ("linux", Architecture.X64) => "linux-x64-devtunnel",
            ("linux", Architecture.Arm64) => "linux-arm64-devtunnel",
            _ => throw new PlatformNotSupportedException(Loc.Get("Remote_DevTunnelAutoSetupUnavailable"))
        };
        return new Uri(DownloadRoot + name);
    }

    internal static bool IsOfficialDownloadUri(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.IdnHost == "tunnelsassetsprod.blob.core.windows.net"
        && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && (uri.AbsolutePath is "/cli/devtunnel.exe"
            or "/cli/osx-x64-devtunnel" or "/cli/osx-arm64-devtunnel"
            or "/cli/linux-x64-devtunnel" or "/cli/linux-arm64-devtunnel");

    internal static async Task InstallAsync(
        Uri source,
        string destination,
        HttpClient client,
        Func<string, CancellationToken, Task> verify,
        Action<string> reportStatus,
        CancellationToken cancellationToken)
    {
        if (!IsOfficialDownloadUri(source))
            throw new InvalidOperationException(Loc.Get("Remote_DevTunnelUntrustedDownload"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);
        var token = timeout.Token;
        token.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException(Loc.Get("Remote_DevTunnelInstallFailed"));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = Path.Combine(directory, $"download-{Guid.NewGuid():N}-{ExecutableName}");
        try
        {
            reportStatus(Loc.Get("Remote_DevTunnelDownloading"));
            using var response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != source)
                throw new InvalidOperationException(Loc.Get("Remote_DevTunnelUntrustedDownload"));
            if (response.Content.Headers.ContentLength is <= 0 or > MaximumDownloadBytes)
                throw new InvalidDataException(Loc.Get("Remote_DevTunnelInvalidDownload"));

            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var copied = await CopyDownloadAsync(input, output, MaximumDownloadBytes, token).ConfigureAwait(false);
                if (copied == 0
                    || response.Content.Headers.ContentLength is { } expected && copied != expected)
                {
                    throw new InvalidDataException(Loc.Get("Remote_DevTunnelInvalidDownload"));
                }
            }

            reportStatus(Loc.Get("Remote_DevTunnelVerifyingDownload"));
            await verify(temporary, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.Move(temporary, destination);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"[Remote] Could not remove incomplete CLI download: {ex.Message}");
            }
        }
    }

    internal static async Task<long> CopyDownloadAsync(
        Stream input, Stream output, long maximumBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;
            total += count;
            if (total > maximumBytes)
                throw new InvalidDataException(Loc.Get("Remote_DevTunnelInvalidDownload"));
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        return total;
    }

    internal static async Task VerifyDownloadedCliAsync(string path, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await using (var file = File.OpenRead(path))
            await file.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var expectedFormat = OperatingSystem.IsWindows()
            ? header[0] == 'M' && header[1] == 'Z'
            : OperatingSystem.IsLinux()
                ? header.AsSpan().SequenceEqual(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' })
                : header.AsSpan().SequenceEqual(new byte[] { 0xcf, 0xfa, 0xed, 0xfe });
        if (!expectedFormat)
            throw new InvalidDataException(Loc.Get("Remote_DevTunnelInvalidDownload"));
        if (!OperatingSystem.IsWindows())
            return;

        const string script = """
            $ErrorActionPreference = 'Stop'
            $signature = Get-AuthenticodeSignature -LiteralPath $env:LUMI_DEVTUNNEL_DOWNLOAD
            if ($signature.Status -ne 'Valid' -or
                $signature.SignerCertificate.GetNameInfo(
                    [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -ne 'Microsoft Corporation' -or
                $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
                throw 'The downloaded CLI does not have a valid Microsoft signature.'
            }
            """;
        var windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0");
        var info = CreateStartInfo(Path.Combine(windowsPowerShell, "powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-Command", script]);
        info.Environment["PSModulePath"] = Path.Combine(windowsPowerShell, "Modules");
        info.Environment["LUMI_DEVTUNNEL_DOWNLOAD"] = path;
        var result = await RunProcessAsync(info, TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            Trace.TraceWarning($"[Remote] CLI signature verification failed: {result.Error.Trim()}");
            throw new InvalidOperationException(Loc.Get("Remote_DevTunnelUntrustedDownload"));
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
    }

    internal static async Task<string> RunAsync(
        string executablePath,
        string[] arguments,
        CancellationToken cancellationToken,
        Action<string>? reportOutput = null)
    {
        var isSignIn = arguments.Length >= 2 && arguments[0] == "user" && arguments[1] == "login";
        (int ExitCode, string Output, string Error) result;
        try
        {
            result = await RunProcessAsync(
                    CreateCommandStartInfo(executablePath, arguments),
                    isSignIn ? SignInTimeout : CommandTimeout,
                    cancellationToken,
                    killEntireProcessTree: !isSignIn,
                    reportOutput)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!isSignIn && !cancellationToken.IsCancellationRequested)
        {
            throw new RemoteDevTunnelCliException(Loc.Get("Remote_DevTunnelTimeout"));
        }
        if (result.ExitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim();
            throw new RemoteDevTunnelCliException(
                Loc.Get("Remote_DevTunnelCliFailed", error),
                IsAuthenticationFailure(error), IsTransientFailure(error));
        }
        return NormalizeOutput(result.Output);
    }

    internal static ProcessStartInfo CreateCommandStartInfo(string executablePath, string[] arguments)
    {
        var info = CreateStartInfo(executablePath, arguments);
        if (OperatingSystem.IsWindows() && arguments is ["user", "login", ..])
        {
            // The CLI's Windows broker locates its parent through the console process.
            info.CreateNoWindow = false;
            info.WindowStyle = ProcessWindowStyle.Hidden;
        }
        return info;
    }

    internal static bool IsAuthenticationFailure(string error) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            error,
            @"\b(?:AADSTS\d+|401|unauthorized|not logged in|login required|authentication required|(?:login|token|authentication)[^\r\n]{0,80}expired|(?:log|sign) in again)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static bool IsTransientFailure(string error) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            error,
            @"\b(?:408|429|5\d\d|TooManyRequests|ServiceUnavailable|GatewayTimeout|timeout|timed out|network|socket|unavailable|no such host|name resolution|failed to connect|connection (?:refused|reset|closed|aborted))\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static string NormalizeOutput(string output)
    {
        var text = output.TrimStart();
        if (!text.StartsWith("Welcome to dev tunnels!", StringComparison.Ordinal))
            return output;
        const string bannerEnd =
            "Use 'devtunnel --help' to see available commands or visit: https://aka.ms/devtunnels/docs";
        var end = text.IndexOf(bannerEnd, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidOperationException(Loc.Get("Remote_DevTunnelInvalidResponse"));
        return text[(end + bannerEnd.Length)..].TrimStart();
    }

    internal static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        ProcessStartInfo info,
        TimeSpan deadline,
        CancellationToken cancellationToken,
        bool killEntireProcessTree = true,
        Action<string>? reportOutput = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline);
        timeout.Token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = info };
        if (!process.Start())
            throw new InvalidOperationException(Loc.Get("Remote_DevTunnelStartFailed"));
        using var registration = timeout.Token.Register(() => StopProcess(process, killEntireProcessTree));
        var output = reportOutput is null
            ? process.StandardOutput.ReadToEndAsync(timeout.Token)
            : ReadOutputAsync(process.StandardOutput, reportOutput, timeout.Token);
        var errors = ReadErrorsAsync(process.StandardError, timeout.Token, reportOutput);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false), await errors.ConfigureAwait(false));
        }
        finally
        {
            StopProcess(process, killEntireProcessTree);
            try
            {
                await Task.WhenAll(output, errors).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task<string> ReadOutputAsync(
        StreamReader reader,
        Action<string> reportOutput,
        CancellationToken cancellationToken,
        int? maximumCharacters = null)
    {
        var output = new StringBuilder();
        var buffer = new char[1024];
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) is var count
               && count > 0)
        {
            var chunk = new string(buffer, 0, count);
            output.Append(chunk);
            reportOutput(chunk);
            if (maximumCharacters is { } maximum && output.Length > maximum)
                output.Remove(0, output.Length - maximum);
        }
        return output.ToString();
    }

    internal static async Task<string> ReadErrorsAsync(
        StreamReader reader,
        CancellationToken cancellationToken,
        Action<string>? reportOutput = null)
    {
        if (reportOutput is not null)
            return await ReadOutputAsync(reader, reportOutput, cancellationToken, 2000).ConfigureAwait(false);
        return await ReadErrorLinesAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<string> ReadErrorLinesAsync(
        StreamReader reader,
        CancellationToken cancellationToken,
        Action<string>? reportLine = null)
    {
        var tail = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            tail.AppendLine(line);
            if (tail.Length > 2000)
                tail.Remove(0, tail.Length - 2000);
            reportLine?.Invoke(line);
        }
        return tail.ToString();
    }

    internal static void StopProcess(Process process, bool entireProcessTree = true)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree);
        }
        catch (InvalidOperationException)
        {
            // The owned process exited between the check and cancellation.
        }
        catch (Win32Exception ex)
        {
            Trace.TraceWarning($"[Remote] Could not stop owned Dev Tunnel setup process: {ex.Message}");
        }
    }
}
