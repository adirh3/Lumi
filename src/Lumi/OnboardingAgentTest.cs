#if DEBUG
// Onboarding Agent Test Harness
// Run: cd E:\Git\Lumi-wt-lumi-1c81523f && dotnet run --project src/Lumi -- --test-onboarding-agent
//
// This runs the onboarding agent headlessly with real scan data,
// auto-answers questions, and outputs everything to console.
// Use this to iterate on the agent prompt without the UI.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot.SDK;
using Lumi.Services;
using Microsoft.Extensions.AI;

namespace Lumi;

public static class OnboardingAgentTest
{
    private static int _memoriesSaved = 0;
    private static int _questionsAsked = 0;
    private static int _commandsRun = 0;
    private static readonly List<(string key, string content, string category)> _memories = [];

    public static async Task RunAsync(CopilotService copilotService, CancellationToken ct)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine("  ONBOARDING AGENT TEST HARNESS");
        Console.WriteLine("═══════════════════════════════════════════");
        Console.ResetColor();

        // Phase 1: Run deterministic scans
        Console.WriteLine("\n📊 Phase 1: Running deterministic scans...\n");
        var scanResults = await RunScansAsync(ct);

        foreach (var (key, value) in scanResults)
        {
            var lines = value.Split('\n').Length;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write($"  ✓ {key}: ");
            Console.ResetColor();
            Console.WriteLine($"{lines} lines of data");
        }

        // Phase 2: Run agent
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n═══════════════════════════════════════════");
        Console.WriteLine("  Phase 2: Agent Conversation");
        Console.WriteLine("═══════════════════════════════════════════\n");
        Console.ResetColor();

        if (!copilotService.IsConnected)
            await copilotService.ConnectAsync(ct);

        var tools = BuildTools(ct);
        var systemPrompt = BuildSystemPrompt("TestUser");
        var toolNames = tools.Select(t => t.Name).ToList();

        var session = await copilotService.Client!.CreateSessionAsync(new SessionConfig
        {
            Streaming = true,
            SystemMessage = new SystemMessageConfig
            {
                Content = systemPrompt,
                Mode = SystemMessageMode.Replace
            },
            Tools = tools,
            AvailableTools = toolNames,
            OnPermissionRequest = PermissionHandler.ApproveAll,
        }, ct);

        var assistantText = "";
        var toolNameByCallId = new Dictionary<string, string>(StringComparer.Ordinal);

        var sub = session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageDeltaEvent delta:
                    var chunk = delta.Data?.DeltaContent ?? "";
                    if (chunk.Length > 0)
                    {
                        Console.ForegroundColor = ConsoleColor.White;
                        Console.Write(chunk);
                        Console.ResetColor();
                    }
                    assistantText += chunk;
                    break;

                case ToolExecutionStartEvent toolStart:
                    var name = toolStart.Data?.ToolName ?? "";
                    var id = toolStart.Data?.ToolCallId;
                    if (!string.IsNullOrEmpty(id))
                        toolNameByCallId[id] = name;
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.Write($"\n  🔧 [{name}] ");
                    Console.ResetColor();
                    break;

