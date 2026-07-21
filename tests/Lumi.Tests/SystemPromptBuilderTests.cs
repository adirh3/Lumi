using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class SystemPromptBuilderTests
{
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
        Assert.Contains("call `read_powershell` promptly", prompt);
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
}
