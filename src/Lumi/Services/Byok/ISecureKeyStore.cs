using System;
using System.Threading.Tasks;

namespace Lumi.Services.Byok;

/// <summary>
/// Operating-system backed credential store for BYOK API keys. Stores secrets outside the
/// app's <c>data.json</c> so nothing sensitive reaches disk. On Windows this is the
/// Credential Manager (<c>advapi32!CredWrite/CredRead/CredDelete</c>); other platforms return
/// <see cref="IsSupported"/> = <c>false</c> for now (the UI hides the mode there and the
/// runtime falls back to env vars).
/// </summary>
/// <remarks>
/// Implementations must be safe to call from any thread and must never throw for "key not
/// found" — they return <c>null</c> instead. Hardware/OS failures may throw; callers should
/// treat exceptions as "key unavailable" and surface a clear error.
/// </remarks>
public interface ISecureKeyStore
{
    /// <summary>
    /// <c>true</c> when this store is usable on the current platform. When <c>false</c> the
    /// BYOK UI must not offer the <c>CredentialStore</c> mode and the runtime resolves keys
    /// through the regular env-var / stored-key fallback chain.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>Reads a previously stored secret. Returns <c>null</c> when the key is absent.</summary>
    Task<string?> GetAsync(string key);

    /// <summary>
    /// Stores (or replaces) a secret. A <c>null</c>/<c>empty</c> value is equivalent to
    /// <see cref="DeleteAsync"/> — implementations MUST treat it as a delete, not a no-op,
    /// so a user clearing the password box reliably removes the OS entry.
    /// </summary>
    Task SetAsync(string key, string? secret);

    /// <summary>Removes a secret if present. No-op when the key is absent.</summary>
    Task DeleteAsync(string key);
}
