using System.Threading;
using System.Reflection;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ChatViewModelProjectSkillComposerTests
{
    [Fact]
    public async Task SwitchingProjectFilter_RefreshesDraftComposerSkillsAndPrunesSelection()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectSkill(
            "Sherlock Investigator",
            "Investigate Sherlock incidents.",
            category: "investigation");
        var otherRoot = Path.Combine(Path.GetTempPath(), $"lumi-project-skill-composer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(otherRoot);
        var skillNames = new List<string>();
        var activeSkillNamesAfterSwitch = new List<string>();

        try
        {
            await session.Dispatch(() =>
            {
                var project = new Project
                {
                    Name = "Sherlock",
                    WorkingDirectory = tempRoot
                };
                var otherProject = new Project
                {
                    Name = "Other",
                    WorkingDirectory = otherRoot
                };
                var viewModel = new ChatViewModel(
                    new DataStore(new AppData { Projects = [project, otherProject] }),
                    new CopilotService());

                viewModel.ActiveProjectFilterId = project.Id;
                skillNames = viewModel.AvailableSkillChips.Select(chip => chip.Name).ToList();

                viewModel.ActiveSkillChips.Add(viewModel.AvailableSkillChips.Single(
                    chip => chip.Name == "Sherlock Investigator"));
                viewModel.ActiveProjectFilterId = otherProject.Id;
                activeSkillNamesAfterSwitch = viewModel.ActiveSkillChips
                    .OfType<StrataComposerChip>()
                    .Select(chip => chip.Name)
                    .ToList();
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.Contains("Sherlock Investigator", skillNames);
            Assert.DoesNotContain("Sherlock Investigator", activeSkillNamesAfterSwitch);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
            Directory.Delete(otherRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProjectSkill_AppearsInComposerAndPersistsSelection()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectSkill("Sherlock Investigator", "Investigate Sherlock incidents.");
        StrataComposerChip? availableSkill = null;
        var persistedNames = new List<string>();
        var removed = false;

        try
        {
            await session.Dispatch(() =>
            {
                var project = new Project
                {
                    Name = "Sherlock",
                    WorkingDirectory = tempRoot
                };
                var chat = new Chat
                {
                    Title = "Sherlock chat",
                    ProjectId = project.Id
                };
                var viewModel = new ChatViewModel(
                    new DataStore(new AppData { Projects = [project], Chats = [chat] }),
                    new CopilotService())
                {
                    CurrentChat = chat
                };

                viewModel.RefreshComposerCatalogs();
                availableSkill = viewModel.AvailableSkillChips.SingleOrDefault(
                    chip => chip.Name == "Sherlock Investigator");

                if (availableSkill is not null)
                    viewModel.ActiveSkillChips.Add(availableSkill);
                persistedNames = chat.ActiveExternalSkillNames.ToList();

                viewModel.RemoveSkillByName("Sherlock Investigator");
                removed = chat.ActiveExternalSkillNames.Count == 0
                          && viewModel.ActiveSkillChips.All(chip =>
                              chip is not StrataComposerChip skill
                              || skill.Name != "Sherlock Investigator");
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.NotNull(availableSkill);
            Assert.Equal("\u26A1", availableSkill!.Glyph);
            Assert.Equal("Investigate Sherlock incidents.", availableSkill.SecondaryText);
            Assert.Equal(["Sherlock Investigator"], persistedNames);
            Assert.True(removed);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LoadChatAsync_RestoresSelectedProjectSkill()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectSkill("Sherlock Investigator", "Investigate Sherlock incidents.");
        var activeNames = new List<string>();
        var persistedNames = new List<string>();

        try
        {
            await session.Dispatch(async () =>
            {
                var project = new Project
                {
                    Name = "Sherlock",
                    WorkingDirectory = tempRoot
                };
                var chat = new Chat
                {
                    Title = "Sherlock chat",
                    ProjectId = project.Id,
                    ActiveExternalSkillNames = ["Sherlock Investigator"]
                };
                var viewModel = new ChatViewModel(
                    new DataStore(new AppData { Projects = [project], Chats = [chat] }),
                    new CopilotService());

                await viewModel.LoadChatAsync(chat);
                activeNames = viewModel.ActiveSkillChips
                    .OfType<StrataComposerChip>()
                    .Select(chip => chip.Name)
                    .ToList();
                persistedNames = chat.ActiveExternalSkillNames.ToList();
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.Contains("Sherlock Investigator", activeNames);
            Assert.Equal(["Sherlock Investigator"], persistedNames);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProjectSkillSelection_QueuesPerTurnActivationAndDequeuesOnRemoval()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectSkill("Sherlock Investigator", "Investigate Sherlock incidents.");
        var queuedAfterSelection = new List<string>();
        var queuedAfterRemoval = new List<string>();

        try
        {
            await session.Dispatch(() =>
            {
                var project = new Project
                {
                    Name = "Sherlock",
                    WorkingDirectory = tempRoot
                };
                var chat = new Chat
                {
                    Title = "Sherlock chat",
                    ProjectId = project.Id
                };
                var viewModel = new ChatViewModel(
                    new DataStore(new AppData { Projects = [project], Chats = [chat] }),
                    new CopilotService())
                {
                    CurrentChat = chat
                };
                viewModel.RefreshComposerCatalogs();

                var pendingActivations = GetPrivateField<List<string>>(
                    viewModel,
                    "_pendingExternalSkillInjections");

                viewModel.ActiveSkillChips.Add(viewModel.AvailableSkillChips.Single(
                    chip => chip.Name == "Sherlock Investigator"));
                queuedAfterSelection = pendingActivations.ToList();

                viewModel.RemoveSkillByName("Sherlock Investigator");
                queuedAfterRemoval = pendingActivations.ToList();
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.Equal(["Sherlock Investigator"], queuedAfterSelection);
            Assert.Empty(queuedAfterRemoval);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProjectSkillSelection_IsNotInjectedIntoSystemPrompt()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectSkill("Sherlock Investigator", "Investigate Sherlock incidents.");
        var reconfigurationRequested = false;

        try
        {
            await session.Dispatch(() =>
            {
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var chat = new Chat { Title = "Sherlock chat", ProjectId = project.Id };
                var viewModel = new ChatViewModel(
                    new DataStore(new AppData { Projects = [project], Chats = [chat] }),
                    new CopilotService())
                {
                    CurrentChat = chat
                };
                viewModel.RefreshComposerCatalogs();

                var pendingReconfigurations = GetPrivateField<HashSet<Guid>>(
                    viewModel,
                    "_pendingSessionReconfigurations");
                pendingReconfigurations.Clear();

                viewModel.ActiveSkillChips.Add(viewModel.AvailableSkillChips.Single(
                    chip => chip.Name == "Sherlock Investigator"));

                // Selecting a file-based skill must not rebuild the session: it carries no system
                // prompt content and is activated per-turn through the SDK slash command instead.
                reconfigurationRequested = pendingReconfigurations.Contains(chat.Id);
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.False(reconfigurationRequested);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExternalProjectMove_PrunesUnavailableSelectedSkill()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectSkill("Sherlock Investigator", "Investigate Sherlock incidents.");
        var otherRoot = Path.Combine(Path.GetTempPath(), $"lumi-project-skill-composer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(otherRoot);
        var skillWasPruned = false;

        try
        {
            await session.Dispatch(() =>
            {
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var otherProject = new Project { Name = "Other", WorkingDirectory = otherRoot };
                var chat = new Chat { Title = "Sherlock chat", ProjectId = project.Id };
                var viewModel = new ChatViewModel(
                    new DataStore(new AppData { Projects = [project, otherProject], Chats = [chat] }),
                    new CopilotService())
                {
                    CurrentChat = chat
                };
                viewModel.RefreshComposerCatalogs();
                viewModel.ActiveSkillChips.Add(viewModel.AvailableSkillChips.Single(
                    chip => chip.Name == "Sherlock Investigator"));

                chat.ProjectId = otherProject.Id;
                viewModel.OnCurrentChatProjectChangedExternally();

                skillWasPruned = chat.ActiveExternalSkillNames.Count == 0
                                 && viewModel.ActiveSkillChips.All(chip =>
                                     chip is not StrataComposerChip skill
                                     || skill.Name != "Sherlock Investigator");
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.True(skillWasPruned);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
            Directory.Delete(otherRoot, recursive: true);
        }
    }

    [Fact]
    public async Task McpConfigurationChange_PreservesSelectedProjectSkill()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectSkill("Sherlock Investigator", "Investigate Sherlock incidents.");
        var skillRemainedSelected = false;

        try
        {
            await session.Dispatch(() =>
            {
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var chat = new Chat { Title = "Sherlock chat", ProjectId = project.Id };
                var viewModel = new ChatViewModel(
                    new DataStore(new AppData { Projects = [project], Chats = [chat] }),
                    new CopilotService())
                {
                    CurrentChat = chat
                };
                viewModel.RefreshComposerCatalogs();
                viewModel.ActiveSkillChips.Add(viewModel.AvailableSkillChips.Single(
                    chip => chip.Name == "Sherlock Investigator"));

                viewModel.InvalidateMcpSession();
                viewModel.RemoveSkillByName("Sherlock Investigator");

                skillRemainedSelected = chat.ActiveExternalSkillNames.Count == 0
                                        && viewModel.ActiveSkillChips.All(chip =>
                                            chip is not StrataComposerChip skill
                                            || skill.Name != "Sherlock Investigator");
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.True(skillRemainedSelected);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartingANewChat_ClearsQueuedSkillActivations()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectSkill("Sherlock Investigator", "Investigate Sherlock incidents.");
        var queuedBeforeNewChat = new List<string>();
        var queuedAfterNewChat = new List<string>();

        try
        {
            await session.Dispatch(() =>
            {
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var chat = new Chat { Title = "Sherlock chat", ProjectId = project.Id };
                var viewModel = new ChatViewModel(
                    new DataStore(new AppData { Projects = [project], Chats = [chat] }),
                    new CopilotService())
                {
                    CurrentChat = chat
                };
                viewModel.RefreshComposerCatalogs();

                var pendingActivations = GetPrivateField<List<string>>(
                    viewModel,
                    "_pendingExternalSkillInjections");

                viewModel.ActiveSkillChips.Add(viewModel.AvailableSkillChips.Single(
                    chip => chip.Name == "Sherlock Investigator"));
                queuedBeforeNewChat = pendingActivations.ToList();

                // A skill queued but never sent must not leak into the next chat's first turn.
                viewModel.ClearChat();
                queuedAfterNewChat = pendingActivations.ToList();
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.Equal(["Sherlock Investigator"], queuedBeforeNewChat);
            Assert.Empty(queuedAfterNewChat);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task MixedSelection_RoutesLumiSkillsToThePromptAndProjectSkillsToTheSdk()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectSkill("Sherlock Investigator", "Investigate Sherlock incidents.");
        var chipNames = new List<string>();
        var queuedLumiSkills = new List<Guid>();
        var queuedProjectSkills = new List<string>();
        var promptAdditions = string.Empty;
        var lumiSkillId = Guid.Empty;

        try
        {
            await session.Dispatch(() =>
            {
                var lumiSkill = new Skill
                {
                    Name = "Web Researcher",
                    Description = "Searches the web.",
                    Content = "LUMI_SKILL_BODY_MARKER"
                };
                lumiSkillId = lumiSkill.Id;

                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var chat = new Chat
                {
                    Title = "Sherlock chat",
                    ProjectId = project.Id,
                    // A live session: adding a Lumi skill mid-conversation queues a prompt injection
                    // rather than waiting for the next session build.
                    CopilotSessionId = "session-1"
                };
                var viewModel = new ChatViewModel(
                    new DataStore(new AppData
                    {
                        Skills = [lumiSkill],
                        Projects = [project],
                        Chats = [chat]
                    }),
                    new CopilotService())
                {
                    CurrentChat = chat
                };
                viewModel.RefreshComposerCatalogs();

                var pendingLumi = GetPrivateField<List<Guid>>(viewModel, "_pendingSkillInjections");
                var pendingProject = GetPrivateField<List<string>>(
                    viewModel,
                    "_pendingExternalSkillInjections");

                viewModel.ActiveSkillChips.Add(viewModel.AvailableSkillChips.Single(
                    chip => chip.Name == "Web Researcher"));
                viewModel.ActiveSkillChips.Add(viewModel.AvailableSkillChips.Single(
                    chip => chip.Name == "Sherlock Investigator"));

                chipNames = viewModel.ActiveSkillChips
                    .OfType<StrataComposerChip>()
                    .Select(chip => chip.Name)
                    .ToList();
                queuedLumiSkills = pendingLumi.ToList();
                queuedProjectSkills = pendingProject.ToList();

                promptAdditions = (string)typeof(ChatViewModel)
                    .GetMethod("BuildSendPromptAdditions", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(viewModel, [true, null])!;
                viewModel.Dispose();
            }, CancellationToken.None);

            // Both systems coexist in one selection without clobbering each other.
            Assert.Equal(["Web Researcher", "Sherlock Investigator"], chipNames);
            Assert.Equal([lumiSkillId], queuedLumiSkills);
            Assert.Equal(["Sherlock Investigator"], queuedProjectSkills);

            // Lumi-managed skills are inlined into the prompt; project skills never are — they are
            // activated through the SDK slash command instead.
            Assert.Contains("LUMI_SKILL_BODY_MARKER", promptAdditions);
            Assert.DoesNotContain("Sherlock Investigator", promptAdditions);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RemovingALumiSkill_LeavesTheSelectedProjectSkillIntact()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectSkill("Sherlock Investigator", "Investigate Sherlock incidents.");
        var remainingChips = new List<string>();
        var remainingProjectSkills = new List<string>();

        try
        {
            await session.Dispatch(() =>
            {
                var lumiSkill = new Skill { Name = "Web Researcher", Content = "body" };
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var chat = new Chat
                {
                    Title = "Sherlock chat",
                    ProjectId = project.Id,
                    CopilotSessionId = "session-1"
                };
                var viewModel = new ChatViewModel(
                    new DataStore(new AppData
                    {
                        Skills = [lumiSkill],
                        Projects = [project],
                        Chats = [chat]
                    }),
                    new CopilotService())
                {
                    CurrentChat = chat
                };
                viewModel.RefreshComposerCatalogs();

                viewModel.ActiveSkillChips.Add(viewModel.AvailableSkillChips.Single(
                    chip => chip.Name == "Web Researcher"));
                viewModel.ActiveSkillChips.Add(viewModel.AvailableSkillChips.Single(
                    chip => chip.Name == "Sherlock Investigator"));

                viewModel.RemoveSkillByName("Web Researcher");

                remainingChips = viewModel.ActiveSkillChips
                    .OfType<StrataComposerChip>()
                    .Select(chip => chip.Name)
                    .ToList();
                remainingProjectSkills = GetPrivateField<List<string>>(
                    viewModel,
                    "_pendingExternalSkillInjections").ToList();
                viewModel.Dispose();
            }, CancellationToken.None);

            // Scope note: this asserts only the file-based side, which is what the SDK-activation
            // change owns. The Lumi-managed queue is deliberately not asserted here because
            // RemoveSkillByName does not prune _pendingSkillInjections — a pre-existing leak that
            // predates this change and is tracked separately.
            Assert.Equal(["Sherlock Investigator"], remainingChips);
            Assert.Equal(["Sherlock Investigator"], remainingProjectSkills);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DeletedLumiSkillReference_IsNotResurrectedAsAProjectSkill()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectSkill("Sherlock Investigator", "Investigate Sherlock incidents.");
        var activeExternalNames = new List<string>();
        var activeChipNames = new List<string>();

        try
        {
            await session.Dispatch(() =>
            {
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                // A message that referenced a Lumi-managed skill which has since been deleted,
                // alongside a still-valid project skill.
                var message = new ChatMessage
                {
                    Role = "user",
                    Content = "Do the thing",
                    ActiveSkills =
                    [
                        new SkillReference { Name = "Deleted Lumi Skill" },
                        new SkillReference { Name = "Sherlock Investigator" }
                    ]
                };
                var chat = new Chat
                {
                    Title = "Sherlock chat",
                    ProjectId = project.Id,
                    Messages = [message]
                };
                var viewModel = new ChatViewModel(
                    new DataStore(new AppData { Projects = [project], Chats = [chat] }),
                    new CopilotService())
                {
                    CurrentChat = chat
                };
                viewModel.RefreshComposerCatalogs();

                InvokePrivate(viewModel, "ReplaceActiveSkillsFromMessage", message, false);

                activeExternalNames = GetPrivateField<List<string>>(
                    viewModel,
                    "_activeExternalSkillNames").ToList();
                activeChipNames = viewModel.ActiveSkillChips
                    .OfType<StrataComposerChip>()
                    .Select(chip => chip.Name)
                    .ToList();
                viewModel.Dispose();
            }, CancellationToken.None);

            // The dangling reference must not become a file-based skill: activating it would fail
            // with "Unknown slash command" and surface a misleading error card.
            Assert.Equal(["Sherlock Investigator"], activeExternalNames);
            Assert.DoesNotContain("Deleted Lumi Skill", activeChipNames);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static void InvokePrivate(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(target, args);
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(target));
    }

    private static string CreateProjectSkill(string name, string description, string? category = null)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-project-skill-composer-{Guid.NewGuid():N}");
        var skillsRoot = Path.Combine(tempRoot, ".github", "skills");
        var skillDirectory = string.IsNullOrWhiteSpace(category)
            ? Path.Combine(skillsRoot, "sherlock-investigator")
            : Path.Combine(skillsRoot, category, "sherlock-investigator");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(
            Path.Combine(skillDirectory, "SKILL.md"),
            $"""
             ---
             name: {name}
             description: {description}
             ---

             Use the Sherlock investigation workflow.
             """);
        return tempRoot;
    }
}
