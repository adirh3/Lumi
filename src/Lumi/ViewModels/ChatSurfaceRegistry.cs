using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace Lumi.ViewModels;

public sealed class ChatSurfaceRegistry : IDisposable
{
    private readonly object _sync = new();
    private readonly HashSet<ChatViewModel> _surfaces = [];
    private readonly Dictionary<Guid, ChatViewModel> _ownersByChatId = [];
    private bool _isDisposed;

    public void Attach(ChatViewModel surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_surfaces.Add(surface))
                return;

            surface.PropertyChanged += OnSurfacePropertyChanged;
            if (surface.CurrentChat is { } chat)
                _ownersByChatId[chat.Id] = surface;
        }
    }

    public void Detach(ChatViewModel surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        lock (_sync)
        {
            if (!_surfaces.Remove(surface))
                return;

            surface.PropertyChanged -= OnSurfacePropertyChanged;
            RemoveSurfaceOwners(surface);
        }
    }

    public bool TryGetOwner(Guid chatId, out ChatViewModel surface)
    {
        lock (_sync)
            return _ownersByChatId.TryGetValue(chatId, out surface!);
    }

    public bool HasOwner(Guid chatId)
    {
        lock (_sync)
            return _ownersByChatId.ContainsKey(chatId);
    }

    public IReadOnlyList<ChatViewModel> SnapshotSurfaces()
    {
        lock (_sync)
            return _surfaces.ToList();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            foreach (var surface in _surfaces)
                surface.PropertyChanged -= OnSurfacePropertyChanged;
            _surfaces.Clear();
            _ownersByChatId.Clear();
        }
    }

    private void OnSurfacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChatViewModel.CurrentChat) || sender is not ChatViewModel surface)
            return;

        lock (_sync)
        {
            if (!_surfaces.Contains(surface))
                return;

            RemoveSurfaceOwners(surface);
            if (surface.CurrentChat is { } chat)
                _ownersByChatId[chat.Id] = surface;
        }
    }

    private void RemoveSurfaceOwners(ChatViewModel surface)
    {
        foreach (var chatId in _ownersByChatId
                     .Where(kvp => ReferenceEquals(kvp.Value, surface))
                     .Select(static kvp => kvp.Key)
                     .ToList())
        {
            _ownersByChatId.Remove(chatId);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(ChatSurfaceRegistry));
    }
}
