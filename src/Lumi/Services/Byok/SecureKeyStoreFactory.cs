using System;

namespace Lumi.Services.Byok;

/// <summary>
/// Resolves the platform-appropriate <see cref="ISecureKeyStore"/>. Native Keychain/Secret
/// Service tools report unsupported when unavailable; no encrypted-file fallback is used.
/// </summary>
internal static class SecureKeyStoreFactory
{
    private static readonly ISecureKeyStore _instance = OperatingSystem.IsWindows()
        ? new WindowsCredentialKeyStore()
        : new NativeCredentialKeyStore();

    /// <summary>The shared key store. Always non-null; check <see cref="ISecureKeyStore.IsSupported"/>.</summary>
    public static ISecureKeyStore Instance => _instance;

    /// <summary>
    /// Builds the stable OS-store key for a BYOK endpoint's API key. The endpoint <see cref="Models.ByokEndpoint.Id"/>
    /// is the stable identity, so the OS entry survives endpoint renames and never collides.
    /// </summary>
    public static string EndpointKey(string endpointId)
    {
        ArgumentNullException.ThrowIfNull(endpointId);
        return "Endpoint." + endpointId + ".ApiKey";
    }
}
