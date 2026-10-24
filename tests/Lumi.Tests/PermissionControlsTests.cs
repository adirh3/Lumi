using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Permissions;
using Lumi.ViewModels;
using Lumi.Views;
using Microsoft.Extensions.AI;
using StrataTheme.Controls;
using Xunit;
using Chat = Lumi.Models.Chat;

#pragma warning disable GHCP001 // Exercise the pinned SDK's experimental native permission contract.

namespace Lumi.Tests;

/// <summary>
/// Approvals choose and re-apply Copilot's own permission controls; Copilot enforces them. These
/// tests pin the mapping onto native modes, rules and decisions, and that every request is answered
/// once, by the chat that owns it, through the native decision path.
/// </summary>
[Collection("Headless UI")]
public sealed class PermissionControlsTests
{
    // ── Defaults and persistence ───────────────────────

    [Fact]
    [Trait("Category", "CoreQA")]
    public void Defaults_KeepFullAccess_ForExistingAndNewUsers()
    {
        var settings = JsonSerializer.Deserialize("{}", AppDataJsonContext.Default.UserSettings)!;
        var chat = JsonSerializer.Deserialize("""{"approvalRules":null}""", AppDataJsonContext.Default.Chat)!;
        var project = JsonSerializer.Deserialize("{}", AppDataJsonContext.Default.Project)!;

        Assert.Equal(ApprovalMode.FullAccess, new UserSettings().DefaultApprovalMode);
        Assert.Equal(ApprovalMode.FullAccess, settings.DefaultApprovalMode);
        Assert.Equal(AwayApprovalBehavior.Wait, settings.AwayApprovalBehavior);
        Assert.Empty(settings.ApprovalRules);
        Assert.Null(chat.ApprovalMode);
        Assert.Empty(chat.ApprovalRules);
        Assert.Null(project.ApprovalMode);
        Assert.Equal((ApprovalMode.FullAccess, ApprovalModeSource.Default), ApprovalPolicy.ResolveMode(chat, project, settings));
    }

    [Fact]
    [Trait("Category", "CoreQA")]
    public void ModesAndRules_SurviveSnapshotsJsonAndForks()
    {
        var project = new Project { Name = "Site", ApprovalMode = ApprovalMode.AutoReview };
        project.ApprovalRules.Add(new ApprovalRule { Action = ApprovalRuleAction.Ask, Kind = ApprovalRuleKind.Connector, Target = "GitHub" });
        var chat = new Chat { ProjectId = project.Id, ApprovalMode = ApprovalMode.AskFirst };
        chat.ApprovalRules.Add(new ApprovalRule { Action = ApprovalRuleAction.Allow, Kind = ApprovalRuleKind.Command, Target = "git status", Remembered = true });
        var data = new AppData { Chats = [chat] };
        data.Projects.Add(project);
        data.Settings.DefaultApprovalMode = ApprovalMode.AskFirst;
        data.Settings.AwayApprovalBehavior = AwayApprovalBehavior.Skip;
        data.Settings.ApprovalRules.Add(new ApprovalRule { Action = ApprovalRuleAction.Never, Kind = ApprovalRuleKind.Command, Target = "git push" });

        var restored = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(AppDataSnapshotFactory.CreateIndexSnapshot(data), AppDataJsonContext.Default.AppData),
            AppDataJsonContext.Default.AppData)!;

        Assert.Equal(ApprovalMode.AskFirst, restored.Settings.DefaultApprovalMode);
        Assert.Equal(AwayApprovalBehavior.Skip, restored.Settings.AwayApprovalBehavior);
        Assert.Equal("git push", Assert.Single(restored.Settings.ApprovalRules).Target);
        var restoredProject = Assert.Single(restored.Projects);
        Assert.Equal(ApprovalMode.AutoReview, restoredProject.ApprovalMode);
        Assert.Equal(ApprovalRuleKind.Connector, Assert.Single(restoredProject.ApprovalRules).Kind);
        var restoredChat = Assert.Single(restored.Chats);
        Assert.Equal(ApprovalMode.AskFirst, restoredChat.ApprovalMode);
        Assert.True(Assert.Single(restoredChat.ApprovalRules).Remembered);

