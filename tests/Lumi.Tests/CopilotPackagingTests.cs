using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Copilot;
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
    public async Task TheSameBundledCliSupportsProtocolFourAndLogin()
    {
        var native = Path.Combine(AppContext.BaseDirectory, "runtimes",
            RuntimeInformation.RuntimeIdentifier, "native");
        var binaryExtension = OperatingSystem.IsWindows() ? ".exe" : "";
        var cli = Path.Combine(native, "copilot" + binaryExtension);
        Assert.Equal(cli, TestCopilot.BundledRuntimePath);
        Assert.True(File.Exists(Path.Combine(native, ".copilot-explicit-cli")));
        Assert.False(File.Exists(Path.Combine(native, "copilot-runtime" + binaryExtension)));
        Assert.False(File.Exists(Path.Combine(native, "copilot-full" + binaryExtension)));
        Assert.False(File.Exists(Path.Combine(native, "runtime.node")));
        var runtimeLibrary = OperatingSystem.IsWindows() ? "copilot_runtime.dll"
            : OperatingSystem.IsMacOS() ? "libcopilot_runtime.dylib" : "libcopilot_runtime.so";
        Assert.False(File.Exists(Path.Combine(native, runtimeLibrary)));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var runtime = RuntimeConnection.ForSpawnedStdio(cli, ["--no-auto-update"]);
        runtime.BaseDirectory = Path.Combine(TestProfile.Root, "packaging-copilot-home");
        Directory.CreateDirectory(runtime.BaseDirectory);
        await using var client = new CopilotClient(new CopilotClientOptions
        {
            Mode = CopilotClientMode.CodingAgent,
            Runtime = runtime,
            GitHub = new() { UseLoggedInUser = false }
        });
        await client.StartAsync(timeout.Token);
        Assert.Equal(4, (await client.GetStatusAsync(timeout.Token)).ProtocolVersion);

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
