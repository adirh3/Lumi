using System;
using System.Linq;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class CrashReportServiceTests
{
    [Fact]
    public void Create_PreservesCompleteExceptionText()
    {
        var exception = CaptureNestedException();

        var report = CrashReportData.Create(exception, "test", isDarkTheme: true);

        Assert.Equal(exception.ToString(), report.ExceptionText);
        Assert.Contains("outer crash", report.BuildDiagnosticText());
        Assert.Contains("inner crash", report.BuildDiagnosticText());
        Assert.Contains(nameof(ThrowInnerException), report.BuildDiagnosticText());
    }

    [Fact]
    public void FeedbackEmail_TargetsSupportAndContainsCompleteReport()
    {
        var report = CrashReportData.Create(
            CaptureNestedException(),
            "test",
            isDarkTheme: true);

        var uri = CrashReportService.CreateFeedbackEmailUri(report);
        var decodedUri = Uri.UnescapeDataString(uri.OriginalString);

        Assert.Equal("mailto", uri.Scheme);
        Assert.StartsWith(
            $"mailto:{CrashReportService.SupportEmail}?",
            uri.OriginalString,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(report.BuildDiagnosticText(), decodedUri);
    }

    [Fact]
    public void Serialization_RoundTripsCompleteReport()
    {
        var original = CrashReportData.Create(
            CaptureNestedException(),
            "serialization test",
            isDarkTheme: false);

        var restored = CrashReportService.DeserializeReport(
            CrashReportService.SerializeReport(original));

        Assert.NotNull(restored);
        Assert.Equal(original.ExceptionText, restored.ExceptionText);
        Assert.Equal(original.Source, restored.Source);
        Assert.Equal(original.IsDarkTheme, restored.IsDarkTheme);
        Assert.Equal(original.Language, restored.Language);
    }

    [Fact]
    public void CreateStartInfo_DotNetHostAddsEntryAssemblyBeforeArguments()
    {
        var startInfo = LumiProcessLauncher.CreateStartInfo(
            ["--crash-report", "report file.json"],
            processPath: "dotnet",
            entryAssemblyPath: @"C:\Lumi\Lumi.dll",
            workingDirectory: @"C:\Lumi");

        Assert.Equal("dotnet", startInfo.FileName);
        Assert.Equal(
            [@"C:\Lumi\Lumi.dll", "--crash-report", "report file.json"],
            startInfo.ArgumentList.ToArray());
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public void CreateStartInfo_AppHostUsesArgumentsDirectly()
    {
        var startInfo = LumiProcessLauncher.CreateStartInfo(
            ["--crash-report", "report.json"],
            processPath: "Lumi.exe",
            entryAssemblyPath: @"C:\Lumi\Lumi.dll");

        Assert.Equal("Lumi.exe", startInfo.FileName);
        Assert.Equal(
            ["--crash-report", "report.json"],
            startInfo.ArgumentList.ToArray());
    }

    [Theory]
    [InlineData(new[] { "--crash-report", "report.json" }, true, "report.json")]
    [InlineData(new[] { "--CRASH-REPORT", "report.json" }, true, "report.json")]
    [InlineData(new[] { "--crash-report" }, true, null)]
    [InlineData(new[] { "--other" }, false, null)]
    public void TryGetHandoffPath_ParsesCrashReporterArguments(
        string[] args,
        bool expectedResult,
        string? expectedPath)
    {
        var result = CrashReportService.TryGetHandoffPath(args, out var path);

        Assert.Equal(expectedResult, result);
        Assert.Equal(expectedPath, path);
    }

    private static Exception CaptureNestedException()
    {
        try
        {
            try
            {
                ThrowInnerException();
            }
            catch (Exception inner)
            {
                throw new InvalidOperationException("outer crash", inner);
            }
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected the nested exception to be thrown.");
    }

    private static void ThrowInnerException()
        => throw new ArgumentException("inner crash");
}
