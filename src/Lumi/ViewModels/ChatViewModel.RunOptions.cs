using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Microsoft.Copilot;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    private ChatRunOptions _newChatRunOptions;
    private ChatRunOptions _editingRunOptions;

    internal ChatRunOptions DraftRunOptions => IsEditingMessage
        ? _editingRunOptions
        : CurrentChat?.DraftRunOptions ?? _newChatRunOptions;

    public IReadOnlyList<StrataComposerDraftOption> ComposerRunOptions
    {
        get
        {
            var draft = DraftRunOptions;
            var researchAvailable = ResearchUnavailableReason(SelectedModel) is null;
            return
            [
                new(nameof(ChatRunOptions.Autopilot), Loc.Get("Composer_Autopilot"), Loc.Get("Composer_AutopilotDetail"),
                    static () => StrataIcons.Bolt, draft.HasFlag(ChatRunOptions.Autopilot)),
                new(nameof(ChatRunOptions.Parallel), Loc.Get("Composer_Parallel"), Loc.Get("Composer_ParallelDetail"),
                    static () => StrataIcons.Branch, draft.HasFlag(ChatRunOptions.Parallel)),
                new(nameof(ChatRunOptions.Research), Loc.Get("Composer_Research"),
                    Loc.Get(researchAvailable ? "Composer_ResearchDetail" : "Composer_ResearchRequiresCopilot"),
                    static () => StrataIcons.Search, draft.HasFlag(ChatRunOptions.Research),
                    IsEnabled: researchAvailable || draft == ChatRunOptions.Research),
            ];
        }
    }

    internal void RefreshComposerRunOptions() => OnPropertyChanged(nameof(ComposerRunOptions));

    internal void SetPersistedDraftRunOptions(Chat chat, ChatRunOptions options)
    {
        if (chat.DraftRunOptions == options)
            return;

        chat.DraftRunOptions = options;
        QueueSaveChat(chat, saveIndex: true);
        if (CurrentChat?.Id == chat.Id && !IsEditingMessage)
            OnPropertyChanged(nameof(ComposerRunOptions));
    }

    [RelayCommand]
    private void ToggleComposerRunOption(string? id)
    {
        var option = id switch
        {
            nameof(ChatRunOptions.Autopilot) => ChatRunOptions.Autopilot,
            nameof(ChatRunOptions.Parallel) => ChatRunOptions.Parallel,
            nameof(ChatRunOptions.Research) => ChatRunOptions.Research,
            _ => ChatRunOptions.None
        };
        if (option == ChatRunOptions.None)
            return;

        var draft = DraftRunOptions;
        if (draft.HasFlag(option))
            SetDraftRunOptions(draft & ~option);
        else if (option == ChatRunOptions.Research && ResearchUnavailableReason(SelectedModel) is not null)
            return;
        else if (option == ChatRunOptions.Research || draft == ChatRunOptions.Research)
            SetDraftRunOptions(option);
        else
            SetDraftRunOptions(draft | option);
    }

    internal void SetDraftRunOptions(ChatRunOptions options, bool syncToChat = true)
    {
        if (IsEditingMessage)
        {
            if (_editingRunOptions == options)
                return;
            _editingRunOptions = options;
            OnPropertyChanged(nameof(ComposerRunOptions));
            return;
        }

        if (options == DraftRunOptions)
            return;
        if (CurrentChat is { } chat)
        {
            chat.DraftRunOptions = options;
            if (syncToChat)
                QueueSaveChat(chat, saveIndex: true);
        }
        else
            _newChatRunOptions = options;
        OnPropertyChanged(nameof(ComposerRunOptions));
    }

    private ChatRunOptions TakeDraftRunOptions()
    {
        var options = DraftRunOptions;
        SetDraftRunOptions(ChatRunOptions.None);
        return options;
    }

    internal string? ResearchUnavailableReason(string? model)
        => _dataStore.Data.Settings.UseBYOKOnly || ByokConfigHelper.IsByokModel(model)
            ? Loc.Get("Composer_ResearchRequiresCopilot")
            : null;

    internal string? GetResearchUnavailableReasonForChat(Chat chat)
        => ResearchUnavailableReason(ResolveSelectedModelForChat(chat));

    private bool BlockComposerResearchForModel(Chat? chat, ChatRunOptions options, string? model)
    {
        if (options != ChatRunOptions.Research || ResearchUnavailableReason(model) is not { } reason)
            return false;

        StatusText = reason;
        AppendSendBlockedMessage(chat, reason);
        return true;
    }

