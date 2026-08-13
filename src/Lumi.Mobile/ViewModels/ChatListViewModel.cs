using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Remote.Protocol;

namespace Lumi.Mobile.ViewModels;

public sealed partial class ChatListItemViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string? _preview;
    [ObservableProperty] private Guid? _projectId;
    [ObservableProperty] private string? _projectName;
    [ObservableProperty] private Guid? _agentId;
    [ObservableProperty] private string? _agentName;
    [ObservableProperty] private string _agentGlyph = "";
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _hasUnreadMessages;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private int _messageCount;
    [ObservableProperty] private DateTimeOffset _updatedAt;
    [ObservableProperty] private string? _lastModelUsed;

    public Guid Id { get; private set; }

    public bool HasProject => !string.IsNullOrWhiteSpace(ProjectName);

    public bool HasAgent => !string.IsNullOrWhiteSpace(AgentName);

    /// <summary>
    /// Compact "when" label for the row: "now", "14m", "3h", "2d", then a date. Rows are already
    /// bucketed by day, so this only has to disambiguate within a bucket and must stay narrow.
    /// </summary>
    public string RelativeTime
    {
        get
        {
            var elapsed = DateTimeOffset.Now - UpdatedAt;
            return elapsed switch
            {
                { TotalMinutes: < 1 } => "now",
                { TotalMinutes: < 60 } => $"{(int)elapsed.TotalMinutes}m",
                { TotalHours: < 24 } => $"{(int)elapsed.TotalHours}h",
                { TotalDays: < 7 } => $"{(int)elapsed.TotalDays}d",
                _ => UpdatedAt.ToString("MMM d")
            };
        }
    }

    public ChatListItemViewModel(RemoteChat chat) => Update(chat);

    public void Update(RemoteChat chat)
    {
        Id = chat.Id;
        Title = chat.Title;
        Preview = chat.Preview;
        ProjectId = chat.ProjectId;
        ProjectName = chat.ProjectName;
        AgentId = chat.AgentId;
        AgentName = chat.AgentName;
        AgentGlyph = chat.AgentGlyph ?? "";
        IsPinned = chat.IsPinned;
        IsRunning = chat.IsRunning;
        HasUnreadMessages = chat.HasUnreadMessages;
        MessageCount = chat.MessageCount;
        UpdatedAt = chat.UpdatedAt;
        LastModelUsed = chat.LastModelUsed;
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(HasAgent));
        OnPropertyChanged(nameof(RelativeTime));
    }
}

public sealed partial class ChatGroupViewModel : ObservableObject
{
    [ObservableProperty] private string _label = "";

    public ObservableCollection<ChatListItemViewModel> Chats { get; } = [];

    public ChatGroupViewModel(string label) => Label = label;
}

/// <summary>
/// Chat list with search. Grouping mirrors the desktop sidebar exactly (the server projects the
/// same buckets), so the phone and PC never disagree about where a chat lives.
/// </summary>
public sealed partial class ChatListViewModel : ObservableObject, IDisposable
{
    internal const int InitialVisibleChatLimit = 120;
    internal const int ChatPageSize = 120;

    private readonly IRemoteCommandSink _sink;
    private readonly IRemoteChatPageSink? _pageSink;
    private readonly Dictionary<Guid, ChatListItemViewModel> _realizedChats = [];
    private List<RemoteChatGroup> _source = [];
    private CancellationTokenSource? _reloadCts = new();
    private long _reloadGeneration;
    private bool _serverPaged;
    private bool _serverHasMore;
    private int _loadedChatCount;
    private int _visibleLimit = InitialVisibleChatLimit;
    private int _totalChatCount;
    private int _matchingChatCount;
    private int _visibleChatCount;
    private string _pinnedGroupLabel = "Pinned";
    private string _todayGroupLabel = "Today";

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private Guid _selectedChatId;
    [ObservableProperty] private bool _isRefreshing;

    /// <summary>
    /// When set, the list shows only that project's chats. This is what "selecting a project" means
    /// on Lumi desktop and in ChatGPT: the project becomes the lens you are working through, not just
    /// a label attached to the next message.
    /// </summary>
    [ObservableProperty] private Guid? _projectFilterId;

    partial void OnProjectFilterIdChanged(Guid? value) => QueueServerReload(debounce: false);

    public ChatListViewModel(IRemoteCommandSink sink)
    {
        _sink = sink;
        _pageSink = sink as IRemoteChatPageSink;
    }

