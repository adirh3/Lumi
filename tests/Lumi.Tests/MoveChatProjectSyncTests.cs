using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Regression coverage for moving a chat between projects from the sidebar while that chat is the
/// live/active surface. Moving must propagate to the open surface: the composer project chip updates,
/// and an established Copilot session is resumed with the new system prompt and working directory
/// without losing native history or replaying the transcript.
/// </summary>
[Collection("Headless UI")]
public sealed class MoveChatProjectSyncTests
{
    [Fact]
    public async Task AssignChatToProject_ForActiveChat_UpdatesComposerProjectChip()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var work = new Project { Name = "Work" };
            var personal = new Project { Name = "Personal" };
            var chat = new Chat { Title = "Active chat" };
            var viewModel = CreateViewModel([work, personal], chat);
            viewModel.ChatVM.CurrentChat = chat;

            viewModel.AssignChatToProjectCommand.Execute(new object[] { chat, work });

            Assert.Equal(work.Id, chat.ProjectId);
            Assert.Equal("Work", viewModel.ChatVM.ProjectBadgeText);
            Assert.Equal("Work", viewModel.ChatVM.SelectedProjectName);

            viewModel.Dispose();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RemoveChatFromProject_ForActiveChat_ClearsComposerProjectChip()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var personal = new Project { Name = "Personal" };
            var chat = new Chat { Title = "Active chat" };
            var viewModel = CreateViewModel([personal], chat);
            viewModel.ChatVM.CurrentChat = chat;

            // Move into Personal so the chip is established, then move to "All projects" (no project).
            viewModel.AssignChatToProjectCommand.Execute(new object[] { chat, personal });
            Assert.Equal("Personal", viewModel.ChatVM.ProjectBadgeText);

            viewModel.RemoveChatFromProjectCommand.Execute(chat);

            Assert.Null(chat.ProjectId);
            Assert.Null(viewModel.ChatVM.ProjectBadgeText);
            Assert.Null(viewModel.ChatVM.SelectedProjectName);

            viewModel.Dispose();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AssignChatToProject_ForActiveChatWithSession_PreservesSessionForContextRefresh()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var work = new Project
            {
                Name = "Work",
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            var personal = new Project { Name = "Personal" };
            var chat = new Chat
            {
                Title = "Active chat",
                ProjectId = personal.Id,
                CopilotSessionId = "session-abc"
            };
            var viewModel = CreateViewModel([work, personal], chat);
            viewModel.ChatVM.CurrentChat = chat;

            viewModel.AssignChatToProjectCommand.Execute(new object[] { chat, work });

            Assert.Equal(work.Id, chat.ProjectId);
            Assert.Equal("session-abc", chat.CopilotSessionId);
            Assert.DoesNotContain(
                chat.Id,
                GetPrivateField<HashSet<Guid>>(viewModel.ChatVM, "_pendingSessionInvalidations"));

            viewModel.Dispose();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ComposerProjectChanges_PreserveExistingSessionId()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var work = new Project { Name = "Work" };
            var personal = new Project { Name = "Personal" };
            var chat = new Chat
            {
                Title = "Active chat",
                ProjectId = personal.Id,
                CopilotSessionId = "session-composer"
            };
            var viewModel = CreateViewModel([work, personal], chat);
            viewModel.ChatVM.CurrentChat = chat;

            viewModel.ChatVM.SetProjectId(work.Id);
            Assert.Equal("session-composer", chat.CopilotSessionId);

            viewModel.ChatVM.ClearProjectId();
            Assert.Equal("session-composer", chat.CopilotSessionId);
            Assert.DoesNotContain(
                chat.Id,
                GetPrivateField<HashSet<Guid>>(viewModel.ChatVM, "_pendingSessionInvalidations"));

            viewModel.Dispose();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AssignChatToProject_ForInactiveChat_LeavesActiveSurfaceUntouched()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var work = new Project { Name = "Work" };
            var activeChat = new Chat { Title = "Active chat" };
            var otherChat = new Chat { Title = "Other chat", CopilotSessionId = "keep-me" };
            var viewModel = CreateViewModel([work], activeChat, otherChat);
            viewModel.ChatVM.CurrentChat = activeChat;

            viewModel.AssignChatToProjectCommand.Execute(new object[] { otherChat, work });

            Assert.Equal(work.Id, otherChat.ProjectId);
            // The visible surface shows a different chat, so its chip must not change and the moved
            // chat's (background) session must not be disturbed.
            Assert.Null(viewModel.ChatVM.ProjectBadgeText);
            Assert.Equal("keep-me", otherChat.CopilotSessionId);

            viewModel.Dispose();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AssignChatToProject_ForActiveBusyChat_DefersSameSessionReconfiguration()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var work = new Project { Name = "Work" };
            var personal = new Project { Name = "Personal" };
            var chat = new Chat
            {
                Title = "Busy chat",
                ProjectId = personal.Id,
                CopilotSessionId = "session-live"
            };
            var viewModel = CreateViewModel([work, personal], chat);
            var chatVm = viewModel.ChatVM;
            chatVm.CurrentChat = chat;

            // Simulate an in-flight turn for this chat.
            var runtimeStates = GetPrivateField<Dictionary<Guid, ChatRuntimeState>>(chatVm, "_runtimeStates");
            var runtime = new ChatRuntimeState { Chat = chat };
            runtime.IsBusy = true;
            runtimeStates[chat.Id] = runtime;

            viewModel.AssignChatToProjectCommand.Execute(new object[] { chat, work });

            Assert.Equal(work.Id, chat.ProjectId);
            // The in-flight turn must not be torn down. The next send resumes the same session with
            // the new project configuration instead of replaying the transcript into a replacement.
            Assert.Equal("session-live", chat.CopilotSessionId);
            Assert.Contains(
                chat.Id,
                GetPrivateField<HashSet<Guid>>(chatVm, "_pendingSessionReconfigurations"));
            Assert.DoesNotContain(
                chat.Id,
                GetPrivateField<HashSet<Guid>>(chatVm, "_pendingSessionInvalidations"));
            // The composer chip still updates immediately even while busy.
            Assert.Equal("Work", chatVm.ProjectBadgeText);
            Assert.Equal("Work", chatVm.SelectedProjectName);

            viewModel.Dispose();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SidebarProjectCommands_DoNotMutateAReservedFirstTurn()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var original = new Project { Name = "Original" };
            var other = new Project { Name = "Other" };
            var chat = new Chat { Title = "Empty", ProjectId = original.Id };
            var viewModel = CreateViewModel([original, other], chat);
            viewModel.ChatVM.CurrentChat = chat;
            using var reservation = viewModel.ChatVM.TryReserveExternalSend(chat.Id);
            Assert.NotNull(reservation);

            viewModel.AssignChatToProjectCommand.Execute(new object[] { chat, other });
            viewModel.RemoveChatFromProjectCommand.Execute(chat);

            Assert.Equal(original.Id, chat.ProjectId);
            viewModel.Dispose();
        }, CancellationToken.None);
    }

    private static MainViewModel CreateViewModel(Project[] projects, params Chat[] chats)
    {
        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            },
            Projects = [.. projects],
            Chats = [.. chats]
        };

        return new MainViewModel(new DataStore(data), TestCopilot.Shared, new UpdateService());
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found.");
        return (T)field.GetValue(target)!;
    }
}
