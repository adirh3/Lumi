using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

public sealed class ChatSessionStore : IDisposable
{
    private const int DefaultMaxIdleCachedSurfaces = 8;

    // How many idle-cached surfaces keep their realized transcript controls. Beyond this many, cached
    // surfaces are kept as lightweight view-models only — their built Avalonia control subtrees are
    // shed — so a deep pool of idle chats doesn't retain hundreds of live controls each. Keeping the
    // most-recently-used idle surface fully realized makes toggling between the current and previous
    // chat instant; the active (hosted) surface is never idle-cached, so it is never shed.
    private const int MaxRealizedIdleSurfaces = 1;

    private readonly DataStore _dataStore;
    private readonly CopilotService _copilotService;
    private readonly ChatSurfaceRegistry _registry;
    private readonly GlobalSearchService? _globalSearchService;
    private readonly Func<ChatViewModel, Chat, Task> _loadChatAsync;
    private readonly int _maxIdleCachedSurfaces;
    private readonly Dictionary<Guid, ChatViewModel> _sessionsByChatId = [];
    private readonly Dictionary<ChatViewModel, int> _hostCounts = [];
    private readonly HashSet<ChatViewModel> _surfaces = [];
    private readonly LinkedList<ChatViewModel> _idleSurfacesLru = new();
    private readonly SemaphoreSlim _acquireChatLock = new(1, 1);
    private bool _isDisposed;

    public ChatSessionStore(
        DataStore dataStore,
        CopilotService copilotService,
        ChatSurfaceRegistry registry,
        GlobalSearchService? globalSearchService = null)
        : this(dataStore, copilotService, registry, static (surface, chat) => surface.LoadChatAsync(chat), globalSearchService: globalSearchService)
    {
    }

    internal ChatSessionStore(
        DataStore dataStore,
        CopilotService copilotService,
        ChatSurfaceRegistry registry,
        Func<ChatViewModel, Chat, Task> loadChatAsync,
        int maxIdleCachedSurfaces = DefaultMaxIdleCachedSurfaces,
        GlobalSearchService? globalSearchService = null)
    {
        if (maxIdleCachedSurfaces < 0)
            throw new ArgumentOutOfRangeException(nameof(maxIdleCachedSurfaces));

        _dataStore = dataStore;
        _copilotService = copilotService;
        _registry = registry;
        _globalSearchService = globalSearchService;
        _loadChatAsync = loadChatAsync;
        _maxIdleCachedSurfaces = maxIdleCachedSurfaces;
    }

    /// <summary>
    /// Raised when any tracked surface reports a feature-management change (a project, skill,
    /// agent, MCP server, memory, or job created/updated/deleted through a chat tool).
    /// This lets the main window refresh its bound collections regardless of which surface
    /// executed the change — including chats running in the background, background-job chats,
    /// and detached chat windows.
    /// </summary>
    public event Action? SurfaceFeatureManagementStateChanged;

    public ChatViewModel AcquireDraft(Guid? projectId, Action<ChatViewModel>? configure = null)
    {
        ThrowIfDisposed();

        var surface = CreateTrackedSurface(configure);
        // Retain BEFORE seeding the draft's project context. SetDraftProjectContext raises a
        // PropertyChanged the store listens to (OnSurfacePropertyChanged -> CacheOrReleaseIfIdleAndUnhosted).
        // A brand-new draft has no CurrentChat and hostCount 0, so that callback would treat it as an idle,
        // uncacheable, unhosted surface and dispose it on the spot — handing the caller a disposed surface
        // that then throws ObjectDisposedException on first send. Retaining first (hostCount = 1) makes the
        // callback treat it as hosted and leave it alive.
        Retain(surface);
        SetDraftProjectContext(surface, projectId);
        return surface;
    }

    public async Task<ChatViewModel> AcquireChatAsync(Chat chat, Action<ChatViewModel>? configure = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(chat);

        await _acquireChatLock.WaitAsync();
        try
        {
            ThrowIfDisposed();

            if (!_sessionsByChatId.TryGetValue(chat.Id, out var surface))
            {
                surface = CreateTrackedSurface(configure);
                _sessionsByChatId[chat.Id] = surface;
            }

            Retain(surface);
            if (surface.CurrentChat?.Id != chat.Id)
            {
                try
                {
                    await _loadChatAsync(surface, chat);
                }
                catch
                {
                    Release(surface);
                    throw;
                }
            }

            return surface;
        }
        finally
        {
            _acquireChatLock.Release();
        }
    }

    public void Retain(ChatViewModel surface)
    {
        ThrowIfDisposed();
        TrackSurface(surface);
        RemoveFromIdleCache(surface);
        _hostCounts[surface] = _hostCounts.TryGetValue(surface, out var count) ? count + 1 : 1;
    }

    public void Release(ChatViewModel surface)
    {
        if (!_hostCounts.TryGetValue(surface, out var count))
            return;

        if (count > 1)
        {
            _hostCounts[surface] = count - 1;
            return;
        }

        _hostCounts[surface] = 0;
        CacheOrReleaseIfIdleAndUnhosted(surface);
    }

    public void CleanupChat(Guid chatId)
    {
        foreach (var surface in _surfaces.ToArray())
        {
            if (surface.CurrentChat?.Id == chatId
                || surface.OwnsLiveChat(chatId)
                || surface.HasBrowserService(chatId))
                surface.CleanupSession(chatId);
        }

        if (_sessionsByChatId.TryGetValue(chatId, out var mappedSurface)
            && _hostCounts.GetValueOrDefault(mappedSurface) == 0)
        {
            UntrackSurface(mappedSurface, dispose: true);
        }
    }

    public IReadOnlyList<ChatViewModel> SnapshotSurfaces() => _surfaces.ToArray();

