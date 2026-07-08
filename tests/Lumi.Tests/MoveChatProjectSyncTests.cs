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
/// and an established Copilot session is dropped so the next turn rebuilds the system prompt and
/// working directory from the new project (otherwise the model keeps answering with the old project).
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
    public async Task AssignChatToProject_ForActiveChatWithSession_DropsSessionSoNextTurnRebuildsContext()
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
            // The live session is discarded so EnsureSessionAsync rebuilds the system prompt AND the
            // working directory from the new project on the next send.
            Assert.Null(chat.CopilotSessionId);

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
    public async Task AssignChatToProject_ForActiveBusyChat_DefersRebuildInsteadOfAbortingTurn()
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
            // The in-flight turn must NOT be torn down: session preserved, rebuild deferred to next send.
            Assert.Equal("session-live", chat.CopilotSessionId);
            var pending = GetPrivateField<HashSet<Guid>>(chatVm, "_pendingSessionInvalidations");
            Assert.Contains(chat.Id, pending);
            // The composer chip still updates immediately even while busy.
            Assert.Equal("Work", chatVm.ProjectBadgeText);
            Assert.Equal("Work", chatVm.SelectedProjectName);

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

        return new MainViewModel(new DataStore(data), new CopilotService(), new UpdateService());
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found.");
        return (T)field.GetValue(target)!;
    }
}
