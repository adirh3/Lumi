#if DEBUG
using System.Diagnostics;
using Lumi.Models;
using Microsoft.Copilot;

namespace Lumi.ViewModels;

public partial class MainViewModel
{
    private async Task OpenAttentionFixtureAsync()
    {
        try
        {
            _dataStore.Data.Settings.AutoSaveChats = false;
            _dataStore.Data.Settings.NotificationsEnabled = false;
            var project = new Project { Name = "Needs you preview" };
            _dataStore.Data.Projects.Add(project);
            var question = new Chat { Title = "Cross-project questions", ProjectId = project.Id };
            question.Messages.Add(new() { Role = "user", Content = "Help me keep track of questions while several chats are working." });
            question.Messages.Add(new()
            {
                Role = "assistant",
                Content = "The pending requests already have live owners. I can put a red banner above Unread chats and keep answers connected to their original tasks. One scope decision remains before I continue."
            });
            var form = new Chat { Title = "Weekend plans" };
            form.Messages.Add(new() { Role = "user", Content = "Find a quiet weekend stay with a good breakfast. Do not book anything." });
            form.Messages.Add(new() { Role = "assistant", Content = "I need your destination and budget before narrowing the options. Providing these details will not authorize a booking." });
            var approval = new Chat { Title = "Review changes", ProjectId = project.Id, ApprovalMode = ApprovalMode.AskFirst };
            approval.Messages.Add(new() { Role = "user", Content = "Review the changes in my repository." });
            approval.Messages.Add(new() { Role = "assistant", Content = "I would like to check the working tree before reviewing the diff. This is a synthetic approval; no command will actually run." });
            _dataStore.Data.Chats.AddRange([question, form, approval]);
            var questionOwner = await _chatSessionStore.AcquireChatAsync(question);
            var formOwner = await _chatSessionStore.AcquireChatAsync(form);
            var approvalOwner = await _chatSessionStore.AcquireChatAsync(approval);
            _ = FinishAttentionFixtureAsync(questionOwner, questionOwner.RunAttentionQuestionFixtureAsync(question));
            _ = FinishAttentionFixtureAsync(formOwner, formOwner.AskInputAsync(form.Id, "form",
                """{"id":"weekend","title":"Where should I look, and what is your budget?","fields":[{"id":"city","label":"Destination","required":true},{"id":"budget","label":"Nightly budget","type":"number","minimum":1,"required":true,"unit":"USD"}]}""",
                CancellationToken.None));
            approvalOwner.PresentPermissionRequest(approval, "preview", "preview-approval",
                new PermissionPromptRequestCommands
                {
                    FullCommandText = "git status --short", Intention = "Check the working tree",
                    CommandIdentifiers = ["git status"], CanOfferSessionApproval = true
                }, false, (_, _, _) => Task.FromResult(true));
            _chatSessionStore.Release(approvalOwner);
            question.HasUnreadMessages = true;
            RefreshChatList();
            SelectedNavIndex = 0;
        }
        catch (Exception error)
        {
            Trace.TraceError("[NeedsYou fixture] {0}", error);
            ChatVM.StatusText = error.Message;
        }
    }

    private async Task FinishAttentionFixtureAsync(ChatViewModel owner, Task pending)
    {
        try { await pending; }
        catch (OperationCanceledException) { }
        catch (Exception error) { Trace.TraceError("[NeedsYou fixture] {0}", error); ChatVM.StatusText = error.Message; }
        finally { _chatSessionStore.Release(owner); }
    }
}
#endif
