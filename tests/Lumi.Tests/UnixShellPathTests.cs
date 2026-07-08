using System;
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
    public void Augment_OnWindows_IsANoOp()
    {
        // On Windows the augmentation must not alter PATH (GUI processes already have the full PATH).
        if (!OperatingSystem.IsWindows())
            return;

        const string path = @"C:\Windows\System32;C:\tools";
        Assert.Equal(path, UnixShellPath.Augment(path));
        Assert.Equal(string.Empty, UnixShellPath.Augment(null));
    }
}