    public ObservableCollection<ChatGroupViewModel> Groups { get; } = [];

    public int TotalChats => _totalChatCount;

    public int MatchingChatCount => _matchingChatCount;

    public int VisibleChatCount => _visibleChatCount;

    public bool HasMoreChats => _serverPaged ? _serverHasMore : VisibleChatCount < MatchingChatCount;

    public string VisibleChatCountLabel => $"{VisibleChatCount:N0} of {MatchingChatCount:N0} shown";

    public bool IsEmpty => MatchingChatCount == 0;

    /// <summary>Raised when the user picks a chat, so the shell can navigate to the detail pane.</summary>
    public event Action<Guid, string, string?, int>? ChatActivated;

    public event Action<Guid>? ChatRemoved;

    internal IReadOnlyList<RemoteChatGroup> SnapshotLoadedGroups() =>
        _source.Select(group => new RemoteChatGroup
        {
            Label = group.Label,
            Chats = [.. group.Chats]
        }).ToList();

    public void Apply(IEnumerable<RemoteChatGroup> groups)
    {
        _serverPaged = false;
        _serverHasMore = false;
        var hadChats = TotalChats > 0;
        var previousIds = _source.SelectMany(group => group.Chats).Select(chat => chat.Id).ToHashSet();
        _source = groups.ToList();
        var currentIds = _source.SelectMany(group => group.Chats).Select(chat => chat.Id).ToHashSet();
        var totalChats = _source.Sum(group => group.Chats.Count);

        if (!hadChats || totalChats == 0)
            _visibleLimit = InitialVisibleChatLimit;
        InferGroupLabels();

        if (_totalChatCount != totalChats)
        {
            _totalChatCount = totalChats;
            OnPropertyChanged(nameof(TotalChats));
        }

        Rebuild();

        foreach (var removedId in previousIds.Where(id => !currentIds.Contains(id)))
            ChatRemoved?.Invoke(removedId);
    }

    public void Apply(RemoteChatPage page, bool reset = true)
    {
        _serverPaged = true;
        _pinnedGroupLabel = page.PinnedGroupLabel;
        _todayGroupLabel = page.TodayGroupLabel;
        if (reset)
            _source = [];

        MergePage(page);
        _loadedChatCount = _source.Sum(group => group.Chats.Count);
        _visibleLimit = _loadedChatCount;
        _serverHasMore = page.HasMore;
        if (_totalChatCount != page.TotalCount)
        {
            _totalChatCount = page.TotalCount;
            OnPropertyChanged(nameof(TotalChats));
        }
        Rebuild();
    }

    /// <summary>
    /// Applies a live first-page patch without collapsing rows loaded with "Load more" or reordering
    /// the list beneath the user's finger. A normal view refresh performs authoritative sorting.
    /// </summary>
    public void ApplyLive(RemoteChatPage page)
    {
        _pinnedGroupLabel = page.PinnedGroupLabel;
        _todayGroupLabel = page.TodayGroupLabel;
        var removedIds = page.RemovedChatIds.ToHashSet();
        foreach (var group in _source)
            group.Chats.RemoveAll(chat => removedIds.Contains(chat.Id));
        _source.RemoveAll(group => group.Chats.Count == 0);

        foreach (var incomingGroup in page.Groups)
        {
            foreach (var incoming in incomingGroup.Chats)
            {
                RemoteChatGroup? existingGroup = null;
                var existingIndex = -1;
                foreach (var group in _source)
                {
                    existingIndex = group.Chats.FindIndex(chat => chat.Id == incoming.Id);
                    if (existingIndex >= 0)
                    {
                        existingGroup = group;
                        break;
                    }
                }

                if (existingGroup is not null)
                {
                    existingGroup.Chats[existingIndex] = incoming;
                    continue;
                }

                var target = _source.FirstOrDefault(group =>
                    string.Equals(group.Label, incomingGroup.Label, StringComparison.Ordinal));
                if (target is null)
                {
                    target = new RemoteChatGroup { Label = incomingGroup.Label };
                    _source.Insert(0, target);
                }
                target.Chats.Insert(0, incoming);
            }
        }

        _serverPaged = true;
        _serverHasMore = page.HasMore;
        _loadedChatCount = _source.Sum(group => group.Chats.Count);
        _visibleLimit = Math.Max(_visibleLimit, _loadedChatCount);
        if (_totalChatCount != page.TotalCount)
        {
            _totalChatCount = page.TotalCount;
            OnPropertyChanged(nameof(TotalChats));
        }
        Rebuild();
    }

