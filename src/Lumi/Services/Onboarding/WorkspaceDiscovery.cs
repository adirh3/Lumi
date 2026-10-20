using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Lumi.Services.Onboarding;

public enum DiscoveredProjectKind { Code, Folder }

/// <summary>A folder Lumi could set up as a project: a code repository or a folder of documents.</summary>
public sealed record DiscoveredProject(
    string Name,
    string Path,
    DiscoveredProjectKind Kind,
    DateTimeOffset? LastActive,
    string? Stack = null,
    string? Remote = null,
    string? Description = null,
    int DocumentCount = 0,
    int EditorRank = -1);

/// <summary>How many recent files onboarding saw, by kind. Never their names or contents.</summary>
public sealed record WorkspaceDocuments(int Recent, int Pdfs, int Office, int Downloads)
{
    public static WorkspaceDocuments Empty { get; } = new(0, 0, 0, 0);
}

/// <summary>What onboarding found on this computer, for the sources the user allowed.</summary>
public sealed record WorkspaceSnapshot(
    IReadOnlyList<DiscoveredProject> Projects,
    IReadOnlyList<string> Apps,
    IReadOnlyList<string> Sites,
    WorkspaceDocuments Documents)
{
    public static WorkspaceSnapshot Empty { get; } = new([], [], [], WorkspaceDocuments.Empty);

    public bool IsEmpty => Projects.Count == 0 && Apps.Count == 0 && Sites.Count == 0 && Documents.Recent == 0;
}

/// <summary>The local signals onboarding reads, one source at a time so each can be allowed separately.</summary>
public interface IWorkspaceDiscovery
{
    Task<IReadOnlyList<DiscoveredProject>> FindProjectsAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> FindAppsAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> FindSitesAsync(CancellationToken ct);
    Task<WorkspaceDocuments> FindDocumentsAsync(CancellationToken ct);
}

/// <summary>Where discovery looks. The defaults describe this computer; tests point it at temporary folders.</summary>
public sealed class WorkspaceDiscoveryOptions
{
    public string HomeDirectory { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Folders searched two levels deep for repositories. Null uses the usual places on this computer.</summary>
    public IReadOnlyList<string>? CodeRoots { get; init; }

    /// <summary>Folders whose subfolders may hold documents worth a project. Null uses Documents and Desktop.</summary>
    public IReadOnlyList<string>? DocumentRoots { get; init; }

    /// <summary>VS Code-family state databases that list recently opened folders. Null uses the installed editors.</summary>
    public IReadOnlyList<string>? EditorStateFiles { get; init; }

    public IReadOnlyList<string>? ChromiumHistoryFiles { get; init; }
    public IReadOnlyList<string>? FirefoxHistoryFiles { get; init; }
    public string? DownloadsDirectory { get; init; }

    /// <summary>Folders that never become projects, nor anything inside them. Null uses app data, system folders and temp.</summary>
    public IReadOnlyList<string>? ExcludedRoots { get; init; }

    /// <summary>Whether installed and running apps are read from this computer.</summary>
    public bool ReadSystemApps { get; init; } = true;

    public Func<DateTimeOffset> Now { get; init; } = static () => DateTimeOffset.Now;
}

/// <summary>
/// Reads what the user works on from local metadata only: repository folders and their git state,
/// editors' recent-folder lists, folders of recent documents, known apps, browser-history domains and
/// recent-file counts. It never reads document contents (only a project's README summary), never writes,
/// and never starts another process.
/// </summary>
public sealed class WorkspaceDiscovery : IWorkspaceDiscovery
{
    internal const int MaxProjects = 8;
    private const int MaxApps = 24;
    private const int MaxSites = 15;
    private const int MaxFoldersPerRoot = 600;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(365);
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromDays(90);

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly EnumerationOptions FolderEnumeration = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    private readonly WorkspaceDiscoveryOptions _options;

    public WorkspaceDiscovery(WorkspaceDiscoveryOptions? options = null) => _options = options ?? new();

    public Task<IReadOnlyList<DiscoveredProject>> FindProjectsAsync(CancellationToken ct)
        => Task.Run(() => FindProjects(ct), ct);

    public Task<IReadOnlyList<string>> FindAppsAsync(CancellationToken ct)
        => Task.Run(() => FindApps(ct), ct);

    public Task<IReadOnlyList<string>> FindSitesAsync(CancellationToken ct)
        => Task.Run(() => FindSites(ct), ct);

    public Task<WorkspaceDocuments> FindDocumentsAsync(CancellationToken ct)
        => Task.Run(() => FindDocuments(ct), ct);

    // ═══════════════════════════════════════════════════════════════
    // Projects
    // ═══════════════════════════════════════════════════════════════

    internal IReadOnlyList<DiscoveredProject> FindProjects(CancellationToken ct)
    {
        var excluded = ExcludedRoots();
        var unsuitable = UnsuitableFolders();
        var evidence = new Dictionary<string, ProjectEvidence>(PathComparer);

        var editorFolders = ReadEditorRecentFolders(EditorStateFiles());
        for (var rank = 0; rank < editorFolders.Count; rank++)
            Collect(editorFolders[rank], rank, fromEditor: true);

        foreach (var (root, depth) in CodeRoots())
        {
            foreach (var repository in FindRepositories(root, depth, ct))
                Collect(repository, -1, fromEditor: false);
        }

        var now = _options.Now();
        var projects = new List<DiscoveredProject>();
        foreach (var item in evidence.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (Describe(item, now) is { } project)
                projects.Add(project);
        }

        projects.AddRange(FindDocumentFolders(evidence.Keys.ToHashSet(PathComparer), excluded, unsuitable, now, ct));
        return Rank(projects);

        void Collect(string folder, int editorRank, bool fromEditor)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(folder) || IsExcluded(folder, excluded, unsuitable))
                return;

            var git = GitRepository.TryOpen(folder) ?? (fromEditor ? FindEnclosingRepository(folder) : null);
            if (git is { IsSubmodule: true })
                return;
            if (git is null && (!fromEditor || DetectStack(folder) is null))
                return;

            // A worktree is the same project as its repository, so its activity counts toward that.
            var path = NormalizePath(git?.MainWorkTree is { } main && Directory.Exists(main) ? main : folder);
            if (IsExcluded(path, excluded, unsuitable))
                return;

