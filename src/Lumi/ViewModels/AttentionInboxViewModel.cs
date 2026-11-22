using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Microsoft.Data.Sqlite;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

internal sealed record PendingChatInput(
    ChatViewModel Owner, Chat Chat, string RequestId, ChatMessage? Message, PermissionRequestViewModel? Approval);

public sealed record AttentionContextEntry(string Label, string Text);

public sealed partial class AttentionRequestViewModel : ObservableObject, IDisposable
{
    private readonly PendingChatInput _request;
    private readonly DataStore _store;
    private readonly Func<Chat, Task<bool>> _openChat;
    private readonly CancellationTokenSource _lifetime = new();
    public DateTimeOffset RequestedAt { get; }
    public Guid ChatId => _request.Chat.Id;
    public string RequestId => _request.RequestId;
    public string ChatTitle => _request.Chat.Title;
    public string ProjectName => _store.Data.Projects.Find(project => project.Id == _request.Chat.ProjectId)?.Name
        ?? Loc.Unread_NoProject;
    public string AskedAt => Loc.Get("NeedsYou_AskedAt", RequestedAt.ToLocalTime().ToString("g", Loc.Culture));
    public string Kind => Loc.Get(_request.Approval is not null ? "NeedsYou_Approval"
        : _request.Message?.ToolName == "ask_input" ? "NeedsYou_Form" : "NeedsYou_Question");
    public bool CanAnswer => !_request.Chat.IsPaused;
    public TranscriptItem Item { get; }
    internal Task ContextReady { get; }

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private IReadOnlyList<AttentionContextEntry> _context = [];
    [ObservableProperty] private bool _isContextLoading = true;
    [ObservableProperty] private bool _isContextExpanded;
    [ObservableProperty] private string? _contextError;
    [ObservableProperty] private string? _error;

    internal AttentionRequestViewModel(PendingChatInput request, DataStore store, Func<Chat, Task<bool>> openChat)
    {
        _request = request;
        _store = store;
        _openChat = openChat;
        RequestedAt = request.Approval?.RequestedAt ?? request.Message!.Timestamp;
        Item = request.Approval is { } approval ? new ApprovalItem(approval)
            : request.Message is { ToolName: "ask_input" } input ? new InputRequestItem(input)
            : new QuestionItem(request.RequestId, request.Message!.QuestionText ?? "",
                JsonSerializer.Deserialize(request.Message.QuestionOptions ?? "[]", AppDataJsonContext.Default.ListString) ?? [],
                request.Message.QuestionAllowFreeText ?? true, SubmitAnswer,
                request.Message.QuestionAllowMultiSelect ?? false);
        request.Chat.PropertyChanged += OnChatChanged;
        ContextReady = LoadContextAsync();
    }

    private void SubmitAnswer(string questionId, string answer)
    {
        Error = null;
        if (_request.Owner.TryAnswerPendingInput(ChatId, questionId, answer))
            return;
        Error = !CanAnswer ? Loc.Chat_PausedSendBlocked : InteractiveLabels.ForLanguage(Loc.Culture.Name)["inputExpired"];
        if (Item is QuestionItem question)
        {
            question.IsAnswered = false;
            question.SelectedAnswer = null;
        }
    }

    internal async Task<InteractiveActionResult> RequestInteractionAsync(string blockId, string state, string? action)
    {
        if (_lifetime.IsCancellationRequested || _request.Message is not { ToolName: "ask_input" } message)
            return new(false, InteractiveLabels.ForLanguage(Loc.Culture.Name)["inputExpired"]);
        var result = await _request.Owner.RequestInputInteractionAsync(_request.Chat, message, blockId, state, action);
        ((InputRequestItem)Item).Refresh();
        return result;
    }

    [RelayCommand]
    private async Task OpenChat()
    {
        Error = null;
        try
        {
            if (!await _openChat(_request.Chat))
                Error = Loc.Get("NeedsYou_OpenFailed");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            Trace.TraceWarning("[NeedsYou] Could not open chat {0}: {1}", ChatId, ex);
            Error = Loc.Get("NeedsYou_OpenFailed");
        }
    }

