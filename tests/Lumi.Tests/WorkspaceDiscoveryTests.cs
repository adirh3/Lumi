using Lumi.Services.Onboarding;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lumi.Tests;

public sealed class WorkspaceDiscoveryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lumi-discovery-" + Guid.NewGuid().ToString("N"));

    public WorkspaceDiscoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Fact]
    public void FindsRepositoriesWithTheirStackRemoteAndSummary()
    {
        var code = Folder("code");
        var lumi = Repository(code, "Lumi", Now.AddHours(-2));
        File.WriteAllText(Path.Combine(lumi, ".git", "config"),
            "[core]\n\tbare = false\n[remote \"origin\"]\n\turl = https://user:secret-token@github.com/adirh3/Lumi.git\n");
        File.WriteAllText(Path.Combine(lumi, "Lumi.slnx"), "<Solution />");
        Directory.CreateDirectory(Path.Combine(lumi, "src", "Lumi"));
        File.WriteAllText(Path.Combine(lumi, "src", "Lumi", "Lumi.csproj"),
            "<Project><ItemGroup><PackageReference Include=\"Avalonia\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(lumi, "README.md"),
            "# Lumi ✨\n\n[![CI](https://x/badge.svg)](https://x)\n\nA personal **agent** for your desktop, built with [Avalonia](https://avaloniaui.net).\n");

        var project = Assert.Single(Discovery(codeRoots: [code]).FindProjects(CancellationToken.None));

        Assert.Equal("Lumi", project.Name);
        Assert.Equal(DiscoveredProjectKind.Code, project.Kind);
        Assert.Equal(lumi, project.Path, ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal("C# · Avalonia", project.Stack);
        Assert.Equal("adirh3/Lumi", project.Remote);
        Assert.Equal("A personal agent for your desktop, built with Avalonia.", project.Description);
        Assert.NotNull(project.LastActive);
    }

    [Fact]
    public void FoldsWorktreesIntoTheirRepositoryAndSkipsSubmodulesAndDependencies()
    {
        var code = Folder("code");
        var lumi = Repository(code, "Lumi", Now.AddDays(-40));

        // A worktree: its .git is a file pointing into the repository's worktrees folder.
        var worktreeGitDir = Path.Combine(lumi, ".git", "worktrees", "feature");
        Directory.CreateDirectory(worktreeGitDir);
        File.WriteAllText(Path.Combine(worktreeGitDir, "commondir"), "../..");
        Touch(Path.Combine(worktreeGitDir, "index"), Now.AddHours(-1));
        var worktree = Folder("code", "Lumi-wt-feature");
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGitDir}");

        // A submodule checked out on its own, and a dependency folder that happens to hold a repository.
        var submodule = Folder("code", "Library");
        File.WriteAllText(Path.Combine(submodule, ".git"), $"gitdir: {Path.Combine(lumi, ".git", "modules", "Library")}");
        Repository(Folder("code", "node_modules"), "package", Now);

        var project = Assert.Single(Discovery(codeRoots: [code]).FindProjects(CancellationToken.None));

        Assert.Equal("Lumi", project.Name);
        Assert.True(Now - project.LastActive < TimeSpan.FromDays(1), "a worktree's activity counts toward its repository");
    }

    [Fact]
    public void RanksProjectsByActivityAndLeavesOutStaleRepositories()
    {
        var code = Folder("code");
        Repository(code, "Older", Now.AddDays(-3));
        Repository(code, "Newest", Now.AddHours(-1));
        Repository(code, "Abandoned", Now.AddDays(-500));

        var names = Discovery(codeRoots: [code]).FindProjects(CancellationToken.None).Select(project => project.Name);

        Assert.Equal(new[] { "Newest", "Older" }, names);
    }

    [Fact]
    public void ReadsRecentFoldersFromVsCodeState()
    {
        var notes = Folder("elsewhere", "notes-app");
        File.WriteAllText(Path.Combine(notes, "package.json"), "{\"dependencies\":{\"react\":\"19.0.0\"}}");
        File.WriteAllText(Path.Combine(notes, "tsconfig.json"), "{}");
        var state = Path.Combine(_root, "state.vscdb");
        // VS Code writes Windows folders with the drive colon escaped.
        var notesUri = OperatingSystem.IsWindows()
            ? "file:///" + char.ToLowerInvariant(notes[0]) + "%3A" + notes[2..].Replace('\\', '/')
            : new Uri(notes).AbsoluteUri;
        var recent = $$"""
            {"entries":[{"folderUri":"{{notesUri}}"},{"folderUri":"file://server/share/app"},{"folderUri":"vscode-remote://wsl+Ubuntu/home/me/app"},{"fileUri":"file:///c%3A/notes.txt"}]}
            """;
        using (var connection = new SqliteConnection($"Data Source={state};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB); " +
                                  "INSERT INTO ItemTable VALUES ('history.recentlyOpenedPathsList', $value);";
            command.Parameters.AddWithValue("$value", recent);
            command.ExecuteNonQuery();
        }

        var folders = WorkspaceDiscovery.ReadEditorRecentFolders([state]);
        Assert.Equal(new[] { WorkspaceDiscovery.NormalizePath(notes) }, folders);

        var project = Assert.Single(Discovery(editorStateFiles: [state]).FindProjects(CancellationToken.None));
        Assert.Equal("notes-app", project.Name);
        Assert.Equal(0, project.EditorRank);
        Assert.Equal("TypeScript · React", project.Stack);
    }

    [Fact]
    public void FindsFoldersOfRecentDocumentsButNotAppLogs()
    {
        var documents = Folder("Documents");
        var wedding = Folder("Documents", "Wedding");
        Touch(Path.Combine(wedding, "Guests.xlsx"), Now.AddDays(-2));
        Touch(Path.Combine(wedding, "Venue.pdf"), Now.AddDays(-5));
        Touch(Path.Combine(Folder("Documents", "Wedding", "Vendors"), "Catering.docx"), Now.AddDays(-9));

        var game = Folder("Documents", "Some Game");
        for (var index = 0; index < 10; index++)
            Touch(Path.Combine(game, $"log{index}.txt"), Now.AddDays(-1));

        var old = Folder("Documents", "Taxes 2019");
        for (var index = 0; index < 4; index++)
            Touch(Path.Combine(old, $"form{index}.pdf"), Now.AddDays(-300));

        var project = Assert.Single(Discovery(documentRoots: [documents]).FindProjects(CancellationToken.None));

        Assert.Equal("Wedding", project.Name);
        Assert.Equal(DiscoveredProjectKind.Folder, project.Kind);
        Assert.Equal(3, project.DocumentCount);
    }

    [Theory]
    [InlineData("https://github.com/adirh3/Lumi.git", "adirh3/Lumi")]
    [InlineData("git@github.com:adirh3/Lumi.git", "adirh3/Lumi")]
    [InlineData("https://user:secret@github.com/owner/repo", "owner/repo")]
    [InlineData("https://adirh3-gmail.visualstudio.com/Blast/_git/Blast", "Azure DevOps: Blast")]
    [InlineData("https://dev.azure.com/org/Website/_git/Blog", "Azure DevOps: Website/Blog")]
    [InlineData("https://gitlab.com/group/app.git", "gitlab.com/group/app")]
    [InlineData(@"C:\repos\local", null)]
    [InlineData("/srv/git/local.git", null)]
    [InlineData("", null)]
    public void DescribesRemotesWithoutCredentialsOrLocalPaths(string url, string? expected)
        => Assert.Equal(expected, WorkspaceDiscovery.NormalizeRemote(url));

    [Fact]
    public void SummarizesTheFirstRealParagraphOfAReadme()
    {
        const string readme = """
            ---
            title: Ignored front matter
            ---
            <p align="center"><img src="logo.png" /></p>

            # Project

            ![build](https://example/badge.svg)

            ```bash
            npm install
            ```

            Turns your scattered notes into a searchable, *private* knowledge base that runs entirely on your own machine, with no account or cloud sync required at any point.

            ## More
            """;

        var summary = WorkspaceDiscovery.ExtractSummary(readme, maxLength: 90);

        Assert.NotNull(summary);
        Assert.StartsWith("Turns your scattered notes into a searchable, private knowledge base", summary);
        Assert.EndsWith("…", summary);
        Assert.True(summary.Length <= 90);
        Assert.Null(WorkspaceDiscovery.ExtractSummary("# Title\n\nWIP"));
    }

    [Fact]
    public void ListsVisitedSitesWithoutSearchesSignInsLocalOrSensitiveOnes()
    {
        var history = Path.Combine(_root, "History");
        var recent = ChromiumTime(Now.AddDays(-3));
        var old = ChromiumTime(Now.AddDays(-200));
        using (var connection = new SqliteConnection($"Data Source={history};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE urls (url TEXT, visit_count INTEGER, last_visit_time INTEGER);";
            command.ExecuteNonQuery();
            (string Url, int Visits, long Time)[] rows =
            [
                ("https://github.com/adirh3/Lumi", 10, recent),
                ("https://www.github.com/notifications", 5, recent),
                ("https://www.google.com/search?q=lumi", 50, recent),
                ("https://accounts.google.com/signin", 30, recent),
                ("http://localhost:5000/", 40, recent),
                ("https://192.168.1.1/admin", 40, recent),
                ("https://someadultsite.com/", 100, recent),
                ("https://www.reddit.com/r/dotnet", 7, recent),
                ("https://old.example.com/", 90, old),
            ];
            foreach (var (url, visits, time) in rows)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO urls VALUES ($url, $visits, $time);";
                insert.Parameters.AddWithValue("$url", url);
                insert.Parameters.AddWithValue("$visits", visits);
                insert.Parameters.AddWithValue("$time", time);
                insert.ExecuteNonQuery();
            }
        }

        var sites = Discovery(chromiumHistoryFiles: [history]).FindSites(CancellationToken.None);

        Assert.Equal(new[] { "github.com", "reddit.com" }, sites);
        Assert.True(File.Exists(history), "the browser's own history file is never touched");
    }

    [Fact]
    public void CountsRecentAuthoredDocuments()
    {
        var documents = Folder("Documents");
        Touch(Path.Combine(documents, "Plan.docx"), Now.AddDays(-1));
        Touch(Path.Combine(documents, "Invoice.pdf"), Now.AddDays(-2));
        Touch(Path.Combine(documents, "notes.txt"), Now.AddDays(-1));
        Touch(Path.Combine(documents, "Archive.pdf"), Now.AddDays(-90));
        var downloads = Folder("Downloads");
        for (var index = 0; index < 5; index++)
            Touch(Path.Combine(downloads, $"setup{index}.exe"), Now.AddDays(-index));

        var found = Discovery(documentRoots: [documents], downloads: downloads).FindDocuments(CancellationToken.None);

        Assert.Equal(new WorkspaceDocuments(Recent: 2, Pdfs: 1, Office: 1, Downloads: 5), found);
    }

    [Theory]
    [InlineData("Microsoft Visual Studio Code (User)", "VS Code")]
    [InlineData("Visual Studio Community 2022", "Visual Studio")]
    [InlineData("Steam", "Steam")]
    [InlineData("SteamVR", null)]
    [InlineData("ZoomIt", null)]
    [InlineData("Zoom Workplace", "Zoom")]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64)", null)]
    public void RecognizesOnlyWellKnownApps(string displayName, string? expected)
        => Assert.Equal(expected, KnownApps.FromDisplayName(displayName));

    private WorkspaceDiscovery Discovery(
        IReadOnlyList<string>? codeRoots = null,
        IReadOnlyList<string>? documentRoots = null,
        IReadOnlyList<string>? editorStateFiles = null,
        IReadOnlyList<string>? chromiumHistoryFiles = null,
        string? downloads = null)
        => new(new WorkspaceDiscoveryOptions
        {
            HomeDirectory = Folder("home"),
            CodeRoots = codeRoots ?? [],
            DocumentRoots = documentRoots ?? [],
            EditorStateFiles = editorStateFiles ?? [],
            ChromiumHistoryFiles = chromiumHistoryFiles ?? [],
            FirefoxHistoryFiles = [],
            DownloadsDirectory = downloads ?? Path.Combine(_root, "no-downloads"),
            ExcludedRoots = [],
            ReadSystemApps = false,
            Now = () => Now,
        });

    private string Folder(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Repository(string parent, string name, DateTimeOffset lastActive)
    {
        var repository = Path.Combine(parent, name);
        var gitDir = Path.Combine(repository, ".git");
        Directory.CreateDirectory(Path.Combine(gitDir, "logs"));
        Touch(Path.Combine(gitDir, "HEAD"), lastActive, "ref: refs/heads/main\n");
        Touch(Path.Combine(gitDir, "index"), lastActive);
        Touch(Path.Combine(gitDir, "logs", "HEAD"), lastActive);
        return repository;
    }

    private static void Touch(string file, DateTimeOffset written, string content = "x")
    {
        File.WriteAllText(file, content);
        File.SetLastWriteTimeUtc(file, written.UtcDateTime);
    }

    private static long ChromiumTime(DateTimeOffset time)
        => (time.UtcDateTime - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks / 10;
}
