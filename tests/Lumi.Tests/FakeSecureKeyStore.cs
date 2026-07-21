using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumi.Services.Byok;

namespace Lumi.Tests;

/// <summary>
/// In-memory fake of <see cref="ISecureKeyStore"/> for tests. Reports <see cref="IsSupported"/>
/// = <c>true</c> by default so the CredentialStore code paths are exercised without touching the
/// real Windows Credential Manager.
/// </summary>
internal sealed class FakeSecureKeyStore : ISecureKeyStore
{
    private readonly Dictionary<string, string> _store = new(StringComparer.Ordinal);

    public bool IsSupported { get; set; } = true;

    public int SetCallCount { get; private set; }
    public int DeleteCallCount { get; private set; }
    public Exception? SetException { get; set; }

    public Task<string?> GetAsync(string key)
        => Task.FromResult(_store.TryGetValue(key, out var v) ? v : null);

    public Task SetAsync(string key, string? secret)
    {
        SetCallCount++;
        if (SetException is not null)
            return Task.FromException(SetException);
        if (string.IsNullOrEmpty(secret))
            _store.Remove(key);
        else
            _store[key] = secret;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key)
    {
        DeleteCallCount++;
        _store.Remove(key);
        return Task.CompletedTask;
    }

    /// <summary>Number of entries currently held in the fake store.</summary>
    public int Count => _store.Count;

    /// <summary>Direct peek for test assertions (bypasses the async API).</summary>
    public bool Contains(string key) => _store.ContainsKey(key);
}
