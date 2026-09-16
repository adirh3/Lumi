using System.Diagnostics;
using System.Text;

namespace Lumi.Services;

public static partial class GitService
{
    // Remote inspection must not refresh the index, run user-configured diff commands, or read an
    // unbounded pipe. Desktop's existing mutation helpers deliberately remain separate.
    internal static async Task<(List<GitFileChange> Files, bool Truncated)> GetReadOnlyChangesAsync(
        string repoRoot, int fileLimit, CancellationToken cancellationToken, bool includeLineStatistics = false) =>
        await GetReadOnlyChangesAsync(repoRoot, fileLimit, 0, cancellationToken, includeLineStatistics).ConfigureAwait(false);

    private static async Task<(List<GitFileChange> Files, bool Truncated)> GetReadOnlyChangesAsync(
        string repoRoot, int fileLimit, int depth, CancellationToken cancellationToken, bool includeLineStatistics)
    {
        var output = await RunReadOnlyGitAsync(repoRoot,
            ["status", "--porcelain=v1", "-z", "-uall", "--ignore-submodules=none"],
            256 * 1024, cancellationToken);
        var records = output.Text.Split('\0');
        var entries = new Dictionary<string, (string Status, string? OriginalPath)>(StringComparer.Ordinal);
        var truncated = output.Truncated;
        // Only complete NUL-terminated records can become selectable paths.
        for (var i = 0; i < records.Length - 1; i++)
        {
            var record = records[i];
            if (record.Length < 4)
                continue;
            var status = record[..2];
            var path = record[3..];
            string? originalPath = null;
            if (status.Contains('R') || status.Contains('C'))
            {
                if (++i >= records.Length - 1)
                    break;
                originalPath = records[i];
            }
            // Git emits a staged deletion and its untracked replacement as separate same-path records.
            // Merge before applying the file limit so both sides remain reachable through the path API.
            if (entries.TryGetValue(path, out var previous)
                && (previous.Status == "D " && status == "??" || previous.Status == "??" && status == "D "))
            {
                status = "D?";
            }
            entries[path] = (status, originalPath);
        }

        var changes = new List<GitFileChange>();
        foreach (var (path, entry) in entries)
        {
            var (status, originalPath) = entry;
            if (changes.Count >= fileLimit)
            {
                truncated = true;
                break;
            }
            if (!IsReadOnlyPathSafe(repoRoot, path)
                || originalPath is not null && !IsReadOnlyPathSafe(repoRoot, originalPath))
            {
                truncated = true;
                continue;
            }
            var kind = status switch
            {
                "??" => GitChangeKind.Untracked,
                "D?" => GitChangeKind.Modified,
                _ when status.Contains('U') || status is "AA" or "DD" => GitChangeKind.Modified,
                _ when status.Contains('R') => GitChangeKind.Renamed,
                _ when status.Contains('D') => GitChangeKind.Deleted,
                _ when status.Contains('A') => GitChangeKind.Added,
                _ => GitChangeKind.Modified
            };
            changes.Add(new GitFileChange
            {
                RelativePath = path, RepoRelativePath = path,
                OriginalRepoRelativePath = originalPath,
                RepoRoot = repoRoot, FullPath = Path.Combine(repoRoot, path),
                StatusCode = status, Kind = kind
            });
            var nestedRoot = Path.GetFullPath(Path.Combine(repoRoot, path));
            if (depth < MaxSubmoduleDepth && Directory.Exists(nestedRoot)
                && string.Equals(FindRepoRoot(nestedRoot), nestedRoot,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                if (changes.Count >= fileLimit)
                {
                    truncated = true;
                    continue;
                }
                var nested = await GetReadOnlyChangesAsync(nestedRoot,
                    Math.Max(0, fileLimit - changes.Count), depth + 1, cancellationToken, includeLineStatistics).ConfigureAwait(false);
                changes.AddRange(nested.Files.Select(file => file.WithSubmodulePrefix(path)));
                truncated |= nested.Truncated;
            }
        }
        if (includeLineStatistics)
            await PopulateReadOnlyLineStatisticsAsync(repoRoot, changes, cancellationToken).ConfigureAwait(false);
        return (changes, truncated);
    }

    private static async Task PopulateReadOnlyLineStatisticsAsync(
        string repoRoot, List<GitFileChange> changes, CancellationToken cancellationToken)
    {
        var ownedFiles = changes.Where(file => file.SubmodulePath is null).ToList();
        if (ownedFiles.Count == 0)
            return;

        string[] arguments = ["diff", "--numstat", "-z", "--no-ext-diff", "--no-textconv", "--no-color",
            "--submodule=short", "--find-renames"];
        (string Text, bool Truncated) staged;
        (string Text, bool Truncated) working;
        try
        {
            staged = await RunReadOnlyGitAsync(repoRoot, [.. arguments, "--cached"], 256 * 1024, cancellationToken);
            working = await RunReadOnlyGitAsync(repoRoot, arguments, 256 * 1024, cancellationToken);
        }
        catch (IOException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Trace.TraceWarning($"[Git] Line statistics are unavailable: {ex.Message}");
            return;
        }
        if (staged.Truncated || working.Truncated)
            return;

        var statistics = new Dictionary<string, (int Added, int Removed, bool Binary)>(StringComparer.Ordinal);
        AddNumstat(staged.Text);
        AddNumstat(working.Text);
        const int untrackedLimit = 1024 * 1024;
        var untrackedBudget = 8 * untrackedLimit;
        char[]? buffer = null;
        foreach (var file in ownedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            statistics.TryGetValue(file.RepoRelativePath, out var stats);
            file.LinesAdded = stats.Added;
            file.LinesRemoved = stats.Removed;
            file.IsBinary = stats.Binary;
            file.HasLineStatistics = !stats.Binary;
            if (file.Kind != GitChangeKind.Untracked && file.StatusCode != "D?")
                continue;

            // Untracked files are absent from numstat. Read one bounded buffer, never a diff per file.
            // Large files remain explicitly unknown instead of reporting a truncated prefix as a total.
            file.HasLineStatistics = false;
            if (file.IsBinary || untrackedBudget <= 0 || !IsReadOnlyPathSafe(repoRoot, file.RepoRelativePath))
                continue;
            try
            {
                using var reader = new StreamReader(file.FullPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                buffer ??= new char[untrackedLimit + 1];
                var limit = Math.Min(untrackedLimit, untrackedBudget);
                var count = await reader.ReadBlockAsync(buffer.AsMemory(0, limit + 1), cancellationToken).ConfigureAwait(false);
                untrackedBudget -= count;
                if (buffer.AsSpan(0, count).Contains('\0'))
                {
                    file.IsBinary = true;
                    continue;
                }
                if (count > limit)
                    continue;
                var lines = 0;
                for (var i = 0; i < count; i++)
                    if (buffer[i] == '\n' || buffer[i] == '\r' && (i + 1 == count || buffer[i + 1] != '\n'))
                        lines++;
                if (count > 0 && buffer[count - 1] is not ('\n' or '\r'))
                    lines++;
                file.LinesAdded += lines;
                file.HasLineStatistics = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"[Git] Could not count lines for {file.RelativePath}: {ex.Message}");
            }
        }

        void AddNumstat(string output)
        {
            var records = output.Split('\0');
            for (var i = 0; i < records.Length - 1; i++)
            {
                var fields = records[i].Split('\t', 3);
                if (fields.Length != 3)
                    continue;
                var path = fields[2];
                // With -z, a rename carries empty path, source NUL, destination NUL.
                if (path.Length == 0)
                {
                    if (i + 2 >= records.Length - 1)
                        break;
                    path = records[i + 2];
                    i += 2;
                }
                var binary = fields[0] == "-" || fields[1] == "-";
                if (!binary && (!int.TryParse(fields[0], out _) || !int.TryParse(fields[1], out _)))
                    continue;
                var added = binary ? 0 : int.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture);
                var removed = binary ? 0 : int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture);
                statistics.TryGetValue(path, out var previous);
                statistics[path] = (previous.Added + added, previous.Removed + removed, previous.Binary || binary);
            }
        }
    }

    internal static async Task<(string Text, bool Truncated)> GetReadOnlyDiffAsync(
        string repoRoot, string path, int fileLimit, int characterLimit, CancellationToken cancellationToken)
    {
        // Even a path returned by an earlier list must still belong to the explicit repository's
        // current changed-file set. Literal pathspecs prevent wildcard/:(...) interpretation.
        if (!IsReadOnlyPathSafe(repoRoot, path))
            throw new ArgumentException("Invalid repository file.");
        var state = await GetReadOnlyChangesAsync(repoRoot, fileLimit, cancellationToken);
        var file = state.Files.FirstOrDefault(file => string.Equals(file.RelativePath, path, StringComparison.Ordinal))
            ?? throw new FileNotFoundException("This file is no longer in the changed-file list. Refresh changes.");
        if (file.Kind == GitChangeKind.Untracked)
            return await ReadUntrackedDiffAsync(repoRoot, file, characterLimit, cancellationToken);

        var arguments = new[] { "diff", "--no-ext-diff", "--no-textconv", "--no-color",
            "--submodule=short", "--unified=3", "--find-renames" };
        // Filtering to only the destination hides the source from Git's rename detection.
        // Both paths are repository-state metadata, relative to the owning (possibly nested) repo.
        string[] paths = file.OriginalRepoRelativePath is { } originalPath
            ? [originalPath, file.RepoRelativePath]
            : [file.RepoRelativePath];
        var staged = await RunReadOnlyGitAsync(file.RepoRoot,
            [.. arguments, "--cached", "--", .. paths], characterLimit, cancellationToken);
        var remaining = characterLimit - staged.Text.Length;
        if (remaining <= 64 || staged.Truncated)
            return (staged.Text, true);
        var working = file.StatusCode == "D?"
            ? await ReadUntrackedDiffAsync(repoRoot, file, remaining, cancellationToken)
            : await RunReadOnlyGitAsync(file.RepoRoot,
                [.. arguments, "--", .. paths], remaining, cancellationToken);
        var text = staged.Text.Length == 0 ? working.Text
            : "Staged changes\n" + staged.Text + (working.Text.Length == 0 ? "" : "\nWorking tree changes\n" + working.Text);
        return ClipReadOnlyOutput(text, working.Truncated, characterLimit);
    }

    private static async Task<(string Text, bool Truncated)> ReadUntrackedDiffAsync(
        string repoRoot, GitFileChange file, int characterLimit, CancellationToken cancellationToken)
    {
        if (!File.Exists(file.FullPath))
            throw new FileNotFoundException("The untracked file no longer exists.");
        if (!IsReadOnlyPathSafe(repoRoot, file.RelativePath))
            throw new ArgumentException("Invalid repository file.");
        using var reader = new StreamReader(file.FullPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[characterLimit + 1];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        if (buffer.AsSpan(0, count).Contains('\0'))
            return ("", false);
        var content = new string(buffer, 0, Math.Min(count, characterLimit));
        if (content.Length == 0)
            return ("", false);
        var normalized = content.Replace("\r\n", "\n");
        var lines = (normalized.EndsWith('\n') ? normalized[..^1] : normalized).Split('\n');
        var diff = $"--- /dev/null\n+++ b/{file.RelativePath}\n@@ -0,0 +1,{lines.Length} @@\n"
            + string.Join('\n', lines.Select(line => "+" + line))
            + (normalized.EndsWith('\n') ? "\n" : "\n\\ No newline at end of file\n");
        return ClipReadOnlyOutput(diff, count > characterLimit, characterLimit);
    }

    internal static bool IsReadOnlyPathSafe(string repoRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || Path.IsPathRooted(path)
            || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl))
            return false;
        var segments = path.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."
            || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            return false;
        try
        {
            var current = Path.GetFullPath(repoRoot);
            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);
                // Missing leaf paths are valid for deleted files; do not follow directory links.
                if ((File.Exists(current) || Directory.Exists(current))
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static (string Text, bool Truncated) ClipReadOnlyOutput(string text, bool truncated, int limit)
    {
        if (text.Length <= limit)
            return (text, truncated);
        var end = limit;
        if (end > 0 && char.IsHighSurrogate(text[end - 1]))
            end--;
        return (text[..end], true);
    }

    private static async Task<(string Text, bool Truncated)> RunReadOnlyGitAsync(
        string repoRoot, string[] arguments, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoRoot, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--no-optional-locks");
        start.ArgumentList.Add("--literal-pathspecs");
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, limit, timeout.Token);
            var stderr = ReadBoundedAsync(process.StandardError, 4096, timeout.Token);
            var result = await stdout.ConfigureAwait(false);
            // Stop producing once the retained response budget is full.
            if (result.Truncated && !process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            if (!result.Truncated && process.ExitCode != 0)
                throw new IOException("Git could not read this repository. Try refreshing changes.");
            return result;
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(
        StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var buffer = new char[limit + 1];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        return ClipReadOnlyOutput(new string(buffer, 0, count), false, limit);
    }
}
