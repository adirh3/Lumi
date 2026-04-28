using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Lumi.Services;

/// <summary>
/// Searches for files in a directory, respecting .gitignore rules and hardcoded ignore patterns.
/// Designed to be used from the # file autocomplete in the chat composer.
///
/// Performance strategy:
/// 1. On first search, walks the filesystem once and caches all eligible file paths.
/// 2. Subsequent searches filter the in-memory cache — zero filesystem IO.
/// 3. Incremental narrowing: if the user types "ch" → "cha" → "chat", each keystroke
///    filters the previous result set instead of re-scanning.
/// 4. Cache auto-expires after <see cref="CacheTtl"/> to pick up new files.
/// </summary>
public sealed class FileSearchService
{
    // ── Cache state ─────────────────────────────────────────────────

    private string? _cachedDir;
    private int _cachedMaxDepth;
    private List<CachedFile>? _fileIndex;
    private long _fileIndexTimestamp; // Stopwatch ticks when index was built

    private string? _cachedGitIgnoreDir;
    private List<GitIgnoreRule>? _cachedGitIgnoreRules;

    // Previous search state for incremental narrowing
    private string? _prevQuery;
    private List<ScoredFile>? _prevResults;

    private static readonly long CacheTtl = Stopwatch.Frequency * 30; // 30 seconds

    /// <summary>
    /// Searches for files matching <paramref name="query"/> in <paramref name="workDir"/>.
    /// Returns scored, ranked (relativePath, fullPath) tuples.
    /// </summary>
    public List<(string RelativePath, string FullPath)> Search(string workDir, string query, int maxResults = 20, int maxDepth = 10)
    {
        if (!Directory.Exists(workDir)) return [];

        // Ensure file index is populated and fresh
        var index = GetOrBuildIndex(workDir, maxDepth);
        var compiledQuery = CompiledFileQuery.Create(query);

        var hasQuery = compiledQuery.Terms.Length > 0;

        if (!hasQuery)
        {
            _prevQuery = null;
            _prevResults = null;
            var count = Math.Min(index.Count, maxResults);
            var list = new List<(string, string)>(count);
            for (var i = 0; i < count; i++)
                list.Add((index[i].RelativePath, index[i].FullPath));
            return list;
        }

        // ── Incremental narrowing ───────────────────────────────────
        // If the new query is a refinement of the previous one (user typed more),
        // filter the previous scored results instead of rescanning the full index.
        if (_prevResults is not null &&
            _prevQuery is not null &&
            query.StartsWith(_prevQuery, StringComparison.OrdinalIgnoreCase) &&
            _prevResults.Count > 0)
        {
            var narrowed = new List<ScoredFile>();
            foreach (var prev in _prevResults)
            {
                var score = ScoreMatch(prev.RelativePath, compiledQuery);
                if (score > 0)
                    narrowed.Add(new ScoredFile(prev.RelativePath, prev.FullPath, score));
            }

            narrowed.Sort(CompareScoredFiles);

            _prevQuery = query;
            _prevResults = narrowed;

            return TakeTopResults(narrowed, maxResults);
        }

        // ── Full index scan ─────────────────────────────────────────
        // No candidate limit — the index is in-memory so scanning all entries is fast.
        // A limit would cause different results depending on which intermediate queries
        // were processed (timing-dependent), breaking determinism.
        var candidates = new List<ScoredFile>();

        foreach (var file in index)
        {
            var score = ScoreMatch(file, compiledQuery);
            if (score > 0)
                candidates.Add(new ScoredFile(file.RelativePath, file.FullPath, score));
        }

        candidates.Sort(CompareScoredFiles);

        _prevQuery = query;
        _prevResults = candidates;

        return TakeTopResults(candidates, maxResults);
    }

    /// <summary>Invalidates the file index cache, forcing a fresh filesystem walk on next search.</summary>
    public void InvalidateCache()
    {
        _fileIndex = null;
        _cachedDir = null;
        _prevQuery = null;
        _prevResults = null;
    }

