using System.Reflection;
using System.Text.Json;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class DataStoreCodingLumiToolTests
{
    private static readonly string[] BrowserToolNames =
    [
        "lumi_browser_open",
        "lumi_browser_tabs",
        "lumi_browser_screenshot",
        "lumi_browser_look",
        "lumi_browser_find",
        "lumi_browser_do",
        "lumi_browser_js"
    ];

    [Fact]
    public void SeedCodingLumi_IncludesAllBrowserToolsAndExistingTools()
    {
        var skillsDirectory = Path.Combine(Path.GetTempPath(), $"lumi-coding-tools-{Guid.NewGuid():N}");
        try
        {
            var data = new AppData();
            var store = new DataStore(data, skillsDirectory);
            var seed = typeof(DataStore).GetMethod(
                "SeedCodingLumi",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(seed);

            seed.Invoke(store, null);

            var codingLumi = Assert.Single(data.Agents);
            Assert.Equal("Coding Lumi", codingLumi.Name);
            Assert.True(codingLumi.IsBuiltIn);
            Assert.True(codingLumi.HasExplicitToolSelection);
            Assert.True(data.Settings.CodingLumiBrowserToolsSeeded);
            string[] existingTools =
            [
                "lumi_fetch",
                "announce_file", "skill", "recall_memory", "manage_current_chat"
            ];
            Assert.Equal(existingTools.Concat(BrowserToolNames), codingLumi.ToolNames);
            foreach (var retired in RetiredTools)
            {
                Assert.DoesNotContain(retired, codingLumi.SystemPrompt, StringComparison.Ordinal);
                Assert.All(data.Skills, skill => Assert.DoesNotContain(retired, skill.Content, StringComparison.Ordinal));
            }
            Assert.False(DataStore.RemoveRetiredCodingHelpers(data));
        }
        finally
        {
            if (Directory.Exists(skillsDirectory))
                Directory.Delete(skillsDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Migration_AddsMissingBrowserToolsAndPreservesExistingPermissions(bool explicitSelection)
    {
        var codingLumi = new LumiAgent
        {
            Name = "coding lumi",
            IsBuiltIn = true,
            HasExplicitToolSelection = explicitSelection,
            ToolNames = ["lumi_fetch", "custom_tool", "lumi_browser_look"]
        };
        var data = new AppData { Agents = [codingLumi] };

        Assert.True(DataStore.MigrateCodingLumiBrowserTools(data));
        Assert.False(DataStore.MigrateCodingLumiBrowserTools(data));

        Assert.True(data.Settings.CodingLumiBrowserToolsSeeded);
        Assert.Equal(explicitSelection, codingLumi.HasExplicitToolSelection);
        Assert.Contains("lumi_fetch", codingLumi.ToolNames);
        Assert.Contains("custom_tool", codingLumi.ToolNames);
        foreach (var toolName in BrowserToolNames)
            Assert.Single(codingLumi.ToolNames, name => name == toolName);
    }

    [Fact]
    public void Migration_LeavesUnrestrictedAgentUnrestricted()
    {
        var codingLumi = new LumiAgent { Name = "Coding Lumi", IsBuiltIn = true };
        var data = new AppData { Agents = [codingLumi] };

        Assert.True(DataStore.MigrateCodingLumiBrowserTools(data));

        Assert.Empty(codingLumi.ToolNames);
        Assert.False(codingLumi.HasToolRestrictions);
    }

    [Theory]
    [InlineData("Coding Lumi", false)]
    [InlineData("Creative Writer", true)]
    public void Migration_DoesNotChangeOtherAgents(string name, bool isBuiltIn)
    {
        var agent = new LumiAgent
        {
            Name = name,
            IsBuiltIn = isBuiltIn,
            HasExplicitToolSelection = true,
            ToolNames = ["lumi_fetch"]
        };
        var data = new AppData { Agents = [agent] };

        Assert.True(DataStore.MigrateCodingLumiBrowserTools(data));

        Assert.Equal(["lumi_fetch"], agent.ToolNames);
    }

    [Fact]
    public void Migration_DoesNotReenableBrowserToolsAfterUserCustomization()
    {
        var codingLumi = new LumiAgent
        {
            Name = "Coding Lumi",
            IsBuiltIn = true,
            HasExplicitToolSelection = true,
            ToolNames = ["lumi_fetch"]
        };
        var data = new AppData { Agents = [codingLumi] };
        Assert.True(DataStore.MigrateCodingLumiBrowserTools(data));
        codingLumi.ToolNames.Remove("lumi_browser_do");

        Assert.False(DataStore.MigrateCodingLumiBrowserTools(data));

        Assert.DoesNotContain("lumi_browser_do", codingLumi.ToolNames);
    }

    [Fact]
    public void IndexSnapshot_PersistsMigrationFlagAndToolPermissions()
    {
        var codingLumi = new LumiAgent
        {
            Name = "Coding Lumi",
            IsBuiltIn = true,
            HasExplicitToolSelection = true,
            ToolNames = ["lumi_fetch"]
        };
        var data = new AppData { Agents = [codingLumi] };
        Assert.True(DataStore.MigrateCodingLumiBrowserTools(data));
        var snapshot = AppDataSnapshotFactory.CreateIndexSnapshot(data);

        var json = JsonSerializer.Serialize(snapshot, AppDataJsonContext.Default.AppData);
        var restored = JsonSerializer.Deserialize(json, AppDataJsonContext.Default.AppData);

        Assert.NotNull(restored);
        Assert.True(restored.Settings.CodingLumiBrowserToolsSeeded);
        var restoredAgent = Assert.Single(restored.Agents);
        Assert.Equal(codingLumi.ToolNames, restoredAgent.ToolNames);
        Assert.True(restoredAgent.HasExplicitToolSelection);
        Assert.False(DataStore.MigrateCodingLumiBrowserTools(restored));
    }

    private static readonly string[] RetiredTools = ["code_review", "generate_tests", "explain_code", "analyze_project"];

    [Fact]
    public void Retirement_RemovesOnlyRetiredToolSelectionsAndModelChoice()
    {
        var skill = new Skill { Name = "My review process", Content = "Keep my custom instructions." };
        var agent = new LumiAgent
        {
            Name = "My agent",
            SystemPrompt = "My custom code_review instructions stay mine.",
            ToolNames = [.. RetiredTools, "lumi_fetch", "custom_tool"],
            SkillIds = [skill.Id]
        };
        var unrestricted = new LumiAgent { Name = "Unrestricted" };
        var data = new AppData
        {
            Agents = [agent, unrestricted],
            Skills = [skill],
            Settings = new UserSettings
            {
                BackgroundModel = "my-background-model",
                AiFeatureModels = { ["coding-helpers"] = "retired-model", [AiFeatureIds.Memories] = "memory-model" }
            }
        };

        Assert.True(DataStore.RemoveRetiredCodingHelpers(data));

        Assert.Equal(["lumi_fetch", "custom_tool"], agent.ToolNames);
        Assert.Equal("My custom code_review instructions stay mine.", agent.SystemPrompt);
        Assert.Equal([skill.Id], agent.SkillIds);
        Assert.False(agent.HasExplicitToolSelection);
        Assert.True(agent.HasToolRestrictions);
        Assert.False(unrestricted.HasToolRestrictions);
        Assert.Equal("Keep my custom instructions.", skill.Content);
        Assert.Equal("my-background-model", data.Settings.BackgroundModel);
        Assert.Equal("memory-model", Assert.Single(data.Settings.AiFeatureModels).Value);
        Assert.False(DataStore.RemoveRetiredCodingHelpers(data));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Retirement_RetiredOnlySelectionRemainsRestrictedAfterSaving(bool explicitSelection)
    {
        var agent = new LumiAgent
        {
            Name = "Legacy reviewer",
            HasExplicitToolSelection = explicitSelection,
            ToolNames = [.. RetiredTools]
        };
        var data = new AppData { Agents = [agent] };

        Assert.True(DataStore.RemoveRetiredCodingHelpers(data));
        var snapshot = AppDataSnapshotFactory.CreateIndexSnapshot(data);
        var json = JsonSerializer.Serialize(snapshot, AppDataJsonContext.Default.AppData);
        var restored = JsonSerializer.Deserialize(json, AppDataJsonContext.Default.AppData)!;
        var restoredAgent = Assert.Single(restored.Agents);

        Assert.Empty(restoredAgent.ToolNames);
        Assert.True(restoredAgent.HasExplicitToolSelection);
        Assert.True(restoredAgent.HasToolRestrictions);
        Assert.False(DataStore.RemoveRetiredCodingHelpers(restored));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Retirement_UpdatesFactoryInstructionsAndPreservesCustomText(string newline)
    {
        var legacyPrompt = string.Join(newline,
            "Custom introduction.",
            "You have a `code_review` tool that performs deep automated analysis. Use it when:",
            "You have a `generate_tests` tool that creates comprehensive test suites. Use it when:",
            "You have an `explain_code` tool for deep analysis. Use it when:",
            "You have an `analyze_project` tool for project-level understanding. Use it when:",
            "5. **Review** — Use `code_review` on your own output for important changes",
            "Custom ending.");
        var codingLumi = new LumiAgent { Name = "coding lumi", IsBuiltIn = true, SystemPrompt = legacyPrompt };
        var customAgent = new LumiAgent { Name = "Coding Lumi", SystemPrompt = legacyPrompt };
        var otherBuiltIn = new LumiAgent { Name = "Creative Writer", IsBuiltIn = true, SystemPrompt = legacyPrompt };
        var legacyReview = string.Join(newline,
            "Use the `code_review` tool for automated analysis, then layer on your own expertise.",
            "### 2. Automated Analysis",
            "Use the `code_review` tool to get an initial automated review of the code. This handles:",
            "### 3. Human-Level Review (your added value)",
            "Go beyond what automated tools catch:",
            "- **Missing tests** — Use `generate_tests` to create test suggestions",
            "Custom review rule.");
        var reviewSkill = new Skill { Name = "Code Reviewer Pro", IsBuiltIn = true, Content = legacyReview };
        var customSkill = new Skill { Name = "Code Reviewer Pro", Content = legacyReview };
        var debugSkill = new Skill
        {
            Name = "Debug Expert",
            IsBuiltIn = true,
            Content = "- Use `code_review` tool to spot related issues" + newline + "Custom debug rule."
        };
        var data = new AppData { Agents = [codingLumi, customAgent, otherBuiltIn], Skills = [reviewSkill, customSkill, debugSkill] };

        Assert.True(DataStore.RemoveRetiredCodingHelpers(data));

        foreach (var retired in RetiredTools)
        {
            Assert.DoesNotContain(retired, codingLumi.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain(retired, reviewSkill.Content, StringComparison.Ordinal);
            Assert.DoesNotContain(retired, debugSkill.Content, StringComparison.Ordinal);
        }
        Assert.StartsWith("Custom introduction." + newline, codingLumi.SystemPrompt, StringComparison.Ordinal);
        Assert.EndsWith(newline + "Custom ending.", codingLumi.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Review the actual changes and surrounding code", codingLumi.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Inspect the actual diff and surrounding repository code", reviewSkill.Content, StringComparison.Ordinal);
        Assert.EndsWith(newline + "Custom review rule.", reviewSkill.Content, StringComparison.Ordinal);
        Assert.EndsWith(newline + "Custom debug rule.", debugSkill.Content, StringComparison.Ordinal);
        Assert.Equal(legacyPrompt, customAgent.SystemPrompt);
        Assert.Equal(legacyPrompt, otherBuiltIn.SystemPrompt);
        Assert.Equal(legacyReview, customSkill.Content);
        Assert.False(DataStore.RemoveRetiredCodingHelpers(data));
    }

    [Fact]
    public void Retirement_LegacySettingsLoadButAreNotSavedAgain()
    {
        var data = JsonSerializer.Deserialize("""
            {"settings":{"enableCodingHelpers":false,"aiFeatureModels":{"coding-helpers":"retired-model","chat-titles":"title-model"}}}
            """, AppDataJsonContext.Default.AppData)!;

        Assert.True(DataStore.RemoveRetiredCodingHelpers(data));
        var snapshot = AppDataSnapshotFactory.CreateIndexSnapshot(data);
        var json = JsonSerializer.Serialize(snapshot, AppDataJsonContext.Default.AppData);

        Assert.DoesNotContain("enableCodingHelpers", json, StringComparison.Ordinal);
        Assert.DoesNotContain("coding-helpers", json, StringComparison.Ordinal);
        Assert.Equal("title-model", snapshot.Settings.GetAiFeatureModel(AiFeatureIds.ChatTitles));
    }

    [Fact]
    public void Retirement_PreservesFocusedReviewRulesInOlderSavedInstructions()
    {
        var skill = new Skill
        {
            Name = "Code Reviewer Pro",
            IsBuiltIn = true,
            Content = """
                Use the `code_review` tool for an initial pass covering bugs, security, performance, and code quality. Treat every automated result as a hypothesis, not an instruction:
                - Verify findings against the requested change before acting.
                - Use `generate_tests` only for a concrete gap in changed behavior; do not generate broad combinatorial suites for hypothetical risks.
                """
        };
        var data = new AppData { Skills = [skill] };

        Assert.True(DataStore.RemoveRetiredCodingHelpers(data));

        Assert.DoesNotContain("code_review", skill.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("generate_tests", skill.Content, StringComparison.Ordinal);
        Assert.Contains("Treat every finding as a hypothesis, not an instruction:", skill.Content, StringComparison.Ordinal);
        Assert.Contains("- Verify findings against the requested change before acting.", skill.Content, StringComparison.Ordinal);
        Assert.Contains("do not generate broad combinatorial suites for hypothetical risks.", skill.Content, StringComparison.Ordinal);
        Assert.False(DataStore.RemoveRetiredCodingHelpers(data));
    }
}
