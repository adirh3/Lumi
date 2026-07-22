using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumi.Services;

internal sealed class CrashReportData
{
    public DateTimeOffset TimestampUtc { get; init; }
    public string Source { get; init; } = string.Empty;
    public string ExceptionText { get; init; } = string.Empty;
    public string AppVersion { get; init; } = string.Empty;
    public string RuntimeVersion { get; init; } = string.Empty;
    public string OperatingSystem { get; init; } = string.Empty;
    public string ProcessArchitecture { get; init; } = string.Empty;
    public string Language { get; init; } = "en";
    public bool IsDarkTheme { get; init; } = true;

    public static CrashReportData Create(Exception exception, string source, bool isDarkTheme)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new CrashReportData
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Source = source,
            ExceptionText = exception.ToString(),
            AppVersion = ResolveAppVersion(),
            RuntimeVersion = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Language = CrashReportService.NormalizeLanguage(
                CultureInfo.CurrentUICulture.TwoLetterISOLanguageName),
            IsDarkTheme = isDarkTheme,
        };
    }

    public string BuildDiagnosticText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Lumi crash report");
        builder.AppendLine();
        builder.AppendLine($"Time (UTC): {TimestampUtc:O}");
        builder.AppendLine($"Source: {Source}");
        builder.AppendLine($"Lumi version: {AppVersion}");
        builder.AppendLine($"Runtime: {RuntimeVersion}");
        builder.AppendLine($"Operating system: {OperatingSystem}");
        builder.AppendLine($"Process architecture: {ProcessArchitecture}");
        builder.AppendLine();
        builder.AppendLine("Exception:");
        builder.Append(ExceptionText);
        return builder.ToString();
    }

    private static string ResolveAppVersion()
    {
        var assembly = typeof(CrashReportData).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }
}

internal static class CrashReportService
{
    public const string SupportEmail = "support@fluentsearch.net";
    public const string CrashReportArgument = "--crash-report";
#if DEBUG
    public const string DebugCrashReportArgument = "--debug-crash-report";
#endif

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static bool TryGetHandoffPath(IReadOnlyList<string> args, out string? path)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], CrashReportArgument, StringComparison.OrdinalIgnoreCase))
                continue;

            path = i + 1 < args.Count ? args[i + 1] : null;
            return true;
        }

        path = null;
        return false;
    }

    public static bool TryLaunchReporter(CrashReportData report, out string error)
    {
        try
        {
            var reportPath = WriteHandoffReport(report);
            var startInfo = LumiProcessLauncher.CreateStartInfo([CrashReportArgument, reportPath]);
            _ = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The crash reporter process did not start.");
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException
                                   or NotSupportedException
                                   or InvalidOperationException
                                   or Win32Exception
                                   or ArgumentException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryLoadHandoffReport(string path, out CrashReportData? report, out string error)
    {
        try
        {
            var json = File.ReadAllText(path, Utf8NoBom);
            report = DeserializeReport(json)
                ?? throw new InvalidDataException("The crash report file was empty.");
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException
                                   or NotSupportedException
                                   or InvalidDataException
                                   or ArgumentException)
        {
            report = null;
            error = ex.Message;
            return false;
        }
    }

    public static void TryDeleteHandoffReport(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Trace.TraceWarning($"[CrashReporter] Could not delete handoff report '{path}': {ex.Message}");
        }
    }

    public static Uri CreateFeedbackEmailUri(CrashReportData report)
    {
        var subject = $"Lumi crash report - v{report.AppVersion}";
        var body = report.BuildDiagnosticText();
        return new Uri(
            $"mailto:{SupportEmail}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}");
    }

    public static bool TryOpenFeedbackEmail(CrashReportData report, out string error)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = CreateFeedbackEmailUri(report).AbsoluteUri,
                UseShellExecute = true,
            });
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or Win32Exception
                                   or NotSupportedException
                                   or ArgumentException)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static string SerializeReport(CrashReportData report)
        => JsonSerializer.Serialize(report, CrashReportJsonContext.Default.CrashReportData);

    internal static CrashReportData? DeserializeReport(string json)
        => JsonSerializer.Deserialize(json, CrashReportJsonContext.Default.CrashReportData);

    internal static string NormalizeLanguage(string? language)
        => string.Equals(language, "he", StringComparison.OrdinalIgnoreCase) ? "he" : "en";

    private static string WriteHandoffReport(CrashReportData report)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lumi", "CrashReports");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(
            directory,
            $"crash-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, SerializeReport(report), Utf8NoBom);
        return path;
    }
}

internal static class LumiProcessLauncher
{
    public static ProcessStartInfo CreateStartInfo(
        IEnumerable<string>? arguments = null,
        string? processPath = null,
        string? entryAssemblyPath = null,
        string? workingDirectory = null)
    {
        var resolvedProcessPath = processPath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(resolvedProcessPath))
            throw new InvalidOperationException("Lumi's executable path is unavailable.");

        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedProcessPath,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (IsDotNetHost(resolvedProcessPath))
        {
            var resolvedAssemblyPath = entryAssemblyPath ?? Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(resolvedAssemblyPath))
                throw new InvalidOperationException("Lumi's managed entry assembly path is unavailable.");
            startInfo.ArgumentList.Add(resolvedAssemblyPath);
        }

        if (arguments is not null)
        {
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    public static bool TryLaunch(out string error)
    {
        try
        {
            _ = Process.Start(CreateStartInfo())
                ?? throw new InvalidOperationException("The new Lumi process did not start.");
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or Win32Exception
                                   or NotSupportedException
                                   or ArgumentException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsDotNetHost(string processPath)
        => string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(CrashReportData))]
internal partial class CrashReportJsonContext : JsonSerializerContext;
