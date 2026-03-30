using System;
using System.Collections.Generic;

namespace Lumi.Services;

/// <summary>
/// Indicates that lines were deleted after a specific line in the current file.
/// <paramref name="AfterLineIndex"/> of -1 means deletions occurred before the first line.
/// </summary>
public readonly record struct DeletionPoint(int AfterLineIndex, int Count);

/// <summary>Result of diff analysis: which lines changed and where deletions occurred.</summary>
public sealed record DiffResult(HashSet<int> ChangedLines, IReadOnlyList<DeletionPoint> Deletions);

/// <summary>
/// Computes which lines in a file were actually changed by a set of edits.
/// Uses LCS (Longest Common Subsequence) to compare old vs new text line-by-line
/// so only truly modified or added lines are highlighted, and deletion points are tracked.
/// </summary>
public static class DiffAlgorithm
{
    private static readonly DiffResult EmptyResult = new([], []);

    /// <summary>
    /// Given the full file content (after edits) and a list of edits (old → new text),
    /// returns changed line indices and deletion points.
    /// Line indices correspond to <c>fileContent.Split('\n')</c>.
    /// </summary>
    public static DiffResult ComputeChangedLines(
        string fileContent,
        IReadOnlyList<(string? OldText, string? NewText)> edits,
        bool isCreate)
    {
        if (string.IsNullOrEmpty(fileContent))
            return EmptyResult;

        var normalized = NormalizeLineEndings(fileContent);
        var lines = normalized.Split('\n');

        if (isCreate)
        {
            var all = new HashSet<int>();
            for (int i = 0; i < lines.Length; i++)
                all.Add(i);
            return new DiffResult(all, []);
        }

        var changed = new HashSet<int>();
        var deletions = new List<DeletionPoint>();

        foreach (var (oldText, newText) in edits)
            MarkEditChangedLines(normalized, lines, oldText, newText, changed, deletions);

        return new DiffResult(changed, deletions);
    }

    /// <summary>
    /// Marks actually-changed line indices and deletion points for a single edit.
    /// </summary>
    internal static void MarkEditChangedLines(
        string normalizedContent,
        string[] fileLines,
        string? oldText,
        string? newText,
        HashSet<int> changedLines,
        List<DeletionPoint> deletions)
    {
        if (string.IsNullOrEmpty(newText))
            return; // deletion only — nothing visible to highlight

        var normalizedNew = NormalizeLineEndings(newText);

        var idx = normalizedContent.IndexOf(normalizedNew, StringComparison.Ordinal);
        if (idx < 0) return;

        int startLine = CountNewlinesBefore(normalizedContent, idx);
        var newLines = normalizedNew.Split('\n');

        if (string.IsNullOrEmpty(oldText))
        {
            // Pure insertion or creation — all new lines are changed, no deletions
            for (int i = 0; i < newLines.Length; i++)
                changedLines.Add(startLine + i);
            return;
        }

        var normalizedOld = NormalizeLineEndings(oldText);
        var oldLines = normalizedOld.Split('\n');

        if (normalizedOld == normalizedNew)
            return; // identical — no change

        // Use LCS to find unchanged lines and deletion points
        var (unchangedInNew, localDeletions) = AnalyzeLineDiff(oldLines, newLines);

        for (int i = 0; i < newLines.Length; i++)
        {
            if (!unchangedInNew.Contains(i))
                changedLines.Add(startLine + i);
        }

        // Offset local deletion points to file-level coordinates
        foreach (var d in localDeletions)
            deletions.Add(new DeletionPoint(startLine + d.AfterLineIndex, d.Count));
    }

    /// <summary>
    /// Performs LCS analysis and returns both unchanged new-line indices and deletion points.
    /// Deletion points use coordinates relative to the newLines array.
    /// </summary>
    internal static (HashSet<int> UnchangedNewIndices, List<DeletionPoint> Deletions)
        AnalyzeLineDiff(string[] oldLines, string[] newLines)
    {
        int m = oldLines.Length;
        int n = newLines.Length;

        // Build LCS length table
        var dp = new int[m + 1, n + 1];
        for (int i = 1; i <= m; i++)
            for (int j = 1; j <= n; j++)
            {
                if (string.Equals(oldLines[i - 1], newLines[j - 1], StringComparison.Ordinal))
                    dp[i, j] = dp[i - 1, j - 1] + 1;
                else
                    dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
            }

        // Backtrack to get ordered match pairs
        var matches = new List<(int OldIdx, int NewIdx)>();
        int ii = m, jj = n;
        while (ii > 0 && jj > 0)
        {
            if (string.Equals(oldLines[ii - 1], newLines[jj - 1], StringComparison.Ordinal))
            {
                matches.Add((ii - 1, jj - 1));
                ii--;
                jj--;
            }
            else if (dp[ii - 1, jj] >= dp[ii, jj - 1])
                ii--;
            else
                jj--;
        }
        matches.Reverse();

        // Compute unchanged indices
        var unchanged = new HashSet<int>();
        foreach (var (_, newIdx) in matches)
            unchanged.Add(newIdx);

        // Compute deletion points: gaps in old-line coverage between consecutive matches.
        // Only emit a deletion when old lines were removed WITHOUT new lines replacing them.
        // If both old and new lines exist in a gap, it's a replacement — the green highlights
        // on the new lines already communicate the change; a red deletion marker would be noise.
        var deletionPoints = new List<DeletionPoint>();
        int prevOldIdx = -1;
        int prevNewIdx = -1;

        foreach (var (oldIdx, newIdx) in matches)
        {
            int deletedCount = oldIdx - prevOldIdx - 1;
            int addedCount = newIdx - prevNewIdx - 1;
            // Pure deletion: old lines removed with no new lines taking their place
            if (deletedCount > 0 && addedCount == 0)
                deletionPoints.Add(new DeletionPoint(prevNewIdx, deletedCount));
            prevOldIdx = oldIdx;
            prevNewIdx = newIdx;
        }

        // Trailing: old lines after last match
        int trailingDeleted = m - prevOldIdx - 1;
        int trailingAdded = n - prevNewIdx - 1;
        if (trailingDeleted > 0 && trailingAdded == 0)
            deletionPoints.Add(new DeletionPoint(prevNewIdx, trailingDeleted));

        return (unchanged, deletionPoints);
    }

    /// <summary>
    /// Returns the set of 0-based indices in <paramref name="newLines"/> that correspond
    /// to unchanged lines (matched via LCS against <paramref name="oldLines"/>).
    /// Any index NOT in this set represents a modified or added line.
    /// </summary>
    internal static HashSet<int> FindUnchangedNewLineIndices(string[] oldLines, string[] newLines)
        => AnalyzeLineDiff(oldLines, newLines).UnchangedNewIndices;

    /// <summary>
    /// Counts the number of '\n' characters before the given character index.
    /// This converts a character offset in normalized content to a 0-based line number.
    /// </summary>
    internal static int CountNewlinesBefore(string text, int charIndex)
    {
        int count = 0;
        int limit = Math.Min(charIndex, text.Length);
        for (int i = 0; i < limit; i++)
        {
            if (text[i] == '\n')
                count++;
        }
        return count;
    }

    private static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n").Replace("\r", "\n");
}