            if (!evidence.TryGetValue(path, out var item))
                evidence[path] = item = new ProjectEvidence(path);
            if (git is not null)
            {
                item.GitDirs.Add(git.GitDir);
                item.CommonGitDir ??= git.CommonDir;
            }
            if (editorRank >= 0 && (item.EditorRank < 0 || editorRank < item.EditorRank))
                item.EditorRank = editorRank;
        }
    }

    private static IReadOnlyList<DiscoveredProject> Rank(IEnumerable<DiscoveredProject> projects)
        => projects
            .OrderByDescending(project => project.LastActive ?? DateTimeOffset.MinValue)
            .ThenBy(project => project.EditorRank < 0 ? int.MaxValue : project.EditorRank)
            .Take(MaxProjects)
            .ToList();

    private static DiscoveredProject? Describe(ProjectEvidence item, DateTimeOffset now)
    {
        var path = ActualCasing(item.Path);
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var lastActive = item.GitDirs.Count > 0
            ? LatestWrite(item.GitDirs
                .SelectMany(GitActivityFiles)
                .Concat(item.CommonGitDir is { } common ? GitActivityFiles(common) : []))
            : LatestDirectoryWrite(path);
        if (item.EditorRank < 0 && lastActive is { } active && now - active > StaleAfter)
            return null;

        return new DiscoveredProject(
            name,
            path,
            DiscoveredProjectKind.Code,
            lastActive,
            DetectStack(path),
            item.CommonGitDir is { } gitDir ? ReadRemote(gitDir) : null,
            ReadDescription(path),
            EditorRank: item.EditorRank);
    }

    /// <summary>
    /// The folder's path as the file system spells it. Editors record folders as typed or as URIs
    /// (e.g. "e:\git\lumi"), and a project should show and use the real name.
    /// </summary>
    internal static string ActualCasing(string path)
    {
        if (!OperatingSystem.IsWindows())
            return path;

        try
        {
            var directory = new DirectoryInfo(path);
            if (!directory.Exists)
                return path;

            var names = new Stack<string>();
            for (var current = directory; current.Parent is { } parent; current = parent)
            {
                var match = parent.EnumerateDirectories(current.Name).FirstOrDefault();
                names.Push(match?.Name ?? current.Name);
            }

            return names.Aggregate(NormalizePath(directory.Root.FullName), Path.Combine);
        }
        catch
        {
            return path;
        }
    }

    private IEnumerable<(string Root, int Depth)> CodeRoots()
    {
        if (_options.CodeRoots is { } roots)
            return roots.Where(Directory.Exists).Select(root => (root, 2));

        var home = _options.HomeDirectory;
        var list = new List<(string, int)> { (home, 1) };
        foreach (var name in HomeCodeFolders)
            list.Add((Path.Combine(home, name), 2));

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(documents))
        {
            foreach (var name in DocumentsCodeFolders)
                list.Add((Path.Combine(documents, name), 2));
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in SafeDrives())
            {
                list.Add((drive, 1));
                foreach (var name in DriveCodeFolders)
                    list.Add((Path.Combine(drive, name), 2));
            }
        }

        return list
            .Where(root => Directory.Exists(root.Item1))
            .DistinctBy(root => NormalizePath(root.Item1), PathComparer);
    }

    private static readonly string[] HomeCodeFolders =
    [
        "source", "repos", "Repos", "git", "Git", "github", "GitHub", "code", "Code", "dev", "Dev",
        "development", "Development", "projects", "Projects", "src", "workspace", "Workspace",
        "work", "Work", "Developer", Path.Combine("go", "src"),
    ];

    private static readonly string[] DocumentsCodeFolders = ["GitHub", "Projects", "Code", "Source", "Repos"];

    private static readonly string[] DriveCodeFolders =
        ["git", "github", "repos", "src", "source", "code", "dev", "projects", "work", "workspace"];

    private static IEnumerable<string> SafeDrives()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { yield break; }

        foreach (var drive in drives)
        {
            bool usable;
            try { usable = drive.DriveType == DriveType.Fixed && drive.IsReady; }
            catch { usable = false; }
            if (usable)
                yield return drive.RootDirectory.FullName;
        }
    }

    private static IEnumerable<string> FindRepositories(string root, int maxDepth, CancellationToken ct)
    {
        if (LooksLikeRepository(root))
        {
            yield return root;
            yield break;
        }

        var pending = new Queue<(string Folder, int Depth)>();
        pending.Enqueue((root, 0));
        var visited = 0;
        while (pending.Count > 0 && visited < MaxFoldersPerRoot)
        {
            ct.ThrowIfCancellationRequested();
            var (folder, depth) = pending.Dequeue();
            foreach (var child in SafeDirectories(folder))
            {
                if (++visited > MaxFoldersPerRoot)
                    break;
                if (IsSkippedFolderName(Path.GetFileName(child)))
                    continue;
                if (LooksLikeRepository(child))
                {
                    yield return child;
                    continue;
                }
                if (depth + 1 < maxDepth)
                    pending.Enqueue((child, depth + 1));
            }
        }
    }

    private static GitRepository? FindEnclosingRepository(string folder)
    {
        var current = Path.GetDirectoryName(folder);
        for (var depth = 0; current is not null && depth < 4; depth++, current = Path.GetDirectoryName(current))
        {
            if (GitRepository.TryOpen(current) is { } repository)
                return repository;
        }

        return null;
    }

    private static readonly HashSet<string> SkippedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "packages", "target", "dist", "build", "out", "vendor", "__pycache__",
        "venv", "AppData", "Library", "Applications", "Windows", "Program Files", "Program Files (x86)",
        "ProgramData", "$Recycle.Bin", "System Volume Information", "Recovery", "PerfLogs", "OneDriveTemp",
    };

    private static bool IsSkippedFolderName(string? name)
        => string.IsNullOrEmpty(name) || name.StartsWith('.') || SkippedFolderNames.Contains(name);

    private static bool LooksLikeRepository(string folder)
    {
        var dotGit = Path.Combine(folder, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    private static IEnumerable<string> GitActivityFiles(string gitDir)
    {
        yield return Path.Combine(gitDir, "index");
        yield return Path.Combine(gitDir, "HEAD");
        yield return Path.Combine(gitDir, "FETCH_HEAD");
        yield return Path.Combine(gitDir, "ORIG_HEAD");
        yield return Path.Combine(gitDir, "logs", "HEAD");
    }

    private static DateTimeOffset? LatestWrite(IEnumerable<string> files)
    {
        DateTime? latest = null;
        foreach (var file in files)
        {
            try
            {
                if (!File.Exists(file))
                    continue;
                var written = File.GetLastWriteTimeUtc(file);
                if (latest is null || written > latest)
                    latest = written;
            }
            catch { }
        }

        return latest is { } utc ? new DateTimeOffset(utc, TimeSpan.Zero).ToLocalTime() : null;
    }

    private static DateTimeOffset? LatestDirectoryWrite(string folder)
    {
        try { return new DateTimeOffset(Directory.GetLastWriteTimeUtc(folder), TimeSpan.Zero).ToLocalTime(); }
        catch { return null; }
    }

    private sealed class ProjectEvidence(string path)
    {
        public string Path { get; } = path;
        public HashSet<string> GitDirs { get; } = new(PathComparer);
        public string? CommonGitDir { get; set; }
        public int EditorRank { get; set; } = -1;
    }

    /// <summary>A folder's git metadata: its own git dir, the shared one, and the repository it belongs to.</summary>
    internal sealed record GitRepository(string GitDir, string CommonDir, string? MainWorkTree, bool IsSubmodule)
    {
        public static GitRepository? TryOpen(string folder)
        {
            var dotGit = Path.Combine(folder, ".git");
            if (Directory.Exists(dotGit))
                return new GitRepository(dotGit, dotGit, folder, false);

            var pointer = ReadSmallText(dotGit).Split('\n', 2)[0];
            if (!pointer.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                return null;

            var gitDir = ResolvePath(folder, pointer["gitdir:".Length..].Trim());
            if (gitDir is null)
                return null;
            if (gitDir.Replace('\\', '/').Contains("/.git/modules/", StringComparison.OrdinalIgnoreCase))
                return new GitRepository(gitDir, gitDir, null, true);

            var common = ResolvePath(gitDir, ReadSmallText(Path.Combine(gitDir, "commondir")).Trim()) ?? gitDir;

            var trimmed = common.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var main = string.Equals(Path.GetFileName(trimmed), ".git", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(trimmed)
                : folder;
            return new GitRepository(gitDir, common, main, false);
        }

        private static string? ResolvePath(string baseFolder, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try { return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseFolder, path)); }
            catch { return null; }
        }
    }

    // ── Stack, remote and README ──

    /// <summary>The project's language and, when obvious, its framework (e.g. "C# · Avalonia").</summary>
    internal static string? DetectStack(string folder)
    {
        if (DetectStack(folder, SafeFileNames(folder)) is { } stack)
            return stack;

        var src = Path.Combine(folder, "src");
        if (!Directory.Exists(src))
            return null;

        foreach (var child in SafeDirectories(src).Take(12).Prepend(src))
        {
            if (DetectStack(child, SafeFileNames(child)) is { } nested)
                return nested;
        }

        return null;
    }

    private static string? DetectStack(string folder, IReadOnlyList<string> names)
    {
        bool Has(string name) => names.Contains(name, StringComparer.OrdinalIgnoreCase);
        bool HasExtension(string extension) => names.Any(name => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

        if (HasExtension(".csproj") || HasExtension(".sln") || HasExtension(".slnx") || HasExtension(".fsproj"))
        {
            var language = HasExtension(".fsproj") && !HasExtension(".csproj") ? "F#" : "C#";
            return Combine(language, DotNetFramework(folder, names));
        }

        if (Has("package.json"))
        {
            var package = ReadSmallText(Path.Combine(folder, "package.json"));
            var language = Has("tsconfig.json") || package.Contains("\"typescript\"", StringComparison.Ordinal)
                ? "TypeScript"
                : "JavaScript";
            return Combine(language, JavaScriptFramework(package));
        }

        if (Has("pyproject.toml") || Has("requirements.txt") || Has("setup.py") || Has("Pipfile"))
        {
            var text = ReadSmallText(Path.Combine(folder, "pyproject.toml")) + ReadSmallText(Path.Combine(folder, "requirements.txt"));
            return Combine("Python", FirstMatch(text, ("django", "Django"), ("fastapi", "FastAPI"), ("flask", "Flask"), ("torch", "PyTorch")));
        }

        if (Has("Cargo.toml")) return "Rust";
        if (Has("go.mod")) return "Go";
        if (Has("pubspec.yaml"))
        {
            return ReadSmallText(Path.Combine(folder, "pubspec.yaml")).Contains("flutter", StringComparison.OrdinalIgnoreCase)
                ? "Dart · Flutter"
                : "Dart";
        }
        if (Has("build.gradle.kts") || Has("settings.gradle.kts")) return "Kotlin";
        if (Has("pom.xml") || Has("build.gradle")) return "Java";
        if (Has("Gemfile"))
            return Combine("Ruby", ReadSmallText(Path.Combine(folder, "Gemfile")).Contains("rails", StringComparison.OrdinalIgnoreCase) ? "Rails" : null);
        if (Has("composer.json"))
            return Combine("PHP", ReadSmallText(Path.Combine(folder, "composer.json")).Contains("laravel", StringComparison.OrdinalIgnoreCase) ? "Laravel" : null);
        if (Has("Package.swift") || HasExtension(".xcodeproj")) return "Swift";
        if (Has("mix.exs")) return "Elixir";
        if (Has("CMakeLists.txt") || HasExtension(".vcxproj")) return "C++";
        if (Has("deno.json")) return "TypeScript · Deno";
        if (Directory.Exists(Path.Combine(folder, "Assets")) && Directory.Exists(Path.Combine(folder, "ProjectSettings"))) return "C# · Unity";
        return null;
    }

    private static string? DotNetFramework(string folder, IReadOnlyList<string> names)
    {
        var projectFiles = names
            .Where(name => name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
            .Select(name => Path.Combine(folder, name))
            .ToList();

        if (projectFiles.Count == 0)
        {
            var src = Path.Combine(folder, "src");
            if (Directory.Exists(src))
            {
                projectFiles.AddRange(SafeDirectories(src)
                    .Take(12)
                    .SelectMany(child => SafeFileNames(child)
                        .Where(name => name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                        .Select(name => Path.Combine(child, name))));
            }
        }

        var text = string.Concat(projectFiles.Take(4).Select(file => ReadSmallText(file)));
        return FirstMatch(text,
            ("Avalonia", "Avalonia"),
            ("Microsoft.Maui", "MAUI"),
            ("<UseMaui>", "MAUI"),
            ("<UseWPF>", "WPF"),
            ("BlazorWebAssembly", "Blazor"),
            ("Microsoft.NET.Sdk.Web", "ASP.NET"),
            ("<UseWindowsForms>", "WinForms"));
    }

    private static string? JavaScriptFramework(string package)
        => FirstMatch(package,
            ("\"next\"", "Next.js"),
            ("\"react\"", "React"),
            ("\"vue\"", "Vue"),
            ("\"svelte\"", "Svelte"),
            ("\"@angular/core\"", "Angular"),
            ("\"electron\"", "Electron"),
            ("\"express\"", "Node.js"));

    private static string? FirstMatch(string text, params (string Needle, string Label)[] candidates)
    {
        foreach (var (needle, label) in candidates)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return label;
        }

        return null;
    }

    private static string Combine(string language, string? framework)
        => framework is null || string.Equals(language, framework, StringComparison.Ordinal) ? language : $"{language} · {framework}";

    /// <summary>The origin remote as "owner/repo" (GitHub) or "host/path", never with credentials.</summary>
    internal static string? ReadRemote(string gitDir)
    {
        string? section = null;
        string? origin = null;
        string? first = null;
        foreach (var raw in ReadSmallText(Path.Combine(gitDir, "config")).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                section = line;
                continue;
            }

            if (section is null || !section.StartsWith("[remote", StringComparison.OrdinalIgnoreCase))
                continue;
            var separator = line.IndexOf('=');
            if (separator <= 0 || !line[..separator].Trim().Equals("url", StringComparison.OrdinalIgnoreCase))
                continue;

            var url = line[(separator + 1)..].Trim();
            first ??= url;
            if (section.Contains("\"origin\"", StringComparison.OrdinalIgnoreCase))
                origin ??= url;
        }

        return NormalizeRemote(origin ?? first);
    }

    internal static string? NormalizeRemote(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        url = url.Trim();
        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith('/') || url.StartsWith('.') || url.StartsWith('\\')
            || (url.Length > 2 && url[1] == ':' && char.IsLetter(url[0])))
        {
            return null;
        }

        string host;
        string path;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            host = uri.Host;
            path = uri.AbsolutePath;
        }
        else
        {
            // scp-like: git@github.com:owner/repo.git
            var colon = url.IndexOf(':');
            if (colon <= 0)
                return null;
            var hostPart = url[..colon];
            host = hostPart[(hostPart.LastIndexOf('@') + 1)..];
            path = url[(colon + 1)..];
        }

        path = Uri.UnescapeDataString(path).Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        path = path.Replace("/_git/", "/", StringComparison.Ordinal);
        if (path.Length == 0 || host.Length == 0)
            return null;

        if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return path;

        // Azure DevOps: {org}.visualstudio.com/{project}/{repo} or dev.azure.com/{org}/{project}/{repo}.
        if (host.EndsWith("visualstudio.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var repository = segments[^1];
            var project = segments.Length >= 2 ? segments[^2] : repository;
            return string.Equals(project, repository, StringComparison.OrdinalIgnoreCase)
                ? $"Azure DevOps: {repository}"
                : $"Azure DevOps: {project}/{repository}";
        }

        return $"{host.ToLowerInvariant()}/{path}";
    }

    private static readonly string[] ReadmeNames =
        ["README.md", "Readme.md", "readme.md", "README.markdown", "README", "README.txt", "readme.txt", "README.rst"];

    /// <summary>The first real paragraph of the project's README, plain and short.</summary>
    internal static string? ReadDescription(string folder)
    {
        foreach (var name in ReadmeNames)
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path) || IsCloudPlaceholder(path))
                continue;
            return ExtractSummary(ReadSmallText(path, 16 * 1024));
        }

        return null;
    }

    private static readonly Regex HtmlTag = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex HtmlComment = new("<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex MarkdownImage = new(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex MarkdownLink = new(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Emphasis = new(@"(\*\*|__|\*|`)(.+?)\1", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    internal static string? ExtractSummary(string markdown, int maxLength = 180)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return null;

        var text = markdown.Replace("\r\n", "\n");
        if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end > 0)
                text = text[(end + 4)..];
        }
        text = HtmlComment.Replace(text, " ");

        var paragraph = new StringBuilder();
        var inFence = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence)
                continue;

            var isBreak = line.Length == 0
                          || line.StartsWith('#')
                          || line.StartsWith('|')
                          || line.StartsWith('>')
                          || line.StartsWith("===", StringComparison.Ordinal)
                          || line.StartsWith("---", StringComparison.Ordinal)
                          || line.StartsWith("- ", StringComparison.Ordinal)
                          || line.StartsWith("* ", StringComparison.Ordinal)
                          || line.Contains("![", StringComparison.Ordinal)
                          || (line.StartsWith('<') && HtmlTag.Replace(line, "").Trim().Length == 0);
            if (isBreak)
            {
                if (TryFinish(paragraph, maxLength) is { } summary)
                    return summary;
                paragraph.Clear();
                continue;
            }

            paragraph.Append(line).Append(' ');
        }

        return TryFinish(paragraph, maxLength);
    }

    private static string? TryFinish(StringBuilder paragraph, int maxLength)
    {
        if (paragraph.Length == 0)
            return null;

        var text = paragraph.ToString();
        text = MarkdownImage.Replace(text, " ");
        text = MarkdownLink.Replace(text, "$1");
        text = HtmlTag.Replace(text, " ");
        text = Emphasis.Replace(text, "$2");
        text = Whitespace.Replace(text, " ").Trim();
        if (text.Count(char.IsLetter) < 24)
            return null;

        return Shorten(text, maxLength);
    }

    internal static string Shorten(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return text;

        var cut = text.LastIndexOf(' ', maxLength - 1);
        if (cut < maxLength / 2)
            cut = maxLength - 1;
        return text[..cut].TrimEnd(',', ';', ':', '.', '-', ' ') + "…";
    }

    // ── Folders of documents ──

    // Documents people author. Plain .txt and .csv are left out: apps and games write those as logs and
    // settings, which would make their folders look like projects.
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".doc", ".xlsx", ".xls", ".xlsm", ".pptx", ".ppt", ".pdf", ".odt", ".ods", ".odp", ".rtf",
        ".md", ".key", ".pages", ".numbers", ".psd", ".ai", ".fig", ".sketch", ".xd", ".indd",
    };

    private static readonly HashSet<string> SkippedDocumentFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "My Music", "My Pictures", "My Videos", "My Games", "WindowsPowerShell", "PowerShell", "IISExpress",
        "Custom Office Templates", "OneNote Notebooks", "Outlook Files", "Zoom", "Rainmeter", "Sound Recordings",
        "Snagit", "Camtasia", "Fax", "Downloads",
    };

    private IEnumerable<DiscoveredProject> FindDocumentFolders(
        HashSet<string> known,
        IReadOnlyList<string> excluded,
        HashSet<string> unsuitable,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var since = now.AddDays(-120).UtcDateTime;
        var recent = now.AddDays(-60);
        var results = new List<DiscoveredProject>();
        foreach (var root in DocumentRoots())
        {
            foreach (var folder in SafeDirectories(root).Take(80))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(folder);
                var path = NormalizePath(folder);
                if (IsSkippedFolderName(name)
                    || SkippedDocumentFolders.Contains(name)
                    || name.StartsWith("Visual Studio", StringComparison.OrdinalIgnoreCase)
                    || known.Contains(path)
                    || IsExcluded(path, excluded, unsuitable)
                    || LooksLikeRepository(folder))
                {
                    continue;
                }

                var (count, newest) = CountRecentDocuments(folder, since);
                if (count < 3 || newest is null || newest < recent)
                    continue;

                results.Add(new DiscoveredProject(name, path, DiscoveredProjectKind.Folder, newest, DocumentCount: count));
            }
        }

        return results;
    }

    private static (int Count, DateTimeOffset? Newest) CountRecentDocuments(string folder, DateTime sinceUtc)
    {
        var count = 0;
        DateTime? newest = null;
        var scanned = 0;
        foreach (var directory in SafeDirectories(folder).Take(30).Prepend(folder))
        {
            foreach (var file in SafeFiles(directory))
            {
                if (++scanned > 400)
                    break;
                if (!DocumentExtensions.Contains(Path.GetExtension(file)))
                    continue;
                DateTime written;
                try { written = File.GetLastWriteTimeUtc(file); }
                catch { continue; }
                if (written < sinceUtc)
                    continue;
                count++;
                if (newest is null || written > newest)
                    newest = written;
            }
        }

        return (count, newest is { } utc ? new DateTimeOffset(utc, TimeSpan.Zero).ToLocalTime() : null);
    }

    private IEnumerable<string> DocumentRoots()
    {
        if (_options.DocumentRoots is { } roots)
            return roots.Where(Directory.Exists);

        return new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            }
            .Where(path => !string.IsNullOrEmpty(path) && Directory.Exists(path))
            .Distinct(PathComparer);
    }

    // ── Editors' recent folders ──

    private IReadOnlyList<string> EditorStateFiles()
    {
        if (_options.EditorStateFiles is { } files)
            return files;

        var configRoot = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : OperatingSystem.IsMacOS()
                ? Path.Combine(_options.HomeDirectory, "Library", "Application Support")
                : Path.Combine(_options.HomeDirectory, ".config");
        return new[] { "Code", "Code - Insiders", "Cursor", "Windsurf", "VSCodium" }
            .Select(product => Path.Combine(configRoot, product, "User", "globalStorage", "state.vscdb"))
            .ToList();
    }

    /// <summary>Folders VS Code-family editors opened recently, most recent first.</summary>
    internal static IReadOnlyList<string> ReadEditorRecentFolders(IEnumerable<string> stateFiles)
    {
        var folders = new List<string>();
        var seen = new HashSet<string>(PathComparer);
        foreach (var file in stateFiles)
        {
            if (!File.Exists(file))
                continue;

            var json = ReadSqliteText(file, "SELECT value FROM ItemTable WHERE key = 'history.recentlyOpenedPathsList'");
            foreach (var folder in ParseRecentlyOpened(json))
            {
                if (seen.Add(folder))
                    folders.Add(folder);
            }
        }

        return folders;
    }

    internal static IReadOnlyList<string> ParseRecentlyOpened(string? json)
    {
        var folders = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
            return folders;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                return folders;

            foreach (var entry in entries.EnumerateArray())
            {
                // Network shares (and \\wsl.localhost) can stall the scan or start a distro just to be checked.
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("folderUri", out var folderUri)
                    || folderUri.ValueKind != JsonValueKind.String
                    || !Uri.TryCreate(folderUri.GetString(), UriKind.Absolute, out var uri)
                    || !uri.IsFile
                    || uri.IsUnc)
                {
                    continue;
                }

                folders.Add(NormalizePath(LocalPath(uri)));
                if (folders.Count >= 40)
                    break;
            }
        }
        catch (JsonException) { }

        return folders;
    }

    /// <summary>VS Code escapes the drive colon ("file:///c%3A/..."), which <see cref="Uri.LocalPath"/> reads as "/c:/...".</summary>
    private static string LocalPath(Uri uri)
    {
        var path = uri.LocalPath;
        return OperatingSystem.IsWindows() && path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':'
            ? path[1..]
            : path;
    }

    // ═══════════════════════════════════════════════════════════════
    // Apps
    // ═══════════════════════════════════════════════════════════════

    internal IReadOnlyList<string> FindApps(CancellationToken ct)
    {
        if (!_options.ReadSystemApps)
            return [];

        var apps = new List<string>();
        foreach (var process in RunningProcessNames())
        {
            if (KnownApps.FromProcess(process) is { } name)
                apps.Add(name);
        }

        ct.ThrowIfCancellationRequested();
        foreach (var displayName in InstalledAppNames())
        {
            if (KnownApps.FromDisplayName(displayName) is { } name)
                apps.Add(name);
        }

        return apps.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxApps).ToList();
    }

    private static List<string> RunningProcessNames()
    {
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { return []; }

        var names = new List<string>(processes.Length);
        foreach (var process in processes)
        {
            try { names.Add(process.ProcessName); }
            catch { }
            finally { process.Dispose(); }
        }

        return names;
    }

    private IEnumerable<string> InstalledAppNames()
    {
        if (OperatingSystem.IsWindows())
            return WindowsInstalledAppNames();

        if (OperatingSystem.IsMacOS())
        {
            return new[] { "/Applications", Path.Combine(_options.HomeDirectory, "Applications") }
                .SelectMany(SafeDirectories)
                .Where(path => path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>();
        }

        return new[]
            {
                "/usr/share/applications",
                Path.Combine(_options.HomeDirectory, ".local", "share", "applications"),
                "/var/lib/flatpak/exports/share/applications",
                "/var/lib/snapd/desktop/applications",
            }
            .SelectMany(SafeFiles)
            .Where(path => path.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase))
            .Select(DesktopEntryName)
            .OfType<string>();
    }

    private static string? DesktopEntryName(string path)
    {
        try
        {
            return File.ReadLines(path)
                .Take(40)
                .FirstOrDefault(line => line.StartsWith("Name=", StringComparison.Ordinal))?["Name=".Length..];
        }
        catch { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static List<string> WindowsInstalledAppNames()
    {
        var names = new List<string>();
        (Microsoft.Win32.RegistryKey Hive, string Path)[] locations =
        [
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        ];

        foreach (var (hive, path) in locations)
        {
            try
            {
                using var uninstall = hive.OpenSubKey(path);
                if (uninstall is null)
                    continue;
                foreach (var keyName in uninstall.GetSubKeyNames())
                {
                    using var app = uninstall.OpenSubKey(keyName);
                    if (app?.GetValue("DisplayName") is string name && app.GetValue("SystemComponent") is not 1)
                        names.Add(name);
                }
            }
            catch { }
        }

        return names;
    }

    // ═══════════════════════════════════════════════════════════════
    // Sites
    // ═══════════════════════════════════════════════════════════════

    internal IReadOnlyList<string> FindSites(CancellationToken ct)
    {
        var since = _options.Now() - HistoryWindow;
        var weights = new Dictionary<string, long>(StringComparer.Ordinal);

        var chromiumSince = (since.UtcDateTime - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks / 10;
        foreach (var file in ChromiumHistoryFiles().Take(6))
        {
            ct.ThrowIfCancellationRequested();
            AccumulateHosts(file,
                "SELECT url, visit_count FROM urls WHERE last_visit_time >= $since ORDER BY visit_count DESC LIMIT 3000",
                chromiumSince, weights);
        }

        var firefoxSince = (since.UtcDateTime - DateTime.UnixEpoch).Ticks / 10;
        foreach (var file in FirefoxHistoryFiles().Take(3))
        {
            ct.ThrowIfCancellationRequested();
            AccumulateHosts(file,
                "SELECT url, visit_count FROM moz_places WHERE last_visit_date >= $since ORDER BY visit_count DESC LIMIT 3000",
                firefoxSince, weights);
        }

        return weights
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(MaxSites)
            .Select(pair => pair.Key)
            .ToList();
    }

    private static void AccumulateHosts(string historyFile, string sql, long since, Dictionary<string, long> weights)
    {
        // Browsers keep their history open, so read a private copy rather than the live file.
        var copy = CopyToTemp(historyFile, maxBytes: 256L * 1024 * 1024);
        if (copy is null)
            return;

        try
        {
            using var connection = OpenReadOnly(copy);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$since", since);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || NormalizeHost(reader.GetString(0)) is not { } host)
                    continue;
                var visits = reader.IsDBNull(1) ? 1 : Math.Max(1, reader.GetInt64(1));
                weights[host] = weights.GetValueOrDefault(host) + visits;
            }
        }
        catch { }
        finally
        {
            DeleteCopy(copy);
        }
    }

    private IEnumerable<string> ChromiumHistoryFiles()
    {
        if (_options.ChromiumHistoryFiles is { } files)
            return files;

        string[] userDataFolders;
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            userDataFolders =
            [
                Path.Combine(local, "Google", "Chrome", "User Data"),
                Path.Combine(local, "Microsoft", "Edge", "User Data"),
                Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"),
                Path.Combine(local, "Vivaldi", "User Data"),
            ];
        }
        else if (OperatingSystem.IsMacOS())
        {
            var support = Path.Combine(_options.HomeDirectory, "Library", "Application Support");
            userDataFolders =
            [
                Path.Combine(support, "Google", "Chrome"),
                Path.Combine(support, "Microsoft Edge"),
                Path.Combine(support, "BraveSoftware", "Brave-Browser"),
                Path.Combine(support, "Vivaldi"),
            ];
        }
        else
        {
            var config = Path.Combine(_options.HomeDirectory, ".config");
            userDataFolders =
            [
                Path.Combine(config, "google-chrome"),
                Path.Combine(config, "microsoft-edge"),
                Path.Combine(config, "BraveSoftware", "Brave-Browser"),
                Path.Combine(config, "vivaldi"),
            ];
        }

        return userDataFolders
            .SelectMany(SafeDirectories)
            .Where(profile =>
            {
                var name = Path.GetFileName(profile);
                return name == "Default" || name.StartsWith("Profile ", StringComparison.Ordinal);
            })
            .Select(profile => Path.Combine(profile, "History"))
            .Where(File.Exists);
    }

    private IEnumerable<string> FirefoxHistoryFiles()
    {
        if (_options.FirefoxHistoryFiles is { } files)
            return files;

        var profiles = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox", "Profiles")
            : OperatingSystem.IsMacOS()
                ? Path.Combine(_options.HomeDirectory, "Library", "Application Support", "Firefox", "Profiles")
                : Path.Combine(_options.HomeDirectory, ".mozilla", "firefox");
        return SafeDirectories(profiles)
            .Select(profile => Path.Combine(profile, "places.sqlite"))
            .Where(File.Exists);
    }

    private static readonly HashSet<string> NoiseHosts = new(StringComparer.Ordinal)
    {
        "google.com", "bing.com", "duckduckgo.com", "search.brave.com", "yahoo.com", "yandex.com", "yandex.ru",
        "msn.com", "ntp.msn.com", "baidu.com", "ecosia.org", "startpage.com", "t.co", "bit.ly",
    };

    private static readonly string[] NoiseHostPrefixes = ["login.", "auth.", "accounts.", "account.", "sso.", "signin.", "id.", "oauth."];

    private static readonly string[] SensitiveHostWords =
    [
        "porn", "xxx", "sex", "adult", "nsfw", "onlyfans", "xvideos", "xhamster", "hentai", "escort",
        "dating", "tinder", "bumble", "grindr", "hinge", "casino", "gambl", "betting", "poker",
    ];

    /// <summary>A visited URL's site, or null for searches, sign-in pages, local addresses and sensitive sites.</summary>
    internal static string? NormalizeHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.HostNameType is not UriHostNameType.Dns)
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        foreach (var prefix in new[] { "www.", "m.", "mobile." })
        {
            if (host.StartsWith(prefix, StringComparison.Ordinal))
                host = host[prefix.Length..];
        }

        if (!host.Contains('.')
            || host.EndsWith(".local", StringComparison.Ordinal)
            || host.EndsWith(".internal", StringComparison.Ordinal)
            || NoiseHosts.Contains(host)
            || NoiseHostPrefixes.Any(prefix => host.StartsWith(prefix, StringComparison.Ordinal))
            || SensitiveHostWords.Any(word => host.Contains(word, StringComparison.Ordinal)))
        {
            return null;
        }

        return host;
    }

    // ═══════════════════════════════════════════════════════════════
    // Recent files
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".doc", ".xlsx", ".xls", ".xlsm", ".pptx", ".ppt", ".odt", ".ods", ".odp", ".rtf", ".key", ".pages", ".numbers",
    };

    internal WorkspaceDocuments FindDocuments(CancellationToken ct)
    {
        var since = (_options.Now() - TimeSpan.FromDays(30)).UtcDateTime;
        var downloads = _options.DownloadsDirectory ?? Path.Combine(_options.HomeDirectory, "Downloads");
        var roots = DocumentRoots()
            .Append(downloads)
            .Where(Directory.Exists)
            .Distinct(PathComparer);

        int recent = 0, pdfs = 0, office = 0;
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            var scanned = 0;
            foreach (var directory in SafeDirectories(root).Take(40).Prepend(root))
            {
                foreach (var file in SafeFiles(directory))
                {
                    if (++scanned > 2000)
                        break;
                    var extension = Path.GetExtension(file);
                    if (!DocumentExtensions.Contains(extension))
                        continue;
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < since)
                            continue;
                    }
                    catch { continue; }

                    recent++;
                    if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                        pdfs++;
                    else if (OfficeExtensions.Contains(extension))
                        office++;
                }
            }
        }

        var downloadCount = Directory.Exists(downloads) ? SafeFiles(downloads).Take(5000).Count() : 0;
        return new WorkspaceDocuments(recent, pdfs, office, downloadCount);
    }

    // ═══════════════════════════════════════════════════════════════
    // File-system helpers
    // ═══════════════════════════════════════════════════════════════

    private IReadOnlyList<string> ExcludedRoots()
    {
        if (_options.ExcludedRoots is { } roots)
            return roots.Select(NormalizePath).ToList();

        var home = _options.HomeDirectory;
        return new[]
            {
                Path.GetTempPath(),
                Path.Combine(home, "AppData"),
                Path.Combine(home, "Library"),
                Path.Combine(home, ".cache"),
                Path.Combine(home, ".local"),
                Path.Combine(home, ".config"),
                Path.Combine(home, ".vscode"),
                Path.Combine(home, ".cursor"),
                Path.Combine(home, ".nuget"),
                Path.Combine(home, ".cargo"),
                Path.Combine(home, ".rustup"),
                Path.Combine(home, ".copilot"),
                Path.Combine(home, "go", "pkg"),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            }
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(NormalizePath)
            .ToList();
    }

    /// <summary>Folders that are too broad to be one project themselves, though projects may live inside them.</summary>
    private HashSet<string> UnsuitableFolders()
    {
        var home = _options.HomeDirectory;
        return new[]
            {
                home,
                Path.Combine(home, "Downloads"),
                Path.Combine(home, "Documents"),
                Path.Combine(home, "Desktop"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            }
            .Concat(_options.DocumentRoots ?? [])
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(NormalizePath)
            .ToHashSet(PathComparer);
    }

    private static bool IsExcluded(string folder, IReadOnlyList<string> excluded, HashSet<string> unsuitable)
    {
        var path = NormalizePath(folder);
        if (unsuitable.Contains(path) || (Path.GetPathRoot(path) is { Length: > 0 } pathRoot && PathComparer.Equals(path, NormalizePath(pathRoot))))
            return true;

        foreach (var root in excluded)
        {
            if (PathComparer.Equals(path, root) || path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
                return true;
        }

        return false;
    }

    internal static string NormalizePath(string path)
    {
        try { path = Path.GetFullPath(path); }
        catch { }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || path.Length > root.Length)
            path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (path.Length >= 2 && path[1] == ':')
            path = char.ToUpperInvariant(path[0]) + path[1..];
        return path;
    }

    private static string[] SafeDirectories(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.GetDirectories(folder, "*", FolderEnumeration) : []; }
        catch { return []; }
    }

    private static string[] SafeFiles(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.GetFiles(folder, "*", FolderEnumeration) : []; }
        catch { return []; }
    }

    private static List<string> SafeFileNames(string folder)
        => SafeFiles(folder).Take(300)
            .Concat(SafeDirectories(folder).Take(100).Where(path => path.EndsWith(".xcodeproj", StringComparison.OrdinalIgnoreCase)))
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToList();

    private static string ReadSmallText(string path) => ReadSmallText(path, 64 * 1024);

    private static string ReadSmallText(string path, int maxBytes)
    {
        try
        {
            if (!File.Exists(path) || IsCloudPlaceholder(path))
                return "";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(maxBytes, stream.Length)];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch { return ""; }
    }

    /// <summary>Online-only cloud files would download when read, so they are left alone.</summary>
    private static bool IsCloudPlaceholder(string path)
    {
        try
        {
            const FileAttributes recallOnDataAccess = (FileAttributes)0x00400000;
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Offline | recallOnDataAccess)) != 0;
        }
        catch { return true; }
    }

    private static string? ReadSqliteText(string file, string sql)
    {
        try { return QueryText(file, sql); }
        catch { }

        // The editor may hold a lock; read a private copy instead.
        var copy = CopyToTemp(file, maxBytes: 512L * 1024 * 1024);
        if (copy is null)
            return null;
        try { return QueryText(copy, sql); }
        catch { return null; }
        finally { DeleteCopy(copy); }
    }

    private static string? QueryText(string file, string sql)
    {
        using var connection = OpenReadOnly(file);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() switch
        {
            string text => text,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => null,
        };
    }

    private static SqliteConnection OpenReadOnly(string file)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string? CopyToTemp(string file, long maxBytes)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > maxBytes)
                return null;

            var copy = Path.Combine(Path.GetTempPath(), $"lumi-onboarding-{Guid.NewGuid():N}.db");
            using (var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var target = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                source.CopyTo(target);
            return copy;
        }
        catch { return null; }
    }

    /// <summary>Removes a private copy and the -wal/-shm files SQLite creates beside a WAL-mode copy.</summary>
    private static void DeleteCopy(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            try { File.Delete(file); }
            catch { }
        }
    }
}

/// <summary>Friendly names for well-known apps, so onboarding only ever mentions apps people recognize.</summary>
internal static class KnownApps
{
    private static readonly (string Pattern, string Name)[] DisplayNames =
    [
        ("Visual Studio Code", "VS Code"),
        ("Visual Studio Community", "Visual Studio"),
        ("Visual Studio Professional", "Visual Studio"),
        ("Visual Studio Enterprise", "Visual Studio"),
        ("JetBrains Rider", "Rider"),
        ("IntelliJ IDEA", "IntelliJ IDEA"),
        ("PyCharm", "PyCharm"),
        ("WebStorm", "WebStorm"),
        ("Android Studio", "Android Studio"),
        ("Cursor", "Cursor"),
        ("Windsurf", "Windsurf"),
        ("Xcode", "Xcode"),
        ("Docker Desktop", "Docker"),
        ("GitHub Desktop", "GitHub Desktop"),
        ("Postman", "Postman"),
        ("Unity Hub", "Unity"),
        ("Unreal Engine", "Unreal Engine"),
        ("Godot", "Godot"),
        ("Blender", "Blender"),
        ("Figma", "Figma"),
        ("Adobe Photoshop", "Photoshop"),
        ("Adobe Illustrator", "Illustrator"),
        ("Adobe Premiere Pro", "Premiere Pro"),
        ("Adobe After Effects", "After Effects"),
        ("Adobe Lightroom", "Lightroom"),
        ("Adobe InDesign", "InDesign"),
        ("Adobe Acrobat", "Acrobat"),
        ("DaVinci Resolve", "DaVinci Resolve"),
        ("Final Cut Pro", "Final Cut Pro"),
        ("Logic Pro", "Logic Pro"),
        ("Krita", "Krita"),
        ("GIMP", "GIMP"),
        ("Inkscape", "Inkscape"),
        ("Canva", "Canva"),
        ("Microsoft 365", "Microsoft 365"),
        ("Microsoft Office", "Microsoft Office"),
        ("Notion", "Notion"),
        ("Obsidian", "Obsidian"),
        ("Evernote", "Evernote"),
        ("Zotero", "Zotero"),
        ("MATLAB", "MATLAB"),
        ("RStudio", "RStudio"),
        ("Tableau", "Tableau"),
        ("Power BI", "Power BI"),
        ("Slack", "Slack"),
        ("Discord", "Discord"),
        ("Microsoft Teams", "Teams"),
        ("Zoom", "Zoom"),
        ("Telegram", "Telegram"),
        ("WhatsApp", "WhatsApp"),
        ("Signal", "Signal"),
        ("Spotify", "Spotify"),
        ("Steam", "Steam"),
        ("Epic Games Launcher", "Epic Games"),
        ("Battle.net", "Battle.net"),
        ("EA app", "EA app"),
        ("Ubisoft Connect", "Ubisoft Connect"),
        ("GOG GALAXY", "GOG Galaxy"),
        ("Riot Client", "Riot Games"),
        ("OBS Studio", "OBS Studio"),
        ("Audacity", "Audacity"),
        ("FL Studio", "FL Studio"),
        ("Ableton Live", "Ableton Live"),
        ("VLC media player", "VLC"),
        ("Google Chrome", "Chrome"),
        ("Mozilla Firefox", "Firefox"),
        ("Brave", "Brave"),
        ("Opera", "Opera"),
        ("1Password", "1Password"),
        ("Bitwarden", "Bitwarden"),
        ("Wireshark", "Wireshark"),
        ("Ollama", "Ollama"),
        ("LM Studio", "LM Studio"),
        ("Claude", "Claude"),
        ("ChatGPT", "ChatGPT"),
    ];

    private static readonly Dictionary<string, string> Processes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Code"] = "VS Code", ["Code - Insiders"] = "VS Code", ["devenv"] = "Visual Studio", ["rider64"] = "Rider",
        ["idea64"] = "IntelliJ IDEA", ["pycharm64"] = "PyCharm", ["webstorm64"] = "WebStorm", ["studio64"] = "Android Studio",
        ["Cursor"] = "Cursor", ["Windsurf"] = "Windsurf", ["Docker Desktop"] = "Docker", ["GitHubDesktop"] = "GitHub Desktop",
        ["Postman"] = "Postman", ["Unity"] = "Unity", ["blender"] = "Blender", ["Figma"] = "Figma", ["Photoshop"] = "Photoshop",
        ["Illustrator"] = "Illustrator", ["Adobe Premiere Pro"] = "Premiere Pro", ["AfterFX"] = "After Effects",
        ["Resolve"] = "DaVinci Resolve", ["WINWORD"] = "Word", ["EXCEL"] = "Excel", ["POWERPNT"] = "PowerPoint",
        ["OUTLOOK"] = "Outlook", ["olk"] = "Outlook", ["ONENOTE"] = "OneNote", ["Notion"] = "Notion", ["Obsidian"] = "Obsidian",
        ["slack"] = "Slack", ["Discord"] = "Discord", ["ms-teams"] = "Teams", ["Teams"] = "Teams", ["Zoom"] = "Zoom",
        ["Telegram"] = "Telegram", ["WhatsApp"] = "WhatsApp", ["Spotify"] = "Spotify", ["steam"] = "Steam",
        ["EpicGamesLauncher"] = "Epic Games", ["Battle.net"] = "Battle.net", ["obs64"] = "OBS Studio", ["chrome"] = "Chrome",
        ["firefox"] = "Firefox", ["brave"] = "Brave", ["opera"] = "Opera", ["1Password"] = "1Password", ["Bitwarden"] = "Bitwarden",
        ["Claude"] = "Claude", ["ChatGPT"] = "ChatGPT", ["ollama"] = "Ollama", ["LM Studio"] = "LM Studio",
    };

    internal static string? FromProcess(string processName)
        => Processes.TryGetValue(processName, out var name) ? name : null;

    internal static string? FromDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return null;

        foreach (var (pattern, name) in DisplayNames)
        {
            if (ContainsWord(displayName, pattern))
                return name;
        }

        return null;
    }

    private static bool ContainsWord(string text, string word)
    {
        var start = 0;
        while (true)
        {
            var index = text.IndexOf(word, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;

            var before = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var end = index + word.Length;
            var after = end >= text.Length || !char.IsLetter(text[end]);
            if (before && after)
                return true;
            start = index + 1;
        }
    }
}
