using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Lumi.Services;

/// <summary>
/// Lightweight git operations helper. All methods are static and shell out to git CLI.
/// </summary>
public static class GitService
{
    private static readonly TimeSpan DefaultGitCommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WorktreeGitCommandTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TimedOutGitCleanupTimeout = TimeSpan.FromSeconds(5);

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

    /// <summary>
    /// Walks up from <paramref name="dir"/> to find the repository root — the directory that
    /// contains a <c>.git</c> folder (normal checkout) or a <c>.git</c> file (linked worktree /
    /// submodule). Returns <c>null</c> when the path is not inside a git repository. This is the
    /// synchronous counterpart to <c>git rev-parse --show-toplevel</c> and is safe to call on hot
    /// paths because it only stats a handful of parent directories.
    /// </summary>
    public static string? FindRepoRoot(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            return null;

        DirectoryInfo? d;
        try
        {
            d = new DirectoryInfo(dir);
        }
        catch
        {
            return null;
        }

        while (d is not null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
                return d.FullName;
            d = d.Parent;
        }

        return null;
    }

    /// <summary>
    /// Maps a project working directory into the equivalent location inside a worktree. A git
    /// worktree mirrors the whole repository tree, so when the project working directory is a
    /// subfolder of the repo (e.g. <c>apps/web</c>), the effective directory inside the worktree
    /// is that same subpath under the worktree root (<c>&lt;worktreeRoot&gt;/apps/web</c>). This keeps
    /// <c>.github</c> context, skills/agents discovery, MCP config, and the SDK working directory
    /// resolving exactly as they do in local mode. Falls back to the worktree root when the project
    /// directory is the repo root, when no mapping can be determined, or when the mapped path does
    /// not exist on disk.
    /// </summary>
    public static string ResolveWorktreeWorkingDirectory(string worktreeRoot, string? projectDir)
    {
        if (string.IsNullOrWhiteSpace(worktreeRoot) || string.IsNullOrWhiteSpace(projectDir))
            return worktreeRoot;

        var gitRoot = FindRepoRoot(projectDir);
        if (gitRoot is null)
            return worktreeRoot;

        string relative;
        try
        {
            relative = Path.GetRelativePath(gitRoot, projectDir);
        }
        catch
        {
            return worktreeRoot;
        }

        // Project dir == git root, the relative path escapes the repo, or it is absolute:
        // there is no meaningful subpath to map, so the worktree root is the effective dir.
        if (string.IsNullOrEmpty(relative)
            || relative == "."
            || relative.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            return worktreeRoot;
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(worktreeRoot, relative));
        }
        catch
        {
            return worktreeRoot;
        }

