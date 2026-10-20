using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Lumi.Tests;

/// <summary>
/// Keeps every test away from the developer's real Lumi profile. Lumi resolves each folder it writes
/// (data, chats, skill files, active skills, sessions, the browser profile, connector caches) under
/// <c>LUMI_APPDATA_DIR</c>, and the phone app's pairing file under <c>LUMI_MOBILE_DATA_DIR</c>; this
/// points both at a throwaway folder before any test runs. Without it, a test whose in-memory store
/// synced its skills replaced every skill file in the real <c>%AppData%\Lumi\skills</c>.
/// </summary>
public static class TestProfile
{
    private static readonly string RunsFolder = Path.Combine(Path.GetTempPath(), "Lumi.Tests");

    /// <summary>The folder this run uses in place of the user's app-data folder.</summary>
    public static string Root { get; } = Path.Combine(
        RunsFolder, $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}");

    [ModuleInitializer]
    internal static void Isolate()
    {
        DeleteAbandonedRuns();
        Directory.CreateDirectory(Root);

        // Always replaced, never inherited: a shell still pointing at a debug profile, or at the real
        // one, must not become the test profile.
        Environment.SetEnvironmentVariable("LUMI_APPDATA_DIR", Root);
        Environment.SetEnvironmentVariable("LUMI_MOBILE_DATA_DIR", Path.Combine(Root, "LumiMobile"));
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => TryDelete(Root);
    }

    // A run that crashed, or whose Copilot runtime still held a file at exit, leaves its folder behind.
    private static void DeleteAbandonedRuns()
    {
        if (!Directory.Exists(RunsFolder))
            return;

        foreach (var folder in Directory.EnumerateDirectories(RunsFolder, "run-*"))
        {
            if (Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddDays(-1))
                TryDelete(folder);
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
