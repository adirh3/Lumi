using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Models;
using Lumi.Services;
using StrataSearch;

namespace Lumi.ViewModels;

public partial class McpServersViewModel : ObservableObject
{
    private readonly DataStore _dataStore;
    private CancellationTokenSource? _searchCts;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders = { { "Accept", "application/json" } }
    };

    /// <summary>Raised when MCP server config changes (add/edit/delete/toggle) so chat sessions can be invalidated.</summary>
    public event Action? McpConfigChanged;

    [ObservableProperty] private McpServer? _selectedServer;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isBrowsing;
    [ObservableProperty] private bool _isSearchingNpm;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editDescription = "";
    [ObservableProperty] private int _editServerTypeIndex; // 0 = local, 1 = remote
    [ObservableProperty] private string _editCommand = "";
    [ObservableProperty] private string _editArgs = "";
    [ObservableProperty] private string _editNpxPackage = ""; // npm package name for npx servers
    [ObservableProperty] private string _editServerArgs = ""; // user-configurable args (after package name)
    [ObservableProperty] private bool _isNpxCommand;
    [ObservableProperty] private string _editUrl = "";
    [ObservableProperty] private string _editEnvVars = ""; // KEY=VALUE per line
    [ObservableProperty] private string _editHeaders = ""; // KEY=VALUE per line
    [ObservableProperty] private bool _editIsEnabled = true;
    [ObservableProperty] private bool _editRunIsolated;
    [ObservableProperty] private string _searchQuery = "";

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

    [ObservableProperty] private string _browseSearchQuery = "";

    public ObservableCollection<McpServer> Servers { get; } = [];
    public ObservableCollection<McpCatalogEntry> CatalogEntries { get; } = [];

     public McpServersViewModel(DataStore dataStore)
     {
         _dataStore = dataStore;
         RefreshList();
         ShowFeaturedCatalog();
     }

     public void RefreshFromStore()
     {
         RefreshList();
         RefreshCatalogInstallState();

         if (SelectedServer is null)
             return;

         var selectedServer = _dataStore.Data.McpServers.FirstOrDefault(server => server.Id == SelectedServer.Id);
         if (selectedServer is null)
         {
             SelectedServer = null;
             IsEditing = false;
             return;
         }

         if (!ReferenceEquals(SelectedServer, selectedServer))
         {
             SelectedServer = selectedServer;
             return;
         }

         SyncEditorFromServer(selectedServer);
     }

    private void RefreshList()
    {
        Servers.Clear();
        var hasQuery = !string.IsNullOrWhiteSpace(SearchQuery);
        var items = hasQuery
            ? SearchPipeline.Rank(
                _dataStore.Data.McpServers,
                SearchQuery,
                static server =>
                [
                    SearchField.Primary(server.Name, 3.2),
                    new SearchField(server.Description, 1.7),
                    new SearchField(server.Command, 1.0),
                    new SearchField(server.Url, 0.8),
                    new SearchField(string.Join(' ', server.Args), 0.9),
                    new SearchField(string.Join(' ', server.Tools), 0.85)
                ],
                static server => new SearchSortMetadata(Text: server.Name))
            : _dataStore.Data.McpServers.OrderBy(server => server.Name).ToArray();

        foreach (var server in items)
            Servers.Add(server);
    }

    [RelayCommand]
    private void NewServer()
    {
        SelectedServer = null;
        EditName = "";
        EditDescription = "";
        EditServerTypeIndex = 0;
        EditCommand = "";
        EditArgs = "";
        EditNpxPackage = "";
        EditServerArgs = "";
        IsNpxCommand = false;
        EditUrl = "";
        EditEnvVars = "";
        EditHeaders = "";
        EditIsEnabled = true;
        EditRunIsolated = false;
        IsBrowsing = false;
        IsEditing = true;
    }

    [RelayCommand]
    private void EditServer(McpServer server)
    {
        SelectedServer = server;
    }

     partial void OnSelectedServerChanged(McpServer? value)
     {
         if (value is null) return;
         SyncEditorFromServer(value);

         IsBrowsing = false;
         IsEditing = true;
     }

     private void SyncEditorFromServer(McpServer server)
     {
         EditName = server.Name;
         EditDescription = server.Description;
         EditServerTypeIndex = server.ServerType == "remote" ? 1 : 0;
         EditCommand = server.Command;
         EditArgs = string.Join("\n", server.Args);
         EditUrl = server.Url;
         EditEnvVars = string.Join("\n", server.Env.Select(kv => $"{kv.Key}={kv.Value}"));
         EditHeaders = string.Join("\n", server.Headers.Select(kv => $"{kv.Key}={kv.Value}"));
         EditIsEnabled = server.IsEnabled;
         EditRunIsolated = server.RunIsolated;

         IsNpxCommand = server.Command is "npx" or "npx.cmd";
         if (IsNpxCommand)
         {
             var packageIndex = server.Args.FindIndex(arg => !arg.StartsWith('-'));
             if (packageIndex >= 0)
             {
                 EditNpxPackage = server.Args[packageIndex];
                 EditServerArgs = string.Join("\n", server.Args.Skip(packageIndex + 1));
             }
             else
             {
                 EditNpxPackage = "";
                 EditServerArgs = "";
             }
         }
         else
         {
             EditNpxPackage = "";
             EditServerArgs = "";
         }
     }

     private void RefreshCatalogInstallState()
     {
         var installedPackages = _dataStore.Data.McpServers
             .SelectMany(server => server.Args)
             .ToHashSet(StringComparer.OrdinalIgnoreCase);

         foreach (var entry in CatalogEntries)
             entry.IsInstalled = installedPackages.Contains(entry.NpmPackage);
     }

    partial void OnEditCommandChanged(string value)
    {
        IsNpxCommand = value is "npx" or "npx.cmd";
    }

    [RelayCommand]
    private void SaveServer()
    {
        if (string.IsNullOrWhiteSpace(EditName)) return;

        var isLocal = EditServerTypeIndex == 0;
        if (isLocal && string.IsNullOrWhiteSpace(EditCommand)) return;
        if (!isLocal && string.IsNullOrWhiteSpace(EditUrl)) return;

        List<string> args;
        if (isLocal && IsNpxCommand && !string.IsNullOrEmpty(EditNpxPackage))
        {
            // Reconstruct args: -y + package + user server args
            var serverArgs = EditServerArgs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            args = ["-y", EditNpxPackage, ..serverArgs];
        }
        else
        {
            args = EditArgs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        var envEntries = EditEnvVars.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var headerEntries = EditHeaders.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new LumiFeatureManager(_dataStore).ManageMcps(
            action: SelectedServer is null ? "create" : "update",
            identifier: SelectedServer?.Id.ToString(),
            name: EditName.Trim(),
            description: EditDescription.Trim(),
            serverType: isLocal ? "local" : "remote",
            command: isLocal ? EditCommand.Trim() : null,
            args: isLocal ? [..args] : [],
            url: isLocal ? null : EditUrl.Trim(),
            envEntries: isLocal ? envEntries : [],
            headerEntries: isLocal ? [] : headerEntries,
            isEnabled: EditIsEnabled,
            runIsolated: isLocal && EditRunIsolated);
        if (!result.DataChanged)
            return;

        _ = _dataStore.SaveAsync();
        IsEditing = false;
        RefreshList();
        McpConfigChanged?.Invoke();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
    }

    [RelayCommand]
    private void OpenNpmPage()
    {
        if (string.IsNullOrEmpty(EditNpxPackage)) return;
        var url = $"https://www.npmjs.com/package/{EditNpxPackage}";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* ignore if browser launch fails */ }
    }

    [RelayCommand]
    private void DeleteServer(McpServer server)
    {
        var result = new LumiFeatureManager(_dataStore).ManageMcps("delete", identifier: server.Id.ToString());
        if (!result.DataChanged)
            return;

        _ = _dataStore.SaveAsync();
        if (SelectedServer == server)
        {
            SelectedServer = null;
            IsEditing = false;
        }
        RefreshList();
        McpConfigChanged?.Invoke();
    }

    [RelayCommand]
    private void ToggleServer(McpServer server)
    {
        server.IsEnabled = !server.IsEnabled;
        _ = _dataStore.SaveAsync();
        RefreshList();
        McpConfigChanged?.Invoke();
    }

    partial void OnSearchQueryChanged(string value) => RefreshList();

    partial void OnBrowseSearchQueryChanged(string value)
    {
        _searchCts?.Cancel();
        if (string.IsNullOrWhiteSpace(value))
        {
            ShowFeaturedCatalog();
            return;
        }

        // Immediately filter featured catalog entries
        FilterFeaturedCatalog(value);

        // Also search npm after a debounce delay for broader results
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        _ = Task.Delay(400, token).ContinueWith(_ =>
        {
            if (!token.IsCancellationRequested)
                Dispatcher.UIThread.Post(() => _ = SearchNpmAsync(value, token));
        }, TaskScheduler.Default);
    }

    [RelayCommand]
    private void BrowseCatalog()
    {
        IsBrowsing = true;
        IsEditing = false;
        BrowseSearchQuery = "";
        ShowFeaturedCatalog();
    }

    [RelayCommand]
    private void InstallCatalogEntry(McpCatalogEntry entry)
    {
        // Check if already installed by npm package name
        if (_dataStore.Data.McpServers.Any(s =>
            s.Args.Any(a => a.Equals(entry.NpmPackage, StringComparison.OrdinalIgnoreCase))))
            return;

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        var server = new McpServer
        {
            Name = entry.Name,
            Description = entry.Description,
            ServerType = "local",
            Command = isWindows ? "npx.cmd" : "npx",
            Args = ["-y", entry.NpmPackage],
            IsEnabled = true
        };

        // Auto-populate required env vars with placeholder values
        if (entry.RequiredEnvVars.Count > 0)
        {
            foreach (var (key, hint) in entry.RequiredEnvVars)
                server.Env[key] = $"YOUR_{key}_HERE";
        }

        _dataStore.Data.McpServers.Add(server);
        _ = _dataStore.SaveAsync();
        entry.IsInstalled = true;
        RefreshList();
        McpConfigChanged?.Invoke();

        // If env vars are required, open the editor so user fills them in
        if (entry.RequiredEnvVars.Count > 0)
        {
            IsBrowsing = false;
            EditServer(server);
        }
    }

    private void ShowFeaturedCatalog()
    {
        CatalogEntries.Clear();
        var installedPackages = _dataStore.Data.McpServers
            .SelectMany(s => s.Args)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in FeaturedCatalog)
        {
            entry.IsInstalled = installedPackages.Contains(entry.NpmPackage);
            CatalogEntries.Add(entry);
        }
    }

    private void FilterFeaturedCatalog(string query)
    {
        CatalogEntries.Clear();
        var installedPackages = _dataStore.Data.McpServers
            .SelectMany(s => s.Args)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in SearchPipeline.Rank(
                     FeaturedCatalog.OrderByDescending(static entry => entry.MonthlyDownloads),
                     query,
                     static entry =>
                     [
                         SearchField.Primary(entry.Name, 3.2),
                         new SearchField(entry.Description, 1.7),
                         new SearchField(entry.NpmPackage, 1.2),
                         new SearchField(entry.Category, 1.0)
                     ]))
        {
            entry.IsInstalled = installedPackages.Contains(entry.NpmPackage);
            CatalogEntries.Add(entry);
        }
    }

    private async Task SearchNpmAsync(string query, CancellationToken ct)
    {
        IsSearchingNpm = true;
        try
        {
            var url = $"https://registry.npmjs.org/-/v1/search?text=keywords:mcp-server+{Uri.EscapeDataString(query)}&size=50";
            var json = await Http.GetStringAsync(url, ct);
            if (ct.IsCancellationRequested) return;

            using var doc = JsonDocument.Parse(json);
            var objects = doc.RootElement.GetProperty("objects");

            var installedPackages = _dataStore.Data.McpServers
                .SelectMany(s => s.Args)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Keep track of packages already shown from featured catalog
            var existingPackages = CatalogEntries.Select(e => e.NpmPackage).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var candidates = new List<McpCatalogEntry>();
            foreach (var obj in objects.EnumerateArray())
            {
                var pkg = obj.GetProperty("package");
                var name = pkg.GetProperty("name").GetString() ?? "";
                var desc = pkg.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "";
                var downloads = obj.TryGetProperty("downloads", out var dlProp)
                    && dlProp.TryGetProperty("monthly", out var monthlyProp)
                    ? monthlyProp.GetInt64() : 0;

                // Skip SDK/inspector/non-server packages and duplicates of featured entries
                if (name.EndsWith("/sdk") || name.EndsWith("/inspector") || name.EndsWith("/ext-apps"))
                    continue;
                if (existingPackages.Contains(name))
                    continue;

                var icon = InferIcon(name, desc);
                var category = InferCategory(name, desc);
                var displayName = FormatPackageName(name);
                var entry = new McpCatalogEntry(displayName, desc, name, icon, category, downloads);
                entry.IsInstalled = installedPackages.Contains(name);
                candidates.Add(entry);
            }

            foreach (var entry in SearchPipeline.Rank(
                         candidates.OrderByDescending(static entry => entry.MonthlyDownloads),
                         query,
                         static entry =>
                         [
                             SearchField.Primary(entry.Name, 3.2),
                             new SearchField(entry.Description, 1.7),
                             new SearchField(entry.NpmPackage, 1.2),
                             new SearchField(entry.Category, 1.0)
                         ]))
            {
                CatalogEntries.Add(entry);
            }
        }
        catch (OperationCanceledException) { /* debounce cancelled */ }
        catch { /* network error — silently show empty results */ }
        finally
        {
            IsSearchingNpm = false;
        }
    }

    private static string FormatPackageName(string npmName)
    {
        // "@modelcontextprotocol/server-memory" → "Memory"
        // "playwright-mcp-server" → "Playwright"
        var name = npmName;
        var slashIdx = name.LastIndexOf('/');
        if (slashIdx >= 0) name = name[(slashIdx + 1)..];
        name = name.Replace("server-", "").Replace("-server", "").Replace("-mcp", "").Replace("mcp-", "");
        if (string.IsNullOrWhiteSpace(name)) name = npmName;
        // Title case
        return string.Join(' ', name.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    private static string InferIcon(string name, string desc)
    {
        var combined = (name + " " + desc).ToLowerInvariant();
        if (combined.Contains("memory") || combined.Contains("knowledge")) return "🧠";
        if (combined.Contains("filesystem") || combined.Contains("file")) return "📁";
        if (combined.Contains("github")) return "🐙";
        if (combined.Contains("git")) return "📋";
        if (combined.Contains("search")) return "🔍";
        if (combined.Contains("fetch") || combined.Contains("web") || combined.Contains("http")) return "🌐";
        if (combined.Contains("browser") || combined.Contains("puppeteer") || combined.Contains("playwright")) return "🎭";
        if (combined.Contains("postgres") || combined.Contains("mysql") || combined.Contains("database") || combined.Contains("sql")) return "🐘";
        if (combined.Contains("slack") || combined.Contains("discord") || combined.Contains("chat") || combined.Contains("message")) return "💬";
        if (combined.Contains("map") || combined.Contains("geo") || combined.Contains("location")) return "🗺️";
        if (combined.Contains("think") || combined.Contains("reason")) return "💭";
        if (combined.Contains("docker") || combined.Contains("container")) return "🐳";
        if (combined.Contains("video") || combined.Contains("youtube") || combined.Contains("media")) return "📺";
        if (combined.Contains("mail") || combined.Contains("email")) return "📧";
        if (combined.Contains("calendar") || combined.Contains("time")) return "📅";
        if (combined.Contains("pdf") || combined.Contains("document")) return "📄";
        if (combined.Contains("image") || combined.Contains("photo")) return "🖼️";
        if (combined.Contains("test")) return "🧪";
        return "🔌";
    }

    private static string InferCategory(string name, string desc)
    {
        var combined = (name + " " + desc).ToLowerInvariant();
        if (combined.Contains("browser") || combined.Contains("puppeteer") || combined.Contains("playwright")) return "Browser";
        if (combined.Contains("database") || combined.Contains("postgres") || combined.Contains("mysql") || combined.Contains("sqlite") || combined.Contains("sql")) return "Database";
        if (combined.Contains("search")) return "Search";
        if (combined.Contains("github") || combined.Contains("git") || combined.Contains("code")) return "Development";
        if (combined.Contains("file") || combined.Contains("filesystem")) return "Files";
        if (combined.Contains("docker") || combined.Contains("deploy") || combined.Contains("kubernetes")) return "DevOps";
        if (combined.Contains("slack") || combined.Contains("discord") || combined.Contains("email") || combined.Contains("mail")) return "Communication";
        if (combined.Contains("memory") || combined.Contains("knowledge")) return "Knowledge";
        if (combined.Contains("fetch") || combined.Contains("web") || combined.Contains("http") || combined.Contains("crawl")) return "Web";
        return "Tool";
    }

    private static readonly McpCatalogEntry[] FeaturedCatalog =
    [
        new("Memory", "Persistent knowledge graph for long-term memory across sessions",
            "@modelcontextprotocol/server-memory", "🧠", "Knowledge"),
        new("Filesystem", "Read, write, and manage files and directories",
            "@modelcontextprotocol/server-filesystem", "📁", "Files"),
        new("GitHub", "Manage repositories, issues, pull requests, and more",
            "@modelcontextprotocol/server-github", "🐙", "Development",
            requiredEnvVars: new() { ["GITHUB_PERSONAL_ACCESS_TOKEN"] = "Personal access token from github.com/settings/tokens" }),
        new("Git", "Git operations — status, diff, log, branch, commit",
            "@modelcontextprotocol/server-git", "📋", "Development"),
        new("Brave Search", "Web search powered by Brave Search API",
            "@modelcontextprotocol/server-brave-search", "🔍", "Search",
            requiredEnvVars: new() { ["BRAVE_API_KEY"] = "API key from brave.com/search/api" }),
        new("Fetch", "Fetch and extract content from any URL",
            "@modelcontextprotocol/server-fetch", "🌐", "Web"),
        new("Puppeteer", "Browser automation — navigate, click, screenshot, scrape",
            "@modelcontextprotocol/server-puppeteer", "🎭", "Browser"),
        new("PostgreSQL", "Query and inspect PostgreSQL databases",
            "@modelcontextprotocol/server-postgres", "🐘", "Database"),
        new("SQLite", "Query and manage SQLite databases",
            "@modelcontextprotocol/server-sqlite", "💾", "Database"),
        new("Slack", "Read and send Slack messages, manage channels",
            "@modelcontextprotocol/server-slack", "💬", "Communication",
            requiredEnvVars: new() { ["SLACK_BOT_TOKEN"] = "Bot token from api.slack.com/apps", ["SLACK_TEAM_ID"] = "Team/workspace ID" }),
        new("Google Maps", "Geocoding, directions, and place search",
            "@modelcontextprotocol/server-google-maps", "🗺️", "Location",
            requiredEnvVars: new() { ["GOOGLE_MAPS_API_KEY"] = "API key from console.cloud.google.com" }),
        new("Sequential Thinking", "Dynamic problem-solving through thought sequences",
            "@modelcontextprotocol/server-sequential-thinking", "💭", "Reasoning"),
        new("Playwright", "Browser automation with Playwright — test, scrape, interact",
            "@executeautomation/playwright-mcp-server", "🎪", "Browser"),
        new("Everything", "Reference server with all MCP capabilities for testing",
            "@modelcontextprotocol/server-everything", "🧪", "Testing"),
        new("YouTube Transcript", "Fetch transcripts and captions from YouTube videos",
            "@kimtaeyoon83/mcp-server-youtube-transcript", "📺", "Media"),
        new("Docker", "Manage Docker containers, images, volumes, and networks",
            "@modelcontextprotocol/server-docker", "🐳", "DevOps"),
        new("PDF", "Load and extract text from PDF files with pagination",
            "@modelcontextprotocol/server-pdf", "📄", "Documents"),
        new("Time", "Current time and timezone conversions",
            "@modelcontextprotocol/server-time", "⏰", "Utilities"),
        new("Notion", "Read and manage Notion pages, databases, and content",
            "notion-mcp-server", "📝", "Productivity",
            requiredEnvVars: new() { ["NOTION_API_KEY"] = "Integration token from notion.so/my-integrations" }),
        new("Linear", "Manage Linear issues, projects, and workflows",
            "linear-mcp-server", "🎯", "Project Management",
            requiredEnvVars: new() { ["LINEAR_API_KEY"] = "API key from linear.app/settings/api" }),
        new("Todoist", "Manage tasks and projects in Todoist",
            "todoist-mcp-server", "✅", "Productivity",
            requiredEnvVars: new() { ["TODOIST_API_TOKEN"] = "API token from todoist.com/app/settings/integrations/developer" }),
        new("Sentry", "Query and manage Sentry error monitoring",
            "@sentry/mcp-server-sentry", "🐛", "Monitoring",
            requiredEnvVars: new() { ["SENTRY_AUTH_TOKEN"] = "Auth token from sentry.io/settings/auth-tokens" }),
        new("Cloudflare", "Manage Cloudflare Workers, KV, R2, and DNS",
            "@nicepkg/cloudflare-mcp-server", "☁️", "Cloud",
            requiredEnvVars: new() { ["CLOUDFLARE_API_TOKEN"] = "API token from dash.cloudflare.com/profile/api-tokens" }),
        new("Supabase", "Manage Supabase projects, database, and auth",
            "supabase-mcp-server", "⚡", "Database",
            requiredEnvVars: new() { ["SUPABASE_ACCESS_TOKEN"] = "Access token from app.supabase.com/account/tokens" }),
        new("Stripe", "Interact with Stripe payments, customers, and subscriptions",
            "@stripe/mcp", "💳", "Payments",
            requiredEnvVars: new() { ["STRIPE_API_KEY"] = "Secret key from dashboard.stripe.com/apikeys" }),
        new("AWS", "Manage AWS resources — S3, Lambda, EC2, and more",
            "aws-mcp-server", "🟧", "Cloud",
            requiredEnvVars: new() { ["AWS_ACCESS_KEY_ID"] = "Access key ID", ["AWS_SECRET_ACCESS_KEY"] = "Secret access key" }),
        new("Figma", "Access Figma designs, components, and styles",
            "figma-mcp", "🎨", "Design",
            requiredEnvVars: new() { ["FIGMA_PERSONAL_ACCESS_TOKEN"] = "Token from figma.com/developers/api" }),
        new("Twilio", "Send SMS, make calls, and manage Twilio services",
            "twilio-mcp-server", "📱", "Communication",
            requiredEnvVars: new() { ["TWILIO_ACCOUNT_SID"] = "Account SID", ["TWILIO_AUTH_TOKEN"] = "Auth token" }),
        new("Obsidian", "Read and manage Obsidian vault notes and metadata",
            "obsidian-mcp-server", "🗄️", "Knowledge"),
        new("Jira", "Manage Jira issues, sprints, and projects",
            "jira-mcp-server", "📊", "Project Management",
            requiredEnvVars: new() { ["JIRA_API_TOKEN"] = "API token from id.atlassian.com/manage-profile/security/api-tokens",
                ["JIRA_BASE_URL"] = "Your Jira instance URL (e.g. https://yourteam.atlassian.net)",
                ["JIRA_EMAIL"] = "Your Atlassian account email" }),
    ];

    private static Dictionary<string, string> ParseKeyValueLines(string text)
    {
        var dict = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(text)) return dict;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eqIdx = line.IndexOf('=');
            if (eqIdx > 0)
            {
                var key = line[..eqIdx].Trim();
                var val = line[(eqIdx + 1)..].Trim();
                if (!string.IsNullOrEmpty(key))
                    dict[key] = val;
            }
        }
        return dict;
    }
}

