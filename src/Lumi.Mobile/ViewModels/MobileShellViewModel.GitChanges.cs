using Lumi.Remote.Protocol;

namespace Lumi.Mobile.ViewModels;

public sealed partial class MobileShellViewModel : IRemoteGitChangesSink
{
    public bool SupportsGitChanges => Client.SupportsGitChanges;

    public async Task<RemoteGitChanges?> GetGitChangesAsync(Guid chatId, CancellationToken cancellationToken)
    {
        using var request = CreateConnectionRequest();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(request.Token, cancellationToken);
        return await Client.GetGitChangesAsync(chatId, linked.Token)
            ?? throw new InvalidOperationException(Client.StateMessage ?? "Git changes could not be loaded.");
    }

    public async Task<RemoteGitDiff?> GetGitDiffAsync(
        Guid chatId, string scopeId, string path, CancellationToken cancellationToken)
    {
        using var request = CreateConnectionRequest();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(request.Token, cancellationToken);
        return await Client.GetGitDiffAsync(chatId, scopeId, path, linked.Token)
            ?? throw new InvalidOperationException(Client.StateMessage ?? "The file diff could not be loaded.");
    }
}
