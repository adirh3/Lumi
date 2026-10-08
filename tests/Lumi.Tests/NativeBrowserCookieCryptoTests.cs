using System.Security.Cryptography;
using System.Text;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class NativeBrowserCookieCryptoTests
{
    [Theory]
    [InlineData("Google Chrome", "Chrome", "chrome")]
    [InlineData("Microsoft Edge", "Microsoft Edge", "microsoft-edge")]
    [InlineData("Brave", "Brave", "brave")]
    [InlineData("Vivaldi", "Vivaldi", "vivaldi")]
    [InlineData("Chromium", "Chromium", "chromium")]
    public void SecretStoreNames_MatchTheBrowserNotAnAssumedChromeIdentity(
        string browser, string macProduct, string linuxApplication)
    {
        Assert.Equal(macProduct, NativeBrowserCookieCrypto.GetMacKeychainProduct(browser));
        Assert.Equal(linuxApplication, NativeBrowserCookieCrypto.GetLinuxKeyringApplication(browser));
    }

    [Fact]
    public void SecretStoreNames_DoNotGuessForUnsupportedBrowsers()
    {
        Assert.Null(NativeBrowserCookieCrypto.GetMacKeychainProduct("Firefox"));
        Assert.Null(NativeBrowserCookieCrypto.GetLinuxKeyringApplication("Firefox"));
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 1003)]
    public void SafeStorageKey_UsesThePlatformPbkdf2Iterations(bool isLinux, int iterations)
    {
        var expected = Rfc2898DeriveBytes.Pbkdf2(
            "synthetic-safe-storage"u8, "saltysalt"u8, iterations, HashAlgorithmName.SHA1, 16);

        Assert.Equal(expected, NativeBrowserCookieCrypto.DeriveSafeStorageKey("synthetic-safe-storage", isLinux));
    }

    [Fact]
    public void LinuxV10_UsesTheKnownPeanutsKeyEvenWhenAKeyringKeyIsAvailable()
    {
        var legacyKey = Convert.FromHexString("FD621FE5A2B402539DFA147CA9272778");
        Assert.Equal(legacyKey, NativeBrowserCookieCrypto.DeriveSafeStorageKey("peanuts", isLinux: true));
        var unrelatedKey = NativeBrowserCookieCrypto.DeriveSafeStorageKey("synthetic-keyring", isLinux: true);
        var encrypted = Encrypt("v10", legacyKey, "legacy-session"u8.ToArray());

        Assert.Equal("legacy-session", NativeBrowserCookieCrypto.DecryptValue(
            encrypted, ".example.com", 23, isLinux: true, unrelatedKey));
        Assert.Equal("legacy-session", NativeBrowserCookieCrypto.DecryptValue(
            encrypted, ".example.com", 23, isLinux: true, safeStorageKey: null));
    }

    [Theory]
    [InlineData(true, "v11")]
    [InlineData(false, "v10")]
    public void SafeStorageCookies_RequireTheirOwnKeyAndNeverFallBackToPeanuts(bool isLinux, string prefix)
    {
        const string host = ".example.com";
        var key = NativeBrowserCookieCrypto.DeriveSafeStorageKey("synthetic-safe-storage", isLinux);
        var encrypted = Encrypt(prefix, key, HostBoundValue(host, "signed-in"));

        Assert.Throws<InvalidOperationException>(() => NativeBrowserCookieCrypto.DecryptValue(
            encrypted, host, 24, isLinux, safeStorageKey: null));
        Assert.Equal("signed-in", NativeBrowserCookieCrypto.DecryptValue(encrypted, host, 24, isLinux, key));

        var wrongKey = NativeBrowserCookieCrypto.DeriveSafeStorageKey("peanuts", isLinux: true);
        Assert.Throws<CryptographicException>(() => NativeBrowserCookieCrypto.DecryptValue(
            encrypted, host, 24, isLinux, wrongKey));
    }

    [Fact]
    public void SchemaBefore24_DoesNotStripALongCookieValue()
    {
        var value = new string('a', 64) + "-tail";
        var key = NativeBrowserCookieCrypto.DeriveSafeStorageKey("peanuts", isLinux: true);
        var encrypted = Encrypt("v10", key, Encoding.UTF8.GetBytes(value));

        Assert.Equal(value, NativeBrowserCookieCrypto.DecryptValue(
            encrypted, ".example.com", 23, isLinux: true, safeStorageKey: null));
        Assert.Throws<CryptographicException>(() => NativeBrowserCookieCrypto.DecryptValue(
            encrypted, ".example.com", 24, isLinux: true, safeStorageKey: null));
    }

    [Fact]
    public void Schema24_VerifiesTheExactHostIncludingItsLeadingDot()
    {
        const string host = ".example.com";
        var key = NativeBrowserCookieCrypto.DeriveSafeStorageKey("peanuts", isLinux: true);
        var encrypted = Encrypt("v10", key, HostBoundValue(host, ""));

        Assert.Equal("", NativeBrowserCookieCrypto.DecryptValue(
            encrypted, host, 24, isLinux: true, safeStorageKey: null));
        Assert.Throws<CryptographicException>(() => NativeBrowserCookieCrypto.DecryptValue(
            encrypted, "example.com", 24, isLinux: true, safeStorageKey: null));

        var missingHash = Encrypt("v10", key, "short"u8.ToArray());
        Assert.Throws<CryptographicException>(() => NativeBrowserCookieCrypto.DecryptValue(
            missingHash, host, 24, isLinux: true, safeStorageKey: null));
    }

    [Theory]
    [InlineData("v20")]
    [InlineData("v99")]
    public void UnsupportedEncryption_IsAnExplicitFailure(string prefix)
    {
        byte[] encrypted = [.. Encoding.ASCII.GetBytes(prefix), .. new byte[16]];

        Assert.Throws<NotSupportedException>(() => NativeBrowserCookieCrypto.DecryptValue(
            encrypted, ".example.com", 24, isLinux: true, safeStorageKey: null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(18)]
    public void TruncatedOrUnalignedCiphertext_IsAnExplicitFailure(int length)
    {
        var encrypted = new byte[length];
        if (length >= 3)
            "v10"u8.CopyTo(encrypted);

        Assert.Throws<CryptographicException>(() => NativeBrowserCookieCrypto.DecryptValue(
            encrypted, ".example.com", 24, isLinux: true, safeStorageKey: null));
    }

    internal static byte[] HostBoundValue(string host, string value) =>
        [.. SHA256.HashData(Encoding.UTF8.GetBytes(host)), .. Encoding.UTF8.GetBytes(value)];

    internal static byte[] Encrypt(string prefix, byte[] key, byte[] plaintext)
    {
        Span<byte> iv = stackalloc byte[16];
        iv.Fill((byte)' ');
        using var aes = Aes.Create();
        aes.Key = key;
        return [.. Encoding.ASCII.GetBytes(prefix), .. aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7)];
    }
}