    [RelayCommand]
    private async Task LoadContextAsync()
    {
        IsContextLoading = true;
        ContextError = null;
        try
        {
            // Only a bounded conversation window, never full transcripts, reasoning, or raw tool output.
            var messages = await _store.ReadRecentChatMessagesAsync(_request.Chat, 12,
                conversationOnly: true, cancellationToken: _lifetime.Token);
            if (_lifetime.IsCancellationRequested)
                return;
            var before = messages.Where(message => message.Timestamp <= RequestedAt
                && message.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(message.Content)).ToList();
            var user = before.LastOrDefault(message => message.Role == "user");
            var update = before.LastOrDefault(message => message.Role == "assistant"
                && (user is null || message.Timestamp >= user.Timestamp));
            var recap = new List<string>();
            if (user is not null)
                recap.Add(Loc.Get("NeedsYou_UserRecap", ManagementText.PlainPreview(user.Content, 220)));
            if (update is not null)
                recap.Add(Loc.Get("NeedsYou_UpdateRecap", ManagementText.PlainPreview(update.Content, 320)));
            var steps = _request.Chat.Messages.Where(message => message.Role == "tool"
                    && message.ToolName is not (null or "ask_question" or "ask_input")
                    && message.Timestamp <= RequestedAt && (user is null || message.Timestamp >= user.Timestamp))
                .TakeLast(3)
                .Select(message => ManagementText.PlainPreview(
                    ToolDisplayHelper.GetCommandTitle(message.Content)
                    ?? ToolDisplayHelper.GetFriendlyToolDisplay(message.ToolName!, message.Author, message.Content).Name, 100))
                .ToArray();
            if (steps.Length > 0)
                recap.Add(Loc.Get("NeedsYou_ActivityRecap", string.Join(" · ", steps)));
            Summary = recap.Count > 0 ? string.Join("\n\n", recap) : Loc.Get("NeedsYou_NoContext");
            Context = before.TakeLast(8).Select(message => new AttentionContextEntry(
                message.Role == "user" ? Loc.Get("NeedsYou_You") : "Lumi",
                ManagementText.PlainPreview(message.Content, 2400))).ToArray();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or JsonException)
        {
            Trace.TraceWarning("[NeedsYou] Could not read context for {0}: {1}", ChatId, ex);
            ContextError = Loc.Get("NeedsYou_ContextFailed");
        }
        finally
        {
            IsContextLoading = false;
        }
    }

    private void OnChatChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Chat.IsPaused))
            OnPropertyChanged(nameof(CanAnswer));
        if (args.PropertyName == nameof(Chat.Title))
            OnPropertyChanged(nameof(ChatTitle));
    }

    public void Dispose()
    {
        _request.Chat.PropertyChanged -= OnChatChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

public sealed class AttentionInboxViewModel : ObservableObject, IDisposable
{
    private readonly DataStore _store;
    private readonly ChatSurfaceRegistry _surfaces;
    private readonly ChatEventHub _events;
    private readonly Func<Chat, Task<bool>> _openChat;
    private bool _disposed;

    public ObservableCollection<AttentionRequestViewModel> Requests { get; } = [];
    public bool HasRequests => Requests.Count > 0;
    public string BannerText => Loc.Get("NeedsYou_Banner", Requests.Count);
    public string CountText => Requests.Count.ToString(Loc.Culture);

    internal AttentionInboxViewModel(DataStore store, ChatSurfaceRegistry surfaces, ChatEventHub events,
        Func<Chat, Task<bool>> openChat)
    {
        _store = store;
        _surfaces = surfaces;
        _events = events;
        _openChat = openChat;
        events.AttentionRaised += OnAttentionRaised;
        events.AttentionResolved += OnAttentionResolved;
        Refresh();
    }

    internal void Refresh()
    {
        if (_disposed)
            return;
        var pending = _surfaces.SnapshotSurfaces().SelectMany(surface => surface.GetPendingInputs()).ToList();
        foreach (var entry in Requests.ToArray())
            if (!pending.Any(request => request.Chat.Id == entry.ChatId && request.RequestId == entry.RequestId))
                Remove(entry);
        foreach (var request in pending)
            Add(request);
    }

    private void OnAttentionRaised(ChatAttention attention)
    {
        if (_disposed || attention.Kind == ChatAttentionKind.Reply)
            return;
        foreach (var request in _surfaces.SnapshotSurfaces().SelectMany(surface => surface.GetPendingInputs())
                     .Where(request => request.Chat.Id == attention.ChatId && request.RequestId == attention.RequestId))
            Add(request);
    }

    private void OnAttentionResolved(Guid chatId, string requestId)
    {
        if (_disposed)
            return;
        foreach (var entry in Requests.Where(entry => entry.ChatId == chatId && entry.RequestId == requestId).ToArray())
            Remove(entry);
    }

    private void Add(PendingChatInput request)
    {
        if (Requests.Any(entry => entry.ChatId == request.Chat.Id && entry.RequestId == request.RequestId))
            return;
        Requests.Add(new AttentionRequestViewModel(request, _store, _openChat));
        NotifyCounts();
    }

    private void Remove(AttentionRequestViewModel entry)
    {
        Requests.Remove(entry);
        entry.Dispose();
        NotifyCounts();
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(HasRequests));
        OnPropertyChanged(nameof(BannerText));
        OnPropertyChanged(nameof(CountText));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _events.AttentionRaised -= OnAttentionRaised;
        _events.AttentionResolved -= OnAttentionResolved;
        foreach (var entry in Requests)
            entry.Dispose();
        Requests.Clear();
    }
}
