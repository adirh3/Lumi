using System.Diagnostics;
using System.Text;
using GitHub.Copilot.Rpc;

namespace Lumi.Services;

internal sealed class CopilotExtensionLaunchProvider : IExtensionLaunchProviderHandler
{
    private const string SdkLoader = """
        import { pathToFileURL } from "node:url";
        import { join } from "node:path";
        let sdkRoot;
        export function initialize(data) {
          if (!data.sdkRoot) throw new Error("Copilot did not supply its extension SDK.");
          sdkRoot = data.sdkRoot;
        }
        export async function resolve(specifier, context, nextResolve) {
          const entry = specifier === "@github/copilot-sdk" ? "index.js"
            : specifier === "@github/copilot-sdk/extension" ? "extension.js" : null;
          return entry ? { url: pathToFileURL(join(sdkRoot, entry)).href, shortCircuit: true }
            : nextResolve(specifier, context);
        }
        """;

    internal static string SdkBootstrapImport
    {
        get
        {
            var loader = "data:text/javascript;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(SdkLoader));
            var bootstrap = "import { register } from 'node:module'; register('" + loader
                + "', { data: { sdkRoot: process.env.COPILOT_SDK_PATH } });";
            return "data:text/javascript;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(bootstrap));
        }
    }

    internal static string? NodePath
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (!OperatingSystem.IsWindows())
                path = UnixShellPath.Augment(path);
            foreach (var directory in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory.Trim('"'), OperatingSystem.IsWindows() ? "node.exe" : "node");
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            return null;
        }
    }

    public Task<ExtensionLaunchProviderResolveResult> ResolveAsync(
        ExtensionLaunchProviderResolveRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(request.ModulePath);
        if (NodePath is { } node && Path.IsPathFullyQualified(request.ModulePath)
            && extension is ".mjs" or ".js" or ".cjs")
        {
            return Task.FromResult(new ExtensionLaunchProviderResolveResult
            {
                // The runtime supplies the SDK path; no SDK installation or writes inside the plugin are needed.
                Launch = new ExtensionLaunchProfile
                {
                    Executable = node,
                    Args = ["--import", SdkBootstrapImport, request.ModulePath],
                    // Extensions look for session state under Copilot's home; Lumi keeps its sessions in its own folder.
                    Env = new Dictionary<string, string> { ["COPILOT_HOME"] = DataStore.CopilotConfigDir },
                }
            });
        }

        Trace.TraceWarning("Canvas extension '{0}' could not be launched. A JavaScript entrypoint and Node.js on PATH are required.", request.Name);
        return Task.FromResult(new ExtensionLaunchProviderResolveResult());
    }
}
