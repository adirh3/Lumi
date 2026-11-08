using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using GitHub.Copilot.Rpc;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Capabilities;
using Lumi.Services.Permissions;
using Chat = Lumi.Models.Chat;

namespace Lumi.ViewModels;

/// <summary>
/// The project sandbox in a chat. A project that turns it on has Copilot run its chats' commands in
/// the OS sandbox. This partial sends the policy with every create and resume (the runtime keeps none
/// of it across a resume), applies a change to live sessions, saves what approval cards let past it,
/// and lets one chat turn it off.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>What a live session's sandbox was built from, so a change reaches it as the next session would get it.</summary>
    private sealed record SessionSandbox(string WorkingDirectory, IReadOnlyList<string> ReadOnlyRoots, string Applied);

    private readonly Dictionary<Guid, SessionSandbox> _sessionSandboxes = [];
    private bool _draftSandboxDisabled;

    // ── In the approval picker ──────────────────────────

    private bool IsSandboxTurnedOff(Chat? chat) => chat?.SandboxDisabled ?? _draftSandboxDisabled;

    /// <summary>The chat's project runs its commands in the sandbox, by its own choice or your default, so the approval picker offers the switch.</summary>
    public bool HasProjectSandbox => SandboxPolicy.IsOnFor(_dataStore.Data.Settings, ApprovalProject(CurrentChat));

    /// <summary>This chat's commands run in its project's sandbox. Turning it off applies to this chat alone.</summary>
    public bool IsSandboxOn
    {
        get => SandboxPolicy.IsActive(_dataStore.Data.Settings, ApprovalProject(CurrentChat), IsSandboxTurnedOff(CurrentChat));
        set => SetSandboxOn(value);
    }

    public string SandboxSummary => Loc.Get(IsSandboxOn ? "Sandbox_ChatOn" : "Sandbox_ChatOff");

    public int ChatSandboxFolderCount => CurrentChat?.SandboxFolders.Count ?? 0;
    public bool HasChatSandboxFolders => IsSandboxOn && ChatSandboxFolderCount > 0;
    public string ForgetChatSandboxFoldersLabel => ChatSandboxFolderCount == 1
        ? Loc.Get("Sandbox_ForgetOneFolder")
        : Loc.Get("Sandbox_ForgetFolders", ChatSandboxFolderCount);

    /// <summary>Raised to open the project's sandbox settings, with the project's id.</summary>
    public event Action<Guid>? ManageSandboxRequested;

    [RelayCommand]
    private void ManageSandbox()
    {
        if (ApprovalProject(CurrentChat) is { } project)
            ManageSandboxRequested?.Invoke(project.Id);
    }

    /// <summary>Drops the folders approval cards allowed in this chat; the project's own folders stay.</summary>
    [RelayCommand]
    private void ForgetChatSandboxFolders()
    {
        if (CurrentChat is not { SandboxFolders.Count: > 0 } chat)
            return;
        chat.SandboxFolders.Clear();
        QueueSaveChatIndex(chat);
        _ = ApplySandboxPolicyAsync(chat);
    }

    private void SetSandboxOn(bool on)
    {
        if (!HasProjectSandbox || on == IsSandboxOn)
            return;
        if (CurrentChat is { } chat)
        {
            chat.SandboxDisabled = !on;
            QueueSaveChatIndex(chat);
            _ = ApplySandboxPolicyAsync(chat);
        }
        else
            _draftSandboxDisabled = !on;
        NotifySandboxPropertiesChanged();
    }

    internal void NotifySandboxPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasProjectSandbox));
        OnPropertyChanged(nameof(IsSandboxOn));
        OnPropertyChanged(nameof(SandboxSummary));
        OnPropertyChanged(nameof(ChatSandboxFolderCount));
        OnPropertyChanged(nameof(HasChatSandboxFolders));
        OnPropertyChanged(nameof(ForgetChatSandboxFoldersLabel));
        OnPropertyChanged(nameof(ApprovalModeToolTip));
    }

    // ── Sessions ────────────────────────────────────────

    /// <summary>Folders the session's skills, plugins and context live in: their scripts read them, so the sandbox may too.</summary>
    private static List<string> SandboxReadOnlyRoots(CapabilitySnapshot capabilities, Project? project)
        => [.. capabilities.SessionSkillRoots, .. capabilities.SessionPluginRoots,
            .. ProjectContextDirectoryHelper.NormalizeFolderList(project?.AdditionalContextDirectories)];

    /// <summary>The sandbox for a session about to be created or resumed, recorded so a later change can reach it live.</summary>
    private async Task<SandboxConfig?> PrepareSessionSandboxAsync(
        Chat chat, Project? project, string workingDirectory, IReadOnlyList<string> readOnlyRoots, CancellationToken ct)
    {
        var settings = _dataStore.Data.Settings;
        var host = SandboxPolicy.IsActive(settings, project, chat.SandboxDisabled)
            ? await _copilotService.GetSandboxHostSupportAsync(ct)
            : null;
        var config = SandboxPolicy.Build(settings, project, chat, workingDirectory, readOnlyRoots, host);
        _sessionSandboxes[chat.Id] = new SessionSandbox(workingDirectory, readOnlyRoots, SandboxPolicy.Describe(config));
        return config;
    }

    /// <summary>Applies a changed project or chat sandbox to the chat's live session, if it has one.</summary>
    /// <returns>False when the live session couldn't take the change and was released: the next turn starts one that has it.</returns>
    internal async Task<bool> ApplySandboxPolicyAsync(Chat chat)
    {
        NotifySandboxPropertiesChanged();
        if (!_sessionCache.TryGetValue(chat.Id, out var session) || !_sessionSandboxes.TryGetValue(chat.Id, out var built))
            return true;

        var project = ApprovalProject(chat);
        var config = BuildLiveSandbox(chat, built);
        // A session that started with Lumi's connector proxy runs its local connectors in Lumi's own
        // process, where no sandbox reaches them: it starts again, leaving them to the runtime's sandbox.
        if (config is not null && _sessionMcpPlans.TryGetValue(chat.Id, out var plan) && plan.UsesProxy)
        {
            InvalidateSessionConfiguration(chat);
            if (!_sessionCache.TryGetValue(chat.Id, out session))
                return true;
            // It is still working and starts again once its turn ends; until then its connectors are cut off.
            ReleaseMcpProxyLease(chat.Id, session);
        }

        var description = SandboxPolicy.Describe(config);
        if (description == built.Applied)
            return true;
        _sessionSandboxes[chat.Id] = built with { Applied = description };
        try
        {
            var result = await session.Rpc.Options.UpdateAsync(
                sandboxConfig: config ?? new SandboxConfig { Enabled = false },
                sandboxConfigSource: SandboxPolicy.SourceOf(_dataStore.Data.Settings, project, chat.SandboxDisabled));
            if (!result.Success)
                throw new InvalidOperationException(Loc.Get("Sandbox_NotApplied"));
            return true;
        }
        catch (Exception error) when (!_isDisposed)
        {
            Trace.TraceWarning("[Sandbox] Live update failed for chat {0}: {1}", chat.Id, error.Message);
            // The next turn starts the session again with the policy instead.
            if (_sessionCache.TryGetValue(chat.Id, out var current) && ReferenceEquals(current, session))
                ReleaseSessionResources(chat.Id, cancelActiveRequest: false);
            return false;
        }
    }

    /// <summary>
    /// Brings a just-published session up to date with sandbox changes made while it started, which had
    /// no live session to reach. False when it couldn't take them and was released.
    /// </summary>
    private async Task<bool> CatchUpSessionSandboxAsync(Chat chat)
        => await ApplySandboxPolicyAsync(chat) && _sessionCache.ContainsKey(chat.Id);

    /// <summary>
    /// Saves what a person allowed past the sandbox. A folder is kept for the chat or its project, so
    /// a later session reaches it too; turning the sandbox off lasts until they turn it on again.
    /// </summary>
    /// <returns>True when the project changed, so its other chats and its page take it on.</returns>
    private bool RememberSandboxAnswer(Chat chat, SandboxAnswer answer, SandboxPathGrant? grant, bool forProject)
    {
        var project = forProject ? ApprovalProject(chat) : null;
        if (answer == SandboxAnswer.TurnOff)
        {
            chat.SandboxDisabled = true;
            QueueSaveChatIndex(chat);
        }
        else if (grant is not null && project is not null)
        {
            SandboxPolicy.Remember(project.Sandbox.Folders, grant);
            _ = _dataStore.SaveAsync();
        }
        else if (grant is not null)
        {
            SandboxPolicy.Remember(chat.SandboxFolders, grant);
            QueueSaveChatIndex(chat);
        }

        // The runtime already applied the answer to the live session; record it rather than send it again.
        if (_sessionSandboxes.TryGetValue(chat.Id, out var built))
            _sessionSandboxes[chat.Id] = built with { Applied = SandboxPolicy.Describe(BuildLiveSandbox(chat, built)) };
        NotifySandboxPropertiesChanged();
        return answer == SandboxAnswer.GrantPath && project is not null;
    }

    private SandboxConfig? BuildLiveSandbox(Chat chat, SessionSandbox built)
        => SandboxPolicy.Build(_dataStore.Data.Settings, ApprovalProject(chat), chat, built.WorkingDirectory, built.ReadOnlyRoots,
            _copilotService.KnownSandboxHostSupport);
}
