using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

/// <summary>
/// The phone's new chat as the PC's launchpad projects it: For you follows the project the chat starts in, each tab
/// holds its starters, and a starter fills the composer with its blanks. The starters row lights the most specific
/// starter that fits the draft, shows a draft none fits dashed with a pin, fits the screen and gathers the rest into a
/// stack whose speed dial rises above it; the starters' sheets and the new starter sheet ask the PC over the real wire,
/// naming a starter on the default model without a model. A PC from before the launchpad keeps its three setups.
/// </summary>
public sealed class MobileLaunchpadTests
{
    private static readonly Guid LumiId = Guid.NewGuid();
    private static readonly Guid TravelId = Guid.NewGuid();
    private static readonly Guid AgentId = Guid.NewGuid();
    private static readonly Guid WaitingChatId = Guid.NewGuid();

    /// <summary>
    /// A starter as the PC projects it. One on the default model (no <paramref name="model"/>) starts with the default
    /// model now, Model A with High effort, which the phone never names back. A kept one has an id of its own, which
    /// changes to what it starts with keep (<paramref name="id"/>, else its name's).
    /// </summary>
    internal static RemoteChatSetup Starter(
        string title, Guid? projectId = null, Guid? agentId = null, bool worktree = false, string? model = null,
        string? effort = null, bool kept = false, string? icon = null, string? note = null, string? id = null)
    {
        var place = projectId == LumiId ? "Lumi" : projectId == TravelId ? "Travel" : agentId is not null ? "Coding Lumi" : "Just chat";
        var isDefault = model is null;
        var startEffort = isDefault ? "high" : effort;
        return new RemoteChatSetup
        {
            Id = id ?? (kept ? $"kept:{title}" : $"{projectId:N}:{agentId:N}:{worktree}:{model}:{effort}"),
            Title = title,
            PlaceTitle = place,
            Description = place,
            Glyph = projectId is not null ? place[..1] : agentId is not null ? "⚡" : "✦",
            ProjectId = projectId,
            ProjectName = projectId is null ? null : place,
            AgentId = agentId,
            AgentName = agentId is null ? null : "Coding Lumi",
            AgentGlyph = agentId is null ? null : "⚡",
            UseWorktree = worktree,
            Model = model ?? "model-a",
            Effort = startEffort,
            Quality = startEffort is null ? null : char.ToUpperInvariant(startEffort[0]) + startEffort[1..],
            IsDefaultModel = isDefault,
            IsKept = kept,
            CanHide = !kept && (projectId is not null || agentId is not null),
            Icon = icon,
            Note = note,
            CanUseWorktree = projectId == LumiId
        };
    }

    /// <summary>
    /// Two kept starters (UI dev on Model B, Fixes on the default model, both Lumi in a worktree), then the places the
    /// user starts in: Lumi in its folder, Just chat and Coding Lumi.
    /// </summary>
    internal static RemoteNewChatExperience Experience() => new()
    {
        Greeting = "Good morning, Adir",
        Brief = "You're all caught up.",
        SetupsTitle = "Start with",
        DefaultModel = "Model A",
        StarterIcons = ["layers", "bug", "lightbulb"],
        Setups =
        [
            Starter("UI dev", LumiId, worktree: true, model: "model-b", effort: "high", kept: true, icon: "layers"),
            Starter("Fixes", LumiId, worktree: true, kept: true, icon: "bug"),
            Starter("Lumi", LumiId, note: "28 of your chats in the last 30 days."),
            Starter("Just chat", note: "21 of your chats in the last 30 days."),
            Starter("Coding Lumi", agentId: AgentId)
        ],
        Starters = [new() { Glyph = "🌙", Label = "Recap my day", Prompt = "Recap my day." }],
        Tabs =
        [
            new()
            {
                Id = "plan", Title = "Plan",
                Starters =
                [
                    new() { Id = "plan.trip", Icon = "globe", Label = "Plan a trip to [where?]", Prompt = "Plan a trip to [where?] in [when?]" },
                    new() { Id = "plan.meals", Icon = "checklist", Label = "Plan meals for the week", Prompt = "Plan meals for the week" }
                ]
            }
        ],
        Scopes =
        [
            new()
            {
                Subtitle = "What would you like to start?",
                ForYou = [new() { Id = "write.email", Icon = "mail", Label = "Write an email to [who]", Prompt = "Write an email to [who] about [what]" }]
            },
            new()
            {
                ProjectId = LumiId, Subtitle = "Lumi 1.0.2 is ready to publish.",
                ForYou = [new() { Icon = "bookmark", Label = "Publish Lumi 1.0.2", Prompt = "Publish Lumi 1.0.2", Reason = "From Lumi's brief" }]
            }
        ],
        Places =
        [
            new() { Id = TravelId, IsProject = true, Title = "Travel", Hint = "2h ago", Setup = Starter("Travel", TravelId) },
            new() { Id = AgentId, Title = "Coding Lumi", Hint = "40m ago", Setup = Starter("Coding Lumi", agentId: AgentId) }
        ],
        Labels = new()
        {
            ForYou = "For you", NewStarter = "New starter", DraftSetup = "This chat's setup", Keep = "Keep",
            DefaultModel = "Default model", MoreStarters = "{0} more starters", MoreStarter = "1 more starter",
            Customize = "Customize this starter"
        },
        NeedsYouCount = 1,
        NeedsYou = "1 chat needs you",
        NeedsYouChats =
        [
            new() { ChatId = WaitingChatId, Title = "Crash triage", IsWaiting = true, StateLabel = "Asked you something", Meta = "Fluent Search", TimeLabel = "2h ago" }
        ]
    };

    internal static RemoteLibrary Library() => new()
    {
        Projects =
        [
            new RemoteProject { Id = LumiId, Name = "Lumi", IsCodingProject = true },
            new RemoteProject { Id = TravelId, Name = "Travel" }
        ],
        Lumis = [new RemoteLumi { Id = AgentId, Name = "Coding Lumi", IconGlyph = "⚡" }]
    };

    internal static RemoteSettings Catalogs() => new()
    {
        PreferredModel = "model-a",
        AvailableModels = ["model-a", "model-b"],
        ModelDisplayNames = ["model-a=Model A", "model-b=Model B"],
        ModelReasoningEfforts = ["model-a=Low,High", "model-b=Low,High"]
    };

    private static MobileShellViewModel NewShell() => new(
        store: new MobileSettingsStore(Path.Combine(Path.GetTempPath(), "lumi-mobile-tests", Guid.NewGuid().ToString("N"))),
        post: action => action());

