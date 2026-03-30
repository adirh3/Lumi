using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public class DiffAlgorithmTests
{
    // ───────────────────── ComputeChangedLines ─────────────────────

    [Fact]
    public void IsCreate_MarksAllLines()
    {
        var content = "line1\nline2\nline3";
        var edits = new List<(string?, string?)>();

        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: true);

        Assert.Equal(new HashSet<int> { 0, 1, 2 }, result.ChangedLines);
        Assert.Empty(result.Deletions);
    }

    [Fact]
    public void EmptyContent_ReturnsEmpty()
    {
        var result = DiffAlgorithm.ComputeChangedLines(
            "", new List<(string?, string?)> { (null, "hello") }, isCreate: false);

        Assert.Empty(result.ChangedLines);
        Assert.Empty(result.Deletions);
    }

    [Fact]
    public void NullNewText_NothingHighlighted()
    {
        var content = "line1\nline2\nline3";
        var edits = new List<(string?, string?)> { ("line2", null) };

        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Empty(result.ChangedLines);
    }

    [Fact]
    public void IdenticalOldAndNew_NothingHighlighted()
    {
        var content = "aaa\nbbb\nccc";
        var edits = new List<(string?, string?)> { ("bbb", "bbb") };

        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Empty(result.ChangedLines);
        Assert.Empty(result.Deletions);
    }

    // ───────────────── Core: Only changed lines highlighted ─────────────────

    [Fact]
    public void SingleLineChange_OnlyChangedLineHighlighted()
    {
        var content = "line1\nNEW_LINE2\nline3";
        var edits = new List<(string?, string?)> { ("old_line2", "NEW_LINE2") };

        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Equal(new HashSet<int> { 1 }, result.ChangedLines);
    }

    [Fact]
    public void MultiLineReplace_OnlyModifiedLinesHighlighted()
    {
        var oldText = "aaa\nbbb\nccc\nddd\neee";
        var newText = "aaa\nBBB\nccc\nDDD\neee";
        var content = $"header\n{newText}\nfooter";

        var edits = new List<(string?, string?)> { (oldText, newText) };

        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Equal(new HashSet<int> { 2, 4 }, result.ChangedLines);
    }

    [Fact]
    public void InsertedLines_AllNewLinesHighlighted()
    {
        var oldText = "aaa\nccc";
        var newText = "aaa\nbbb_new\nccc\nddd_new";
        var content = $"header\n{newText}\nfooter";

        var edits = new List<(string?, string?)> { (oldText, newText) };

        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Equal(new HashSet<int> { 2, 4 }, result.ChangedLines);
    }

    [Fact]
    public void PureInsertion_NoOldText_AllNewLinesHighlighted()
    {
        var content = "before\nnew_line1\nnew_line2\nafter";
        var edits = new List<(string?, string?)> { (null, "new_line1\nnew_line2") };

        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Equal(new HashSet<int> { 1, 2 }, result.ChangedLines);
    }

    [Fact]
    public void MultipleEdits_AllChangesDetected()
    {
        var content = "aaa\nBBB\nccc\nddd\nEEE\nfff";
        var edits = new List<(string?, string?)>
        {
            ("bbb", "BBB"),
            ("eee", "EEE"),
        };

        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Equal(new HashSet<int> { 1, 4 }, result.ChangedLines);
    }

    // ───────────────── Line endings ─────────────────

    [Fact]
    public void WindowsLineEndings_HandleCorrectly()
    {
        var content = "line1\r\nNEW_LINE2\r\nline3";
        var edits = new List<(string?, string?)> { ("old_line2", "NEW_LINE2") };

        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Equal(new HashSet<int> { 1 }, result.ChangedLines);
    }

    [Fact]
    public void MixedLineEndings_OldCrlfNewLf()
    {
        var content = "aaa\nBBB\nccc";
        var edits = new List<(string?, string?)> { ("aaa\r\nbbb\r\nccc", "aaa\nBBB\nccc") };

        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Equal(new HashSet<int> { 1 }, result.ChangedLines);
    }

    // ───────────────── Edge cases ─────────────────

    [Fact]
    public void EditAtStartOfFile()
    {
        var content = "NEW_FIRST\nline2\nline3";
        var edits = new List<(string?, string?)> { ("old_first", "NEW_FIRST") };

        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Equal(new HashSet<int> { 0 }, result.ChangedLines);
    }

    [Fact]
    public void EditAtEndOfFile()
    {
        var content = "line1\nline2\nNEW_LAST";
        var edits = new List<(string?, string?)> { ("old_last", "NEW_LAST") };

        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Equal(new HashSet<int> { 2 }, result.ChangedLines);
    }

    [Fact]
    public void SingleLineFile()
    {
        var content = "CHANGED";
        var edits = new List<(string?, string?)> { ("original", "CHANGED") };

        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Equal(new HashSet<int> { 0 }, result.ChangedLines);
    }

    [Fact]
    public void NewTextNotFoundInFile_NothingHighlighted()
    {
        var content = "line1\nline2\nline3";
        var edits = new List<(string?, string?)> { ("line2", "NOT_IN_FILE") };

        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Empty(result.ChangedLines);
    }

    [Fact]
    public void EmptyOldText_TreatedAsInsertion()
    {
        var content = "line1\nnew_content\nline2";
        var edits = new List<(string?, string?)> { ("", "new_content") };

        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Equal(new HashSet<int> { 1 }, result.ChangedLines);
    }

    [Fact]
    public void LargeContextWithSmallChange()
    {
        var lines = new List<string>();
        for (int i = 0; i < 10; i++) lines.Add($"context_line_{i}");
        var oldLines = new List<string>(lines);

        lines[5] = "MODIFIED_LINE_5";

        var content = string.Join("\n", lines);
        var oldText = string.Join("\n", oldLines);
        var newText = string.Join("\n", lines);

        var edits = new List<(string?, string?)> { (oldText, newText) };

        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Single(result.ChangedLines);
        Assert.Contains(5, result.ChangedLines);
    }

    [Fact]
    public void DeletedLines_RemainingLinesNotHighlighted()
    {
        var oldText = "aaa\nbbb\nccc\nddd\neee";
        var newText = "aaa\nccc\neee";
        var content = $"header\n{newText}\nfooter";

        var edits = new List<(string?, string?)> { (oldText, newText) };

        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Empty(result.ChangedLines);
    }

    [Fact]
    public void MixedInsertAndModify()
    {
        var oldText = "aaa\nbbb\nccc";
        var newText = "aaa\nBBB\ninserted\nccc";
        var content = $"header\n{newText}\nfooter";

        var edits = new List<(string?, string?)> { (oldText, newText) };

        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Equal(new HashSet<int> { 2, 3 }, result.ChangedLines);
    }

    // ───────────────── FindUnchangedNewLineIndices ─────────────────

    [Fact]
    public void LCS_AllSame_AllUnchanged()
    {
        var old = new[] { "a", "b", "c" };
        var @new = new[] { "a", "b", "c" };

        var unchanged = DiffAlgorithm.FindUnchangedNewLineIndices(old, @new);

        Assert.Equal(new HashSet<int> { 0, 1, 2 }, unchanged);
    }

    [Fact]
    public void LCS_AllDifferent_NoneUnchanged()
    {
        var old = new[] { "a", "b", "c" };
        var @new = new[] { "x", "y", "z" };

        var unchanged = DiffAlgorithm.FindUnchangedNewLineIndices(old, @new);

        Assert.Empty(unchanged);
    }

    [Fact]
    public void LCS_OneLineChanged()
    {
        var old = new[] { "a", "b", "c" };
        var @new = new[] { "a", "B", "c" };

        var unchanged = DiffAlgorithm.FindUnchangedNewLineIndices(old, @new);

        Assert.Equal(new HashSet<int> { 0, 2 }, unchanged);
    }

    [Fact]
    public void LCS_LinesInserted()
    {
        var old = new[] { "a", "c" };
        var @new = new[] { "a", "b", "c" };

        var unchanged = DiffAlgorithm.FindUnchangedNewLineIndices(old, @new);

        Assert.Equal(new HashSet<int> { 0, 2 }, unchanged);
    }

    [Fact]
    public void LCS_LinesDeleted()
    {
        var old = new[] { "a", "b", "c" };
        var @new = new[] { "a", "c" };

        var unchanged = DiffAlgorithm.FindUnchangedNewLineIndices(old, @new);

        Assert.Equal(new HashSet<int> { 0, 1 }, unchanged);
    }

    [Fact]
    public void LCS_EmptyOld()
    {
        var old = Array.Empty<string>();
        var @new = new[] { "a", "b" };

        var unchanged = DiffAlgorithm.FindUnchangedNewLineIndices(old, @new);

        Assert.Empty(unchanged);
    }

    [Fact]
    public void LCS_EmptyNew()
    {
        var old = new[] { "a", "b" };
        var @new = Array.Empty<string>();

        var unchanged = DiffAlgorithm.FindUnchangedNewLineIndices(old, @new);

        Assert.Empty(unchanged);
    }

    [Fact]
    public void LCS_DuplicateLines_MatchesCorrectly()
    {
        var old = new[] { "x", "a", "x", "b" };
        var @new = new[] { "x", "a", "Y", "x", "b" };

        var unchanged = DiffAlgorithm.FindUnchangedNewLineIndices(old, @new);

        Assert.Contains(0, unchanged);
        Assert.Contains(1, unchanged);
        Assert.DoesNotContain(2, unchanged);
        Assert.Contains(3, unchanged);
        Assert.Contains(4, unchanged);
    }

    // ───────────────── CountNewlinesBefore ─────────────────

    [Fact]
    public void CountNewlines_AtStart()
    {
        Assert.Equal(0, DiffAlgorithm.CountNewlinesBefore("abc\ndef", 0));
    }

    [Fact]
    public void CountNewlines_AfterFirstLine()
    {
        Assert.Equal(1, DiffAlgorithm.CountNewlinesBefore("abc\ndef\nghi", 4));
    }

    [Fact]
    public void CountNewlines_AfterSecondLine()
    {
        Assert.Equal(2, DiffAlgorithm.CountNewlinesBefore("abc\ndef\nghi", 8));
    }

    [Fact]
    public void CountNewlines_AtEnd()
    {
        var text = "a\nb\nc";
        Assert.Equal(2, DiffAlgorithm.CountNewlinesBefore(text, text.Length));
    }

    // ───────────────── Deletion detection ─────────────────

    [Fact]
    public void Deletion_SingleLineRemoved_ShowsDeletionPoint()
    {
        var oldText = "aaa\nbbb\nccc";
        var newText = "aaa\nccc";
        var content = $"header\n{newText}\nfooter";

        var edits = new List<(string?, string?)> { (oldText, newText) };
        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Empty(result.ChangedLines);
        Assert.Single(result.Deletions);
        Assert.Equal(1, result.Deletions[0].AfterLineIndex); // after file line 1 (aaa)
        Assert.Equal(1, result.Deletions[0].Count);
    }

    [Fact]
    public void Deletion_MultipleLinesRemoved_ShowsDeletionCount()
    {
        var oldText = "aaa\nbbb\nccc\nddd\neee";
        var newText = "aaa\neee";
        var content = $"header\n{newText}\nfooter";

        var edits = new List<(string?, string?)> { (oldText, newText) };
        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Empty(result.ChangedLines);
        Assert.Single(result.Deletions);
        Assert.Equal(1, result.Deletions[0].AfterLineIndex); // after file line 1 (aaa)
        Assert.Equal(3, result.Deletions[0].Count);
    }

    [Fact]
    public void Deletion_LinesRemovedAtEnd()
    {
        var oldText = "aaa\nbbb\nccc";
        var newText = "aaa";
        var content = $"header\n{newText}\nfooter";

        var edits = new List<(string?, string?)> { (oldText, newText) };
        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Empty(result.ChangedLines);
        Assert.Single(result.Deletions);
        Assert.Equal(1, result.Deletions[0].AfterLineIndex); // after file line 1 (aaa)
        Assert.Equal(2, result.Deletions[0].Count);
    }

    [Fact]
    public void Deletion_LinesRemovedAtStart()
    {
        var oldText = "aaa\nbbb\nccc";
        var newText = "ccc";
        var content = $"header\n{newText}\nfooter";

        var edits = new List<(string?, string?)> { (oldText, newText) };
        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Empty(result.ChangedLines);
        Assert.Single(result.Deletions);
        // aaa and bbb removed before ccc; local AfterNewLineIndex = -1 → file: startLine + (-1) = 0
        Assert.Equal(0, result.Deletions[0].AfterLineIndex);
        Assert.Equal(2, result.Deletions[0].Count);
    }

    [Fact]
    public void Deletion_MixedChangesAndDeletions()
    {
        var oldText = "aaa\nbbb\nccc\nddd";
        var newText = "aaa\nBBB\nddd";
        var content = $"header\n{newText}\nfooter";

        var edits = new List<(string?, string?)> { (oldText, newText) };
        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Contains(2, result.ChangedLines); // BBB is changed
        // ccc was removed in the same gap as BBB was added → replacement, not pure deletion
        Assert.Empty(result.Deletions);
    }

    [Fact]
    public void Deletion_NoOldText_NoDeletionPoints()
    {
        var content = "line1\nnew_content\nline2";
        var edits = new List<(string?, string?)> { (null, "new_content") };

        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Empty(result.Deletions);
    }

    [Fact]
    public void Deletion_IdenticalEdit_NoDeletionPoints()
    {
        var content = "aaa\nbbb\nccc";
        var edits = new List<(string?, string?)> { ("bbb", "bbb") };

        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Empty(result.Deletions);
    }

    [Fact]
    public void Deletion_MultipleGaps()
    {
        var oldText = "a\nb\nc\nd\ne";
        var newText = "a\nc\ne";
        var content = $"header\n{newText}\nfooter";

        var edits = new List<(string?, string?)> { (oldText, newText) };
        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Empty(result.ChangedLines);
        Assert.Equal(2, result.Deletions.Count);
        Assert.Equal(1, result.Deletions[0].AfterLineIndex); // after a
        Assert.Equal(1, result.Deletions[0].Count); // b removed
        Assert.Equal(2, result.Deletions[1].AfterLineIndex); // after c
        Assert.Equal(1, result.Deletions[1].Count); // d removed
    }

    // ───────────────── AnalyzeLineDiff ─────────────────

    [Fact]
    public void AnalyzeLineDiff_AllDeleted()
    {
        var old = new[] { "a", "b", "c" };
        var @new = Array.Empty<string>();

        var (unchanged, deletions) = DiffAlgorithm.AnalyzeLineDiff(old, @new);

        Assert.Empty(unchanged);
        Assert.Single(deletions);
        Assert.Equal(-1, deletions[0].AfterLineIndex);
        Assert.Equal(3, deletions[0].Count);
    }

    [Fact]
    public void AnalyzeLineDiff_AllInserted()
    {
        var old = Array.Empty<string>();
        var @new = new[] { "x", "y" };

        var (unchanged, deletions) = DiffAlgorithm.AnalyzeLineDiff(old, @new);

        Assert.Empty(unchanged);
        Assert.Empty(deletions);
    }

    [Fact]
    public void AnalyzeLineDiff_MiddleDeleted()
    {
        var old = new[] { "a", "b", "c", "d", "e" };
        var @new = new[] { "a", "e" };

        var (unchanged, deletions) = DiffAlgorithm.AnalyzeLineDiff(old, @new);

        Assert.Equal(new HashSet<int> { 0, 1 }, unchanged);
        Assert.Single(deletions);
        Assert.Equal(0, deletions[0].AfterLineIndex); // after new[0] = "a"
        Assert.Equal(3, deletions[0].Count); // b, c, d removed
    }

    [Fact]
    public void AnalyzeLineDiff_TrailingDeletion()
    {
        var old = new[] { "a", "b", "c" };
        var @new = new[] { "a" };

        var (unchanged, deletions) = DiffAlgorithm.AnalyzeLineDiff(old, @new);

        Assert.Equal(new HashSet<int> { 0 }, unchanged);
        Assert.Single(deletions);
        Assert.Equal(0, deletions[0].AfterLineIndex);
        Assert.Equal(2, deletions[0].Count);
    }

    [Fact]
    public void AnalyzeLineDiff_LeadingDeletion()
    {
        var old = new[] { "a", "b", "c" };
        var @new = new[] { "c" };

        var (unchanged, deletions) = DiffAlgorithm.AnalyzeLineDiff(old, @new);

        Assert.Equal(new HashSet<int> { 0 }, unchanged);
        Assert.Single(deletions);
        Assert.Equal(-1, deletions[0].AfterLineIndex);
        Assert.Equal(2, deletions[0].Count);
    }

    // ───────────────── Realistic scenarios ─────────────────

    [Fact]
    public void Realistic_CSharpMethodChange()
    {
        var oldText = """
            public void DoWork()
            {
                Console.WriteLine("old");
                Thread.Sleep(1000);
            }
            """;
        var newText = """
            public void DoWork()
            {
                Console.WriteLine("new");
                await Task.Delay(1000);
            }
            """;

        var fileContent = $"using System;\n\nnamespace App;\n\npublic class Worker\n{{\n{newText}\n}}";

        var edits = new List<(string?, string?)> { (oldText, newText) };
        var result = DiffAlgorithm.ComputeChangedLines(fileContent, edits, isCreate: false);

        Assert.Equal(2, result.ChangedLines.Count);
    }

    [Fact]
    public void Realistic_MultipleEditsToSameFile()
    {
        var content = "import React from 'react';\n\nfunction App() {\n  return <div>Updated</div>;\n}\n\nexport default App;";

        var edits = new List<(string?, string?)>
        {
            ("  return <div>Hello</div>;", "  return <div>Updated</div>;"),
        };

        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Single(result.ChangedLines);
        Assert.Contains(3, result.ChangedLines);
    }

    [Fact]
    public void Realistic_RemoveMethodFromClass()
    {
        var oldText = "    void MethodA() { }\n\n    void MethodB() { }\n\n    void MethodC() { }";
        var newText = "    void MethodA() { }\n\n    void MethodC() { }";
        var content = $"public class Foo\n{{\n{newText}\n}}";

        var edits = new List<(string?, string?)> { (oldText, newText) };
        var result = DiffAlgorithm.ComputeChangedLines(content, edits, isCreate: false);

        Assert.Empty(result.ChangedLines);
        Assert.True(result.Deletions.Count > 0);
        Assert.True(result.Deletions.Sum(d => d.Count) >= 1);
    }
}
