using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Lumi.Localization;

namespace Lumi.Services.Permissions;

/// <summary>
/// A request to go past the sandbox, as the runtime offered it: reach one more path
/// (<see cref="Grant"/>), retry with its file and process limits only recording
/// (<see cref="IsRetry"/>), or run outside it. Only the runtime raises these, and only a person
/// answers them.
/// </summary>
/// <param name="Reason">The runtime's own explanation: which rule blocked the call, or why it can't be sandboxed.</param>
public sealed record SandboxEscape(string? Reason, bool IsRetry, SandboxPathGrant? Grant)
{
    /// <summary>
    /// Reads the escape from the full request when there is one: in this SDK only the shell and URL
    /// prompts carry it, while file reads and writes carry it on the request alone.
    /// </summary>
    public static SandboxEscape? Of(PermissionRequest? request, PermissionPromptRequest? prompt) => request switch
    {
        PermissionRequestShell { RequestSandboxBypass: true } shell
            => new(shell.RequestSandboxBypassReason, shell.RequestSandboxPermissive == true, SandboxPathGrant.Of(shell.SandboxPathGrant)),
        PermissionRequestWrite { RequestSandboxBypass: true } write
            => new(write.RequestSandboxBypassReason, false, SandboxPathGrant.Of(write.SandboxPathGrant)),
        PermissionRequestRead { RequestSandboxBypass: true } read
            => new(read.RequestSandboxBypassReason, false, SandboxPathGrant.Of(read.SandboxPathGrant)),
        PermissionRequestUrl { RequestSandboxBypass: true } url
            => new(url.RequestSandboxBypassReason, false, null),
        _ => prompt switch
        {
            PermissionPromptRequestCommands { RequestSandboxBypass: true } commands
                => new(commands.RequestSandboxBypassReason, commands.RequestSandboxPermissive == true, SandboxPathGrant.Of(commands.SandboxPathGrant)),
            PermissionPromptRequestUrl { RequestSandboxBypass: true } url
                => new(url.RequestSandboxBypassReason, false, null),
            _ => null
        }
    };

    /// <summary>
    /// The escape behind a request a resumed session is still waiting on. Its prompt alone loses a
    /// file read's or write's escape, so that is read from the full request in the session's history;
    /// without it the request couldn't be shown safely, so the session isn't restored.
    /// </summary>
    public static SandboxEscape? OfPending(PendingPermissionRequest pending, IEnumerable<SessionEvent>? history)
    {
        if (!NeedsHistory(pending))
            return Of(null, pending.Request);
        var requested = history?.OfType<PermissionRequestedEvent>().LastOrDefault(e => e.Data.RequestId == pending.RequestId)
            ?? throw new InvalidOperationException(Loc.Get("Status_OriginalSessionUnavailable"));
        return Of(requested.Data.PermissionRequest, pending.Request);
    }

    /// <summary>Whether a pending request's escape can only be read from the session's history.</summary>
    public static bool NeedsHistory(PendingPermissionRequest pending)
        => pending.Request is PermissionPromptRequestRead or PermissionPromptRequestWrite;
}

/// <summary>The policy edit that lets a request run inside the sandbox: one more path it may reach.</summary>
/// <param name="RemovedReadOnlyPaths">Read-only entries the grant replaces, which a saved copy must drop too.</param>
public sealed record SandboxPathGrant(string Path, bool CanWrite, IReadOnlyList<string> RemovedReadOnlyPaths)
{
    public static SandboxPathGrant? Of(PermissionSandboxPathGrant? grant) => grant is { Path: { Length: > 0 } path }
        ? new(path, grant.Access == PermissionSandboxPathGrantAccess.ReadWrite, grant.RemovedReadonlyPaths ?? [])
        : null;
}