    public void ApplyToSurfaces(Action<ChatViewModel> action)
    {
        foreach (var surface in _surfaces.ToArray())
            action(surface);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        foreach (var surface in _surfaces.ToArray())
            UntrackSurface(surface, dispose: true);
        _acquireChatLock.Dispose();
    }

    public static void SetDraftProjectContext(ChatViewModel surface, Guid? projectId)
    {
        if (projectId.HasValue)
            surface.SetProjectId(projectId.Value);
        else
            surface.ClearProjectId();
    }

    private ChatViewModel CreateTrackedSurface(Action<ChatViewModel>? configure = null)
    {
        var surface = new ChatViewModel(_dataStore, _copilotService, _globalSearchService)
        {
            SendWithEnter = _dataStore.Data.Settings.SendWithEnter
        };
        configure?.Invoke(surface);
        TrackSurface(surface);
        return surface;
    }

    private void TrackSurface(ChatViewModel surface)
    {
        if (!_surfaces.Add(surface))
            return;

        _hostCounts.TryAdd(surface, 0);
        _registry.Attach(surface);
        surface.PropertyChanged += OnSurfacePropertyChanged;
        surface.FeatureManagementStateChanged += OnSurfaceFeatureManagementStateChanged;
        if (surface.CurrentChat is { } chat)
            RegisterChatOwner(surface, chat.Id);
    }

    private void UntrackSurface(ChatViewModel surface, bool dispose)
    {
        if (!_surfaces.Remove(surface))
            return;

        surface.PropertyChanged -= OnSurfacePropertyChanged;
        surface.FeatureManagementStateChanged -= OnSurfaceFeatureManagementStateChanged;
        _registry.Detach(surface);
        _hostCounts.Remove(surface);
        RemoveFromIdleCache(surface);
        RemoveChatOwner(surface);

        if (dispose)
            surface.Dispose();
    }

    private void OnSurfaceFeatureManagementStateChanged() => SurfaceFeatureManagementStateChanged?.Invoke();

    private void OnSurfacePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not ChatViewModel surface)
            return;

        if (args.PropertyName == nameof(ChatViewModel.CurrentChat))
        {
            RemoveChatOwner(surface);
            if (surface.CurrentChat is { } chat)
                RegisterChatOwner(surface, chat.Id);
        }

        if (args.PropertyName is nameof(ChatViewModel.CurrentChat)
            or nameof(ChatViewModel.IsBusy)
            or nameof(ChatViewModel.IsStreaming))
            CacheOrReleaseIfIdleAndUnhosted(surface);
    }

    private void RegisterChatOwner(ChatViewModel surface, Guid chatId)
    {
        if (_sessionsByChatId.TryGetValue(chatId, out var existing)
            && !ReferenceEquals(existing, surface)
            && _hostCounts.GetValueOrDefault(existing) == 0
            && !existing.OwnsAnyLiveChat())
        {
            UntrackSurface(existing, dispose: true);
        }

        _sessionsByChatId[chatId] = surface;
    }

    private void RemoveChatOwner(ChatViewModel surface)
    {
        foreach (var chatId in _sessionsByChatId
                     .Where(pair => ReferenceEquals(pair.Value, surface))
                     .Select(pair => pair.Key)
                     .ToList())
            _sessionsByChatId.Remove(chatId);
    }

    private void CacheOrReleaseIfIdleAndUnhosted(ChatViewModel surface)
    {
        if (_isDisposed
            || !_surfaces.Contains(surface))
            return;

        if (_hostCounts.GetValueOrDefault(surface) > 0 || surface.OwnsAnyLiveChat())
        {
            RemoveFromIdleCache(surface);
            return;
        }

        if (CanCacheIdleSurface(surface))
        {
            AddToIdleCache(surface);
            TrimIdleCache();
            ShedDeepIdleRealizedControls();
            return;
        }

        UntrackSurface(surface, dispose: true);
    }

    private bool CanCacheIdleSurface(ChatViewModel surface)
        => _maxIdleCachedSurfaces > 0 && surface.CurrentChat is not null;

    private void AddToIdleCache(ChatViewModel surface)
    {
        RemoveFromIdleCache(surface);
        _idleSurfacesLru.AddLast(surface);
    }

    private void RemoveFromIdleCache(ChatViewModel surface)
    {
        var node = _idleSurfacesLru.Find(surface);
        if (node is not null)
            _idleSurfacesLru.Remove(node);
    }

    private void TrimIdleCache()
    {
        while (_idleSurfacesLru.Count > _maxIdleCachedSurfaces)
        {
            var surface = _idleSurfacesLru.First!.Value;
            _idleSurfacesLru.RemoveFirst();

            if (!_surfaces.Contains(surface)
                || _hostCounts.GetValueOrDefault(surface) > 0
                || surface.OwnsAnyLiveChat())
            {
                continue;
            }

            UntrackSurface(surface, dispose: true);
        }
    }

    // Release the realized transcript controls of idle-cached surfaces deeper than the most-recently
    // used ones. _idleSurfacesLru is ordered oldest-first (AddToIdleCache appends the newest at the
    // tail), so the newest MaxRealizedIdleSurfaces surfaces stay fully realized for instant
    // switch-back and everything older is shed down to its view-models. Releasing an already-shed
    // surface is a cheap no-op, so re-running this on every cache insert is safe.
    private void ShedDeepIdleRealizedControls()
    {
        var shedCount = _idleSurfacesLru.Count - MaxRealizedIdleSurfaces;
        if (shedCount <= 0)
            return;

        var node = _idleSurfacesLru.First;
        for (var i = 0; i < shedCount && node is not null; i++)
        {
            node.Value.ReleaseRealizedTranscriptControls();
            node = node.Next;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(ChatSessionStore));
    }
}
