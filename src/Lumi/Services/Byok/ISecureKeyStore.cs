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
/// Implementations must be safe to call from any thread. Read operations never throw for
/// "key not found" — <see cref="GetAsync"/> returns <c>null</c> instead. Write and delete
/// operations <b>throw</b> on hardware/OS failure so callers can surface a clear error and
/// avoid reporting success while the store is inconsistent; the single exception is
/// <see cref="DeleteAsync"/> on a missing key, which is idempotent and succeeds.
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
    /// so a user clearing the password box reliably removes the OS entry. Throws on OS/hardware
    /// failure so a caller never reports success while the key was not persisted.
    /// </summary>
    Task SetAsync(string key, string? secret);

    /// <summary>
    /// Removes a secret if present. Deleting a missing key is idempotent and succeeds (no
    /// exception). Any other OS/hardware failure throws so a caller never reports a successful
    /// clear while the credential is orphaned in the store.
    /// </summary>
    Task DeleteAsync(string key);
}
