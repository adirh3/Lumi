#if !WINDOWS
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Lumi.Services;

/// <summary>Reads Chromium profiles for import through the native browser's cookie manager.</summary>
public sealed class BrowserCookieService
{
    public record BrowserInfo(string Name, string UserDataPath, string IconGlyph);
    public record BrowserProfile(string Name, string Path, BrowserInfo Browser);

    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly TimeSpan SecretLookupTimeout = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> GoogleAuthCookieNames =
    [
        "SID", "HSID", "SSID", "APISID", "SAPISID", "LSID",
        "__Secure-1PSID", "__Secure-3PSID", "__Secure-1PSIDTS", "__Secure-3PSIDTS", "__Host-GAPS",
    ];

    public static List<BrowserInfo> GetInstalledBrowsers()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return [];

        return GetKnownBrowsers(
                OperatingSystem.IsLinux(),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"))
            .Where(browser => Directory.Exists(browser.UserDataPath))
            .ToList();
    }

    internal static BrowserInfo[] GetKnownBrowsers(bool isLinux, string home, string? configHome)
    {
        if (string.IsNullOrWhiteSpace(home))
            return [];

        if (!isLinux)
        {
            var support = Path.Combine(home, "Library", "Application Support");
            return
            [
                new("Google Chrome", Path.Combine(support, "Google", "Chrome"), "🌐"),
                new("Microsoft Edge", Path.Combine(support, "Microsoft Edge"), "🔵"),
                new("Brave", Path.Combine(support, "BraveSoftware", "Brave-Browser"), "🦁"),
                new("Vivaldi", Path.Combine(support, "Vivaldi"), "🔴"),
                new("Chromium", Path.Combine(support, "Chromium"), "🌐"),
            ];
        }

        // XDG_CONFIG_HOME must be absolute; a relative value is not a valid XDG override.
        var config = !string.IsNullOrWhiteSpace(configHome) && Path.IsPathFullyQualified(configHome)
            ? configHome
            : Path.Combine(home, ".config");
        return
        [
            new("Google Chrome", Path.Combine(config, "google-chrome"), "🌐"),
            new("Microsoft Edge", Path.Combine(config, "microsoft-edge"), "🔵"),
            new("Brave", Path.Combine(config, "BraveSoftware", "Brave-Browser"), "🦁"),
            new("Vivaldi", Path.Combine(config, "vivaldi"), "🔴"),
            new("Chromium", Path.Combine(config, "chromium"), "🌐"),
        ];
    }

    public static List<BrowserProfile> GetProfiles(BrowserInfo browser)
    {
        var profiles = new List<BrowserProfile>();
        if (!Directory.Exists(browser.UserDataPath))
            return profiles;

        AddProfile(Path.Combine(browser.UserDataPath, "Default"));
        try
        {
            foreach (var directory in Directory.GetDirectories(browser.UserDataPath, "Profile *")
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                AddProfile(directory);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[BrowserCookies] Cannot enumerate browser profiles ({error.GetType().Name}).");
        }

        return profiles;

        void AddProfile(string directory)
        {
            if (FindCookieFile(directory) is null)
                return;

            var folder = Path.GetFileName(directory);
            profiles.Add(new BrowserProfile(ReadProfileName(directory) ?? folder, folder, browser));
        }
    }

    public static int GetGoogleSessionCookieScore(BrowserProfile profile)
    {
        var cookieFile = FindCookieFile(Path.Combine(profile.Browser.UserDataPath, profile.Path));
        if (cookieFile is null)
            return 0;

        try
        {
            return ReadStagedDatabase(cookieFile, DataStore.AppDirectory, CountGoogleSessionCookies);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                      or SqliteException or InvalidOperationException)
        {
            Debug.WriteLine($"[BrowserCookies] Cannot score browser cookies ({error.GetType().Name}).");
            return 0;
        }
    }

    /// <summary>
    /// Returns decrypted cookies without changing the source profile or the embedded browser.
    /// Unavailable Safe Storage keys and unsupported encryption fail the import, not individual cookies.
    /// </summary>
    public static Task<IReadOnlyList<Cookie>> ReadCookiesAsync(BrowserProfile profile) => Task.Run(() =>
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Native browser cookie import requires Linux or macOS.");

        var cookieFile = FindCookieFile(Path.Combine(profile.Browser.UserDataPath, profile.Path))
            ?? throw new FileNotFoundException("The selected browser profile has no readable cookie database.");

        try
        {
            return ReadStagedDatabase(
                cookieFile, DataStore.AppDirectory,
                connection => ReadCookiesFromDatabase(
                    connection, OperatingSystem.IsLinux(),
                    () => ReadSafeStorageKey(profile.Browser.Name, OperatingSystem.IsLinux())));
        }
        catch (SqliteException error)
        {
            Debug.WriteLine($"[BrowserCookies] Cannot read cookie database (SQLite {error.SqliteErrorCode}).");
            throw new InvalidOperationException(
                "Cannot read the browser cookie database. Close the source browser and retry.", error);
        }
        catch (Exception error)
        {
            // Exception messages from cookie validation or secret-store tools can contain secrets.
            Debug.WriteLine($"[BrowserCookies] Native cookie import failed ({error.GetType().Name}).");
            throw;
        }
    });

