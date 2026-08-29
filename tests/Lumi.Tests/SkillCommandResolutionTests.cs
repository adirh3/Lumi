using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// A skill's invocable slash-command id is not always its name — plugin-supplied skills are
/// namespaced as <c>&lt;plugin&gt;:&lt;skill&gt;</c>. Activation resolves the id from the session's
/// own command list, so every source can be activated per turn.
/// </summary>
public sealed class SkillCommandResolutionTests
{
    [Fact]
    public void PrefersAnExactCommandMatch()
    {
        string[] commands = ["Publish-New-Version", "context-engineering:Publish-New-Version"];

        Assert.Equal(
            "Publish-New-Version",
            ChatViewModel.ResolveSkillCommandId(commands, "Publish-New-Version"));
    }

    [Fact]
    public void ResolvesAPluginNamespacedCommand()
    {
        // Regression: plugin skills are offered by their bare name but only invocable namespaced,
        // so activating them by name failed with "Couldn't load the skill".
        string[] commands = ["Publish-New-Version", "context-engineering:context-map"];

        Assert.Equal(
            "context-engineering:context-map",
            ChatViewModel.ResolveSkillCommandId(commands, "context-map"));
    }

    [Fact]
    public void MatchesTheNamespacedCommandCaseInsensitively()
    {
        string[] commands = ["context-engineering:Context-Map"];

        Assert.Equal(
            "context-engineering:Context-Map",
            ChatViewModel.ResolveSkillCommandId(commands, "context-map"));
    }

    [Fact]
    public void FallsBackToTheSkillNameWhenTwoPluginsCollide()
    {
        // Ambiguous: picking either plugin arbitrarily could run the wrong skill, so defer to the
        // runtime's own resolution instead of guessing.
        string[] commands = ["alpha:shared", "beta:shared"];

        Assert.Equal("shared", ChatViewModel.ResolveSkillCommandId(commands, "shared"));
    }

    [Fact]
    public void FallsBackToTheSkillNameWhenTheSessionListedNoCommands()
    {
        Assert.Equal("Some-Skill", ChatViewModel.ResolveSkillCommandId([], "Some-Skill"));
    }
}
