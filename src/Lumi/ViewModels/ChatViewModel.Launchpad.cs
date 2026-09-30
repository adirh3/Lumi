using System;
using System.Linq;
using System.Threading.Tasks;

namespace Lumi.ViewModels;

/// <summary>
/// The surface side of the new-chat launchpad: applying a setup to the draft and routing the
/// launchpad's navigation requests to whichever window hosts the surface.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>
    /// The draft as it was before the first setup was applied, so a setup can be undone. A Copilot
    /// agent and a chosen existing worktree are not part of any setup, so they are kept beside it.
    /// </summary>
    private sealed record LaunchpadBaseline(LaunchpadSetupSpec Draft, string? SdkAgentName, string? WorktreePath);

    private LaunchpadViewModel? _launchpad;
    private LaunchpadBaseline? _launchpadBaseline;

    /// <summary>The new-chat launchpad shown while this surface has no chat.</summary>
    public LaunchpadViewModel Launchpad => _launchpad ??= new LaunchpadViewModel(this, _dataStore, _chatEvents);

    /// <summary>Raised when the launchpad opens a chat; the main window also reveals it in the sidebar.</summary>
    public event Action<Guid>? RevealChatRequested;

    /// <summary>Raised when the launchpad asks to show the automations (Jobs) page.</summary>
    public event Action? OpenAutomationsRequested;

    internal void RequestRevealChat(Guid chatId)
    {
        if (RevealChatRequested is { } reveal)
            reveal(chatId);
        else
            OpenChatRequested?.Invoke(chatId);
    }

    internal void RequestOpenAutomations() => OpenAutomationsRequested?.Invoke();

    /// <summary>
    /// True while the draft carries a setup applied from the launchpad. It can be undone, and its
    /// model selection stays with this chat instead of becoming the default for later chats.
    /// </summary>
    internal bool IsLaunchpadSetupApplied => _launchpadBaseline is not null;

    /// <summary>True once the model catalog has arrived, so a model missing from it really is unavailable.</summary>
    internal bool IsModelCatalogKnown => _modelOptionMetadata.Count > 0;

    /// <summary>The draft's configuration in setup terms, or null once the surface holds a chat.</summary>
    internal LaunchpadSetupSpec? GetLaunchpadDraftSetup()
    {
        if (CurrentChat is not null)
            return null;

        var project = GetCurrentProject();
        var model = string.IsNullOrWhiteSpace(SelectedModel) ? null : SelectedModel;
        return new LaunchpadSetupSpec(
            project?.Id,
            ActiveAgent?.Id,
            project is not null && IsWorktreeMode,
            model,
            ResolveReasoningEffortForModel(GetPersistedReasoningEffortPreference(), model));
    }

    /// <summary>A Copilot agent has no setup equivalent, so a draft using one matches no setup.</summary>
    internal bool HasLaunchpadIncompatibleAgent => SelectedSdkAgentName is not null;

    /// <summary>
    /// Applies one of the user's setups to the draft. Clicking the setup that is already applied
    /// restores the draft as it was before the first setup, so trying one on is never a trap. A draft
    /// that simply matches a setup has nothing to restore, so clicking it only returns to the composer.
    /// </summary>
    internal async Task ToggleLaunchpadSetupAsync(LaunchpadSetupSpec setup, bool isActive)
    {
        if (GetLaunchpadDraftSetup() is not { } draft)
            return;

        if (!isActive)
        {
            _launchpadBaseline ??= new LaunchpadBaseline(draft, SelectedSdkAgentName, IsWorktreeMode ? WorktreePath : null);
            await ApplyLaunchpadSetupAsync(setup, sdkAgentName: null);
        }
        else if (_launchpadBaseline is { } baseline)
        {
            _launchpadBaseline = null;
            await ApplyLaunchpadSetupAsync(baseline.Draft, baseline.SdkAgentName);
            if (baseline.WorktreePath is { } worktreePath)
                await SelectExistingWorktree(worktreePath);
        }

        FocusComposerRequested?.Invoke();
    }

    private async Task ApplyLaunchpadSetupAsync(LaunchpadSetupSpec setup, string? sdkAgentName)
    {
        var project = setup.ProjectId is { } projectId
            ? _dataStore.Data.Projects.FirstOrDefault(candidate => candidate.Id == projectId)
            : null;

        // Mirrors picking the project in the composer, so the sidebar lens follows it the same way.
        if (GetCurrentProject()?.Id != project?.Id)
        {
            if (project is null)
                ClearProjectId();
            else
                SetProjectId(project.Id);

            ComposerProjectFilterRequested?.Invoke(project?.Id);
        }

        var agent = setup.AgentId is { } agentId
            ? _dataStore.Data.Agents.FirstOrDefault(candidate => candidate.Id == agentId)
            : null;
        ApplyComposerAgentSelection(agent?.Name ?? sdkAgentName);

        // A setup shapes this one chat; unlike a manual pick it must not become the default for later chats.
        if (setup.ModelId is { Length: > 0 } model)
            ApplyModelSelection(model, setup.Effort);

        await SetWorktreeModePreChatAsync(project is not null && setup.UseWorktree);
    }
}
