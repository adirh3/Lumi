using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public class ToolDisplayHelperTests
{
    [Fact]
    public void FormatToolStatusName_Task_UsesFriendlyAgentLabel()
    {
        var status = ToolDisplayHelper.FormatToolStatusName("task", "{\"agent_type\":\"explore\"}");

        Assert.Equal("Running explore", status);
    }

    [Fact]
    public void FormatToolStatusName_AgentTool_UsesFriendlyAgentLabel()
    {
        var status = ToolDisplayHelper.FormatToolStatusName("agent:Coding Lumi");

        Assert.Equal("Running Coding Lumi", status);
    }

    [Fact]
    public void FormatProgressLabel_AppendsEllipsisWithoutDuplicatingRunningPrefix()
    {
        var status = ToolDisplayHelper.FormatProgressLabel("Running command");

        Assert.Equal("Running command…", status);
        Assert.DoesNotContain("Running Running", status);
    }

    [Fact]
    public void FormatProgressLabel_PreservesExistingEllipsis()
    {
        var status = ToolDisplayHelper.FormatProgressLabel("Thinking…");

        Assert.Equal("Thinking…", status);
    }

    [Fact]
    public void FormatProgressLabel_LeavesStandaloneActionPhraseIntact()
    {
        var baseLabel = ToolDisplayHelper.FormatToolStatusName("view", "{\"path\":\"E:\\\\repo\\\\sample.txt\"}");
        var status = ToolDisplayHelper.FormatProgressLabel(baseLabel);

        Assert.Equal("Reading sample.txt…", status);
    }

    [Fact]
    public void FormatToolStatusName_FetchSkill_UsesSkillName()
    {
        var status = ToolDisplayHelper.FormatToolStatusName("fetch_skill", "{\"name\":\"Debug Expert\"}");

        Assert.Equal("Using Debug Expert", status);
    }

    [Fact]
    public void GetFriendlyToolDisplay_FetchSkill_UsesSkillNameInLabel()
    {
        var (name, info) = ToolDisplayHelper.GetFriendlyToolDisplay("fetch_skill", null, "{\"name\":\"Debug Expert\"}");

        Assert.Equal("Using Debug Expert", name);
        Assert.Null(info);
    }

    [Fact]
    public void FormatToolArgsFriendly_FetchSkill_ShowsSkillField()
    {
        var args = ToolDisplayHelper.FormatToolArgsFriendly("fetch_skill", "{\"name\":\"Debug Expert\"}");

        Assert.Equal("**Skill:** Debug Expert", args);
    }

    [Fact]
    public void GetToolGlyph_FetchSkill_UsesSkillGlyph()
    {
        var glyph = ToolDisplayHelper.GetToolGlyph("fetch_skill");

        Assert.Equal("⚡", glyph);
    }

    [Fact]
    public void BuildToolActivitySummary_UsesRecentLabelsAndOverflowCount()
    {
        var summary = ToolDisplayHelper.BuildToolActivitySummary(
        [
            "📄 Reading first.txt",
            "🔎 Searching files",
            "⌨ Running command",
            "🧪 Generating tests"
        ]);

        Assert.Equal("🔎 Searching files  ·  ⌨ Running command  ·  🧪 Generating tests  +1", summary);
    }

    [Fact]
    public void TruncateInlineLabel_CollapsesWhitespaceAndAddsEllipsis()
    {
        var label = ToolDisplayHelper.TruncateInlineLabel("  Reading    a very long file name.txt  ", 18);

        Assert.Equal("Reading a very lo…", label);
    }
}
