using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class AgentsViewModelToolSelectionTests
{
    [Fact]
    public void DesktopBatchTool_IsSelectableOnlyOnWindows()
    {
        var names = GetVisibleToolNames();
        Assert.Equal(OperatingSystem.IsWindows(), names.Contains("ui_do"));
        Assert.Equal(OperatingSystem.IsWindows(), names.Contains("ui_screenshot"));
        Assert.Equal(OperatingSystem.IsWindows(), names.Contains("ui_click_at"));
    }

    [Fact]
    public void BrowserTools_MatchSharedHostAvailability()
    {
        var visibleNames = GetVisibleToolNames();

        var available = NativeBrowserLogic.IsEmbeddedBrowserAvailable;
        Assert.Equal(available, visibleNames.Contains(ToolDisplayHelper.BrowserOpenToolName));
        Assert.Equal(available, visibleNames.Contains(ToolDisplayHelper.BrowserLookToolName));
        Assert.Equal(available, visibleNames.Contains(ToolDisplayHelper.BrowserFindToolName));
        Assert.Equal(available, visibleNames.Contains(ToolDisplayHelper.BrowserDoToolName));
        Assert.Equal(available, visibleNames.Contains(ToolDisplayHelper.BrowserJsToolName));
        Assert.Equal(available, visibleNames.Contains(ToolDisplayHelper.BrowserTabsToolName));
        Assert.Equal(available, visibleNames.Contains(ToolDisplayHelper.BrowserScreenshotToolName));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            Assert.True(available);
    }

    [Fact]
    public void SaveAgent_PreservesExplicitBrowserTabsAndScreenshotSelection()
    {
        var agent = new LumiAgent
        {
            Name = "Browser observer",
            ToolNames = [ToolDisplayHelper.BrowserTabsToolName, ToolDisplayHelper.BrowserScreenshotToolName],
            HasExplicitToolSelection = true
        };
        var viewModel = CreateEditor(agent);

        viewModel.SaveAgentCommand.Execute(null);

        Assert.True(agent.HasToolRestrictions);
        Assert.Equal(
            new[] { ToolDisplayHelper.BrowserScreenshotToolName, ToolDisplayHelper.BrowserTabsToolName },
            agent.ToolNames.OrderBy(static name => name));
    }

    [Fact]
    public void SaveAgent_ExplicitEmptySelectionRemainsRestricted()
    {
        var agent = new LumiAgent
        {
            Name = "Prompt-only Lumi",
            HasExplicitToolSelection = true
        };
        var viewModel = CreateEditor(agent);

        viewModel.SaveAgentCommand.Execute(null);

        Assert.True(agent.HasExplicitToolSelection);
        Assert.True(agent.HasToolRestrictions);
        Assert.Empty(agent.ToolNames);
    }

    [Fact]
    public void SaveAgent_UnrestrictedSelectionRemainsUnrestricted()
    {
        var agent = new LumiAgent { Name = "Unrestricted Lumi" };
        var viewModel = CreateEditor(agent);

        Assert.All(viewModel.AvailableTools, tool => Assert.True(tool.IsSelected));
        viewModel.SaveAgentCommand.Execute(null);

        Assert.False(agent.HasExplicitToolSelection);
        Assert.False(agent.HasToolRestrictions);
        Assert.Empty(agent.ToolNames);
    }

    [Fact]
    public void SaveAgent_ExplicitVisibleSelectionDoesNotReenableUnavailableDesktopTools()
    {
        var visibleToolNames = GetVisibleToolNames();
        var agent = new LumiAgent
        {
            Name = "Unix-visible tools only",
            ToolNames = visibleToolNames,
            HasExplicitToolSelection = true
        };
        var viewModel = CreateEditor(agent);

        Assert.All(viewModel.AvailableTools, tool => Assert.True(tool.IsSelected));
        viewModel.SaveAgentCommand.Execute(null);

        if (OperatingSystem.IsWindows())
        {
            Assert.False(agent.HasExplicitToolSelection);
            Assert.False(agent.HasToolRestrictions);
            Assert.Empty(agent.ToolNames);
            return;
        }

        Assert.True(agent.HasExplicitToolSelection);
        Assert.True(agent.HasToolRestrictions);
        Assert.Equal(
            visibleToolNames.OrderBy(static name => name),
            agent.ToolNames.OrderBy(static name => name));
        Assert.Equal(NativeBrowserLogic.IsEmbeddedBrowserAvailable,
            agent.ToolNames.Contains(ToolDisplayHelper.BrowserOpenToolName));
        Assert.DoesNotContain("ui_list_windows", agent.ToolNames);
    }

    [Fact]
    public void SaveAgent_PreservesSelectedDesktopToolsAcrossPlatforms()
    {
        var agent = new LumiAgent
        {
            Name = "Cross-platform tools",
            ToolNames = [ToolDisplayHelper.BrowserOpenToolName, "ui_do"],
            HasExplicitToolSelection = true
        };
        var viewModel = CreateEditor(agent);

        viewModel.SaveAgentCommand.Execute(null);

        Assert.True(agent.HasExplicitToolSelection);
        Assert.Contains(ToolDisplayHelper.BrowserOpenToolName, agent.ToolNames);
        Assert.Contains("ui_do", agent.ToolNames);
        Assert.Equal(
            new[] { ToolDisplayHelper.BrowserOpenToolName, "ui_do" }.OrderBy(static name => name),
            agent.ToolNames.OrderBy(static name => name));
    }

    private static AgentsViewModel CreateEditor(LumiAgent agent)
    {
        var data = new AppData { Agents = [agent] };
        var viewModel = new AgentsViewModel(new DataStore(data));
        viewModel.EditAgentCommand.Execute(agent);
        return viewModel;
    }

    private static List<string> GetVisibleToolNames()
    {
        var viewModel = new AgentsViewModel(new DataStore(new AppData()));
        viewModel.NewAgentCommand.Execute(null);
        return viewModel.AvailableTools.Select(static tool => tool.ToolName).ToList();
    }
}