    public void SetRunning(Guid chatId, bool isRunning)
    {
        foreach (var chat in _source.SelectMany(group => group.Chats).Where(chat => chat.Id == chatId))
            chat.IsRunning = isRunning;
        if (_realizedChats.TryGetValue(chatId, out var realized))
            realized.IsRunning = isRunning;
    }

    public void PromoteChat(RemoteChat incoming, bool isNewChat = false)
    {
        RemoteChat? existing = null;
        foreach (var group in _source)
        {
            var index = group.Chats.FindIndex(chat => chat.Id == incoming.Id);
            if (index < 0)
                continue;
            existing = group.Chats[index];
            existing = group.Chats[index];
            group.Chats.RemoveAt(index);
            break;
        }
        _source.RemoveAll(group => group.Chats.Count == 0);

        var promoted = new RemoteChat
        {
            Id = incoming.Id,
            Title = string.IsNullOrWhiteSpace(incoming.Title)
                ? existing?.Title ?? "New chat"
                : incoming.Title,
            Preview = incoming.Preview ?? existing?.Preview,
            ProjectId = incoming.ProjectId ?? existing?.ProjectId,
            ProjectName = incoming.ProjectName ?? existing?.ProjectName,
            AgentId = incoming.AgentId ?? existing?.AgentId,
            AgentName = incoming.AgentName ?? existing?.AgentName,
            AgentGlyph = incoming.AgentGlyph ?? existing?.AgentGlyph,
            MessageCount = existing is null
                ? Math.Max(1, incoming.MessageCount)
                : Math.Max(incoming.MessageCount, existing.MessageCount),
            UpdatedAt = incoming.UpdatedAt == default ? DateTimeOffset.Now : incoming.UpdatedAt,
            IsPinned = existing?.IsPinned ?? incoming.IsPinned,
            IsRunning = incoming.IsRunning,
            HasUnreadMessages = false,
            LastModelUsed = incoming.LastModelUsed ?? existing?.LastModelUsed
        };

        var targetLabel = promoted.IsPinned
            ? _pinnedGroupLabel
            : _todayGroupLabel;
        var target = _source.FirstOrDefault(group =>
            string.Equals(group.Label, targetLabel, StringComparison.Ordinal));
        if (target is null)
        {
            target = new RemoteChatGroup { Label = targetLabel };
            var targetIndex = promoted.IsPinned ? 0 : _source.FindIndex(group =>
                !string.Equals(
                    group.Label,
                    _pinnedGroupLabel,
                    StringComparison.Ordinal));
            _source.Insert(targetIndex < 0 ? _source.Count : targetIndex, target);
        }
        target.Chats.Insert(0, promoted);

        if (existing is null && isNewChat)
        {
            _totalChatCount++;
            _loadedChatCount++;
        }
        _visibleLimit = Math.Max(
            _visibleLimit,
            _source.Sum(group => group.Chats.Count));
        Rebuild();
    }

    private void InferGroupLabels()
    {
        var pinned = _source.FirstOrDefault(group =>
            group.Chats.Any(chat => chat.IsPinned));
        if (pinned is not null)
            _pinnedGroupLabel = pinned.Label;

        var today = DateTimeOffset.Now.Date;
        var todayGroup = _source.FirstOrDefault(group =>
            group.Chats.Any(chat => !chat.IsPinned && chat.UpdatedAt.Date == today));
        if (todayGroup is not null)
            _todayGroupLabel = todayGroup.Label;
    }

    partial void OnSearchTextChanged(string value) => QueueServerReload(debounce: true);

    partial void OnSelectedChatIdChanged(Guid oldValue, Guid newValue)
    {
        if (_realizedChats.TryGetValue(oldValue, out var oldChat))
            oldChat.IsSelected = false;

        if (_realizedChats.TryGetValue(newValue, out var newChat))
            newChat.IsSelected = true;
    }

    private void ResetPageAndRebuild()
    {
        _visibleLimit = InitialVisibleChatLimit;
        Rebuild();
    }