    private static MobileShellViewModel ShellWithLaunchpad()
    {
        var shell = NewShell();
        shell.Chat.ApplyLibraryCatalogs(Library());
        shell.Chat.ApplyCatalogs(Catalogs());
        shell.ApplyNewChatExperience(Experience());
        return shell;
    }

    [Fact]
    public async Task ForYouFollowsTheProjectTheChatStartsIn_AndEachTabHoldsItsStarters()
    {
        await using var shell = NewShell();
        shell.ApplyNewChatExperience(Experience());

        Assert.True(shell.HasLaunchpad);
        Assert.True(shell.HasLaunchpadTabs);
        Assert.Equal(["for-you", "plan"], shell.LaunchpadTabs.Select(tab => tab.Id));
        Assert.Equal("For you", shell.LaunchpadTabs[0].Title);
        Assert.Same(shell.LaunchpadTabs[0], shell.SelectedLaunchpadTab);
        Assert.Equal("Write an email to [who] about [what]", Assert.Single(shell.LaunchpadStarters).Prompt);
        Assert.Equal("What would you like to start?", shell.NewChatBrief);
        // Every tab's words as their capsules show them, for the room the starters keep.
        Assert.Equal(
            new[] { new[] { "Write an email to who" }, new[] { "Plan a trip to where?", "Plan meals for the week" } },
            shell.LaunchpadStarterTexts.Select(set => set.ToArray()));

        // In Lumi: Lumi's For you and the opening of its brief, its plans wearing the accent.
        shell.Chat.ProjectValue = LumiId.ToString();
        var plan = Assert.Single(shell.LaunchpadStarters);
        Assert.True(plan.IsFromBrief);
        Assert.Equal("bookmark", plan.IconKey);
        Assert.Equal("Lumi 1.0.2 is ready to publish.", shell.NewChatBrief);
        Assert.Equal(new[] { "Publish Lumi 1.0.2" }, shell.LaunchpadStarterTexts[0]);

        // A project the PC sent nothing for starts from no project's.
        shell.Chat.ProjectValue = Guid.NewGuid().ToString();
        Assert.Equal("write.email", Assert.Single(shell.LaunchpadStarters).Id);

        shell.SelectedLaunchpadTab = shell.LaunchpadTabs[1];
        Assert.Equal(["plan.trip", "plan.meals"], shell.LaunchpadStarters.Select(starter => starter.Id));
        var trip = shell.LaunchpadStarters[0];
        Assert.Equal(["[where?]", "[when?]"], trip.Blanks);
        Assert.Equal("Plan a trip to where?", trip.AccessibleName);
        Assert.Equal([("Plan a trip to", false), ("where?", true)], trip.Parts.Select(part => (part.Text, part.IsBlank)));
        Assert.False(trip.IsFromBrief);

        // The PC sends the new chat again whenever its chats change: the open tab, and the rows it shows, stay.
        var starters = shell.LaunchpadStarters;
        var setups = shell.StartSetups;
        shell.ApplyNewChatExperience(Experience());
        Assert.Equal("plan", shell.SelectedLaunchpadTab!.Id);
        Assert.Same(starters, shell.LaunchpadStarters);
        Assert.Same(setups, shell.StartSetups);
    }

    /// <summary>
    /// The lit starter is the most specific one that fits the draft: one on the default model fits whatever model the
    /// draft has, a chosen model counts more than a chosen effort, and a starter elsewhere never fits.
    /// </summary>
    [Fact]
    public async Task TheLitStarterIsTheMostSpecificThatFitsTheDraft()
    {
        await using var shell = ShellWithLaunchpad();
        var (uiDev, fixes, lumi, justChat) = (shell.StartSetups[0], shell.StartSetups[1], shell.StartSetups[2], shell.StartSetups[3]);

        // A new chat outside projects is Just chat, which starts with the default model whatever the draft's is.
        Assert.Same(justChat, shell.ActiveStartSetup);
        Assert.True(justChat.IsSelected);
        Assert.True(justChat.ShowsMore);
        shell.Chat.Model = "model-b";
        Assert.Same(justChat, shell.ActiveStartSetup);

        // UI dev chose Model B and High: applied, it beats Fixes, the same place on the default model.
        shell.UseStartSetupCommand.Execute(uiDev);
        Assert.Equal(LumiId.ToString(), shell.Chat.ProjectValue);
        Assert.True(shell.Chat.UseWorktree);
        Assert.Equal("model-b", shell.Chat.Model);
        Assert.Equal("High", shell.Chat.Quality);
        Assert.Same(uiDev, shell.ActiveStartSetup);
        Assert.True(uiDev.ShowsMore);
        Assert.False(fixes.IsSelected);
        Assert.False(justChat.ShowsMore);

        // Another effort than UI dev's: Fixes, which leaves the model and effort to the chat, fits instead.
        shell.Chat.Quality = "Low";
        Assert.Same(fixes, shell.ActiveStartSetup);
        Assert.False(uiDev.IsSelected);

        // In Lumi's folder rather than a worktree: Lumi, learned from where the user starts.
        shell.Chat.UseWorktree = false;
        Assert.Same(lumi, shell.ActiveStartSetup);
        Assert.False(shell.HasUnsavedDraft);
        Assert.Single(shell.StartSetups, setup => setup.IsSelected);
    }

