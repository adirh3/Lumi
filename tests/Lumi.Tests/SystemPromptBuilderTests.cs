using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class SystemPromptBuilderTests
{
    [Fact]
    public void Build_IncludesConfiguredGlobalCustomInstructions()
    {
        var prompt = SystemPromptBuilder.Build(
            new UserSettings
            {
                Language = "en",
                GlobalCustomInstructions = "\n  Always use metric units and explain acronyms.  \n"
            },
            agent: null,
            project: null,
            allSkills: [],
            activeSkills: [],
            memories: []);

        Assert.Contains("--- Global Custom Instructions ---", prompt);
        Assert.Contains("Always use metric units and explain acronyms.", prompt);
        Assert.DoesNotContain("  Always use metric units", prompt);
    }

    [Fact]
    public void Build_OmitsBlankGlobalCustomInstructions()
    {
        var prompt = SystemPromptBuilder.Build(
            new UserSettings { Language = "en", GlobalCustomInstructions = " \r\n\t " },
            agent: null,
            project: null,
            allSkills: [],
            activeSkills: [],
            memories: []);

        Assert.DoesNotContain("--- Global Custom Instructions ---", prompt);
    }

    [Fact]
    public void Build_IncludesAsyncCommandGuidance()
    {
        var prompt = SystemPromptBuilder.Build(
            new UserSettings { Language = "en" },
            agent: null,
            project: null,
            allSkills: [],
            activeSkills: [],
            memories: []);

        Assert.Contains("## Async Command Guidance", prompt);
        Assert.Contains("prefer letting the tool generate the `shellId`", prompt);
        Assert.Contains("read it as soon as that command completes", prompt);
        // The async shell tool hint is platform-specific (Windows names `read_powershell`; macOS/Linux
        // say "read its output"), so it is asserted per-platform in Build_Windows_* and the leakage
        // tests. Here assert the platform-common background-agent guidance so this host-OS build passes
        // on every CI runner (windows/ubuntu/macos).
        Assert.Contains("call `read_agent` promptly", prompt);
    }

    [Fact]
    public void Build_IncludesExplicitLumiManagementGuidance()
    {
        var prompt = SystemPromptBuilder.Build(
            new UserSettings { Language = "en" },
            agent: null,
            project: null,
            allSkills:
            [
                new Skill
                {
                    Name = "Lumi Feature Manager",
                    Description = "Manages Lumi's projects, skills, Lumis, MCP servers, and memories when explicitly asked",
                    Content = "# Lumi Feature Manager"
                }
            ],
            activeSkills: [],
            memories: []);

        Assert.Contains("## Managing Lumi Itself", prompt);
        Assert.Contains("fetch the `Lumi Feature Manager` skill first", prompt);
        Assert.Contains("manage_skills", prompt);
        Assert.Contains("Lumi Feature Manager", prompt);
    }

    [Fact]
    public void Build_InstructsAgentsToSynchronizeCreatedWorktrees()
    {
        var prompt = SystemPromptBuilder.Build(
            new UserSettings { Language = "en" },
            agent: null,
            project: null,
            allSkills: [],
            activeSkills: [],
            memories: []);

        Assert.Contains("## Keeping the Current Chat Aligned", prompt);
        Assert.Contains("manage_current_chat", prompt);
        Assert.Contains("immediately call", prompt);
        Assert.Contains("git worktree", prompt);
        Assert.Contains("the next turn", prompt);
    }

    [Fact]
    public void Build_IncludesWritingStyleGuidance()
    {
        var prompt = SystemPromptBuilder.Build(
            new UserSettings { Language = "en" },
            agent: null,
            project: null,
            allSkills: [],
            activeSkills: [],
            memories: []);

        Assert.Contains("## Writing Style", prompt);
        Assert.Contains("Write like a knowledgeable friend", prompt);
        Assert.Contains("Lead with the answer, not the preamble", prompt);
        Assert.Contains("Emoji are welcome when they fit the moment naturally", prompt);
        Assert.Contains("respond as a person first", prompt);
        Assert.Contains("show genuine curiosity about what they built or achieved", prompt);
        Assert.Contains("Match the shape of your response to the moment", prompt);
        Assert.Contains("Use the full formatting palette available to you", prompt);
        Assert.Contains("`comparison`, `card`, `chart`, `confidence`, `mermaid`", prompt);
        Assert.Contains("Use markdown links instead of raw URLs", prompt);
        Assert.Contains("Use visualization blocks proactively", prompt);
        Assert.Contains("offer to do it — don't just describe the steps when you could run them", prompt);
        Assert.Contains("weave that context in naturally so the answer feels personal", prompt);
        Assert.Contains("clarity with warmth, not decoration", prompt);
    }

    [Fact]
    public void Build_AppendsConcretePresentationCheckAfterDynamicContext()
    {
        const string projectSentinel = "PROJECT_INSTRUCTIONS_SENTINEL";
        const string jobSentinel = "BACKGROUND_JOB_SENTINEL";
        var prompt = SystemPromptBuilder.Build(
            new UserSettings { Language = "en" },
            agent: null,
            project: new Project
            {
                Name = "Lumi",
                Instructions = projectSentinel
            },
            allSkills: [],
            activeSkills: [],
            memories: [],
            backgroundJobs:
            [
                new BackgroundJob
                {
                    Name = jobSentinel,
                    IsEnabled = true
                }
            ]);

        var presentationCheckIndex = prompt.LastIndexOf(
            "--- Response Presentation Check ---",
            StringComparison.Ordinal);

        Assert.True(presentationCheckIndex > prompt.LastIndexOf(projectSentinel, StringComparison.Ordinal));
        Assert.True(presentationCheckIndex > prompt.LastIndexOf(jobSentinel, StringComparison.Ordinal));
        Assert.Contains("exactly two meaningful alternatives", prompt);
        Assert.Contains("two or more central numeric values", prompt);
        Assert.Contains("a single compact profile, lookup, digest, deal, or result", prompt);
        Assert.Contains("a finished artifact delivered primarily by URL", prompt);
        Assert.Contains("do not return only a bare URL or prose-only link", prompt);
        Assert.Contains("functional UI controls", prompt);
        Assert.Contains("When a trigger matches, use the matching block as the default presentation", prompt);
        Assert.Contains("If none fits, use normal Markdown", prompt);
    }

    [Fact]
    public void Build_PresentsFinalUrlArtifactsAsCards()
    {
        var prompt = SystemPromptBuilder.Build(
            new UserSettings { Language = "en" },
            agent: null,
            project: null,
            allSkills: [],
            activeSkills: [],
            memories: []);

        Assert.Contains("## Link Deliverables", prompt);
        Assert.Contains("When the primary final artifact is a URL", prompt);
        Assert.Contains("one clear Markdown action link in the always-visible `summary`", prompt);
        Assert.Contains("[Open the finished dashboard](https://example.com/expense-dashboard)", prompt);
        Assert.Contains("not ordinary citations, source lists, or incidental links", prompt);
        Assert.Contains("Continue using `announce_file` for local files", prompt);
    }

    [Fact]
    public void Build_DoesNotContainModelSpecificCalibration()
    {
        var gptPrompt = SystemPromptBuilder.Build(
            new UserSettings { Language = "en", PreferredModel = "gpt-5.4" },
            agent: null,
            project: null,
            allSkills: [],
            activeSkills: [],
            memories: []);

        var claudePrompt = SystemPromptBuilder.Build(
            new UserSettings { Language = "en", PreferredModel = "claude-opus-4.6" },
            agent: null,
            project: null,
            allSkills: [],
            activeSkills: [],
            memories: []);

        // Same writing guidance regardless of model
        Assert.DoesNotContain("Extra Writing Calibration", gptPrompt);
        Assert.DoesNotContain("Extra Writing Calibration", claudePrompt);
    }

    [Fact]
    public void Build_FiltersNoisyMemoriesFromPrompt()
    {
        var prompt = SystemPromptBuilder.Build(
            new UserSettings { Language = "en", UserName = "Adir" },
            agent: null,
            project: null,
            allSkills: [],
            activeSkills: [],
            memories:
            [
                new Memory
                {
                    Key = "Preferred IDE",
                    Content = "Adir prefers VS Code as his code editor.",
                    Category = "Preferences"
                },
                new Memory
                {
                    Key = "Current Blast branch activity",
                    Content = "Blast is currently on branch version/1.1.0.0 with many uncommitted changes.",
                    Category = "Work"
                },
                new Memory
                {
                    Key = "VS Code tooling stack",
                    Content = "Uses VS Code extensions for .NET/C#, Python, Pylance, PowerShell, and GitHub Copilot Chat.",
                    Category = "Technical"
                }
            ]);

        Assert.Contains("Preferred IDE", prompt);
        Assert.DoesNotContain("Current Blast branch activity", prompt);
        Assert.DoesNotContain("VS Code tooling stack", prompt);
    }

    [Fact]
    public void Build_IncludesOnlyMatchingProjectMemories()
    {
        var lumiProjectId = Guid.NewGuid();
        var otherProjectId = Guid.NewGuid();
        var prompt = SystemPromptBuilder.Build(
            new UserSettings { Language = "en", UserName = "Adir" },
            agent: null,
            project: new Project { Id = lumiProjectId, Name = "Lumi" },
            allSkills: [],
            activeSkills: [],
            memories:
            [
                new Memory
                {
                    Key = "Favorite color",
                    Content = "Adir's favorite color is blue.",
                    Category = "Preferences"
                },
                new Memory
                {
                    Key = "Lumi UI convention",
                    Content = "In Lumi, always use Strata controls for chat UI.",
                    Category = "Project",
                    Scope = MemoryScopes.Project,
                    ProjectId = lumiProjectId
                },
                new Memory
                {
                    Key = "Blast UI convention",
                    Content = "In Blast, always use Fluent controls for settings UI.",
                    Category = "Project",
                    Scope = MemoryScopes.Project,
                    ProjectId = otherProjectId
                },
                new Memory
                {
                    Key = "Archived project memory",
                    Content = "In Lumi, always use archived project rules.",
                    Category = "Project",
                    Scope = MemoryScopes.Project,
                    ProjectId = lumiProjectId,
                    Status = MemoryStatuses.Archived
                }
            ]);

        Assert.Contains("--- Your Memories About Adir ---", prompt);
        Assert.Contains("Favorite color", prompt);
        Assert.Contains("--- Project Memories: Lumi ---", prompt);
        Assert.Contains("Lumi UI convention", prompt);
        Assert.DoesNotContain("Blast UI convention", prompt);
        Assert.DoesNotContain("Archived project memory", prompt);
    }

    private static string BuildForPlatform(SystemPromptBuilder.PromptPlatform platform)
        => SystemPromptBuilder.Build(
            new UserSettings { Language = "en", UserName = "Adir" },
            agent: null,
            project: null,
            allSkills: [],
            activeSkills: [],
            memories: [],
            platform);

    [Fact]
    public void Build_Windows_AdvertisesWindowsOnlyCapabilities()
    {
        var prompt = BuildForPlatform(SystemPromptBuilder.PromptPlatform.Windows);

        // The embedded browser + desktop UI automation sections are present on Windows.
        Assert.Contains("## Browser Automation", prompt);
        Assert.Contains("## Window Automation (UI Automation)", prompt);
        Assert.Contains("lumi_browser_open", prompt);
        Assert.Contains("ui_inspect", prompt);

        // PowerShell / COM / winget / WMI techniques are Windows-only.
        Assert.Contains("via PowerShell or Python", prompt);
        Assert.Contains("automate Office apps via COM", prompt);
        Assert.Contains("New-Object -ComObject", prompt);
        Assert.Contains("winget list", prompt);
        Assert.Contains("Get-CimInstance Win32_OperatingSystem", prompt);

        // The async-tool guidance uses the Windows shell-tool names.
        Assert.Contains("call `read_powershell`", prompt);
    }

    [Fact]
    public void WindowsCapabilitiesSection_MatchesBaselineWhitespace()
    {
        // Locks the "## What You Can Do" section to the prior release's EXACT (intentionally ragged)
        // leading whitespace so the Windows prompt cannot silently drift if the literal is reformatted.
        var prompt = BuildForPlatform(SystemPromptBuilder.PromptPlatform.Windows).ReplaceLineEndings("\n");
        const string expected =
            "  ## What You Can Do\n" +
            "   - **Run any command** via PowerShell or Python — you have a shell with full access\n" +
            " - **Read and write files** anywhere on the filesystem\n" +
            " - **Search the web** and fetch webpages\n" +
            " - **Automate the browser** (navigate, click, type, screenshot)\n" +
            "- **Automate any desktop window** via UI Automation — click buttons, type text, read values in any app\n" +
            "- **Query app databases** — most apps store data locally in SQLite, JSON, or XML files\n" +
            " - **Automate Office** — Word, Excel, PowerPoint via COM objects in PowerShell (for email/calendar, use webmail in the browser — see **Email** under Quick Reference)\n" +
            " - **Manage the system** — processes, disk space, installed apps, network, clipboard, and more\n";
        Assert.Contains(expected, prompt);
    }

    [Fact]
    public void Build_Linux_DoesNotLeakWindowsOnlyCapabilities()
        => AssertNoWindowsOnlyLeakage(SystemPromptBuilder.PromptPlatform.Linux);

    [Fact]
    public void Build_MacOS_DoesNotLeakWindowsOnlyCapabilities()
        => AssertNoWindowsOnlyLeakage(SystemPromptBuilder.PromptPlatform.MacOS);

    private static void AssertNoWindowsOnlyLeakage(SystemPromptBuilder.PromptPlatform platform)
    {
        var prompt = BuildForPlatform(platform);

        // Windows-only tool sections must NOT be advertised (the tools aren't registered).
        Assert.DoesNotContain("## Browser Automation", prompt);
        Assert.DoesNotContain("## Window Automation", prompt);
        Assert.DoesNotContain("lumi_browser_open", prompt);
        Assert.DoesNotContain("ui_inspect", prompt);
        Assert.DoesNotContain("ui_list_windows", prompt);

        // Windows-only shell/automation techniques must NOT leak.
        Assert.DoesNotContain("via PowerShell or Python", prompt);
        Assert.DoesNotContain("automate Office apps via COM", prompt);
        Assert.DoesNotContain("New-Object -ComObject", prompt);
        Assert.DoesNotContain("winget", prompt);
        Assert.DoesNotContain("Get-CimInstance", prompt);
        Assert.DoesNotContain("HKLM:", prompt);
        Assert.DoesNotContain("read_powershell", prompt);

        // Cross-platform guidance IS present.
        Assert.Contains("the shell (bash/zsh)", prompt);
        Assert.Contains("python-docx", prompt);
        Assert.Contains("async shell command", prompt);
    }

    [Fact]
    public void Build_Linux_UsesLinuxNativeTechniques()
    {
        var prompt = BuildForPlatform(SystemPromptBuilder.PromptPlatform.Linux);

        Assert.Contains("xdg-open", prompt);
        Assert.Contains("/etc/os-release", prompt);
        Assert.DoesNotContain("pbcopy", prompt);
    }

    [Fact]
    public void Build_MacOS_UsesMacNativeTechniques()
    {
        var prompt = BuildForPlatform(SystemPromptBuilder.PromptPlatform.MacOS);

        Assert.Contains("open <url-or-path>", prompt);
        Assert.Contains("pbcopy", prompt);
        Assert.Contains("sw_vers", prompt);
        Assert.DoesNotContain("xdg-open", prompt);
    }
}
