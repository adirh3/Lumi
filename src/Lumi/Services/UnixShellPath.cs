using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Lumi.Services;

/// <summary>
/// Builds an augmented <c>PATH</c> for child processes on macOS/Linux.
/// <para>
/// GUI apps launched from Finder/launchd (macOS) or some desktop launchers (Linux) inherit a
/// minimal <c>PATH</c> (roughly <c>/usr/bin:/bin:/usr/sbin:/sbin</c>) that omits Homebrew
/// (<c>/opt/homebrew/bin</c>, <c>/usr/local/bin</c>) and version managers (nvm/fnm/asdf). Lumi passes
/// its own environment to the bundled Copilot CLI, which then spawns MCP servers and shell tools — so
/// without this augmentation, common MCP commands like <c>npx</c>/<c>node</c>/<c>uvx</c> (and many
/// user tools) are "command not found" when Lumi is opened from the GUI.
/// </para>
/// <para>
/// This unions the current <c>PATH</c> with (1) the user's real login+interactive shell <c>PATH</c>
/// (best-effort, captures nvm/fnm/Homebrew shellenv) and (2) well-known tool directories as a
/// fallback. The shell probe is cached and time-boxed so it never hangs startup. No-op on Windows,
/// where GUI processes already inherit the full user <c>PATH</c>.
/// </para>
/// </summary>
internal static class UnixShellPath
{
    private static readonly object Gate = new();
    private static string? _cachedLoginShellPath;
    private static bool _loginShellProbed;

    private const string PathStartMarker = "__LUMI_PATH_START__";
    private const string PathEndMarker = "__LUMI_PATH_END__";

    /// <summary>
    /// Returns <paramref name="currentPath"/> augmented with the login-shell PATH and common tool
    /// directories. On Windows returns <paramref name="currentPath"/> unchanged.
    /// </summary>
    public static string Augment(string? currentPath)
    {
        if (OperatingSystem.IsWindows())
            return currentPath ?? string.Empty;

        return Combine(GetLoginShellPath(), currentPath, GetCommonToolDirectories());
    }

    /// <summary>
    /// Applies the augmented PATH (see <see cref="Augment"/>) to a child process's environment so a
    /// command Lumi spawns directly resolves the same tools the user has in their terminal (Homebrew,
    /// nvm, etc.). No-op on Windows, where GUI processes already inherit the full user PATH. Use for
    /// PATH-resolved commands Lumi launches itself (background jobs, git, "open in IDE") — not needed
    /// for commands routed through the Copilot CLI, whose environment is already augmented.
    /// </summary>
    public static void ApplyTo(ProcessStartInfo startInfo)
    {
        if (OperatingSystem.IsWindows())
            return;

        var current = startInfo.Environment.TryGetValue("PATH", out var existing) && !string.IsNullOrEmpty(existing)
            ? existing
            : Environment.GetEnvironmentVariable("PATH");
        startInfo.Environment["PATH"] = Augment(current);
    }

    /// <summary>
    /// Pure union of PATH sources, order-preserving and de-duplicated: login-shell entries first, then
    /// the current PATH, then the extra fallback directories. Exposed for unit testing (the OS-gated
    /// <see cref="Augment"/> feeds it the live values).
    /// </summary>
    internal static string Combine(string? loginShellPath, string? currentPath, IEnumerable<string> extraDirectories)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddEntries(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return;
            foreach (var raw in value.Split(':'))
            {
                var entry = raw.Trim();
                if (entry.Length > 0 && seen.Add(entry))
                    ordered.Add(entry);
            }
        }

        AddEntries(loginShellPath);
        AddEntries(currentPath);
        foreach (var dir in extraDirectories)
        {
            if (!string.IsNullOrWhiteSpace(dir) && seen.Add(dir))
                ordered.Add(dir);
        }

        return string.Join(':', ordered);
    }

    private static string? GetLoginShellPath()
    {
        lock (Gate)
        {
            if (_loginShellProbed)
                return _cachedLoginShellPath;

            _loginShellProbed = true;
            _cachedLoginShellPath = ProbeLoginShellPath();
            return _cachedLoginShellPath;
        }
    }

    private static string? ProbeLoginShellPath()
    {
        try
        {
            var shell = Environment.GetEnvironmentVariable("SHELL");
            if (string.IsNullOrWhiteSpace(shell) || !File.Exists(shell))
                shell = OperatingSystem.IsMacOS() ? "/bin/zsh" : "/bin/bash";

            // Login (-l) sources profile files (Homebrew shellenv); interactive (-i) sources rc files
            // (nvm/fnm). Markers delimit the value so noisy rc banners are ignored.
            var psi = new ProcessStartInfo
            {
                FileName = shell,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-lic");
            psi.ArgumentList.Add($"printf '{PathStartMarker}%s{PathEndMarker}' \"$PATH\"");

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            // Ensure the interactive shell never blocks on stdin.
            try { process.StandardInput.Close(); } catch { /* best-effort */ }

            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(4000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return null;
            }

            var start = stdout.IndexOf(PathStartMarker, StringComparison.Ordinal);
            var end = stdout.IndexOf(PathEndMarker, StringComparison.Ordinal);
            if (start < 0 || end < 0 || end <= start)
                return null;

            start += PathStartMarker.Length;
            var value = stdout[start..end].Trim();
            return value.Contains('/') ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> GetCommonToolDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>();

        if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/opt/homebrew/bin");
            candidates.Add("/opt/homebrew/sbin");
        }

        candidates.Add("/usr/local/bin");
        candidates.Add("/usr/local/sbin");

        if (!string.IsNullOrEmpty(home))
        {
            candidates.Add(Path.Combine(home, ".local", "bin"));
            candidates.Add(Path.Combine(home, ".cargo", "bin"));
            candidates.Add(Path.Combine(home, ".deno", "bin"));
            candidates.Add(Path.Combine(home, ".bun", "bin"));
        }

        if (OperatingSystem.IsLinux())
            candidates.Add("/snap/bin");

        return candidates.Where(Directory.Exists);
    }
}
