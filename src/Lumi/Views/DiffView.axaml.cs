using Lumi.ViewModels;
using StrataTheme.Controls;
using StrataTheme.Diff;

namespace Lumi.Views;

/// <summary>Desktop adapter for Lumi's captured file edits; rendering is shared with mobile.</summary>
public sealed class DiffView : StrataDiffView
{
    public void SetFileChangeDiff(FileChangeItem fileChange) =>
        SetDocument(fileChange.FilePath, () => fileChange.HasSnapshots
            ? UnifiedDiffBuilder.BuildFromSnapshots(fileChange.OriginalContent, fileChange.CurrentContent)
            : UnifiedDiffBuilder.BuildFromEdits(fileChange.Edits, fileChange.IsCreate));
}
