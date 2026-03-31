using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHub.Copilot.SDK;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Microsoft.Extensions.AI;

namespace Lumi.ViewModels;

public enum DiscoveryCategoryStatus { Pending, Scanning, Complete, Skipped }

public enum InsightKind { Discovery, Memory, AgentText }

public partial class InsightItem : ObservableObject
{
    [ObservableProperty] private string _icon = "💡";
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private InsightKind _kind = InsightKind.Discovery;

    public bool IsMemory => Kind == InsightKind.Memory;
    public bool IsDiscovery => Kind == InsightKind.Discovery;
    public bool IsAgentText => Kind == InsightKind.AgentText;
}

public partial class DiscoveryCategoryViewModel : ObservableObject
{
    [ObservableProperty] private string _icon = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private DiscoveryCategoryStatus _status = DiscoveryCategoryStatus.Pending;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private int _findingsCount;

    public bool IsPending => Status == DiscoveryCategoryStatus.Pending;
    public bool IsScanning => Status == DiscoveryCategoryStatus.Scanning;
    public bool IsComplete => Status == DiscoveryCategoryStatus.Complete;
    public bool IsSkipped => Status == DiscoveryCategoryStatus.Skipped;

    partial void OnStatusChanged(DiscoveryCategoryStatus value)
    {
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(IsSkipped));
    }
}

/// <summary>
/// Manages the 5-step onboarding flow: Welcome → Learn → Scan → Meet → Ready.
/// Discovery runs in two phases across steps 2 and 3:
///   Phase 1 (Scan, Step 2): Fast local scans with per-category loading indicators
///   Phase 2 (Meet, Step 3): Copilot SDK agent analyzes scan results, digs deeper, asks questions, creates memories
/// </summary>
public partial class OnboardingViewModel : ObservableObject
{
    private readonly DataStore _dataStore;
    private readonly CopilotService _copilotService;
    private CancellationTokenSource? _discoveryCts;
    private TaskCompletionSource<string>? _pendingQuestionTcs;

    // ── Step tracking ──
    [ObservableProperty] private int _currentStep; // 0=Welcome, 1=Learn, 2=Scan, 3=Meet, 4=Ready

    // ── Step 0: Basic info ──
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private int _sexIndex;
    [ObservableProperty] private int _languageIndex;
    [ObservableProperty] private bool _isDarkTheme = true;

    /// <summary>Shared GitHub login ViewModel — set by MainViewModel.</summary>
    private GitHubLoginViewModel? _loginVM;
    public GitHubLoginViewModel? LoginVM
    {
        get => _loginVM;
        set
        {
            if (_loginVM is not null)
                _loginVM.AuthenticationChanged -= OnAuthChanged;
            _loginVM = value;
            if (_loginVM is not null)
                _loginVM.AuthenticationChanged += OnAuthChanged;
            OnPropertyChanged(nameof(IsAuthenticated));
        }
    }

    private void OnAuthChanged(bool _) => OnPropertyChanged(nameof(IsAuthenticated));

    /// <summary>Whether the user is authenticated (delegates to LoginVM).</summary>
    public bool IsAuthenticated => LoginVM?.IsAuthenticated == true;

    // ── Step 2+3: Discovery ──
    [ObservableProperty] private bool _isDiscovering;
    [ObservableProperty] private bool _isScanPhase;     // true = deterministic scan, false = agent phase
    [ObservableProperty] private bool _isAgentPhase;
    [ObservableProperty] private string _agentOutput = "";
    [ObservableProperty] private string _currentToolName = "";
    [ObservableProperty] private bool _hasActiveToolCall;
    [ObservableProperty] private string _pendingQuestion = "";
    [ObservableProperty] private string _pendingQuestionOptions = "";
    [ObservableProperty] private bool _hasPendingQuestion;
    [ObservableProperty] private bool _questionAnswered;
    [ObservableProperty] private string _scanPhaseStatus = "";

    // ── Step 3: Meet — single-card carousel ──
    [ObservableProperty] private string _cardIcon = "🔍";
    [ObservableProperty] private string _cardTitle = "";
    [ObservableProperty] private string _cardBody = "";
    [ObservableProperty] private bool _hasCard;
    [ObservableProperty] private int _cardIndex;
    [ObservableProperty] private int _cardTotal;
    [ObservableProperty] private string _cardProgress = "";

    /// <summary>Show the "exploring" placeholder when no card and no question are visible.</summary>
    public bool ShowPlaceholder => !HasCard && !HasPendingQuestion && IsDiscovering && AgentOutput.Length == 0;

    /// <summary>Show the streaming card when there's live agent text and no static card.</summary>
    public bool ShowStreaming => !HasCard && AgentOutput.Length > 0;

