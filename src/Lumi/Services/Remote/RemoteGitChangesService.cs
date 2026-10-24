using System.Security.Cryptography;
using System.Text;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.ViewModels;
using StrataTheme.Diff;

namespace Lumi.Services.Remote;

internal sealed record RemoteGitScope(
    Guid ChatId, Guid? ProjectId, string Label, string ScopeId, bool UsesWorktree, string? Repository);

internal static class RemoteGitChangesService
{
    internal static RemoteGitScope CaptureScope(DataStore store, Chat chat)
    {
        var project = store.Data.Projects.FirstOrDefault(item => item.Id == chat.ProjectId);
        var usesWorktree = !string.IsNullOrWhiteSpace(chat.WorktreePath);
        // Unlike ordinary chat startup, inspection cannot silently fall back to the home directory,
        // another checkout, or a selected desktop chat when an explicit scope is missing.
        var hasDirectory = usesWorktree
            ? Directory.Exists(chat.WorktreePath)
            : !string.IsNullOrWhiteSpace(project?.WorkingDirectory) && Directory.Exists(project.WorkingDirectory);
        var directory = hasDirectory
            ? ChatViewModel.ResolveEffectiveWorkingDirectory(store, chat.ProjectId, chat.WorktreePath)
            : null;
        var root = directory is null ? null : GitService.FindRepoRoot(directory);
        var label = Loc.Get(
            usesWorktree ? "Remote_GitChatWorktreeScope" : "Remote_GitProjectCheckoutScope",
            project?.Name ?? Loc.Library_NoProject);
        if (root is not null)
            label += $" · {Path.GetFileName(root)}";
        var scopeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{chat.Id}\n{chat.ProjectId}\n{chat.WorktreePath}\n{directory}\n{root}")));
        return new(chat.Id, chat.ProjectId,
            RemoteProtocol.TruncateForMobile(label, 1024)!, scopeId, usesWorktree, root);
    }

    internal static async Task<RemoteGitChanges> GetChangesAsync(
        RemoteGitScope scope, CancellationToken cancellationToken)
    {
        var result = new RemoteGitChanges
        {
            ChatId = scope.ChatId, ProjectId = scope.ProjectId, ScopeId = scope.ScopeId,
            ScopeLabel = scope.Label, UsesWorktree = scope.UsesWorktree,
            IsRepository = scope.Repository is not null
        };
        if (scope.Repository is null)
        {
            result.Message = Loc.Remote_GitRepositoryUnavailable;
            return result;
        }
        var state = await GitService.GetReadOnlyChangesAsync(
            scope.Repository, RemoteProtocol.GitFileLimit, cancellationToken, includeLineStatistics: true).ConfigureAwait(false);
        result.Files = state.Files.Select(file => new RemoteGitFile
        {
            Path = file.RelativePath, Status = file.StatusCode,
            Kind = file.StatusCode == "D?" ? "Recreated"
                : file.StatusCode.Contains('U') || file.StatusCode is "AA" or "DD"
                ? "Conflicted" : file.KindLabel,
            LinesAdded = file.HasLineStatistics ? file.LinesAdded : null,
            LinesRemoved = file.HasLineStatistics ? file.LinesRemoved : null,
            IsBinary = file.IsBinary
        }).ToList();
        result.IsTruncated = state.Truncated;
        result.Message = state.Truncated
            ? Loc.Get("Remote_GitFilesTruncated", RemoteProtocol.GitFileLimit)
            : result.Files.Count == 0 ? Loc.Remote_GitNoUncommittedChanges : null;
        return result;
    }

    internal static async Task<RemoteGitDiff> GetDiffAsync(
        RemoteGitScope scope, string path, CancellationToken cancellationToken)
    {
        if (scope.Repository is null)
            throw new FileNotFoundException(Loc.Remote_GitRepositoryNoLongerAvailable);
        var diff = await GitService.GetReadOnlyDiffAsync(
            scope.Repository, path, RemoteProtocol.GitFileLimit,
            RemoteProtocol.GitDiffCharacterLimit, cancellationToken).ConfigureAwait(false);
        var lines = 0;
        for (var index = 0; index < diff.Text.Length; index++)
        {
            if (diff.Text[index] == '\n' && ++lines == RemoteProtocol.GitDiffLineLimit
                && index + 1 < diff.Text.Length)
            {
                diff = (diff.Text[..(index + 1)], true);
                break;
            }
        }
        var document = UnifiedDiffBuilder.BuildFromUnifiedDiff(diff.Text);
        return new RemoteGitDiff
        {
            ChatId = scope.ChatId, ScopeId = scope.ScopeId, Path = path,
            UnifiedDiff = diff.Text, IsTruncated = diff.Truncated,
            LinesAdded = document.AddedLineCount, LinesRemoved = document.RemovedLineCount,
            Message = diff.Truncated ? Loc.Remote_GitDiffTruncated
                : string.IsNullOrWhiteSpace(diff.Text)
                    ? Loc.Remote_GitNoTextDiff
                    : null
        };
    }
}
