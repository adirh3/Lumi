using System;
using System.Collections.Generic;
using System.Linq;
using Lumi.Models;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Covers the pure decision logic behind a chat's "Move to Project" submenu — the part that
/// can't be exercised through a right-click in a headless/CI environment. Rendering (glyphs,
/// click wiring) lives in the code-behind; here we assert the ordered targets and current-home
/// flagging that drive it.
/// </summary>
public sealed class ChatMoveTargetBuilderTests
{
    private const string AllProjectsHeader = "All projects";

    private static Project Project(string name) => new() { Name = name };

    [Fact]
    public void AllProjectsEntry_IsAlwaysFirst()
    {
        var chat = new Chat { ProjectId = null };

        var targets = ChatMoveTargetBuilder.Build([Project("A"), Project("B")], chat, AllProjectsHeader);

        var first = targets[0];
        Assert.Equal(ChatMoveTargetKind.AllProjects, first.Kind);
        Assert.Equal(AllProjectsHeader, first.Header);
        Assert.Null(first.ProjectId);
    }

    [Fact]
    public void UnfiledChat_MarksAllProjectsAsCurrent()
    {
        var chat = new Chat { ProjectId = null };
        var a = Project("A");
        var b = Project("B");

        var targets = ChatMoveTargetBuilder.Build([a, b], chat, AllProjectsHeader);

        Assert.True(targets[0].IsCurrent);                       // All projects = current home
        Assert.All(targets.Skip(1), t => Assert.False(t.IsCurrent));
    }

    [Fact]
    public void ChatInProject_MarksThatProjectAsCurrent_AndAllProjectsMovable()
    {
        var a = Project("A");
        var b = Project("B");
        var chat = new Chat { ProjectId = a.Id };

        var targets = ChatMoveTargetBuilder.Build([a, b], chat, AllProjectsHeader);

        Assert.False(targets[0].IsCurrent);                      // "All projects" is now an actionable move-out
        var projectTargets = targets.Where(t => t.Kind == ChatMoveTargetKind.Project).ToList();
        Assert.True(projectTargets.Single(t => t.ProjectId == a.Id).IsCurrent);
        Assert.False(projectTargets.Single(t => t.ProjectId == b.Id).IsCurrent);
    }

    [Fact]
    public void PreservesProjectOrder_AndUsesProjectNamesAsHeaders()
    {
        var a = Project("Alpha");
        var b = Project("Beta");
        var c = Project("Gamma");
        var chat = new Chat { ProjectId = null };

        var targets = ChatMoveTargetBuilder.Build([a, b, c], chat, AllProjectsHeader);

        var projectTargets = targets.Where(t => t.Kind == ChatMoveTargetKind.Project).ToList();
        Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, projectTargets.Select(t => t.Header));
        Assert.Equal(new Guid?[] { a.Id, b.Id, c.Id }, projectTargets.Select(t => t.ProjectId));
    }

    [Fact]
    public void NoProjects_ReturnsOnlyAllProjectsEntry()
    {
        var chat = new Chat { ProjectId = null };

        var targets = ChatMoveTargetBuilder.Build(Array.Empty<Project>(), chat, AllProjectsHeader);

        var only = Assert.Single(targets);
        Assert.Equal(ChatMoveTargetKind.AllProjects, only.Kind);
        Assert.True(only.IsCurrent);
    }

    [Fact]
    public void ChatInUnknownProject_FlagsNothingAsCurrent()
    {
        // Chat references a project id that is no longer in the list (e.g. deleted project).
        var chat = new Chat { ProjectId = Guid.NewGuid() };

        var targets = ChatMoveTargetBuilder.Build([Project("A"), Project("B")], chat, AllProjectsHeader);

        Assert.All(targets, t => Assert.False(t.IsCurrent));
    }

    [Fact]
    public void CountIsProjectsPlusOne()
    {
        var projects = new List<Project> { Project("A"), Project("B"), Project("C") };
        var chat = new Chat { ProjectId = null };

        var targets = ChatMoveTargetBuilder.Build(projects, chat, AllProjectsHeader);

        Assert.Equal(projects.Count + 1, targets.Count);
    }
}
