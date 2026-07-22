using System;
using System.Diagnostics;
using System.IO;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class UnixShellPathTests
{
    [Fact]
    public void Combine_CurrentPathComesFirst_ThenLoginShell_ThenExtras()
    {
        var result = UnixShellPath.Combine(
            loginShellPath: "/opt/homebrew/bin:/usr/bin",
            currentPath: "/usr/bin:/bin",
            extraDirectories: ["/usr/local/bin"]);

        // Current PATH first (purely additive), then login-shell-only entries, then fallback dirs.
        Assert.Equal("/usr/bin:/bin:/opt/homebrew/bin:/usr/local/bin", result);
    }

    [Fact]
    public void Combine_IsAdditive_NeverShadowsAnEntryAlreadyInCurrentPath()
    {
        // A directory the user intentionally put first in PATH (e.g. a venv or direnv shim) must stay
        // first — the login-shell PATH may only append directories that were otherwise missing, so a
        // command that already resolves keeps resolving to the same binary.
        var result = UnixShellPath.Combine(
            loginShellPath: "/opt/homebrew/bin:/usr/bin:/bin",
            currentPath: "/my/venv/bin:/usr/bin",
            extraDirectories: []);

        Assert.Equal("/my/venv/bin:/usr/bin:/opt/homebrew/bin:/bin", result);
    }

    [Fact]
    public void Combine_DeduplicatesAcrossAllSources_PreservingFirstOccurrence()
    {
        var result = UnixShellPath.Combine(
            loginShellPath: "/opt/homebrew/bin:/usr/bin",
            currentPath: "/usr/bin:/opt/homebrew/bin:/bin",
            extraDirectories: ["/usr/bin", "/snap/bin"]);

        Assert.Equal("/usr/bin:/opt/homebrew/bin:/bin:/snap/bin", result);
    }

    [Fact]
    public void Combine_HandlesNullAndEmptySources()
    {
        Assert.Equal("/usr/bin", UnixShellPath.Combine(null, "/usr/bin", []));
        Assert.Equal("/usr/bin", UnixShellPath.Combine("", "/usr/bin", []));
        Assert.Equal("/opt/homebrew/bin", UnixShellPath.Combine("/opt/homebrew/bin", null, []));
        Assert.Equal(string.Empty, UnixShellPath.Combine(null, null, []));
    }

    [Fact]
    public void Combine_TrimsWhitespaceAndSkipsBlankEntries()
    {
        var result = UnixShellPath.Combine(
            loginShellPath: " /opt/homebrew/bin : : /usr/bin ",
            currentPath: null,
            extraDirectories: ["  ", "/snap/bin"]);

        Assert.Equal("/opt/homebrew/bin:/usr/bin:/snap/bin", result);
    }

    [Fact]
    public void Combine_PreservesEmptyEntriesFromCurrentPath()
    {
        var result = UnixShellPath.Combine(
            loginShellPath: "/usr/bin",
            currentPath: "./node_modules/.bin::/bin",
            extraDirectories: []);

        Assert.Equal("./node_modules/.bin::/bin:/usr/bin", result);
    }

    [Fact]
    public void Augment_OnWindows_IsANoOp()
    {
        // On Windows the augmentation must not alter PATH (GUI processes already have the full PATH).
        if (!OperatingSystem.IsWindows())
            return;

        const string path = @"C:\Windows\System32;C:\tools";
        Assert.Equal(path, UnixShellPath.Augment(path));
        Assert.Equal(string.Empty, UnixShellPath.Augment(null));
    }

    [Fact]
    public void ApplyTo_OnWindows_DoesNotChangeExecutable()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var startInfo = new ProcessStartInfo { FileName = "git" };

        UnixShellPath.ApplyTo(startInfo);

        Assert.Equal("git", startInfo.FileName);
    }

    [Fact]
    public void ApplyTo_OnUnix_ResolvesBareExecutableAgainstChildPath()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), "lumi-path-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executablePath = Path.Combine(root, "lumi-fake-tool");
            File.WriteAllText(executablePath, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(
                executablePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var startInfo = new ProcessStartInfo
            {
                FileName = "lumi-fake-tool",
                UseShellExecute = false
            };
            startInfo.Environment["PATH"] = root;

            UnixShellPath.ApplyTo(startInfo);

            Assert.Equal(Path.GetFullPath(executablePath), startInfo.FileName);
            Assert.StartsWith(root, startInfo.Environment["PATH"], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveExecutable_OnUnix_SkipsNonExecutableFiles()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), "lumi-path-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var candidatePath = Path.Combine(root, "lumi-fake-tool");
            File.WriteAllText(candidatePath, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(candidatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            Assert.Equal(
                "lumi-fake-tool",
                UnixShellPath.ResolveExecutable("lumi-fake-tool", root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveExecutable_OnUnix_UsesChildWorkingDirectoryForRelativePathEntries()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), "lumi-path-test-" + Guid.NewGuid().ToString("N"));
        var workDir = Path.Combine(root, "work");
        var binDir = Path.Combine(workDir, "node_modules", ".bin");
        Directory.CreateDirectory(binDir);
        try
        {
            var executablePath = Path.Combine(binDir, "lumi-fake-tool");
            File.WriteAllText(executablePath, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(
                executablePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            Assert.Equal(
                Path.GetFullPath(executablePath),
                UnixShellPath.ResolveExecutable(
                    "lumi-fake-tool",
                    "./node_modules/.bin",
                    workDir));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveExecutable_OnUnix_UsesChildWorkingDirectoryForEmptyPathEntries()
    {
        if (OperatingSystem.IsWindows())
            return;

        var workDir = Path.Combine(Path.GetTempPath(), "lumi-path-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var executablePath = Path.Combine(workDir, "lumi-fake-tool");
            File.WriteAllText(executablePath, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(
                executablePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            Assert.Equal(
                Path.GetFullPath(executablePath),
                UnixShellPath.ResolveExecutable("lumi-fake-tool", ":/usr/bin", workDir));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
