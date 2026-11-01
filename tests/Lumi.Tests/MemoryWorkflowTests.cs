using Xunit;

namespace Lumi.Tests;

public sealed class MemoryWorkflowTests
{
    [Fact]
    public void MemoryQaIsScheduledManualOrValidationOnlyAndDoesNotBecomeAReleaseOrMergeDependency()
    {
        var root = RepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "memory-qa.yml"));
        Assert.Contains("workflow_dispatch:", workflow);
        Assert.Contains("schedule:", workflow);
        Assert.DoesNotContain("pull_request:", workflow);
        Assert.DoesNotContain("workflow_call:", workflow);
        Assert.Contains("validation/memory-tools-fast", workflow);
        Assert.DoesNotContain("- main", workflow);
        Assert.DoesNotContain("continue-on-error", string.Join('\n', workflow.Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#'))));
        foreach (var file in new[] { "release.yml", "pr-validation.yml", "browser-native-validation.yml" })
            Assert.DoesNotContain("memory-qa", File.ReadAllText(Path.Combine(root, ".github", "workflows", file)));
    }

    [Fact]
    public void HostedMemoryQaUsesExplicitCoarsePolicyWithoutPrivateBaselines()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", "memory-qa.yml"));
        Assert.Contains("runs-on: windows-latest", workflow);
        Assert.Contains("Test-LumiMemory.ps1", workflow);
        Assert.Contains("-Mode Fast", workflow);
        Assert.Contains("-Policy CI", workflow);
        Assert.DoesNotContain("-ProfilePath", workflow);
        Assert.DoesNotContain("-SnapshotPath", workflow);
        Assert.DoesNotContain("-BaselinePath", workflow);
        Assert.DoesNotContain("-MaxRamMiB", workflow);
        Assert.DoesNotContain("-MaxPrivateMiB", workflow);
        Assert.Contains("if-no-files-found: error", workflow);
        Assert.Contains("INCOMPLETE:", workflow);
        Assert.DoesNotContain("profile/lumi.db", workflow, StringComparison.OrdinalIgnoreCase);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Lumi.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("The Lumi repository root was not found.");
    }
}
