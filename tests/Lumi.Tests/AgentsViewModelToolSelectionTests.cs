using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class AgentsViewModelToolSelectionTests
{
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
    public void SaveAgent_OnNonWindows_DoesNotReenableHiddenWindowsTools()
    {
        if (OperatingSystem.IsWindows())
            return;

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

        Assert.True(agent.HasExplicitToolSelection);
        Assert.True(agent.HasToolRestrictions);
        Assert.Equal(
            visibleToolNames.OrderBy(static name => name),
            agent.ToolNames.OrderBy(static name => name));
        Assert.DoesNotContain(ToolDisplayHelper.BrowserOpenToolName, agent.ToolNames);
        Assert.DoesNotContain("ui_list_windows", agent.ToolNames);
    }

    [Fact]
    public void SaveAgent_OnNonWindows_PreservesSelectedHiddenWindowsTools()
    {
        if (OperatingSystem.IsWindows())
            return;

        var visibleToolNames = GetVisibleToolNames();
        var agent = new LumiAgent
        {
            Name = "Cross-platform tools",
            ToolNames = [.. visibleToolNames, ToolDisplayHelper.BrowserOpenToolName],
            HasExplicitToolSelection = true
        };
        var viewModel = CreateEditor(agent);

        viewModel.SaveAgentCommand.Execute(null);

        Assert.True(agent.HasExplicitToolSelection);
        Assert.Contains(ToolDisplayHelper.BrowserOpenToolName, agent.ToolNames);
        Assert.Equal(
            visibleToolNames.Append(ToolDisplayHelper.BrowserOpenToolName).OrderBy(static name => name),
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