    /// <summary>Score descending, then path length ascending (shorter = more relevant).</summary>
    private static int CompareScoredFiles(ScoredFile a, ScoredFile b)
    {
        var cmp = b.Score.CompareTo(a.Score);
        if (cmp != 0)
            return cmp;

        cmp = a.RelativePath.Length.CompareTo(b.RelativePath.Length);
        return cmp != 0
            ? cmp
            : StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath);
    }

    private static List<(string, string)> TakeTopResults(List<ScoredFile> scored, int maxResults)
    {
        var count = Math.Min(scored.Count, maxResults);
        var results = new List<(string, string)>(count);
        for (var i = 0; i < count; i++)
            results.Add((scored[i].RelativePath, scored[i].FullPath));
        return results;
    }

    // ── File index ──────────────────────────────────────────────────

    private List<CachedFile> GetOrBuildIndex(string workDir, int maxDepth)
    {
        var now = Stopwatch.GetTimestamp();

        // Return cached index if same directory, same depth, and not expired
        if (_fileIndex is not null &&
            _cachedDir == workDir &&
            _cachedMaxDepth == maxDepth &&
            (now - _fileIndexTimestamp) < CacheTtl)
        {
            return _fileIndex;
        }

        // Build new index
        var gitIgnoreRules = GetGitIgnoreRules(workDir);
        var index = new List<CachedFile>();

        EnumerateNonIgnoredFiles(workDir, workDir, gitIgnoreRules, maxDepth, 0, (rel, full) =>
        {
            index.Add(new CachedFile(rel, full, PreparedFileSearchEntry.Create(rel)));
            return true; // collect all files
        });

        _fileIndex = index;
        _cachedDir = workDir;
        _cachedMaxDepth = maxDepth;
        _fileIndexTimestamp = now;
        _prevQuery = null;
        _prevResults = null;

        return index;
    }

    // ── Directory walking (skips ignored subtrees) ──────────────────

    /// <summary>
    /// Manually walks the directory tree, skipping ignored directories at the directory level
    /// so we never enter bin/, obj/, node_modules/, .git/ etc.
    /// </summary>
    private static void EnumerateNonIgnoredFiles(
        string rootDir,
        string currentDir,
        List<GitIgnoreRule> gitIgnoreRules,
        int maxDepth,
        int currentDepth,
        Func<string, string, bool> onFile)
    {
        // Enumerate files in current directory first
        try
        {
            foreach (var fullPath in Directory.EnumerateFiles(currentDir))
            {
                var relativePath = Path.GetRelativePath(rootDir, fullPath);
                if (IsIgnoredFile(relativePath, gitIgnoreRules))
                    continue;
                if (!onFile(relativePath, fullPath))
                    return;
            }
        }
        catch { /* access denied, etc */ }

        if (currentDepth >= maxDepth) return;

        // Then recurse into non-ignored subdirectories
        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(currentDir))
            {
                var dirName = Path.GetFileName(subDir);

                // Fast skip: hardcoded ignored directories
                if (IsHardcodedIgnoredDirName(dirName))
                    continue;

                // Check gitignore directory rules
                var relativeDir = Path.GetRelativePath(rootDir, subDir);
                if (IsIgnoredDirectory(relativeDir, gitIgnoreRules))
                    continue;

                EnumerateNonIgnoredFiles(rootDir, subDir, gitIgnoreRules, maxDepth, currentDepth + 1, onFile);
            }
        }
        catch { /* access denied, etc */ }
    }

    // ── Scoring ─────────────────────────────────────────────────────

    /// <summary>
    /// Scores a file path against query parts. Returns 0 if no match.
    /// Higher scores mean better matches.
    ///
    /// Scoring tiers (base score before bonuses/penalties):
    ///   100 — exact filename match (query == filename)
    ///    95 — exact filename-without-extension match
    ///    80 — filename starts with query
    ///    60 — filename contains query as substring
    ///    30 — path-only match (query appears in directory, not filename)
    ///
    /// Bonuses:
    ///   +20 max — coverage ratio: how much of the filename the query covers
    ///   +10     — multi-term: all query terms appear in the filename itself
    ///   + 5     — source file bonus (.cs, .ts, .py, etc.) over non-source files
    ///
    /// Penalties:
    ///   -1 per  — directory depth (shallower files win ties)
    ///
    /// Fuzzy matching (when substring match fails):
    ///    45 — fuzzy match in filename (query chars appear in order)
    ///    15 — fuzzy match in path only
    ///   +15 max — consecutive-char bonus (rewards tighter matches)
    /// </summary>
    internal static int ScoreMatch(string relativePath, string[] queryParts)
    {
        return ScoreMatch(relativePath, CompiledFileQuery.Create(queryParts));
    }

    private static int ScoreMatch(string relativePath, CompiledFileQuery query)
    {
        return ScoreMatch(
            new CachedFile(relativePath, relativePath, PreparedFileSearchEntry.Create(relativePath)),
            query);
    }

    private static int ScoreMatch(CachedFile file, CompiledFileQuery query)
    {
        if (query.Terms.Length == 0)
            return 30;

        var phraseScore = ScorePhrase(file.Prepared, query);
        var totalTermScore = 0d;
        var fileNameTermMatches = 0;

        foreach (var term in query.Terms)
        {
            var match = ScoreTerm(file.Prepared, term);
            if (match.Score <= 0)
                return 0;

            totalTermScore += match.Score;
            if (match.MatchedInFileName)
                fileNameTermMatches++;
        }

        var score = Math.Max(phraseScore, totalTermScore);

        if (fileNameTermMatches == query.Terms.Length)
            score += 65;
        else if (fileNameTermMatches > 0)
            score += fileNameTermMatches * 18;

        if (query.Terms.Length > 1 && fileNameTermMatches == query.Terms.Length)
            score += 20;

        if (file.Prepared.IsSourceFile)
            score += 24;

        score += GetCoverageBonus(file.Prepared.FileNameNoExtCompact.Length, query.CompactPhrase.Length);
        score -= file.Prepared.Depth * 2;

        return (int)Math.Max(Math.Round(score), 1);
    }

    /// <summary>
    /// Checks if <paramref name="query"/> is a subsequence of <paramref name="text"/>
    /// (all query characters appear in order, case-insensitive).
    /// Returns (isMatch, consecutiveBonus) where consecutiveBonus rewards runs of
    /// consecutive matching characters (higher = tighter match).
    /// </summary>
    internal static (bool IsMatch, int ConsecutiveBonus) FuzzyMatch(string text, string query)
    {
        if (query.Length == 0) return (true, 0);
        if (query.Length > text.Length) return (false, 0);

        var qi = 0;
        var consecutive = 0;
        var maxConsecutive = 0;
        var totalConsecutive = 0;
        var lastMatchIndex = -2;

        for (var ti = 0; ti < text.Length && qi < query.Length; ti++)
        {
            if (char.ToLowerInvariant(text[ti]) == char.ToLowerInvariant(query[qi]))
            {
                if (ti == lastMatchIndex + 1)
                {
                    consecutive++;
                    if (consecutive > maxConsecutive)
                        maxConsecutive = consecutive;
                }
                else
                {
                    consecutive = 1;
                }
                totalConsecutive += consecutive;
                lastMatchIndex = ti;
                qi++;
            }
        }

        if (qi < query.Length)
            return (false, 0);

        // Bonus formula: reward long consecutive runs
        // e.g., "chtvw" matching "ChatView" has runs of 2+1+1+1 = lower bonus
        //        "chatvi" matching "ChatView" has a run of 6 = higher bonus
        var bonus = maxConsecutive + (totalConsecutive / 2);
        return (true, bonus);
    }

    private static double ScorePhrase(PreparedFileSearchEntry file, CompiledFileQuery query)
    {
        var score = 0d;
        if (!string.IsNullOrEmpty(query.NormalizedPhrase))
        {
            if (string.Equals(file.FileName, query.NormalizedPhrase, StringComparison.Ordinal))
                score = Math.Max(score, 610);
            if (string.Equals(file.FileNameNoExt, query.NormalizedPhrase, StringComparison.Ordinal))
                score = Math.Max(score, 590);
            if (string.Equals(file.NormalizedPath, query.NormalizedPhrase, StringComparison.Ordinal))
                score = Math.Max(score, 570);
        }

        if (!string.IsNullOrEmpty(query.CompactPhrase))
        {
            if (string.Equals(file.FileNameCompact, query.CompactPhrase, StringComparison.Ordinal))
                score = Math.Max(score, 600);
            if (string.Equals(file.FileNameNoExtCompact, query.CompactPhrase, StringComparison.Ordinal))
                score = Math.Max(score, 585);
            if (string.Equals(file.PathCompact, query.CompactPhrase, StringComparison.Ordinal))
                score = Math.Max(score, 550);
        }

        return score;
    }

    private static FileTermMatch ScoreTerm(PreparedFileSearchEntry file, CompiledFileQueryTerm term)
    {
        var bestScore = 0d;
        var matchedInFileName = false;

        void Consider(double score, bool inFileName)
        {
            if (score <= bestScore)
                return;

            bestScore = score;
            matchedInFileName = inFileName;
        }

        if (!string.IsNullOrEmpty(term.Normalized))
        {
            if (string.Equals(file.FileName, term.Normalized, StringComparison.Ordinal))
                Consider(600, true);
            if (string.Equals(file.FileNameNoExt, term.Normalized, StringComparison.Ordinal))
                Consider(590, true);
        }

        if (!string.IsNullOrEmpty(term.Compact))
        {
            if (string.Equals(file.FileNameCompact, term.Compact, StringComparison.Ordinal))
                Consider(595, true);
            if (string.Equals(file.FileNameNoExtCompact, term.Compact, StringComparison.Ordinal))
                Consider(585, true);

            Consider(ScoreInitials(file.FileNameInitials, term.Compact, 585, 540, 500), true);
            Consider(ScoreInitials(file.PathInitials, term.Compact, 390, 350, 310), false);

            Consider(ScorePrefix(file.FileNameNoExtCompact, term.Compact, 560), true);
            Consider(ScoreContains(file.FileNameNoExtCompact, term.Compact, 470), true);
            Consider(ScoreApproximateContains(file.FileNameNoExtCompact, term.Compact, 445), true);
            Consider(ScoreSubsequence(file.FileNameNoExtCompact, term.Compact, 420), true);
            Consider(ScoreEditDistance(file.FileNameNoExtCompact, term.Compact, 450), true);

            for (var index = 0; index < file.FileNameTokens.Length; index++)
            {
                var token = file.FileNameTokens[index];
                var positionPenalty = index * 28;
                if (string.Equals(token, term.Compact, StringComparison.Ordinal))
                {
                    Consider(575 - positionPenalty, true);
                    continue;
                }

                Consider(ScorePrefix(token, term.Compact, 545 - positionPenalty), true);
                Consider(ScoreContains(token, term.Compact, 465 - positionPenalty), true);
                Consider(ScoreApproximateContains(token, term.Compact, 430 - positionPenalty), true);
                Consider(ScoreSubsequence(token, term.Compact, 405 - positionPenalty), true);
                Consider(ScoreEditDistance(token, term.Compact, 425 - positionPenalty), true);
            }

            foreach (var token in file.PathTokens)
            {
                if (string.Equals(token, term.Compact, StringComparison.Ordinal))
                {
                    Consider(390, false);
                    continue;
                }

                Consider(ScorePrefix(token, term.Compact, 350), false);
                Consider(ScoreContains(token, term.Compact, 305), false);
                Consider(ScoreApproximateContains(token, term.Compact, 260), false);
                Consider(ScoreSubsequence(token, term.Compact, 150), false);
            }

            Consider(ScoreSubsequence(file.PathCompact, term.Compact, 110), false);
        }

        if (!string.IsNullOrEmpty(term.Normalized))
        {
            Consider(ScorePrefix(file.FileNameNoExt, term.Normalized, 555), true);
            Consider(ScoreContains(file.FileNameNoExt, term.Normalized, 475), true);
            Consider(ScorePrefix(file.NormalizedPath, term.Normalized, 330), false);
            Consider(ScoreContains(file.NormalizedPath, term.Normalized, 285), false);

            foreach (var segment in file.PathSegments)
            {
                if (string.Equals(segment, term.Normalized, StringComparison.Ordinal))
                {
                    Consider(400, false);
                    continue;
                }

                Consider(ScorePrefix(segment, term.Normalized, 360), false);
                Consider(ScoreContains(segment, term.Normalized, 310), false);
            }
        }

        return new FileTermMatch(bestScore, matchedInFileName);
    }

    private static double ScorePrefix(string candidate, string query, double baseScore)
    {
        if (string.IsNullOrEmpty(candidate)
            || string.IsNullOrEmpty(query)
            || !candidate.StartsWith(query, StringComparison.Ordinal))
        {
            return 0;
        }

        var lengthPenalty = Math.Max(0, candidate.Length - query.Length) * 5;
        return baseScore - lengthPenalty;
    }

    private static double ScoreContains(string candidate, string query, double baseScore)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(query))
            return 0;

        var index = candidate.IndexOf(query, StringComparison.Ordinal);
        if (index < 0)
            return 0;

        var positionPenalty = index * 18;
        var lengthPenalty = Math.Max(0, candidate.Length - query.Length) * 3;
        return baseScore - positionPenalty - lengthPenalty;
    }

    private static double ScoreInitials(
        string initials,
        string query,
        double exactScore,
        double prefixScore,
        double subsequenceScore)
    {
        if (string.IsNullOrEmpty(initials) || string.IsNullOrEmpty(query))
            return 0;

        if (string.Equals(initials, query, StringComparison.Ordinal))
            return exactScore;

        return Math.Max(
            ScorePrefix(initials, query, prefixScore),
            ScoreSubsequence(initials, query, subsequenceScore));
    }

    private static double ScoreApproximateContains(string candidate, string query, double baseScore)
    {
        if (string.IsNullOrEmpty(candidate) || query.Length < 5 || candidate.Length < 3)
            return 0;

        var maxDistance = GetApproximateMaxDistance(query.Length);
        var minWindowLength = Math.Max(3, query.Length - Math.Min(maxDistance, 1));
        var maxWindowLength = Math.Min(candidate.Length, query.Length + maxDistance);
        if (minWindowLength > maxWindowLength)
            return 0;

        var bestScore = 0d;
        for (var windowLength = minWindowLength; windowLength <= maxWindowLength; windowLength++)
        {
            for (var start = 0; start <= candidate.Length - windowLength; start++)
            {
                var window = candidate.Substring(start, windowLength);
                var distance = DamerauLevenshteinDistance(window, query, maxDistance);
                if (distance <= 0 || distance > maxDistance)
                    continue;

                var score = baseScore
                            - (distance * 95)
                            - (Math.Abs(windowLength - query.Length) * 30)
                            - (start * 14)
                            + (Math.Min(1d, (double)query.Length / candidate.Length) * 45);
                if (score > bestScore)
                    bestScore = score;
            }
        }

        return bestScore;
    }

    private static double ScoreSubsequence(string candidate, string query, double baseScore)
    {
        if (string.IsNullOrEmpty(candidate) || query.Length < 2 || candidate.Length < query.Length)
            return 0;

        var (isMatch, consecutiveBonus) = FuzzyMatch(candidate, query);
        if (!isMatch)
            return 0;

        var firstMatchIndex = candidate.IndexOf(query[0]);
        var coverage = (double)query.Length / candidate.Length;
        return baseScore
               + Math.Min(consecutiveBonus, 24) * 8
               + (coverage * 90)
               - Math.Max(0, firstMatchIndex) * 10;
    }

    private static double ScoreEditDistance(string candidate, string query, double baseScore)
    {
        if (candidate.Length < 3
            || query.Length < 3
            || Math.Abs(candidate.Length - query.Length) > 2)
        {
            return 0;
        }

        var maxDistance = GetApproximateMaxDistance(query.Length);
        var distance = DamerauLevenshteinDistance(candidate, query, maxDistance);
        if (distance > maxDistance)
            return 0;

        return baseScore
               - (distance * 110)
               - (Math.Abs(candidate.Length - query.Length) * 35);
    }

    private static int GetApproximateMaxDistance(int queryLength)
    {
        return queryLength switch
        {
            <= 4 => 1,
            <= 8 => 2,
            <= 14 => 3,
            _ => 4
        };
    }

    private static double GetCoverageBonus(int candidateLength, int queryLength)
    {
        if (candidateLength <= 0 || queryLength <= 0)
            return 0;

        var coverage = Math.Min(1d, (double)queryLength / candidateLength);
        return coverage * 40;
    }

    internal static string NormalizePathText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var builder = new StringBuilder(text.Length);
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;

            if (character == '\\' || character == '/')
            {
                if (builder.Length > 0 && builder[^1] != '/')
                    builder.Append('/');
                continue;
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString().Trim('/');
    }

    internal static string ToCompact(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var builder = new StringBuilder(text.Length);
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    internal static string[] ExtractSearchTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var tokens = new List<string>();
        var builder = new StringBuilder();
        var previousCharacter = '\0';

        void Flush()
        {
            if (builder.Length == 0)
                return;

            tokens.Add(builder.ToString());
            builder.Clear();
        }

        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;

            if (!char.IsLetterOrDigit(character))
            {
                Flush();
                previousCharacter = '\0';
                continue;
            }

            var startsNewToken = builder.Length > 0
                                 && ((char.IsUpper(character) && char.IsLower(previousCharacter))
                                     || (char.IsDigit(character) != char.IsDigit(previousCharacter)));

            if (startsNewToken)
                Flush();

            builder.Append(char.ToLowerInvariant(character));
            previousCharacter = character;
        }

        Flush();
        return tokens.Count == 0 ? [] : tokens.ToArray();
    }

    internal static string BuildInitials(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
            return "";

        var builder = new StringBuilder(tokens.Count);
        foreach (var token in tokens)
            builder.Append(token[0]);

        return builder.ToString();
    }

    private static int DamerauLevenshteinDistance(string source, string target, int maxDistance)
    {
        if (source.Length == 0)
            return target.Length;
        if (target.Length == 0)
            return source.Length;

        if (Math.Abs(source.Length - target.Length) > maxDistance)
            return maxDistance + 1;

        var previous = new int[target.Length + 1];
        var current = new int[target.Length + 1];
        var transposition = new int[target.Length + 1];

        for (var j = 0; j <= target.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= source.Length; i++)
        {
            current[0] = i;
            var rowMinimum = current[0];

            for (var j = 1; j <= target.Length; j++)
            {
                var substitutionCost = source[i - 1] == target[j - 1] ? 0 : 1;
                var value = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + substitutionCost);

                if (i > 1
                    && j > 1
                    && source[i - 1] == target[j - 2]
                    && source[i - 2] == target[j - 1])
                {
                    value = Math.Min(value, transposition[j - 2] + 1);
                }

                current[j] = value;
                rowMinimum = Math.Min(rowMinimum, value);
            }

            if (rowMinimum > maxDistance)
                return maxDistance + 1;

            (transposition, previous, current) = (previous, current, transposition);
        }

        return previous[target.Length];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsSourceFileExtension(string ext)
    {
        return ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".tsx", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jsx", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".py", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".java", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".go", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".rs", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".cpp", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".c", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".h", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".swift", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".rb", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".axaml", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".xaml", StringComparison.OrdinalIgnoreCase);
    }

    // ── Ignore logic ────────────────────────────────────────────────

    /// <summary>Checks whether a relative file path should be excluded from search results.</summary>
    internal static bool IsIgnoredPath(string relativePath, List<GitIgnoreRule> gitIgnoreRules)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (IsHardcodedIgnoredPath(normalized))
            return true;
        return IsGitIgnored(normalized, gitIgnoreRules);
    }

    /// <summary>Checks if a file is ignored (called with relativePath already computed).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIgnoredFile(string relativePath, List<GitIgnoreRule> gitIgnoreRules)
    {
        // The file-level check only needs gitignore file rules (hardcoded dirs already skipped in walk)
        if (gitIgnoreRules.Count == 0) return false;
        var normalized = relativePath.Replace('\\', '/');
        return IsGitIgnored(normalized, gitIgnoreRules);
    }

    /// <summary>Checks if a directory should be skipped entirely during walk.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIgnoredDirectory(string relativeDirPath, List<GitIgnoreRule> gitIgnoreRules)
    {
        if (gitIgnoreRules.Count == 0) return false;
        var normalized = relativeDirPath.Replace('\\', '/');

        // Check directory-applicable gitignore rules
        var ignored = false;
        foreach (var rule in gitIgnoreRules)
        {
            if (GitIgnoreMatchesDir(normalized, rule))
                ignored = !rule.IsNegation;
        }
        return ignored;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsGitIgnored(string normalizedPath, List<GitIgnoreRule> gitIgnoreRules)
    {
        var ignored = false;
        foreach (var rule in gitIgnoreRules)
        {
            if (GitIgnoreMatches(normalizedPath, rule))
                ignored = !rule.IsNegation;
        }
        return ignored;
    }

    /// <summary>Fast check for directory names that are always ignored.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsHardcodedIgnoredDirName(string dirName)
    {
        return dirName.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals("__pycache__", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals(".next", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals("dist", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals(".nuget", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals("packages", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsHardcodedIgnoredPath(string normalizedPath)
    {
        var span = normalizedPath.AsSpan();
        while (span.Length > 0)
        {
            var sepIndex = span.IndexOf('/');
            var segment = sepIndex >= 0 ? span.Slice(0, sepIndex) : span;

            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("__pycache__", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".next", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("dist", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".nuget", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("packages", StringComparison.OrdinalIgnoreCase))
                return true;

            if (sepIndex < 0) break;
            span = span.Slice(sepIndex + 1);
        }
        return false;
    }

    // ── .gitignore parsing ──────────────────────────────────────────

    internal List<GitIgnoreRule> GetGitIgnoreRules(string workDir)
    {
        if (_cachedGitIgnoreDir == workDir && _cachedGitIgnoreRules is not null)
            return _cachedGitIgnoreRules;

        _cachedGitIgnoreDir = workDir;
        _cachedGitIgnoreRules = ParseGitIgnoreFile(Path.Combine(workDir, ".gitignore"));
        return _cachedGitIgnoreRules;
    }

    /// <summary>Parses a .gitignore file into a list of rules.</summary>
    internal static List<GitIgnoreRule> ParseGitIgnoreFile(string gitIgnorePath)
    {
        if (!File.Exists(gitIgnorePath))
            return [];

        try
        {
            return ParseGitIgnoreLines(File.ReadLines(gitIgnorePath));
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Parses .gitignore lines into rules. Separated for testability.</summary>
    internal static List<GitIgnoreRule> ParseGitIgnoreLines(IEnumerable<string> lines)
    {
        var rules = new List<GitIgnoreRule>();
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            var isNegation = false;
            if (line[0] == '!')
            {
                isNegation = true;
                line = line[1..];
            }

            var isDirectoryOnly = line.EndsWith('/');
            if (isDirectoryOnly)
                line = line.TrimEnd('/');

            // Remove leading slash (anchored to root)
            line = line.TrimStart('/');

            if (line.Length > 0)
                rules.Add(new GitIgnoreRule(line, isNegation, isDirectoryOnly));
        }
        return rules;
    }

    // ── Gitignore matching ──────────────────────────────────────────

    internal static bool GitIgnoreMatches(string normalizedPath, GitIgnoreRule rule)
    {
        var pattern = rule.Pattern;

        // If pattern contains '/', match against the full path
        if (pattern.Contains('/'))
            return GlobMatch(normalizedPath, pattern);

        // Match pattern against each path segment
        var pathSpan = normalizedPath.AsSpan();
        while (pathSpan.Length > 0)
        {
            var sep = pathSpan.IndexOf('/');
            var segment = sep >= 0 ? pathSpan.Slice(0, sep) : pathSpan;

            // Directory-only rules only match segments followed by '/'
            if (!rule.IsDirectoryOnly || sep >= 0)
            {
                if (GlobMatch(segment, pattern))
                    return true;
            }

            if (sep < 0) break;
            pathSpan = pathSpan.Slice(sep + 1);
        }

        // For non-directory-only patterns, also try against the full path (e.g. "*.log")
        if (!rule.IsDirectoryOnly)
            return GlobMatch(normalizedPath, pattern);

        return false;
    }

    /// <summary>Matches a directory path against a gitignore rule.</summary>
    private static bool GitIgnoreMatchesDir(string normalizedDirPath, GitIgnoreRule rule)
    {
        var pattern = rule.Pattern;

        // If pattern contains '/', match against the full dir path
        if (pattern.Contains('/'))
            return GlobMatch(normalizedDirPath, pattern);

        // Match pattern against each segment of the dir path
        var pathSpan = normalizedDirPath.AsSpan();
        while (pathSpan.Length > 0)
        {
            var sep = pathSpan.IndexOf('/');
            var segment = sep >= 0 ? pathSpan.Slice(0, sep) : pathSpan;

            if (GlobMatch(segment, pattern))
                return true;

            if (sep < 0) break;
            pathSpan = pathSpan.Slice(sep + 1);
        }

        return false;
    }

    // ── Glob matching ───────────────────────────────────────────────

    /// <summary>
    /// Glob pattern matching supporting *, ?, and [...] character classes.
    /// Case-insensitive. Used for .gitignore pattern evaluation.
    /// </summary>
    internal static bool GlobMatch(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        int ti = 0, pi = 0;
        int starTi = -1, starPi = -1;

        while (ti < text.Length)
        {
            if (pi < pattern.Length && pattern[pi] == '*')
            {
                while (pi < pattern.Length && pattern[pi] == '*') pi++;
                starPi = pi;
                starTi = ti;
                continue;
            }

            if (pi < pattern.Length && pattern[pi] == '?')
            {
                ti++;
                pi++;
                continue;
            }

            if (pi < pattern.Length && pattern[pi] == '[')
            {
                if (MatchCharClass(text[ti], pattern, ref pi))
                {
                    ti++;
                    continue;
                }
                if (starPi >= 0)
                {
                    pi = starPi;
                    ti = ++starTi;
                    continue;
                }
                return false;
            }

            if (pi < pattern.Length && char.ToLowerInvariant(pattern[pi]) == char.ToLowerInvariant(text[ti]))
            {
                ti++;
                pi++;
                continue;
            }

            if (starPi >= 0)
            {
                pi = starPi;
                ti = ++starTi;
                continue;
            }

            return false;
        }

        while (pi < pattern.Length && pattern[pi] == '*')
            pi++;

        return pi == pattern.Length;
    }

    /// <summary>
    /// Matches a character against a [...] character class in the pattern.
    /// Advances <paramref name="pi"/> past the closing ']'.
    /// Supports ranges like [a-z], negation [!...] / [^...], and literal characters.
    /// </summary>
    private static bool MatchCharClass(char ch, ReadOnlySpan<char> pattern, ref int pi)
    {
        // pi points to '['
        pi++; // skip '['
        if (pi >= pattern.Length) return false;

        var negate = false;
        if (pattern[pi] == '!' || pattern[pi] == '^')
        {
            negate = true;
            pi++;
        }

        var matched = false;
        var first = true;

        while (pi < pattern.Length && (first || pattern[pi] != ']'))
        {
            first = false;
            var lo = pattern[pi];
            pi++;

            // Check for range: [a-z]
            if (pi + 1 < pattern.Length && pattern[pi] == '-' && pattern[pi + 1] != ']')
            {
                var hi = pattern[pi + 1];
                pi += 2;
                if (char.ToLowerInvariant(ch) >= char.ToLowerInvariant(lo) &&
                    char.ToLowerInvariant(ch) <= char.ToLowerInvariant(hi))
                    matched = true;
            }
            else
            {
                if (char.ToLowerInvariant(ch) == char.ToLowerInvariant(lo))
                    matched = true;
            }
        }

        // Skip past ']'
        if (pi < pattern.Length && pattern[pi] == ']')
            pi++;

        return negate ? !matched : matched;
    }
}

/// <summary>A single parsed .gitignore rule.</summary>
public readonly record struct GitIgnoreRule(string Pattern, bool IsNegation, bool IsDirectoryOnly);

/// <summary>A file with a match score for ranking.</summary>
internal readonly record struct ScoredFile(string RelativePath, string FullPath, int Score);

/// <summary>A cached file entry in the index.</summary>
internal readonly record struct CachedFile(string RelativePath, string FullPath, PreparedFileSearchEntry Prepared);

internal readonly record struct FileTermMatch(double Score, bool MatchedInFileName);

internal readonly record struct CompiledFileQuery(string NormalizedPhrase, string CompactPhrase, CompiledFileQueryTerm[] Terms)
{
    public static CompiledFileQuery Create(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new CompiledFileQuery("", "", []);

        return Create(query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public static CompiledFileQuery Create(string[] queryParts)
    {
        if (queryParts.Length == 0)
            return new CompiledFileQuery("", "", []);

        var normalizedParts = new List<string>(queryParts.Length);
        var compactBuilder = new StringBuilder();
        var terms = new List<CompiledFileQueryTerm>(queryParts.Length);

        foreach (var part in queryParts)
        {
            var term = CompiledFileQueryTerm.Create(part);
            if (string.IsNullOrEmpty(term.Normalized) && string.IsNullOrEmpty(term.Compact))
                continue;

            terms.Add(term);
            if (!string.IsNullOrEmpty(term.Normalized))
                normalizedParts.Add(term.Normalized);
            if (!string.IsNullOrEmpty(term.Compact))
                compactBuilder.Append(term.Compact);
        }

        return new CompiledFileQuery(
            string.Join(" ", normalizedParts),
            compactBuilder.ToString(),
            terms.Count == 0 ? [] : terms.ToArray());
    }
}

internal readonly record struct CompiledFileQueryTerm(string Normalized, string Compact)
{
    public static CompiledFileQueryTerm Create(string value)
    {
        return new CompiledFileQueryTerm(
            FileSearchService.NormalizePathText(value),
            FileSearchService.ToCompact(value));
    }
}

internal readonly record struct PreparedFileSearchEntry(
    string NormalizedPath,
    string PathCompact,
    string[] PathSegments,
    string[] PathTokens,
    string FileName,
    string FileNameNoExt,
    string FileNameCompact,
    string FileNameNoExtCompact,
    string[] FileNameTokens,
    string FileNameInitials,
    string PathInitials,
    int Depth,
    bool IsSourceFile)
{
    public static PreparedFileSearchEntry Create(string relativePath)
    {
        var normalizedPath = FileSearchService.NormalizePathText(relativePath);
        var pathSegments = normalizedPath.Length == 0
            ? []
            : normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var originalSegments = relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var originalFileName = originalSegments.Length == 0 ? relativePath : originalSegments[^1];
        var originalExtension = Path.GetExtension(originalFileName);
        var originalFileNameNoExt = string.IsNullOrEmpty(originalExtension)
            ? originalFileName
            : originalFileName[..^originalExtension.Length];

        var pathTokens = FileSearchService.ExtractSearchTokens(relativePath);
        var fileName = pathSegments.Length == 0 ? normalizedPath : pathSegments[^1];
        var extension = originalExtension;
        var fileNameNoExt = string.IsNullOrEmpty(extension)
            ? fileName
            : fileName[..^extension.Length];
        var fileNameTokens = FileSearchService.ExtractSearchTokens(originalFileNameNoExt);
        var pathForInitials = relativePath.Replace('\\', '/');
        if (!string.IsNullOrEmpty(originalExtension))
        {
            var extensionIndex = pathForInitials.LastIndexOf(originalExtension, StringComparison.Ordinal);
            if (extensionIndex >= 0)
                pathForInitials = pathForInitials.Remove(extensionIndex, originalExtension.Length);
        }

        return new PreparedFileSearchEntry(
            normalizedPath,
            FileSearchService.ToCompact(normalizedPath),
            pathSegments,
            pathTokens,
            fileName,
            fileNameNoExt,
            FileSearchService.ToCompact(fileName),
            FileSearchService.ToCompact(fileNameNoExt),
            fileNameTokens,
            FileSearchService.BuildInitials(fileNameTokens),
            FileSearchService.BuildInitials(FileSearchService.ExtractSearchTokens(pathForInitials)),
            pathSegments.Length > 0 ? pathSegments.Length - 1 : 0,
            FileSearchService.IsSourceFileExtension(extension));
    }
}