    private void Rebuild()
    {
        var query = SearchText.Trim();
        var projected = new List<(string Label, List<RemoteChat> Chats)>();
        var remainingVisibleSlots = _visibleLimit;
        var matchingChatCount = _serverPaged ? _totalChatCount : 0;
        var visibleChatCount = 0;

        // The production source is already a server-filtered page. Tests and offline fixtures can
        // still provide a complete local source, so applying the same predicate here also gives
        // immediate visual feedback while a new server query is in flight.
        foreach (var group in _source)
        {
            List<RemoteChat>? visibleChats = null;

            foreach (var chat in group.Chats)
            {
                if (!Matches(chat))
                    continue;

                if (!_serverPaged)
                    matchingChatCount++;
                if (remainingVisibleSlots == 0)
                    continue;

                (visibleChats ??= []).Add(chat);
                remainingVisibleSlots--;
                visibleChatCount++;
            }

            if (visibleChats is not null)
                projected.Add((group.Label, visibleChats));
        }

        var visibleIds = projected
            .SelectMany(group => group.Chats)
            .Select(chat => chat.Id)
            .ToHashSet();

        foreach (var id in _realizedChats.Keys.Where(id => !visibleIds.Contains(id)).ToArray())
            _realizedChats.Remove(id);

        // Rebuilding in place (rather than clearing) keeps scroll position stable while typing and
        // preserves row instances across server updates.
        for (var i = 0; i < projected.Count; i++)
        {
            var (label, chats) = projected[i];

            if (i >= Groups.Count)
                Groups.Add(new ChatGroupViewModel(label));
            else
                Groups[i].Label = label;

            var target = Groups[i].Chats;
            for (var j = 0; j < chats.Count; j++)
            {
                var sourceChat = chats[j];
                if (_realizedChats.TryGetValue(sourceChat.Id, out var chat))
                {
                    chat.Update(sourceChat);
                }
                else
                {
                    chat = new ChatListItemViewModel(sourceChat);
                    _realizedChats.Add(sourceChat.Id, chat);
                }

                chat.IsSelected = sourceChat.Id == SelectedChatId;

                if (j >= target.Count)
                    target.Add(chat);
                else if (!ReferenceEquals(target[j], chat))
                    target[j] = chat;
            }

            while (target.Count > chats.Count)
                target.RemoveAt(target.Count - 1);
        }

        while (Groups.Count > projected.Count)
            Groups.RemoveAt(Groups.Count - 1);

        _matchingChatCount = matchingChatCount;
        _visibleChatCount = visibleChatCount;
        OnPropertyChanged(nameof(MatchingChatCount));
        OnPropertyChanged(nameof(VisibleChatCount));
        OnPropertyChanged(nameof(HasMoreChats));
        OnPropertyChanged(nameof(VisibleChatCountLabel));
        OnPropertyChanged(nameof(IsEmpty));
        return;

        bool Matches(RemoteChat chat)
        {
            // The project filter is a hard gate: a chat outside the active project is not in the
            // list at all, however well it matches the search text.
            if (ProjectFilterId is { } projectId && chat.ProjectId != projectId)
            {
                return false;
            }

            return query.Length == 0 ||
                   chat.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   (chat.Preview?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (chat.ProjectName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
        }
    }

    [RelayCommand]
    private async Task LoadMoreChatsAsync()
    {
        if (!HasMoreChats)
            return;

        if (_serverPaged && _pageSink is not null)
        {
            var generation = Volatile.Read(ref _reloadGeneration);
            var offset = _loadedChatCount;
            var query = SearchText.Trim();
            var projectId = ProjectFilterId;
            var cancellationToken = _reloadCts?.Token ?? CancellationToken.None;
            IsRefreshing = true;
            try
            {
                var page = await _pageSink.GetChatPageAsync(
                    offset,
                    ChatPageSize,
                    query,
                    projectId,
                    cancellationToken);
                if (page is not null
                    && generation == Volatile.Read(ref _reloadGeneration)
                    && offset == _loadedChatCount
                    && string.Equals(query, SearchText.Trim(), StringComparison.Ordinal)
                    && projectId == ProjectFilterId
                    && page.Offset == offset
                    && string.Equals(page.Query ?? "", query, StringComparison.Ordinal)
                    && page.ProjectId == projectId)
                {
                    Apply(page, reset: false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                if (generation == Volatile.Read(ref _reloadGeneration))
                    IsRefreshing = false;
            }
            return;
        }

        _visibleLimit = Math.Min(MatchingChatCount, _visibleLimit + ChatPageSize);
        Rebuild();
    }

    public Task RefreshFromServerAsync() => ReloadServerAsync(debounce: false);

    private void QueueServerReload(bool debounce)
    {
        if (_pageSink is null)
        {
            ResetPageAndRebuild();
            return;
        }

        _ = ReloadServerAsync(debounce);
    }

    private async Task ReloadServerAsync(bool debounce)
    {
        var generation = Interlocked.Increment(ref _reloadGeneration);
        var current = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _reloadCts, current);
        previous?.Cancel();
        previous?.Dispose();
        var cancellationToken = current.Token;

        try
        {
            if (debounce)
                await Task.Delay(200, cancellationToken);

            IsRefreshing = true;
            var page = await _pageSink!.GetChatPageAsync(
                0,
                InitialVisibleChatLimit,
                SearchText,
                ProjectFilterId,
                cancellationToken);
            if (page is not null
                && generation == Volatile.Read(ref _reloadGeneration)
                && !cancellationToken.IsCancellationRequested
                && page.Offset == 0
                && string.Equals(page.Query ?? "", SearchText.Trim(), StringComparison.Ordinal)
                && page.ProjectId == ProjectFilterId)
            {
                Apply(page);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (generation == Volatile.Read(ref _reloadGeneration))
                IsRefreshing = false;
        }
    }

    private void MergePage(RemoteChatPage page)
    {
        foreach (var incomingGroup in page.Groups)
        {
            var target = _source.FirstOrDefault(group =>
                string.Equals(group.Label, incomingGroup.Label, StringComparison.Ordinal));
            if (target is null)
            {
                target = new RemoteChatGroup { Label = incomingGroup.Label };
                _source.Add(target);
            }

            foreach (var incoming in incomingGroup.Chats)
            {
                var existingIndex = target.Chats.FindIndex(chat => chat.Id == incoming.Id);
                if (existingIndex >= 0)
                    target.Chats[existingIndex] = incoming;
                else
                    target.Chats.Add(incoming);
            }
        }
    }

    [RelayCommand]
    private void OpenChat(ChatListItemViewModel? chat)
    {
        if (chat is null)
            return;

        SelectedChatId = chat.Id;
        chat.HasUnreadMessages = false;
        ChatActivated?.Invoke(chat.Id, chat.Title, chat.LastModelUsed, chat.MessageCount);
    }

    /// <summary>
    /// Opens a blank chat locally WITHOUT creating anything on the PC.
    ///
    /// <para>This used to call <c>create_chat</c> immediately, so every tap left an empty "New Chat"
    /// in the history — and tapping it a few times while deciding what to ask littered the list with
    /// them. The desktop and ChatGPT both treat a new chat as an intent, not an object: it exists
    /// once you say something. <c>SendAsync</c> already creates the chat when it fires with no
    /// <c>ChatId</c>, and <c>_pendingConfiguration</c> already stages model/agent/project choices
    /// made before that, so deferring costs nothing.</para>
    /// </summary>
    [RelayCommand]
    private void NewChat()
    {
        SelectedChatId = Guid.Empty;
        ChatActivated?.Invoke(Guid.Empty, "New chat", null, 0);
    }

    [RelayCommand]
    private Task TogglePinAsync(ChatListItemViewModel? chat) =>
        chat is null
            ? Task.CompletedTask
            : _sink.SendCommandAsync(
                new RemoteCommand(RemoteProtocol.Actions.PinChat)
                    .With("chatId", chat.Id.ToString())
                    .With("pinned", (!chat.IsPinned).ToString()));

    [RelayCommand]
    private Task DeleteChatAsync(ChatListItemViewModel? chat) =>
        chat is null
            ? Task.CompletedTask
            : _sink.SendCommandAsync(
                new RemoteCommand(RemoteProtocol.Actions.DeleteChat).With("chatId", chat.Id.ToString()));

    [RelayCommand]
    private Task RenameChatAsync(ChatListItemViewModel? chat) =>
        chat is null
            ? Task.CompletedTask
            : _sink.SendCommandAsync(
                new RemoteCommand(RemoteProtocol.Actions.RenameChat)
                    .With("chatId", chat.Id.ToString())
                    .With("title", chat.Title));

    public void Dispose()
    {
        var reload = Interlocked.Exchange(ref _reloadCts, null);
        reload?.Cancel();
        reload?.Dispose();
    }
}

public interface IRemoteChatPageSink
{
    Task<RemoteChatPage?> GetChatPageAsync(
        int offset,
        int limit,
        string? query,
        Guid? projectId,
        CancellationToken cancellationToken);
}