    /// <summary>
    /// A draft no starter fits (a project picked in the composer) shows where it starts, dashed, with a pin, and the new
    /// starter sheet starts from it: the starter's sheet for one not kept yet, its place among every place it can choose.
    /// A Copilot agent's draft is no place a starter holds.
    /// </summary>
    [Fact]
    public async Task ADraftNoStarterFitsShowsWhereItStartsDashedWithAPin()
    {
        await using var shell = ShellWithLaunchpad();
        Assert.False(shell.HasUnsavedDraft);

        // A project picked in the composer, named as soon as its name follows its id.
        shell.Chat.OpenContextPickerCommand.Execute("Project");
        shell.Chat.PickContextOptionCommand.Execute(shell.Chat.ContextOptions.Single(option => option.Name == "Travel"));
        Assert.Null(shell.ActiveStartSetup);
        Assert.DoesNotContain(shell.StartSetups, setup => setup.IsSelected);
        Assert.True(shell.HasUnsavedDraft);
        Assert.True(shell.CanKeepDraft);
        Assert.Equal("Travel", shell.DraftSetup.Title);
        Assert.True(shell.DraftSetup.Badge.HasProjectMark);
        Assert.Equal("T", shell.DraftSetup.Badge.Glyph);
        Assert.Equal("This chat's setup: Travel", shell.DraftSetupAccessibleName);

        // Filled from the draft, on the default model, with no name yet.
        shell.OpenNewStarterSheetCommand.Execute(null);
        Assert.True(shell.IsSetupSheetOpen);
        Assert.True(shell.IsSetupSheetNew);
        Assert.True(shell.ShowsSetupSheetForm);
        Assert.False(shell.ShowsSetupSheetLearned);
        Assert.Equal("New starter", shell.SetupSheetTitle);
        Assert.Equal("", shell.SetupSheetName);
        Assert.Equal("Travel", shell.SetupSheetStarter.PlaceTitle);
        Assert.True(shell.SetupSheetStarter.IsDefaultModel);
        Assert.True(shell.SetupSheetModels[0].IsCurrent);

        // Its places: Just chat, the projects and the agents, its own checked. Back returns to its form, then closes it.
        shell.ChooseStarterPlaceCommand.Execute(null);
        Assert.True(shell.IsChoosingStarterPlace);
        Assert.False(shell.ShowsSetupSheetForm);
        Assert.Equal("Just chat", shell.JustChatPlace?.Title);
        Assert.False(shell.JustChatPlace!.IsCurrent);
        Assert.Equal(["Travel"], shell.PlaceProjects.Select(place => place.Title));
        Assert.True(shell.PlaceProjects.Single().IsCurrent);
        Assert.Equal(["Coding Lumi"], shell.PlaceAgents.Select(place => place.Title));
        shell.PlaceQuery = "COD";
        Assert.Null(shell.JustChatPlace);
        Assert.Empty(shell.PlaceProjects);
        Assert.Single(shell.PlaceAgents);
        shell.PlaceQuery = "nothing like it";
        Assert.True(shell.HasNoPlaceMatch);
        shell.GoBackCommand.Execute(null);
        Assert.True(shell.IsSetupSheetOpen);
        Assert.False(shell.IsChoosingStarterPlace);
        shell.GoBackCommand.Execute(null);
        Assert.False(shell.IsSetupSheetOpen);
        Assert.False(shell.IsSetupSheetNew);

        // A starter picked on the row takes the dashed chip's place.
        shell.UseStartSetupCommand.Execute(shell.StartSetups[3]);
        Assert.False(shell.HasUnsavedDraft);
        Assert.False(shell.CanKeepDraft);

        // A Copilot agent (one without a Lumi agent's id) lights nothing and offers nothing to keep.
        shell.Chat.AgentName = "Explore";
        Assert.Null(shell.Chat.AgentValue);
        Assert.Null(shell.ActiveStartSetup);
        Assert.False(shell.HasUnsavedDraft);
        Assert.False(shell.CanKeepDraft);
    }

    /// <summary>
    /// The row shows the starters that fit the screen, in order, and the lit one always, in the last place shown; the rest
    /// gather into the stack (its count and first three badges). With room for all, a round + takes the stack's place.
    /// </summary>
    [Fact]
    public async Task TheRowShowsWhatFits_TheLitStarterInALearnedOnesPlace_AndTheRestInTheStack()
    {
        await using var shell = ShellWithLaunchpad();
        var setups = shell.StartSetups;
        Assert.Same(setups[3], shell.ActiveStartSetup);
        Assert.All(setups, setup => Assert.True(setup.IsInRow));
        Assert.True(shell.ShowsNewStarterButton);

        // UI dev, Fixes and Lumi fill what the stack leaves. Just chat is lit, but even in Lumi's place it has no room beside
        // the kept starters, which never leave the row for it: it stays in the stack, which wears its light.
        shell.FitStarterRow([100, 100, 100, 120, 100], available: 400, draftWidth: 70, newStarterWidth: 56, stackWidth: 90);
        Assert.Equal([true, true, true, false, false], setups.Select(setup => setup.IsInRow));
        Assert.True(shell.IsStarterStackSelected);
        Assert.Equal(2, shell.StarterStackCount);
        Assert.Equal("+2", shell.StarterStackLabel);
        Assert.Equal("2 more starters", shell.StarterStackAccessibleName);
        Assert.Equal([setups[3], setups[4]], shell.StarterDialItems);
        Assert.Equal([setups[3].Badge, setups[4].Badge], shell.StarterStackPreview);
        Assert.False(shell.ShowsNewStarterButton);

        // With a little more room it takes the place of Lumi, the last learned starter shown.
        shell.FitStarterRow([100, 100, 100, 120, 100], available: 410, draftWidth: 70, newStarterWidth: 56, stackWidth: 90);
        Assert.Equal([true, true, false, true, false], setups.Select(setup => setup.IsInRow));
        Assert.False(shell.IsStarterStackSelected);
        Assert.Equal([setups[2], setups[4]], shell.StarterDialItems);

        // Room for all, the new starter + included: no stack.
        shell.FitStarterRow([100, 100, 100, 120, 100], available: 600, draftWidth: 70, newStarterWidth: 56, stackWidth: 90);
        Assert.All(setups, setup => Assert.True(setup.IsInRow));
        Assert.Equal(0, shell.StarterStackCount);
        Assert.Empty(shell.StarterDialItems);
        Assert.False(shell.IsStarterStackSelected);
        Assert.True(shell.ShowsNewStarterButton);

        // The draft's dashed chip takes room of its own while no starter fits the draft: the same width no longer holds all.
        shell.Chat.ProjectValue = TravelId.ToString();
        shell.Chat.ProjectName = "Travel";
        Assert.True(shell.HasUnsavedDraft);
        shell.FitStarterRow([100, 100, 100, 120, 100], available: 600, draftWidth: 70, newStarterWidth: 56, stackWidth: 90);
        Assert.Equal([true, true, true, true, false], setups.Select(setup => setup.IsInRow));
        Assert.Equal(1, shell.StarterStackCount);

        // A refresh keeps the place each starter had until the row is fitted again.
        var refreshed = Experience();
        refreshed.Setups[0].Title = "UI work";
        shell.ApplyNewChatExperience(refreshed);
        Assert.NotSame(setups, shell.StartSetups);
        Assert.Equal([true, true, true, true, false], shell.StartSetups.Select(setup => setup.IsInRow));
        Assert.Equal(1, shell.StarterStackCount);
        Assert.Same(shell.StartSetups[4], Assert.Single(shell.StarterDialItems));
    }