    partial void OnHasCardChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(ShowStreaming));
    }
    partial void OnHasPendingQuestionChanged(bool value) => OnPropertyChanged(nameof(ShowPlaceholder));
    partial void OnIsDiscoveringChanged(bool value) => OnPropertyChanged(nameof(ShowPlaceholder));
    partial void OnAgentOutputChanged(string value)
    {
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(ShowStreaming));
    }

    // ── Step 3: Summary ──
    [ObservableProperty] private int _totalDiscoveries;
    [ObservableProperty] private bool _learningWasSkipped;
    [ObservableProperty] private int _memoriesCreated;

    // Scan results collected in Phase 1, fed to agent as context in Phase 2
    private readonly Dictionary<string, string> _scanResults = new();

    public ObservableCollection<DiscoveryCategoryViewModel> Categories { get; } = [];
    public ObservableCollection<InsightItem> Insights { get; } = [];

    public event Action? OnboardingCompleted;
    public event Action<bool>? ThemeChanged;
    /// <summary>Raised when the agent asks a question. Payload: (question, comma-separated options).</summary>
    public event Action<string, string>? QuestionAsked;

    public bool CanContinueToLearn => !string.IsNullOrWhiteSpace(UserName);

    public OnboardingViewModel(DataStore dataStore, CopilotService copilotService)
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
        _isDarkTheme = dataStore.Data.Settings.IsDarkTheme;

        Categories.Add(new DiscoveryCategoryViewModel
        {
            Icon = "🖥️", Name = Loc.Onboarding_LearnFeatureApps,
            Description = Loc.Onboarding_LearnFeatureAppsDesc
        });
        Categories.Add(new DiscoveryCategoryViewModel
        {
            Icon = "🌐", Name = Loc.Onboarding_LearnFeatureBrowsing,
            Description = Loc.Onboarding_LearnFeatureBrowsingDesc
        });
        Categories.Add(new DiscoveryCategoryViewModel
        {
            Icon = "📁", Name = Loc.Onboarding_LearnFeatureFiles,
            Description = Loc.Onboarding_LearnFeatureFilesDesc
        });
        Categories.Add(new DiscoveryCategoryViewModel
        {
            Icon = "🎯", Name = Loc.Onboarding_LearnFeatureProfile,
            Description = Loc.Onboarding_LearnFeatureProfileDesc
        });
        Categories.Add(new DiscoveryCategoryViewModel
        {
            Icon = "🔍", Name = "Browser History",
            Description = "Your most visited sites"
        });
    }

    partial void OnUserNameChanged(string value) => OnPropertyChanged(nameof(CanContinueToLearn));
    partial void OnIsDarkThemeChanged(bool value) => ThemeChanged?.Invoke(value);

    // ═══════════════════════════════════════════════════════════════
    // Step navigation
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    private void ContinueToLearn()
    {
        if (!CanContinueToLearn) return;
        CurrentStep = 1;
    }

    [RelayCommand]
    private async Task AcceptLearning()
    {
        CurrentStep = 2;
        await RunDiscoveryAsync();
    }

    [RelayCommand]
    private void SkipLearning()
    {
        LearningWasSkipped = true;
        CurrentStep = 4;
    }

    [RelayCommand]
    private void SkipDiscovery()
    {
        _discoveryCts?.Cancel();
    }

    [RelayCommand]
    private async Task FinishOnboarding()
    {
        var settings = _dataStore.Data.Settings;
        settings.UserName = UserName.Trim();
        settings.UserSex = SexIndex switch { 0 => "male", 1 => "female", _ => null };
        if (LanguageIndex >= 0 && LanguageIndex < Loc.AvailableLanguages.Length)
            settings.Language = Loc.AvailableLanguages[LanguageIndex].Code;
        settings.IsDarkTheme = IsDarkTheme;
        settings.IsOnboarded = true;
        await _dataStore.SaveAsync();
        OnboardingCompleted?.Invoke();
    }

    /// <summary>Called by the View when user answers a question card.</summary>
    public void SubmitQuestionAnswer(string answer)
    {
        QuestionAnswered = true;
        _pendingQuestionTcs?.TrySetResult(answer);
    }

    // ═══════════════════════════════════════════════════════════════
    // Two-phase discovery
    // ═══════════════════════════════════════════════════════════════

    private async Task RunDiscoveryAsync()
    {
        _discoveryCts?.Dispose();
        _discoveryCts = new CancellationTokenSource();
        var ct = _discoveryCts.Token;
        IsDiscovering = true;
        var memoriesAtStart = _dataStore.Data.Memories.Count;

        try
        {
            // ── Phase 1: Deterministic scans with per-category loading ──
            IsScanPhase = true;
            ScanPhaseStatus = Loc.Onboarding_ScanningYourPC ?? "Scanning your PC…";
            await RunDeterministicScansAsync(ct);
            IsScanPhase = false;

            ct.ThrowIfCancellationRequested();

            // ── Transition to Meet step ──
            CurrentStep = 3;

            // ── Phase 2: Agent analyzes results, digs deeper, asks questions ──
            IsAgentPhase = true;
            CardIndex = 0;
            CardTotal = 12; // Estimated: ~5 investigation + ~5 memories + ~3 questions
            ScanPhaseStatus = Loc.Onboarding_AgentAnalyzing ?? "Lumi is getting to know you…";
            await RunAgentPhaseAsync(ct);
        }
        catch (OperationCanceledException)
        {
            foreach (var cat in Categories)
                if (cat.Status is DiscoveryCategoryStatus.Pending or DiscoveryCategoryStatus.Scanning)
                    cat.Status = DiscoveryCategoryStatus.Skipped;
        }
        catch (Exception ex)
        {
            // Agent failed — create basic memories from scan data
            Dispatcher.UIThread.Post(() =>
                AgentOutput = $"I couldn't complete the deep analysis, but I saved what I found! ({ex.Message})");
            CreateFallbackMemories();
        }
        finally
        {
            _discoveryCts?.Dispose();
            _discoveryCts = null;
            IsDiscovering = false;
            IsScanPhase = false;
            IsAgentPhase = false;
            HasActiveToolCall = false;
            MemoriesCreated = _dataStore.Data.Memories.Count - memoriesAtStart;
            TotalDiscoveries = Categories.Sum(c => c.FindingsCount);
            CurrentStep = 4;
        }
    }

    // ── Phase 1: Fast deterministic scans ──

    private async Task RunDeterministicScansAsync(CancellationToken ct)
    {
        var scans = new (int index, string key, Func<CancellationToken, Task<string>> scan)[]
        {
            (0, "apps", ScanInstalledAppsAsync),
            (1, "bookmarks", ScanBookmarksAsync),
            (2, "files", ScanRecentFilesAsync),
            (3, "devtools", ScanDevEnvironmentAsync),
            (4, "browser_history", ScanBrowserHistoryAsync),
        };

        foreach (var (index, key, scan) in scans)
        {
            ct.ThrowIfCancellationRequested();
            Dispatcher.UIThread.Post(() => Categories[index].Status = DiscoveryCategoryStatus.Scanning);

            try
            {
                var result = await scan(ct);
                _scanResults[key] = result;
                Dispatcher.UIThread.Post(() => Categories[index].Status = DiscoveryCategoryStatus.Complete);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                _scanResults[key] = "Scan failed.";
                Dispatcher.UIThread.Post(() => Categories[index].Status = DiscoveryCategoryStatus.Skipped);
            }
        }
    }

    // ── Phase 2: Agent with scan context + investigation tools ──

    private async Task RunAgentPhaseAsync(CancellationToken ct)
    {
        if (!_copilotService.IsConnected)
            await _copilotService.ConnectAsync(ct);

        var tools = BuildAgentTools(ct);
        var session = await _copilotService.CreateSessionAsync(
            CopilotService.BuildLightweightConfig(
                BuildAgentSystemPrompt(), model: null, tools: tools), ct);

        IDisposable? subscription = null;
        try
        {
            var assistantText = "";
            var toolNameByCallId = new Dictionary<string, string>(StringComparer.Ordinal);
            subscription = session.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageDeltaEvent delta:
                        assistantText += delta.Data?.DeltaContent ?? "";
                        Dispatcher.UIThread.Post(() => AgentOutput = assistantText);
                        break;

                    case ToolExecutionStartEvent toolStart:
                    {
                        var name = toolStart.Data?.ToolName ?? "";
                        var id = toolStart.Data?.ToolCallId;
                        if (!string.IsNullOrEmpty(id))
                            toolNameByCallId[id] = name;
                        // Flush pending assistant text as a card
                        var pendingText = assistantText.Trim();
                        if (pendingText.Length > 0)
                        {
                            ShowCard("", "", pendingText);
                            assistantText = "";
                        }
                        var friendly = GetFriendlyToolName(name);
                        Dispatcher.UIThread.Post(() =>
                        {
                            CurrentToolName = friendly;
                            HasActiveToolCall = true;
                        });
                        break;
                    }

                    case ToolExecutionCompleteEvent toolEnd:
                    {
                        Dispatcher.UIThread.Post(() => HasActiveToolCall = false);
                        break;
                    }

                    case SessionIdleEvent:
                        // Flush remaining assistant text
                        var remainingText = assistantText.Trim();
                        if (remainingText.Length > 0)
                        {
                            ShowCard("", "", remainingText);
                            assistantText = "";
                        }
                        break;
                }
            });

            var prompt = BuildAgentPromptWithContext();
            await session.SendAndWaitAsync(
                new MessageOptions { Prompt = prompt },
                TimeSpan.FromMinutes(5), ct);
        }
        finally
        {
            subscription?.Dispose();
            await session.DisposeAsync();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Agent tools — only intelligence tasks, not deterministic scans
    // ═══════════════════════════════════════════════════════════════

    private List<AIFunction> BuildAgentTools(CancellationToken ct)
    {
        return
        [
            // Save a memory
            AIFunctionFactory.Create(
                async ([Description("Brief label (e.g. Favorite IDE, Profession)")] string key,
                       [Description("Full memory text with details")] string content,
                       [Description("Category: Personal, Preferences, Work, Technical, Interests")] string? category) =>
                {
                    return await SaveMemoryAsync(key, content, category ?? "General");
                },
                "save_memory",
                "Save a persistent memory about the user. Use this to record important facts and insights."),

            // Ask the user a question
            AIFunctionFactory.Create(
                async ([Description("A clear, engaging question to ask the user")] string question,
                       [Description("Comma-separated list of suggested answer options (3-5 options)")] string options) =>
                {
                    return await AskUserQuestionAsync(question, options, ct);
                },
                "ask_user",
                "Ask the user a question with clickable answer options. The user sees a visual card with buttons. Use this to learn about their preferences, work, and interests."),

            // Run a command to dig deeper
            AIFunctionFactory.Create(
                async ([Description("PowerShell command to execute (must be read-only, no mutations)")] string command) =>
                {
                    return await RunSafeCommandAsync(command, ct);
                },
                "run_command",
                "Execute a read-only PowerShell command to investigate the user's PC. Use this to dig deeper into specific areas of interest discovered in the scan data. Examples: read config files, check git repos, inspect app settings, list recent browser history."),

            // Read a specific file
            AIFunctionFactory.Create(
                async ([Description("Absolute path to the file to read")] string path,
                       [Description("Max number of lines to read (default 50)")] int? maxLines) =>
                {
                    return await ReadFileAsync(path, maxLines ?? 50, ct);
                },
                "read_file",
                "Read the contents of a file on the user's PC. Use this to inspect configuration files, project files, or other interesting files discovered during scanning."),

            // List directory contents
            AIFunctionFactory.Create(
                ([Description("Absolute path to the directory to list")] string path,
                       [Description("Max number of entries to return (default 30)")] int? maxEntries) =>
                {
                    return ListDirectoryAsync(path, maxEntries ?? 30);
                },
                "list_directory",
                "List files and folders in a directory. Use this to explore project structures, app data, or areas of interest."),
        ];
    }

    // ═══════════════════════════════════════════════════════════════
    // Deterministic scan implementations
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> ScanInstalledAppsAsync(CancellationToken ct)
    {
        var results = new List<string>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var output = await RunPowerShellAsync(
                    "(Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*','HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*' -ErrorAction SilentlyContinue | " +
                    "Where-Object { $_.DisplayName -and $_.DisplayName.Length -gt 1 } | " +
                    "Select-Object -ExpandProperty DisplayName -Unique | Sort-Object | Select-Object -First 60) -join \"`n\"", ct);
                results.AddRange(output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            var knownProcesses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["code"] = "VS Code", ["devenv"] = "Visual Studio", ["rider64"] = "JetBrains Rider",
                ["chrome"] = "Chrome", ["firefox"] = "Firefox", ["msedge"] = "Edge",
                ["discord"] = "Discord", ["slack"] = "Slack", ["Telegram"] = "Telegram",
                ["teams"] = "Teams", ["spotify"] = "Spotify", ["docker"] = "Docker",
                ["WINWORD"] = "Word", ["EXCEL"] = "Excel", ["OUTLOOK"] = "Outlook",
                ["figma_agent"] = "Figma", ["steam"] = "Steam", ["obsidian"] = "Obsidian",
                ["notion"] = "Notion", ["WindowsTerminal"] = "Windows Terminal",
            };

            var running = Process.GetProcesses()
                .Select(p => { try { return p.ProcessName; } catch { return null; } })
                .Where(n => n is not null && knownProcesses.ContainsKey(n!))
                .Select(n => $"{knownProcesses[n!]} (running)")
                .Distinct().ToList();
            results.AddRange(running);
        }
        catch (Exception ex) { results.Add($"Error: {ex.Message}"); }

        UpdateCategoryCount(0, results.Count);
        return results.Count == 0
            ? "No apps found."
            : $"Found {results.Count} apps:\n{string.Join("\n", results)}";
    }

    private async Task<string> ScanBookmarksAsync(CancellationToken ct)
    {
        var results = new List<string>();
        try
        {
            var paths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\User Data\Default\Bookmarks"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\User Data\Default\Bookmarks"),
            };

            foreach (var path in paths)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var json = await File.ReadAllTextAsync(path, ct);
                    ExtractBookmarkNames(json, results);
                }
                catch { }
            }
            results = results.Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
        }
        catch (Exception ex) { results.Add($"Error: {ex.Message}"); }

        UpdateCategoryCount(1, results.Count);
        return results.Count == 0
            ? "No bookmarks found."
            : $"Found {results.Count} bookmarks:\n{string.Join("\n", results)}";
    }

    private Task<string> ScanRecentFilesAsync(CancellationToken ct)
    {
        var results = new List<string>();
        try
        {
            var folders = new[]
            {
                (Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Desktop"),
                (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents"),
                (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), "Downloads"),
            };

            foreach (var (path, label) in folders)
            {
                if (!Directory.Exists(path)) continue;
                try
                {
                    var dirs = Directory.GetDirectories(path)
                        .Select(Path.GetFileName)
                        .Where(n => n is not null && !n.StartsWith('.'))
                        .Take(10).Select(n => $"[Folder] {label}/{n}").ToList();
                    results.AddRange(dirs);

                    var files = Directory.GetFiles(path)
                        .Select(f => new FileInfo(f))
                        .Where(f => !f.Name.StartsWith('.') && f.Name != "desktop.ini")
                        .OrderByDescending(f => f.LastWriteTime)
                        .Take(8).Select(f => $"[File] {label}/{f.Name} ({f.Extension}, {f.LastWriteTime:yyyy-MM-dd})").ToList();
                    results.AddRange(files);
                }
                catch { }
            }
        }
        catch (Exception ex) { results.Add($"Error: {ex.Message}"); }

        UpdateCategoryCount(2, results.Count);
        return Task.FromResult(results.Count == 0
            ? "No files found."
            : $"Found {results.Count} items:\n{string.Join("\n", results)}");
    }

    private Task<string> ScanDevEnvironmentAsync(CancellationToken ct)
    {
        var results = new List<string>();
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var indicators = new Dictionary<string, string>
            {
                [".gitconfig"] = "Git (version control)",
                [".npmrc"] = "Node.js/npm",
                [".ssh"] = "SSH keys configured",
                [".vscode"] = "VS Code settings",
                [".docker"] = "Docker",
                [".nuget"] = ".NET/NuGet",
                [".cargo"] = "Rust/Cargo",
                [".rustup"] = "Rust toolchain",
                [".pyenv"] = "Python (pyenv)",
                [".conda"] = "Python (Conda)",
                ["go"] = "Go workspace",
                [".m2"] = "Java/Maven",
                [".gradle"] = "Java/Gradle",
            };

            foreach (var (name, desc) in indicators)
            {
                var fullPath = Path.Combine(userProfile, name);
                if (File.Exists(fullPath) || Directory.Exists(fullPath))
                    results.Add($"{desc} ({name})");
            }

            if (Directory.Exists(Path.Combine(userProfile, "AppData", "Local", "Packages")) &&
                Directory.GetDirectories(Path.Combine(userProfile, "AppData", "Local", "Packages"))
                    .Any(d => Path.GetFileName(d)?.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase) == true ||
                              Path.GetFileName(d)?.Contains("Debian", StringComparison.OrdinalIgnoreCase) == true))
                results.Add("WSL (Windows Subsystem for Linux)");
        }
        catch (Exception ex) { results.Add($"Error: {ex.Message}"); }

        UpdateCategoryCount(3, results.Count);
        return Task.FromResult(results.Count == 0
            ? "No developer tools found."
            : $"Found {results.Count} developer indicators:\n{string.Join("\n", results)}");
    }

    private async Task<string> ScanBrowserHistoryAsync(CancellationToken ct)
    {
        var domainCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var historyPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\User Data\Default\History"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\User Data\Default\History"),
            };

            foreach (var histPath in historyPaths)
            {
                if (!File.Exists(histPath)) continue;
                try
                {
                    var tempCopy = Path.Combine(Path.GetTempPath(), $"lumi_hist_{Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(histPath)))}.db");
                    File.Copy(histPath, tempCopy, true);

                    var script = "import sqlite3, collections\n" +
                        $"conn = sqlite3.connect(r'{tempCopy}')\n" +
                        "rows = conn.execute('SELECT url, visit_count FROM urls WHERE visit_count > 2 ORDER BY visit_count DESC LIMIT 100').fetchall()\n" +
                        "domains = collections.Counter()\n" +
                        "for url, count in rows:\n" +
                        "    parts = url.split('/')\n" +
                        "    if len(parts) > 2:\n" +
                        "        domain = parts[2].replace('www.', '')\n" +
                        "        domains[domain] += count\n" +
                        "for domain, count in domains.most_common(25):\n" +
                        "    print(str(count) + ' ' + domain)\n" +
                        "conn.close()\n";

                    var scriptFile = Path.Combine(Path.GetTempPath(), "lumi_hist_scan.py");
                    await File.WriteAllTextAsync(scriptFile, script, ct);
                    try
                    {
                        var output = await RunPowerShellAsync($"python \"{scriptFile}\"", ct);
                        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            var spaceIdx = line.IndexOf(' ');
                            if (spaceIdx > 0 && int.TryParse(line[..spaceIdx], out var count))
                            {
                                var domain = line[(spaceIdx + 1)..].Trim();
                                domainCounts.TryGetValue(domain, out var existing);
                                domainCounts[domain] = existing + count;
                            }
                        }
                    }
                    finally { try { File.Delete(scriptFile); } catch { } }
                }
                catch { }
            }
        }
        catch { }

        var sorted = domainCounts.OrderByDescending(kv => kv.Value).Take(25)
            .Select(kv => $"  {kv.Value}x {kv.Key}").ToList();

        UpdateCategoryCount(4, sorted.Count);
        return sorted.Count == 0
            ? "No browser history found."
            : $"Top {sorted.Count} most visited domains:\n{string.Join("\n", sorted)}";
    }

    // ═══════════════════════════════════════════════════════════════
    // Agent tool implementations
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> SaveMemoryAsync(string key, string content, string category)
    {
        var existing = _dataStore.Data.Memories.FirstOrDefault(m =>
            string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Content = content;
            existing.Category = category;
            existing.UpdatedAt = DateTimeOffset.Now;
        }
        else
        {
            _dataStore.Data.Memories.Add(new Memory
            {
                Key = key, Content = content, Category = category,
                Source = "onboarding"
            });
        }
        await _dataStore.SaveAsync();

        var icon = category switch
        {
            "Work" => "💼",
            "Technical" => "🔧",
            "Interests" => "🎯",
            "Preferences" => "⭐",
            "Personal" => "👤",
            "Goals" => "🚀",
            _ => "💡"
        };
        ShowCard(icon, key, content);

        return $"Memory saved: [{category}] {key}";
    }

    private async Task<string> AskUserQuestionAsync(string question, string options, CancellationToken ct)
    {
        _pendingQuestionTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Hide the current card and show the question card
        Dispatcher.UIThread.Post(() =>
        {
            HasCard = false;
            PendingQuestion = question;
            PendingQuestionOptions = options;
            HasPendingQuestion = true;
            QuestionAnswered = false;
            AgentOutput = "";
            QuestionAsked?.Invoke(question, options);
        });

        try
        {
            // Wait for user answer with overall cancellation support
            var tcs = _pendingQuestionTcs;
            using var reg = ct.Register(() => tcs.TrySetCanceled());
            var answer = await tcs.Task;
            Dispatcher.UIThread.Post(() => HasPendingQuestion = false);
            return $"User answered: {answer}";
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() => HasPendingQuestion = false);
            return "User skipped the question.";
        }
    }

    private async Task<string> RunSafeCommandAsync(string command, CancellationToken ct)
    {
        // Block dangerous commands
        var lower = command.ToLowerInvariant();
        var blocked = new[] { "remove-item", "del ", "rm ", "format-", "stop-process", "kill",
            "set-content", "out-file", "new-item", "mkdir", "invoke-webrequest",
            "start-process", "invoke-expression", "iex ", "& {", "restart-", "shutdown" };
        if (blocked.Any(b => lower.Contains(b)))
            return "Error: This command is not allowed. Only read-only commands are permitted.";

        try
        {
            // Timeout individual commands at 15 seconds to prevent blocking the whole flow
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var output = await RunPowerShellAsync(command, cts.Token);
            return string.IsNullOrWhiteSpace(output) ? "(no output)" : Truncate(output, 3000);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "Command timed out (15s limit). Try a simpler command.";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static async Task<string> ReadFileAsync(string path, int maxLines, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path))
                return $"File not found: {path}";

            var info = new FileInfo(path);
            if (info.Length > 512 * 1024)
                return $"File too large ({info.Length / 1024}KB). Try reading a smaller file or using run_command with Select-String.";

            var lines = await File.ReadAllLinesAsync(path, ct);
            var taken = lines.Take(maxLines).ToArray();
            var result = string.Join("\n", taken);
            if (lines.Length > maxLines)
                result += $"\n... ({lines.Length - maxLines} more lines)";
            return result;
        }
        catch (Exception ex)
        {
            return $"Error reading file: {ex.Message}";
        }
    }

    private static string ListDirectoryAsync(string path, int maxEntries)
    {
        try
        {
            if (!Directory.Exists(path))
                return $"Directory not found: {path}";

            var entries = new List<string>();
            foreach (var dir in Directory.GetDirectories(path).Take(maxEntries))
            {
                var name = Path.GetFileName(dir);
                entries.Add($"[DIR]  {name}");
            }
            foreach (var file in Directory.GetFiles(path).Take(maxEntries - entries.Count))
            {
                var fi = new FileInfo(file);
                entries.Add($"[FILE] {fi.Name} ({fi.Length / 1024}KB, {fi.LastWriteTime:yyyy-MM-dd})");
            }
            return entries.Count == 0
                ? "Directory is empty."
                : string.Join("\n", entries);
        }
        catch (Exception ex)
        {
            return $"Error listing directory: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Fallback: basic memories from scan data if agent fails
    // ═══════════════════════════════════════════════════════════════

    private void CreateFallbackMemories()
    {
        try
        {
            if (_scanResults.TryGetValue("apps", out var apps) && apps.Contains('\n'))
            {
                _dataStore.Data.Memories.Add(new Memory
                {
                    Key = "Installed apps",
                    Content = $"Onboarding scan found: {Truncate(apps, 300)}",
                    Category = "Technical", Source = "onboarding"
                });
            }
            if (_scanResults.TryGetValue("devtools", out var dev) && dev.Contains('\n'))
            {
                _dataStore.Data.Memories.Add(new Memory
                {
                    Key = "Dev environment",
                    Content = $"Developer tools: {Truncate(dev, 300)}",
                    Category = "Technical", Source = "onboarding"
                });
            }
            _ = _dataStore.SaveAsync();
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════════════════
    // Agent system prompt & user prompt
    // ═══════════════════════════════════════════════════════════════

    private string BuildAgentSystemPrompt()
    {
        return $"""
            You are Lumi's onboarding investigator. Your job is to deeply understand {UserName} by investigating their PC and asking smart questions.

            You have scan data as a starting point, but the real value comes from YOUR investigation using tools.

            ## Your tools
            - run_command: Run PowerShell commands to investigate (git config, app settings, recent activity, etc.)
            - read_file: Read config files, project files, settings
            - list_directory: Explore folders to understand project structure
            - save_memory: Save a useful fact about the user (key + content + category)
            - ask_user: Ask the user a question with clickable answer buttons. Blocks until they answer.

            ## What to investigate (use run_command / read_file)
            - Git config: run_command "git config --global --list" → get their name, email, default branch
            - VS Code extensions: list_directory of ~/.vscode/extensions → what languages/frameworks they use
            - Recent git repos: run_command to find .git folders in common locations
            - SSH keys: read_file ~/.ssh/config → what servers they connect to
            - NPM global packages: run_command "npm list -g --depth=0" if Node detected
            - Recent PowerShell history: read_file of ConsoleHost_history.txt
            - Interesting project files: read project READMEs or config files in repos the scan found

            The scan already includes browser history domains — use that to understand interests, services used, and work patterns. No need to re-query browser history.

            ## Flow
            1. Investigate 3-5 things from the list above using run_command/read_file/list_directory
            2. After each investigation, write a SHORT friendly comment about what you found. Examples:
               - "Oh nice, looks like you're deep into .NET and Avalonia development!"
               - "I see you're a Home Assistant user — smart home enthusiast!"
               - "Interesting, you've got three different AI coding assistants installed"
            3. Save 4-6 specific memories from what you found (NOT from the scan summary — from YOUR investigation)
            4. Ask 3 questions using ask_user based on what you discovered. After each answer, save a memory.
            5. End with a brief closing message.

            ## Question Guidelines
            Questions should be BROAD and personal, not hyper-specific technical questions.
            The user can select MULTIPLE answers, so provide 5-6 diverse options per question.
            Good questions:
            - "What do you enjoy most about your work?" → options: Building products, Solving hard problems, Learning new tech, Working with teams, Creative design, Teaching/mentoring
            - "What would you like Lumi to help with?" → options: Coding & debugging, Writing & communication, Research & learning, Task planning, File management, Creative projects
            - "What are your interests outside work?" → options: Gaming, Music, Travel, Cooking, Fitness, Reading/anime
            Bad questions (too specific):
            - "Which Avalonia rendering issue should we fix?" — way too technical
            - "Do you prefer imperative or declarative UI?" — not suitable for onboarding

            ## Rules
            - To ask a question, ALWAYS call ask_user. Never write questions as text.
            - ask_user blocks until the user clicks answers, then returns their choices.
            - Memories must be SPECIFIC and based on evidence you found, not generic summaries.
            - Good: "Git identity" → "Name: Adir Halfon, email: adir@example.com, uses main as default branch"
            - Good: "Active projects" → "Working on Lumi (Avalonia app), Strata (UI library), has 12 git repos"
            - Bad: "Dev tools" → "Uses Git and VS Code" (too obvious, already in scan)
            - Categories: Personal, Preferences, Work, Technical, Interests, Goals
            - Never show raw file paths or technical jargon to the user in text.
            
            CRITICAL: Do not stop after writing a comment. You must continue making tool calls until you have completed all phases.
            After writing a comment, IMMEDIATELY make the next tool call in the same response. Never end a turn with just text.
            """;
    }

    private string BuildAgentPromptWithContext()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Hi, I'm {UserName}! Here's what the quick scan found on my PC:");
        sb.AppendLine();

        foreach (var (key, value) in _scanResults)
        {
            sb.AppendLine($"## {key.ToUpperInvariant()}");
            sb.AppendLine(Truncate(value, 2000));
            sb.AppendLine();
        }

        sb.AppendLine($"""
            The scan gives you leads — now investigate deeper! Follow this plan:

            Phase 1 — Investigate (3-5 tool calls):
            - Run "git config --global --list" to learn my identity and preferences
            - Check my VS Code extensions folder to see what I develop with
            - Look at my recent projects/git repos
            - Check any other interesting config files the scan hints at
            Write a brief friendly comment after each investigation, but ALWAYS include a tool call with it.

            Phase 2 — Save memories (4-6 save_memory calls):
            - Save specific, evidence-based memories from what you found in Phase 1
            - Do NOT just rephrase the scan data — save NEW insights from your investigation

            Phase 3 — Ask 3 BROAD questions (3 calls to ask_user, save_memory after each):
            The user can select MULTIPLE answers. Provide 5-6 options per question.
            - Q1: What they enjoy most about their work (broad career/passion question)
            - Q2: What they'd like Lumi to help with (daily life + work, not just coding)
            - Q3: Their interests and hobbies outside work

            Phase 4 — Close with one friendly sentence.

            IMPORTANT: Do NOT stop after Phase 1. You must complete ALL four phases.
            """);
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private void UpdateCategoryCount(int idx, int count)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (idx >= 0 && idx < Categories.Count)
            {
                Categories[idx].FindingsCount = count;
                Categories[idx].StatusText = string.Format(Loc.Onboarding_DiscoverFound, count);
            }
        });
    }

    /// <summary>Push a new card to the Meet step carousel. Replaces the current card.</summary>
    private void ShowCard(string icon, string title, string body)
    {
        Dispatcher.UIThread.Post(() =>
        {
            AgentOutput = ""; // Clear streaming text before showing card to prevent overlap
            CardIndex++;
            CardIcon = icon;
            CardTitle = title;
            CardBody = body;
            CardProgress = $"{CardIndex} / {CardTotal}";
            HasCard = true;
        });
    }

    private static string GetFriendlyToolName(string? toolName) => toolName switch
    {
        "save_memory" => "Saving memory…",
        "ask_user" => "Thinking of a question…",
        "run_command" => "Investigating…",
        "read_file" => "Reading a file…",
        "list_directory" => "Exploring folders…",
        _ => toolName ?? ""
    };

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";

    private static async Task<string> RunPowerShellAsync(string command, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -NoLogo -NonInteractive -Command {command}",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            }
        };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { process.Kill(); } catch { } throw; }
        return output.Trim();
    }

    private static void ExtractBookmarkNames(string json, List<string> names)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var roots = doc.RootElement.GetProperty("roots");
            ExtractBookmarkNamesRecursive(roots, names);
        }
        catch { }
    }

    private static void ExtractBookmarkNamesRecursive(JsonElement element, List<string> names)
    {
        if (names.Count >= 50) return;
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("type", out var t) && t.GetString() == "url" &&
                element.TryGetProperty("name", out var n))
            {
                var name = n.GetString();
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
            foreach (var prop in element.EnumerateObject())
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    ExtractBookmarkNamesRecursive(prop.Value, names);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ExtractBookmarkNamesRecursive(item, names);
        }
    }
}
