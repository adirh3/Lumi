using System.Security.Cryptography;
using System.Text;
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
        var label = $"{project?.Name ?? "No project"} · {(usesWorktree ? "Chat worktree" : "Project checkout")}";
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
            result.Message = "This chat has no available Git repository. Set its project or worktree on desktop.";
            return result;
        }
        var state = await GitService.GetReadOnlyChangesAsync(
            scope.Repository, RemoteProtocol.GitFileLimit, cancellationToken).ConfigureAwait(false);
        result.Files = state.Files.Select(file => new RemoteGitFile
        {
            Path = file.RelativePath, Status = file.StatusCode,
            Kind = file.StatusCode == "D?" ? "Recreated"
                : file.StatusCode.Contains('U') || file.StatusCode is "AA" or "DD"
                ? "Conflicted" : file.KindLabel
        }).ToList();
        result.IsTruncated = state.Truncated;
        result.Message = state.Truncated
            ? $"Showing up to {RemoteProtocol.GitFileLimit} safe file paths. Some entries were omitted; open desktop for all changes."
            : result.Files.Count == 0 ? "No uncommitted changes in this repository." : null;
        return result;
    }

    internal static async Task<RemoteGitDiff> GetDiffAsync(
        RemoteGitScope scope, string path, CancellationToken cancellationToken)
    {
        if (scope.Repository is null)
            throw new FileNotFoundException("This chat's repository is no longer available.");
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
            Message = diff.Truncated ? "Diff truncated on mobile. Open desktop for the full file."
                : string.IsNullOrWhiteSpace(diff.Text)
                    ? "No text diff available (binary, empty file, or submodule-only change)."
                    : null
        };
    }
}