    [Fact]
    public void TheRowFitsAsTheDesktopsPlannerDoes()
    {
        bool[] learned = [false, false, false];
        // Everything fits beside the new starter +.
        Assert.Equal([true, true], LaunchpadRowFit.Fit([100, 100], [false, false], available: 260, fixedWidth: 0, endWidth: 56, stackWidth: 90, keepIndex: -1));
        // Without room for the +, the stack takes its place and the first ones that fit beside it stay.
        Assert.Equal([true, false, false], LaunchpadRowFit.Fit([100, 100, 100], learned, available: 250, fixedWidth: 0, endWidth: 56, stackWidth: 90, keepIndex: -1));
        // The lit one comes in from the stack in the place of a learned one shown...
        Assert.Equal([false, false, true], LaunchpadRowFit.Fit([100, 100, 100], learned, available: 250, fixedWidth: 0, endWidth: 56, stackWidth: 90, keepIndex: 2));
        // ...but never of a kept one: it stays in the stack, which wears its light.
        Assert.Equal([true, false, false], LaunchpadRowFit.Fit([100, 100, 100], [true, false, false], available: 250, fixedWidth: 0, endWidth: 56, stackWidth: 90, keepIndex: 2));
        // What the row always shows (the dashed chip) leaves less room.
        Assert.Equal([true, false], LaunchpadRowFit.Fit([100, 100], [false, false], available: 320, fixedWidth: 70, endWidth: 56, stackWidth: 90, keepIndex: -1));
    }

    /// <summary>
    /// The stack's speed dial rises with the starters the row has no room for and folds back: a starter picked from it
    /// starts the draft there, Back folds it, and so do the welcome giving way and a row with room for all.
    /// </summary>
    [Fact]
    public async Task TheStacksSpeedDialRisesAndFolds()
    {
        await using var shell = ShellWithLaunchpad();
        var setups = shell.StartSetups;

        // Without a stack there is nothing to rise.
        shell.ToggleStarterDialCommand.Execute(null);
        Assert.False(shell.IsStarterDialOpen);

        shell.FitStarterRow([100, 100, 100, 120, 100], available: 400, draftWidth: 0, newStarterWidth: 56, stackWidth: 90);
        shell.ToggleStarterDialCommand.Execute(null);
        Assert.True(shell.IsStarterDialOpen);
        Assert.True(shell.CanGoBack);
        Assert.False(shell.CanDragDrawer);
        shell.GoBackCommand.Execute(null);
        Assert.False(shell.IsStarterDialOpen);

        // The stack folds what it raised.
        shell.ToggleStarterDialCommand.Execute(null);
        shell.ToggleStarterDialCommand.Execute(null);
        Assert.False(shell.IsStarterDialOpen);

        // A starter from the dial starts the draft there and folds it; lit, it takes the place of the last learned one shown.
        shell.ToggleStarterDialCommand.Execute(null);
        var coding = Assert.Single(shell.StarterDialItems, setup => setup.Title == "Coding Lumi");
        shell.UseStartSetupCommand.Execute(coding);
        Assert.False(shell.IsStarterDialOpen);
        Assert.Same(coding, shell.ActiveStartSetup);
        shell.FitStarterRow([100, 100, 100, 120, 100], available: 400, draftWidth: 0, newStarterWidth: 56, stackWidth: 90);
        Assert.True(coding.IsInRow);
        Assert.False(setups[2].IsInRow);
        Assert.DoesNotContain(coding, shell.StarterDialItems);
        Assert.False(shell.IsStarterStackSelected);

        // Opening a starter's sheet from the dial folds it.
        shell.ToggleStarterDialCommand.Execute(null);
        shell.OpenSetupSheetCommand.Execute(shell.StarterDialItems[0]);
        Assert.False(shell.IsStarterDialOpen);
        Assert.True(shell.IsSetupSheetOpen);
        shell.GoBackCommand.Execute(null);

        // The welcome giving way to the keyboard folds it.
        shell.ToggleStarterDialCommand.Execute(null);
        shell.IsKeyboardOpen = true;
        Assert.False(shell.IsStarterDialOpen);
        shell.IsKeyboardOpen = false;

        // So does a row with room for every starter.
        shell.ToggleStarterDialCommand.Execute(null);
        shell.FitStarterRow([100, 100, 100, 120, 100], available: 800, draftWidth: 0, newStarterWidth: 56, stackWidth: 90);
        Assert.False(shell.IsStarterDialOpen);
        Assert.All(setups, setup => Assert.True(setup.IsInRow));
    }