        return Directory.Exists(candidate) ? candidate : worktreeRoot;
    }

    /// <summary>Gets the current branch name, or null if not a git repo.</summary>
    public static async Task<string?> GetCurrentBranchAsync(string dir)
    {
        var result = NormalizeBranchName(await RunGitAsync(dir, "branch --show-current").ConfigureAwait(false));
        if (result is not null)
            return result;

        result = NormalizeBranchName(await RunGitAsync(dir, "rev-parse --abbrev-ref HEAD").ConfigureAwait(false));
        if (result is not null)
            return result;

        result = ParseStatusBranch(await RunGitAsync(dir, "status --short --branch").ConfigureAwait(false));
        if (result is not null)
            return result;

        var shortSha = (await RunGitAsync(dir, "rev-parse --short HEAD").ConfigureAwait(false))?.Trim();
        return string.IsNullOrWhiteSpace(shortSha) ? null : $"Detached {shortSha}";
    }

    /// <summary>
    /// Detects the repository's default branch. The remote HEAD is authoritative when available;
    /// repositories without one fall back to remote or local <c>main</c>, then <c>master</c>.
    /// </summary>
    public static async Task<GitDefaultBranchInfo?> GetDefaultBranchInfoAsync(
        string dir,
        CancellationToken cancellationToken = default)
    {
        var gitRoot = FindRepoRoot(dir);
        if (gitRoot is null)
            return null;

        var remotes = ParseLines(await RunGitAsync(
                gitRoot,
                "remote",
                cancellationToken: cancellationToken).ConfigureAwait(false))
            .OrderBy(static remote => string.Equals(remote, "origin", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(static remote => remote, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var remote in remotes)
        {
            var remoteHeadRef = $"refs/remotes/{remote}/HEAD";
            var symbolic = NormalizeBranchName(await RunGitAsync(
                gitRoot,
                $"symbolic-ref --quiet --short {QuoteGitArgument(remoteHeadRef)}",
                cancellationToken: cancellationToken).ConfigureAwait(false));
            var prefix = remote + "/";
            if (symbolic is null || !symbolic.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var branchName = symbolic[prefix.Length..];
            if (await RefExistsAsync(
                    gitRoot,
                    $"refs/remotes/{remote}/{branchName}",
                    cancellationToken).ConfigureAwait(false))
                return new GitDefaultBranchInfo(branchName, remote);
        }

        foreach (var candidate in new[] { "main", "master" })
        {
            foreach (var remote in remotes)
            {
                if (await RefExistsAsync(
                        gitRoot,
                        $"refs/remotes/{remote}/{candidate}",
                        cancellationToken).ConfigureAwait(false))
                    return new GitDefaultBranchInfo(candidate, remote);
            }
        }

        foreach (var candidate in new[] { "main", "master" })
        {
            if (await RefExistsAsync(
                    gitRoot,
                    $"refs/heads/{candidate}",
                    cancellationToken).ConfigureAwait(false))
                return new GitDefaultBranchInfo(candidate, null);
        }

        return null;
    }

    /// <summary>
    /// Fetches and safely synchronizes the detected default branch. A checked-out branch is updated
    /// only when its worktree is clean and a fast-forward is possible; an unmounted local branch is
    /// moved only when it has no commits that would be discarded.
    /// </summary>
    public static async Task<GitBranchSyncResult> SyncDefaultBranchAsync(
        string dir,
        CancellationToken cancellationToken = default)
    {
        var gitRoot = FindRepoRoot(dir);
        if (gitRoot is null)
            return new GitBranchSyncResult(false, null, false, "The project is not inside a git repository.");

        var defaultBranch = await GetDefaultBranchInfoAsync(gitRoot, cancellationToken).ConfigureAwait(false);
        if (defaultBranch is null)
            return new GitBranchSyncResult(false, null, false, "Could not detect the repository default branch.");
        if (string.IsNullOrWhiteSpace(defaultBranch.RemoteName))
        {
            return new GitBranchSyncResult(
                false,
                defaultBranch.BranchName,
                false,
                $"Default branch \"{defaultBranch.BranchName}\" has no detected remote.");
        }

        var remote = defaultBranch.RemoteName;
        var branch = defaultBranch.BranchName;
        var remoteRef = $"refs/remotes/{remote}/{branch}";
        var fetchRefSpec = $"+refs/heads/{branch}:{remoteRef}";
        var fetchResult = await RunGitAsync(
            gitRoot,
            $"fetch --prune {QuoteGitArgument(remote)} {QuoteGitArgument(fetchRefSpec)}",
            WorktreeGitCommandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (fetchResult is null)
        {
            return new GitBranchSyncResult(
                false,
                branch,
                false,
                $"Failed to fetch \"{remote}/{branch}\".");
        }

        var remoteCommit = await ResolveCommitAsync(gitRoot, remoteRef, cancellationToken).ConfigureAwait(false);
        if (remoteCommit is null)
        {
            return new GitBranchSyncResult(
                false,
                branch,
                false,
                $"Fetched \"{remote}/{branch}\", but its commit could not be resolved.");
        }

        var localRef = $"refs/heads/{branch}";
        var localCommit = await ResolveCommitAsync(gitRoot, localRef, cancellationToken).ConfigureAwait(false);
        if (localCommit is null)
        {
            var createResult = await RunGitAsync(
                gitRoot,
                $"branch --force {QuoteGitArgument(branch)} {QuoteGitArgument(remoteRef)}",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return createResult is null
                ? new GitBranchSyncResult(false, branch, false, $"Could not create local branch \"{branch}\" from \"{remote}/{branch}\".")
                : new GitBranchSyncResult(true, branch, true, $"Created local branch \"{branch}\" from \"{remote}/{branch}\".");
        }

        if (string.Equals(localCommit, remoteCommit, StringComparison.OrdinalIgnoreCase))
            return new GitBranchSyncResult(true, branch, false, $"Branch \"{branch}\" is already up to date.");

        var aheadBehind = await GetAheadBehindAsync(
            gitRoot,
            localRef,
            remoteRef,
            cancellationToken).ConfigureAwait(false);
        if (aheadBehind is null)
            return new GitBranchSyncResult(false, branch, false, $"Could not compare \"{branch}\" with \"{remote}/{branch}\".");

        if (aheadBehind.Value.RemoteOnly == 0)
        {
            return new GitBranchSyncResult(
                true,
                branch,
                false,
                $"Branch \"{branch}\" already contains the latest \"{remote}/{branch}\" commits.");
        }

        if (aheadBehind.Value.LocalOnly > 0)
        {
            return new GitBranchSyncResult(
                false,
                branch,
                false,
                $"Branch \"{branch}\" has diverged from \"{remote}/{branch}\"; automatic sync was skipped.");
        }

        var checkedOutWorktree = (await ListWorktreeInfoAsync(gitRoot, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(worktree => string.Equals(worktree.Branch, branch, StringComparison.Ordinal));
        if (checkedOutWorktree is not null)
        {
            var status = await RunGitAsync(
                checkedOutWorktree.Path,
                "status --porcelain -uall",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (status is null)
                return new GitBranchSyncResult(false, branch, false, $"Could not inspect the \"{branch}\" worktree.");
            if (!string.IsNullOrWhiteSpace(status))
            {
                return new GitBranchSyncResult(
                    false,
                    branch,
                    false,
                    $"Branch \"{branch}\" has uncommitted changes; automatic sync was skipped.");
            }

            var mergeResult = await RunGitAsync(
                checkedOutWorktree.Path,
                $"merge --ff-only {QuoteGitArgument(remoteRef)}",
                WorktreeGitCommandTimeout,
                cancellationToken).ConfigureAwait(false);
            return mergeResult is null
                ? new GitBranchSyncResult(false, branch, false, $"Could not fast-forward \"{branch}\" to \"{remote}/{branch}\".")
                : new GitBranchSyncResult(true, branch, true, $"Fast-forwarded \"{branch}\" to \"{remote}/{branch}\".");
        }

        var updateResult = await RunGitAsync(
            gitRoot,
            $"branch --force {QuoteGitArgument(branch)} {QuoteGitArgument(remoteRef)}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return updateResult is null
            ? new GitBranchSyncResult(false, branch, false, $"Could not fast-forward local branch \"{branch}\".")
            : new GitBranchSyncResult(true, branch, true, $"Fast-forwarded local branch \"{branch}\" to \"{remote}/{branch}\".");
    }

    /// <summary>
    /// Returns the list of changed files (staged + unstaged + untracked) with line stats.
    /// Submodules are expanded: git reports a dirty submodule as a single opaque entry, so each
    /// changed submodule is enumerated recursively and its files are surfaced with the submodule
    /// path as prefix. A <see cref="GitChangeKind.Submodule"/> row is kept when the recorded commit
    /// pointer moved.
    /// </summary>
    public static Task<List<GitFileChange>> GetChangedFilesAsync(string dir)
        => GetChangedFilesAsync(dir, 0);

    /// <summary>Deepest submodule nesting level that is expanded (guards pathological repos).</summary>
    private const int MaxSubmoduleDepth = 3;

    private static async Task<List<GitFileChange>> GetChangedFilesAsync(string dir, int depth)
    {
        var output = await RunGitAsync(dir, "status --porcelain -uall").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output)) return [];

        // Porcelain paths are always relative to the repository root, even when git runs from a
        // subfolder — so full paths must be rebuilt from the root, not from the working directory.
        var repoRoot = FindRepoRoot(dir) ?? dir;

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

            var fullPath = Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar));

            // Skip worktree sibling directories (they appear as untracked in some configs)
            if (kind == GitChangeKind.Untracked && path.Contains("-wt-"))
                continue;

            changes.Add(new GitFileChange
            {
                RelativePath = path,
                FullPath = fullPath,
                RepoRoot = repoRoot,
                RepoRelativePath = path,
                Kind = kind,
                StatusCode = status
            });
        }

        // Enrich with line stats from numstat
        var numstat = await RunGitAsync(dir, "diff --numstat").ConfigureAwait(false);
        var cachedNumstat = await RunGitAsync(dir, "diff --cached --numstat").ConfigureAwait(false);
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
                        c.LinesAdded = File.ReadLines(c.FullPath).Count();
                }
                catch { /* ignore */ }
            }
        }

        return await ExpandSubmoduleChangesAsync(repoRoot, changes, depth).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces opaque submodule entries (git reports a whole dirty submodule as one line) with the
    /// files actually changed inside it, keeping a commit-pointer row when the submodule moved to a
    /// different commit.
    /// </summary>
    private static async Task<List<GitFileChange>> ExpandSubmoduleChangesAsync(
        string repoRoot,
        List<GitFileChange> changes,
        int depth)
    {
        if (depth >= MaxSubmoduleDepth || changes.Count == 0)
            return changes;

        var submodules = await GetSubmoduleStatesAsync(repoRoot).ConfigureAwait(false);
        if (submodules.Count == 0)
            return changes;

        var expanded = new List<GitFileChange>(changes.Count);
        foreach (var change in changes)
        {
            if (!submodules.TryGetValue(change.RelativePath, out var state) || !state.IsInitialized)
            {
                expanded.Add(change);
                continue;
            }

            var submoduleDir = Path.Combine(repoRoot, change.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(submoduleDir))
            {
                expanded.Add(change);
                continue;
            }

            var nested = await GetChangedFilesAsync(submoduleDir, depth + 1).ConfigureAwait(false);

            // A staged pointer bump shows up as an index-side status letter on the submodule entry.
            var pointerMoved = state.CommitMoved || change.StatusCode.Length > 0 && change.StatusCode[0] is not ' ' and not '?';
            if (pointerMoved || nested.Count == 0)
                expanded.Add(change.AsSubmodulePointer());

            foreach (var nestedChange in nested)
                expanded.Add(nestedChange.WithSubmodulePrefix(change.RelativePath));
        }

        return expanded;
    }

    /// <summary>
    /// Direct submodules of the repository keyed by their repo-relative path. Reads a single
    /// <c>git submodule status</c> (skipped entirely when the repo has no <c>.gitmodules</c>), which
    /// reports whether each submodule is initialized and whether its checked-out commit differs from
    /// the one recorded by the superproject.
    /// </summary>
    private static async Task<Dictionary<string, (bool IsInitialized, bool CommitMoved)>> GetSubmoduleStatesAsync(string repoRoot)
    {
        var states = new Dictionary<string, (bool, bool)>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(Path.Combine(repoRoot, ".gitmodules")))
            return states;

        var output = await RunGitAsync(repoRoot, "submodule status").ConfigureAwait(false);
        foreach (var line in ParseLines(output))
        {
            // Format: "<flag><sha1> <path> (<describe>)" — flag is ' ', '+', '-' or 'U'.
            var flag = line[0];
            var body = char.IsLetterOrDigit(flag) ? line : line[1..];
            var separator = body.IndexOf(' ');
            if (separator < 0) continue;

            var path = body[(separator + 1)..].Trim();
            var describeStart = path.LastIndexOf(" (", StringComparison.Ordinal);
            if (describeStart > 0 && path.EndsWith(')'))
                path = path[..describeStart].TrimEnd();
            if (path.Length == 0) continue;

            states[path.Replace('\\', '/')] = (flag != '-', flag == '+');
        }

        return states;
    }

    /// <summary>Gets the unified diff for a specific file.</summary>
    public static async Task<string?> GetFileDiffAsync(string dir, string relativePath)
    {
        // Try staged first, then unstaged, then for untracked show the whole file
        var diff = await RunGitAsync(dir, $"diff -- \"{relativePath}\"").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(diff))
            diff = await RunGitAsync(dir, $"diff --cached -- \"{relativePath}\"").ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(diff) ? null : diff;
    }

    /// <summary>Gets the commit-log summary for a moved submodule pointer (the commits the parent
    /// repository would gain or lose), or null when nothing is recorded.</summary>
    public static async Task<string?> GetSubmoduleCommitLogAsync(string repoRoot, string submodulePath)
    {
        var quoted = QuoteGitArgument(submodulePath);
        var log = await RunGitAsync(repoRoot, $"diff --submodule=log -- {quoted}").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(log))
            log = await RunGitAsync(repoRoot, $"diff --cached --submodule=log -- {quoted}").ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(log) ? null : log;
    }

    /// <summary>Gets the short stat summary (e.g. "3 files changed, 12 insertions(+), 5 deletions(-)").</summary>
    public static async Task<string?> GetDiffStatAsync(string dir)
    {
        return await RunGitAsync(dir, "diff --stat --stat-width=60").ConfigureAwait(false);
    }

    /// <summary>Creates a git worktree as a sibling directory to the repository root. Returns the
    /// worktree root path. When <paramref name="repoDir"/> is a subfolder of the repo, the worktree
    /// is still anchored to the repository root so it lands beside the main checkout (never nested
    /// inside it). Callers map the project subpath into the worktree via
    /// <see cref="ResolveWorktreeWorkingDirectory"/>.</summary>
    public static async Task<string?> CreateWorktreeAsync(string repoDir, string branchName)
    {
        // Anchor to the repository root so the worktree is a sibling of the main checkout even when
        // the project working directory is a subfolder (e.g. a monorepo app). Without this the
        // worktree would be created beside the subfolder — nested inside the repo — which breaks
        // git and loses the project's context layout.
        var gitRoot = FindRepoRoot(repoDir) ?? repoDir;
        var trimmedRoot = gitRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repoName = Path.GetFileName(trimmedRoot);
        var safeBranch = branchName.Replace('/', '-').Replace('\\', '-');
        var parentDir = Path.GetDirectoryName(trimmedRoot);
        if (parentDir is null) return null;

        var worktreePath = Path.Combine(parentDir, $"{repoName}-wt-{safeBranch}");
        if (Directory.Exists(worktreePath))
            return worktreePath; // Already exists

        // Try creating with a new branch first. Run from the repo root so paths stay predictable.
        var result = await RunGitAsync(gitRoot, $"worktree add \"{worktreePath}\" -b \"{branchName}\"", WorktreeGitCommandTimeout).ConfigureAwait(false);
        if (result is not null && Directory.Exists(worktreePath))
            return worktreePath;

        // Branch may already exist — try attaching to it
        result = await RunGitAsync(gitRoot, $"worktree add \"{worktreePath}\" \"{branchName}\"", WorktreeGitCommandTimeout).ConfigureAwait(false);
        if (result is not null && Directory.Exists(worktreePath))
            return worktreePath;

        // Last resort — create with detached HEAD
        result = await RunGitAsync(gitRoot, $"worktree add --detach \"{worktreePath}\"", WorktreeGitCommandTimeout).ConfigureAwait(false);
        if (result is not null && Directory.Exists(worktreePath))
            return worktreePath;

        return null;
    }

    /// <summary>Removes a git worktree and its associated branch.</summary>
    public static async Task<bool> RemoveWorktreeAsync(string dir, string worktreePath)
    {
        if (!Directory.Exists(worktreePath)) return true;

        // Get the branch name before removing the worktree
        var branch = await RunGitAsync(worktreePath, "rev-parse --abbrev-ref HEAD").ConfigureAwait(false);
        branch = branch?.Trim();

        var result = await RunGitAsync(dir, $"worktree remove \"{worktreePath}\" --force").ConfigureAwait(false);
        if (result is null) return false;

        // Delete the orphaned branch if it was a lumi/ branch
        if (branch is { Length: > 0 } && branch.StartsWith("lumi/"))
            await RunGitAsync(dir, $"branch -D \"{branch}\"").ConfigureAwait(false);

        return true;
    }

    /// <summary>Lists existing worktrees.</summary>
    public static async Task<List<string>> ListWorktreesAsync(string dir)
    {
        var output = await RunGitAsync(dir, "worktree list --porcelain").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output)) return [];

        return output.Split('\n')
            .Where(l => l.StartsWith("worktree "))
            .Select(l => l[9..].Trim())
            .ToList();
    }

    /// <summary>Lists existing worktrees with their branch names.</summary>
    public static async Task<List<WorktreeInfo>> ListWorktreeInfoAsync(
        string dir,
        CancellationToken cancellationToken = default)
    {
        var output = await RunGitAsync(
            dir,
            "worktree list --porcelain",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output)) return [];

        var results = new List<WorktreeInfo>();
        string? currentPath = null;
        string? currentBranch = null;
        bool isBare = false;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("worktree "))
            {
                // Save previous entry
                if (currentPath is not null && !isBare)
                    results.Add(new WorktreeInfo(currentPath, currentBranch));

                currentPath = line[9..].Trim();
                currentBranch = null;
                isBare = false;
            }
            else if (line.StartsWith("branch "))
            {
                // branch refs/heads/main → main
                var refName = line[7..].Trim();
                currentBranch = refName.StartsWith("refs/heads/")
                    ? refName["refs/heads/".Length..]
                    : refName;
            }
            else if (line == "bare")
            {
                isBare = true;
            }
        }

        // Save last entry
        if (currentPath is not null && !isBare)
            results.Add(new WorktreeInfo(currentPath, currentBranch));

        return results;
    }

    // Serializes git invocations process-wide. Running multiple redirected git processes
    // concurrently is unsafe on Windows: Process.Start marks the stdout/stderr pipe write
    // handles inheritable while it launches the child, and the Git-for-Windows launcher
    // (cmd\git.exe) re-execs the real git (mingw64\bin\git.exe) as a grandchild outside the
    // .NET start lock. That grandchild can inherit a *sibling* git's pipe write handle, so the
    // sibling's pipe never reaches EOF and ReadToEndAsync hangs forever (observed: refresh
    // triad branch+status+worktree-list leaving orphaned 0-CPU git processes). Running one git
    // pipeline at a time closes the handle-inheritance window.
    private static readonly SemaphoreSlim GitInvocationGate = new(1, 1);

    private static async Task<string?> RunGitAsync(
        string workDir,
        string args,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? DefaultGitCommandTimeout;
        await GitInvocationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = workDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // Resolve "git" the same way the user's terminal would (Homebrew/Xcode git) even when Lumi
            // is GUI-launched with a truncated PATH on macOS/Linux. No-op on Windows.
            UnixShellPath.ApplyTo(psi);

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            using var standardOutput = proc.StandardOutput;
            using var standardError = proc.StandardError;

            // Close stdin immediately so git can never block waiting on input (e.g. a
            // credential or config prompt); it should fail fast instead of hanging.
            try { proc.StandardInput.Close(); } catch { /* stdin may already be gone */ }

            var stdoutTask = standardOutput.ReadToEndAsync();
            var stderrTask = standardError.ReadToEndAsync();
            using var timeoutCts = new CancellationTokenSource(effectiveTimeout);
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCts.Token,
                cancellationToken);

            try
            {
                await proc.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
                // Bound the output drain by the same deadline. The process having exited does
                // NOT guarantee the pipes reach EOF — a leaked/inherited write handle in a
                // grandchild can keep them open, and an unbounded read would hang forever.
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(proc);
                await WaitForExitQuietlyAsync(proc, TimedOutGitCleanupTimeout).ConfigureAwait(false);
                await DrainOutputQuietlyAsync(stdoutTask, stderrTask, TimedOutGitCleanupTimeout).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    throw;

                System.Diagnostics.Debug.WriteLine($"[Lumi] Git command timed out after {effectiveTimeout.TotalSeconds:N0}s: git {args}");
                return null;
            }

            var output = stdoutTask.Result;
            return proc.ExitCode == 0 ? output : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Lumi] Git command failed: git {args} ({ex.Message})");
            return null;
        }
        finally
        {
            GitInvocationGate.Release();
        }
    }

    private static void TryKillProcessTree(Process proc)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Lumi] Failed to kill timed-out git process {proc.Id}: {ex.Message}");
        }
    }

    private static async Task WaitForExitQuietlyAsync(Process proc, TimeSpan timeout)
    {
        try
        {
            var waitTask = proc.WaitForExitAsync();
            if (await Task.WhenAny(waitTask, Task.Delay(timeout)).ConfigureAwait(false) == waitTask)
                await waitTask.ConfigureAwait(false);
            else
                System.Diagnostics.Debug.WriteLine($"[Lumi] Timed out waiting for killed git process {proc.Id} to exit.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Lumi] Failed waiting for timed-out git process {proc.Id}: {ex.Message}");
        }
    }

    private static async Task DrainOutputQuietlyAsync(Task<string> stdoutTask, Task<string> stderrTask, TimeSpan timeout)
    {
        var drainTask = Task.WhenAll(stdoutTask, stderrTask);
        try
        {
            if (await Task.WhenAny(drainTask, Task.Delay(timeout)).ConfigureAwait(false) == drainTask)
                await drainTask.ConfigureAwait(false);
            else
            {
                ObserveFault(stdoutTask);
                ObserveFault(stderrTask);
                System.Diagnostics.Debug.WriteLine("[Lumi] Timed out draining killed git process output.");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Lumi] Failed draining timed-out git output: {ex.Message}");
        }
    }

    private static void ObserveFault(Task<string> task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string? NormalizeBranchName(string? value)
    {
        var branch = value?.Trim();
        return string.IsNullOrWhiteSpace(branch) || string.Equals(branch, "HEAD", StringComparison.OrdinalIgnoreCase)
            ? null
            : branch;
    }

    private static string? ParseStatusBranch(string? statusOutput)
    {
        if (string.IsNullOrWhiteSpace(statusOutput))
            return null;

        var firstLine = statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?.Trim();
        if (firstLine is null || !firstLine.StartsWith("## ", StringComparison.Ordinal))
            return null;

        var branch = firstLine[3..].Trim();
        const string noCommitsPrefix = "No commits yet on ";
        if (branch.StartsWith(noCommitsPrefix, StringComparison.OrdinalIgnoreCase))
            return NormalizeBranchName(branch[noCommitsPrefix.Length..]);

        var upstreamIndex = branch.IndexOf("...", StringComparison.Ordinal);
        if (upstreamIndex >= 0)
            branch = branch[..upstreamIndex];
        var detailIndex = branch.IndexOf(' ');
        if (detailIndex >= 0)
            branch = branch[..detailIndex];

        return NormalizeBranchName(branch);
    }

    private static async Task<bool> RefExistsAsync(
        string gitRoot,
        string refName,
        CancellationToken cancellationToken = default)
    {
        var result = await RunGitAsync(
            gitRoot,
            $"show-ref --verify --quiet {QuoteGitArgument(refName)}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result is not null;
    }

    private static async Task<string?> ResolveCommitAsync(
        string gitRoot,
        string refName,
        CancellationToken cancellationToken = default)
    {
        var result = await RunGitAsync(
            gitRoot,
            $"rev-parse --verify {QuoteGitArgument(refName + "^{commit}")}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    private static async Task<(int LocalOnly, int RemoteOnly)?> GetAheadBehindAsync(
        string gitRoot,
        string localRef,
        string remoteRef,
        CancellationToken cancellationToken = default)
    {
        var output = await RunGitAsync(
            gitRoot,
            $"rev-list --left-right --count {QuoteGitArgument(localRef + "..." + remoteRef)}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var parts = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            && int.TryParse(parts[0], out var localOnly)
            && int.TryParse(parts[1], out var remoteOnly)
                ? (localOnly, remoteOnly)
                : null;
    }

    private static IEnumerable<string> ParseLines(string? output)
    {
        return string.IsNullOrWhiteSpace(output)
            ? []
            : output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string QuoteGitArgument(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}

public enum GitChangeKind { Modified, Added, Deleted, Renamed, Untracked, Submodule }

public sealed record GitDefaultBranchInfo(string BranchName, string? RemoteName);

public sealed record GitBranchSyncResult(bool Succeeded, string? BranchName, bool Updated, string Message);

/// <summary>Represents a git worktree with its path and branch name.</summary>
public record WorktreeInfo(string Path, string? Branch)
{
    /// <summary>Display name: branch name if available, otherwise the directory name.</summary>
    public string DisplayName => Branch ?? System.IO.Path.GetFileName(Path);
}

public class GitFileChange
{
    /// <summary>Path relative to the outermost repository the change was collected from. For files
    /// inside a submodule this includes the submodule prefix (e.g. <c>Strata/src/Foo.cs</c>).</summary>
    public required string RelativePath { get; init; }
    public required string FullPath { get; init; }
    public required GitChangeKind Kind { get; init; }
    public required string StatusCode { get; init; }

    /// <summary>Root of the git repository that actually owns this change — the submodule root for
    /// submodule files, otherwise the outer repository root. Use this as the working directory for
    /// per-file git commands.</summary>
    public required string RepoRoot { get; init; }

    /// <summary>Path relative to <see cref="RepoRoot"/> (no submodule prefix).</summary>
    public required string RepoRelativePath { get; init; }

    /// <summary>Repo-relative path of the submodule that owns this change, or null for the outer
    /// repository. Nested submodules are joined with '/'.</summary>
    public string? SubmodulePath { get; init; }

    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }

    public bool IsSubmoduleFile => !string.IsNullOrEmpty(SubmodulePath);

    /// <summary>Display name of the owning submodule (last path segment), or null.</summary>
    public string? SubmoduleName => string.IsNullOrEmpty(SubmodulePath)
        ? null
        : SubmodulePath[(SubmodulePath.LastIndexOf('/') + 1)..];

    public string FileName => Kind == GitChangeKind.Submodule
        ? RelativePath[(RelativePath.LastIndexOf('/') + 1)..]
        : Path.GetFileName(RelativePath);

    public string? Directory => Path.GetDirectoryName(RelativePath)?.Replace('\\', '/');

    /// <summary>Directory of this change relative to its own repository (no submodule prefix).</summary>
    public string? RepoRelativeDirectory => Path.GetDirectoryName(RepoRelativePath)?.Replace('\\', '/');

    public string KindIcon => Kind switch
    {
        GitChangeKind.Modified => "M",
        GitChangeKind.Added => "A",
        GitChangeKind.Deleted => "D",
        GitChangeKind.Renamed => "R",
        GitChangeKind.Untracked => "U",
        GitChangeKind.Submodule => "S",
        _ => "?"
    };

    public string KindLabel => Kind switch
    {
        GitChangeKind.Modified => "Modified",
        GitChangeKind.Added => "Added",
        GitChangeKind.Deleted => "Deleted",
        GitChangeKind.Renamed => "Renamed",
        GitChangeKind.Untracked => "Untracked",
        GitChangeKind.Submodule => "Submodule",
        _ => "Unknown"
    };

    /// <summary>Reprojects a change collected inside a submodule so it reads relative to the parent
    /// repository, while keeping the owning repo root for per-file git commands.</summary>
    internal GitFileChange WithSubmodulePrefix(string submodulePath)
    {
        var prefix = submodulePath.Replace('\\', '/').Trim('/');
        return new GitFileChange
        {
            RelativePath = $"{prefix}/{RelativePath}",
            FullPath = FullPath,
            Kind = Kind,
            StatusCode = StatusCode,
            RepoRoot = RepoRoot,
            RepoRelativePath = RepoRelativePath,
            SubmodulePath = string.IsNullOrEmpty(SubmodulePath) ? prefix : $"{prefix}/{SubmodulePath}",
            LinesAdded = LinesAdded,
            LinesRemoved = LinesRemoved,
        };
    }

    /// <summary>Turns the opaque "submodule is dirty" entry into an explicit commit-pointer row that
    /// groups with the rest of that submodule's changes.</summary>
    internal GitFileChange AsSubmodulePointer() => new()
    {
        RelativePath = RelativePath,
        FullPath = FullPath,
        Kind = GitChangeKind.Submodule,
        StatusCode = StatusCode,
        RepoRoot = RepoRoot,
        RepoRelativePath = RepoRelativePath,
        SubmodulePath = RepoRelativePath.Replace('\\', '/').Trim('/'),
    };
}
