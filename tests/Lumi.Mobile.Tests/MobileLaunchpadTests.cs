using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

/// <summary>
/// The phone's new chat as the PC's launchpad projects it: For you follows the project the chat starts in, each tab
/// holds its starters, a starter fills the composer with its blanks, the setups row says which setup is the chat's
/// (Just chat, one of the user's setups, or Other with Keep), and a setup's sheet and Keep ask the PC over the real
/// wire. A PC from before the launchpad keeps its three setups and starters.
/// </summary>
public sealed class MobileLaunchpadTests
{
    private static readonly Guid LumiId = Guid.NewGuid();
    private static readonly Guid TravelId = Guid.NewGuid();
    private static readonly Guid AgentId = Guid.NewGuid();
    private static readonly Guid WaitingChatId = Guid.NewGuid();

    internal static RemoteNewChatExperience Experience() => new()
    {
        Greeting = "Good morning, Adir",
        Brief = "You're all caught up.",
        SetupsTitle = "Start with",
        JustChat = new() { Id = "plain", Title = "Just chat", Glyph = "✦", Model = "model-a", Detail = "Model A" },
        Setups =
        [
            new()
            {
                Id = "lumi-a", Title = "Lumi", Description = "Model A · High", Glyph = "L", ProjectId = LumiId, ProjectName = "Lumi",
                Model = "model-a", Effort = "high", Quality = "High", Detail = "Model A", IsVariant = true, IsKept = true, CanUseWorktree = true
            },
            new()
            {
                Id = "lumi-b", Title = "Lumi", Description = "Worktree · Model B", Glyph = "L", ProjectId = LumiId, ProjectName = "Lumi",
                Model = "model-b", UseWorktree = true, Detail = "Worktree · Model B", IsVariant = true, CanUseWorktree = true
            },
            new() { Id = "agent", Title = "Coding Lumi", Glyph = "⚡", AgentId = AgentId, AgentName = "Coding Lumi", AgentGlyph = "⚡" }
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
            new()
            {
                Id = TravelId, IsProject = true, Title = "Travel", Hint = "2h ago",
                Setup = new() { Id = "travel", Title = "Travel", Glyph = "T", ProjectId = TravelId, ProjectName = "Travel", Model = "model-b" }
            },
            new()
            {
                Id = AgentId, Title = "Coding Lumi", IsKept = true,
                Setup = new() { Id = "agent-place", Title = "Coding Lumi", Glyph = "⚡", AgentId = AgentId, AgentName = "Coding Lumi", AgentGlyph = "⚡" }
            }
        ],
        Labels = new() { ForYou = "For you", JustChat = "Just chat", Other = "Other", Keep = "Keep" },
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

    [Fact]
    public async Task TheChatsSetupIsJustChat_OneOfTheUsersSetups_OrOther()
    {
        await using var shell = NewShell();
        shell.Chat.ApplyLibraryCatalogs(Library());
        shell.Chat.ApplyCatalogs(Catalogs());
        shell.ApplyNewChatExperience(Experience());

        // A new chat outside projects is Just chat, which says the model it starts with.
        Assert.True(shell.IsJustChatActive);
        Assert.Equal(MobileShellViewModel.JustChatKey, shell.ActiveSetupKey);
        Assert.Equal("Model A", shell.JustChatDetail);
        Assert.False(shell.IsOtherActive);
        Assert.All(shell.StartSetups, setup => Assert.False(setup.ShowsMore));

        // One of Lumi's setups: the one the chat is, never its variant, with its ⋯ and its detail.
        var modelB = shell.StartSetups[1];
        shell.UseStartSetupCommand.Execute(modelB);
        Assert.True(modelB.IsSelected);
        Assert.True(modelB.ShowsMore);
        Assert.True(modelB.ShowsDetail);
        Assert.False(shell.StartSetups[0].IsSelected);
        Assert.Equal("lumi-b", shell.ActiveSetupKey);
        Assert.True(shell.Chat.UseWorktree);
        Assert.Equal("Lumi · Worktree · Model B", modelB.AccessibleName);
        Assert.False(shell.IsJustChatActive);

        // Other: a place the row doesn't hold, which then shows on Other with Keep beside it.
        shell.OpenOtherSheetCommand.Execute(null);
        Assert.True(shell.IsOtherSheetOpen);
        Assert.True(shell.CanGoBack);
        Assert.False(shell.CanDragDrawer);
        Assert.Equal(["Travel"], shell.OtherProjects.Select(place => place.Title));
        Assert.Equal(["Coding Lumi"], shell.OtherAgents.Select(place => place.Title));
        shell.OtherQuery = "TRAV";
        var travel = Assert.Single(shell.OtherProjects);
        Assert.Empty(shell.OtherAgents);
        shell.OtherQuery = "nothing like it";
        Assert.True(shell.HasNoOtherMatch);
        shell.OtherQuery = "";
        shell.PickPlaceCommand.Execute(travel);
        Assert.False(shell.IsOtherSheetOpen);
        Assert.True(shell.IsOtherActive);
        Assert.Equal("Travel", shell.OtherTitle);
        Assert.True(shell.HasOtherMark);
        Assert.Equal("Model B", shell.OtherDetail);
        Assert.True(shell.CanKeepDraft);
        Assert.Equal(MobileShellViewModel.OtherKey, shell.ActiveSetupKey);
        Assert.Equal(TravelId.ToString(), shell.Chat.ProjectValue);
        Assert.Equal(TravelId, shell.ActiveProjectId);
        Assert.DoesNotContain(shell.StartSetups, setup => setup.IsSelected);

        // Back closes a launchpad sheet first.
        shell.OpenNeedsYouCommand.Execute(null);
        Assert.True(shell.IsNeedsYouSheetOpen);
        shell.GoBackCommand.Execute(null);
        Assert.False(shell.IsNeedsYouSheetOpen);

        // Just chat leaves every place.
        shell.UseStartSetupCommand.Execute(shell.JustChatSetup);
        Assert.True(shell.IsJustChatActive);
        Assert.Null(shell.Chat.ProjectValue);
        Assert.False(shell.CanKeepDraft);
        Assert.False(shell.HasOtherMark);
        Assert.Equal("Other", shell.OtherTitle);

        // A project picked in the composer is Other too, named as soon as its name follows its id.
        shell.Chat.OpenContextPickerCommand.Execute("Project");
        shell.Chat.PickContextOptionCommand.Execute(shell.Chat.ContextOptions.Single(option => option.Name == "Travel"));
        Assert.True(shell.IsOtherActive);
        Assert.Equal("Travel", shell.OtherTitle);
        Assert.True(shell.HasOtherMark);
    }

    [Fact]
    public async Task KeepTheSetupsSheetAndStartersAskThePcOverTheWire()
    {
        await using var host = new FakeLumiDesktop
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
        await using var shell = new MobileShellViewModel(
            new LumiRemoteClient("launchpad-test", "Test phone"),
            store: new MobileSettingsStore(Path.Combine(Path.GetTempPath(), "lumi-mobile-tests", Guid.NewGuid().ToString("N"))),
            post: action => action());
        shell.Connect.ManualAddress = host.BaseUrl;
        await shell.Connect.ConnectManuallyCommand.ExecuteAsync(null);
        shell.Connect.PairingCode = "123456";
        await shell.Connect.SubmitCodeCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => shell.BootstrapSnapshotCount > 0 && shell.HasLaunchpad);

        // A starter fills the composer, hands the view its blanks, and tells the PC which one was picked.
        IReadOnlyList<string>? blanks = null;
        shell.StarterApplied += applied => blanks = applied;
        shell.UseLaunchpadStarterCommand.Execute(shell.LaunchpadStarters.Single());
        Assert.Equal("Write an email to [who] about [what]", shell.Chat.PromptText);
        Assert.Equal(["[who]", "[what]"], blanks);
        var used = await ReceivedAsync(host, RemoteProtocol.Actions.UseStarter);
        Assert.Equal("write.email", used.Get("id"));
        Assert.Null(used.Get("projectId"));

        // Keep: the draft as the composer has it, effort named as its picker shows it.
        shell.PickPlaceCommand.Execute(shell.OtherProjects.Single());
        Assert.True(shell.CanKeepDraft);
        await shell.KeepDraftCommand.ExecuteAsync(null);
        var keep = await ReceivedAsync(host, RemoteProtocol.Actions.KeepSetup);
        Assert.Equal(TravelId, keep.GetGuid("projectId"));
        Assert.Null(keep.GetGuid("agentId"));
        Assert.False(keep.GetBool("worktree"));
        Assert.Equal("model-b", keep.Get("model"));
        Assert.Equal(shell.Chat.Quality, keep.Get("quality"));
        Assert.True(keep.GetBool("kept"));

        // A kept setup's sheet stops keeping it, naming it whole, effort as the row holds it.
        var kept = shell.StartSetups[0];
        shell.OpenSetupSheetCommand.Execute(kept);
        Assert.True(shell.IsSetupSheetOpen);
        Assert.Equal("Lumi", shell.SetupSheetTitle);
        Assert.True(shell.SetupSheetIsKept);
        Assert.True(shell.SetupSheetCanUseWorktree);
        Assert.False(shell.SetupSheetIsWorktree);
        Assert.Equal(["Model A", "Model B"], shell.SetupSheetModels.Select(model => model.Title));
        Assert.True(shell.SetupSheetModels[0].IsCurrent);
        await shell.ToggleSetupKeptCommand.ExecuteAsync(null);
        Assert.False(shell.IsSetupSheetOpen);
        var unkeep = await ReceivedAsync(host, RemoteProtocol.Actions.KeepSetup, skip: 1);
        Assert.False(unkeep.GetBool("kept"));
        Assert.Equal(LumiId, unkeep.GetGuid("projectId"));
        Assert.Equal("model-a", unkeep.Get("model"));
        Assert.Equal("high", unkeep.Get("effort"));

        // Its checkout and its model.
        shell.OpenSetupSheetCommand.Execute(shell.StartSetups[1]);
        Assert.True(shell.SetupSheetIsWorktree);
        await shell.ToggleSetupWorktreeCommand.ExecuteAsync(null);
        var checkout = await ReceivedAsync(host, RemoteProtocol.Actions.RetuneSetup);
        Assert.True(checkout.GetBool("worktree"));
        Assert.False(checkout.GetBool("toWorktree"));

        shell.OpenSetupSheetCommand.Execute(shell.StartSetups[2]);
        await shell.PickSetupModelCommand.ExecuteAsync(shell.SetupSheetModels.Single(model => model.ModelId == "model-b"));
        var model = await ReceivedAsync(host, RemoteProtocol.Actions.RetuneSetup, skip: 1);
        Assert.Equal(AgentId, model.GetGuid("agentId"));
        Assert.Equal("model-b", model.Get("toModel"));

        // Don't suggest this.
        shell.OpenSetupSheetCommand.Execute(shell.StartSetups[2]);
        await shell.HideSetupCommand.ExecuteAsync(null);
        var hide = await ReceivedAsync(host, RemoteProtocol.Actions.HideSetup);
        Assert.Equal(AgentId, hide.GetGuid("agentId"));
        Assert.Null(hide.GetGuid("projectId"));

        // A PC that refuses says why, in the chat.
        host.CommandResultFactory = _ => new RemoteCommandResult { Error = "That setup isn't available on this computer." };
        shell.OpenSetupSheetCommand.Execute(shell.StartSetups[2]);
        await shell.HideSetupCommand.ExecuteAsync(null);
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
        shell.OpenSetupSheetCommand.Execute(shell.StartSetups[2]);
        var refused = shell.HideSetupCommand.ExecuteAsync(null);
        await ReceivedAsync(host, RemoteProtocol.Actions.HideSetup, skip: 2);

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
    public async Task ASetupsSheetNamesEachFusionTierAsThePcsMenuDoes()
    {
        await using var shell = NewShell();
        shell.Chat.ApplyLibraryCatalogs(Library());
        var catalogs = Catalogs();
        catalogs.AvailableModels = ["model-a", "auto:balance", "auto:hydrafusion", "auto:hydrafusion-max"];
        catalogs.ModelDisplayNames =
        [
            "model-a=Model A", "auto:balance=Copilot Fusion Standard",
            "auto:hydrafusion=Copilot Fusion High", "auto:hydrafusion-max=Copilot Fusion Max"
        ];
        // The picker lists the tiers as one row, with the effort beside it, as the PC's picker does.
        catalogs.ModelOptions =
        [
            new RemoteModelOption { Model = "model-a", DisplayName = "Model A" },
            new RemoteModelOption
            {
                Model = "auto:balance",
                DisplayName = "Copilot Fusion",
                SelectionAliases = ["auto:balance", "auto:hydrafusion", "auto:hydrafusion-max"]
            }
        ];
        shell.Chat.ApplyCatalogs(catalogs);
        shell.ApplyNewChatExperience(Experience());

        shell.OpenSetupSheetCommand.Execute(shell.StartSetups[0]);
        Assert.Equal(
            ["Model A", "Copilot Fusion Standard", "Copilot Fusion High", "Copilot Fusion Max"],
            shell.SetupSheetModels.Select(model => model.Title));
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
        Assert.False(shell.HasJustChat);
        Assert.False(shell.HasOtherPlaces);
        Assert.True(shell.ShowsSetupRow);
        Assert.Equal(3, shell.StartSetups.Count);
        Assert.Equal("1 new reply", shell.NewChatBrief);
        Assert.Equal(["Recap my day.", "Help me plan tomorrow."], shell.LaunchpadStarters.Select(starter => starter.Prompt));
        Assert.All(shell.LaunchpadStarters, starter => Assert.True(starter.HasGlyph));

        // Its setups apply as before, without a sheet the PC couldn't answer.
        shell.UseStartSetupCommand.Execute(shell.StartSetups[0]);
        Assert.True(shell.StartSetups[0].IsSelected);
        Assert.False(shell.StartSetups[0].ShowsMore);
        Assert.False(shell.IsOtherActive);
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
