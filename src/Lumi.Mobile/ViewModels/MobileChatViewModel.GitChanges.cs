using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Remote.Protocol;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

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
    public string SelectedGitFileStatus => GitKindDisplay(SelectedGitFile?.Kind);
    public string GitHeaderTitle => IsGitShortLayout && IsGitFileOpen ? SelectedGitFileName : Strings.Get("GitChanges");
    public string GitHeaderSubtitle => IsGitShortLayout
        ? IsGitFileOpen ? Strings.Get("ReadOnlyAfter", GitDiffStats) : Strings.Get("ReadOnlyBefore", GitRepositoryLabel)
        : Strings.Get("ReadOnlyHeader");
    public bool ShowGitExpandedScope => !IsGitShortLayout;
    public bool ShowGitFileHeader => IsGitFileOpen && !IsGitShortLayout;
    public bool ShowGitCompactFileNavigation => IsGitFileOpen && IsGitShortLayout;
    public bool HasGitFiles => GitFiles.Count > 0;
    public bool HasGitLineStatistics => GitFiles.Any(file => file.LinesAdded.HasValue && file.LinesRemoved.HasValue);
    public string GitTotalAdditionsText => $"+{GitFiles.Sum(file => (long)(file.LinesAdded ?? 0)):N0}";
    public string GitTotalDeletionsText => $"−{GitFiles.Sum(file => (long)(file.LinesRemoved ?? 0)):N0}";
    public string GitStatsScopeText => GitFilesTruncated || GitFiles.Any(file => !file.IsBinary && !file.LinesAdded.HasValue)
        ? Strings.Get("GitKnownTextChanges")
        : Strings.Get("GitTotalTextChanges");
    public bool HasGitDiffText => !string.IsNullOrWhiteSpace(GitDiffText);
    public bool HasGitMessage => !string.IsNullOrWhiteSpace(GitMessage);
    public bool HasGitError => !string.IsNullOrWhiteSpace(GitError);
    public bool ShowGitEmptyState => !HasGitFiles && !IsGitLoading && !HasGitError;
    public bool ShowGitFileList => IsGitWideLayout || !IsGitFileOpen;
    public bool ShowGitDetailPane => IsGitWideLayout || IsGitFileOpen;
    public bool IsGitListLoading => IsGitLoading && !IsGitFileOpen;
    public bool IsGitDiffLoading => IsGitLoading && IsGitFileOpen;
    public bool ShowGitTextPlaceholder => IsGitFileOpen && !IsGitLoading && !HasGitDiffText && !HasGitError;
    public string GitBackLabel => Strings.Get(IsGitFileOpen ? "BackToChangedFiles" : "BackToChat");
    public string GitFileCountText =>
        Strings.Get(GitFiles.Count == 1 ? "GitChangedFileOne" : "GitChangedFileMany", GitFiles.Count)
        + (GitFilesTruncated ? Strings.Get("LimitedSuffix") : "");
    public string GitFilePositionText =>
        SelectedGitFileIndex is var index && index >= 0 ? Strings.Get("PositionOfCount", index + 1, GitFiles.Count) : "";

    internal static string GitKindDisplay(string? kind) => kind switch
    {
        "Modified" => Strings.Get("GitKindModified"),
        "Added" => Strings.Get("GitKindAdded"),
        "Deleted" => Strings.Get("GitKindDeleted"),
        "Renamed" => Strings.Get("GitKindRenamed"),
        "Copied" => Strings.Get("GitKindCopied"),
        "Untracked" => Strings.Get("GitKindUntracked"),
        "Type changed" => Strings.Get("GitKindTypeChanged"),
        "Conflicted" => Strings.Get("GitKindConflicted"),
        "Recreated" => Strings.Get("GitKindRecreated"),
        "Submodule" => Strings.Get("GitKindSubmodule"),
        "Unknown" => Strings.Get("GitKindUnknown"),
        _ => kind ?? ""
    };

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
        GitScopeLabel = Strings.Get("ChatNamed", Title);
        GitRepositoryLabel = Strings.Get("GitResolvingScope");
        if (ChatId == Guid.Empty)
        {
            GitRepositoryLabel = Strings.Get("GitNoRepositorySelected");
            GitMessage = Strings.Get("GitOpenChat");
            return;
        }
        if (_sink is not IRemoteGitChangesSink { SupportsGitChanges: true } sink)
        {
            GitRepositoryLabel = Strings.Get("GitScopeUnavailable");
            GitMessage = Strings.Get("GitUpdateRequired");
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
                throw new InvalidOperationException(Strings.Get("GitLoadFailed"));
            _gitScopeId = changes.ScopeId;
            GitScopeLabel = Strings.Get("GitScopeDescription", Title, changes.ScopeLabel);
            GitRepositoryLabel = changes.ScopeLabel;
            GitFiles = changes.Files;
            GitFilesTruncated = changes.IsTruncated;
            GitMessage = _gitListMessage = changes.Message;
            if (GitFiles.Count == 0 && string.IsNullOrWhiteSpace(GitMessage))
                GitMessage = _gitListMessage = changes.IsRepository
                    ? Strings.Get("GitWorkingTreeClean")
                    : Strings.Get("GitNoRepository");
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentGitRequest(chatId, version))
            {
                GitRepositoryLabel = Strings.Get("GitScopeUnavailable");
                GitError = Strings.Get("GitCancelled");
            }
        }
        catch (Exception ex)
        {
            if (IsCurrentGitRequest(chatId, version))
            {
                GitRepositoryLabel = Strings.Get("GitScopeUnavailable");
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
                throw new InvalidOperationException(Strings.Get("GitWorkspaceChanged"));
            GitDiffText = diff.UnifiedDiff;
            GitMessage = diff.Message ?? (diff.IsTruncated
                ? Strings.Get("GitDiffTruncated")
                : string.IsNullOrWhiteSpace(diff.UnifiedDiff)
                    ? Strings.Get("GitNoTextDiff")
                    : null);
            GitDiffStats = file.IsBinary ? Strings.Get("BinaryFile")
                : diff.IsTruncated && file.LinesAdded.HasValue ? FileLineStatistics(file)
                : $"+{diff.LinesAdded:N0}  −{diff.LinesRemoved:N0}{(diff.IsTruncated ? Strings.Get("PreviewOnlySuffix") : "")}";
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentGitRequest(chatId, version))
                GitError = Strings.Get("GitCancelled");
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
        ? Strings.Get("BinaryFile")
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
