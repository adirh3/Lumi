using StrataTheme.Diff;
using Xunit;

namespace Lumi.Tests;

public class UnifiedDiffBuilderTests
{
    [Fact]
    public void BuildFromSnapshots_RendersAddedRemovedAndContextLines()
    {
        var original = """
            public class Sample
            {
                void OldName() { }
            }
            """;

        var updated = """
            public class Sample
            {
                void NewName() { }
                void AddedMethod() { }
            }
            """;

        var document = UnifiedDiffBuilder.BuildFromSnapshots(original, updated, contextLineCount: 1);

        Assert.Single(document.Hunks);
        Assert.Equal(2, document.AddedLineCount);
        Assert.Equal(1, document.RemovedLineCount);
        Assert.True(document.HasAccurateLineNumbers);

        var lines = document.Hunks[0].Lines;
        Assert.Contains(lines, line => line.Kind == DiffLineKind.Context && line.Text == "{");
        Assert.Contains(lines, line => line.Kind == DiffLineKind.Removed && line.Text.Contains("OldName"));
        Assert.Contains(lines, line => line.Kind == DiffLineKind.Added && line.Text.Contains("NewName"));
        Assert.Contains(lines, line => line.Kind == DiffLineKind.Added && line.Text.Contains("AddedMethod"));
    }

    [Fact]
    public void BuildFromEdits_PureDeletionShowsRemovedLinesWithoutSnapshots()
    {
        var document = UnifiedDiffBuilder.BuildFromEdits(
            [("line1\nline2\nline3", "line1\nline3")],
            isCreate: false);

        Assert.Single(document.Hunks);
        Assert.False(document.HasAccurateLineNumbers);
        Assert.Equal(0, document.AddedLineCount);
        Assert.Equal(1, document.RemovedLineCount);

        var removed = Assert.Single(document.Hunks[0].Lines, line => line.Kind == DiffLineKind.Removed);
        Assert.Equal("line2", removed.Text);
    }

    [Fact]
    public void BuildFromUnifiedDiff_ParsesGitLineNumbersAndChangeKinds()
    {
        var diff = """
            diff --git a/Sample.cs b/Sample.cs
            index 1234567..89abcde 100644
            --- a/Sample.cs
            +++ b/Sample.cs
            @@ -2,3 +2,4 @@
             public class Sample
             {
            -    void OldName() { }
            +    void NewName() { }
            +    void AddedMethod() { }
             }
            """;

        var document = UnifiedDiffBuilder.BuildFromUnifiedDiff(diff);

        Assert.Single(document.Hunks);
        Assert.True(document.HasAccurateLineNumbers);
        Assert.Equal(2, document.AddedLineCount);
        Assert.Equal(1, document.RemovedLineCount);

        var lines = document.Hunks[0].Lines;
        var removed = Assert.Single(lines, line => line.Kind == DiffLineKind.Removed);
        Assert.Equal(4, removed.OldLineNumber);
        Assert.Null(removed.NewLineNumber);

        var addedLines = lines.Where(line => line.Kind == DiffLineKind.Added).ToList();
        Assert.Equal(2, addedLines.Count);
        Assert.Equal(4, addedLines[0].NewLineNumber);
        Assert.Equal(5, addedLines[1].NewLineNumber);
    }
}