    /// <summary>
    /// The draft's pin keeps the draft as its pickers name it (its model and the effort as the picker shows it). The new
    /// starter sheet sends nothing until Add, then names the starter whole as the sheet has it: its place and checkout,
    /// the model chosen for it with its effort (kept even should it be the default model now), its name and its icon. On
    /// the default model it names no model, so it follows the default.
    /// </summary>
    [Fact]
    public async Task TheDraftAndTheNewStarterAskThePcOverTheWire()
    {
        await using var host = Desktop();
        await using var shell = await PairAsync(host);

        // A starter fills the composer, hands the view its blanks, and tells the PC which one was picked.
        IReadOnlyList<string>? blanks = null;
        shell.StarterApplied += applied => blanks = applied;
        shell.UseLaunchpadStarterCommand.Execute(shell.LaunchpadStarters.Single());
        Assert.Equal("Write an email to [who] about [what]", shell.Chat.PromptText);
        Assert.Equal(["[who]", "[what]"], blanks);
        var used = await ReceivedAsync(host, RemoteProtocol.Actions.UseStarter);
        Assert.Equal("write.email", used.Get("id"));
        Assert.Null(used.Get("projectId"));

        // The dashed chip's pin: the draft as the composer has it.
        shell.Chat.OpenContextPickerCommand.Execute("Project");
        shell.Chat.PickContextOptionCommand.Execute(shell.Chat.ContextOptions.Single(option => option.Name == "Travel"));
        Assert.True(shell.HasUnsavedDraft);
        await shell.KeepDraftCommand.ExecuteAsync(null);
        var keep = await ReceivedAsync(host, RemoteProtocol.Actions.KeepSetup);
        Assert.Equal(TravelId, keep.GetGuid("projectId"));
        Assert.Null(keep.Get("agentId"));
        Assert.False(keep.GetBool("worktree"));
        Assert.Equal("model-a", keep.Get("model"));
        // Its quality as the picker shows it, empty for a model without efforts: either way, it names the draft.
        Assert.Equal(shell.Chat.Quality ?? "", keep.Get("quality"));
        Assert.Null(keep.Get("effort"));
        Assert.Null(keep.Get("name"));
        Assert.True(keep.GetBool("kept"));

        // The new starter sheet: Coding Lumi as its place, Model B at High chosen for it, an icon and a name.
        shell.OpenNewStarterSheetCommand.Execute(null);
        shell.ChooseStarterPlaceCommand.Execute(null);
        shell.PickPlaceCommand.Execute(shell.PlaceAgents.Single());
        Assert.False(shell.IsChoosingStarterPlace);
        Assert.Equal("Coding Lumi", shell.SetupSheetStarter.PlaceTitle);
        Assert.False(shell.SetupSheetStarter.CanUseWorktree);
        shell.PickStarterModelCommand.Execute(shell.SetupSheetModels.Single(model => model.ModelId == "model-b"));
        Assert.Equal(["Low", "High"], shell.SetupSheetQualities.Select(quality => quality.Title));
        shell.PickStarterQualityCommand.Execute(shell.SetupSheetQualities.Single(quality => quality.Title == "High"));
        Assert.True(shell.SetupSheetQualities.Single(quality => quality.Title == "High").IsCurrent);
        shell.PickStarterIconCommand.Execute(shell.SetupSheetIcons.Single(icon => icon.Key == "bug"));
        Assert.Equal("bug", shell.SetupSheetStarter.Badge.IconKey);
        shell.SetupSheetName = "  Agent work ";
        lock (host.ReceivedCommands)
            Assert.Single(host.ReceivedCommands, command => command.Action == RemoteProtocol.Actions.KeepSetup);
        await shell.AddStarterCommand.ExecuteAsync(null);
        Assert.False(shell.IsSetupSheetOpen);
        var added = await ReceivedAsync(host, RemoteProtocol.Actions.KeepSetup, skip: 1);
        Assert.Null(added.Get("projectId"));
        Assert.Equal(AgentId, added.GetGuid("agentId"));
        Assert.False(added.GetBool("worktree"));
        Assert.Equal("model-b", added.Get("model"));
        Assert.Equal("High", added.Get("quality"));
        Assert.True(added.GetBool("ownModel"));
        Assert.Equal("Agent work", added.Get("name"));
        Assert.Equal("bug", added.Get("icon"));
        Assert.True(added.GetBool("kept"));
        // Making a starter isn't starting there: the draft stays as it was.
        Assert.Equal(TravelId.ToString(), shell.Chat.ProjectValue);
        Assert.Equal("model-a", shell.Chat.Model);

        // On the default model, as the draft is, it names no model.
        shell.OpenNewStarterSheetCommand.Execute(null);
        await shell.AddStarterCommand.ExecuteAsync(null);
        var plain = await ReceivedAsync(host, RemoteProtocol.Actions.KeepSetup, skip: 2);
        Assert.Equal(TravelId, plain.GetGuid("projectId"));
        Assert.Null(plain.Get("model"));
        Assert.Null(plain.Get("ownModel"));
        Assert.Equal("", plain.Get("name"));
        Assert.Equal("", plain.Get("icon"));
    }

    /// <summary>
    /// A learned starter's sheet keeps it, renames it (keeping it, then focusing its name once the PC sends it back
    /// kept) or stops suggesting it, naming it without a model; Just chat can't be dismissed.
    /// </summary>
    [Fact]
    public async Task ALearnedStartersSheetKeepsRenamesOrHidesItOverTheWire()
    {
        await using var host = Desktop();
        await using var shell = await PairAsync(host);
        var focused = 0;
        shell.StarterNameFocusRequested += () => focused++;

        var lumi = shell.StartSetups.Single(setup => setup.Title == "Lumi");
        shell.OpenSetupSheetCommand.Execute(lumi);
        Assert.True(shell.IsSetupSheetOpen);
        Assert.Same(lumi, shell.SetupSheetStarter);
        Assert.False(lumi.IsKept);
        Assert.True(lumi.CanHide);
        Assert.Equal("28 of your chats in the last 30 days.", lumi.Note);
        Assert.Empty(shell.SetupSheetModels);

        shell.RenameStarterCommand.Execute(null);
        Assert.True(shell.IsSetupSheetBusy);
        var keep = await ReceivedAsync(host, RemoteProtocol.Actions.KeepSetup);
        Assert.Equal(LumiId, keep.GetGuid("projectId"));
        Assert.False(keep.GetBool("worktree"));
        Assert.Null(keep.Get("model"));
        Assert.Null(keep.Get("effort"));
        Assert.True(keep.GetBool("kept"));

        // The PC sends the row with Lumi kept: the sheet shows it kept, with its name ready to type.
        var kept = Experience();
        kept.Setups[2] = Starter("Lumi", LumiId, kept: true);
        PcSends(host, shell, kept);
        Assert.False(shell.IsSetupSheetBusy);
        Assert.True(shell.SetupSheetStarter.IsKept);
        Assert.Equal(1, focused);
        Assert.Equal("Lumi", shell.SetupSheetName);
        Assert.Equal(["Default model", "Model A", "Model B"], shell.SetupSheetModels.Select(model => model.Title));
        Assert.True(shell.SetupSheetModels[0].IsCurrent);
        Assert.Equal("Model A", shell.SetupSheetModels[0].Hint);

        // The name typed is kept as the sheet closes.
        shell.SetupSheetName = "Research";
        shell.IsSetupSheetOpen = false;
        var renamed = await ReceivedAsync(host, RemoteProtocol.Actions.RetuneSetup);
        Assert.Equal("Research", renamed.Get("toName"));
        Assert.Equal(LumiId, renamed.GetGuid("projectId"));
        Assert.Null(renamed.Get("model"));

        // Don't suggest: a learned place goes, named without a model; the sheet closes.
        shell.OpenSetupSheetCommand.Execute(shell.StartSetups.Single(setup => setup.Title == "Coding Lumi"));
        await shell.HideStarterCommand.ExecuteAsync(null);
        Assert.False(shell.IsSetupSheetOpen);
        var hide = await ReceivedAsync(host, RemoteProtocol.Actions.HideSetup);
        Assert.Equal(AgentId, hide.GetGuid("agentId"));
        Assert.Null(hide.GetGuid("projectId"));
        Assert.Null(hide.Get("model"));
        kept.Setups.RemoveAt(4);
        PcSends(host, shell, kept);

        // Just chat is always there; Keep shows it kept, without taking the keyboard.
        shell.OpenSetupSheetCommand.Execute(shell.StartSetups.Single(setup => setup.Title == "Just chat"));
        Assert.False(shell.SetupSheetStarter.CanHide);
        await shell.HideStarterCommand.ExecuteAsync(null);
        Assert.True(shell.IsSetupSheetOpen);
        shell.KeepStarterCommand.Execute(null);
        var keptJustChat = await ReceivedAsync(host, RemoteProtocol.Actions.KeepSetup, skip: 1);
        Assert.Null(keptJustChat.Get("projectId"));
        Assert.Null(keptJustChat.Get("agentId"));
        Assert.Null(keptJustChat.Get("model"));
        Assert.True(keptJustChat.GetBool("kept"));
        kept.Setups[3] = Starter("Just chat", kept: true);
        PcSends(host, shell, kept);
        Assert.True(shell.SetupSheetStarter.IsKept);
        Assert.Equal(1, focused);
        lock (host.ReceivedCommands)
            Assert.Single(host.ReceivedCommands, command => command.Action == RemoteProtocol.Actions.HideSetup);
    }