        var fork = ChatForkFactory.CreateFork(chat, []).Chat;
        Assert.Equal(ApprovalMode.AskFirst, fork.ApprovalMode);
        Assert.Equal("git status", Assert.Single(fork.ApprovalRules).Target);
        Assert.NotSame(chat.ApprovalRules, fork.ApprovalRules);
    }

    // ── Mapping onto Copilot ───────────────────────────

    [Theory]
    [InlineData(null, null, ApprovalMode.FullAccess, ApprovalModeSource.Default)]
    [InlineData(null, ApprovalMode.AskFirst, ApprovalMode.AskFirst, ApprovalModeSource.Project)]
    [InlineData(ApprovalMode.AutoReview, ApprovalMode.AskFirst, ApprovalMode.AutoReview, ApprovalModeSource.Chat)]
    [InlineData(ApprovalMode.FullAccess, ApprovalMode.AskFirst, ApprovalMode.FullAccess, ApprovalModeSource.Chat)]
    [Trait("Category", "CoreQA")]
    public void Mode_FollowsTheChatThenItsProjectThenTheDefault(
        ApprovalMode? chatMode, ApprovalMode? projectMode, ApprovalMode expected, ApprovalModeSource source)
    {
        var project = new Project { ApprovalMode = projectMode };
        var chat = new Chat { ProjectId = project.Id, ApprovalMode = chatMode };
        Assert.Equal((expected, source), ApprovalPolicy.ResolveMode(chat, project, new UserSettings()));
    }

    [Fact]
    [Trait("Category", "CoreQA")]
    public void Modes_AreCopilotsNativePermissionModes()
    {
        Assert.Equal(PermissionMode.AllowAll, ApprovalPolicy.ToNativeMode(ApprovalMode.FullAccess));
        Assert.Equal(PermissionMode.Assisted, ApprovalPolicy.ToNativeMode(ApprovalMode.AutoReview));
        Assert.Equal(PermissionMode.Manual, ApprovalPolicy.ToNativeMode(ApprovalMode.AskFirst));

        // With only your own accounts, no Copilot reviewer: Auto-review asks, the others are unchanged.
        Assert.Equal(PermissionMode.Manual, ApprovalPolicy.ToNativeMode(ApprovalMode.AutoReview, ownAccountsOnly: true));
        Assert.Equal(PermissionMode.AllowAll, ApprovalPolicy.ToNativeMode(ApprovalMode.FullAccess, ownAccountsOnly: true));
        Assert.Equal(PermissionMode.Manual, ApprovalPolicy.ToNativeMode(ApprovalMode.AskFirst, ownAccountsOnly: true));
    }

    [Fact]
    [Trait("Category", "CoreQA")]
    public void AllowAndNeverRules_BecomeNativeSessionRules_AndAlwaysAskRulesDoNot()
    {
        ApprovalRule[] rules =
        [
            new() { Action = ApprovalRuleAction.Allow, Kind = ApprovalRuleKind.Command, Target = "git status" },
            new() { Action = ApprovalRuleAction.Never, Kind = ApprovalRuleKind.Command, Target = "git push:*" },
            new() { Action = ApprovalRuleAction.Never, Kind = ApprovalRuleKind.EditFiles },
            new() { Action = ApprovalRuleAction.Allow, Kind = ApprovalRuleKind.ReadFiles, Target = @"C:\docs" },
            new() { Action = ApprovalRuleAction.Allow, Kind = ApprovalRuleKind.Connector, Target = "GitHub/create_issue" },
            new() { Action = ApprovalRuleAction.Never, Kind = ApprovalRuleKind.LumiTool, Target = ApprovalPolicy.DesktopGroup },
            new() { Action = ApprovalRuleAction.Ask, Kind = ApprovalRuleKind.Command, Target = "rm" },
            new() { Action = ApprovalRuleAction.Allow, Kind = ApprovalRuleKind.Command, Target = "git status" },
        ];

        var native = ApprovalPolicy.BuildSessionRules(rules, ConnectorKey);

        Assert.Equal(["shell:git status:*", @"read:C:\docs", "github-key:create_issue"], native.Approved.Select(Describe));
        Assert.Equal(
            ["shell:git push:*", "write:", .. ApprovalPolicy.ToolsInGroup(ApprovalPolicy.DesktopGroup).Select(tool => "custom-tool:" + tool)],
            native.Denied.Select(Describe));
    }

    [Fact]
    [Trait("Category", "CoreQA")]
    public void AlwaysAskRules_BecomeTheNativeManagedAskLayer()
    {
        Assert.Null(ApprovalPolicy.BuildAskLayer([new ApprovalRule { Action = ApprovalRuleAction.Never, Kind = ApprovalRuleKind.Command }]));
        Assert.Equal("", ApprovalPolicy.AskLayerSignature(null));

        var layer = ApprovalPolicy.BuildAskLayer(
        [
            new() { Action = ApprovalRuleAction.Ask, Kind = ApprovalRuleKind.Command, Target = "git push" },
            new() { Action = ApprovalRuleAction.Ask, Kind = ApprovalRuleKind.Command },
            new() { Action = ApprovalRuleAction.Ask, Kind = ApprovalRuleKind.EditFiles },
            new() { Action = ApprovalRuleAction.Ask, Kind = ApprovalRuleKind.Connector, Target = "GitHub" },
            new() { Action = ApprovalRuleAction.Ask, Kind = ApprovalRuleKind.LumiTool, Target = ApprovalPolicy.BrowserGroup },
        ], ConnectorKey)!;

        Assert.Equal(
            ["Shell(git push)", "Shell", "Write", "Edit", "Mcp(github-key)",
             .. ApprovalPolicy.ToolsInGroup(ApprovalPolicy.BrowserGroup).Select(tool => $"CustomTool({tool})")],
            layer.Permissions!.Ask!);
        // The layer is fixed when a session starts, so a changed one must read as changed.
        Assert.NotEqual(
            ApprovalPolicy.AskLayerSignature(layer),
            ApprovalPolicy.AskLayerSignature(ApprovalPolicy.BuildAskLayer(
                [new ApprovalRule { Action = ApprovalRuleAction.Ask, Kind = ApprovalRuleKind.Command }])));
    }

    [Fact]
    [Trait("Category", "CoreQA")]
    public void Rules_CombineEverywhereTheProjectAndTheChat_AndRememberingTwiceIsANoOp()
    {
        var everywhere = new ApprovalRule { Action = ApprovalRuleAction.Never, Kind = ApprovalRuleKind.Command, Target = "git push" };
        var forProject = new ApprovalRule { Action = ApprovalRuleAction.Ask, Kind = ApprovalRuleKind.EditFiles };
        var forChat = new ApprovalRule { Action = ApprovalRuleAction.Allow, Kind = ApprovalRuleKind.Command, Target = "npm test", Remembered = true };
        var settings = new UserSettings();
        settings.ApprovalRules.Add(everywhere);
        var project = new Project();
        project.ApprovalRules.Add(forProject);
        var chat = new Chat { ProjectId = project.Id };
        chat.ApprovalRules.Add(forChat);

        Assert.Equal([everywhere, forProject, forChat], ApprovalPolicy.EffectiveRules(settings, project, chat));
        Assert.Equal([everywhere], ApprovalPolicy.EffectiveRules(settings, null, null));
        Assert.True(ApprovalPolicy.IsSameRule(
            new ApprovalRule { Action = ApprovalRuleAction.Allow, Kind = ApprovalRuleKind.Command, Target = "git push:*" },
            new ApprovalRule { Action = ApprovalRuleAction.Allow, Kind = ApprovalRuleKind.Command, Target = " GIT PUSH " }));
        Assert.False(ApprovalPolicy.IsSameRule(everywhere,
            new ApprovalRule { Action = ApprovalRuleAction.Allow, Kind = ApprovalRuleKind.Command, Target = "git push" }));
    }

    [Fact]
    [Trait("Category", "CoreQA")]
    public void LumiToolsThatOnlyLookOrTalk_SkipApproval_WhileHostActionsCanAsk()
    {
        foreach (var tool in new[] { "ask_question", "announce_file", "read_chat", "lumi_browser_look", "ui_inspect", "ui_screenshot" })
            Assert.True(ApprovalPolicy.SkipsApproval(tool), tool);
        foreach (var tool in new[] { "lumi_browser_do", "lumi_browser_js", "ui_do", "ui_click_at", "ui_press_keys", "lumi_fetch", "manage_memories", "manage_chats" })
        {
            Assert.False(ApprovalPolicy.SkipsApproval(tool), tool);
            Assert.NotNull(ApprovalPolicy.GroupOf(tool));
        }
    }

    // ── Session configuration ──────────────────────────

    [Theory]
    [InlineData(false, ApprovalMode.FullAccess)]
    [InlineData(false, ApprovalMode.AutoReview)]
    [InlineData(false, ApprovalMode.AskFirst)]
    [InlineData(true, ApprovalMode.FullAccess)]
    [InlineData(true, ApprovalMode.AutoReview)]
    [InlineData(true, ApprovalMode.AskFirst)]
    public async Task CreateAndResume_InstallLumisHandler_WithoutDroppingProviderOrTools(bool resume, ApprovalMode mode)
    {
        var handler = SessionConfigBuilder.BuildPermissionHandler(() => mode);
        var provider = new GitHub.Copilot.ProviderConfig { Type = "openai", BaseUrl = "https://provider.example/v1" };
        AIFunction[] tools = [AIFunctionFactory.Create(() => "receipt", "ask_question"), AIFunctionFactory.Create(() => "receipt", "ui_do")];
        SessionConfigBase config = resume
            ? SessionConfigBuilder.BuildForResume(
                "prompt", "private-model", null, null, [], [], [.. tools], null, null, handler,
                ChatViewModel.BuildSessionHooks(), provider: provider)
            : SessionConfigBuilder.Build(
                "prompt", "private-model", null, null, [], [], [.. tools], null, null, handler,
                ChatViewModel.BuildSessionHooks(), provider: provider);

        Assert.Same(provider, config.Provider);
        Assert.Equal("private-model", config.Model);
        Assert.True(config.Tools!.Single(tool => tool.Name == "ask_question").AdditionalProperties
            .TryGetValue("skip_permission", out var skip) && skip is true);
        Assert.False(config.Tools!.Single(tool => tool.Name == "ui_do").AdditionalProperties.ContainsKey("skip_permission"));

        var decision = await config.OnPermissionRequest!(
            new PermissionRequestCustomTool { ToolName = "ui_do", ToolDescription = "Click a button" },
            new PermissionInvocation { SessionId = "own-session" });
        if (mode == ApprovalMode.FullAccess)
            Assert.IsType<PermissionDecisionApproveOnce>(decision);
        else
            Assert.IsType<PermissionDecisionNoResult>(decision);
        Assert.Null((await config.Hooks!.OnPreToolUse!(new(), new()))!.PermissionDecision);
        if (resume)
            Assert.False(Assert.IsType<ResumeSessionConfig>(config).ContinuePendingWork);
    }

    [Fact]
    public void FullAccess_StillLeavesManagedUnknownAndSandboxEscapesToAPerson()
    {
        Assert.False(SessionConfigBuilder.NeedsPerson(ApprovalMode.FullAccess, Shell()));
        Assert.False(SessionConfigBuilder.NeedsPerson(ApprovalMode.FullAccess,
            new PermissionRequestCustomTool { ToolName = "ui_do", ToolDescription = "Click" }));
        PermissionRequest[] guarded =
        [
            Write(managedApprovalRequired: true),
            Shell(sandboxBypass: true),
            new PermissionRequestRead { Path = "file.txt", Intention = "Read receipt", RequestSandboxBypass = true },
            new PermissionRequestUrl { Url = "https://example.test", Intention = "Read receipt", RequestSandboxBypass = true },
            new PermissionRequest { Kind = "future-permission" }
        ];
        foreach (var request in guarded)
            Assert.True(SessionConfigBuilder.NeedsPerson(ApprovalMode.FullAccess, request), request.Kind);
        Assert.True(SessionConfigBuilder.NeedsPerson(ApprovalMode.AskFirst, Shell()));
        Assert.True(SessionConfigBuilder.NeedsPerson(ApprovalMode.AutoReview, Shell()));
    }

    // ── Reading native prompts ─────────────────────────

    [Fact]
    public void Cards_ShowTheNativeRequest_AndExactlyWhatRememberingCovers()
    {
        Loc.Load("en");
        var command = ApprovalRequestReader.Read(Command(), alwaysAsk: false);
        Assert.Equal(ApprovalCardKind.Command, command.Kind);
        Assert.Equal("git status --short", command.Subject);
        Assert.True(command.CanRemember);
        Assert.Equal(["git status"], Assert.IsType<PermissionDecisionApproveForSessionApprovalCommands>(command.SessionApproval).CommandIdentifiers);
        Assert.Equal("git status", Assert.Single(command.RememberRules).Target);

        var edit = ApprovalRequestReader.Read(Edit(), alwaysAsk: false);
        Assert.Equal(ApprovalCardKind.Edit, edit.Kind);
        Assert.Equal("+hello", edit.Diff);
        Assert.IsType<PermissionDecisionApproveForSessionApprovalWrite>(edit.SessionApproval);
        Assert.Equal(ApprovalRuleKind.EditFiles, Assert.Single(edit.RememberRules).Kind);

        var connector = ApprovalRequestReader.Read(Connector(), alwaysAsk: false, key => key == "github-key" ? "GitHub" : key);
        Assert.Equal(Loc.Get("Approval_UseConnector", "GitHub"), connector.Title);
        Assert.Contains("Fix the build", connector.Detail);
        Assert.Equal("GitHub/create_issue", Assert.Single(connector.RememberRules).Target);
        Assert.Equal("github-key", Assert.IsType<PermissionDecisionApproveForSessionApprovalMcp>(connector.SessionApproval).ServerName);

        var browser = ApprovalRequestReader.Read(LumiTool("lumi_browser_do"), alwaysAsk: false);
        Assert.Equal(Loc.Get("Approval_GroupTitle_browser"), browser.Title);
        Assert.Contains("click", browser.Detail);
        Assert.Equal(ApprovalPolicy.BrowserGroup, Assert.Single(browser.RememberRules).Target);
        Assert.Equal("lumi_browser_do", Assert.IsType<PermissionDecisionApproveForSessionApprovalCustomTool>(browser.SessionApproval).ToolName);

        var alwaysAsk = ApprovalRequestReader.Read(Command(), alwaysAsk: true);
        Assert.True(alwaysAsk.IsAlwaysAsk);
        Assert.False(alwaysAsk.CanRemember);
        var escape = ApprovalRequestReader.Read(Command(sandboxBypass: true), alwaysAsk: false);
        Assert.True(escape.IsEscalation);
        Assert.False(escape.CanRemember);
        Assert.NotNull(escape.Warning);
    }

    // ── Answering, per chat ────────────────────────────

    [Fact]
    public async Task ConcurrentApprovals_StayWithTheirChatAcrossSwitches_AndAreAnsweredOnce()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            Loc.Load("en");
            var first = new Chat { Title = "First", ApprovalMode = ApprovalMode.AskFirst };
            var second = new Chat { Title = "Second", ApprovalMode = ApprovalMode.AskFirst };
            using var vm = CreateVm(first, second);
            vm.CurrentChat = first;
            var firstAnswers = new Decider();
            var secondAnswers = new Decider();
            var a = vm.PresentPermissionRequest(first, "session", "same-id", Command(), false, firstAnswers.Decide)!;
            var b = vm.PresentPermissionRequest(second, "session", "same-id", Command(), false, secondAnswers.Decide)!;

            Assert.Same(a, Assert.Single(vm.PendingPermissionRequests));
            Assert.True(first.IsAwaitingApproval);
            Assert.True(second.IsAwaitingApproval);
            Assert.True(vm.OwnsLiveChat(second.Id));
            vm.CurrentChat = second;
            Assert.Same(b, Assert.Single(vm.PendingPermissionRequests));

            await a.AllowOnceCommand.ExecuteAsync(null);
            await a.AllowOnceCommand.ExecuteAsync(null);
            var (decision, context) = Assert.Single(firstAnswers.Calls);
            Assert.True(Assert.IsType<PermissionDecisionApproveOnce>(decision).ApprovedInteractively);
            Assert.Equal(PermissionDecisionSource.HumanResponse, context.Source);
            Assert.Equal(ApprovalOutcome.Allowed, a.Outcome);
            Assert.Empty(secondAnswers.Calls);
            Assert.False(first.IsAwaitingApproval);
            Assert.True(second.IsAwaitingApproval);
            Assert.Same(b, Assert.Single(vm.PendingPermissionRequests));

            await b.DenyCommand.ExecuteAsync(null);
            Assert.IsType<PermissionDecisionReject>(Assert.Single(secondAnswers.Calls).Decision);
            Assert.False(second.IsAwaitingApproval);
            Assert.Empty(vm.PendingPermissionRequests);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AllowForThisChat_IsANativeSessionApproval_RememberedOnlyOnceCopilotAcceptsIt()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            Loc.Load("en");
            var chat = new Chat { ApprovalMode = ApprovalMode.AskFirst };
            using var vm = CreateVm(chat);
            vm.CurrentChat = chat;
            var remembered = 0;
            vm.ApprovalsRemembered += () => remembered++;

            var stale = new Decider { Accepts = false };
            var expired = vm.PresentPermissionRequest(chat, "session", "stale", Command(), false, stale.Decide)!;
            await expired.AllowForChatCommand.ExecuteAsync(null);
            Assert.IsType<PermissionDecisionApproveForSession>(Assert.Single(stale.Calls).Decision);
            Assert.Equal(ApprovalOutcome.Expired, expired.Outcome);
            Assert.Empty(chat.ApprovalRules);
            Assert.Equal(0, remembered);

            var live = new Decider();
            var card = vm.PresentPermissionRequest(chat, "session", "live", Command(), false, live.Decide)!;
            await card.AllowForChatCommand.ExecuteAsync(null);
            var approval = Assert.IsType<PermissionDecisionApproveForSession>(Assert.Single(live.Calls).Decision);
            Assert.Equal(["git status"], Assert.IsType<PermissionDecisionApproveForSessionApprovalCommands>(approval.Approval).CommandIdentifiers);
            var rule = Assert.Single(chat.ApprovalRules);
            Assert.Equal((ApprovalRuleAction.Allow, ApprovalRuleKind.Command, "git status", true), (rule.Action, rule.Kind, rule.Target, rule.Remembered));
            Assert.Equal(ApprovalOutcome.AllowedForChat, card.Outcome);
            Assert.Equal(1, remembered);

            var again = vm.PresentPermissionRequest(chat, "session", "again", Command(), false, new Decider().Decide)!;
            await again.AllowForChatCommand.ExecuteAsync(null);
            Assert.Single(chat.ApprovalRules);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AlwaysInTheProject_RemembersForEveryChatInIt()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            Loc.Load("en");
            var project = new Project { Name = "Site" };
            var chat = new Chat { ProjectId = project.Id, ApprovalMode = ApprovalMode.AskFirst };
            var other = new Chat { ProjectId = project.Id };
            var data = NewData(chat, other);
            data.Projects.Add(project);
            using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            vm.CurrentChat = chat;
            // The main window answers this by applying the rules to every open chat (other included).
            var remembered = 0;
            vm.ApprovalsRemembered += () => remembered++;
            var answers = new Decider();
            var card = vm.PresentPermissionRequest(chat, "session", "edit", Edit(), false, answers.Decide)!;

            Assert.True(card.AllowForProjectCommand.CanExecute(null));
            await card.AllowForProjectCommand.ExecuteAsync(null);

            var approval = Assert.IsType<PermissionDecisionApproveForSession>(Assert.Single(answers.Calls).Decision);
            Assert.IsType<PermissionDecisionApproveForSessionApprovalWrite>(approval.Approval);
            var rule = Assert.Single(project.ApprovalRules);
            Assert.Equal((ApprovalRuleAction.Allow, ApprovalRuleKind.EditFiles, true), (rule.Action, rule.Kind, rule.Remembered));
            Assert.Empty(chat.ApprovalRules);
            Assert.Contains(rule, ApprovalPolicy.EffectiveRules(data.Settings, project, other));
            Assert.Equal(ApprovalOutcome.AllowedForProject, card.Outcome);
            Assert.Equal(Loc.Get("Approval_Receipt_AllowedForProject", "Site"), card.ReceiptText);
            Assert.Equal(1, remembered);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DenyWithANote_TellsLumiWhatToDoInstead()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            Loc.Load("en");
            var chat = new Chat { ApprovalMode = ApprovalMode.AskFirst };
            using var vm = CreateVm(chat);
            vm.CurrentChat = chat;
            var answers = new Decider();
            var card = vm.PresentPermissionRequest(chat, "session", "push", Command("git push", "git push"), false, answers.Decide)!;

            card.OpenFeedbackCommand.Execute(null);
            Assert.True(card.IsFeedbackOpen);
            Assert.False(card.SendFeedbackCommand.CanExecute(null));
            card.Feedback = "  Open a pull request instead.  ";
            await card.SendFeedbackCommand.ExecuteAsync(null);

            Assert.Equal("Open a pull request instead.",
                Assert.IsType<PermissionDecisionReject>(Assert.Single(answers.Calls).Decision).Feedback);
            Assert.Equal(ApprovalOutcome.Denied, card.Outcome);
            Assert.Empty(chat.ApprovalRules);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AlwaysAskRequests_OnlyEverGetAOneTimeAnswer()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            Loc.Load("en");
            var chat = new Chat();
            using var vm = CreateVm(chat);
            vm.CurrentChat = chat;
            var answers = new Decider();
            var card = vm.PresentPermissionRequest(chat, "session", "managed", Command(), managed: true, answers.Decide)!;

            Assert.True(card.IsAlwaysAsk);
            Assert.False(card.AllowForChatCommand.CanExecute(null));
            Assert.False(card.AllowForProjectCommand.CanExecute(null));
            // Even when forced, a remember choice answers only this once and saves nothing.
            await card.AllowForChatCommand.ExecuteAsync(null);
            Assert.IsType<PermissionDecisionApproveOnce>(Assert.Single(answers.Calls).Decision);
            Assert.Empty(chat.ApprovalRules);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AutoReview_LetsTheReviewerApproveOnlyWhatItApproves()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            Loc.Load("en");
            var chat = new Chat { ApprovalMode = ApprovalMode.AutoReview };
            using var vm = CreateVm(chat);
            vm.CurrentChat = chat;

            var routine = new Decider();
            Assert.Null(vm.PresentPermissionRequest(chat, "session", "routine",
                Command(review: AssistedApprovalRecommendation.Approve), false, routine.Decide));
            var (decision, context) = Assert.Single(routine.Calls);
            Assert.IsType<PermissionDecisionApproveOnce>(decision);
            Assert.Equal(PermissionDecisionSource.AssistedApproval, context.Source);
            Assert.Empty(vm.PendingPermissionRequests);

            var risky = new Decider();
            var card = vm.PresentPermissionRequest(chat, "session", "risky",
                Command("rm -rf build", "rm", AssistedApprovalRecommendation.RequireApproval, "Deletes files"), false, risky.Decide)!;
            Assert.Empty(risky.Calls);
            Assert.Equal("Deletes files", card.ReviewerReason);

            // When the reviewer fails, the card says so plainly instead of the runtime's wording.
            var unsure = vm.PresentPermissionRequest(chat, "session", "unsure",
                Command("rm -rf build", "rm", AssistedApprovalRecommendation.Error,
                    "The auto-approval judge could not make a valid recommendation."), false, new Decider().Decide)!;
            Assert.Equal(Loc.Get("Approval_ReviewerUnsure"), unsure.ReviewerReason);

            // An Always-ask rule outranks the reviewer, and so does Ask first.
            var managed = new Decider();
            Assert.NotNull(vm.PresentPermissionRequest(chat, "session", "managed",
                Command(review: AssistedApprovalRecommendation.Approve), true, managed.Decide));
            Assert.Empty(managed.Calls);
            chat.ApprovalMode = ApprovalMode.AskFirst;
            var asked = new Decider();
            Assert.NotNull(vm.PresentPermissionRequest(chat, "session", "asked",
                Command(review: AssistedApprovalRecommendation.Approve), false, asked.Decide));
            Assert.Empty(asked.Calls);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(AwayApprovalBehavior.Wait)]
    [InlineData(AwayApprovalBehavior.Skip)]
    public async Task UnattendedTurns_WaitOrSkip_AsTheUserChose(AwayApprovalBehavior away)
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            Loc.Load("en");
            var chat = new Chat { ApprovalMode = ApprovalMode.AskFirst };
            var data = NewData(chat);
            data.Settings.AwayApprovalBehavior = away;
            using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            vm.CurrentChat = chat;
            vm.MarkTurnUnattended(chat.Id);
            var answers = new Decider();
            var card = vm.PresentPermissionRequest(chat, "session", "away", Command(), false, answers.Decide);

            if (away == AwayApprovalBehavior.Wait)
            {
                Assert.NotNull(card);
                Assert.Empty(answers.Calls);
                Assert.True(chat.IsAwaitingApproval);
            }
            else
            {
                Assert.Null(card);
                var (decision, context) = Assert.Single(answers.Calls);
                Assert.IsType<PermissionDecisionUserNotAvailable>(decision);
                Assert.Equal(PermissionDecisionSource.UnattendedFallback, context.Source);
                Assert.False(chat.IsAwaitingApproval);
            }

            // A turn the user sent from the chat waits for them.
            vm.MarkTurnAttended(chat.Id);
            var here = new Decider();
            Assert.NotNull(vm.PresentPermissionRequest(chat, "session", "here", Command(), false, here.Decide));
            Assert.Empty(here.Calls);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    // ── Lifecycle ──────────────────────────────────────

    [Theory]
    [InlineData("stop")]
    [InlineData("cleanup")]
    [InlineData("dispose")]
    public async Task StopCleanupAndDisposal_RetireCards_WithoutLateAnswers(string action)
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            var chat = new Chat { ApprovalMode = ApprovalMode.AskFirst };
            using var vm = CreateVm(chat);
            vm.CurrentChat = chat;
            var answers = new Decider();
            var card = vm.PresentPermissionRequest(chat, "session", "pending", Command(), false, answers.Decide)!;

            if (action == "stop")
                await vm.StopGenerationCommand.ExecuteAsync(null);
            else if (action == "cleanup")
                vm.CleanupSession(chat.Id);
            else
                vm.Dispose();
            await card.AllowOnceCommand.ExecuteAsync(null);

            Assert.False(card.IsPending);
            Assert.Equal(ApprovalOutcome.Expired, card.Outcome);
            Assert.False(chat.IsAwaitingApproval);
            Assert.Empty(vm.PendingPermissionRequests);
            Assert.Empty(answers.Calls);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ARequestSettledElsewhere_IsNotResurrected_AndARepeatShowsOneCard()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            var chat = new Chat { ApprovalMode = ApprovalMode.AskFirst };
            using var vm = CreateVm(chat);
            vm.CurrentChat = chat;

            vm.CompletePermissionRequest(chat.Id, "session", "retired");
            Assert.Null(vm.PresentPermissionRequest(chat, "session", "retired", Command(), false, new Decider().Decide));
            Assert.False(chat.IsAwaitingApproval);

            Assert.NotNull(vm.PresentPermissionRequest(chat, "session", "twice", Command(), false, new Decider().Decide));
            Assert.Null(vm.PresentPermissionRequest(chat, "session", "twice", Command(), false, new Decider().Decide));
            Assert.Single(vm.PendingPermissionRequests);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AFailedAnswer_StaysRetryable_AndNeverLeavesARule()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            Loc.Load("en");
            var chat = new Chat { ApprovalMode = ApprovalMode.AskFirst };
            using var vm = CreateVm(chat);
            vm.CurrentChat = chat;
            var attempts = 0;
            NativeApprovalDecider flaky = (_, _, _) => ++attempts == 1
                ? throw new IOException("connection lost")
                : Task.FromResult(true);
            var card = vm.PresentPermissionRequest(chat, "session", "flaky", Command(), false, flaky)!;

            await card.AllowForChatCommand.ExecuteAsync(null);
            Assert.True(card.IsPending);
            Assert.Contains("connection lost", card.Error);
            Assert.Empty(chat.ApprovalRules);
            Assert.True(card.AllowForChatCommand.CanExecute(null));

            await card.AllowForChatCommand.ExecuteAsync(null);
            Assert.False(card.IsPending);
            Assert.Single(chat.ApprovalRules);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CancellingWhileAnswering_RetiresTheCardWithoutAnError()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            var chat = new Chat { ApprovalMode = ApprovalMode.AskFirst };
            using var vm = CreateVm(chat);
            vm.CurrentChat = chat;
            NativeApprovalDecider slow = async (_, _, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return true;
            };
            var card = vm.PresentPermissionRequest(chat, "session", "slow", Command(), false, slow)!;

            var response = card.AllowOnceCommand.ExecuteAsync(null);
            vm.CancelPermissionRequests(chat.Id);
            await response;

            Assert.False(card.IsPending);
            Assert.Null(card.Error);
            Assert.Empty(vm.PendingPermissionRequests);
        }, CancellationToken.None);
    }

    // ── Choosing modes ─────────────────────────────────

    [Fact]
    public async Task ThePicker_ChangesOnlyThisChat_AndFollowingTheDefaultInheritsAgain()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            Loc.Load("en");
            var project = new Project { Name = "Site", ApprovalMode = ApprovalMode.AskFirst };
            var chat = new Chat { ProjectId = project.Id };
            var other = new Chat();
            var data = NewData(chat, other);
            data.Projects.Add(project);
            using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            vm.CurrentChat = chat;

            Assert.Equal(ApprovalMode.AskFirst, vm.CurrentApprovalMode);
            Assert.Equal(Loc.Get("ApprovalMode_Origin_Project", "Site"), vm.ApprovalModeOrigin);
            Assert.False(vm.HasOwnApprovalMode);
            Assert.Equal(StrataModeTone.Caution, vm.ApprovalModeTone);

            vm.SelectApprovalModeCommand.Execute(vm.ApprovalModeOptions.Single(option => option.Mode == ApprovalMode.AutoReview));
            Assert.Equal(ApprovalMode.AutoReview, chat.ApprovalMode);
            Assert.True(vm.HasOwnApprovalMode);
            Assert.Equal(StrataModeTone.Accent, vm.ApprovalModeTone);
            Assert.Null(other.ApprovalMode);
            Assert.Equal(ApprovalMode.AskFirst, project.ApprovalMode);

            vm.FollowDefaultApprovalModeCommand.Execute(null);
            Assert.Null(chat.ApprovalMode);
            Assert.Equal(ApprovalMode.AskFirst, vm.CurrentApprovalMode);

            vm.CurrentChat = other;
            Assert.True(vm.IsFullAccessMode);
            Assert.Equal(StrataModeTone.Neutral, vm.ApprovalModeTone);
            Assert.Equal(Loc.Get("ApprovalMode_Origin_Default"), vm.ApprovalModeOrigin);

            // Before a new chat exists, the choice waits as its draft.
            vm.CurrentChat = null;
            vm.SelectApprovalModeCommand.Execute(vm.ApprovalModeOptions.Single(option => option.Mode == ApprovalMode.AskFirst));
            Assert.Equal(ApprovalMode.AskFirst, vm.CurrentApprovalMode);
            Assert.Equal(ApprovalMode.FullAccess, data.Settings.DefaultApprovalMode);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MovingAChatToAnotherProject_ShowsThatProjectsMode_AtOnce()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            Loc.Load("en");
            var careful = new Project { Name = "Careful", ApprovalMode = ApprovalMode.AskFirst };
            var relaxed = new Project { Name = "Relaxed" };
            var chat = new Chat { ProjectId = careful.Id };
            var data = NewData(chat);
            data.Projects.AddRange([careful, relaxed]);
            using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            vm.CurrentChat = chat;
            Assert.Equal(ApprovalMode.AskFirst, vm.CurrentApprovalMode);
            var changed = new List<string?>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            vm.SetProjectId(relaxed.Id);
            Assert.Equal(ApprovalMode.FullAccess, vm.CurrentApprovalMode);
            Assert.Contains(nameof(ChatViewModel.CurrentApprovalModeOption), changed);

            // A new chat's draft takes the mode of the project it will start in.
            vm.CurrentChat = null;
            changed.Clear();
            vm.SetProjectId(careful.Id);
            Assert.Equal(ApprovalMode.AskFirst, vm.CurrentApprovalMode);
            Assert.Contains(nameof(ChatViewModel.CurrentApprovalModeOption), changed);
            changed.Clear();
            vm.ClearProjectId();
            Assert.Equal(ApprovalMode.FullAccess, vm.CurrentApprovalMode);
            Assert.Contains(nameof(ChatViewModel.CurrentApprovalModeOption), changed);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SettingsPermissions_ChangesTheDefault_AndBuildsRulesForEverywhereOrAProject()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            Loc.Load("en");
            var project = new Project { Name = "Site" };
            var data = new AppData();
            data.Projects.Add(project);
            await using var browser = new BrowserService();
            using var vm = new SettingsViewModel(new DataStore(data), TestCopilot.Shared, browser, new UpdateService())
            {
                SelectedPageIndex = SettingsViewModel.PermissionsPageIndex
            };
            var changes = 0;
            vm.ApprovalPolicyChanged += () => changes++;
            var view = new SettingsView { DataContext = vm };
            var window = new Window { Content = view, Width = 1000, Height = 800 };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.True(view.FindSettingsControl<ItemsControl>("DefaultApprovalModeChoices")!.IsEffectivelyVisible);
                Assert.False(view.FindSettingsControl<Border>("ApprovalRuleBuilder")!.IsVisible);

                Assert.Equal(ApprovalMode.FullAccess, Assert.Single(vm.DefaultModeCards, card => card.IsSelected).Mode);
                vm.DefaultModeCards.Single(card => card.Mode == ApprovalMode.AskFirst).SelectCommand.Execute(null);
                Assert.Equal(ApprovalMode.AskFirst, data.Settings.DefaultApprovalMode);
                Assert.True(vm.IsDefaultApprovalModeModified);
                vm.RevertDefaultApprovalModeCommand.Execute(null);
                Assert.Equal(ApprovalMode.FullAccess, data.Settings.DefaultApprovalMode);
                Assert.False(vm.IsDefaultApprovalModeModified);

                vm.StartAddApprovalRuleCommand.Execute(null);
                window.UpdateLayout();
                Assert.True(view.FindSettingsControl<Border>("ApprovalRuleBuilder")!.IsVisible);
                vm.NewRuleTarget = "git push";
                vm.SelectNewRuleActionCommand.Execute("never");
                Assert.Contains("git push", vm.NewRulePreview);
                vm.AddApprovalRuleCommand.Execute(null);
                var rule = Assert.Single(data.Settings.ApprovalRules);
                Assert.Equal((ApprovalRuleAction.Never, ApprovalRuleKind.Command, "git push"), (rule.Action, rule.Kind, rule.Target));
                Assert.False(vm.IsAddingApprovalRule);

                vm.StartAddApprovalRuleFor(project.Id);
                Assert.Equal(project.Id, vm.NewRuleScope!.Value);
                vm.NewRuleKind = vm.RuleKindOptions.Single(option => option.Value == ApprovalRuleKind.LumiTool);
                vm.NewRuleGroup = vm.LumiToolGroupOptions.Single(option => option.Value == ApprovalPolicy.DesktopGroup);
                vm.AddApprovalRuleCommand.Execute(null);
                var projectRule = Assert.Single(project.ApprovalRules);
                Assert.Equal((ApprovalRuleAction.Ask, ApprovalRuleKind.LumiTool, ApprovalPolicy.DesktopGroup),
                    (projectRule.Action, projectRule.Kind, projectRule.Target));
                Assert.Equal(2, vm.ApprovalRuleRows.Count);

                // A new answer for the same command replaces the old one.
                vm.StartAddApprovalRuleCommand.Execute(null);
                vm.NewRuleTarget = "git push:*";
                vm.SelectNewRuleActionCommand.Execute("allow");
                vm.AddApprovalRuleCommand.Execute(null);
                Assert.Equal(ApprovalRuleAction.Allow, Assert.Single(data.Settings.ApprovalRules).Action);

                vm.RemoveApprovalRuleCommand.Execute(vm.ApprovalRuleRows.Single(row => row.Scope == ApprovalRuleScope.Project));
                Assert.Empty(project.ApprovalRules);

                vm.SelectAwayBehaviorCommand.Execute("skip");
                Assert.Equal(AwayApprovalBehavior.Skip, data.Settings.AwayApprovalBehavior);
                Assert.True(changes >= 5);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AProjectsMode_SavesWithTheProject_AndCanFollowTheDefaultAgain()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            Loc.Load("en");
            var project = new Project { Name = "Site" };
            var data = new AppData();
            data.Projects.Add(project);
            var vm = new ProjectsViewModel(new DataStore(data));
            var changes = 0;
            vm.ApprovalPolicyChanged += () => changes++;
            vm.SelectedProject = project;

            Assert.True(Assert.Single(vm.ProjectApprovalChoices, choice => choice.IsSelected).Mode is null);
            vm.ProjectApprovalChoices.Single(choice => choice.Mode == ApprovalMode.AskFirst).SelectCommand.Execute(null);
            Assert.True(vm.HasUnsavedChanges);
            Assert.Null(project.ApprovalMode);
            vm.SaveProjectCommand.Execute(null);
            Assert.Equal(ApprovalMode.AskFirst, project.ApprovalMode);
            Assert.Equal(1, changes);

            vm.ProjectApprovalChoices.Single(choice => choice.Mode is null).SelectCommand.Execute(null);
            vm.SaveProjectCommand.Execute(null);
            Assert.Null(project.ApprovalMode);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    // ── In the chat ────────────────────────────────────

    [Theory]
    [InlineData(600)]
    [InlineData(1000)]
    [InlineData(1600)]
    public async Task ApprovalCards_RenderInTheChat_WithTheModePickerAndWaitingBar(double width)
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            Loc.Load("en");
            var chat = new Chat { Title = "Owner", ApprovalMode = ApprovalMode.AskFirst };
            using var vm = CreateVm(chat);
            vm.CurrentChat = chat;
            var view = new ChatView { DataContext = vm };
            var window = new Window { Content = view, Width = width, Height = 800 };
            window.Show();
            try
            {
                vm.PresentPermissionRequest(chat, "session", "command", Command(), false, new Decider().Decide);
                vm.PresentPermissionRequest(chat, "session", "edit", Edit(), false, new Decider().Decide);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                var picker = view.FindControl<StrataModePicker>("ApprovalModePicker")!;
                Assert.True(picker.IsEffectivelyVisible);
                Assert.Equal(Loc.Get("ApprovalMode_AskFirst"), picker.Label);
                Assert.Equal(StrataModeTone.Caution, picker.Tone);
                Assert.True(view.FindControl<Border>("ApprovalsWaitingBar")!.IsEffectivelyVisible);
                Assert.Equal(Loc.Get("Approval_WaitingMany", 2), vm.ApprovalsWaitingText);

                var cards = view.GetVisualDescendants().OfType<StrataApprovalCard>().ToList();
                Assert.Equal(2, cards.Count);
                Assert.Contains(cards, card => card.Title == Loc.Get("Approval_RunCommand") && card.Subject == "git status --short");
                Assert.All(cards, card => Assert.Equal(StrataApprovalStatus.Pending, card.Status));

                // However wide the chat, a card starts where the chat does and fills up to its reading width
                // (within the pixel layout rounding may add).
                foreach (var card in cards)
                {
                    var surface = card.GetVisualDescendants().OfType<Border>().First(border => border.Name == "PART_Card");
                    var slot = card.GetVisualParent()!.Bounds.Width - card.Margin.Left - card.Margin.Right;
                    Assert.Equal(card.Margin.Left, card.Bounds.X + surface.Bounds.X, 1);
                    Assert.InRange(surface.Bounds.Width, Math.Min(640, slot) - 1, Math.Min(640, slot) + 1);
                }
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    // ── Helpers ────────────────────────────────────────

    /// <summary>Records the native decisions a card sends, standing in for the session's pending-request RPC.</summary>
    private sealed class Decider
    {
        public List<(PermissionDecision Decision, PermissionDecisionContext Context)> Calls { get; } = [];
        public bool Accepts { get; init; } = true;

        public NativeApprovalDecider Decide => (decision, context, _) =>
        {
            Calls.Add((decision, context));
            return Task.FromResult(Accepts);
        };
    }

    private static string ConnectorKey(string name) => name == "GitHub" ? "github-key" : name;

    private static string Describe(PermissionRule rule) => $"{rule.Kind}:{rule.Argument}";

    private static AppData NewData(params Chat[] chats) => new()
    {
        Settings = new UserSettings { AutoGenerateTitles = false, SuggestFollowUps = false },
        Chats = chats.ToList()
    };

    private static ChatViewModel CreateVm(params Chat[] chats)
        => new(new DataStore(NewData(chats)), TestCopilot.Shared);

    private static PermissionPromptRequestCommands Command(
        string text = "git status --short",
        string identifier = "git status",
        AssistedApprovalRecommendation? review = null,
        string? reason = null,
        bool sandboxBypass = false) => new()
    {
        FullCommandText = text,
        Intention = "Check the repository",
        CommandIdentifiers = [identifier],
        CanOfferSessionApproval = true,
        RequestSandboxBypass = sandboxBypass,
        AssistedApproval = review is { } recommendation
            ? new PermissionAssistedApproval { Recommendation = recommendation, Reason = reason }
            : null
    };

    private static PermissionPromptRequestWrite Edit() => new()
    {
        FileName = "notes.md",
        Intention = "Write notes",
        Diff = "+hello",
        CanOfferSessionApproval = true
    };

    private static PermissionPromptRequestMcp Connector() => new()
    {
        ServerName = "github-key",
        ToolName = "create_issue",
        ToolTitle = "Create issue",
        Args = Json("""{"title":"Fix the build"}""")
    };

    private static PermissionPromptRequestCustomTool LumiTool(string name) => new()
    {
        ToolName = name,
        ToolDescription = "Browser action",
        Args = Json("""{"action":"click","target":"Submit"}""")
    };

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static PermissionRequestShell Shell(bool sandboxBypass = false) => new()
    {
        FullCommandText = "echo receipt",
        CanOfferSessionApproval = false,
        Commands = [],
        HasWriteFileRedirection = false,
        Intention = "Diagnostic receipt",
        PossiblePaths = [],
        PossibleUrls = [],
        RequestSandboxBypass = sandboxBypass
    };

    private static PermissionRequestWrite Write(bool managedApprovalRequired = false) => new()
    {
        FileName = "file.txt",
        CanOfferSessionApproval = false,
        Diff = "+receipt",
        Intention = "Write receipt",
        ManagedApprovalRequired = managedApprovalRequired
    };
}
