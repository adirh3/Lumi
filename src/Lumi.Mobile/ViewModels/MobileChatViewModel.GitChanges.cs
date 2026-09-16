using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Remote.Protocol;

namespace Lumi.Mobile.ViewModels;

public interface IRemoteGitChangesSink
{
    bool SupportsGitChanges { get; }
    Task<RemoteGitChanges?> GetGitChangesAsync(Guid chatId, CancellationToken cancellationToken);
    Task<RemoteGitDiff?> GetGitDiffAsync(Guid chatId, string scopeId, string path, CancellationToken cancellationToken);
}

public sealed partial class MobileChatViewModel
{
    private CancellationTokenSource? _gitRequest;
    private long _gitRequestVersion;
    private string? _gitScopeId;
    private string? _gitListMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenSheet))]
    private bool _isGitChangesOpen;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGitEmptyState))]
    [NotifyPropertyChangedFor(nameof(IsGitListLoading))]
    [NotifyPropertyChangedFor(nameof(IsGitDiffLoading))]
    [NotifyPropertyChangedFor(nameof(ShowGitTextPlaceholder))]
    private bool _isGitLoading;
    [ObservableProperty] private string _gitScopeLabel = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitHeaderSubtitle))]
    private string _gitRepositoryLabel = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitMessage))]
    private string? _gitMessage;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitError))]
    [NotifyPropertyChangedFor(nameof(ShowGitEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowGitTextPlaceholder))]
    private string? _gitError;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitFiles))]
    [NotifyPropertyChangedFor(nameof(GitFileCountText))]
    [NotifyPropertyChangedFor(nameof(GitTotalAdditionsText))]
    [NotifyPropertyChangedFor(nameof(GitTotalDeletionsText))]
    [NotifyPropertyChangedFor(nameof(GitStatsScopeText))]
    [NotifyPropertyChangedFor(nameof(HasGitLineStatistics))]
    [NotifyPropertyChangedFor(nameof(GitFilePositionText))]
    [NotifyPropertyChangedFor(nameof(ShowGitEmptyState))]
    [NotifyCanExecuteChangedFor(nameof(PreviousGitFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextGitFileCommand))]
    private IReadOnlyList<RemoteGitFile> _gitFiles = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitFileCountText))]
    [NotifyPropertyChangedFor(nameof(GitStatsScopeText))]
    private bool _gitFilesTruncated;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGitFileList))]
    [NotifyPropertyChangedFor(nameof(ShowGitDetailPane))]
    private bool _isGitWideLayout;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitHeaderTitle))]
    [NotifyPropertyChangedFor(nameof(GitHeaderSubtitle))]
    [NotifyPropertyChangedFor(nameof(ShowGitExpandedScope))]
    [NotifyPropertyChangedFor(nameof(ShowGitFileHeader))]
    [NotifyPropertyChangedFor(nameof(ShowGitCompactFileNavigation))]
    private bool _isGitShortLayout;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGitFileOpen))]
    [NotifyPropertyChangedFor(nameof(SelectedGitFilePath))]
    [NotifyPropertyChangedFor(nameof(SelectedGitFileName))]
    [NotifyPropertyChangedFor(nameof(SelectedGitFileStatus))]
    [NotifyPropertyChangedFor(nameof(GitHeaderTitle))]
    [NotifyPropertyChangedFor(nameof(GitHeaderSubtitle))]
    [NotifyPropertyChangedFor(nameof(ShowGitFileHeader))]
    [NotifyPropertyChangedFor(nameof(ShowGitCompactFileNavigation))]
    [NotifyPropertyChangedFor(nameof(GitFilePositionText))]
    [NotifyPropertyChangedFor(nameof(GitBackLabel))]
    [NotifyPropertyChangedFor(nameof(ShowGitFileList))]
    [NotifyPropertyChangedFor(nameof(ShowGitDetailPane))]
    [NotifyPropertyChangedFor(nameof(IsGitListLoading))]
    [NotifyPropertyChangedFor(nameof(IsGitDiffLoading))]
    [NotifyPropertyChangedFor(nameof(ShowGitTextPlaceholder))]
    [NotifyCanExecuteChangedFor(nameof(PreviousGitFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextGitFileCommand))]
    private RemoteGitFile? _selectedGitFile;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitDiffText))]
    [NotifyPropertyChangedFor(nameof(ShowGitTextPlaceholder))]
    private string _gitDiffText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitHeaderSubtitle))]
    private string _gitDiffStats = "";

    public bool IsGitFileOpen => SelectedGitFile is not null;
    public string SelectedGitFilePath => SelectedGitFile?.Path ?? "";
    public string SelectedGitFileName => SelectedGitFilePath[(SelectedGitFilePath.LastIndexOf('/') + 1)..];
    public string SelectedGitFileStatus => SelectedGitFile?.Kind ?? "";
    public string GitHeaderTitle => IsGitShortLayout && IsGitFileOpen ? SelectedGitFileName : "Git changes";
    public string GitHeaderSubtitle => IsGitShortLayout
        ? IsGitFileOpen ? $"{GitDiffStats} · Read-only" : $"Read-only · {GitRepositoryLabel}"
        : "READ-ONLY";
    public bool ShowGitExpandedScope => !IsGitShortLayout;
    public bool ShowGitFileHeader => IsGitFileOpen && !IsGitShortLayout;
    public bool ShowGitCompactFileNavigation => IsGitFileOpen && IsGitShortLayout;
    public bool HasGitFiles => GitFiles.Count > 0;
    public bool HasGitLineStatistics => GitFiles.Any(file => file.LinesAdded.HasValue && file.LinesRemoved.HasValue);
    public string GitTotalAdditionsText => $"+{GitFiles.Sum(file => (long)(file.LinesAdded ?? 0)):N0}";
    public string GitTotalDeletionsText => $"−{GitFiles.Sum(file => (long)(file.LinesRemoved ?? 0)):N0}";
    public string GitStatsScopeText => GitFilesTruncated || GitFiles.Any(file => !file.IsBinary && !file.LinesAdded.HasValue)
        ? "Known text changes · partial"
        : "Total text changes";
    public bool HasGitDiffText => !string.IsNullOrWhiteSpace(GitDiffText);
    public bool HasGitMessage => !string.IsNullOrWhiteSpace(GitMessage);
    public bool HasGitError => !string.IsNullOrWhiteSpace(GitError);
    public bool ShowGitEmptyState => !HasGitFiles && !IsGitLoading && !HasGitError;
    public bool ShowGitFileList => IsGitWideLayout || !IsGitFileOpen;
    public bool ShowGitDetailPane => IsGitWideLayout || IsGitFileOpen;
    public bool IsGitListLoading => IsGitLoading && !IsGitFileOpen;
    public bool IsGitDiffLoading => IsGitLoading && IsGitFileOpen;
    public bool ShowGitTextPlaceholder => IsGitFileOpen && !IsGitLoading && !HasGitDiffText && !HasGitError;
    public string GitBackLabel => IsGitFileOpen ? "Back to changed files" : "Back to chat";
    public string GitFileCountText =>
        $"{GitFiles.Count} changed {(GitFiles.Count == 1 ? "file" : "files")}{(GitFilesTruncated ? " · Limited" : "")}";
    public string GitFilePositionText =>
        SelectedGitFileIndex is var index && index >= 0 ? $"{index + 1} of {GitFiles.Count}" : "";

    private int SelectedGitFileIndex
    {
        get
        {
            for (var index = 0; index < GitFiles.Count; index++)
                if (ReferenceEquals(GitFiles[index], SelectedGitFile))
                    return index;
            return -1;
        }
    }

    private bool CanPreviousGitFile() => SelectedGitFileIndex > 0;
    private bool CanNextGitFile() =>
        SelectedGitFileIndex is var index && index >= 0 && index + 1 < GitFiles.Count;

    [RelayCommand(CanExecute = nameof(CanPreviousGitFile), AllowConcurrentExecutions = true)]
    private Task PreviousGitFileAsync() =>
        CanPreviousGitFile() ? OpenGitFileAsync(GitFiles[SelectedGitFileIndex - 1]) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanNextGitFile), AllowConcurrentExecutions = true)]
    private Task NextGitFileAsync() =>
        CanNextGitFile() ? OpenGitFileAsync(GitFiles[SelectedGitFileIndex + 1]) : Task.CompletedTask;

    [RelayCommand]
    private void NavigateGitBack()
    {
        if (IsGitFileOpen)
            BackFromGitFile();
        else
            CloseGitChanges();
    }

    [RelayCommand]
    private Task RetryGitChangesAsync() =>
        SelectedGitFile is { } file ? OpenGitFileAsync(file) : RefreshGitChangesAsync();

    [RelayCommand]
    private async Task OpenGitChangesAsync()
    {
        IsGitChangesOpen = true;
        await RefreshGitChangesAsync();
    }

    [RelayCommand]
    private async Task RefreshGitChangesAsync()
    {
        CancelGitRequest();
        SelectedGitFile = null;
        GitDiffText = "";
        GitDiffStats = "";
        GitFiles = [];
        GitFilesTruncated = false;
        GitError = null;
        GitMessage = null;
        _gitListMessage = null;
        _gitScopeId = null;
        GitScopeLabel = $"Chat: {Title}";
        GitRepositoryLabel = "Resolving project / worktree…";
        if (ChatId == Guid.Empty)
        {
            GitRepositoryLabel = "No repository selected";
            GitMessage = "Open a chat to inspect its project or worktree.";
            return;
        }
        if (_sink is not IRemoteGitChangesSink { SupportsGitChanges: true } sink)
        {
            GitRepositoryLabel = "Project / worktree scope unavailable";
            GitMessage = "Update Lumi on desktop to view Git changes from your phone.";
            return;
        }
        var chatId = ChatId;
        var version = _gitRequestVersion;
        var request = _gitRequest = new CancellationTokenSource();
        IsGitLoading = true;
        try
        {
            var changes = await sink.GetGitChangesAsync(chatId, request.Token);
            if (!IsCurrentGitRequest(chatId, version))
                return;
            if (changes is null || changes.ChatId != chatId)
                throw new InvalidOperationException("Git changes could not be loaded.");
            _gitScopeId = changes.ScopeId;
            GitScopeLabel = $"Chat: {Title}\n{changes.ScopeLabel} · Read-only";
            GitRepositoryLabel = changes.ScopeLabel;
            GitFiles = changes.Files;
            GitFilesTruncated = changes.IsTruncated;
            GitMessage = _gitListMessage = changes.Message;
            if (GitFiles.Count == 0 && string.IsNullOrWhiteSpace(GitMessage))
                GitMessage = _gitListMessage = changes.IsRepository
                    ? "Your working tree is clean. Refresh after making changes on desktop."
                    : "This chat has no available Git repository.";
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentGitRequest(chatId, version))
            {
                GitRepositoryLabel = "Project / worktree scope unavailable";
                GitError = "Git request cancelled. Refresh to try again.";
            }
        }
        catch (Exception ex)
        {
            if (IsCurrentGitRequest(chatId, version))
            {
                GitRepositoryLabel = "Project / worktree scope unavailable";
                GitError = ex.Message;
            }
        }
        finally
        {
            if (IsCurrentGitRequest(chatId, version))
                IsGitLoading = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenGitFileAsync(RemoteGitFile file)
    {
        if (_sink is not IRemoteGitChangesSink sink || _gitScopeId is null
            || !GitFiles.Contains(file))
            return;
        CancelGitRequest();
        SelectedGitFile = file;
        GitDiffText = "";
        GitDiffStats = FileLineStatistics(file);
        GitMessage = null;
        GitError = null;
        var chatId = ChatId;
        var scopeId = _gitScopeId;
        var version = _gitRequestVersion;
        var request = _gitRequest = new CancellationTokenSource();
        IsGitLoading = true;
        try
        {
            var diff = await sink.GetGitDiffAsync(chatId, scopeId, file.Path, request.Token);
            if (!IsCurrentGitRequest(chatId, version))
                return;
            if (diff is null || diff.ChatId != chatId || diff.ScopeId != scopeId || diff.Path != file.Path)
                throw new InvalidOperationException("The workspace changed. Refresh Git changes.");
            GitDiffText = diff.UnifiedDiff;
            GitMessage = diff.Message ?? (diff.IsTruncated
                ? "This diff is truncated on mobile. Open desktop for the full file."
                : string.IsNullOrWhiteSpace(diff.UnifiedDiff)
                    ? "No text diff is available for this file (binary, empty, or metadata-only change)."
                    : null);
            GitDiffStats = file.IsBinary ? "Binary file"
                : diff.IsTruncated && file.LinesAdded.HasValue ? FileLineStatistics(file)
                : $"+{diff.LinesAdded:N0}  −{diff.LinesRemoved:N0}{(diff.IsTruncated ? " · Preview only" : "")}";
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentGitRequest(chatId, version))
                GitError = "Git request cancelled. Refresh to try again.";
        }
        catch (Exception ex)
        {
            if (IsCurrentGitRequest(chatId, version))
                GitError = ex.Message;
        }
        finally
        {
            if (IsCurrentGitRequest(chatId, version))
                IsGitLoading = false;
        }
    }

    [RelayCommand]
    private void BackFromGitFile()
    {
        CancelGitRequest();
        SelectedGitFile = null;
        GitDiffText = "";
        GitDiffStats = "";
        GitError = null;
        GitMessage = _gitListMessage;
    }

    private static string FileLineStatistics(RemoteGitFile file) => file.IsBinary
        ? "Binary file"
        : file.LinesAdded.HasValue && file.LinesRemoved.HasValue
            ? $"+{file.LinesAdded:N0}  −{file.LinesRemoved:N0}"
            : "";

    [RelayCommand]
    private void CloseGitChanges() => IsGitChangesOpen = false;

    partial void OnIsGitChangesOpenChanged(bool value)
    {
        if (!value)
            ResetGitChanges();
    }

    private bool IsCurrentGitRequest(Guid chatId, long version) =>
        IsGitChangesOpen && ChatId == chatId && version == _gitRequestVersion;

    private void CancelGitRequest()
    {
        _gitRequestVersion++;
        _gitRequest?.Cancel();
        _gitRequest?.Dispose();
        _gitRequest = null;
        IsGitLoading = false;
    }

    private void ResetGitChanges()
    {
        CancelGitRequest();
        IsGitChangesOpen = false;
        SelectedGitFile = null;
        GitFiles = [];
        GitFilesTruncated = false;
        GitDiffText = "";
        GitDiffStats = "";
        GitMessage = null;
        GitError = null;
        GitScopeLabel = "";
        GitRepositoryLabel = "";
        _gitScopeId = null;
        _gitListMessage = null;
    }
}
