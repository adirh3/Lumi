using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Lumi.Services;

/// <summary>
/// Lightweight git operations helper. All methods are static and shell out to git CLI.
/// </summary>
public static class GitService
{
    /// <summary>Returns true if the directory is inside a git repository.</summary>
    public static bool IsGitRepo(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        // Quick check: .git folder exists at root or any parent
        var d = new DirectoryInfo(dir);
        while (d is not null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
                return true;
            d = d.Parent;
        }
        return false;
    }

    /// <summary>Gets the current branch name, or null if not a git repo.</summary>
    public static async Task<string?> GetCurrentBranchAsync(string dir)
    {
        var result = await RunGitAsync(dir, "branch --show-current");
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    /// <summary>Returns the list of changed files (staged + unstaged + untracked) with line stats.</summary>
    public static async Task<List<GitFileChange>> GetChangedFilesAsync(string dir)
    {
        var output = await RunGitAsync(dir, "status --porcelain -uall");
        if (string.IsNullOrWhiteSpace(output)) return [];

        var changes = new List<GitFileChange>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4) continue;
            var status = line[..2];
            var path = line[3..].Trim().Trim('"');

            var kind = status.Trim() switch
            {
                "M" or "MM" => GitChangeKind.Modified,
                "A" or "AM" => GitChangeKind.Added,
                "D" => GitChangeKind.Deleted,
                "R" or "RM" => GitChangeKind.Renamed,
                "??" => GitChangeKind.Untracked,
                _ => GitChangeKind.Modified
            };

            var fullPath = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));

            // Skip worktree sibling directories (they appear as untracked in some configs)
            if (kind == GitChangeKind.Untracked && path.Contains("-wt-"))
                continue;

            changes.Add(new GitFileChange
            {
                RelativePath = path,
                FullPath = fullPath,
                Kind = kind,
                StatusCode = status
            });
        }

        // Enrich with line stats from numstat
        var numstat = await RunGitAsync(dir, "diff --numstat");
        var cachedNumstat = await RunGitAsync(dir, "diff --cached --numstat");
        var statsMap = new Dictionary<string, (int added, int removed)>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in new[] { numstat, cachedNumstat })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            foreach (var sline in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = sline.Split('\t');
                if (parts.Length < 3) continue;
                if (int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var r))
                {
                    var fpath = parts[2];
                    if (statsMap.TryGetValue(fpath, out var existing))
                        statsMap[fpath] = (existing.added + a, existing.removed + r);
                    else
                        statsMap[fpath] = (a, r);
                }
            }
        }
        foreach (var c in changes)
        {
            if (statsMap.TryGetValue(c.RelativePath, out var stats))
            {
                c.LinesAdded = stats.added;
                c.LinesRemoved = stats.removed;
            }
            else if (c.Kind is GitChangeKind.Untracked or GitChangeKind.Added)
            {
                // Untracked/new files don't appear in numstat — count lines directly
                try
                {
                    if (File.Exists(c.FullPath))
                        c.LinesAdded = File.ReadAllLines(c.FullPath).Length;
                }
                catch { /* ignore */ }
            }
        }

        return changes;
    }

    /// <summary>Gets the unified diff for a specific file.</summary>
    public static async Task<string?> GetFileDiffAsync(string dir, string relativePath)
    {
        // Try staged first, then unstaged, then for untracked show the whole file
        var diff = await RunGitAsync(dir, $"diff -- \"{relativePath}\"");
        if (string.IsNullOrWhiteSpace(diff))
            diff = await RunGitAsync(dir, $"diff --cached -- \"{relativePath}\"");
        return string.IsNullOrWhiteSpace(diff) ? null : diff;
    }

    /// <summary>Gets the short stat summary (e.g. "3 files changed, 12 insertions(+), 5 deletions(-)").</summary>
    public static async Task<string?> GetDiffStatAsync(string dir)
    {
        return await RunGitAsync(dir, "diff --stat --stat-width=60");
    }

    /// <summary>Creates a git worktree as a sibling directory to the repo. Returns the worktree path.</summary>
    public static async Task<string?> CreateWorktreeAsync(string repoDir, string branchName)
    {
        // Place worktree as sibling: E:\Git\Lumi → E:\Git\Lumi-worktree-abc123
        var trimmedRepoDir = repoDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repoName = Path.GetFileName(trimmedRepoDir);
        var safeBranch = branchName.Replace('/', '-').Replace('\\', '-');
        var parentDir = Path.GetDirectoryName(trimmedRepoDir);
        if (parentDir is null) return null;

        var worktreePath = Path.Combine(parentDir, $"{repoName}-wt-{safeBranch}");
        if (Directory.Exists(worktreePath))
            return worktreePath; // Already exists

        // Try creating with a new branch first
        var result = await RunGitAsync(repoDir, $"worktree add \"{worktreePath}\" -b \"{branchName}\"");
        if (result is not null && Directory.Exists(worktreePath))
            return worktreePath;

        // Branch may already exist — try attaching to it
        result = await RunGitAsync(repoDir, $"worktree add \"{worktreePath}\" \"{branchName}\"");
        if (result is not null && Directory.Exists(worktreePath))
            return worktreePath;

        // Last resort — create with detached HEAD
        result = await RunGitAsync(repoDir, $"worktree add --detach \"{worktreePath}\"");
        if (result is not null && Directory.Exists(worktreePath))
            return worktreePath;

        return null;
    }

    /// <summary>Removes a git worktree.</summary>
    public static async Task<bool> RemoveWorktreeAsync(string dir, string worktreePath)
    {
        if (!Directory.Exists(worktreePath)) return true;
        var result = await RunGitAsync(dir, $"worktree remove \"{worktreePath}\" --force");
        return result is not null;
    }

    /// <summary>Lists existing worktrees.</summary>
    public static async Task<List<string>> ListWorktreesAsync(string dir)
    {
        var output = await RunGitAsync(dir, "worktree list --porcelain");
        if (string.IsNullOrWhiteSpace(output)) return [];

        return output.Split('\n')
            .Where(l => l.StartsWith("worktree "))
            .Select(l => l[9..].Trim())
            .ToList();
    }

    private static async Task<string?> RunGitAsync(string workDir, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}

public enum GitChangeKind { Modified, Added, Deleted, Renamed, Untracked }

public class GitFileChange
{
    public required string RelativePath { get; init; }
    public required string FullPath { get; init; }
    public required GitChangeKind Kind { get; init; }
    public required string StatusCode { get; init; }
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }

    public string FileName => Path.GetFileName(RelativePath);
    public string? Directory => Path.GetDirectoryName(RelativePath)?.Replace('\\', '/');

    public string KindIcon => Kind switch
    {
        GitChangeKind.Modified => "M",
        GitChangeKind.Added => "A",
        GitChangeKind.Deleted => "D",
        GitChangeKind.Renamed => "R",
        GitChangeKind.Untracked => "U",
        _ => "?"
    };

    public string KindLabel => Kind switch
    {
        GitChangeKind.Modified => "Modified",
        GitChangeKind.Added => "Added",
        GitChangeKind.Deleted => "Deleted",
        GitChangeKind.Renamed => "Renamed",
        GitChangeKind.Untracked => "Untracked",
        _ => "Unknown"
    };
}
