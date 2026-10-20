#if !WINDOWS
using Lumi.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lumi.Tests;

public sealed class NativeBrowserCookieReaderTests
{
    [Fact]
    public void BrowserPaths_UseXdgConfigOrMacApplicationSupport()
    {
        var home = Path.Combine(Path.GetTempPath(), "synthetic-home");
        var config = Path.Combine(home, "xdg-config");
        var linux = BrowserCookieService.GetKnownBrowsers(isLinux: true, home, config);
        Assert.Equal(Path.Combine(config, "google-chrome"), linux.Single(b => b.Name == "Google Chrome").UserDataPath);
        Assert.Equal(Path.Combine(config, "microsoft-edge"), linux.Single(b => b.Name == "Microsoft Edge").UserDataPath);
        Assert.Equal(Path.Combine(config, "BraveSoftware", "Brave-Browser"), linux.Single(b => b.Name == "Brave").UserDataPath);
        Assert.Equal(Path.Combine(home, ".config", "chromium"),
            BrowserCookieService.GetKnownBrowsers(isLinux: true, home, "relative")
                .Single(b => b.Name == "Chromium").UserDataPath);

        var mac = BrowserCookieService.GetKnownBrowsers(isLinux: false, home, configHome: null);
        Assert.Equal(Path.Combine(home, "Library", "Application Support", "Google", "Chrome"),
            mac.Single(b => b.Name == "Google Chrome").UserDataPath);
        Assert.Equal(Path.Combine(home, "Library", "Application Support", "Microsoft Edge"),
            mac.Single(b => b.Name == "Microsoft Edge").UserDataPath);
        Assert.All(linux, b => Assert.NotNull(NativeBrowserCookieCrypto.GetLinuxKeyringApplication(b.Name)));
        Assert.All(mac, b => Assert.NotNull(NativeBrowserCookieCrypto.GetMacKeychainProduct(b.Name)));
    }

    [Fact]
    public void Profiles_ReadDisplayNamesAndBothCookieLayoutsWithoutInspectingRealProfiles()
    {
        using var fixture = new CookieDatabaseFixture();
        File.WriteAllText(Path.Combine(fixture.UserDataPath, "Default", "Preferences"),
            """{"profile":{"name":"Personal"}}""");
        var numbered = Path.Combine(fixture.UserDataPath, "Profile 2");
        Directory.CreateDirectory(numbered);
        File.WriteAllBytes(Path.Combine(numbered, "Cookies"), []);
        File.WriteAllText(Path.Combine(numbered, "Preferences"), """{"profile":{"name":"Work"}}""");
        var corrupt = Path.Combine(fixture.UserDataPath, "Profile 1", "Network");
        Directory.CreateDirectory(corrupt);
        File.WriteAllBytes(Path.Combine(corrupt, "Cookies"), []);
        File.WriteAllText(Path.Combine(fixture.UserDataPath, "Profile 1", "Preferences"), "null");
        Directory.CreateDirectory(Path.Combine(fixture.UserDataPath, "Profile 3"));
        var systemProfile = Path.Combine(fixture.UserDataPath, "System Profile");
        Directory.CreateDirectory(systemProfile);
        File.WriteAllBytes(Path.Combine(systemProfile, "Cookies"), []);
        var browser = new BrowserCookieService.BrowserInfo("Google Chrome", fixture.UserDataPath, "🌐");

        var profiles = BrowserCookieService.GetProfiles(browser);

        Assert.Equal(["Personal", "Profile 1", "Work"], profiles.Select(p => p.Name).ToArray());
        Assert.Equal(["Default", "Profile 1", "Profile 2"], profiles.Select(p => p.Path).ToArray());
        Assert.All(profiles, profile => Assert.Same(browser, profile.Browser));
    }

    [SkippableFact]
    public void Reader_PreservesAttributesAndCommittedWalInAPrivateCleanedSnapshot()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix file permissions require a native Linux/macOS host.");
        using var fixture = new CookieDatabaseFixture();
        using var keeper = fixture.Open();
        using (var command = keeper.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL";
            Assert.Equal("wal", command.ExecuteScalar());
            command.CommandText = "PRAGMA wal_autocheckpoint=0";
            command.ExecuteNonQuery();
        }

