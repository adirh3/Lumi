using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Lumi.Mobile.Tests;

/// <summary>
/// Keeps every test away from the real phone-app pairing in <c>%APPDATA%\LumiMobile</c>: a
/// <see cref="Services.MobileSettingsStore"/> built without a folder resolves
/// <c>LUMI_MOBILE_DATA_DIR</c>, which this points at a throwaway folder before any test runs.
/// Tests should still give each shell its own store (<see cref="HeadlessMobileSession.NewStore"/>).
/// </summary>
internal static class TestProfile
{
    internal static string Root { get; } = Path.Combine(
        Path.GetTempPath(), "Lumi.Mobile.Tests", $"profile-{Environment.ProcessId}-{Guid.NewGuid():N}");

    [ModuleInitializer]
    internal static void Isolate()
    {
        Environment.SetEnvironmentVariable("LUMI_MOBILE_DATA_DIR", Root);
        AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        };
    }
}