    /// <summary>
    /// A kept starter's sheet changes its name, its icon, its model and its checkout, naming it whole each time: with its
    /// model and effort while it has a model of its own, without them on the default model. A change to what it starts
    /// with waits for the PC's row, and the sheet follows the starter it became.
    /// </summary>
    [Fact]
    public async Task AKeptStartersSheetChangesItOverTheWire()
    {
        await using var host = Desktop();
        await using var shell = await PairAsync(host);

        var uiDev = shell.StartSetups[0];
        shell.OpenSetupSheetCommand.Execute(uiDev);
        Assert.True(uiDev.IsKept);
        Assert.Equal("UI dev", shell.SetupSheetName);
        Assert.Equal([null, "layers", "bug", "lightbulb"], shell.SetupSheetIcons.Select(icon => icon.Key));
        Assert.True(shell.SetupSheetIcons[0].Badge.HasProjectMark);
        Assert.Equal("Lumi", shell.SetupSheetIcons[0].AccessibleName);
        Assert.True(shell.SetupSheetIcons[1].IsCurrent);
        Assert.Equal(["Default model", "Model A", "Model B"], shell.SetupSheetModels.Select(model => model.Title));
        Assert.True(shell.SetupSheetModels[2].IsCurrent);
        Assert.True(shell.SetupSheetWorktree);

        // Its name, on Done; Done again sends nothing new. A kept starter is named by its id too.
        shell.SetupSheetName = "UI work";
        shell.CommitStarterNameCommand.Execute(null);
        shell.CommitStarterNameCommand.Execute(null);
        var name = await ReceivedAsync(host, RemoteProtocol.Actions.RetuneSetup);
        Assert.Equal("UI work", name.Get("toName"));
        Assert.Equal("kept:UI dev", name.Get("id"));
        Assert.Equal(LumiId, name.GetGuid("projectId"));
        Assert.True(name.GetBool("worktree"));
        Assert.Equal("model-b", name.Get("model"));
        Assert.Equal("high", name.Get("effort"));

        // Its icon, then its place's badge back.
        shell.PickStarterIconCommand.Execute(shell.SetupSheetIcons[2]);
        Assert.True(shell.SetupSheetIcons[2].IsCurrent);
        Assert.False(shell.SetupSheetIcons[1].IsCurrent);
        var icon = await ReceivedAsync(host, RemoteProtocol.Actions.RetuneSetup, skip: 1);
        Assert.Equal("bug", icon.Get("toIcon"));
        Assert.Null(icon.Get("toName"));
        shell.PickStarterIconCommand.Execute(shell.SetupSheetIcons[0]);
        var badge = await ReceivedAsync(host, RemoteProtocol.Actions.RetuneSetup, skip: 2);
        Assert.Equal("", badge.Get("toIcon"));

        // The default model changes what it starts with: the sheet waits for the PC's row, taking nothing else meanwhile.
        shell.PickStarterModelCommand.Execute(shell.SetupSheetModels[0]);
        Assert.True(shell.IsSetupSheetBusy);
        Assert.True(shell.SetupSheetModels[0].IsCurrent);
        shell.PickStarterModelCommand.Execute(shell.SetupSheetModels[1]);
        var toDefault = await ReceivedAsync(host, RemoteProtocol.Actions.RetuneSetup, skip: 3);
        Assert.True(toDefault.GetBool("toDefaultModel"));
        Assert.Equal("model-b", toDefault.Get("model"));
        // Fixes, kept the same way now under a name of its own, stays; the sheet follows UI dev by its id.
        var onDefault = Experience();
        onDefault.Setups[0] = Starter("UI work", LumiId, worktree: true, kept: true, id: "kept:UI dev");
        PcSends(host, shell, onDefault);
        Assert.False(shell.IsSetupSheetBusy);
        Assert.True(shell.SetupSheetStarter.IsDefaultModel);
        Assert.Equal("UI work", shell.SetupSheetStarter.Title);
        Assert.Equal("UI work", shell.SetupSheetName);
        Assert.True(shell.SetupSheetModels[0].IsCurrent);

        // On the default model it is named without one.
        shell.PickStarterModelCommand.Execute(shell.SetupSheetModels[2]);
        var toModel = await ReceivedAsync(host, RemoteProtocol.Actions.RetuneSetup, skip: 4);
        Assert.Equal("model-b", toModel.Get("toModel"));
        Assert.Null(toModel.Get("model"));
        Assert.Null(toModel.Get("effort"));
        var onModel = Experience();
        onModel.Setups[0] = Starter("UI work", LumiId, worktree: true, model: "model-b", effort: "high", kept: true, id: "kept:UI dev");
        PcSends(host, shell, onModel);
        Assert.False(shell.SetupSheetStarter.IsDefaultModel);
        Assert.True(shell.SetupSheetModels[2].IsCurrent);

        // Its checkout.
        shell.SetupSheetWorktree = false;
        var checkout = await ReceivedAsync(host, RemoteProtocol.Actions.RetuneSetup, skip: 5);
        Assert.False(checkout.GetBool("toWorktree"));
        Assert.True(checkout.GetBool("worktree"));
        Assert.Equal("model-b", checkout.Get("model"));
        Assert.Equal("high", checkout.Get("effort"));
        var local = Experience();
        local.Setups[0] = Starter("UI work", LumiId, model: "model-b", effort: "high", kept: true, id: "kept:UI dev");
        PcSends(host, shell, local);
        Assert.False(shell.IsSetupSheetBusy);
        Assert.False(shell.SetupSheetStarter.IsWorktree);
        Assert.False(shell.SetupSheetWorktree);

        // A refused change shows the starter as it still is, and says why in the chat.
        host.CommandResultFactory = _ => new RemoteCommandResult { Error = "That starter isn't on this computer." };
        shell.SetupSheetWorktree = true;
        await WaitUntilAsync(() => !shell.IsSetupSheetBusy && !shell.SetupSheetWorktree);
        Assert.Equal("That starter isn't on this computer.", shell.Chat.ErrorText);
        host.CommandResultFactory = null;

        // Remove lets it go, without keeping a name on the way out.
        shell.SetupSheetName = "Never kept";
        await shell.RemoveStarterCommand.ExecuteAsync(null);
        Assert.False(shell.IsSetupSheetOpen);
        var remove = await ReceivedAsync(host, RemoteProtocol.Actions.KeepSetup);
        Assert.False(remove.GetBool("kept"));
        Assert.Equal("kept:UI dev", remove.Get("id"));
        Assert.False(remove.GetBool("worktree"));
        Assert.Equal("model-b", remove.Get("model"));
        Assert.Equal("high", remove.Get("effort"));
        lock (host.ReceivedCommands)
            Assert.Equal(7, host.ReceivedCommands.Count(command => command.Action == RemoteProtocol.Actions.RetuneSetup));
    }