    private static string? FindCookieFile(string directory)
    {
        var networkCookies = Path.Combine(directory, "Network", "Cookies");
        if (File.Exists(networkCookies))
            return networkCookies;

        var directCookies = Path.Combine(directory, "Cookies");
        return File.Exists(directCookies) ? directCookies : null;
    }

    private static string? ReadProfileName(string directory)
    {
        var preferences = Path.Combine(directory, "Preferences");
        if (!File.Exists(preferences))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(preferences));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("profile", out var profile)
                && profile.ValueKind == JsonValueKind.Object
                && profile.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String)
            {
                return name.GetString();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[BrowserCookies] Cannot read profile display name ({error.GetType().Name}).");
        }

        return null;
    }

    internal static T ReadStagedDatabase<T>(
        string sourcePath, string appDirectory, Func<SqliteConnection, T> read)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Native cookie snapshots require a non-Windows platform.");

        var directory = Path.Combine(appDirectory, $"native-cookie-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory, PrivateDirectoryMode);
        try
        {
            var snapshot = Path.Combine(directory, "Cookies");
            using (new FileStream(snapshot, new FileStreamOptions
                   {
                       Mode = FileMode.CreateNew,
                       Access = FileAccess.ReadWrite,
                       Share = FileShare.None,
                       UnixCreateMode = PrivateFileMode,
                   }))
            {
            }

            // SQLite's backup API includes committed WAL data and never requires a raw,
            // potentially inconsistent copy of a live Chromium database.
            using (var source = CreateConnection(sourcePath, SqliteOpenMode.ReadOnly))
            using (var destination = CreateConnection(snapshot, SqliteOpenMode.ReadWrite))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
                using var command = destination.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=DELETE";
                command.ExecuteScalar();
            }

            using var connection = CreateConnection(snapshot, SqliteOpenMode.ReadOnly);
            connection.Open();
            return read(connection);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[BrowserCookies] Private cookie snapshot cleanup failed ({error.GetType().Name}).");
                throw new IOException(
                    "Could not remove the private cookie snapshot from Lumi's application directory.", error);
            }
        }
    }

    private static SqliteConnection CreateConnection(string path, SqliteOpenMode mode) => new(
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());

    internal static int CountGoogleSessionCookies(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT name FROM cookies
            WHERE host_key LIKE '%google.%'
              AND (has_expires = 0 OR is_persistent = 0 OR expires_utc > $now)
            """;
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToFileTimeUtc() / 10);
        using var reader = command.ExecuteReader();
        var score = 0;
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (GoogleAuthCookieNames.Contains(name))
                score += 10;
            else if (name.Contains("SID", StringComparison.OrdinalIgnoreCase))
                score += 2;
        }

        return score;
    }

    internal static IReadOnlyList<Cookie> ReadCookiesFromDatabase(
        SqliteConnection connection, bool isLinux, Func<byte[]> readSafeStorageKey)
    {
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT value FROM meta WHERE key = 'version'";
        if (!int.TryParse(
                Convert.ToString(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture),
                NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
        {
            throw new InvalidDataException("The Chromium cookie database has no valid schema version.");
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT host_key, name, value, encrypted_value, path, expires_utc,
                   is_secure, is_httponly, has_expires, is_persistent
            FROM cookies
            """;
        using var reader = command.ExecuteReader();
        var cookies = new List<Cookie>();
        byte[]? safeStorageKey = null;
        try
        {
            while (reader.Read())
            {
                var persistent = reader.GetInt64(8) != 0 && reader.GetInt64(9) != 0;
                var expires = DateTime.MinValue;
                if (persistent)
                {
                    try
                    {
                        expires = DateTime.FromFileTimeUtc(checked(reader.GetInt64(5) * 10));
                    }
                    catch (Exception error) when (error is ArgumentOutOfRangeException or OverflowException)
                    {
                        throw new InvalidDataException("The Chromium cookie has an invalid expiry timestamp.");
                    }

                    if (expires <= DateTime.UtcNow)
                        continue;
                }

                var host = reader.GetString(0);
                var encrypted = reader.IsDBNull(3) ? [] : (byte[])reader.GetValue(3);
                var value = reader.IsDBNull(2) ? "" : reader.GetString(2);
                if (encrypted.Length > 0)
                {
                    if (NativeBrowserCookieCrypto.RequiresSafeStorageKey(encrypted, isLinux))
                        safeStorageKey ??= readSafeStorageKey();
                    value = NativeBrowserCookieCrypto.DecryptValue(
                        encrypted, host, version, isLinux, safeStorageKey);
                }

                try
                {
                    cookies.Add(new Cookie(reader.GetString(1), value, reader.GetString(4), host)
                    {
                        Secure = reader.GetInt64(6) != 0,
                        HttpOnly = reader.GetInt64(7) != 0,
                        Expires = expires,
                        Discard = !persistent,
                    });
                }
                catch (CookieException)
                {
                    throw new NotSupportedException(
                        "A Chromium cookie cannot be represented by the native browser cookie API. Import was not completed.");
                }
            }

            return cookies;
        }
        finally
        {
            if (safeStorageKey is not null)
                CryptographicOperations.ZeroMemory(safeStorageKey);
        }
    }

    private static byte[] ReadSafeStorageKey(string browserName, bool isLinux)
    {
        string password;
        if (isLinux)
        {
            var application = NativeBrowserCookieCrypto.GetLinuxKeyringApplication(browserName)
                ?? throw new NotSupportedException("This browser has no supported Linux Safe Storage lookup.");
            password = ReadSecret(
                "secret-tool", ["lookup", "application", application],
                "Cannot read this browser's libsecret Safe Storage key. Install secret-tool (libsecret), " +
                "unlock the desktop keyring, and retry. KWallet-only and other non-libsecret stores are not supported.");
        }
        else
        {
            var product = NativeBrowserCookieCrypto.GetMacKeychainProduct(browserName)
                ?? throw new NotSupportedException("This browser has no supported macOS Safe Storage lookup.");
            password = ReadSecret(
                "/usr/bin/security", ["find-generic-password", "-w", "-s", $"{product} Safe Storage", "-a", product],
                "Cannot read this browser's Safe Storage key from the macOS login keychain. " +
                "Unlock the keychain, allow access to the browser's Safe Storage item, and retry.");
        }

        return NativeBrowserCookieCrypto.DeriveSafeStorageKey(password, isLinux);
    }

    private static string ReadSecret(string executable, string[] arguments, string failureMessage)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException(failureMessage);
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException(failureMessage);
        }

        using var timeout = new CancellationTokenSource(SecretLookupTimeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            // Keep the deadline reachable even if a redirected pipe read has not yet cancelled.
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
                Debug.WriteLine("[BrowserCookies] Could not terminate a timed-out Safe Storage helper.");
            }

            throw new TimeoutException("Safe Storage lookup timed out after 30 seconds. " + failureMessage);
        }

        // Never expose stderr/stdout in an error: stdout is the encryption password.
        if (process.ExitCode != 0)
            throw new InvalidOperationException(failureMessage);
        var secret = stdout.GetAwaiter().GetResult().TrimEnd('\r', '\n');
        if (secret.Length == 0)
            throw new InvalidOperationException(failureMessage);

        return secret;
    }
}
#endif
