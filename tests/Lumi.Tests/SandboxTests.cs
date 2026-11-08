using System.IO.Pipelines;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Avalonia.Controls;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Capabilities;
using Lumi.Services.Permissions;
using Lumi.Services.Storage;
using Lumi.ViewModels;
using Lumi.Views;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using Xunit;
using Chat = Lumi.Models.Chat;
using JsonRpc = StreamJsonRpc.JsonRpc;

#pragma warning disable GHCP001 // The sandbox contract is experimental in the pinned SDK.

namespace Lumi.Tests;

/// <summary>
/// The sandbox: Lumi turns your default, a project's choice and a chat's answers into Copilot's sandbox
/// policy, which the runtime enforces, and only a person lets a request past it.
/// </summary>
[Collection("Headless UI")]
public sealed class SandboxTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\Work" : "/work";

    private static string At(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>Your settings as a new install has them: no sandbox by default, internet allowed.</summary>
    private static readonly UserSettings Defaults = new();

    private static SandboxConfig? Build(
        Project? project, Chat chat, string workingDirectory, IEnumerable<string> readOnlyRoots, SandboxHostSupport? host,
        UserSettings? settings = null)
        => SandboxPolicy.Build(settings ?? Defaults, project, chat, workingDirectory, readOnlyRoots, host);

    // ── The policy ─────────────────────────────────────

    [Fact]
    public void WithoutAProjectSandbox_TheRuntimeKeepsItsDefault()
    {
        var chat = new Chat();
        var project = new Project { WorkingDirectory = At("app") };
        Assert.Null(Build(null, chat, At("app"), [], null));
        Assert.Null(Build(project, chat, At("app"), [], null));

        project.Sandbox.Enabled = true;
        Assert.NotNull(Build(project, chat, At("app"), [], null));
        chat.SandboxDisabled = true;
        Assert.Null(Build(project, chat, At("app"), [], null));
    }

    [Fact]
    public void AProjectsSandbox_ReachesItsFolder_TheFoldersItAllows_AndNothingElse()
    {
        var project = new Project
        {
            WorkingDirectory = At("app"),
            Sandbox = new ProjectSandbox
            {
                Enabled = true,
                AllowInternet = false,
                Folders =
                [
                    new() { Path = At("data"), Access = SandboxFolderAccess.ReadWrite },
                    new() { Path = At("docs"), Access = SandboxFolderAccess.ReadOnly },
                    new() { Path = At("app", "secrets"), Access = SandboxFolderAccess.Blocked },
                ]
            }
        };
        var chat = new Chat
        {
            ProjectId = project.Id,
            SandboxFolders =
            [
                // A weaker rule for a folder the project already opens wider changes nothing.
                new() { Path = At("data") + Path.DirectorySeparatorChar, Access = SandboxFolderAccess.ReadOnly },
                new() { Path = At("downloads"), Access = SandboxFolderAccess.ReadWrite },
            ]
        };

        var config = Build(project, chat, At("app"), [At("skills"), At("docs")], null)!;

        Assert.True(config.Enabled);
        // Every way past the sandbox reaches a person as an approval card.
        Assert.True(config.AllowBypass);
        Assert.True(config.AddCurrentWorkingDirectory);
        Assert.True(config.Auth!.Git);
        Assert.True(config.Auth.Gh);
        var filesystem = config.UserPolicy!.Filesystem!;
        Assert.Equal([At("data"), At("downloads")], filesystem.ReadwritePaths);
        Assert.Equal([At("docs"), At("skills")], filesystem.ReadonlyPaths);
        Assert.Equal([At("app", "secrets")], filesystem.DeniedPaths);
        Assert.False(config.UserPolicy.Network!.AllowOutbound);
        // Windows needs it for name resolution and the git sign-in proxy.
        Assert.True(config.UserPolicy.Network.AllowLocalNetwork);
    }

    [Fact]
    public void TheSandboxOpensOnlyAFolderTheChatOwns()
    {
        var project = new Project { WorkingDirectory = At("app"), Sandbox = { Enabled = true } };
        var worktree = new Chat { ProjectId = project.Id, WorktreePath = At("worktrees", "feature") };

        Assert.True(Build(project, worktree, At("app", "src"), [], null)!.AddCurrentWorkingDirectory);
        Assert.True(Build(project, worktree, At("worktrees", "feature", "app"), [], null)!.AddCurrentWorkingDirectory);
        // A chat whose project folder is gone runs in the user's profile, which the sandbox keeps closed.
        Assert.False(Build(project, new Chat(), At("Users", "me"), [], null)!.AddCurrentWorkingDirectory);
        Assert.False(Build(project, new Chat(), At("application"), [], null)!.AddCurrentWorkingDirectory);
    }

    [Fact]
    public void WhatTheComputerCantEnforce_IsNeverAskedFor()
    {
        var project = new Project { WorkingDirectory = At("app"), Sandbox = { Enabled = true, AllowInternet = true } };
        var withoutNetwork = new SandboxHostSupport
        {
            Supported = true,
            Capabilities = [new SandboxHostCapability { Name = SandboxPolicy.NetworkCapability, Supported = false, Reason = "Install slirp4netns." }]
        };

        Assert.True(Build(project, new Chat(), At("app"), [], null)!.UserPolicy!.Network!.AllowOutbound);
        Assert.False(Build(project, new Chat(), At("app"), [], withoutNetwork)!.UserPolicy!.Network!.AllowOutbound);
        Assert.False(SandboxPolicy.Supports(withoutNetwork, SandboxPolicy.NetworkCapability));
        Assert.True(SandboxPolicy.Supports(withoutNetwork, SandboxPolicy.BlockedFoldersCapability));
        Assert.False(SandboxPolicy.Supports(new SandboxHostSupport { Supported = false, Capabilities = [] }, SandboxPolicy.BlockedFoldersCapability));
        Assert.True(SandboxPolicy.Supports(null, SandboxPolicy.NetworkCapability));
    }

    [Fact]
    public void AGrant_IsKeptWithTheStrongestAccessAndReplacesTheReadOnlyEntriesItDropped()
    {
        var folders = new List<SandboxFolder>
        {
            new() { Path = At("data"), Access = SandboxFolderAccess.ReadOnly },
            new() { Path = At("old"), Access = SandboxFolderAccess.ReadOnly },
        };

        SandboxPolicy.Remember(folders, new SandboxPathGrant(At("docs", "a.txt"), CanWrite: false, []));
        SandboxPolicy.Remember(folders, new SandboxPathGrant(At("data"), CanWrite: true, [At("old")]));
        SandboxPolicy.Remember(folders, new SandboxPathGrant(At("data") + Path.DirectorySeparatorChar, CanWrite: false, []));

        Assert.Equal(
            [(At("data"), SandboxFolderAccess.ReadWrite), (At("docs", "a.txt"), SandboxFolderAccess.ReadOnly)],
            folders.Select(folder => (folder.Path, folder.Access)));
    }

    [Fact]
    public void ALiveSessionIsUpdatedOnlyWhenItsPolicyChanges()
    {
        var project = new Project { WorkingDirectory = At("app"), Sandbox = { Enabled = true } };
        var chat = new Chat { ProjectId = project.Id };
        var first = SandboxPolicy.Describe(Build(project, chat, At("app"), [At("skills")], null));

        Assert.Equal(first, SandboxPolicy.Describe(Build(project, chat, At("app"), [At("skills")], null)));
        chat.SandboxFolders.Add(new SandboxFolder { Path = At("data") });
        Assert.NotEqual(first, SandboxPolicy.Describe(Build(project, chat, At("app"), [At("skills")], null)));
        Assert.Equal("", SandboxPolicy.Describe(null));

        // An organization can still require the sandbox under every choice Lumi reports.
        Assert.Equal(SandboxConfigSource.UserEnabled, SandboxPolicy.SourceOf(Defaults, project, chatTurnedOff: false));
        Assert.Equal(SandboxConfigSource.SessionDisabled, SandboxPolicy.SourceOf(Defaults, project, chatTurnedOff: true));
        Assert.Equal(SandboxConfigSource.UserDisabled, SandboxPolicy.SourceOf(Defaults, new Project(), chatTurnedOff: false));
    }

    [Fact]
    public void YourDefault_SandboxesProjectsWithAFolder_UnlessTheyChooseOtherwise_AndNeverChatsOutsideProjects()
    {
        var settings = new UserSettings
        {
            Sandbox = new SandboxSettings
            {
                Enabled = true,
                AllowInternet = false,
                Folders =
                [
                    new() { Path = At("shared"), Access = SandboxFolderAccess.ReadOnly },
                    new() { Path = At("app", "secrets"), Access = SandboxFolderAccess.Blocked },
                ]
            }
        };
        var following = new Project
        {
            WorkingDirectory = At("app"),
            Sandbox = { Folders = [new() { Path = At("app", "secrets"), Access = SandboxFolderAccess.ReadWrite }] }
        };
        var chat = new Chat { ProjectId = following.Id };

        Assert.True(SandboxPolicy.IsOnFor(settings, following));
        var config = Build(following, chat, At("app"), [], null, settings)!;
        Assert.False(config.UserPolicy!.Network!.AllowOutbound);
        // Your folders reach every sandboxed project, and a block wins over the project's own grant.
        Assert.Equal([At("shared")], config.UserPolicy.Filesystem!.ReadonlyPaths);
        Assert.Equal([At("app", "secrets")], config.UserPolicy.Filesystem.DeniedPaths);
        Assert.Empty(config.UserPolicy.Filesystem.ReadwritePaths!);

        // A project's own internet choice beats yours.
        following.Sandbox.AllowInternet = true;
        Assert.True(Build(following, chat, At("app"), [], null, settings)!.UserPolicy!.Network!.AllowOutbound);

        // Your default needs a folder to contain the commands in; a project's own choice is its own.
        var folderless = new Project();
        Assert.False(SandboxPolicy.IsOnFor(settings, folderless));
        Assert.Null(Build(folderless, new Chat { ProjectId = folderless.Id }, At("Users", "me"), [], null, settings));
        Assert.False(SandboxPolicy.IsOnFor(settings, new Project { WorkingDirectory = At("other"), Sandbox = { Enabled = false } }));
        Assert.True(SandboxPolicy.IsOnFor(Defaults, new Project { WorkingDirectory = At("other"), Sandbox = { Enabled = true } }));
        Assert.False(SandboxPolicy.IsOnFor(Defaults, following));

        // Chats outside projects run in your home folder and are never sandboxed.
        Assert.False(SandboxPolicy.IsOnFor(settings, null));
        Assert.Null(Build(null, new Chat(), At("Users", "me"), [], null, settings));

        // A chat that turned it off is off, and says so to an organization's managed policy.
        chat.SandboxDisabled = true;
        Assert.Null(Build(following, chat, At("app"), [], null, settings));
        Assert.Equal(SandboxConfigSource.SessionDisabled, SandboxPolicy.SourceOf(settings, following, chatTurnedOff: true));
    }

    // ── Persistence ────────────────────────────────────

    [Fact]
    public void TheSandbox_IsOffByDefault_AndSurvivesSavesSnapshotsAndForks()
    {
        var bare = JsonSerializer.Deserialize("""{"sandbox":null}""", AppDataJsonContext.Default.Project)!;
        Assert.Null(bare.Sandbox.Enabled);
        Assert.Null(bare.Sandbox.AllowInternet);
        Assert.Empty(bare.Sandbox.Folders);
        Assert.False(new Chat().SandboxDisabled);
        var fresh = JsonSerializer.Deserialize("""{"sandbox":null}""", AppDataJsonContext.Default.UserSettings)!;
        Assert.False(fresh.Sandbox.Enabled);
        Assert.True(fresh.Sandbox.AllowInternet);
        Assert.Empty(fresh.Sandbox.Folders);

        var project = new Project
        {
            Name = "Site",
            Sandbox = new ProjectSandbox { Enabled = true, AllowInternet = false, Folders = [new() { Path = At("data"), Access = SandboxFolderAccess.Blocked }] }
        };
        var chat = new Chat { ProjectId = project.Id, SandboxDisabled = true, SandboxFolders = [new() { Path = At("docs"), Access = SandboxFolderAccess.ReadOnly }] };
        var data = new AppData { Chats = [chat] };
        data.Projects.Add(project);
        data.Settings.Sandbox = new SandboxSettings
        {
            Enabled = true,
            AllowInternet = false,
            Folders = [new() { Path = At("shared"), Access = SandboxFolderAccess.ReadOnly }]
        };

        var snapshot = AppDataSnapshotFactory.CreateIndexSnapshot(data);
        Assert.NotSame(data.Settings.Sandbox, snapshot.Settings.Sandbox);
        Assert.NotSame(data.Settings.Sandbox.Folders, snapshot.Settings.Sandbox.Folders);
        var restored = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(snapshot, AppDataJsonContext.Default.AppData),
            AppDataJsonContext.Default.AppData)!;
        Assert.True(restored.Settings.Sandbox.Enabled);
        Assert.False(restored.Settings.Sandbox.AllowInternet);
        var shared = Assert.Single(restored.Settings.Sandbox.Folders);
        Assert.Equal((At("shared"), SandboxFolderAccess.ReadOnly), (shared.Path, shared.Access));
        var restoredProject = Assert.Single(restored.Projects);
        Assert.True(restoredProject.Sandbox.Enabled);
        Assert.False(restoredProject.Sandbox.AllowInternet);
        Assert.Equal(SandboxFolderAccess.Blocked, Assert.Single(restoredProject.Sandbox.Folders).Access);
        Assert.NotSame(project.Sandbox, AppDataSnapshotFactory.CreateIndexSnapshot(data).Projects[0].Sandbox);
        var restoredChat = Assert.Single(restored.Chats);
        Assert.True(restoredChat.SandboxDisabled);
        Assert.Equal(At("docs"), Assert.Single(restoredChat.SandboxFolders).Path);

        var stored = ChatStorageJson.DeserializeChat(ChatStorageJson.SerializeChat(chat));
        Assert.True(stored.SandboxDisabled);
        Assert.Equal(SandboxFolderAccess.ReadOnly, Assert.Single(stored.SandboxFolders).Access);
        Assert.False(ChatStorageJson.DeserializeChat(ChatStorageJson.SerializeChat(new Chat())).SandboxDisabled);

        var fork = ChatForkFactory.CreateFork(chat, []).Chat;
        Assert.True(fork.SandboxDisabled);
        Assert.Equal(At("docs"), Assert.Single(fork.SandboxFolders).Path);
        Assert.NotSame(chat.SandboxFolders, fork.SandboxFolders);
    }

    // ── Requests to go past it ─────────────────────────

    [Fact]
    public void Escapes_AreReadFromTheFullRequest_WhichAloneCarriesThemForFiles()
    {
        Loc.Load("en");
        var write = Write(grant: new PermissionSandboxPathGrant
        {
            Access = PermissionSandboxPathGrantAccess.ReadWrite,
            Path = At("data"),
            DeniedPath = At("data", "new.txt"),
            RemovedReadonlyPaths = [At("data")]
        });
        var escape = SandboxEscape.Of(write, EditPrompt())!;
        Assert.Equal((At("data"), true), (escape.Grant!.Path, escape.Grant.CanWrite));
        Assert.Equal([At("data")], escape.Grant.RemovedReadOnlyPaths);

        var card = ApprovalRequestReader.Read(EditPrompt(), alwaysAsk: false, escape: escape);
        Assert.True(card.IsEscalation);
        Assert.False(card.CanRemember);
        Assert.Equal(write.RequestSandboxBypassReason, card.Warning);

        // Without an escape the same prompt is an ordinary edit.
        Assert.False(ApprovalRequestReader.Read(EditPrompt(), alwaysAsk: false).IsEscalation);
        Assert.Null(SandboxEscape.Of(new PermissionRequestWrite { FileName = "a.txt", Diff = "+a", Intention = "Write", CanOfferSessionApproval = true }, EditPrompt()));

        var retry = SandboxEscape.Of(null, CommandPrompt(bypass: true, retry: true))!;
        Assert.True(retry.IsRetry);
        Assert.Null(retry.Grant);
        Assert.Equal(Loc.Get("Approval_SandboxBypass"), ApprovalRequestReader.Read(CommandPrompt(bypass: true, reason: null), false).Warning);
        Assert.True(SandboxEscape.Of(new PermissionRequestUrl { Url = "https://example.test", Intention = "Fetch", RequestSandboxBypass = true }, null) is { Grant: null, IsRetry: false });

        // A request a resumed session still waits on keeps only its prompt: a file's escape is found in the
        // history, and without it the request isn't shown as an ordinary one, since it may go past the sandbox.
        var pending = new PendingPermissionRequest { RequestId = "write", Request = EditPrompt() };
        Assert.True(SandboxEscape.NeedsHistory(pending));
        Assert.Throws<InvalidOperationException>(() => SandboxEscape.OfPending(pending, null));
        Assert.Throws<InvalidOperationException>(() => SandboxEscape.OfPending(pending, []));
        SessionEvent[] history =
        [
            new PermissionRequestedEvent { Data = new PermissionRequestedData { RequestId = "write", PermissionRequest = write, PromptRequest = EditPrompt() } },
            new PermissionRequestedEvent { Data = new PermissionRequestedData { RequestId = "other", PermissionRequest = Write(grant: null), PromptRequest = EditPrompt() } },
        ];
        Assert.Equal(At("data"), SandboxEscape.OfPending(pending, history)?.Grant?.Path);
        var command = new PendingPermissionRequest { RequestId = "command", Request = CommandPrompt(bypass: true) };
        Assert.False(SandboxEscape.NeedsHistory(command));
        Assert.True(SandboxEscape.OfPending(command, null) is { Grant: null, IsRetry: false });
    }

    [Fact]
    public async Task AFolderTheSandboxCanReach_LeadsTheCard_AndIsKeptForTheChatOrItsProject()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            Loc.Load("en");
            var project = new Project { Name = "Site", WorkingDirectory = At("site"), Sandbox = { Enabled = true } };
            var chat = new Chat { ProjectId = project.Id };
            var other = new Chat { ProjectId = project.Id };
            var data = NewData(chat, other);
            data.Projects.Add(project);
            using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            vm.CurrentChat = chat;
            var remembered = 0;
            vm.ApprovalsRemembered += () => remembered++;
            var answers = new Decider();
            var sandbox = new SandboxAnswers();

            var write = Write(grant: new PermissionSandboxPathGrant { Access = PermissionSandboxPathGrantAccess.ReadWrite, Path = At("data") });
            var card = vm.PresentPermissionRequest(chat, "session", "write", EditPrompt(), false, answers.Decide,
                SandboxEscape.Of(write, EditPrompt()), sandbox.Decide)!;

            Assert.True(card.IsSandboxGrant);
            Assert.False(card.ShowAllowOnce);
            Assert.True(card.ShowAllowForChat);
            Assert.True(card.ShowAllowForProject);
            Assert.False(card.CanTurnOffSandbox);
            Assert.False(card.CanAllowInBulk);
            Assert.Equal(Loc.Get("Approval_SandboxGrantHintWrite", At("data")), card.RememberHint);

            await card.AllowForChatCommand.ExecuteAsync(null);
            Assert.Equal(SandboxAnswer.GrantPath, Assert.Single(sandbox.Calls));
            Assert.Empty(answers.Calls);
            var folder = Assert.Single(chat.SandboxFolders);
            Assert.Equal((At("data"), SandboxFolderAccess.ReadWrite), (folder.Path, folder.Access));
            Assert.Empty(chat.ApprovalRules);
            Assert.Empty(project.Sandbox.Folders);
            Assert.Equal(ApprovalOutcome.AllowedForChat, card.Outcome);
            Assert.Equal(0, remembered);
            Assert.True(vm.HasChatSandboxFolders);
            Assert.Equal(Loc.Get("Sandbox_ForgetOneFolder"), vm.ForgetChatSandboxFoldersLabel);

            var read = new PermissionRequestRead
            {
                Path = At("docs", "guide.md"),
                Intention = "Read the guide",
                RequestSandboxBypass = true,
                RequestSandboxBypassReason = "Outside the sandbox's readable paths.",
                SandboxPathGrant = new PermissionSandboxPathGrant { Access = PermissionSandboxPathGrantAccess.Read, Path = At("docs", "guide.md") }
            };
            var readPrompt = new PermissionPromptRequestRead { Path = At("docs", "guide.md"), Intention = "Read the guide" };
            var second = vm.PresentPermissionRequest(chat, "session", "read", readPrompt, false, answers.Decide,
                SandboxEscape.Of(read, readPrompt), sandbox.Decide)!;
            Assert.Equal(Loc.Get("Approval_SandboxGrantHintRead", At("docs", "guide.md")), second.RememberHint);

            await second.AllowForProjectCommand.ExecuteAsync(null);
            Assert.Equal([SandboxAnswer.GrantPath, SandboxAnswer.GrantPath], sandbox.Calls);
            Assert.Equal(SandboxFolderAccess.ReadOnly, Assert.Single(project.Sandbox.Folders).Access);
            Assert.Equal(1, remembered);
            // The project's other chats reach it too.
            Assert.Contains(At("docs", "guide.md"),
                Build(project, other, At("site"), [], null)!.UserPolicy!.Filesystem!.ReadonlyPaths!);

            vm.ForgetChatSandboxFoldersCommand.Execute(null);
            Assert.Empty(chat.SandboxFolders);
            Assert.False(vm.HasChatSandboxFolders);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task OtherEscapes_RunOnceOutsideOrTurnTheSandboxOffForTheChat()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            Loc.Load("en");
            var project = new Project { Name = "Site", WorkingDirectory = At("site"), Sandbox = { Enabled = true } };
            var chat = new Chat { ProjectId = project.Id };
            var data = NewData(chat);
            data.Projects.Add(project);
            using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            vm.CurrentChat = chat;
            var answers = new Decider();
            var sandbox = new SandboxAnswers();

            var detached = CommandPrompt(bypass: true, reason: "This command detaches, and the sandbox cannot enforce a detached process.");
            var once = vm.PresentPermissionRequest(chat, "session", "detached", detached, false, answers.Decide,
                sandboxDecide: sandbox.Decide)!;
            Assert.Equal(Loc.Get("Approval_SandboxRunOutside"), once.AllowLabel);
            Assert.Equal(detached.RequestSandboxBypassReason, once.Warning);
            Assert.True(once.ShowAllowOnce);
            Assert.False(once.ShowAllowForChat);
            Assert.False(once.ShowAllowForProject);
            Assert.True(once.CanTurnOffSandbox);

            await once.AllowOnceCommand.ExecuteAsync(null);
            Assert.True(Assert.IsType<PermissionDecisionApproveOnce>(Assert.Single(answers.Calls).Decision).ApprovedInteractively);
            Assert.Empty(sandbox.Calls);
            Assert.False(chat.SandboxDisabled);
            Assert.True(vm.IsSandboxOn);

            var retry = vm.PresentPermissionRequest(chat, "session", "retry", CommandPrompt(bypass: true, retry: true), false,
                answers.Decide, sandboxDecide: sandbox.Decide)!;
            Assert.Equal(Loc.Get("Approval_SandboxRetryAction"), retry.AllowLabel);
            await retry.TurnOffSandboxCommand.ExecuteAsync(null);
            Assert.Equal(SandboxAnswer.TurnOff, Assert.Single(sandbox.Calls));
            Assert.Single(answers.Calls);
            Assert.True(chat.SandboxDisabled);
            Assert.False(vm.IsSandboxOn);
            Assert.Equal(ApprovalOutcome.SandboxTurnedOff, retry.Outcome);
            Assert.Equal(Loc.Get("Approval_Receipt_SandboxOff"), retry.ReceiptText);

            var fetch = new PermissionPromptRequestUrl { Url = "https://example.test", Intention = "Fetch", RequestSandboxBypass = true };
            Assert.Equal(Loc.Get("Approval_SandboxAllowOutside"),
                vm.PresentPermissionRequest(chat, "session", "fetch", fetch, false, answers.Decide)!.AllowLabel);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AllowAll_LeavesEscapesToTheirOwnCards()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            Loc.Load("en");
            var chat = new Chat { ApprovalMode = ApprovalMode.AskFirst };
            using var vm = new ChatViewModel(new DataStore(NewData(chat)), TestCopilot.Shared);
            vm.CurrentChat = chat;
            var answers = new Decider();
            var first = vm.PresentPermissionRequest(chat, "session", "one", CommandPrompt(), false, answers.Decide)!;
            var escape = vm.PresentPermissionRequest(chat, "session", "escape", CommandPrompt(bypass: true), false, answers.Decide)!;
            Assert.False(vm.CanAllowAllPendingApprovals);

            var second = vm.PresentPermissionRequest(chat, "session", "two", EditPrompt(), false, answers.Decide)!;
            Assert.True(vm.CanAllowAllPendingApprovals);
            Assert.Equal(Loc.Get("Approval_AllowAll", 2), vm.AllowAllApprovalsLabel);

            await vm.AllowAllPendingApprovalsCommand.ExecuteAsync(null);
            Assert.Equal(2, answers.Calls.Count);
            Assert.Equal(ApprovalOutcome.Allowed, first.Outcome);
            Assert.Equal(ApprovalOutcome.Allowed, second.Outcome);
            Assert.True(escape.IsPending);
        }, CancellationToken.None);
    }

    // ── In the chat and on the project page ────────────

    [Fact]
    public async Task AChatCanTurnItsProjectsSandboxOff_AndTheApprovalPickerSaysSo()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            Loc.Load("en");
            var project = new Project { Name = "Site", WorkingDirectory = At("site") };
            var chat = new Chat { ProjectId = project.Id };
            var data = NewData(chat);
            data.Projects.Add(project);
            using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            vm.CurrentChat = chat;
            Assert.False(vm.HasProjectSandbox);
            Assert.False(vm.IsSandboxOn);
            vm.IsSandboxOn = true;
            Assert.False(chat.SandboxDisabled);

            project.Sandbox.Enabled = true;
            vm.NotifyPermissionPropertiesChanged();
            Assert.True(vm.HasProjectSandbox);
            Assert.True(vm.IsSandboxOn);
            Assert.Equal(Loc.Get("Sandbox_ChatOn"), vm.SandboxSummary);
            Assert.Equal(Loc.Get("ApprovalMode_ToolTipSandboxed", Loc.Get("ApprovalMode_FullAccess")), vm.ApprovalModeToolTip);
            var opened = new List<Guid>();
            vm.ManageSandboxRequested += opened.Add;
            vm.ManageSandboxCommand.Execute(null);
            Assert.Equal([project.Id], opened);

            vm.IsSandboxOn = false;
            Assert.True(chat.SandboxDisabled);
            Assert.Equal(Loc.Get("Sandbox_ChatOff"), vm.SandboxSummary);
            Assert.Equal(Loc.Get("ApprovalMode_ToolTip", Loc.Get("ApprovalMode_FullAccess")), vm.ApprovalModeToolTip);
            vm.IsSandboxOn = true;
            Assert.False(chat.SandboxDisabled);

            // A new chat's draft carries its choice until the chat exists.
            vm.CurrentChat = null;
            vm.ActiveProjectFilterId = project.Id;
            Assert.True(vm.IsSandboxOn);
            vm.IsSandboxOn = false;
            Assert.False(vm.IsSandboxOn);
            Assert.False(chat.SandboxDisabled);

            // A project that follows your default offers the switch once your default sandboxes projects.
            vm.CurrentChat = chat;
            project.Sandbox.Enabled = null;
            vm.NotifyPermissionPropertiesChanged();
            Assert.False(vm.HasProjectSandbox);
            data.Settings.Sandbox.Enabled = true;
            vm.NotifyPermissionPropertiesChanged();
            Assert.True(vm.HasProjectSandbox);
            Assert.True(vm.IsSandboxOn);
            Assert.Equal(Loc.Get("Sandbox_ChatOn"), vm.SandboxSummary);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AProjectsSandbox_SavesWithTheProject_AndFollowsWhatTheComputerCanDo()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            Loc.Load("en");
            var project = new Project { Name = "Site", WorkingDirectory = At("site") };
            var data = new AppData();
            data.Projects.Add(project);
            SandboxHostSupport? support = new() { Supported = true, Capabilities = [] };
            var vm = new ProjectsViewModel(new DataStore(data), sandboxSupport: _ => Task.FromResult<SandboxHostSupport?>(support));
            var changes = 0;
            vm.ApprovalPolicyChanged += () => changes++;
            vm.SelectedProject = project;
            SandboxChoice Choice(bool? value) => vm.SandboxChoices.Single(choice => choice.Value == value);

            // A project follows your default until it chooses: off on a new install.
            Assert.Null(vm.EditSandboxEnabled);
            Assert.True(Choice(null).IsSelected);
            Assert.False(vm.IsSandboxOn);
            Assert.Equal(Loc.Get("Projects_SandboxFollowsOff"), vm.SandboxChoiceHint);
            Assert.True(Choice(true).IsEnabled);

            Choice(true).SelectCommand.Execute(null);
            Assert.True(vm.EditSandboxEnabled);
            Assert.True(Choice(true).IsSelected);
            Assert.False(Choice(null).IsSelected);
            Assert.True(vm.IsSandboxOn);
            Assert.Equal(Loc.Get("Projects_SandboxOnHint"), vm.SandboxChoiceHint);
            Assert.Equal(Loc.Get("Projects_SandboxInternetFollowsOn"), vm.SandboxInternetDescription);
            vm.SandboxInternetChoices.Single(choice => choice.Value == false).SelectCommand.Execute(null);
            Assert.False(vm.EditSandboxAllowInternet);
            Assert.Equal(Loc.Get("Projects_SandboxInternetDescription"), vm.SandboxInternetDescription);
            vm.AddSandboxFolders([At("data"), At("data"), At("docs")]);
            Assert.Equal(2, vm.SandboxFolders.Count);
            vm.SandboxFolders[1].SetAccessCommand.Execute(SandboxFolderAccess.ReadOnly);
            Assert.True(vm.HasUnsavedChanges);
            Assert.Null(project.Sandbox.Enabled);

            vm.SaveProjectCommand.Execute(null);
            Assert.True(project.Sandbox.Enabled);
            Assert.False(project.Sandbox.AllowInternet);
            Assert.Equal(
                [(At("data"), SandboxFolderAccess.ReadWrite), (At("docs"), SandboxFolderAccess.ReadOnly)],
                project.Sandbox.Folders.Select(folder => (folder.Path, folder.Access)));
            Assert.Equal(1, changes);
            Assert.False(vm.HasUnsavedChanges);

            // A folder an approval card kept for the project shows up on the open page.
            project.Sandbox.Folders.Add(new SandboxFolder { Path = At("downloads") });
            vm.RefreshApprovals();
            Assert.Equal(3, vm.SandboxFolders.Count);
            Assert.False(vm.HasUnsavedChanges);
            vm.SandboxFolders[2].RemoveCommand.Execute(null);
            Assert.True(vm.HasUnsavedChanges);
            vm.DiscardChangesCommand.Execute(null);
            Assert.Equal(3, vm.SandboxFolders.Count);

            // Back to your default, which now sandboxes projects and gives them a folder of its own.
            data.Settings.Sandbox.Enabled = true;
            data.Settings.Sandbox.Folders.Add(new SandboxFolder { Path = At("shared"), Access = SandboxFolderAccess.ReadOnly });
            vm.RefreshApprovals();
            Choice(null).SelectCommand.Execute(null);
            Assert.True(vm.IsSandboxOn);
            Assert.Equal(Loc.Get("Projects_SandboxFollowsOn"), vm.SandboxChoiceHint);
            Assert.Equal(Loc.Get("Projects_SandboxDefaultFoldersOne"), vm.SandboxDefaultFoldersNote);
            vm.SaveProjectCommand.Execute(null);
            Assert.Null(project.Sandbox.Enabled);
            Assert.True(SandboxPolicy.IsOnFor(data.Settings, project));

            // A project without a folder has nothing to contain its commands in: your default passes it by.
            vm.EditWorkingDirectory = "";
            Assert.False(vm.IsSandboxOn);
            Assert.False(Choice(true).IsEnabled);
            Assert.True(vm.ShowSandboxNeedsFolder);
            vm.DiscardChangesCommand.Execute(null);

            // A computer that can't run the sandbox says why, and a feature it lacks is not offered.
            support = new SandboxHostSupport { Supported = false, Reason = "Needs Windows 11 25H2.", Capabilities = [] };
            var other = new Project { Name = "Other", WorkingDirectory = At("other") };
            data.Projects.Add(other);
            vm.RefreshFromStore();
            vm.SelectedProject = other;
            Assert.False(Choice(true).IsEnabled);
            Assert.Equal(Loc.Get("Projects_SandboxUnavailable", "Needs Windows 11 25H2."), vm.SandboxHost.UnavailableText);

            support = new SandboxHostSupport
            {
                Supported = true,
                Capabilities =
                [
                    new SandboxHostCapability { Name = SandboxPolicy.NetworkCapability, Supported = false, Reason = "Install slirp4netns." },
                    new SandboxHostCapability { Name = SandboxPolicy.BlockedFoldersCapability, Supported = false, Reason = "Update Windows." },
                ]
            };
            vm.SelectedProject = project;
            Assert.True(Choice(true).IsEnabled);
            Assert.False(vm.SandboxHost.CanReachInternet);
            Assert.All(vm.SandboxInternetChoices, choice => Assert.False(choice.IsEnabled));
            Assert.Equal(Loc.Get("Projects_SandboxInternetUnavailable", "Install slirp4netns."), vm.SandboxInternetDescription);
            Assert.False(vm.SandboxHost.CanBlockFolders);
            Assert.All(vm.SandboxFolders, row => Assert.False(row.CanBlock));
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SettingsSandbox_SetsTheDefaultForProjects_AndReachesLiveChatsAtOnce()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = new AppData();
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
                Assert.True(view.FindSettingsControl<ToggleSwitch>("SandboxDefaultToggle")!.IsEffectivelyVisible);
                Assert.False(vm.SandboxEnabled);
                Assert.True(vm.SandboxAllowInternet);
                Assert.Equal(Loc.Get("Permissions_SandboxDefaultDesc"), vm.SandboxDefaultDescription);

                // Like the default mode, a change is saved and reaches live chats at once.
                vm.SandboxEnabled = true;
                Assert.True(data.Settings.Sandbox.Enabled);
                Assert.True(vm.IsSandboxEnabledModified);
                Assert.Equal(1, changes);
                vm.RevertSandboxEnabledCommand.Execute(null);
                Assert.False(data.Settings.Sandbox.Enabled);
                Assert.False(vm.IsSandboxEnabledModified);
                vm.SandboxAllowInternet = false;
                Assert.False(data.Settings.Sandbox.AllowInternet);
                Assert.True(vm.IsSandboxAllowInternetModified);
                Assert.Equal(3, changes);

                // A folder picker returns "…\shared\": it is kept as "…\shared", so picking it again adds nothing.
                vm.AddSandboxFolders([At("shared") + Path.DirectorySeparatorChar, At("shared"), At("secrets")]);
                window.UpdateLayout();
                Assert.Equal(2, view.FindSettingsControl<ItemsControl>("SandboxFoldersList")!.ItemCount);
                Assert.Equal(Loc.Get("Projects_AddFolder"),
                    Avalonia.Automation.AutomationProperties.GetName(view.FindSettingsControl<Button>("AddSandboxFoldersButton")!));
                vm.SandboxFolders[1].SetAccessCommand.Execute(SandboxFolderAccess.Blocked);
                Assert.Equal(
                    [(At("shared"), SandboxFolderAccess.ReadWrite), (At("secrets"), SandboxFolderAccess.Blocked)],
                    data.Settings.Sandbox.Folders.Select(folder => (folder.Path, folder.Access)));
                vm.SandboxFolders[0].RemoveCommand.Execute(null);
                Assert.Equal(At("secrets"), Assert.Single(data.Settings.Sandbox.Folders).Path);
                Assert.Equal(6, changes);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ASandboxedProjectsChat_LeavesItsLocalConnectorsToTheRuntime_EvenWithTheConnectorProxyOn()
    {
        var project = new Project { Name = "Site", WorkingDirectory = At("site"), Sandbox = new ProjectSandbox { Enabled = true } };
        var chat = new Chat { ProjectId = project.Id };
        var data = new AppData
        {
            Settings = new UserSettings { UseMcpProxy = true },
            McpServers = [new Lumi.Models.McpServer { Name = "files", Command = "node" }],
            Chats = [chat]
        };
        data.Projects.Add(project);
        await using var proxy = new McpProxyRuntime();
        Assert.Same(proxy, McpSessionPlanner.SelectProxyRuntime(data.Settings, proxy));
        using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared);

        // The proxy would start the connector in Lumi's own process, where no sandbox reaches it.
        using var plan = vm.BuildMcpPlan(At("site"), CapabilitySnapshot.Empty, chat, activeAgent: null);

        Assert.IsType<McpStdioServerConfig>(plan.Servers["files"]);
        Assert.False(plan.UsesProxy);

        // So does a project your default sandboxes.
        var following = new Project { Name = "Docs", WorkingDirectory = At("docs") };
        var followingChat = new Chat { ProjectId = following.Id };
        data.Projects.Add(following);
        data.Chats.Add(followingChat);
        data.Settings.Sandbox.Enabled = true;
        using var followingPlan = vm.BuildMcpPlan(At("docs"), CapabilitySnapshot.Empty, followingChat, activeAgent: null);
        Assert.False(followingPlan.UsesProxy);
    }

    [Fact]
    public async Task TurningTheSandboxOn_CutsAWorkingChatOffItsProxiedConnectors_UntilItStartsAgain()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            var project = new Project { Name = "Site", WorkingDirectory = At("site") };
            var chat = new Chat { ProjectId = project.Id };
            var data = NewData(chat);
            data.Projects.Add(project);
            using var live = new LiveSession();
            using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared) { CurrentChat = chat };
            var released = false;
            var plan = new McpSessionPlan(new Dictionary<string, McpServerConfig>(), []);
            plan.AttachProxyLease(new McpProxySessionLease([new McpProxyRuntime.SessionRegistrationLease(
                () => released = true, new McpHttpServerConfig { Url = "http://127.0.0.1/mcp/files" })]));
            Field<Dictionary<Guid, McpSessionPlan>>(vm, "_sessionMcpPlans")[chat.Id] = plan;
            await StartTurnAsync(vm, chat, project, At("site"), live.Session);
            Field<Dictionary<CopilotSession, McpProxySessionLease>>(vm, "_mcpProxyLeasesBySession")[live.Session] = plan.DetachProxyLease()!;

            project.Sandbox.Enabled = true;
            Assert.True(await vm.ApplySandboxPolicyAsync(chat));

            // Its commands are sandboxed at once and its connectors, running in Lumi's process, are cut off;
            // the turn goes on and the session starts again, without the proxy, once it ends.
            Assert.Equal([true], live.SandboxUpdates);
            Assert.True(released);
            Assert.Same(live.Session, Field<Dictionary<Guid, CopilotSession>>(vm, "_sessionCache")[chat.Id]);
            Assert.Contains(chat.Id, Field<HashSet<Guid>>(vm, "_pendingSessionReconfigurations"));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AWorkingChat_MovedIntoASandboxedProject_TakesItsSandboxAtOnce()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            var project = new Project { Name = "Site", WorkingDirectory = At("site"), Sandbox = new ProjectSandbox { Enabled = true } };
            var chat = new Chat();
            var data = NewData(chat);
            data.Projects.Add(project);
            using var live = new LiveSession();
            using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared) { CurrentChat = chat };
            await StartTurnAsync(vm, chat, null, At("home"), live.Session);

            vm.SetProjectId(project.Id);

            // The turn finishes in its session, which takes the project's sandbox now rather than next turn.
            await live.SandboxUpdated.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal([true], live.SandboxUpdates);
            Assert.True(vm.IsSandboxOn);
            Assert.Same(live.Session, Field<Dictionary<Guid, CopilotSession>>(vm, "_sessionCache")[chat.Id]);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AFolderACardKeepsForTheProject_JoinsUnsavedEditsOnItsPage_AndIsSaved()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            Loc.Load("en");
            var project = new Project
            {
                Name = "Site",
                WorkingDirectory = At("site"),
                Sandbox = new ProjectSandbox
                {
                    Enabled = true,
                    Folders =
                    [
                        new SandboxFolder { Path = At("docs"), Access = SandboxFolderAccess.ReadOnly },
                        new SandboxFolder { Path = At("cache"), Access = SandboxFolderAccess.ReadOnly },
                        new SandboxFolder { Path = At("cache", "logs"), Access = SandboxFolderAccess.ReadOnly },
                    ]
                }
            };
            var data = new AppData();
            data.Projects.Add(project);
            var vm = new ProjectsViewModel(new DataStore(data));
            vm.SelectedProject = project;

            // Unsaved edits elsewhere on the page don't hold a card's folder back.
            vm.EditInstructions = "Keep it short.";
            SandboxPolicy.Remember(project.Sandbox.Folders, new SandboxPathGrant(At("downloads"), CanWrite: true, []));
            vm.RefreshApprovals();
            Assert.Contains(vm.SandboxFolders, row => row.Path == At("downloads") && row.IsReadWrite);
            Assert.Equal("Keep it short.", vm.EditInstructions);

            // Nor do unsaved sandbox edits, which win wherever they touch the same folder.
            vm.SandboxFolders.Single(row => row.Path == At("docs")).SetAccessCommand.Execute(SandboxFolderAccess.Blocked);
            SandboxPolicy.Remember(project.Sandbox.Folders, new SandboxPathGrant(At("docs"), CanWrite: true, []));
            SandboxPolicy.Remember(project.Sandbox.Folders, new SandboxPathGrant(At("cache"), CanWrite: true, [At("cache", "logs")]));
            SandboxPolicy.Remember(project.Sandbox.Folders, new SandboxPathGrant(At("out"), CanWrite: false, []));
            vm.RefreshApprovals();
            Assert.True(vm.HasUnsavedChanges);

            vm.SaveProjectCommand.Execute(null);
            Assert.Equal("Keep it short.", project.Instructions);
            Assert.Equal(
                [
                    (At("docs"), SandboxFolderAccess.Blocked),
                    (At("cache"), SandboxFolderAccess.ReadWrite),
                    (At("downloads"), SandboxFolderAccess.ReadWrite),
                    (At("out"), SandboxFolderAccess.ReadOnly),
                ],
                project.Sandbox.Folders.Select(folder => (folder.Path, folder.Access)));
            Assert.False(vm.HasUnsavedChanges);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    // ── Helpers ────────────────────────────────────────

    /// <summary>Records the native decisions a card sends, standing in for the session's pending-request RPC.</summary>
    private sealed class Decider
    {
        public List<(PermissionDecision Decision, PermissionDecisionContext Context)> Calls { get; } = [];

        public NativeApprovalDecider Decide => (decision, context, _) =>
        {
            Calls.Add((decision, context));
            return Task.FromResult(true);
        };
    }

    /// <summary>Records what a card settled in the sandbox itself, standing in for the session's sandbox RPCs.</summary>
    private sealed class SandboxAnswers
    {
        public List<SandboxAnswer> Calls { get; } = [];

        public NativeSandboxDecider Decide => (answer, _, _) =>
        {
            Calls.Add(answer);
            return Task.FromResult(true);
        };
    }

    private static AppData NewData(params Chat[] chats) => new()
    {
        Settings = new UserSettings { AutoGenerateTitles = false, SuggestFollowUps = false },
        Chats = chats.ToList()
    };

    private static T Field<T>(ChatViewModel vm, string name)
        => (T)typeof(ChatViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;

    /// <summary>Leaves the chat mid-turn in a live session that started with its sandbox, as a send does.</summary>
    private static async Task StartTurnAsync(ChatViewModel vm, Chat chat, Project? project, string workingDirectory, CopilotSession session)
    {
        chat.CopilotSessionId = session.SessionId;
        Field<Dictionary<Guid, CopilotSession>>(vm, "_sessionCache")[chat.Id] = session;
        Field<Dictionary<Guid, CancellationTokenSource>>(vm, "_ctsSources")[chat.Id] = new CancellationTokenSource();
        await (Task)typeof(ChatViewModel).GetMethod("PrepareSessionSandboxAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [chat, project, workingDirectory, (IReadOnlyList<string>)[], CancellationToken.None])!;
    }

    /// <summary>A session whose runtime runs in memory and takes the approval and sandbox updates a chat sends it.</summary>
    private sealed class LiveSession : IDisposable
    {
        private readonly JsonRpc _runtime;
        private readonly IDisposable _sdkRpc;

        public LiveSession()
        {
            var requests = new Pipe();
            var responses = new Pipe();
            _runtime = new JsonRpc(new HeaderDelimitedMessageHandler(
                responses.Writer.AsStream(), requests.Reader.AsStream(), new JsonMessageFormatter()));
            _runtime.AddLocalRpcTarget(this);
            _runtime.StartListening();

            var rpcType = typeof(CopilotSession).Assembly.GetType("GitHub.Copilot.JsonRpc", throwOnError: true)!;
            _sdkRpc = (IDisposable)Activator.CreateInstance(rpcType, requests.Writer.AsStream(), responses.Reader.AsStream(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() },
                NullLogger.Instance)!;
            rpcType.GetMethod("StartListening")!.Invoke(_sdkRpc, null);
            Session = (CopilotSession)Activator.CreateInstance(typeof(CopilotSession), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [Guid.NewGuid().ToString(), _sdkRpc, NullLogger.Instance, null, null], culture: null)!;
            GC.SuppressFinalize(Session);
        }

        public CopilotSession Session { get; }

        /// <summary>Whether each sandbox the session was sent is on.</summary>
        public List<bool> SandboxUpdates { get; } = [];

        public TaskCompletionSource SandboxUpdated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        [JsonRpcMethod("session.options.update", UseSingleObjectParameterDeserialization = true)]
        public object UpdateOptions(JObject request)
        {
            SandboxUpdates.Add(request.SelectToken("..sandboxConfig.enabled")?.Value<bool>() == true);
            SandboxUpdated.TrySetResult();
            return new { success = true };
        }

        [JsonRpcMethod("session.permissions.setMode", UseSingleObjectParameterDeserialization = true)]
        public object SetMode(JObject request) => new { success = true, mode = request["mode"] };

        [JsonRpcMethod("session.permissions.resetSessionApprovals", UseSingleObjectParameterDeserialization = true)]
        public object ResetApprovals(JObject request) => new { success = true };

        [JsonRpcMethod("session.permissions.configure", UseSingleObjectParameterDeserialization = true)]
        public object ConfigurePermissions(JObject request) => new { success = true };

        [JsonRpcMethod("session.destroy", UseSingleObjectParameterDeserialization = true)]
        public object Destroy(JObject request) => new { };

        public void Dispose()
        {
            _sdkRpc.Dispose();
            _runtime.Dispose();
        }
    }

    private static PermissionPromptRequestCommands CommandPrompt(bool bypass = false, bool retry = false, string? reason = "The sandbox blocked this command.") => new()
    {
        FullCommandText = "git push",
        Intention = "Publish the branch",
        CommandIdentifiers = ["git push"],
        CanOfferSessionApproval = !bypass,
        RequestSandboxBypass = bypass,
        RequestSandboxPermissive = retry,
        RequestSandboxBypassReason = bypass ? reason : null
    };

    private static PermissionPromptRequestWrite EditPrompt() => new()
    {
        FileName = At("data", "new.txt"),
        Intention = "Write notes",
        Diff = "+hello",
        CanOfferSessionApproval = true
    };

    private static PermissionRequestWrite Write(PermissionSandboxPathGrant? grant) => new()
    {
        FileName = At("data", "new.txt"),
        CanOfferSessionApproval = false,
        Diff = "+hello",
        Intention = "Write notes",
        RequestSandboxBypass = true,
        RequestSandboxBypassReason = At("data", "new.txt") + " is outside the sandbox's writable paths.",
        SandboxPathGrant = grant
    };
}
