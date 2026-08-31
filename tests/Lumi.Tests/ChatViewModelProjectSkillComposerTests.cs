using System.Threading;
using System.Reflection;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Capabilities;
using Lumi.ViewModels;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Composer lifecycle for capabilities the Copilot runtime supplies (project, personal, plugin,
/// built-in). The capability source is stubbed so these tests exercise the composer and selection
/// plumbing rather than discovery itself, which <see cref="CapabilityCatalogTests"/> owns.
/// </summary>
[Collection("Headless UI")]
public sealed class ChatViewModelProjectSkillComposerTests
{
    [Fact]
    public async Task ExternalProjectChange_LoadsAColdCapabilityQuery()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();

        try
        {
            await session.Dispatch(async () =>
            {
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var chat = new Chat { Title = "Chat" };
                var store = new DataStore(new AppData { Projects = [project], Chats = [chat] });
                using var catalog = new CapabilityCatalog(
                    new LumiCapabilityProvider(store),
                    new ScopedSkillProvider(tempRoot, ProjectSkillName, ProjectSkillDescription));
                using var viewModel = new ChatViewModel(
                    store,
                    TestCopilot.Shared,
                    capabilityCatalog: catalog)
                {
                    CurrentChat = chat,
                };

                chat.ProjectId = project.Id;
                viewModel.OnCurrentChatProjectChangedExternally();
                await WaitForAsync(() =>
                    viewModel.AvailableSkillChips.Any(chip => chip.Name == ProjectSkillName));
            }, CancellationToken.None);
        }
        finally
        {
            Cleanup(tempRoot);
        }
    }

    [Fact]
    public async Task ManagedProjectChange_LoadsAColdCapabilityQuery()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();

        try
        {
            await session.Dispatch(async () =>
            {
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var chat = new Chat { Title = "Chat", ProjectId = project.Id };
                var store = new DataStore(new AppData { Projects = [project], Chats = [chat] });
                using var catalog = new CapabilityCatalog(
                    new LumiCapabilityProvider(store),
                    new ScopedSkillProvider(tempRoot, ProjectSkillName, ProjectSkillDescription));
                using var viewModel = new ChatViewModel(
                    store,
                    TestCopilot.Shared,
                    capabilityCatalog: catalog)
                {
                    CurrentChat = chat,
                };

                viewModel.RefreshFeatureCatalogState(new FeatureChangeResult(
                    "updated",
                    DataChanged: true,
                    CapabilityContextChanged: true));
                await WaitForAsync(() =>
                    viewModel.AvailableSkillChips.Any(chip => chip.Name == ProjectSkillName));
            }, CancellationToken.None);
        }
        finally
        {
            Cleanup(tempRoot);
        }
    }

    private const string ProjectSkillName = "Sherlock Investigator";
    private const string ProjectSkillDescription = "Investigate Sherlock incidents.";

    [Fact]
    public async Task SwitchingProjectFilter_RefreshesDraftComposerSkillsAndPrunesSelection()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();
        var otherRoot = CreateProjectRoot();
        var skillNames = new List<string>();
        var activeSkillNamesAfterSwitch = new List<string>();

        try
        {
            await session.Dispatch(() =>
            {
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var otherProject = new Project { Name = "Other", WorkingDirectory = otherRoot };
                var store = new DataStore(new AppData { Projects = [project, otherProject] });
                var viewModel = CreateViewModel(store, tempRoot);

                viewModel.ActiveProjectFilterId = project.Id;
                skillNames = viewModel.AvailableSkillChips.Select(chip => chip.Name).ToList();

                viewModel.ActiveSkillChips.Add(viewModel.AvailableSkillChips.Single(
                    chip => chip.Name == ProjectSkillName));
                viewModel.ActiveProjectFilterId = otherProject.Id;
                activeSkillNamesAfterSwitch = viewModel.ActiveSkillChips
                    .OfType<StrataComposerChip>()
                    .Select(chip => chip.Name)
                    .ToList();
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.Contains(ProjectSkillName, skillNames);
            Assert.DoesNotContain(ProjectSkillName, activeSkillNamesAfterSwitch);
        }
        finally
        {
            Cleanup(tempRoot, otherRoot);
        }
    }

    [Fact]
    public async Task ProjectSkill_AppearsInComposerWithSourceHintAndPersistsSelection()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();
        StrataComposerChip? availableSkill = null;
        var persistedNames = new List<string>();
        var removed = false;

        try
        {
            await session.Dispatch(() =>
            {
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var chat = new Chat { Title = "Sherlock chat", ProjectId = project.Id };
                var store = new DataStore(new AppData { Projects = [project], Chats = [chat] });
                var viewModel = CreateViewModel(store, tempRoot, chat);

                viewModel.RefreshComposerCatalogs();
                availableSkill = viewModel.AvailableSkillChips.SingleOrDefault(
                    chip => chip.Name == ProjectSkillName);

                if (availableSkill is not null)
                    viewModel.ActiveSkillChips.Add(availableSkill);
                persistedNames = chat.ActiveExternalSkillNames.ToList();

                viewModel.RemoveSkillByName(ProjectSkillName);
                removed = chat.ActiveExternalSkillNames.Count == 0
                          && viewModel.ActiveSkillChips.All(chip =>
                              chip is not StrataComposerChip skill
                              || skill.Name != ProjectSkillName);
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.NotNull(availableSkill);
            Assert.Equal("\u26A1", availableSkill!.Glyph);
            Assert.Equal(ProjectSkillDescription, availableSkill.SecondaryText);
            // The picker tells the user where the capability came from.
            Assert.Equal(CapabilityOrigin.Project.Label, availableSkill.SourceLabel);
            Assert.Equal([ProjectSkillName], persistedNames);
            Assert.True(removed);
        }
        finally
        {
            Cleanup(tempRoot);
        }
    }

    [Fact]
    public async Task LumiSkill_CarriesItsOwnSourceHint()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();
        StrataComposerChip? lumiChip = null;

        try
        {
            await session.Dispatch(() =>
            {
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var store = new DataStore(new AppData
                {
                    Skills = [new Skill { Name = "Web Researcher", Description = "Searches the web." }],
                    Projects = [project],
                });
                var viewModel = CreateViewModel(store, tempRoot);
                viewModel.RefreshComposerCatalogs();
                lumiChip = viewModel.AvailableSkillChips.SingleOrDefault(chip => chip.Name == "Web Researcher");
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.NotNull(lumiChip);
            Assert.Equal(CapabilityOrigin.Lumi.Label, lumiChip!.SourceLabel);
        }
        finally
        {
            Cleanup(tempRoot);
        }
    }

    [Fact]
    public async Task LoadChatAsync_RestoresSelectedProjectSkill()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();
        var activeNames = new List<string>();
        var persistedNames = new List<string>();

        try
        {
            await session.Dispatch(async () =>
            {
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var chat = new Chat
                {
                    Title = "Sherlock chat",
                    ProjectId = project.Id,
                    ActiveExternalSkillNames = [ProjectSkillName]
                };
                var store = new DataStore(new AppData { Projects = [project], Chats = [chat] });
                var viewModel = CreateViewModel(store, tempRoot);

                await viewModel.LoadChatAsync(chat);
                activeNames = viewModel.ActiveSkillChips
                    .OfType<StrataComposerChip>()
                    .Select(chip => chip.Name)
                    .ToList();
                persistedNames = chat.ActiveExternalSkillNames.ToList();
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.Contains(ProjectSkillName, activeNames);
            Assert.Equal([ProjectSkillName], persistedNames);
        }
        finally
        {
            Cleanup(tempRoot);
        }
    }

    [Fact]
    public async Task ProjectSkillSelection_QueuesPerTurnActivationAndDequeuesOnRemoval()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();
        var queuedAfterSelection = new List<string>();
        var queuedAfterRemoval = new List<string>();

        try
        {
            await session.Dispatch(() =>
            {
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var chat = new Chat { Title = "Sherlock chat", ProjectId = project.Id };
                var store = new DataStore(new AppData { Projects = [project], Chats = [chat] });
                var viewModel = CreateViewModel(store, tempRoot, chat);
                viewModel.RefreshComposerCatalogs();

                var pendingActivations = GetPrivateField<List<string>>(
                    viewModel,
                    "_pendingExternalSkillInjections");

                viewModel.ActiveSkillChips.Add(viewModel.AvailableSkillChips.Single(
                    chip => chip.Name == ProjectSkillName));
                queuedAfterSelection = pendingActivations.ToList();

                viewModel.RemoveSkillByName(ProjectSkillName);
                queuedAfterRemoval = pendingActivations.ToList();
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.Equal([ProjectSkillName], queuedAfterSelection);
            Assert.Empty(queuedAfterRemoval);
        }
        finally
        {
            Cleanup(tempRoot);
        }
    }

    [Fact]
    public async Task ProjectSkillSelection_IsNotInjectedIntoSystemPrompt()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();
        var reconfigurationRequested = false;

        try
        {
            await session.Dispatch(() =>
            {
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var chat = new Chat { Title = "Sherlock chat", ProjectId = project.Id };
                var store = new DataStore(new AppData { Projects = [project], Chats = [chat] });
                var viewModel = CreateViewModel(store, tempRoot, chat);
                viewModel.RefreshComposerCatalogs();

                var pendingReconfigurations = GetPrivateField<HashSet<Guid>>(
                    viewModel,
                    "_pendingSessionReconfigurations");
                pendingReconfigurations.Clear();

                viewModel.ActiveSkillChips.Add(viewModel.AvailableSkillChips.Single(
                    chip => chip.Name == ProjectSkillName));

                // Selecting a runtime-owned skill must not rebuild the session: it carries no system
                // prompt content and is activated per-turn through the SDK slash command instead.
                reconfigurationRequested = pendingReconfigurations.Contains(chat.Id);
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.False(reconfigurationRequested);
        }
        finally
        {
            Cleanup(tempRoot);
        }
    }

    [Fact]
    public async Task ExternalProjectMove_PrunesUnavailableSelectedSkill()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();
        var otherRoot = CreateProjectRoot();
        var skillWasPruned = false;

        try
        {
            await session.Dispatch(() =>
            {
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var otherProject = new Project { Name = "Other", WorkingDirectory = otherRoot };
                var chat = new Chat { Title = "Sherlock chat", ProjectId = project.Id };
                var store = new DataStore(new AppData { Projects = [project, otherProject], Chats = [chat] });
                var viewModel = CreateViewModel(store, tempRoot, chat);
                viewModel.RefreshComposerCatalogs();
                viewModel.ActiveSkillChips.Add(viewModel.AvailableSkillChips.Single(
                    chip => chip.Name == ProjectSkillName));

                chat.ProjectId = otherProject.Id;
                viewModel.OnCurrentChatProjectChangedExternally();

                skillWasPruned = chat.ActiveExternalSkillNames.Count == 0
                                 && viewModel.ActiveSkillChips.All(chip =>
                                     chip is not StrataComposerChip skill
                                     || skill.Name != ProjectSkillName);
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.True(skillWasPruned);
        }
        finally
        {
            Cleanup(tempRoot, otherRoot);
        }
    }

    [Fact]
    public async Task McpConfigurationChange_PreservesSelectedProjectSkill()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();
        var skillRemainedSelected = false;

        try
        {
            await session.Dispatch(() =>
            {
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var chat = new Chat { Title = "Sherlock chat", ProjectId = project.Id };
                var store = new DataStore(new AppData { Projects = [project], Chats = [chat] });
                var viewModel = CreateViewModel(store, tempRoot, chat);
                viewModel.RefreshComposerCatalogs();
                viewModel.ActiveSkillChips.Add(viewModel.AvailableSkillChips.Single(
                    chip => chip.Name == ProjectSkillName));

                viewModel.InvalidateMcpSession();
                viewModel.RemoveSkillByName(ProjectSkillName);

                skillRemainedSelected = chat.ActiveExternalSkillNames.Count == 0
                                        && viewModel.ActiveSkillChips.All(chip =>
                                            chip is not StrataComposerChip skill
                                            || skill.Name != ProjectSkillName);
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.True(skillRemainedSelected);
        }
        finally
        {
            Cleanup(tempRoot);
        }
    }

    [Fact]
    public async Task StartingANewChat_ClearsQueuedSkillActivations()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();
        var queuedBeforeNewChat = new List<string>();
        var queuedAfterNewChat = new List<string>();

        try
        {
            await session.Dispatch(() =>
            {
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var chat = new Chat { Title = "Sherlock chat", ProjectId = project.Id };
                var store = new DataStore(new AppData { Projects = [project], Chats = [chat] });
                var viewModel = CreateViewModel(store, tempRoot, chat);
                viewModel.RefreshComposerCatalogs();

                var pendingActivations = GetPrivateField<List<string>>(
                    viewModel,
                    "_pendingExternalSkillInjections");

                viewModel.ActiveSkillChips.Add(viewModel.AvailableSkillChips.Single(
                    chip => chip.Name == ProjectSkillName));
                queuedBeforeNewChat = pendingActivations.ToList();

                // A skill queued but never sent must not leak into the next chat's first turn.
                viewModel.ClearChat();
                queuedAfterNewChat = pendingActivations.ToList();
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.Equal([ProjectSkillName], queuedBeforeNewChat);
            Assert.Empty(queuedAfterNewChat);
        }
        finally
        {
            Cleanup(tempRoot);
        }
    }

    [Fact]
    public async Task MixedSelection_RoutesLumiSkillsToThePromptAndProjectSkillsToTheSdk()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();
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
                var store = new DataStore(new AppData
                {
                    Skills = [lumiSkill],
                    Projects = [project],
                    Chats = [chat]
                });
                var viewModel = CreateViewModel(store, tempRoot, chat);
                viewModel.RefreshComposerCatalogs();

                var pendingLumi = GetPrivateField<List<Guid>>(viewModel, "_pendingSkillInjections");
                var pendingProject = GetPrivateField<List<string>>(
                    viewModel,
                    "_pendingExternalSkillInjections");

                viewModel.ActiveSkillChips.Add(viewModel.AvailableSkillChips.Single(
                    chip => chip.Name == "Web Researcher"));
                viewModel.ActiveSkillChips.Add(viewModel.AvailableSkillChips.Single(
                    chip => chip.Name == ProjectSkillName));

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
            Assert.Equal(["Web Researcher", ProjectSkillName], chipNames);
            Assert.Equal([lumiSkillId], queuedLumiSkills);
            Assert.Equal([ProjectSkillName], queuedProjectSkills);

            // Lumi-managed skills are inlined into the prompt; runtime skills never are — they are
            // activated through the SDK slash command instead.
            Assert.Contains("LUMI_SKILL_BODY_MARKER", promptAdditions);
            Assert.DoesNotContain(ProjectSkillName, promptAdditions);
        }
        finally
        {
            Cleanup(tempRoot);
        }
    }

    [Fact]
    public async Task RemovingALumiSkill_LeavesTheSelectedProjectSkillIntact()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();
        var remainingChips = new List<string>();
        var remainingLumiSkills = new List<Guid>();
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
                var store = new DataStore(new AppData
                {
                    Skills = [lumiSkill],
                    Projects = [project],
                    Chats = [chat]
                });
                var viewModel = CreateViewModel(store, tempRoot, chat);
                viewModel.RefreshComposerCatalogs();

                viewModel.ActiveSkillChips.Add(viewModel.AvailableSkillChips.Single(
                    chip => chip.Name == "Web Researcher"));
                viewModel.ActiveSkillChips.Add(viewModel.AvailableSkillChips.Single(
                    chip => chip.Name == ProjectSkillName));

                viewModel.RemoveSkillByName("Web Researcher");

                remainingChips = viewModel.ActiveSkillChips
                    .OfType<StrataComposerChip>()
                    .Select(chip => chip.Name)
                    .ToList();
                remainingLumiSkills = GetPrivateField<List<Guid>>(
                    viewModel,
                    "_pendingSkillInjections").ToList();
                remainingProjectSkills = GetPrivateField<List<string>>(
                    viewModel,
                    "_pendingExternalSkillInjections").ToList();
                viewModel.Dispose();
            }, CancellationToken.None);

            // Removing one skill system's selection must not disturb the other's, and the removed
            // skill's own queued delivery must go with it.
            Assert.Equal([ProjectSkillName], remainingChips);
            Assert.Empty(remainingLumiSkills);
            Assert.Equal([ProjectSkillName], remainingProjectSkills);
        }
        finally
        {
            Cleanup(tempRoot);
        }
    }

    [Fact]
    public async Task DeselectedLumiSkill_IsNotInjectedIntoTheNextSend()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();
        var queuedAfterSelection = new List<Guid>();
        var queuedAfterRemoval = new List<Guid>();
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
                    Content = "DESELECTED_SKILL_BODY_MARKER"
                };
                lumiSkillId = lumiSkill.Id;

                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var chat = new Chat
                {
                    Title = "Sherlock chat",
                    ProjectId = project.Id,
                    // A live session is what makes selection queue a one-shot prompt injection
                    // instead of waiting to be baked into the next system prompt.
                    CopilotSessionId = "session-1"
                };
                var store = new DataStore(new AppData
                {
                    Skills = [lumiSkill],
                    Projects = [project],
                    Chats = [chat]
                });
                var viewModel = CreateViewModel(store, tempRoot, chat);
                viewModel.RefreshComposerCatalogs();

                var pendingLumi = GetPrivateField<List<Guid>>(viewModel, "_pendingSkillInjections");

                // Select from the "/" menu, then remove the chip before sending.
                viewModel.ActiveSkillChips.Add(viewModel.AvailableSkillChips.Single(
                    chip => chip.Name == "Web Researcher"));
                queuedAfterSelection = pendingLumi.ToList();

                viewModel.RemoveSkillByName("Web Researcher");
                queuedAfterRemoval = pendingLumi.ToList();

                promptAdditions = (string)typeof(ChatViewModel)
                    .GetMethod("BuildSendPromptAdditions", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(viewModel, [true, null])!;
                viewModel.Dispose();
            }, CancellationToken.None);

            // Selection genuinely queued the injection, so the removal below is exercising it.
            Assert.Equal([lumiSkillId], queuedAfterSelection);

            // Deselecting retracts the queued delivery, so the next send does not inline the body
            // of a skill the user already removed from the composer.
            Assert.Empty(queuedAfterRemoval);
            Assert.DoesNotContain("DESELECTED_SKILL_BODY_MARKER", promptAdditions);
        }
        finally
        {
            Cleanup(tempRoot);
        }
    }

    [Fact]
    public async Task DeselectedProjectSkill_IsNotReactivatedWhenTheSessionIsRecreated()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();
        var queuedAfterRemoval = new List<string>();
        var activeAfterRemoval = new List<string>();
        var persistedAfterRemoval = new List<string>();

        try
        {
            await session.Dispatch(() =>
            {
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var chat = new Chat
                {
                    Title = "Sherlock chat",
                    ProjectId = project.Id,
                    CopilotSessionId = "session-1"
                };
                var store = new DataStore(new AppData { Projects = [project], Chats = [chat] });
                var viewModel = CreateViewModel(store, tempRoot, chat);
                viewModel.RefreshComposerCatalogs();

                viewModel.ActiveSkillChips.Add(viewModel.AvailableSkillChips.Single(
                    chip => chip.Name == ProjectSkillName));
                viewModel.RemoveSkillByName(ProjectSkillName);

                queuedAfterRemoval = GetPrivateField<List<string>>(
                    viewModel,
                    "_pendingExternalSkillInjections").ToList();
                activeAfterRemoval = GetPrivateField<List<string>>(
                    viewModel,
                    "_activeExternalSkillNames").ToList();
                persistedAfterRemoval = chat.ActiveExternalSkillNames.ToList();
                viewModel.Dispose();
            }, CancellationToken.None);

            // A recreated session re-activates the FULL selection rather than just the queue, so a
            // deselected skill has to be gone from the selection and its persisted copy too —
            // otherwise a session rebuild would silently resurrect it.
            Assert.Empty(queuedAfterRemoval);
            Assert.Empty(activeAfterRemoval);
            Assert.Empty(persistedAfterRemoval);
        }
        finally
        {
            Cleanup(tempRoot);
        }
    }

    [Fact]
    public async Task DeletedLumiSkillReference_IsNotResurrectedAsAProjectSkill()
    {
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();
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
                        new SkillReference { Name = ProjectSkillName }
                    ]
                };
                var chat = new Chat
                {
                    Title = "Sherlock chat",
                    ProjectId = project.Id,
                    Messages = [message]
                };
                var store = new DataStore(new AppData { Projects = [project], Chats = [chat] });
                var viewModel = CreateViewModel(store, tempRoot, chat);
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

            // The dangling reference must not become a runtime skill: activating it would fail with
            // "Unknown slash command" and surface a misleading error card.
            Assert.Equal([ProjectSkillName], activeExternalNames);
            Assert.DoesNotContain("Deleted Lumi Skill", activeChipNames);
        }
        finally
        {
            Cleanup(tempRoot);
        }
    }

    [Fact]
    public async Task ClearChat_LeavesTheNewDraftUncurated()
    {
        // Regression: ClearChat reset the draft-curation flag and then cleared the chip collection,
        // whose Reset notification set it straight back — so deleting the chat you were viewing
        // produced a draft that never auto-adopted a discovered MCP server again.
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();
        var curatedAfterClear = true;
        var activeAfterRefresh = new List<string>();

        try
        {
            var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
            var chat = new Chat { Title = "Sherlock chat", ProjectId = project.Id };
            var store = new DataStore(new AppData { Projects = [project], Chats = [chat] });
            var catalog = new CapabilityCatalog(
                new LumiCapabilityProvider(store),
                new ScopedMcpProvider(tempRoot, "workspace-files"));

            await session.Dispatch(() =>
            {
                var viewModel = new ChatViewModel(store, TestCopilot.Shared, capabilityCatalog: catalog)
                {
                    CurrentChat = chat
                };
                WarmCatalog(catalog, store);
                viewModel.RefreshComposerCatalogs();

                viewModel.ClearChat();
                curatedAfterClear = GetPrivateField<bool>(viewModel, "_draftMcpSelectionCurated");

                viewModel.ActiveProjectFilterId = project.Id;
                viewModel.RefreshComposerCatalogs();
                activeAfterRefresh = viewModel.ActiveMcpServerNames.ToList();
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.False(curatedAfterClear);
            Assert.Contains("workspace-files", activeAfterRefresh);
        }
        finally
        {
            Cleanup(tempRoot);
        }
    }

    [Fact]
    public async Task DeletedMcpServer_IsPrunedFromAnExistingChat()
    {
        // Regression: a saved name that no longer resolves was restored as a chip with no source
        // hint, and the prune predicate required one — so a server deleted on the MCP page stayed
        // checked in every existing chat forever.
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();
        var activeAfterRefresh = new List<string>();
        var persisted = new List<string>();

        try
        {
            var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
            var chat = new Chat
            {
                Title = "Sherlock chat",
                ProjectId = project.Id,
                // "deleted-server" is not in the store and nothing discovers it.
                ActiveMcpServerNames = ["deleted-server", "workspace-files"],
                HasExplicitMcpServerSelection = true,
            };
            var store = new DataStore(new AppData { Projects = [project], Chats = [chat] });
            var catalog = new CapabilityCatalog(
                new LumiCapabilityProvider(store),
                new ScopedMcpProvider(tempRoot, "workspace-files"));

            await session.Dispatch(() =>
            {
                var viewModel = new ChatViewModel(store, TestCopilot.Shared, capabilityCatalog: catalog)
                {
                    CurrentChat = chat
                };
                viewModel.ActiveMcpServerNames.Add("deleted-server");
                viewModel.ActiveMcpChips.Add(
                    new StrataTheme.Controls.StrataComposerChip("deleted-server", "🔌"));
                viewModel.ActiveMcpServerNames.Add("workspace-files");
                viewModel.ActiveMcpChips.Add(
                    new StrataTheme.Controls.StrataComposerChip("workspace-files", "🔌"));

                WarmCatalog(catalog, store);
                viewModel.RefreshComposerCatalogs();

                activeAfterRefresh = viewModel.ActiveMcpServerNames.ToList();
                persisted = chat.ActiveMcpServerNames.ToList();
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.DoesNotContain("deleted-server", activeAfterRefresh);
            Assert.DoesNotContain("deleted-server", persisted);
            // A server that still exists is untouched.
            Assert.Contains("workspace-files", activeAfterRefresh);
        }
        finally
        {
            Cleanup(tempRoot);
        }
    }

    [Fact]
    public async Task DraftMcpDeselection_SurvivesTheFirstSendAndALaterRefresh()
    {
        // Regression: a draft has no chat to record curation on, so removing a discovered MCP
        // before the first message left the new chat marked "not curated" — and the very next
        // composer refresh auto-selected the server straight back and persisted it.
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();
        var activeAfterRemoval = new List<string>();
        var activeAfterRefresh = new List<string>();
        var curatedOnDraft = false;

        try
        {
            await session.Dispatch(() =>
            {
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var store = new DataStore(new AppData { Projects = [project] });
                var catalog = new CapabilityCatalog(
                    new LumiCapabilityProvider(store),
                    new ScopedMcpProvider(tempRoot, "workspace-files"));
                WarmCatalog(catalog, store);

                // No CurrentChat: this is the draft composer before the first send.
                var viewModel = new ChatViewModel(store, TestCopilot.Shared, capabilityCatalog: catalog)
                {
                    ActiveProjectFilterId = project.Id
                };
                viewModel.RefreshComposerCatalogs();
                Assert.Null(viewModel.CurrentChat);
                Assert.Contains("workspace-files", viewModel.ActiveMcpServerNames);

                viewModel.RemoveMcpByName("workspace-files");
                activeAfterRemoval = viewModel.ActiveMcpServerNames.ToList();
                curatedOnDraft = GetPrivateField<bool>(viewModel, "_draftMcpSelectionCurated");

                // Discovery landing again must not undo the draft's removal.
                viewModel.RefreshComposerCatalogs();
                activeAfterRefresh = viewModel.ActiveMcpServerNames.ToList();
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.DoesNotContain("workspace-files", activeAfterRemoval);
            Assert.True(curatedOnDraft);
            Assert.DoesNotContain("workspace-files", activeAfterRefresh);
        }
        finally
        {
            Cleanup(tempRoot);
        }
    }

    [Fact]
    public async Task DeselectedDiscoveredMcp_IsNotResurrectedByALaterCatalogRefresh()
    {
        // Regression: a composer refresh (which now also runs when background discovery lands) used
        // to re-add every discovered MCP server, silently re-enabling one the user had removed.
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();
        var activeAfterRemoval = new List<string>();
        var activeAfterRefresh = new List<string>();
        var persisted = new List<string>();

        try
        {
            await session.Dispatch(() =>
            {
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var chat = new Chat { Title = "Sherlock chat", ProjectId = project.Id };
                var store = new DataStore(new AppData { Projects = [project], Chats = [chat] });
                var catalog = new CapabilityCatalog(new LumiCapabilityProvider(store), new ScopedMcpProvider(tempRoot, "workspace-files"));
                WarmCatalog(catalog, store);
                var viewModel = new ChatViewModel(store, TestCopilot.Shared, capabilityCatalog: catalog)
                {
                    CurrentChat = chat
                };

                viewModel.RefreshComposerCatalogs();
                Assert.Contains("workspace-files", viewModel.ActiveMcpServerNames);

                viewModel.RemoveMcpByName("workspace-files");
                activeAfterRemoval = viewModel.ActiveMcpServerNames.ToList();

                // Discovery landing again must respect the user's explicit choice.
                viewModel.RefreshComposerCatalogs();
                activeAfterRefresh = viewModel.ActiveMcpServerNames.ToList();
                persisted = chat.ActiveMcpServerNames.ToList();
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.DoesNotContain("workspace-files", activeAfterRemoval);
            Assert.DoesNotContain("workspace-files", activeAfterRefresh);
            Assert.DoesNotContain("workspace-files", persisted);
        }
        finally
        {
            Cleanup(tempRoot);
        }
    }

    [Fact]
    public async Task UnresolvedDiscovery_DoesNotEraseASavedProjectSkillSelection()
    {
        // Regression: the first chat opened for a project reads a snapshot before Copilot discovery
        // has run. Pruning the saved selection against that Lumi-only view deleted the user's
        // project/personal skill choices from disk, and nothing restored them.
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();
        var activeNames = new List<string>();
        var persisted = new List<string>();

        try
        {
            await session.Dispatch(async () =>
            {
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var chat = new Chat
                {
                    Title = "Sherlock chat",
                    ProjectId = project.Id,
                    ActiveExternalSkillNames = [ProjectSkillName]
                };
                var store = new DataStore(new AppData { Projects = [project], Chats = [chat] });
                // A provider that never reports keeps the snapshot unresolved for the whole test.
                var catalog = new CapabilityCatalog(new LumiCapabilityProvider(store), new NeverReportingProvider());
                var viewModel = new ChatViewModel(store, TestCopilot.Shared, capabilityCatalog: catalog);

                await viewModel.LoadChatAsync(chat);

                activeNames = viewModel.ActiveSkillChips
                    .OfType<StrataComposerChip>()
                    .Select(chip => chip.Name)
                    .ToList();
                persisted = chat.ActiveExternalSkillNames.ToList();
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.Contains(ProjectSkillName, activeNames);
            Assert.Equal([ProjectSkillName], persisted);
        }
        finally
        {
            Cleanup(tempRoot);
        }
    }

    [Fact]
    public async Task UnresolvedDiscovery_DoesNotEraseASavedMcpSelection()
    {
        // Regression: pruning MCP names against an unresolved snapshot also set
        // HasExplicitMcpServerSelection, so the dropped servers could never be re-added.
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();
        var activeNames = new List<string>();
        var persisted = new List<string>();

        try
        {
            await session.Dispatch(async () =>
            {
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var chat = new Chat
                {
                    Title = "Sherlock chat",
                    ProjectId = project.Id,
                    ActiveMcpServerNames = ["workspace-files"],
                    HasExplicitMcpServerSelection = true
                };
                var store = new DataStore(new AppData { Projects = [project], Chats = [chat] });
                var catalog = new CapabilityCatalog(new LumiCapabilityProvider(store), new NeverReportingProvider());
                var viewModel = new ChatViewModel(store, TestCopilot.Shared, capabilityCatalog: catalog);

                await viewModel.LoadChatAsync(chat);
                viewModel.RefreshComposerCatalogs();

                activeNames = viewModel.ActiveMcpServerNames.ToList();
                persisted = chat.ActiveMcpServerNames.ToList();
                viewModel.Dispose();
            }, CancellationToken.None);

            Assert.Contains("workspace-files", activeNames);
            Assert.Contains("workspace-files", persisted);
        }
        finally
        {
            Cleanup(tempRoot);
        }
    }

    [Fact]
    public async Task RestoredSelection_IsReconciledOnceDiscoveryLands()
    {
        // Regression: a chat opened before discovery resolved kept its MCP selection as a bare name
        // with no source hint, and that placeholder stayed invisible to the pruning logic forever
        // because only the available pickers were refreshed when discovery finally landed.
        using var session = HeadlessTestSession.Start();
        var tempRoot = CreateProjectRoot();
        string? labelBeforeDiscovery = null;
        string? labelAfterDiscovery = null;
        var namesBeforeDiscovery = new List<string>();

        try
        {
            await session.Dispatch(() =>
            {
                var project = new Project { Name = "Sherlock", WorkingDirectory = tempRoot };
                var chat = new Chat
                {
                    Title = "Sherlock chat",
                    ProjectId = project.Id,
                    ActiveMcpServerNames = ["workspace-files"],
                    HasExplicitMcpServerSelection = true,
                };
                var store = new DataStore(new AppData { Projects = [project], Chats = [chat] });
                var catalog = new CapabilityCatalog(
                    new LumiCapabilityProvider(store),
                    new ScopedMcpProvider(tempRoot, "workspace-files"));

                // Deliberately cold: discovery has not reported for this project yet, so the
                // restored selection is only a name.
                var viewModel = new ChatViewModel(store, TestCopilot.Shared, capabilityCatalog: catalog)
                {
                    CurrentChat = chat
                };
                viewModel.ActiveMcpServerNames.Add("workspace-files");
                viewModel.ActiveMcpChips.Add(
                    new StrataTheme.Controls.StrataComposerChip("workspace-files", "🔌"));
                viewModel.RefreshComposerCatalogs();

                namesBeforeDiscovery = viewModel.ActiveMcpServerNames.ToList();
                labelBeforeDiscovery = viewModel.ActiveMcpChips
                    .OfType<StrataTheme.Controls.StrataComposerChip>()
                    .First(chip => chip.Name == "workspace-files").SourceLabel;

                WarmCatalog(catalog, store);
                viewModel.RefreshComposerCatalogs();

                labelAfterDiscovery = viewModel.ActiveMcpChips
                    .OfType<StrataTheme.Controls.StrataComposerChip>()
                    .First(chip => chip.Name == "workspace-files").SourceLabel;
                viewModel.Dispose();
            }, CancellationToken.None);

            // The saved name survives the cold window, then gains its source hint.
            Assert.Contains("workspace-files", namesBeforeDiscovery);
            Assert.True(string.IsNullOrEmpty(labelBeforeDiscovery));
            Assert.False(string.IsNullOrEmpty(labelAfterDiscovery));
        }
        finally
        {
            Cleanup(tempRoot);
        }
    }

    private static ChatViewModel CreateViewModel(DataStore store, string skillRoot, Chat? currentChat = null)
    {
        var catalog = new CapabilityCatalog(
            new LumiCapabilityProvider(store),
            new ScopedSkillProvider(skillRoot, ProjectSkillName, ProjectSkillDescription));
        WarmCatalog(catalog, store);

        var viewModel = new ChatViewModel(store, TestCopilot.Shared, capabilityCatalog: catalog);
        if (currentChat is not null)
            viewModel.CurrentChat = currentChat;
        return viewModel;
    }

    /// <summary>
    /// Resolves the catalog for every project in the store so the composer paints from a complete
    /// snapshot, the same guarantee <c>LoadChatAsync</c> gives at runtime. The dispatcher is pumped
    /// rather than blocked: the load resumes on the caller's context, so blocking the UI thread here
    /// would deadlock it.
    /// </summary>
    private static void WarmCatalog(CapabilityCatalog catalog, DataStore store)
    {
        var queries = store.Data.Projects
            .Select(project => new CapabilityQuery(
                ProjectContextDirectoryHelper.GetExistingContextDirectories(
                    project.WorkingDirectory ?? "",
                    project)))
            .Append(CapabilityQuery.Empty);

        foreach (var query in queries)
        {
            var load = catalog.LoadAsync(query);
            while (!load.IsCompleted)
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            load.GetAwaiter().GetResult();
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(condition(), "The capability refresh did not reach the composer.");
    }

    /// <summary>
    /// Stands in for the Copilot runtime: reports one project-scoped skill, but only while the
    /// query still covers the owning directory.
    /// </summary>
    private sealed class ScopedSkillProvider(string root, string name, string description) : ICapabilityProvider
    {
        public string Id => "test-runtime";
        public Task<CapabilityProviderResult> LoadAsync(CapabilityQuery query, CancellationToken cancellationToken)
        {
            var inScope = query.WorkingDirectories.Any(
                directory => string.Equals(directory, root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));

            return Task.FromResult(inScope
                ? new CapabilityProviderResult(
                [
                    new CapabilityDescriptor
                    {
                        Kind = CapabilityKind.Skill,
                        Name = name,
                        Origin = CapabilityOrigin.Project,
                        Description = description,
                        Glyph = CopilotSdkCapabilityProvider.SkillGlyph,
                        SourcePath = Path.Combine(root, ".github", "skills", "sherlock", "SKILL.md"),
                    }
                ])
                : CapabilityProviderResult.Empty);
        }
    }

    /// <summary>Stands in for a runtime-discovered MCP server scoped to one directory.</summary>
    private sealed class ScopedMcpProvider(string root, string name) : ICapabilityProvider
    {
        public string Id => "test-runtime-mcp";
        public Task<CapabilityProviderResult> LoadAsync(CapabilityQuery query, CancellationToken cancellationToken)
        {
            var inScope = query.WorkingDirectories.Any(
                directory => string.Equals(directory, root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));

            return Task.FromResult(inScope
                ? new CapabilityProviderResult(
                [
                    new CapabilityDescriptor
                    {
                        Kind = CapabilityKind.McpServer,
                        Name = name,
                        Origin = CapabilityOrigin.Workspace,
                        Glyph = CopilotSdkCapabilityProvider.McpGlyph,
                    }
                ])
                : CapabilityProviderResult.Empty);
        }
    }

    /// <summary>Stands in for a Copilot runtime that has not answered yet, so the snapshot stays unresolved.</summary>
    private sealed class NeverReportingProvider : ICapabilityProvider
    {
        public string Id => "test-unresolved";
        public Task<CapabilityProviderResult> LoadAsync(CapabilityQuery query, CancellationToken cancellationToken)
            => Task.FromResult(CapabilityProviderResult.Unavailable);
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

    private static string CreateProjectRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-project-skill-composer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    private static void Cleanup(params string[] roots)
    {
        foreach (var root in roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }
}
