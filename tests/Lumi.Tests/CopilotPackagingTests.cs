using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Lumi.Tests;

public sealed class CopilotPackagingTests
{
    [Fact]
    public async Task PackageProvidesOneFullCliForSdkAndLogin()
    {
        var native = Path.Combine(AppContext.BaseDirectory, "runtimes",
            RuntimeInformation.RuntimeIdentifier, "native");
        var cli = Path.Combine(native, OperatingSystem.IsWindows() ? "copilot.exe" : "copilot");
        Assert.True(File.Exists(cli), cli);
        Assert.Equal("explicit", File.ReadAllText(Path.Combine(native, ".copilot-explicit-cli")).Trim());
        foreach (var name in new[] { "copilot-runtime", "copilot-runtime.exe", "runtime.node",
                     "copilot_runtime.dll", "libcopilot_runtime.so", "libcopilot_runtime.dylib" })
            Assert.False(File.Exists(Path.Combine(native, name)), $"Unexpected second runtime: {name}");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var info = new ProcessStartInfo(cli)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("--no-auto-update");
        info.ArgumentList.Add("login");
        info.ArgumentList.Add("--help");
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var help = await output + await error;

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("--web-flow", help);
        Assert.Contains("--device-code", help);
    }
}