                case ToolExecutionCompleteEvent toolEnd:
                    Console.ForegroundColor = ConsoleColor.DarkGreen;
                    Console.Write("✓");
                    Console.ResetColor();
                    break;
            }
        });

        try
        {
            var prompt = BuildPromptWithContext(scanResults, "TestUser");
            await session.SendAndWaitAsync(
                new MessageOptions { Prompt = prompt },
                TimeSpan.FromMinutes(5), ct);
        }
        finally
        {
            sub.Dispose();
            await session.DisposeAsync();
        }

        // Summary
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n\n═══════════════════════════════════════════");
        Console.WriteLine("  RESULTS SUMMARY");
        Console.WriteLine("═══════════════════════════════════════════");
        Console.ResetColor();
        Console.WriteLine($"  Memories saved: {_memoriesSaved}");
        Console.WriteLine($"  Questions asked: {_questionsAsked}");
        Console.WriteLine($"  Commands run: {_commandsRun}");
        Console.WriteLine("\n  📝 Memories created:");
        foreach (var (key, content, category) in _memories)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($"    [{category}] {key}: ");
            Console.ResetColor();
            Console.WriteLine(content);
        }
    }

    private static async Task<Dictionary<string, string>> RunScansAsync(CancellationToken ct)
    {
        var results = new Dictionary<string, string>();

        // Apps
        results["apps"] = await ScanApps(ct);
        // Bookmarks
        results["bookmarks"] = await ScanBookmarks(ct);
        // Files
        results["files"] = ScanFiles();
        // Dev tools
        results["devtools"] = ScanDevTools();
        // Browser history (top visited sites)
        results["browser_history"] = await ScanBrowserHistory(ct);

        return results;
    }

    private static async Task<string> ScanApps(CancellationToken ct)
    {
        var results = new List<string>();
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
        return $"Found {results.Count} apps:\n{string.Join("\n", results)}";
    }

    private static async Task<string> ScanBookmarks(CancellationToken ct)
    {
        var results = new List<string>();
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
                using var doc = JsonDocument.Parse(json);
                ExtractBookmarks(doc.RootElement.GetProperty("roots"), results);
            }
            catch { }
        }
        results = results.Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
        return $"Found {results.Count} bookmarks:\n{string.Join("\n", results)}";
    }

    private static void ExtractBookmarks(JsonElement element, List<string> names)
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
                    ExtractBookmarks(prop.Value, names);
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                ExtractBookmarks(item, names);
    }

    private static string ScanFiles()
    {
        var results = new List<string>();
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
                results.AddRange(Directory.GetDirectories(path).Select(Path.GetFileName)
                    .Where(n => n is not null && !n.StartsWith('.')).Take(10).Select(n => $"[Folder] {label}/{n}"));
                results.AddRange(Directory.GetFiles(path).Select(f => new FileInfo(f))
                    .Where(f => !f.Name.StartsWith('.') && f.Name != "desktop.ini")
                    .OrderByDescending(f => f.LastWriteTime).Take(8)
                    .Select(f => $"[File] {label}/{f.Name} ({f.Extension}, {f.LastWriteTime:yyyy-MM-dd})"));
            }
            catch { }
        }
        return $"Found {results.Count} items:\n{string.Join("\n", results)}";
    }

    private static string ScanDevTools()
    {
        var results = new List<string>();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var indicators = new Dictionary<string, string>
        {
            [".gitconfig"] = "Git", [".npmrc"] = "Node.js/npm", [".ssh"] = "SSH keys",
            [".vscode"] = "VS Code settings", [".docker"] = "Docker", [".nuget"] = ".NET/NuGet",
            [".cargo"] = "Rust/Cargo", [".rustup"] = "Rust", [".pyenv"] = "Python (pyenv)",
            [".conda"] = "Python (Conda)", ["go"] = "Go", [".m2"] = "Java/Maven", [".gradle"] = "Java/Gradle",
        };
        foreach (var (name, desc) in indicators)
        {
            var fullPath = Path.Combine(userProfile, name);
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
                results.Add($"{desc} ({name})");
        }
        return $"Found {results.Count} dev indicators:\n{string.Join("\n", results)}";
    }

    private static async Task<string> ScanBrowserHistory(CancellationToken ct)
    {
        var results = new List<string>();
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
                var browser = histPath.Contains("Edge") ? "Edge" : "Chrome";
                try
                {
                    // Copy to temp because the browser locks the file
                    var tempCopy = Path.Combine(Path.GetTempPath(), $"lumi_{browser}_history_copy.db");
                    File.Copy(histPath, tempCopy, true);

                    // Use Python to query SQLite (most reliable cross-platform)
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
                        $"    print(str(count) + 'x {browser}: ' + domain)\n" +
                        "conn.close()\n";
                    var output = await RunPythonAsync(script, ct);
                    results.AddRange(output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
                catch { }
            }
        }
        catch { }

        // Deduplicate across browsers and aggregate
        var domainCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in results)
        {
            // Format: "123x Edge: domain.com"
            var parts = line.Split(new[] { 'x', ':' }, 3, StringSplitOptions.TrimEntries);
            if (parts.Length >= 3 && int.TryParse(parts[0], out var count))
            {
                var domain = parts[2];
                domainCounts.TryGetValue(domain, out var existing);
                domainCounts[domain] = existing + count;
            }
        }

        var sorted = domainCounts.OrderByDescending(kv => kv.Value).Take(25)
            .Select(kv => $"  {kv.Value}x {kv.Key}").ToList();

        return sorted.Count == 0
            ? "No browser history found."
            : $"Top {sorted.Count} most visited domains:\n{string.Join("\n", sorted)}";
    }

    private static async Task<string> RunPythonAsync(string script, CancellationToken ct)
    {
        var scriptFile = Path.Combine(Path.GetTempPath(), "lumi_scan_script.py");
        await File.WriteAllTextAsync(scriptFile, script, ct);
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"\"{scriptFile}\"",
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
        finally { try { File.Delete(scriptFile); } catch { } }
    }

    private static List<AIFunction> BuildTools(CancellationToken ct)
    {
        return
        [
            AIFunctionFactory.Create(
                ([Description("Brief label")] string key,
                 [Description("Full memory text with details")] string content,
                 [Description("Category: Personal, Preferences, Work, Technical, Interests, Goals")] string? category) =>
                {
                    _memoriesSaved++;
                    _memories.Add((key, content, category ?? "General"));
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.Write($"💾 [{category}] {key}: {content}");
                    Console.ResetColor();
                    return $"Memory saved: [{category}] {key}";
                },
                "save_memory",
                "Save a persistent memory about the user."),

            AIFunctionFactory.Create(
                ([Description("A clear question to ask")] string question,
                 [Description("Comma-separated answer options")] string options) =>
                {
                    _questionsAsked++;
                    Console.ForegroundColor = ConsoleColor.Blue;
                    Console.WriteLine($"\n  ❓ QUESTION: {question}");
                    Console.WriteLine($"     Options: {options}");
                    Console.ResetColor();
                    // Auto-answer with first option
                    var firstOption = options.Split(',').FirstOrDefault()?.Trim() ?? "Yes";
                    Console.ForegroundColor = ConsoleColor.DarkBlue;
                    Console.WriteLine($"     → Auto-answer: {firstOption}");
                    Console.ResetColor();
                    return $"User answered: {firstOption}";
                },
                "ask_user",
                "Ask the user a question with clickable answer options."),

            AIFunctionFactory.Create(
                async ([Description("PowerShell command (read-only)")] string command) =>
                {
                    _commandsRun++;
                    var lower = command.ToLowerInvariant();
                    var blocked = new[] { "remove-item", "del ", "rm ", "format-", "stop-process", "kill",
                        "set-content", "out-file", "new-item", "mkdir", "invoke-webrequest",
                        "start-process", "invoke-expression", "iex ", "& {", "restart-", "shutdown" };
                    if (blocked.Any(b => lower.Contains(b)))
                        return "Error: not allowed.";
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(15));
                    var output = await RunPowerShellAsync(command, cts.Token);
                    var result = string.IsNullOrWhiteSpace(output) ? "(no output)" : (output.Length > 3000 ? output[..3000] + "…" : output);
                    Console.Write($"→ {result.Split('\n').Length} lines");
                    return result;
                },
                "run_command",
                "Execute a read-only PowerShell command to investigate the user's PC."),

            AIFunctionFactory.Create(
                async ([Description("File path")] string path, [Description("Max lines")] int? maxLines) =>
                {
                    if (!File.Exists(path)) return $"File not found: {path}";
                    var info = new FileInfo(path);
                    if (info.Length > 512 * 1024) return "File too large.";
                    var lines = await File.ReadAllLinesAsync(path, ct);
                    var taken = lines.Take(maxLines ?? 50).ToArray();
                    return string.Join("\n", taken);
                },
                "read_file",
                "Read a file's contents."),

            AIFunctionFactory.Create(
                ([Description("Directory path")] string path, [Description("Max entries")] int? maxEntries) =>
                {
                    if (!Directory.Exists(path)) return $"Not found: {path}";
                    var entries = new List<string>();
                    foreach (var dir in Directory.GetDirectories(path).Take(maxEntries ?? 30))
                        entries.Add($"[DIR]  {Path.GetFileName(dir)}");
                    foreach (var file in Directory.GetFiles(path).Take((maxEntries ?? 30) - entries.Count))
                    {
                        var fi = new FileInfo(file);
                        entries.Add($"[FILE] {fi.Name} ({fi.Length / 1024}KB, {fi.LastWriteTime:yyyy-MM-dd})");
                    }
                    return entries.Count == 0 ? "Empty." : string.Join("\n", entries);
                },
                "list_directory",
                "List files and folders in a directory."),
        ];
    }

    private static string BuildSystemPrompt(string userName) =>
        $"""
        You are Lumi's onboarding investigator. Your job is to deeply understand {userName} by investigating their PC and asking smart questions.

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
        2. After each investigation, write a SHORT friendly comment about what you found (shown as a chat bubble to the user). Examples:
           - "Oh nice, looks like you're deep into .NET and Avalonia development!"
           - "I see you're a Home Assistant user — smart home enthusiast!"
           - "Interesting, you've got three different AI coding assistants installed"
        3. Save 4-6 specific memories from what you found (NOT from the scan summary — from YOUR investigation)
        4. Ask 3 questions using ask_user based on what you discovered. After each answer, save a memory.
        5. End with a brief closing message.

        CRITICAL: Do not stop after writing a comment. You must continue making tool calls until you have completed all phases.
        After writing a comment, IMMEDIATELY make the next tool call in the same response. Never end a turn with just text.

        ## Rules
        - To ask a question, ALWAYS call ask_user. Never write questions as text.
        - ask_user blocks until the user clicks an answer, then returns their choice.
        - Write a brief, engaging 1-sentence comment between tool calls — these are shown as chat bubbles.
        - Make comments feel personal: reference specific things you found ("I noticed you visit Reddit a lot!")
        - Memories must be SPECIFIC and based on evidence you found, not generic summaries.
        - Good: "Git identity" → "Name: Adir Halfon, email: adir@example.com, uses main as default branch"
        - Good: "Active projects" → "Working on Lumi (Avalonia app), Strata (UI library), has 12 git repos"
        - Bad: "Dev tools" → "Uses Git and VS Code" (too obvious, already in scan)
        - Categories: Personal, Preferences, Work, Technical, Interests, Goals
        - Never show raw file paths or technical jargon to the user in text.
        """;

    private static string BuildPromptWithContext(Dictionary<string, string> scanResults, string userName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Hi, I'm {userName}! Here's what the quick scan found on my PC:");
        sb.AppendLine();
        foreach (var (key, value) in scanResults)
        {
            sb.AppendLine($"## {key.ToUpperInvariant()}");
            sb.AppendLine(value.Length > 2000 ? value[..2000] + "…" : value);
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

            Phase 3 — Ask questions (3 calls to ask_user, save_memory after each):
            - Ask about my work role (tailor options based on what you found)
            - Ask about what I want help with (tailor options based on my tools/projects)
            - Ask about my interests outside work

            Phase 4 — Close with one friendly sentence.
            
            IMPORTANT: Do NOT stop after Phase 1. You must complete ALL four phases.
            """);
        return sb.ToString();
    }

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
}
#endif