#pragma warning disable GHCP001, MCPSDK001
    private async Task<MessageOptions> PrepareComposerRunAsync(Chat chat, ChatRuntimeState runtime, CopilotSession session,
        MessageOptions options, ChatRunOptions runOptions, CancellationToken ct)
    {
        if (runtime.NeedsInteractiveModeRestore)
            await RestoreInteractiveModeAsync(runtime, session).WaitAsync(ct);

        if ((runOptions & ChatRunOptions.Research) != 0)
        {
            if (ResearchUnavailableReason(ResolveSelectedModelForChat(chat)) is { } reason)
                throw new InvalidOperationException(reason);
            options = await PrepareResearchPromptAsync(session, options, ct);
        }

        // Fleet has no per-message mode parameter. Keep its temporary session mode until the
        // run stops: restoring it immediately can suppress the live continuation decision.
        if (runOptions == (ChatRunOptions.Autopilot | ChatRunOptions.Parallel))
        {
            runtime.NeedsInteractiveModeRestore = true;
            var mode = await session.Rpc.Mode.SetAsync(SessionMode.Autopilot, cancellationToken: ct);
            if (mode.ModeApplied == false)
                throw new InvalidOperationException(mode.Message ?? Loc.Get("Composer_AutopilotUnavailable"));
        }

        return options;
    }

    private static async Task SubmitComposerRunAsync(ChatRuntimeState runtime, CopilotSession session,
        MessageOptions options, ChatRunOptions runOptions, CancellationToken ct)
    {
        runtime.IsComposerAutopilot = (runOptions & ChatRunOptions.Autopilot) != 0;
        if ((runOptions & ChatRunOptions.Parallel) == 0)
        {
            if (runtime.IsComposerAutopilot)
            {
                options = options.Clone();
                options.AgentMode = AgentMode.Autopilot;
            }
            await session.SendAsync(options, ct);
            return;
        }

        var result = await session.Rpc.Fleet.StartAsync(options.Prompt, options.Attachments, cancellationToken: ct);
        if (!result.Started)
            throw new InvalidOperationException(Loc.Get("Composer_ParallelUnavailable"));
    }

    private static async Task TryRestoreInteractiveModeAsync(ChatRuntimeState runtime, CopilotSession session)
    {
        if (!runtime.NeedsInteractiveModeRestore)
            return;

        try { await RestoreInteractiveModeAsync(runtime, session); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"[Chat] Could not restore Interactive mode: {ex}"); }
    }

    private static Task RestoreInteractiveModeAsync(ChatRuntimeState runtime, CopilotSession session)
        => runtime.InteractiveModeRestore is { IsCompleted: false } pending
            ? pending
            : runtime.InteractiveModeRestore = RestoreInteractiveModeCoreAsync(runtime, session);

    private static async Task RestoreInteractiveModeCoreAsync(ChatRuntimeState runtime, CopilotSession session)
    {
        var restored = await session.Rpc.Mode.SetAsync(SessionMode.Interactive);
        if (restored.ModeApplied == false)
            throw new InvalidOperationException(restored.Message ?? Loc.Status_OriginalSessionUnavailable);
        runtime.NeedsInteractiveModeRestore = false;
    }

    internal static async Task<MessageOptions> PrepareResearchPromptAsync(CopilotSession session, MessageOptions options, CancellationToken ct)
    {
        var result = await session.Rpc.Commands.InvokeAsync("research", options.Prompt, cancellationToken: ct);
        if (result is not Microsoft.Copilot.Rpc.SlashCommandInvocationResultAgentPrompt { Prompt.Length: > 0 } research)
        {
            var message = (result as Microsoft.Copilot.Rpc.SlashCommandInvocationResultText)?.Text;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? Loc.Get("Composer_ResearchUnavailable") : message);
        }

        var prepared = options.Clone();
        prepared.Prompt = research.Prompt;
        return prepared;
    }
#pragma warning restore GHCP001, MCPSDK001
}