        // An idle connection does not pin WAL data when the separate insert connections close.
        using var keeperTransaction = keeper.BeginTransaction(deferred: true);
        using var keeperCount = keeper.CreateCommand();
        keeperCount.Transaction = keeperTransaction;
        keeperCount.CommandText = "SELECT COUNT(*) FROM cookies";
        Assert.Equal(0L, (long)keeperCount.ExecuteScalar()!);

        var expired = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var expires = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        fixture.Add("empty-session", ".example.com", value: "", path: "/session",
            expires: expired, secure: true, httpOnly: true, hasExpires: false, persistent: false);
        var key = NativeBrowserCookieCrypto.DeriveSafeStorageKey("peanuts", isLinux: true);
        fixture.Add("persistent", "account.example.com", value: "unused-plaintext", path: "/sign-in",
            encrypted: NativeBrowserCookieCryptoTests.Encrypt(
                "v10", key, NativeBrowserCookieCryptoTests.HostBoundValue("account.example.com", "encrypted-session")),
            expires: expires, secure: true, httpOnly: false, hasExpires: true, persistent: true);
        fixture.Add("expired", ".example.com", value: "expired-value", expires: expired,
            hasExpires: true, persistent: true);
        var walPath = fixture.CookiePath + "-wal";
        Assert.True(File.Exists(walPath));
        Assert.True(new FileInfo(walPath).Length > 32, "The WAL must contain committed frames, not just its header.");
        Assert.Equal(0L, (long)keeperCount.ExecuteScalar()!);
        // SQLite resolves macOS temp-directory symlinks when reporting an open database path.
        var appDirectory = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(keeper.DataSource)!,
            Path.GetRelativePath(Path.GetDirectoryName(fixture.CookiePath)!, fixture.AppDirectory)));

        var cookies = BrowserCookieService.ReadStagedDatabase(
            fixture.CookiePath, fixture.AppDirectory, connection =>
            {
                Assert.StartsWith(appDirectory + Path.DirectorySeparatorChar, connection.DataSource);
                Assert.NotEqual(keeper.DataSource, connection.DataSource);
                Assert.False(new SqliteConnectionStringBuilder(connection.ConnectionString).Pooling);
                if (OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("Unix cookie snapshot permissions require a non-Windows platform.");

                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(connection.DataSource));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(Path.GetDirectoryName(connection.DataSource)!));
                using var count = connection.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM cookies";
                Assert.Equal(3L, (long)count.ExecuteScalar()!);
                return BrowserCookieService.ReadCookiesFromDatabase(connection, isLinux: true,
                    () => throw new InvalidOperationException("Legacy cookies must not query the keyring."));
            });

        Assert.Equal(2, cookies.Count);
        var session = Assert.Single(cookies, cookie => cookie.Name == "empty-session");
        Assert.Equal("", session.Value);
        Assert.Equal(".example.com", session.Domain);
        Assert.Equal("/session", session.Path);
        Assert.True(session.Secure);
        Assert.True(session.HttpOnly);
        Assert.True(session.Discard);
        Assert.Equal(DateTime.MinValue, session.Expires);
        var persistentCookie = Assert.Single(cookies, cookie => cookie.Name == "persistent");
        Assert.Equal("encrypted-session", persistentCookie.Value);
        Assert.Equal("account.example.com", persistentCookie.Domain);
        Assert.Equal("/sign-in", persistentCookie.Path);
        Assert.True(persistentCookie.Secure);
        Assert.False(persistentCookie.HttpOnly);
        Assert.False(persistentCookie.Discard);
        Assert.Equal(expires, persistentCookie.Expires.ToUniversalTime());
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.AppDirectory));
    }

    [Fact]
    public void Reader_RequestsSafeStorageOnlyOnceAndClearsItsDerivedKey()
    {
        using var fixture = new CookieDatabaseFixture();
        var key = NativeBrowserCookieCrypto.DeriveSafeStorageKey("synthetic-keyring", isLinux: true);
        foreach (var name in new[] { "first", "second" })
        {
            fixture.Add(name, ".example.com", encrypted: NativeBrowserCookieCryptoTests.Encrypt(
                "v11", key, NativeBrowserCookieCryptoTests.HostBoundValue(".example.com", name)));
        }

        var keyToRead = key.ToArray();
        var calls = 0;
        using var connection = fixture.Open();
        var cookies = BrowserCookieService.ReadCookiesFromDatabase(connection, isLinux: true, () =>
        {
            calls++;
            return keyToRead;
        });

        Assert.Equal(1, calls);
        Assert.Equal(["first", "second"], cookies.Select(c => c.Value).ToArray());
        Assert.All(keyToRead, value => Assert.Equal((byte)0, value));
    }

    [SkippableFact]
    public void Reader_UnsupportedEncryptedCookieFailsTheWholeReadAndStillCleansTheSnapshot()
    {
        Skip.If(OperatingSystem.IsWindows(), "Private snapshot creation requires a native Linux/macOS host.");
        using var fixture = new CookieDatabaseFixture();
        fixture.Add("plain", ".example.com", value: "synthetic-plain-value");
        fixture.Add("modern", ".example.com", value: "must-not-be-used",
            encrypted: [.. "v20"u8, .. new byte[16]]);

        Assert.Throws<NotSupportedException>(() => BrowserCookieService.ReadStagedDatabase(
            fixture.CookiePath, fixture.AppDirectory,
            connection => BrowserCookieService.ReadCookiesFromDatabase(connection, isLinux: true,
                () => throw new InvalidOperationException("Unsupported encryption must not query the keyring."))));

        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.AppDirectory));
    }

    [Fact]
    public void Scoring_CountsDistinctLiveGoogleAuthenticationCookieNames()
    {
        using var fixture = new CookieDatabaseFixture();
        fixture.Add("SID", ".google.com");
        fixture.Add("SID", "accounts.google.com");
        fixture.Add("__Secure-1PSID", ".google.com");
        fixture.Add("customSID", ".google.co.uk");
        fixture.Add("HSID", ".google.com", expires: new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            hasExpires: true, persistent: true);
        fixture.Add("SSID", ".example.com");
        using var connection = fixture.Open();

        Assert.Equal(22, BrowserCookieService.CountGoogleSessionCookies(connection));
    }

    private sealed class CookieDatabaseFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"Lumi-native-cookies-test-{Guid.NewGuid():N}");
        internal string UserDataPath => Path.Combine(_root, "SyntheticBrowser");
        internal string CookiePath => Path.Combine(UserDataPath, "Default", "Network", "Cookies");
        internal string AppDirectory => Path.Combine(_root, "SyntheticAppData", "Lumi");

        internal CookieDatabaseFixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CookiePath)!);
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO meta (key, value) VALUES ('version', '24');
                CREATE TABLE cookies (
                    host_key TEXT NOT NULL, name TEXT NOT NULL, value TEXT NOT NULL,
                    encrypted_value BLOB NOT NULL, path TEXT NOT NULL, expires_utc INTEGER NOT NULL,
                    is_secure INTEGER NOT NULL, is_httponly INTEGER NOT NULL,
                    has_expires INTEGER NOT NULL, is_persistent INTEGER NOT NULL);
                """;
            command.ExecuteNonQuery();
        }

        internal SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = CookiePath,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }

        internal void Add(
            string name, string host, string value = "", byte[]? encrypted = null, string path = "/",
            DateTime? expires = null, bool secure = false, bool httpOnly = false,
            bool hasExpires = false, bool persistent = false)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO cookies VALUES ($host, $name, $value, $encrypted, $path, $expires,
                                            $secure, $httpOnly, $hasExpires, $persistent)
                """;
            command.Parameters.AddWithValue("$host", host);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$encrypted", encrypted ?? []);
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$expires", expires?.ToFileTimeUtc() / 10 ?? 0);
            command.Parameters.AddWithValue("$secure", secure);
            command.Parameters.AddWithValue("$httpOnly", httpOnly);
            command.Parameters.AddWithValue("$hasExpires", hasExpires);
            command.Parameters.AddWithValue("$persistent", persistent);
            command.ExecuteNonQuery();
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
#endif
