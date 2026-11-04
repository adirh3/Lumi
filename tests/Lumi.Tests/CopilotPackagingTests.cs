using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using GitHub.Copilot;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class CopilotPackagingTests
{
    [Fact]
    public void ApplicationDoesNotReferenceTheLegacyRpcStack()
        => Assert.DoesNotContain(typeof(CopilotService).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name == "StreamJsonRpc");

    [Fact]
    public void SdkConnectionKeepsTheProcessExitSignalAndOwnsItsRpcTransport()
    {
        var connectionTask = typeof(CopilotClient).GetField("_connectionTask",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(connectionTask);
        var connection = Assert.Single(connectionTask.FieldType.GenericTypeArguments);
        var process = connection.GetProperty("CliProcess");
        Assert.NotNull(process);
        Assert.Equal(typeof(Process), process.PropertyType);
        var rpc = connection.GetProperty("Rpc");
        Assert.NotNull(rpc);
        Assert.Equal(typeof(CopilotClient).Assembly, rpc.PropertyType.Assembly);
    }

    [Fact]
    public void ApplicationDependencyManifestKeepsNativeIntegrationsWithoutWpfWrappers()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Lumi.deps.json")));
        var libraries = manifest.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(library => library.Name).ToArray();
        Assert.DoesNotContain(libraries, name => name.StartsWith("StreamJsonRpc/", StringComparison.Ordinal));
        Assert.DoesNotContain(libraries, name => name.StartsWith("Microsoft.VisualStudio.Threading.Only/", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
            return;

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in manifest.RootElement.GetProperty("targets").EnumerateObject())
        foreach (var library in target.Value.EnumerateObject())
        foreach (var kind in new[] { "runtime", "runtimeTargets", "native" })
        {
            if (!library.Value.TryGetProperty(kind, out var assets))
                continue;
            foreach (var asset in assets.EnumerateObject())
                files.Add(Path.GetFileName(asset.Name.Replace('/', Path.DirectorySeparatorChar)));
        }
        Assert.Contains("Microsoft.Web.WebView2.Core.dll", files);
        Assert.Contains("WebView2Loader.dll", files);
        Assert.Contains("FlaUI.Core.dll", files);
        foreach (var unused in new[] { "Microsoft.Web.WebView2.Wpf.dll", "Microsoft.Web.WebView2.WinForms.dll",
                     "PresentationFramework.dll", "PresentationCore.dll", "System.Windows.Forms.dll",
                     "System.Windows.Forms.Primitives.dll", "System.Windows.Forms.Design.dll" })
            Assert.DoesNotContain(unused, files);
    }

    [Fact]
    public void WindowsAutomationUsesThePublishedNetStandardFlaUIAsset()
    {
#if WINDOWS
        var core = typeof(FlaUI.Core.AutomationBase).Assembly;
        Assert.Equal(".NETStandard,Version=v2.0",
            core.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()!.FrameworkName);
        Assert.DoesNotContain(core.GetReferencedAssemblies(), assembly => assembly.Name == "System.Windows.Forms");
        Assert.Null(core.GetType("FlaUI.Core.Overlay.WinFormsOverlayManager"));
        Assert.NotNull(core.GetType("FlaUI.Core.Overlay.NullOverlayManager"));
#endif
    }

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
