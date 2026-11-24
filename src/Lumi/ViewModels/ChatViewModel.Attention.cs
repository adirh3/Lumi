using Lumi.Models;
using Lumi.Services;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;

namespace Lumi.ViewModels;

/// <summary>
/// The moments a chat needs the user: the desktop's own notification, and the same moment published
/// on the shared <see cref="ChatEventHub"/> so a paired phone can be told while the user is away.
/// </summary>
public partial class ChatViewModel
{
    internal IReadOnlyList<PendingChatInput> GetPendingInputs()
    {
        var requests = new List<PendingChatInput>();
        lock (_pendingQuestionsSync)
        {
            foreach (var (id, chatId) in _pendingQuestionChatIds)
            {
                if (!_pendingQuestions.TryGetValue(id, out var completion) || completion.Task.IsCompleted)
                    continue;
                var chat = _dataStore.Data.Chats.Find(candidate => candidate.Id == chatId);
                var message = chat?.Messages.LastOrDefault(candidate => candidate.QuestionId == id);
                if (chat is not null && message is not null)
                    requests.Add(new(this, chat, id, message, null));
            }
        }
        foreach (var approval in _permissionRequests.Values.Where(request => request.IsPending))
        {
            var chat = _dataStore.Data.Chats.Find(candidate => candidate.Id == approval.ChatId);
            if (chat is not null)
                requests.Add(new(this, chat, approval.RequestId, null, approval));
        }
        return requests;
    }

    internal AttentionInboxViewModel? AttentionInbox { get; set; }
    public string? PendingInputTarget { get; private set; }
    public event Action<string>? PendingInputJumpRequested;
    private IReadOnlyList<AttentionRequestViewModel> PendingInputs => AttentionInbox?.Requests.ToList() ?? [];
    private int CurrentPendingInputCount => AttentionInbox is null
        ? GetPendingInputs().Count(entry => entry.Chat.Id == CurrentChat?.Id)
        : PendingInputs.Count(entry => entry.ChatId == CurrentChat?.Id);
    public int OtherPendingChatCount => PendingInputs.Where(entry => entry.ChatId != CurrentChat?.Id).Select(entry => entry.ChatId).Distinct().Count();
    public bool ShowPendingInputBar => CurrentPendingInputCount > 0 || (PendingInputTarget is not null && OtherPendingChatCount > 0);
    public string PendingInputWaitingText => CurrentPendingInputCount > 0
        ? Loc.Get("NeedsYou_ThisChat") : Loc.Get("NeedsYou_Answered");
    public string PendingInputElsewhereText => Loc.Get("NeedsYou_Elsewhere", OtherPendingChatCount);
    public bool HasOtherPendingChats => OtherPendingChatCount > 0;
    public bool HasCurrentPendingInputs => CurrentPendingInputCount > 0;
    public bool HasNextPendingInput => NextPendingInput() is not null;

    internal void RefreshPendingInputNavigation()
    {
        OnPropertyChanged(nameof(ShowPendingInputBar));
        OnPropertyChanged(nameof(PendingInputWaitingText));
        OnPropertyChanged(nameof(PendingInputElsewhereText));
        OnPropertyChanged(nameof(HasOtherPendingChats));
        OnPropertyChanged(nameof(HasCurrentPendingInputs));
        OnPropertyChanged(nameof(HasNextPendingInput));
        OnPropertyChanged(nameof(OtherPendingChatCount));
    }

    internal void RevealPendingInput(string itemId)
    {
        PendingInputTarget = itemId;
        RefreshPendingInputNavigation();
        PendingInputJumpRequested?.Invoke(itemId);
    }

    private AttentionRequestViewModel? NextPendingInput()
    {
        var pending = PendingInputs;
        var current = PendingInputTarget ?? pending.FirstOrDefault(entry => entry.ChatId == CurrentChat?.Id)?.ItemId;
        return pending.FirstOrDefault(entry => entry.ChatId == CurrentChat?.Id && entry.ItemId != current)
            ?? pending.FirstOrDefault(entry => entry.ChatId != CurrentChat?.Id);
    }

    [RelayCommand]
    private void ReviewPendingInput()
    {
        var target = PendingInputs.FirstOrDefault(entry => entry.ChatId == CurrentChat?.Id)?.ItemId
            ?? GetPendingInputs().FirstOrDefault(entry => entry.Chat.Id == CurrentChat?.Id)?.ItemId;
        if (target is not null)
            RevealPendingInput(target);
    }

    [RelayCommand]
    private void NextPendingInputInChats()
    {
        if (NextPendingInput() is not { } next)
            return;
        next.Source.Owner.RevealPendingInput(next.ItemId);
        if (next.ChatId != CurrentChat?.Id)
            RequestRevealChat(next.ChatId);
    }

#if DEBUG
    internal async Task RunAttentionQuestionFixtureAsync(Chat chat)
    {
        try
        {
            var answer = await BuildAskQuestionTool(chat.Id).InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
            {
                ["question"] = "Should the inbox include every project, or only the selected project?",
                ["options"] = new[] { "Every project", "Selected project only" },
                ["allowFreeText"] = true, ["allowMultiSelect"] = false
            });
            chat.Messages.Add(new() { Role = "assistant", Content = $"Thanks! The fixture received: {answer}" });
            chat.HasUnreadMessages = true;
        }
        catch (OperationCanceledException) { }
    }
#endif

    private void NotifyQuestionAsked(ChatAttention attention)
    {
        _chatEvents.PublishAttention(attention);
        if (!_dataStore.Data.Settings.NotificationsEnabled)
            return;

        var chatTitle = _dataStore.Data.Chats.FirstOrDefault(c => c.Id == attention.ChatId)?.Title;
        NotificationService.ShowQuestion(attention.Text, chatTitle, attention.ChatId);
    }

    /// <summary>The words that ended the latest exchange, or null when it ended without any.</summary>
    internal static string? LatestAnswerText(Chat chat)
    {
        for (var index = chat.Messages.Count - 1; index >= 0; index--)
        {
            var message = chat.Messages[index];
            if (message.Role == "user")
                break;
            if (message.Role == "assistant"
                && message.IsProgress != true
                && !string.IsNullOrWhiteSpace(message.Content))
            {
                return message.Content;
            }
        }

        return null;
    }
}