    [Fact]
    public async Task ARefusalIsSaidInTheChatThatAsked()
    {
        await using var host = Desktop();
        await using var shell = await PairAsync(host);

        host.CommandResultFactory = _ => new RemoteCommandResult { Error = "That setup isn't available on this computer." };
        shell.OpenSetupSheetCommand.Execute(shell.StartSetups.Single(setup => setup.Title == "Coding Lumi"));
        await shell.HideStarterCommand.ExecuteAsync(null);
        Assert.Equal("That setup isn't available on this computer.", shell.Chat.ErrorText);

        // ...in that chat only: a refusal that arrives after the user opened another chat isn't said there.
        const string lateRefusal = "The PC refused after the user moved on.";
        using var answer = new ManualResetEventSlim();
        host.CommandResultFactory = command =>
        {
            if (command?.Action != RemoteProtocol.Actions.HideSetup)
                return new RemoteCommandResult { Ok = true, Message = "ok" };
            answer.Wait(TimeSpan.FromSeconds(10));
            return new RemoteCommandResult { Error = lateRefusal };
        };
        host.Transcript = new RemoteTranscript { ChatId = WaitingChatId, Title = "Crash triage" };
        shell.OpenSetupSheetCommand.Execute(shell.StartSetups.Single(setup => setup.Title == "Coding Lumi"));
        var refused = shell.HideStarterCommand.ExecuteAsync(null);
        await ReceivedAsync(host, RemoteProtocol.Actions.HideSetup, skip: 1);

        // What waits on the user opens.
        shell.OpenNeedsYouCommand.Execute(null);
        shell.OpenNeedsYouChatCommand.Execute(shell.NeedsYouChats.Single());
        Assert.False(shell.IsNeedsYouSheetOpen);
        Assert.Equal(WaitingChatId, shell.ChatList.SelectedChatId);
        await WaitUntilAsync(() => shell.Chat.ChatId == WaitingChatId && !shell.Chat.IsLoading);
        answer.Set();
        await refused;
        Assert.NotEqual(lateRefusal, shell.Chat.ErrorText);
    }

    [Fact]
    public async Task AStartersSheetListsModelsAsThePicker_FusionOnce_WithItsTiersAsEfforts()
    {
        await using var host = Desktop();
        await using var shell = await PairAsync(host);
        var catalogs = Catalogs();
        catalogs.AvailableModels = ["model-a", "auto", "auto:balance", "auto:hydrafusion", "auto:hydrafusion-max"];
        catalogs.ModelDisplayNames =
        [
            "model-a=Model A", "auto=Auto", "auto:balance=Copilot Fusion Standard",
            "auto:hydrafusion=Copilot Fusion High", "auto:hydrafusion-max=Copilot Fusion Max"
        ];
        string[] tiers = ["auto:balance", "auto:hydrafusion", "auto:hydrafusion-max"];
        catalogs.ModelReasoningEfforts = ["model-a=Low,High", .. tiers.Select(static tier => $"{tier}=Standard,High,Max")];
        catalogs.ModelDefaults =
        [
            new RemoteModelDefaults { Model = "auto:balance", Quality = "Standard" },
            new RemoteModelDefaults { Model = "auto:hydrafusion", Quality = "High" },
            new RemoteModelDefaults { Model = "auto:hydrafusion-max", Quality = "Max" }
        ];
        // The picker lists Auto and the tiers as one row, with the tier as its effort, as the PC's picker does.
        catalogs.ModelOptions =
        [
            new RemoteModelOption { Model = "model-a", DisplayName = "Model A" },
            new RemoteModelOption { Model = "auto:balance", DisplayName = "Copilot Fusion", SelectionAliases = [.. tiers, "auto"] }
        ];
        var experience = Experience();
        experience.Setups[0] = Starter("UI dev", LumiId, worktree: true, model: "auto:hydrafusion", kept: true);
        catalogs.NewChat = experience;
        host.Snapshot.Settings = catalogs;
        shell.Chat.ApplyCatalogs(catalogs);
        PcSends(host, shell, experience);

        shell.OpenSetupSheetCommand.Execute(shell.StartSetups[0]);
        Assert.Equal(["Default model", "Model A", "Copilot Fusion"], shell.SetupSheetModels.Select(model => model.Title));
        Assert.True(shell.SetupSheetModels[2].IsCurrent);
        Assert.Equal([("Standard", false), ("High", true), ("Max", false)],
            shell.SetupSheetQualities.Select(quality => (quality.Title, quality.IsCurrent)));
        Assert.True(shell.HasSetupSheetQualities);

        // Max is Fusion's tier, asked of the Fusion row; the sheet waits for the PC's row to show it.
        shell.PickStarterQualityCommand.Execute(shell.SetupSheetQualities[2]);
        var tuned = await ReceivedAsync(host, RemoteProtocol.Actions.RetuneSetup);
        Assert.Equal(("auto:balance", "Max", "kept:UI dev"), (tuned.Get("toModel"), tuned.Get("toQuality"), tuned.Get("id")));
        Assert.True(shell.IsSetupSheetBusy);
        experience.Setups[0] = Starter("UI dev", LumiId, worktree: true, model: "auto:hydrafusion-max", kept: true);
        PcSends(host, shell, experience);
        Assert.False(shell.IsSetupSheetBusy);
        Assert.Equal("Max", shell.SetupSheetQualities.Single(quality => quality.IsCurrent).Title);

        // On the default model it brings the default's effort, so there is none to pick.
        shell.PickStarterModelCommand.Execute(shell.SetupSheetModels[0]);
        Assert.False(shell.HasSetupSheetQualities);
    }