public partial class McpCatalogEntry : ObservableObject
{
    public string Name { get; }
    public string Description { get; }
    public string NpmPackage { get; }
    public string Icon { get; }
    public string Category { get; }
    public long MonthlyDownloads { get; }
    /// <summary>Environment variables required for this MCP server (key=description).</summary>
    public Dictionary<string, string> RequiredEnvVars { get; }
    [ObservableProperty] private bool _isInstalled;

    public McpCatalogEntry(string name, string description, string npmPackage, string icon, string category,
        long monthlyDownloads = 0, Dictionary<string, string>? requiredEnvVars = null)
    {
        Name = name;
        Description = description;
        NpmPackage = npmPackage;
        Icon = icon;
        Category = category;
        MonthlyDownloads = monthlyDownloads;
        RequiredEnvVars = requiredEnvVars ?? [];
    }

    public bool HasRequiredEnvVars => RequiredEnvVars.Count > 0;
    public string EnvVarHint => RequiredEnvVars.Count > 0
        ? string.Join(", ", RequiredEnvVars.Keys)
        : "";

    public string DownloadsDisplay => MonthlyDownloads switch
    {
        >= 1_000_000 => $"{MonthlyDownloads / 1_000_000.0:F1}M/mo",
        >= 1_000 => $"{MonthlyDownloads / 1_000.0:F1}K/mo",
        > 0 => $"{MonthlyDownloads}/mo",
        _ => ""
    };
}
