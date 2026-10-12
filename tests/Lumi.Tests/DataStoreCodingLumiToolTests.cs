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
                "code_review", "generate_tests", "explain_code", "analyze_project",
                "lumi_fetch",
                "announce_file", "skill", "recall_memory", "manage_current_chat"
            ];
            Assert.Equal(existingTools.Concat(BrowserToolNames), codingLumi.ToolNames);
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
            ToolNames = ["code_review", "custom_tool", "lumi_browser_look"]
        };
        var data = new AppData { Agents = [codingLumi] };

        Assert.True(DataStore.MigrateCodingLumiBrowserTools(data));
        Assert.False(DataStore.MigrateCodingLumiBrowserTools(data));

        Assert.True(data.Settings.CodingLumiBrowserToolsSeeded);
        Assert.Equal(explicitSelection, codingLumi.HasExplicitToolSelection);
        Assert.Contains("code_review", codingLumi.ToolNames);
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
            ToolNames = ["code_review"]
        };
        var data = new AppData { Agents = [agent] };

        Assert.True(DataStore.MigrateCodingLumiBrowserTools(data));

        Assert.Equal(["code_review"], agent.ToolNames);
    }

    [Fact]
    public void Migration_DoesNotReenableBrowserToolsAfterUserCustomization()
    {
        var codingLumi = new LumiAgent
        {
            Name = "Coding Lumi",
            IsBuiltIn = true,
            HasExplicitToolSelection = true,
            ToolNames = ["code_review"]
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
            ToolNames = ["code_review"]
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
}
