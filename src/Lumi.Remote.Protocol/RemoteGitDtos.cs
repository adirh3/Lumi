namespace Lumi.Remote.Protocol;

/// <summary>Read-only inspection of one chat's repository, never the desktop's selected chat.</summary>
public sealed class RemoteGitChanges
{
    public Guid ChatId { get; set; }
    public Guid? ProjectId { get; set; }
    public string ScopeLabel { get; set; } = "";
    public string ScopeId { get; set; } = "";
    public bool UsesWorktree { get; set; }
    public bool IsRepository { get; set; }
    public bool IsTruncated { get; set; }
    public string? Message { get; set; }
    public List<RemoteGitFile> Files { get; set; } = [];
}

public sealed class RemoteGitFile
{
    public string Path { get; set; } = "";
    public string Status { get; set; } = "";
    public string Kind { get; set; } = "";
}

public sealed class RemoteGitDiff
{
    public Guid ChatId { get; set; }
    public string ScopeId { get; set; } = "";
    public string Path { get; set; } = "";
    public string UnifiedDiff { get; set; } = "";
    public string? Message { get; set; }
    public bool IsTruncated { get; set; }
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
}
