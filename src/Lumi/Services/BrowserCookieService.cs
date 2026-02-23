using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;

namespace Lumi.Services;

/// <summary>
/// Detects installed Chromium-based browser profiles and imports cookies into WebView2.
/// Supports Google Chrome and Microsoft Edge on Windows.
/// </summary>
public sealed class BrowserCookieService
{
    public record BrowserInfo(string Name, string UserDataPath, string IconGlyph);
    public record BrowserProfile(string Name, string Path, BrowserInfo Browser);

    private static readonly BrowserInfo[] KnownBrowsers = GetKnownBrowsers();

    private static BrowserInfo[] GetKnownBrowsers()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return [];

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return
        [
            new("Google Chrome", Path.Combine(local, "Google", "Chrome", "User Data"), "🌐"),
            new("Microsoft Edge", Path.Combine(local, "Microsoft", "Edge", "User Data"), "🔵"),
            new("Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"), "🦁"),
            new("Vivaldi", Path.Combine(local, "Vivaldi", "User Data"), "🔴"),
            new("Opera", Path.Combine(local, "Opera Software", "Opera Stable"), "🔴"),
        ];
    }

    /// <summary>Returns browsers that are installed on this machine.</summary>
    public static List<BrowserInfo> GetInstalledBrowsers()
    {
        return KnownBrowsers
            .Where(b => Directory.Exists(b.UserDataPath))
            .ToList();
    }

    /// <summary>Returns profiles for a given browser.</summary>
    public static List<BrowserProfile> GetProfiles(BrowserInfo browser)
    {
        var profiles = new List<BrowserProfile>();
        if (!Directory.Exists(browser.UserDataPath))
            return profiles;

        // Default profile
        var defaultCookies = FindCookieFile(Path.Combine(browser.UserDataPath, "Default"));
        if (defaultCookies is not null)
        {
            var displayName = ReadProfileName(Path.Combine(browser.UserDataPath, "Default")) ?? "Default";
            profiles.Add(new BrowserProfile(displayName, "Default", browser));
        }

        // Numbered profiles (Profile 1, Profile 2, etc.)
        try
        {
            foreach (var dir in Directory.GetDirectories(browser.UserDataPath, "Profile *"))
            {
                var cookieFile = FindCookieFile(dir);
                if (cookieFile is not null)
                {
                    var folderName = Path.GetFileName(dir);
                    var displayName = ReadProfileName(dir) ?? folderName;
                    profiles.Add(new BrowserProfile(displayName, folderName, browser));
                }
            }
        }
        catch { /* permission issues */ }

        return profiles;
    }

    private static string? FindCookieFile(string profileDir)
    {
        // Chrome 96+ stores cookies under Network/Cookies
        var networkCookies = Path.Combine(profileDir, "Network", "Cookies");
        if (File.Exists(networkCookies))
            return networkCookies;

        // Older versions store directly in profile
        var directCookies = Path.Combine(profileDir, "Cookies");
        if (File.Exists(directCookies))
            return directCookies;

        return null;
    }

    private static string? ReadProfileName(string profileDir)
    {
        var prefsFile = Path.Combine(profileDir, "Preferences");
        if (!File.Exists(prefsFile))
            return null;

        try
        {
            var json = File.ReadAllText(prefsFile);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("profile", out var profile)
                && profile.TryGetProperty("name", out var name))
            {
                return name.GetString();
            }
        }
        catch { /* corrupt prefs */ }

        return null;
    }

    /// <summary>
    /// Import cookies from the selected browser profile into WebView2.
    /// Returns the number of cookies imported.
    /// </summary>
    public static async Task<int> ImportCookiesAsync(BrowserProfile profile, CoreWebView2 webView)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return 0;

        var profileDir = Path.Combine(profile.Browser.UserDataPath, profile.Path);
        var cookieFile = FindCookieFile(profileDir);
        if (cookieFile is null)
            return 0;

        // Try direct SQLite decryption first (works for v10 cookies — Chrome, older Edge)
        var cookies = await TryDirectDecryptAsync(cookieFile, profile);

        // If direct decryption yielded nothing, use CDP to extract cookies via the browser itself
        // (required for v20 App-Bound Encryption in newer Edge/Chrome)
        if (cookies.Count == 0)
            cookies = await ExtractCookiesViaCdpAsync(profile);

        if (cookies.Count == 0)
            return 0;

        return ImportCookieRecords(cookies, webView);
    }

    /// <summary>Imports a list of cookie records into a WebView2 instance.</summary>
    private static int ImportCookieRecords(List<CookieRecord> cookies, CoreWebView2 webView)
    {
        var manager = webView.CookieManager;
        var count = 0;

        foreach (var c in cookies)
        {
            try
            {
                var cookie = manager.CreateCookie(c.Name, c.Value, c.Host, c.Path);
                cookie.IsSecure = c.IsSecure;
                cookie.IsHttpOnly = c.IsHttpOnly;
                cookie.SameSite = c.SameSite switch
                {
                    -1 => CoreWebView2CookieSameSiteKind.None,
                    0 => CoreWebView2CookieSameSiteKind.None,
                    1 => CoreWebView2CookieSameSiteKind.Lax,
                    2 => CoreWebView2CookieSameSiteKind.Strict,
                    _ => CoreWebView2CookieSameSiteKind.Lax
                };

                if (c.ExpiresUtc > DateTime.UtcNow)
                    cookie.Expires = c.ExpiresUtc.ToLocalTime();

                manager.AddOrUpdateCookie(cookie);
                count++;
            }
            catch { /* skip invalid cookies */ }
        }

        return count;
    }

    /// <summary>
    /// Tries to decrypt cookies directly from the SQLite database using DPAPI + AES-GCM.
    /// Works for v10 encryption (Chrome, older Edge). Returns empty list for v20 (App-Bound).
    /// </summary>
    private static async Task<List<CookieRecord>> TryDirectDecryptAsync(string cookieFile, BrowserProfile profile)
    {
        var masterKey = ReadMasterKey(profile.Browser.UserDataPath);
        var tempFile = Path.Combine(Path.GetTempPath(), $"lumi_cookies_{Guid.NewGuid():N}.db");

        try
        {
            await CopyLockedFileAsync(cookieFile, tempFile, profile.Browser.Name);
            return ReadCookiesFromDb(tempFile, masterKey);
        }
        catch
        {
            return [];
        }
        finally
        {
            try { File.Delete(tempFile); }
            catch { /* cleanup best-effort */ }
        }
    }

    /// <summary>
    /// Extracts cookies from a browser profile by launching the browser headlessly
    /// and using Chrome DevTools Protocol (CDP). This handles v20 App-Bound Encryption
    /// that can't be decrypted externally.
    /// </summary>
    private static async Task<List<CookieRecord>> ExtractCookiesViaCdpAsync(BrowserProfile profile)
    {
        var exePath = FindBrowserExe(profile.Browser);
        if (exePath is null)
            return [];

        // Kill background browser processes so we can open the profile
        KillBackgroundBrowserProcesses(profile.Browser.Name);
        await Task.Delay(500);

        var port = Random.Shared.Next(10000, 60000);
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            ArgumentList =
            {
                "--headless=new",
                "--disable-gpu",
                $"--remote-debugging-port={port}",
                "--no-first-run",
                "--no-default-browser-check",
                $"--user-data-dir={profile.Browser.UserDataPath}",
                $"--profile-directory={profile.Path}",
            },
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null) return [];

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            // Wait for CDP to be ready and get a page-level WebSocket URL
            // (Network.getAllCookies is only available on page targets, not browser-level)
            string? wsUrl = null;
            for (var i = 0; i < 50 && !cts.Token.IsCancellationRequested; i++)
            {
                try
                {
                    var json = await http.GetStringAsync($"http://127.0.0.1:{port}/json", cts.Token);
                    using var doc = JsonDocument.Parse(json);
                    foreach (var target in doc.RootElement.EnumerateArray())
                    {
                        if (target.TryGetProperty("type", out var type) && type.GetString() == "page"
                            && target.TryGetProperty("webSocketDebuggerUrl", out var ws))
                        {
                            wsUrl = ws.GetString();
                            break;
                        }
                    }
                    if (wsUrl is not null) break;

                    // If no page target yet, try creating one
                    if (i == 10)
                    {
                        try { await http.GetStringAsync($"http://127.0.0.1:{port}/json/new?about:blank", cts.Token); }
                        catch { }
                    }
                }
                catch { /* not ready yet */ }
                await Task.Delay(200, cts.Token);
            }

            if (wsUrl is null) return [];

            // Connect via WebSocket and request all cookies
            using var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri(wsUrl), cts.Token);

            var request = """{"id":1,"method":"Network.getAllCookies"}"""u8;
            await client.SendAsync(request.ToArray(), WebSocketMessageType.Text, true, cts.Token);

            // Receive (response can be large)
            using var ms = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var result = await client.ReceiveAsync(buffer, cts.Token);
                ms.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) break;
            }

            var responseJson = Encoding.UTF8.GetString(ms.ToArray());
            return ParseCdpCookies(responseJson);
        }
        catch
        {
            return [];
        }
        finally
        {
            if (process is not null && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch { }
            }
        }
    }

    /// <summary>Parses cookies from a CDP Network.getAllCookies response.</summary>
    private static List<CookieRecord> ParseCdpCookies(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("result", out var result))
            return [];
        if (!result.TryGetProperty("cookies", out var cookiesArr))
            return [];

        var cookies = new List<CookieRecord>();
        foreach (var c in cookiesArr.EnumerateArray())
        {
            var name = c.GetProperty("name").GetString() ?? "";
            var value = c.GetProperty("value").GetString() ?? "";
            var domain = c.GetProperty("domain").GetString() ?? "";
            var path = c.TryGetProperty("path", out var p) ? p.GetString() ?? "/" : "/";
            var secure = c.TryGetProperty("secure", out var sec) && sec.GetBoolean();
            var httpOnly = c.TryGetProperty("httpOnly", out var ho) && ho.GetBoolean();
            var sameSite = c.TryGetProperty("sameSite", out var ss)
                ? ss.GetString() switch { "Strict" => 2, "Lax" => 1, _ => 0 }
                : 0;
            var expires = c.TryGetProperty("expires", out var exp)
                && exp.TryGetDouble(out var expVal) && expVal > 0
                ? DateTimeOffset.FromUnixTimeSeconds((long)expVal).UtcDateTime
                : DateTime.MaxValue;

            if (!string.IsNullOrEmpty(name))
                cookies.Add(new CookieRecord(domain, name, value, path, secure, httpOnly, sameSite, expires));
        }

        return cookies;
    }

    /// <summary>Finds the browser executable path for a known browser.</summary>
    private static string? FindBrowserExe(BrowserInfo browser)
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        string[] candidates = browser.Name switch
        {
            "Microsoft Edge" =>
            [
                Path.Combine(pfx86, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"),
            ],
            "Google Chrome" =>
            [
                Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(pfx86, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"),
            ],
            "Brave" =>
            [
                Path.Combine(pf, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(pfx86, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(local, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
            ],
            "Vivaldi" =>
            [
                Path.Combine(local, "Vivaldi", "Application", "vivaldi.exe"),
                Path.Combine(pf, "Vivaldi", "Application", "vivaldi.exe"),
            ],
            "Opera" =>
            [
                Path.Combine(local, "Programs", "Opera", "opera.exe"),
                Path.Combine(pf, "Opera", "opera.exe"),
            ],
            _ => [],
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Kills background-only browser processes (no visible window).</summary>
    private static void KillBackgroundBrowserProcesses(string browserName)
    {
        var processName = browserName switch
        {
            "Google Chrome" => "chrome",
            "Microsoft Edge" => "msedge",
            "Brave" => "brave",
            "Vivaldi" => "vivaldi",
            "Opera" => "opera",
            _ => null,
        };

        if (processName is null) return;

        foreach (var p in Process.GetProcessesByName(processName))
        {
            if (p.MainWindowHandle == IntPtr.Zero)
            {
                try { p.Kill(); }
                catch { /* may have already exited */ }
            }
        }
    }

    /// <summary>
    /// Copies a file that may be locked by a browser. First tries a normal copy;
    /// if that fails, kills background-only browser processes and retries.
    /// </summary>
    private static async Task CopyLockedFileAsync(string sourceFile, string destFile, string browserName)
    {
        try
        {
            await CopyWithSharingAsync(sourceFile, destFile);
            return;
        }
        catch (IOException) { /* file locked — fall through to kill background processes */ }

        KillBackgroundBrowserProcesses(browserName);
        await Task.Delay(500);

        await CopyWithSharingAsync(sourceFile, destFile);
    }

    private static async Task CopyWithSharingAsync(string source, string dest)
    {
        using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dst);
    }

    private record CookieRecord(
        string Host, string Name, string Value, string Path,
        bool IsSecure, bool IsHttpOnly, int SameSite, DateTime ExpiresUtc);

    private static List<CookieRecord> ReadCookiesFromDb(string dbPath, byte[]? masterKey)
    {
        var cookies = new List<CookieRecord>();

        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT host_key, name, encrypted_value, path,
                   is_secure, is_httponly, samesite,
                   expires_utc
            FROM cookies
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var host = reader.GetString(0);
            var name = reader.GetString(1);
            var rawValue = reader.GetValue(2);
            var path = reader.GetString(3);
            var isSecure = reader.GetInt64(4) != 0;
            var isHttpOnly = reader.GetInt64(5) != 0;
            var sameSite = (int)reader.GetInt64(6);
            var expiresChrome = reader.GetInt64(7);

            // encrypted_value can be a BLOB (byte[]) or a string (empty/unencrypted)
            string? value;
            if (rawValue is byte[] encryptedValue && encryptedValue.Length > 0)
                value = DecryptCookieValue(encryptedValue, masterKey);
            else if (rawValue is string s)
                value = s;
            else
                continue;
            if (value is null)
                continue;

            // Chrome timestamps are microseconds since 1601-01-01
            var expiresUtc = expiresChrome > 0
                ? DateTime.FromFileTimeUtc(expiresChrome * 10)
                : DateTime.MaxValue;

            cookies.Add(new CookieRecord(host, name, value, path,
                isSecure, isHttpOnly, sameSite, expiresUtc));
        }

        return cookies;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[]? ReadMasterKey(string userDataPath)
    {
        var localStatePath = Path.Combine(userDataPath, "Local State");
        if (!File.Exists(localStatePath))
            return null;

        try
        {
            var json = File.ReadAllText(localStatePath);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt))
                return null;
            if (!osCrypt.TryGetProperty("encrypted_key", out var keyProp))
                return null;

            var keyBase64 = keyProp.GetString();
            if (keyBase64 is null)
                return null;

            var keyBytes = Convert.FromBase64String(keyBase64);

            // Strip the "DPAPI" prefix (5 bytes)
            if (keyBytes.Length > 5
                && Encoding.ASCII.GetString(keyBytes, 0, 5) == "DPAPI")
            {
                keyBytes = keyBytes[5..];
            }

            return ProtectedData.Unprotect(keyBytes, null, DataProtectionScope.CurrentUser);
        }
        catch
        {
            return null;
        }
    }

    private static string? DecryptCookieValue(byte[] encrypted, byte[]? masterKey)
    {
        if (encrypted.Length == 0)
            return "";

        // v10/v20 encrypted cookies (Chrome 80+)
        if (encrypted.Length > 3
            && encrypted[0] == 'v' && encrypted[1] == '1' && encrypted[2] == '0'
            && masterKey is not null)
        {
            return DecryptAesGcm(encrypted, masterKey);
        }

        if (encrypted.Length > 3
            && encrypted[0] == 'v' && encrypted[1] == '2' && encrypted[2] == '0'
            && masterKey is not null)
        {
            return DecryptAesGcm(encrypted, masterKey);
        }

        // Older DPAPI-encrypted cookies
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return DecryptDpapi(encrypted);
        }

        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? DecryptDpapi(byte[] encrypted)
    {
        try
        {
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return null;
        }
    }

    private static string? DecryptAesGcm(byte[] encrypted, byte[] masterKey)
    {
        try
        {
            // Format: "v10" (3 bytes) + nonce (12 bytes) + ciphertext+tag
            const int prefixLen = 3;
            const int nonceLen = 12;
            const int tagLen = 16;

            if (encrypted.Length < prefixLen + nonceLen + tagLen)
                return null;

            var nonce = encrypted.AsSpan(prefixLen, nonceLen);
            var ciphertextWithTag = encrypted.AsSpan(prefixLen + nonceLen);

            if (ciphertextWithTag.Length < tagLen)
                return null;

            var ciphertext = ciphertextWithTag[..^tagLen];
            var tag = ciphertextWithTag[^tagLen..];

            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(masterKey, tagLen);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return Encoding.UTF8.GetString(plaintext);
        }
        catch
        {
            return null;
        }
    }
}
