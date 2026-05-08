using GitHub.Copilot.SDK;
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

    [Fact]
    public void IsSearchTool_RecognizesBuiltInWebSearch()
    {
        Assert.True(ToolDisplayHelper.IsSearchTool("web_search"));
        Assert.True(ToolDisplayHelper.IsSearchTool("search"));
        Assert.False(ToolDisplayHelper.IsSearchTool("lumi_fetch"));
    }

    [Fact]
    public void ExtractSearchSources_UsesResourceLinksAndDeduplicatesByUrl()
    {
        var result = new ToolExecutionCompleteResult
        {
            Content = "ok",
            Contents =
            [
                new ToolExecutionCompleteContentResourceLink
                {
                    Name = "Example",
                    Title = "Example title",
                    Description = "Example snippet",
                    Uri = "https://example.com/article"
                },
                new ToolExecutionCompleteContentResourceLink
                {
                    Name = "Fallback title",
                    Uri = "https://contoso.com/post"
                },
                new ToolExecutionCompleteContentResourceLink
                {
                    Name = "Duplicate",
                    Title = "Ignored duplicate",
                    Uri = "https://example.com/article"
                },
                new ToolExecutionCompleteContentResourceLink
                {
                    Name = "Local file",
                    Uri = "file:///C:/temp/result.txt"
                },
                new ToolExecutionCompleteContentText
                {
                    Text = "plain text"
                }
            ]
        };

        var sources = ToolDisplayHelper.ExtractSearchSources(result);

        Assert.Collection(
            sources,
            first =>
            {
                Assert.Equal("Example title", first.Title);
                Assert.Equal("Example snippet", first.Snippet);
                Assert.Equal("https://example.com/article", first.Url);
            },
            second =>
            {
                Assert.Equal("Fallback title", second.Title);
                Assert.Equal(string.Empty, second.Snippet);
                Assert.Equal("https://contoso.com/post", second.Url);
            });
    }

    [Fact]
    public void IsWebFetchTool_RecognizesFetchVariants()
    {
        Assert.True(ToolDisplayHelper.IsWebFetchTool("web_fetch"));
        Assert.True(ToolDisplayHelper.IsWebFetchTool("fetch"));
        Assert.True(ToolDisplayHelper.IsWebFetchTool("lumi_fetch"));
        Assert.False(ToolDisplayHelper.IsWebFetchTool("web_search"));
        Assert.False(ToolDisplayHelper.IsWebFetchTool(null));
    }

    [Fact]
    public void ExtractFetchSource_UsesFetchArgumentsAndReadableResult()
    {
        var source = ToolDisplayHelper.ExtractFetchSource("{\"url\":\"https://example.com/article\"}");

        Assert.NotNull(source);
        Assert.Equal("example.com", source.Title);
        Assert.Equal(string.Empty, source.Snippet);
        Assert.Equal("https://example.com/article", source.Url);
    }

    [Fact]
    public void ExtractFetchSource_IgnoresMissingOrNonWebUrl()
    {
        Assert.Null(ToolDisplayHelper.ExtractFetchSource("{}"));
        Assert.Null(ToolDisplayHelper.ExtractFetchSource("{\"url\":\"file:///C:/temp/page.html\"}"));
    }
}