    [Fact]
    public async Task APcFromBeforeTheLaunchpadKeepsItsThreeSetupsAndStarters()
    {
        await using var shell = NewShell();
        shell.ApplyNewChatExperience(new RemoteNewChatExperience
        {
            Greeting = "Good evening, Adir",
            Brief = "1 new reply",
            SetupsTitle = "Start with",
            Setups = [.. Enumerable.Range(1, 4).Select(index => new RemoteChatSetup { Id = $"setup-{index}", Title = $"Project {index}", ProjectId = Guid.NewGuid() })],
            Starters =
            [
                new() { Glyph = "🌙", Label = "Recap my day", Prompt = "Recap my day." },
                new() { Glyph = "🗓️", Label = "Plan tomorrow", Prompt = "Help me plan tomorrow." }
            ]
        });

        Assert.False(shell.HasLaunchpad);
        Assert.False(shell.HasLaunchpadTabs);
        Assert.True(shell.ShowsSetupRow);
        Assert.Equal(3, shell.StartSetups.Count);
        Assert.Equal("1 new reply", shell.NewChatBrief);
        Assert.Equal(["Recap my day.", "Help me plan tomorrow."], shell.LaunchpadStarters.Select(starter => starter.Prompt));
        Assert.All(shell.LaunchpadStarters, starter => Assert.True(starter.HasGlyph));

        // Nothing fits a plain draft, yet no dashed chip or + offers what the PC couldn't keep.
        Assert.Null(shell.ActiveStartSetup);
        Assert.False(shell.HasUnsavedDraft);
        Assert.False(shell.CanKeepDraft);
        Assert.False(shell.ShowsNewStarterButton);

        // Its setups apply as before, without a sheet the PC couldn't answer.
        shell.UseStartSetupCommand.Execute(shell.StartSetups[0]);
        Assert.True(shell.StartSetups[0].IsSelected);
        Assert.False(shell.StartSetups[0].ShowsMore);
        shell.OpenSetupSheetCommand.Execute(shell.StartSetups[0]);
        Assert.False(shell.IsSetupSheetOpen);
        shell.OpenNewStarterSheetCommand.Execute(null);
        Assert.False(shell.IsSetupSheetOpen);

        // One that doesn't fit the screen still gathers into the stack.
        shell.FitStarterRow([150, 150, 150], available: 340, draftWidth: 0, newStarterWidth: 56, stackWidth: 90);
        Assert.Equal([true, false, false], shell.StartSetups.Select(setup => setup.IsInRow));
        Assert.Equal(2, shell.StarterStackCount);
    }

    [Fact]
    public async Task APcWithTheEarlierLaunchpad_KeepsItsThreeSetupsAndStartersToo()
    {
        await using var shell = NewShell();
        // The launchpad before starters: tabs, but no starter icons, and other words for the row.
        var experience = Experience();
        experience.StarterIcons = [];
        shell.ApplyNewChatExperience(experience);

        Assert.False(shell.HasLaunchpad);
        Assert.False(shell.HasLaunchpadTabs);
        Assert.Equal(3, shell.StartSetups.Count);
        shell.OpenSetupSheetCommand.Execute(shell.StartSetups[0]);
        Assert.False(shell.IsSetupSheetOpen);
    }

    [Fact]
    public async Task EveryLaunchpadIconTheProtocolNamesIsDrawn()
    {
        Assert.Equal("lightbulb", RemoteProtocol.LaunchpadIcons[^1]);

        // Geometry needs the render platform, so the icons are checked inside a headless app.
        using var session = HeadlessMobileSession.Start();
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        await session.Dispatch(() =>
        {
            try
            {
                Assert.All(RemoteProtocol.LaunchpadIcons, key =>
                    Assert.True(LaunchpadIconConverter.TryResolve(key) is not null, $"'{key}' has no icon."));
                Assert.Null(LaunchpadIconConverter.TryResolve("not-an-icon"));
                Assert.NotNull(LaunchpadIconConverter.Resolve("not-an-icon"));
            }
            catch (Exception ex)
            {
                failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }
            return Task.CompletedTask;
        }, CancellationToken.None);
        failure?.Throw();
    }

    private static FakeLumiDesktop Desktop()
    {
        var host = new FakeLumiDesktop
        {
            Snapshot = new RemoteSnapshot
            {
                Settings = new RemoteSettings
                {
                    PreferredModel = "model-a",
                    AvailableModels = ["model-a", "model-b"],
                    ModelDisplayNames = ["model-a=Model A", "model-b=Model B"],
                    ModelReasoningEfforts = ["model-a=Low,High", "model-b=Low,High"],
                    NewChat = Experience()
                },
                Library = Library()
            }
        };
        host.Start();
        return host;
    }

    private static async Task<MobileShellViewModel> PairAsync(FakeLumiDesktop host)
    {
        var shell = new MobileShellViewModel(
            new LumiRemoteClient("launchpad-test", "Test phone"),
            store: new MobileSettingsStore(Path.Combine(Path.GetTempPath(), "lumi-mobile-tests", Guid.NewGuid().ToString("N"))),
            post: action => action());
        shell.Connect.ManualAddress = host.BaseUrl;
        await shell.Connect.ConnectManuallyCommand.ExecuteAsync(null);
        shell.Connect.PairingCode = "123456";
        await shell.Connect.SubmitCodeCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => shell.BootstrapSnapshotCount > 0 && shell.HasLaunchpad);
        return shell;
    }

    /// <summary>The PC sends the row as it now is, and serves it from now on.</summary>
    private static void PcSends(FakeLumiDesktop host, MobileShellViewModel shell, RemoteNewChatExperience experience)
    {
        host.Snapshot.Settings.NewChat = experience;
        shell.ApplyNewChatExperience(experience);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(condition());
    }

    /// <summary>The PC's copy of a command the phone sent, once it arrives.</summary>
    private static async Task<RemoteCommand> ReceivedAsync(FakeLumiDesktop host, string action, int skip = 0)
    {
        RemoteCommand? found = null;
        await WaitUntilAsync(() =>
        {
            lock (host.ReceivedCommands)
                found = host.ReceivedCommands.Where(command => command.Action == action).Skip(skip).FirstOrDefault();
            return found is not null;
        });
        return found!;
    }
}
