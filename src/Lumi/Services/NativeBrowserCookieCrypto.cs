using System;
using System.Security.Cryptography;
using System.Text;
using Lumi.Localization;

namespace Lumi.Services;

internal static class NativeBrowserCookieCrypto
{
    private static readonly byte[] LegacyLinuxKey = DeriveSafeStorageKey("peanuts", isLinux: true);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string? GetMacKeychainProduct(string browserName) => browserName switch
    {
        "Google Chrome" => "Chrome",
        "Microsoft Edge" => "Microsoft Edge",
        "Brave" => "Brave",
        "Vivaldi" => "Vivaldi",
        "Chromium" => "Chromium",
        _ => null,
    };

    internal static string? GetLinuxKeyringApplication(string browserName) => browserName switch
    {
        "Google Chrome" => "chrome",
        "Microsoft Edge" => "microsoft-edge",
        "Brave" => "brave",
        "Vivaldi" => "vivaldi",
        "Chromium" => "chromium",
        _ => null,
    };

    internal static byte[] DeriveSafeStorageKey(string password, bool isLinux)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes, "saltysalt"u8, isLinux ? 1 : 1003, HashAlgorithmName.SHA1, 16);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    internal static bool RequiresSafeStorageKey(ReadOnlySpan<byte> encrypted, bool isLinux)
    {
        if (encrypted.Length < 3)
            throw new CryptographicException(Loc.Cookies_EncryptedTruncated);

        if (encrypted.StartsWith("v10"u8))
            return !isLinux;
        if (encrypted.StartsWith("v11"u8))
            return true;
        if (encrypted.StartsWith("v20"u8))
            throw new NotSupportedException(
                Loc.Cookies_AppBoundUnsupported);

        throw new NotSupportedException(
            Loc.Cookies_EncryptionUnsupported);
    }

    internal static string DecryptValue(
        ReadOnlySpan<byte> encrypted, string hostKey, int schemaVersion,
        bool isLinux, byte[]? safeStorageKey)
    {
        var requiresSafeStorage = RequiresSafeStorageKey(encrypted, isLinux);
        var key = requiresSafeStorage
            ? safeStorageKey ?? throw new InvalidOperationException(
                Loc.Cookies_StorageKeyRequired)
            : LegacyLinuxKey;

        var ciphertext = encrypted[3..];
        if (ciphertext.IsEmpty || ciphertext.Length % 16 != 0)
            throw new CryptographicException(Loc.Cookies_EncryptedLengthInvalid);

        Span<byte> iv = stackalloc byte[16];
        iv.Fill((byte)' ');

        byte[] plaintext;
        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            plaintext = aes.DecryptCbc(ciphertext, iv, PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            throw new CryptographicException(
                Loc.Cookies_DecryptFailed);
        }

        try
        {
            ReadOnlySpan<byte> value = plaintext;
            if (schemaVersion >= 24)
            {
                var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(hostKey));
                if (value.Length < expectedHash.Length
                    || !CryptographicOperations.FixedTimeEquals(value[..expectedHash.Length], expectedHash))
                {
                    throw new CryptographicException(
                        Loc.Cookies_HostVerificationFailed);
                }

                value = value[expectedHash.Length..];
            }

            try
            {
                return StrictUtf8.GetString(value);
            }
            catch (DecoderFallbackException)
            {
                throw new CryptographicException(Loc.Cookies_Utf8Invalid);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
