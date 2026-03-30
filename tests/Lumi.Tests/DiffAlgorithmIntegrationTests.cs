using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Integration tests for DiffAlgorithm that simulate real-world file editing scenarios.
/// Each test creates a temp file, defines edits (old_str → new_str), reads the file back,
/// and verifies the algorithm produces correct changed-line indices and deletion points.
/// </summary>
public class DiffAlgorithmIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public DiffAlgorithmIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DiffAlgoTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    /// <summary>
    /// Simulates the full pipeline: write original → apply edit → read file → run algorithm.
    /// Returns the DiffResult with changed lines and deletions relative to the FINAL file content.
    /// </summary>
    private DiffResult SimulateEditAndDiff(
        string originalContent,
        List<(string OldStr, string NewStr)> edits,
        bool isCreate = false)
    {
        var filePath = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.cs");

        // Normalize all strings to LF (mirrors real-world: LLM sends \n, file may have \r\n)
        var content = originalContent.Replace("\r\n", "\n");

        // Apply each edit to the content (simulating what the tool does)
        var editPairs = new List<(string? OldText, string? NewText)>();
        foreach (var (oldStr, newStr) in edits)
        {
            var normOld = oldStr.Replace("\r\n", "\n");
            var normNew = newStr.Replace("\r\n", "\n");
            content = content.Replace(normOld, normNew);
            editPairs.Add((normOld, normNew));
        }

        // Write the final file to disk (simulating the state after edits)
        File.WriteAllText(filePath, content);

        // Read file content back (just like DiffView does)
        var fileContent = File.ReadAllText(filePath);

        // Run the algorithm
        return DiffAlgorithm.ComputeChangedLines(fileContent, editPairs, isCreate);
    }

    // ═══════════════════════════════════════════════════════════════
    // SCENARIO 1: The original bug — large block replacement, few actual changes
    // The old algorithm highlighted ALL 20 lines; we should highlight only 2
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void LargeBlockReplace_OnlyTwoLinesActuallyChanged_HighlightsOnlyThose()
    {
        var original = """
            using System;

            namespace MyApp;

            public class Calculator
            {
                private int _value;

                public Calculator(int initial)
                {
                    _value = initial;
                }

                public int Add(int x)
                {
                    _value += x;
                    return _value;
                }

                public int Subtract(int x)
                {
                    _value -= x;
                    return _value;
                }
            }
            """;

        // Edit: only change "Add" to "AddValue" and "Subtract" to "SubtractValue"
        // but the old_str/new_str span the entire method bodies
        var oldStr = """
                public int Add(int x)
                {
                    _value += x;
                    return _value;
                }

                public int Subtract(int x)
                {
                    _value -= x;
                    return _value;
                }
            """;
        var newStr = """
                public int AddValue(int x)
                {
                    _value += x;
                    return _value;
                }

                public int SubtractValue(int x)
                {
                    _value -= x;
                    return _value;
                }
            """;

        var result = SimulateEditAndDiff(original, [(oldStr, newStr)]);

        // Only the two renamed method signature lines should be highlighted
        Assert.Equal(2, result.ChangedLines.Count);

        // Verify unchanged lines are NOT highlighted
        var finalContent = original.Replace(oldStr, newStr);
        var lines = finalContent.Replace("\r\n", "\n").Split('\n');
        foreach (var lineIdx in result.ChangedLines)
        {
            var line = lines[lineIdx].Trim();
            Assert.True(
                line.Contains("AddValue") || line.Contains("SubtractValue"),
                $"Line {lineIdx} ('{line}') was highlighted but shouldn't be — only renamed methods should be highlighted");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // SCENARIO 2: Method removed from class — deletion markers
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void MethodRemoved_ShowsDeletionMarker_NoFalseHighlights()
    {
        var original = """
            public class Service
            {
                public void Start() { }

                public void OldMethod()
                {
                    Console.WriteLine("deprecated");
                }

                public void Stop() { }
            }
            """;

        var oldStr = """
                public void Start() { }

                public void OldMethod()
                {
                    Console.WriteLine("deprecated");
                }

                public void Stop() { }
            """;
        var newStr = """
                public void Start() { }

                public void Stop() { }
            """;

        var result = SimulateEditAndDiff(original, [(oldStr, newStr)]);

        // No lines should be highlighted as "changed" — Start() and Stop() are unchanged
        Assert.Empty(result.ChangedLines);

        // But there MUST be deletion markers indicating where OldMethod was removed
        Assert.NotEmpty(result.Deletions);
        var totalDeleted = result.Deletions.Sum(d => d.Count);
        Assert.True(totalDeleted >= 4, $"Expected at least 4 deleted lines (method + blank), got {totalDeleted}");
    }

    // ═══════════════════════════════════════════════════════════════
    // SCENARIO 3: Multiple edits to same file — each change tracked independently
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void MultipleEdits_EachChangeTrackedCorrectly()
    {
        var original = """
            import { useState } from 'react';

            function Counter() {
              const [count, setCount] = useState(0);

              const increment = () => setCount(count + 1);
              const decrement = () => setCount(count - 1);

              return (
                <div>
                  <h1>{count}</h1>
                  <button onClick={increment}>+</button>
                  <button onClick={decrement}>-</button>
                </div>
              );
            }

            export default Counter;
            """;

        // Two separate edits (simulating two tool calls)
        var edits = new List<(string, string)>
        {
            // Edit 1: rename component
            ("function Counter() {", "function ClickCounter() {"),
            // Edit 2: change export
            ("export default Counter;", "export default ClickCounter;"),
        };

        var result = SimulateEditAndDiff(original, edits);

        // Exactly 2 lines should be highlighted
        Assert.Equal(2, result.ChangedLines.Count);

        // Verify the highlighted lines contain the actual changes
        var finalContent = original
            .Replace("function Counter() {", "function ClickCounter() {")
            .Replace("export default Counter;", "export default ClickCounter;");
        var lines = finalContent.Replace("\r\n", "\n").Split('\n');

        foreach (var lineIdx in result.ChangedLines)
        {
            Assert.Contains("ClickCounter", lines[lineIdx]);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // SCENARIO 4: New code inserted between existing code
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void InsertNewMethod_OnlyNewLinesHighlighted()
    {
        var original = """
            public class Logger
            {
                public void Info(string msg) => Console.WriteLine($"[INFO] {msg}");

                public void Error(string msg) => Console.WriteLine($"[ERROR] {msg}");
            }
            """;

        var oldStr = """
                public void Info(string msg) => Console.WriteLine($"[INFO] {msg}");

                public void Error(string msg) => Console.WriteLine($"[ERROR] {msg}");
            """;
        var newStr = """
                public void Info(string msg) => Console.WriteLine($"[INFO] {msg}");

                public void Warn(string msg) => Console.WriteLine($"[WARN] {msg}");

                public void Error(string msg) => Console.WriteLine($"[ERROR] {msg}");
            """;

        var result = SimulateEditAndDiff(original, [(oldStr, newStr)]);

        // Only the new Warn method + its surrounding blank line should be highlighted
        var finalContent = original.Replace(oldStr, newStr);
        var lines = finalContent.Replace("\r\n", "\n").Split('\n');

        // The Warn line must be highlighted
        bool warnHighlighted = result.ChangedLines.Any(i => lines[i].Contains("Warn"));
        Assert.True(warnHighlighted, "The new Warn method line should be highlighted");

        // Info and Error must NOT be highlighted
        bool infoHighlighted = result.ChangedLines.Any(i => lines[i].Contains("Info"));
        bool errorHighlighted = result.ChangedLines.Any(i => lines[i].Contains("Error"));
        Assert.False(infoHighlighted, "Info line should NOT be highlighted — it's unchanged");
        Assert.False(errorHighlighted, "Error line should NOT be highlighted — it's unchanged");

        // No deletions in this scenario
        Assert.Empty(result.Deletions);
    }

    // ═══════════════════════════════════════════════════════════════
    // SCENARIO 5: File creation — all lines highlighted
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void NewFileCreated_AllLinesHighlighted()
    {
        var content = """
            namespace NewFeature;

            public class Widget
            {
                public string Name { get; set; } = "";
                public int Size { get; set; }
            }
            """;

        var filePath = Path.Combine(_tempDir, "Widget.cs");
        File.WriteAllText(filePath, content);
        var fileContent = File.ReadAllText(filePath);

        var edits = new List<(string?, string?)> { (null, content) };
        var result = DiffAlgorithm.ComputeChangedLines(fileContent, edits, isCreate: true);

        var lines = fileContent.Replace("\r\n", "\n").Split('\n');
        Assert.Equal(lines.Length, result.ChangedLines.Count);
        Assert.Empty(result.Deletions);
    }

    // ═══════════════════════════════════════════════════════════════
    // SCENARIO 6: Replace with fewer lines — verify deletion count
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ReplaceWithFewerLines_CorrectDeletionCount()
    {
        var original = """
            public class Config
            {
                public string Host { get; set; } = "localhost";
                public int Port { get; set; } = 8080;
                public string Protocol { get; set; } = "https";
                public int Timeout { get; set; } = 30;
                public int RetryCount { get; set; } = 3;
                public bool Verbose { get; set; } = false;
            }
            """;

        // Consolidate 6 properties into 2
        var oldStr = """
                public string Host { get; set; } = "localhost";
                public int Port { get; set; } = 8080;
                public string Protocol { get; set; } = "https";
                public int Timeout { get; set; } = 30;
                public int RetryCount { get; set; } = 3;
                public bool Verbose { get; set; } = false;
            """;
        var newStr = """
                public string ConnectionString { get; set; } = "https://localhost:8080";
                public RetryPolicy Policy { get; set; } = RetryPolicy.Default;
            """;

        var result = SimulateEditAndDiff(original, [(oldStr, newStr)]);

        // Both new lines should be highlighted (they're completely new)
        Assert.Equal(2, result.ChangedLines.Count);

        // All 6 old lines were replaced by 2 new ones — this is a replacement, not a pure deletion
        Assert.Empty(result.Deletions);
    }

    // ═══════════════════════════════════════════════════════════════
    // SCENARIO 7: Real-world C# edit — using statements changed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void UsingStatementsChanged_OnlyChangedUsingsHighlighted()
    {
        var original = """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using System.Text;
            using System.Threading.Tasks;

            namespace MyApp
            {
                public class Program
                {
                    static void Main(string[] args)
                    {
                        Console.WriteLine("Hello");
                    }
                }
            }
            """;

        // Add one using, remove one using, keep the rest
        var oldStr = """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using System.Text;
            using System.Threading.Tasks;
            """;
        var newStr = """
            using System;
            using System.Collections.Generic;
            using System.IO;
            using System.Linq;
            using System.Threading.Tasks;
            """;

        var result = SimulateEditAndDiff(original, [(oldStr, newStr)]);

        var finalContent = original.Replace(oldStr, newStr);
        var lines = finalContent.Replace("\r\n", "\n").Split('\n');

        // System.IO is new — must be highlighted
        bool ioHighlighted = result.ChangedLines.Any(i => lines[i].Contains("System.IO"));
        Assert.True(ioHighlighted, "using System.IO should be highlighted — it's new");

        // Unchanged usings must NOT be highlighted
        bool systemHighlighted = result.ChangedLines.Any(i =>
            lines[i].Trim() == "using System;");
        Assert.False(systemHighlighted, "using System should NOT be highlighted");

        bool linqHighlighted = result.ChangedLines.Any(i =>
            lines[i].Trim() == "using System.Linq;");
        Assert.False(linqHighlighted, "using System.Linq should NOT be highlighted");

        // System.Text was removed — must have deletion marker
        Assert.NotEmpty(result.Deletions);
        Assert.True(result.Deletions.Sum(d => d.Count) >= 1,
            "At least 1 line should be marked as deleted (System.Text)");

        // The Main method area must NOT be highlighted
        bool mainHighlighted = result.ChangedLines.Any(i =>
            lines[i].Contains("Main") || lines[i].Contains("Hello"));
        Assert.False(mainHighlighted, "Main method should NOT be highlighted — it's outside the edit");
    }

    // ═══════════════════════════════════════════════════════════════
    // SCENARIO 8: Whitespace-only changes within a large block
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void IndentationChange_OnlyReindentedLinesHighlighted()
    {
        var original = """
            public class Formatter
            {
                public void Format()
                {
                    if (true)
                    {
                    var x = 1;
                    var y = 2;
                    var z = x + y;
                    Console.WriteLine(z);
                    }
                }
            }
            """;

        // Fix indentation of 4 lines inside the if block
        var oldStr = """
                    if (true)
                    {
                    var x = 1;
                    var y = 2;
                    var z = x + y;
                    Console.WriteLine(z);
                    }
            """;
        var newStr = """
                    if (true)
                    {
                        var x = 1;
                        var y = 2;
                        var z = x + y;
                        Console.WriteLine(z);
                    }
            """;

        var result = SimulateEditAndDiff(original, [(oldStr, newStr)]);

        // Only the 4 re-indented lines should be highlighted, not if/braces
        Assert.Equal(4, result.ChangedLines.Count);

        var finalContent = original.Replace(oldStr, newStr);
        var lines = finalContent.Replace("\r\n", "\n").Split('\n');

        // Verify the highlighted lines are the variable/console lines
        foreach (var idx in result.ChangedLines)
        {
            var line = lines[idx].Trim();
            Assert.True(
                line.StartsWith("var ") || line.StartsWith("Console."),
                $"Line {idx} ('{line}') should not be highlighted — only the re-indented lines should");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // SCENARIO 9: Windows CRLF file content with LF edits
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void CrlfFile_WithLfEdits_WorksCorrectly()
    {
        // File on disk has CRLF
        var original = "line1\r\nold_line\r\nline3\r\nline4\r\n";

        // Tool sends LF-only edits (common with LLM tools)
        var oldStr = "old_line";
        var newStr = "new_line";

        var filePath = Path.Combine(_tempDir, "crlf_test.txt");
        File.WriteAllText(filePath, original.Replace(oldStr, newStr));
        var fileContent = File.ReadAllText(filePath);

        var edits = new List<(string?, string?)> { (oldStr, newStr) };
        var result = DiffAlgorithm.ComputeChangedLines(fileContent, edits, isCreate: false);

        Assert.Single(result.ChangedLines);
        Assert.Contains(1, result.ChangedLines); // line index 1 = "new_line"
    }

    // ═══════════════════════════════════════════════════════════════
    // SCENARIO 10: Edit at the very beginning and very end of file
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void EditsAtBoundaries_HandleCorrectly()
    {
        var original = """
            // OLD HEADER COMMENT
            using System;

            public class Boundary
            {
                public void Work() { }
            }
            // OLD FOOTER
            """;

        var edits = new List<(string, string)>
        {
            ("// OLD HEADER COMMENT", "// NEW HEADER COMMENT"),
            ("// OLD FOOTER", "// NEW FOOTER"),
        };

        var result = SimulateEditAndDiff(original, edits);

        Assert.Equal(2, result.ChangedLines.Count);

        var finalContent = original
            .Replace("// OLD HEADER COMMENT", "// NEW HEADER COMMENT")
            .Replace("// OLD FOOTER", "// NEW FOOTER");
        var lines = finalContent.Replace("\r\n", "\n").Split('\n');

        // First line should be highlighted
        Assert.Contains(0, result.ChangedLines);

        // Last non-empty line should be highlighted
        var lastLine = result.ChangedLines.Max();
        Assert.Contains("NEW FOOTER", lines[lastLine]);

        // Middle content should NOT be highlighted
        bool middleHighlighted = result.ChangedLines.Any(i =>
            lines[i].Contains("class Boundary") || lines[i].Contains("Work"));
        Assert.False(middleHighlighted, "Middle content should NOT be highlighted");
    }

    // ═══════════════════════════════════════════════════════════════
    // SCENARIO 11: Deletion of entire block with nothing replacing it
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void EntireBlockDeleted_DeletionMarkerAtCorrectPosition()
    {
        var original = """
            public class App
            {
                public void KeepMe() { }

                // This whole block gets removed
                public void RemoveMe()
                {
                    DoStuff();
                    DoMoreStuff();
                }

                public void AlsoKeepMe() { }
            }
            """;

        var oldStr = """
                public void KeepMe() { }

                // This whole block gets removed
                public void RemoveMe()
                {
                    DoStuff();
                    DoMoreStuff();
                }

                public void AlsoKeepMe() { }
            """;
        var newStr = """
                public void KeepMe() { }

                public void AlsoKeepMe() { }
            """;

        var result = SimulateEditAndDiff(original, [(oldStr, newStr)]);

        // No lines should be highlighted — KeepMe and AlsoKeepMe are unchanged
        Assert.Empty(result.ChangedLines);

        // Must have deletion markers
        Assert.NotEmpty(result.Deletions);
        var totalDeleted = result.Deletions.Sum(d => d.Count);
        // 5 lines removed: comment, method sig, two body lines, closing brace
        // Plus possibly blank lines around them
        Assert.True(totalDeleted >= 5, $"Expected at least 5 deleted lines, got {totalDeleted}");
    }

    // ═══════════════════════════════════════════════════════════════
    // SCENARIO 12: Same line appears multiple times (duplicate content)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void DuplicateLines_CorrectlyIdentifiesChanges()
    {
        var original = """
            return;
            return;
            return;
            target_line;
            return;
            return;
            """;

        var oldStr = "target_line";
        var newStr = "CHANGED_LINE";

        var result = SimulateEditAndDiff(original, [(oldStr, newStr)]);

        Assert.Single(result.ChangedLines);

        var finalContent = original.Replace(oldStr, newStr);
        var lines = finalContent.Replace("\r\n", "\n").Split('\n');
        var changedIdx = result.ChangedLines.Single();
        Assert.Contains("CHANGED_LINE", lines[changedIdx]);
    }

    // ═══════════════════════════════════════════════════════════════
    // SCENARIO 13: Mixed additions, modifications, and deletions in one edit
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void MixedAddModifyDelete_AllTrackedCorrectly()
    {
        var original = """
            class Pipeline
            {
                void Step1() { Log("step1"); }
                void Step2() { Log("step2"); }
                void Step3() { Log("step3"); }
                void Step4() { Log("step4"); }
                void Step5() { Log("step5"); }
            }
            """;

        // Modify Step1, delete Step2+Step3, keep Step4, add Step6 after Step5
        var oldStr = """
                void Step1() { Log("step1"); }
                void Step2() { Log("step2"); }
                void Step3() { Log("step3"); }
                void Step4() { Log("step4"); }
                void Step5() { Log("step5"); }
            """;
        var newStr = """
                void Step1() { Log("step1_v2"); }
                void Step4() { Log("step4"); }
                void Step5() { Log("step5"); }
                void Step6() { Log("step6"); }
            """;

        var result = SimulateEditAndDiff(original, [(oldStr, newStr)]);

        var finalContent = original.Replace(oldStr, newStr);
        var lines = finalContent.Replace("\r\n", "\n").Split('\n');

        // Step1 modified → highlighted
        bool step1Highlighted = result.ChangedLines.Any(i => lines[i].Contains("step1_v2"));
        Assert.True(step1Highlighted, "Step1 (modified) should be highlighted");

        // Step6 added → highlighted
        bool step6Highlighted = result.ChangedLines.Any(i => lines[i].Contains("step6"));
        Assert.True(step6Highlighted, "Step6 (new) should be highlighted");

        // Step4 unchanged → NOT highlighted
        bool step4Highlighted = result.ChangedLines.Any(i => lines[i].Contains("step4"));
        Assert.False(step4Highlighted, "Step4 (unchanged) should NOT be highlighted");

        // Step5 unchanged → NOT highlighted
        bool step5Highlighted = result.ChangedLines.Any(i => lines[i].Contains("step5"));
        Assert.False(step5Highlighted, "Step5 (unchanged) should NOT be highlighted");

        // Step2 and Step3 were removed in the same gap where Step1 was modified
        // and Step6 was added — this is a replacement, not a pure deletion.
        // The green highlights on Step1 and Step6 already communicate the change.
        Assert.Empty(result.Deletions);
    }

    // ═══════════════════════════════════════════════════════════════
    // SCENARIO 14: Verify deletion marker positions are correct
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void DeletionMarkerPosition_IsAfterCorrectLine()
    {
        // Use explicit \n to avoid any raw string literal line ending issues
        var original = "A\nB\nC\nD\nE";

        // Remove B and D, keep A, C, E
        var oldStr = "A\nB\nC\nD\nE";
        var newStr = "A\nC\nE";

        var result = SimulateEditAndDiff(original, [(oldStr, newStr)]);

        // No lines changed
        Assert.Empty(result.ChangedLines);

        // Two deletion points
        Assert.Equal(2, result.Deletions.Count);

        var finalContent = newStr; // The entire file is just the newStr
        var lines = finalContent.Split('\n');

        // First deletion: B removed after A
        var del1 = result.Deletions[0];
        Assert.Equal("A", lines[del1.AfterLineIndex].Trim());
        Assert.Equal(1, del1.Count);

        // Second deletion: D removed after C
        var del2 = result.Deletions[1];
        Assert.Equal("C", lines[del2.AfterLineIndex].Trim());
        Assert.Equal(1, del2.Count);
    }

    // ═══════════════════════════════════════════════════════════════
    // SCENARIO 15: The exact scenario from the original bug report
    // A 20-line replacement where only 2 lines actually differ
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void OriginalBug_20LineBlock_Only2Changed_OldAlgorithmWouldHighlightAll20()
    {
        // Build a file with 30 lines total
        var sb = new System.Text.StringBuilder();
        sb.Append("// File header\n");
        sb.Append("using System;\n");
        sb.Append('\n');
        for (int i = 0; i < 20; i++)
            sb.Append($"    line_{i}_content;\n");
        sb.Append('\n');
        sb.Append("// Footer\n");

        var original = sb.ToString();

        // Build old/new where only lines 5 and 15 differ
        var oldLines = new List<string>();
        var newLines = new List<string>();
        for (int i = 0; i < 20; i++)
        {
            oldLines.Add($"    line_{i}_content;");
            if (i == 5)
                newLines.Add($"    line_{i}_MODIFIED;");
            else if (i == 15)
                newLines.Add($"    line_{i}_MODIFIED;");
            else
                newLines.Add($"    line_{i}_content;");
        }

        var oldStr = string.Join("\n", oldLines);
        var newStr = string.Join("\n", newLines);

        var result = SimulateEditAndDiff(original, [(oldStr, newStr)]);

        // THE KEY ASSERTION: only 2 lines highlighted, NOT 20
        Assert.Equal(2, result.ChangedLines.Count);

        var finalContent = original.Replace(oldStr, newStr);
        var lines = finalContent.Replace("\r\n", "\n").Split('\n');

        // Verify the 2 highlighted lines are the right ones
        foreach (var idx in result.ChangedLines)
        {
            Assert.Contains("MODIFIED", lines[idx]);
        }

        // Verify the other 18 lines are NOT highlighted
        int unhighlightedContentLines = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("line_") && lines[i].Contains("content") && !result.ChangedLines.Contains(i))
                unhighlightedContentLines++;
        }
        Assert.Equal(18, unhighlightedContentLines);
    }

    // ═══════════════════════════════════════════════════════════════
    // SCENARIO 16: Method signature renamed (async conversion)
    // Common LLM pattern: same structure, few lines actually different
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void MethodRenamed_NoFalseDeletions()
    {
        var original = "class Svc\n{\n    public void Process()\n    {\n        foreach (var x in items)\n        {\n            x.Run();\n            Log(x);\n        }\n    }\n}";

        var oldStr = "    public void Process()\n    {\n        foreach (var x in items)\n        {\n            x.Run();\n            Log(x);\n        }\n    }";
        var newStr = "    public async Task ProcessAsync()\n    {\n        foreach (var x in items)\n        {\n            await x.RunAsync();\n            await LogAsync(x);\n        }\n    }";

        var result = SimulateEditAndDiff(original, [(oldStr, newStr)]);

        // Only the renamed/changed lines should be highlighted
        var finalContent = original.Replace("\r\n", "\n").Replace(oldStr, newStr);
        var lines = finalContent.Split('\n');

        // Braces, foreach, if — should NOT be highlighted
        bool braceHighlighted = result.ChangedLines.Any(i => lines[i].Trim() == "{" || lines[i].Trim() == "}");
        Assert.False(braceHighlighted, "Unchanged braces should NOT be highlighted");

        bool foreachHighlighted = result.ChangedLines.Any(i => lines[i].Contains("foreach"));
        Assert.False(foreachHighlighted, "Unchanged foreach should NOT be highlighted");

        // NO deletion markers — this is a replacement, not a deletion
        Assert.Empty(result.Deletions);

        // Only 3 lines changed: signature, RunAsync, LogAsync
        Assert.Equal(3, result.ChangedLines.Count);
    }

    // ═══════════════════════════════════════════════════════════════
    // SCENARIO 17: Multiple sequential edits to the same file
    // Simulates the exact pattern from this session's DiffView.axaml.cs edits
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void MultipleSequentialEdits_EachTrackedWithoutFalseDeletions()
    {
        var original = "using System;\nusing System.IO;\n\nclass Differ\n{\n    void OldMethod()\n    {\n        var x = 1;\n        var y = 2;\n    }\n\n    void KeepMe() { }\n\n    void AlsoOld()\n    {\n        return;\n    }\n}";

        var edits = new List<(string, string)>
        {
            // Edit 1: add an import
            ("using System;\nusing System.IO;", "using System;\nusing System.IO;\nusing System.Linq;"),
            // Edit 2: rename a method (replacement, not deletion)
            ("    void OldMethod()\n    {\n        var x = 1;\n        var y = 2;\n    }", "    void NewMethod()\n    {\n        var z = 3;\n    }"),
            // Edit 3: remove a method entirely (pure deletion)
            ("    void KeepMe() { }\n\n    void AlsoOld()\n    {\n        return;\n    }", "    void KeepMe() { }"),
        };

        var result = SimulateEditAndDiff(original, edits);

        var finalContent = original.Replace("\r\n", "\n");
        foreach (var (o, n) in edits)
            finalContent = finalContent.Replace(o.Replace("\r\n", "\n"), n.Replace("\r\n", "\n"));
        var lines = finalContent.Split('\n');

        // Edit 1: "using System.Linq;" should be highlighted
        bool linqHighlighted = result.ChangedLines.Any(i => lines[i].Contains("System.Linq"));
        Assert.True(linqHighlighted, "New using should be highlighted");

        // Edit 2: NewMethod signature and body should be highlighted, NOT braces
        bool newMethodHighlighted = result.ChangedLines.Any(i => lines[i].Contains("NewMethod"));
        Assert.True(newMethodHighlighted, "NewMethod should be highlighted");

        // Edit 2: No deletion markers for the replacement (old method → new method)
        // (the old method lines are replaced by new method lines)

        // Edit 3: AlsoOld was purely removed (no new lines replaced it)
        // This SHOULD have a deletion marker
        Assert.NotEmpty(result.Deletions);

        // KeepMe should NOT be highlighted
        bool keepMeHighlighted = result.ChangedLines.Any(i => lines[i].Contains("KeepMe"));
        Assert.False(keepMeHighlighted, "KeepMe should NOT be highlighted");
    }

    // ═══════════════════════════════════════════════════════════════
    // SCENARIO 18: Verify NO false deletion markers on replacements
    // Directly tests the bug that was reported
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Replacement_NeverProducesDeletionMarkers()
    {
        // Every line in old is replaced by a different line in new
        var original = "alpha\nbeta\ngamma\ndelta";
        var oldStr = "alpha\nbeta\ngamma\ndelta";
        var newStr = "ALPHA\nBETA\nGAMMA\nDELTA";

        var result = SimulateEditAndDiff(original, [(oldStr, newStr)]);

        // All 4 lines changed
        Assert.Equal(4, result.ChangedLines.Count);
        // ZERO deletion markers — every old line was replaced, not deleted
        Assert.Empty(result.Deletions);
    }

    // ═══════════════════════════════════════════════════════════════
    // SCENARIO 19: Pure deletion (nothing replaces removed lines)
    // The ONLY case that should produce deletion markers
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void PureDeletion_ProducesDeletionMarker()
    {
        var original = "keep1\nremove_me\nkeep2";
        var oldStr = "keep1\nremove_me\nkeep2";
        var newStr = "keep1\nkeep2";

        var result = SimulateEditAndDiff(original, [(oldStr, newStr)]);

        // No lines changed (keep1 and keep2 are unchanged)
        Assert.Empty(result.ChangedLines);
        // Exactly 1 deletion marker for "remove_me"
        Assert.Single(result.Deletions);
        Assert.Equal(1, result.Deletions[0].Count);
    }

    // ═══════════════════════════════════════════════════════════════
    // SCENARIO 20: Real Copilot edit — rewrite + pure deletion in same file
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void RealEditPattern_RewritePlusPureDeletion()
    {
        var original = "class App\n{\n    // Config\n    int timeout = 30;\n    int retries = 3;\n\n    void Run()\n    {\n        Start();\n        Process();\n        Stop();\n    }\n\n    void Unused() { }\n}";

        var edits = new List<(string, string)>
        {
            // Edit 1: rewrite config (replacement — no deletion markers expected)
            ("    // Config\n    int timeout = 30;\n    int retries = 3;",
             "    // Config\n    TimeSpan timeout = TimeSpan.FromSeconds(30);"),
            // Edit 2: remove unused method (pure deletion — marker expected)
            ("    void Run()\n    {\n        Start();\n        Process();\n        Stop();\n    }\n\n    void Unused() { }",
             "    void Run()\n    {\n        Start();\n        Process();\n        Stop();\n    }"),
        };

        var result = SimulateEditAndDiff(original, edits);

        var finalContent = original.Replace("\r\n", "\n");
        foreach (var (o, n) in edits)
            finalContent = finalContent.Replace(o.Replace("\r\n", "\n"), n.Replace("\r\n", "\n"));
        var lines = finalContent.Split('\n');

        // Edit 1: new config line highlighted, old lines are replacement → no deletion
        bool timeSpanHighlighted = result.ChangedLines.Any(i => lines[i].Contains("TimeSpan"));
        Assert.True(timeSpanHighlighted, "TimeSpan config should be highlighted");

        // Edit 2: Unused was purely deleted → deletion marker expected
        Assert.True(result.Deletions.Count >= 1, "Pure deletion of Unused() should produce a marker");

        // Run method unchanged
        bool runHighlighted = result.ChangedLines.Any(i => lines[i].Contains("void Run"));
        Assert.False(runHighlighted, "Run() should NOT be highlighted");
    }
}
