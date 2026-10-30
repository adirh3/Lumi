#if DEBUG
using Avalonia.Platform;
using Avalonia.Threading;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;

namespace Lumi.Mobile;

/// <summary>
/// DEBUG only: the design preview the form-factor simulator and an Android Debug build open, so the
/// phone's design is judged on every renderer against the same realistic content.
/// </summary>
public static class DesignPreview
{
    /// <summary>
    /// Opens one of the design chat's files in its preview pane, as a tap on its chip does, so each
    /// kind of file can be judged at every size (simulator <c>--open-file</c>, Android <c>lumi.open_file</c>).
    /// </summary>
    public static void OpenFile(MobileShellViewModel shell, string fileName)
    {
        shell.IsDrawerOpen = false;
        shell.Chat.OpenFilePreviewCommand.Execute(new RemoteAttachment
        {
            FileName = fileName,
            Extension = Path.GetExtension(fileName),
            MessageId = Guid.NewGuid()
        });
    }

    /// <summary>
    /// A paired, populated app on local fixture data: chats across projects in every state, a
    /// conversation, and each place's items. Nothing touches a PC; commands simply go nowhere.
    /// </summary>
    public static void Apply(MobileShellViewModel shell)
    {
        shell.IsPaired = shell.IsConnected = shell.IsHostReady = true;
        shell.HostName = "Preview PC";
        shell.UserName = "Adir";

        RemoteProject Project(string name, bool coding = false) =>
            new() { Id = Guid.NewGuid(), Name = name, IsCodingProject = coding, Instructions = $"Work on {name}." };
        var lumi = Project("Lumi", coding: true);
        var fluent = Project("Fluent Search", coding: true);
        var travel = Project("Travel");
        var home = Project("Home");
        var projects = new List<RemoteProject> { lumi, fluent, travel, home };
        foreach (var project in projects)
            shell.Projects.Add(new ProjectPickViewModel { Id = project.Id, Name = project.Name });

        var codingLumi = new RemoteLumi { Id = Guid.NewGuid(), Name = "Coding Lumi", Description = "Writes, reviews, debugs and tests software", IconGlyph = "⚡", IsBuiltIn = true, SkillCount = 3, ToolCount = 16 };
        var dailyPlanner = new RemoteLumi { Id = Guid.NewGuid(), Name = "Daily Planner", Description = "Plans your day and keeps you on track", IconGlyph = "📋", IsBuiltIn = true };
        var library = new RemoteLibrary
        {
            Projects = projects,
            // As a real PC sends them: Lumi's own agents and skills are built in, and connectors
            // and jobs can't be edited from the phone, so every kind of row is represented.
            Lumis =
            [
                new() { Id = Guid.NewGuid(), Name = "Release manager", Description = "Ships Lumi builds and checks the store listing", IconGlyph = "🚀", SkillCount = 2, ToolCount = 6 },
                codingLumi,
                dailyPlanner,
                new() { Id = Guid.NewGuid(), Name = "Creative Writer", Description = "Writing, storytelling and creative content", IconGlyph = "✍", IsBuiltIn = true }
            ],
            Skills =
            [
                new() { Id = Guid.NewGuid(), Name = "Fold layout check", Description = "Screenshots every Fold posture in the simulator", IconGlyph = "📱" },
                new() { Id = Guid.NewGuid(), Name = "Document Creator", Description = "Word, Excel and PowerPoint from a description", IconGlyph = "📄", IsBuiltIn = true },
                new() { Id = Guid.NewGuid(), Name = "Web Researcher", Description = "Searches the web and summarizes", IconGlyph = "🔎", IsBuiltIn = true },
                new() { Id = Guid.NewGuid(), Name = "Debug Expert", Description = "Isolate, reproduce, diagnose and fix", IconGlyph = "🐞", IsBuiltIn = true }
            ],
            McpServers =
            [
                new() { Id = Guid.NewGuid(), Name = "Avalonia MCP", Description = "Inspect a running Avalonia app", ServerType = "local", IsEnabled = true, ToolCount = 19 },
                new() { Id = Guid.NewGuid(), Name = "DotSight", Description = "C# solution intelligence", ServerType = "local", IsEnabled = true, ToolCount = 14 },
                new() { Id = Guid.NewGuid(), Name = "lumi-mcp", Description = "Drive a Debug Lumi", ServerType = "local", IsEnabled = true, ToolCount = 15 },
                new() { Id = Guid.NewGuid(), Name = "GitHub", Description = "Issues, pull requests and code", ServerType = "remote", IsEnabled = false, ToolCount = 24 }
            ],
            Jobs =
            [
                new() { Id = Guid.NewGuid(), Name = "Fluent Search feedback watcher", Description = "New feedback for Fluent Search", ScheduleSummary = "Every day at 09:15", LastRunStatus = "Completed", IsEnabled = true, TriggerType = "time", ChatId = Guid.NewGuid() },
                new() { Id = Guid.NewGuid(), Name = "Daily Lumi manager status report", Description = "PRs, releases and crashes", ScheduleSummary = "Every day at 10:15", LastRunStatus = "Completed", IsEnabled = true, TriggerType = "time", ChatId = Guid.NewGuid() },
                new() { Id = Guid.NewGuid(), Name = "Weekly AI agent UX research", Description = "Trends for Lumi's design", ScheduleSummary = "Mondays at 08:30", LastRunStatus = "Completed", IsEnabled = true, TriggerType = "time", ChatId = Guid.NewGuid() },
                new() { Id = Guid.NewGuid(), Name = "Avalonia text-fix PR owner", Description = "Wakes when the PR changes", ScheduleSummary = "Runs once", LastRunStatus = "Completed", IsEnabled = false, TriggerType = "script", ChatId = Guid.NewGuid() }
            ],
            Memories =
            [
                new() { Id = Guid.NewGuid(), Key = "Preferred theme", Content = "Dark, calm graphite", Category = "Preferences", UpdatedAt = DateTimeOffset.Now },
                new() { Id = Guid.NewGuid(), Key = "Phone", Content = "Galaxy Z Fold 7", Category = "Personal", UpdatedAt = DateTimeOffset.Now },
                new() { Id = Guid.NewGuid(), Key = "Favorite language", Content = "C#", Category = "Preferences", UpdatedAt = DateTimeOffset.Now }
            ]
        };
        shell.Library.Apply(library);
        // The composer's pickers offer what the PC has, as a real snapshot's library gives them.
        shell.Chat.ApplyLibraryCatalogs(library, reconcileProjectSelection: false);

        var now = DateTimeOffset.Now;
        RemoteChat Chat(string title, RemoteProject? project, string? preview, TimeSpan age,
            bool running = false, bool unread = false, bool pinned = false) => new()
        {
            Id = Guid.NewGuid(), Title = title, ProjectId = project?.Id, ProjectName = project?.Name,
            Preview = preview, UpdatedAt = now - age, IsRunning = running, IsSessionActive = running,
            HasUnreadMessages = unread, IsPinned = pinned, MessageCount = 12, LastModelUsed = "gpt-6.1-sol"
        };
        var open = Chat("Fix keyboard gap on resume", lumi, "Fixed — clears cached insets", TimeSpan.FromMinutes(24));
        shell.ChatList.Apply(
        [
            new RemoteChatGroup { Label = "Pinned", Chats = [Chat("Daily plan", null, "3 meetings, gym at 19:00", TimeSpan.FromHours(9), pinned: true)] },
            new RemoteChatGroup
            {
                Label = "Today",
                Chats =
                [
                    Chat("Mobile glass redesign", lumi, null, TimeSpan.FromSeconds(20), running: true),
                    open,
                    Chat("Crash triage: indexer timeout", fluent, "3 crashes share one stack", TimeSpan.FromHours(2), unread: true),
                    Chat("Lisbon weekend itinerary", travel, "Day 1: Alfama walk", TimeSpan.FromHours(5))
                ]
            },
            new RemoteChatGroup
            {
                Label = "Yesterday",
                Chats =
                [
                    Chat("Expense dashboard from CSV", home, "Dashboard ready", TimeSpan.FromHours(28)),
                    Chat("PR review: composer focus", lumi, "2 suggestions", TimeSpan.FromHours(30)),
                    Chat("Anime night picks", null, "Frieren, Dandadan", TimeSpan.FromHours(31))
                ]
            },
            // A real list runs well past one screen, so the header and the places always float over rows.
            new RemoteChatGroup
            {
                Label = "Earlier this week",
                Chats =
                [
                    Chat("Strata blur on Android", lumi, null, TimeSpan.FromDays(3)),
                    Chat("Indexer settings import", fluent, null, TimeSpan.FromDays(3.2)),
                    Chat("Weekly AI agent UX research", null, null, TimeSpan.FromDays(3.5)),
                    Chat("Release notes 1.1.74", lumi, null, TimeSpan.FromDays(4)),
                    Chat("Flight options to Lisbon", travel, null, TimeSpan.FromDays(4.2)),
                    Chat("Fluent Search crash on startup", fluent, null, TimeSpan.FromDays(4.5)),
                    Chat("Grocery list for the weekend", home, null, TimeSpan.FromDays(5)),
                    Chat("Tailscale pairing on the Fold", lumi, null, TimeSpan.FromDays(5.3)),
                    Chat("Mom's birthday gift ideas", null, null, TimeSpan.FromDays(5.6)),
                    Chat("Avalonia text layout PR", lumi, null, TimeSpan.FromDays(6))
                ]
            }
        ]);

        // A PC whose picker has pins, costs, context sizes, a model that still has to be enabled and
        // one on a connected account.
        shell.Client.UsePreviewCapabilities(RemoteProtocol.Capabilities.ModelPinsV1);
        shell.Chat.ApplyCatalogs(new RemoteSettings
        {
            PreferredModel = "gpt-6.1-sol",
            AvailableModels = ["auto", "claude-opus-5.5", "claude-sonnet-5.5", "gpt-6.1-sol", "gpt-6-luna", "gemini-3.8-flash", "byok:chatgpt-sol"],
            ModelDisplayNames =
            [
                "auto=Auto", "claude-opus-5.5=Claude Opus 5.5", "claude-sonnet-5.5=Claude Sonnet 5.5",
                "gpt-6.1-sol=GPT-6.1 Sol", "gpt-6-luna=GPT-6 Luna", "gemini-3.8-flash=Gemini 3.8 Flash",
                "byok:chatgpt-sol=GPT-6.1 Sol"
            ],
            ModelReasoningEfforts = ["gpt-6.1-sol=low,medium,high,xhigh", "claude-opus-5.5=low,medium,high"],
            ModelContextWindowTiers = ["gpt-6.1-sol=Default,Long"],
            ModelDefaults = [new() { Model = "gpt-6.1-sol", Quality = "high", ContextWindowTier = "Default" }],
            ModelOptions =
            [
                new() { Model = "gpt-6.1-sol", DisplayName = "GPT-6.1 Sol", ProviderName = "GitHub Copilot", IsPinned = true, Cost = "1×", ContextSize = "400K" },
                new() { Model = "claude-opus-5.5", DisplayName = "Claude Opus 5.5", ProviderName = "GitHub Copilot", IsPinned = true, Cost = "3×", ContextSize = "200K" },
                new() { Model = "byok:chatgpt-sol", DisplayName = "GPT-6.1 Sol", ProviderName = "ChatGPT", Account = "ChatGPT", IsPinned = true, ContextSize = "400K" },
                new() { Model = "auto", DisplayName = "Auto", ProviderName = "GitHub Copilot", Cost = "Included" },
                new() { Model = "claude-sonnet-5.5", DisplayName = "Claude Sonnet 5.5", ProviderName = "GitHub Copilot", Cost = "1×", ContextSize = "1M" },
                new() { Model = "gpt-6-luna", DisplayName = "GPT-6 Luna", ProviderName = "GitHub Copilot", Cost = "0.33×", ContextSize = "400K" },
                new() { Model = "gemini-3.8-flash", DisplayName = "Gemini 3.8 Flash", ProviderName = "GitHub Copilot", Cost = "0.33×", ContextSize = "1M", Status = "Needs enabling" }
            ]
        });
        // A new chat as a paired PC describes it: its greeting and brief, the setups the user keeps
        // starting chats with, and starters for the hour.
        shell.ApplyNewChatExperience(new RemoteNewChatExperience
        {
            Greeting = DateTime.Now.Hour switch
            {
                >= 5 and < 12 => "Good morning, Adir",
                >= 12 and < 17 => "Good afternoon, Adir",
                >= 17 and < 22 => "Good evening, Adir",
                _ => "Working late, Adir?"
            },
            Brief = "1 chat needs your answer · 1 new reply",
            SetupsTitle = "Start with",
            Setups =
            [
                new()
                {
                    Id = "lumi", Title = lumi.Name, Description = "Coding Lumi · Worktree · GPT-6.1 Sol · High",
                    Glyph = "L", ProjectId = lumi.Id, ProjectName = lumi.Name, AgentId = codingLumi.Id,
                    AgentName = codingLumi.Name, AgentGlyph = codingLumi.IconGlyph, UseWorktree = true,
                    Model = "gpt-6.1-sol", Quality = "High"
                },
                new()
                {
                    Id = "fluent", Title = fluent.Name, Description = "Claude Opus 5.5",
                    Glyph = "F", ProjectId = fluent.Id, ProjectName = fluent.Name, Model = "claude-opus-5.5"
                },
                new()
                {
                    Id = "planner", Title = dailyPlanner.Name, Description = "No project",
                    Glyph = dailyPlanner.IconGlyph, AgentId = dailyPlanner.Id, AgentName = dailyPlanner.Name,
                    AgentGlyph = dailyPlanner.IconGlyph
                }
            ],
            Starters =
            [
                new() { Glyph = "🌙", Label = "Recap my day", Prompt = "Recap my day: what did I work on today across my Lumi chats, what got done, and what's still open?" },
                new() { Glyph = "🗓️", Label = "Plan tomorrow", Prompt = "Help me plan tomorrow: what should I focus on, and in what order?" },
                new() { Glyph = "🔍", Label = "Research a topic", Prompt = "Research a topic" }
            ]
        });
        shell.Chat.Reset(open.Id, open.Title, "gpt-6.1-sol");
        shell.Chat.ProjectValue = lumi.Id.ToString();
        shell.Chat.ProjectName = lumi.Name;
        shell.Chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = open.Id, Title = open.Title, Revision = 1,
            // The open chat carries what a real one on this PC does: its project, a skill, and the six
            // connectors most chats here run with (which the composer leaves behind +, as the desktop does).
            Status = new RemoteChatStatus
            {
                ChatId = open.Id,
                ProjectId = lumi.Id,
                ProjectName = lumi.Name,
                SkillNames = ["Fold layout check"],
                McpNames = ["Avalonia MCP", "DotSight", "avalonia-mcp", "memory", "lumi-mcp", "workiq"]
            },
            Turns = TranscriptTurns(now)
        });
        // The preview has no PC to load each stretch of work's steps from, so it carries them.
        foreach (var activity in shell.Chat.Turns.SelectMany(turn => turn.Items).OfType<ActivitySummaryItemViewModel>())
            activity.ApplyDetails(ActivityDetails(open.Id, activity.ActivityId));
        shell.ChatList.SelectedChatId = open.Id;
        shell.Chat.ProjectValue = lumi.Id.ToString();
        shell.Chat.ProjectName = lumi.Name;
        // The files Lumi made in this chat open in the preview from local fixtures, as a PC would send them.
        shell.DesignPreviewFiles = DesignPreviewFiles.DownloadAsync;
        shell.Chat.PlanContent = "## Fix the keyboard gap\n\n- [x] Reproduce on the Fold cover screen\n- [x] Clear cached insets on deactivate\n- [x] Read fresh IME insets on resume\n- [ ] Verify on the unfolded screen";
        shell.Chat.PlanContent += "\n\n## Touch viewers\n\nOpen either visual full screen to try pinch-to-zoom, panning and Back.\n\n"
            + "```mermaid\nflowchart LR\n    Phone[Phone or Fold] --> Viewer[Full-screen viewer]\n    Viewer --> Zoom[Pinch and pan]\n    Zoom --> Back[Back to the same screen]\n```\n\n"
            + $"![Lumi artwork](<{CreateViewerImage()}>)";
        shell.ShowLaunchHome();

        // The fixture has no PC behind it, so the refreshes the app starts fail; a preview of the
        // design should not carry those offline notices.
        DispatcherTimer.Run(() =>
        {
            shell.ChatList.LoadErrorText = null;
            shell.Chat.TranscriptErrorText = null;
            shell.Chat.ErrorText = null;
            shell.Library.ViewStatus = null;
            return true;
        }, TimeSpan.FromMilliseconds(200));
    }

    private static string CreateViewerImage()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Lumi-mobile-preview-{Environment.ProcessId}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "viewer.png");
        using var asset = AssetLoader.Open(new Uri("avares://Lumi.Mobile/Assets/lumi-icon.png"));
        using (var output = File.Create(path))
            asset.CopyTo(output);
        return new Uri(path).AbsoluteUri;
    }

    /// <summary>
    /// A conversation that shows every kind of item the phone's transcript draws: an attachment, finished
    /// work folded behind its summary with the files it changed, a multi-part answer, sources and the
    /// files Lumi made, an answered question, and work still under way.
    /// </summary>
    private static List<RemoteTranscriptTurn> TranscriptTurns(DateTimeOffset now) =>
    [
        new()
        {
            Id = "turn-1",
            FinalAnswerId = "answer-1",
            WorkDurationMs = 134_000,
            Items =
            [
                new()
                {
                    Id = "user-1", Kind = RemoteProtocol.ItemKinds.User, Timestamp = now.AddMinutes(-26),
                    Text = "The composer jumps to the top when I come back from the file picker with the keyboard open. Fix it and run the mobile tests.",
                    Attachments = [new() { FileName = "fold-resume-before.png", Extension = ".png" }]
                },
                new()
                {
                    Id = "activity-1", Kind = RemoteProtocol.ItemKinds.Activity, ActivityId = "activity-1",
                    Status = "Completed", ActionCount = 6, DurationMs = 74_000, Label = "2 files changed", Icon = "mixed",
                    FileChangeCount = 2,
                    FileChanges =
                    [
                        new() { Path = "src/Lumi.Mobile/Views/MobileShellView.axaml.cs", FileName = "MobileShellView.axaml.cs", Operation = "Modified", LinesAdded = 14, LinesRemoved = 5 },
                        new() { Path = "tests/Lumi.Mobile.Tests/ComposerInsetTests.cs", FileName = "ComposerInsetTests.cs", Operation = "Created", LinesAdded = 42 }
                    ]
                },
                new()
                {
                    Id = "progress-1", Kind = RemoteProtocol.ItemKinds.Assistant, Model = "gpt-6.1-sol", Timestamp = now.AddMinutes(-25),
                    Text = "Fixed: Android kept the keyboard's last inset while Lumi was in the background. Now checking it.",
                    IsProgress = true
                },
                new()
                {
                    Id = "activity-2", Kind = RemoteProtocol.ItemKinds.Activity, ActivityId = "activity-2",
                    Status = "Completed", ActionCount = 4, DurationMs = 60_000, Label = "Activity", Icon = "mixed"
                },
                new()
                {
                    Id = "answer-1", Kind = RemoteProtocol.ItemKinds.Assistant, Author = "Lumi", Model = "gpt-6.1-sol", Timestamp = now.AddMinutes(-24),
                    IsProgress = false,
                    Text = "**Fixed — stale keyboard insets on resume.**\n\nAndroid kept the keyboard's last height while Lumi was in the background, so the composer came back lifted by a keyboard that was no longer there.\n\n- Clear cached input-pane geometry on deactivate\n- Read fresh IME insets on resume, already in DIPs\n\nAll 699 mobile tests pass.",
                    Sources =
                    [
                        new() { Title = "WindowInsets and the soft keyboard", Url = "https://developer.android.com/develop/ui/views/layout/sw-keyboard" },
                        new() { Title = "Input on mobile", Url = "https://docs.avaloniaui.net/docs/concepts/input" },
                        new() { Title = "The activity lifecycle", Url = "https://developer.android.com/guide/components/activities/activity-lifecycle" }
                    ]
                },
                new()
                {
                    Id = "file-1", Kind = RemoteProtocol.ItemKinds.File, Timestamp = now.AddMinutes(-24),
                    Attachments =
                    [
                        new() { FileName = "fold-resume-after.png", Extension = ".png", MessageId = Guid.NewGuid() },
                        new() { FileName = "keyboard-gap-report.md", Extension = ".md", MessageId = Guid.NewGuid() },
                        new() { FileName = "ComposerInsetTests.cs", Extension = ".cs", MessageId = Guid.NewGuid() }
                    ]
                }
            ]
        },
        new()
        {
            Id = "turn-2",
            FinalAnswerId = "answer-2",
            WorkDurationMs = 21_000,
            Items =
            [
                new() { Id = "user-2", Kind = RemoteProtocol.ItemKinds.User, Timestamp = now.AddMinutes(-12), Text = "Also check the unfolded screen at large display size." },
                new()
                {
                    Id = "progress-2", Kind = RemoteProtocol.ItemKinds.Assistant,
                    Text = "Checking the unfolded layout at large display size.", IsProgress = true
                },
                new()
                {
                    Id = "activity-3", Kind = RemoteProtocol.ItemKinds.Activity, ActivityId = "activity-3",
                    Status = "Completed", ActionCount = 3, DurationMs = 21_000, Label = "Activity", Icon = "mixed"
                },
                new()
                {
                    Id = "question-1", Kind = RemoteProtocol.ItemKinds.Question, Timestamp = now.AddMinutes(-11),
                    Question = new()
                    {
                        QuestionId = "question-1", Text = "The unfolded layout passes. Run the Android device tests too?",
                        Options = ["Yes, on my Fold", "Not now"], IsAnswered = true, Answer = "Yes, on my Fold"
                    }
                },
                new()
                {
                    Id = "answer-2-intro", Kind = RemoteProtocol.ItemKinds.Assistant, IsProgress = false,
                    Text = "The unfolded screen keeps the composer in place too."
                },
                new()
                {
                    Id = "answer-2", Kind = RemoteProtocol.ItemKinds.Assistant, Author = "Lumi", Model = "claude-opus-5.5", Timestamp = now.AddMinutes(-10),
                    Text = "The layout checks pass at large display size, with no stale keyboard gap.",
                    IsProgress = false
                },
                new()
                {
                    Id = "file-2", Kind = RemoteProtocol.ItemKinds.File, Timestamp = now.AddMinutes(-10),
                    Attachments =
                    [
                        new() { FileName = "fold-layout-report.pdf", Extension = ".pdf", MessageId = Guid.NewGuid() },
                        new() { FileName = "fold-layout-metrics.csv", Extension = ".csv", MessageId = Guid.NewGuid() },
                        new() { FileName = "Lumi-1.0-release-notes.docx", Extension = ".docx", MessageId = Guid.NewGuid() }
                    ]
                }
            ]
        },
        new()
        {
            Id = "turn-3",
            Items =
            [
                new() { Id = "user-3", Kind = RemoteProtocol.ItemKinds.User, Timestamp = now.AddMinutes(-1), Text = "Run them on my Fold now." },
                new()
                {
                    Id = "activity-4", Kind = RemoteProtocol.ItemKinds.Activity, ActivityId = "activity-4",
                    Status = "InProgress", ActionCount = 2, DurationMs = 9_000, Label = "Running Android device tests...", Icon = "terminal"
                },
                // A question still waiting for its answer: the options and the reply field a thumb answers.
                new()
                {
                    Id = "question-2", Kind = RemoteProtocol.ItemKinds.Question, Timestamp = now,
                    Question = new()
                    {
                        QuestionId = "question-2", Text = "The test build is ready. Where should I install it?",
                        Options = ["On my Fold", "On the emulator first"]
                    }
                }
            ]
        }
    ];

    private static RemoteActivityDetails ActivityDetails(Guid chatId, string activityId)
    {
        RemoteToolCall Step(string name, string display, string icon, string category, double seconds,
            string? input = null, string? output = null, string status = "Completed") => new()
        {
            Id = $"{activityId}-{name}-{display.GetHashCode():x}", Name = name, DisplayName = display, Icon = icon,
            Category = category, DurationMs = seconds * 1000, Input = input, Output = output, Status = status
        };

        return new RemoteActivityDetails
        {
            ChatId = chatId,
            ActivityId = activityId,
            Tools = activityId switch
            {
                "activity-1" =>
                [
                    Step("grep", "Searched the code", "search", "research", 2, "InputPane|SafeAreaBottom", "14 matches in 5 files"),
                    Step("view", "Read MobileShellView.axaml.cs", "read", "research", 1),
                    Step("web_search", "Searched the web", "search", "research", 6, "android ime insets after resume"),
                    Step("view", "Read ChatDetailView.axaml.cs", "read", "research", 1),
                    Step("edit", "Edited MobileShellView.axaml.cs", "edit", "work", 3),
                    Step("create", "Created ComposerInsetTests.cs", "create", "work", 2)
                ],
                "activity-2" =>
                [
                    Step("powershell", "Built the mobile app", "terminal", "verify", 21, "dotnet build src\\Lumi.Mobile", "Build succeeded."),
                    Step("powershell", "Ran the mobile tests", "terminal", "verify", 52, "dotnet test tests\\Lumi.Mobile.Tests", "Passed! 699 tests"),
                    Step("view", "Read the test results", "read", "verify", 1),
                    Step("ui_screenshot", "Took a screenshot", "screenshot", "verify", 4)
                ],
                "activity-3" =>
                [
                    Step("ui_do", "Unfolded the simulator", "desktop", "verify", 3),
                    Step("ui_screenshot", "Took a screenshot", "screenshot", "verify", 2),
                    Step("powershell", "Ran the layout tests", "terminal", "verify", 16, "dotnet test --filter Layout", "Passed! 48 tests")
                ],
                _ =>
                [
                    Step("powershell", "Built the test APK", "terminal", "verify", 34, "dotnet publish -f net10.0-android", "Lumi-mobile.apk"),
                    Step("powershell", "Running Android device tests", "terminal", "verify", 0, "adb shell am instrument -w com.lumi.mobile", status: "InProgress")
                ]
            }
        };
    }
}
#endif
