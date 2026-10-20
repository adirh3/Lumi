using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using StrataTheme.Controls;

using ChatMessage = Lumi.Models.ChatMessage;

namespace Lumi.ViewModels;

/// <summary>
/// Bringing other chats into a message. The composer's chat chips are draft state like the pending
/// reply: kept per chat, handed to the message the next send creates, restored when that message is
/// edited. What the chats hold reaches the model with that message — their transcripts are read when it
/// is sent — so it works whatever tools the chat's agent allows.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>Most chats offered at once, most recently active first.</summary>
    private const int MaxOfferedChats = 500;

    /// <summary>Recent messages read from each referenced chat.</summary>
    private const int ReferencedChatMessages = 40;

    /// <summary>
    /// Transcript characters read for one message in all. A chat past the budget is named with its id
    /// instead, so the model can still read it on request without one message swelling the context.
    /// </summary>
    private const int MaxReferencedChatsContextChars = 60_000;

    // Chips of chats the user switched away from, restored when they come back. Guid.Empty holds the
    // new-chat draft, as it does for the draft text.
    private readonly Dictionary<Guid, List<StrataComposerChip>> _chatReferenceDrafts = new();

    /// <summary>Chats the composer offers to bring in (its Chats picker and # list), most recent first.</summary>
    public ObservableCollection<StrataComposerChip> AvailableChatChips { get; } = [];

    /// <summary>Chats brought into the next message. The composer adds and removes these chips itself.</summary>
    public ObservableCollection<object> ReferencedChatChips { get; } = [];

    /// <summary>Raised to open the Skills page, from the composer's Skills picker.</summary>
    public event Action? SkillsPageRequested;

    [RelayCommand]
    private void ManageSkills() => SkillsPageRequested?.Invoke();

    /// <summary>
    /// Rebuilds the offered chats from the store, just before the composer shows them, so titles and
    /// times are current without following every chat. Leaves the list untouched when nothing changed.
    /// </summary>
    [RelayCommand]
    private void RefreshChatReferences()
    {
        var chips = BuildChatReferenceChips();
        if (!chips.SequenceEqual(AvailableChatChips))
            ReplaceCollection(AvailableChatChips, chips);
    }

    private List<StrataComposerChip> BuildChatReferenceChips()
    {
        var currentId = CurrentChat?.Id;
        var projectNames = new Dictionary<Guid, string>();
        foreach (var project in _dataStore.Data.Projects)
            projectNames.TryAdd(project.Id, project.Name);

        return _dataStore.Data.Chats
            .Where(chat => chat.Id != currentId)
            .OrderByDescending(static chat => chat.UpdatedAt)
            .Take(MaxOfferedChats)
            .Select(chat => CreateChatReferenceChip(
                chat,
                chat.ProjectId is { } projectId && projectNames.TryGetValue(projectId, out var projectName)
                    ? projectName
                    : null))
            .ToList();
    }

    private static StrataComposerChip CreateChatReferenceChip(Chat chat, string? projectName)
        => new(
            DisplayTitle(chat),
            "💬",
            SecondaryText: string.IsNullOrWhiteSpace(projectName) ? null : projectName,
            Value: chat.Id.ToString(),
            SourceLabel: LibraryViewModel.FormatRelativeTime(chat.UpdatedAt));

    private static string DisplayTitle(Chat chat)
        => string.IsNullOrWhiteSpace(chat.Title) ? Loc.Sidebar_NewChat : chat.Title.Trim();

    private static Guid? ChipChatId(StrataComposerChip chip)
        => Guid.TryParse(chip.Value, out var id) ? id : null;

    /// <summary>The composer's chats as references, without taking them.</summary>
    private List<ChatReference>? PeekReferencedChats()
    {
        var references = ReferencedChatChips
            .OfType<StrataComposerChip>()
            .Select(chip => ChipChatId(chip) is { } id ? new ChatReference { ChatId = id, Title = chip.Name } : null)
            .OfType<ChatReference>()
            .DistinctBy(static reference => reference.ChatId)
            .ToList();
        return references.Count > 0 ? references : null;
    }

    /// <summary>
    /// Hands the composer's chats to the message being created from it. They belong to exactly one
    /// message, so they are cleared here.
    /// </summary>
    private List<ChatReference>? TakeReferencedChats()
    {
        var references = PeekReferencedChats();
        ReferencedChatChips.Clear();
        return references;
    }

    /// <summary>As <see cref="TakeReferencedChats()"/>, for a send aimed at <paramref name="chatId"/> only.</summary>
    private List<ChatReference>? TakeReferencedChats(Guid chatId)
        => CurrentChat?.Id == chatId ? TakeReferencedChats() : null;

    /// <summary>Shows <paramref name="references"/> as the composer's chats, as editing a message does.</summary>
    private void ReplaceReferencedChats(IEnumerable<ChatReference>? references)
    {
        ReferencedChatChips.Clear();
        if (references is null)
            return;

        foreach (var reference in references)
        {
            var chat = _dataStore.Data.Chats.FirstOrDefault(candidate => candidate.Id == reference.ChatId);
            var projectName = chat?.ProjectId is { } projectId
                ? _dataStore.Data.Projects.FirstOrDefault(project => project.Id == projectId)?.Name
                : null;
            ReferencedChatChips.Add(chat is not null
                ? CreateChatReferenceChip(chat, projectName)
                : new StrataComposerChip(reference.Title, "💬", Value: reference.ChatId.ToString()));
        }
    }

    /// <summary>Keeps each chat's composer chats with it while the user moves between chats.</summary>
    private void SwapChatReferenceDrafts(Chat? leaving, Chat? arriving)
    {
        var leavingKey = leaving?.Id ?? Guid.Empty;
        if (ReferencedChatChips.Count > 0)
            _chatReferenceDrafts[leavingKey] = ReferencedChatChips.OfType<StrataComposerChip>().ToList();
        else
            _chatReferenceDrafts.Remove(leavingKey);

        ReferencedChatChips.Clear();
        if (_chatReferenceDrafts.Remove(arriving?.Id ?? Guid.Empty, out var restored))
        {
            foreach (var chip in restored)
                ReferencedChatChips.Add(chip);
        }
    }

    /// <summary>
    /// What the chats <paramref name="message"/> brings in hold, for the model: each chat's recent
    /// transcript as <c>read_chat</c> shows it, under a short lead-in. Empty when it brings in none.
    /// </summary>
    private async Task<string> BuildReferencedChatsContextAsync(
        ChatMessage? message,
        CancellationToken cancellationToken)
    {
        if (message?.ReferencedChats is not { Count: > 0 } references)
            return "";

        var chats = new List<ReferencedChatContent>(references.Count);
        var used = 0;
        foreach (var reference in references)
        {
            if (!_dataStore.Data.Chats.Any(chat => chat.Id == reference.ChatId))
            {
                chats.Add(new ReferencedChatContent(reference, null, Exists: false));
                continue;
            }

            string? transcript = null;
            if (used < MaxReferencedChatsContextChars)
            {
                try
                {
                    transcript = await ChatHistory.ReadChatAsync(
                        reference.ChatId.ToString(),
                        ReferencedChatMessages,
                        includeReasoning: false,
                        includeToolCalls: false,
                        cancellationToken: cancellationToken);
                    used += transcript.Length;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ChatReferences] Reading chat {reference.ChatId} failed: {ex.Message}");
                }
            }

            chats.Add(new ReferencedChatContent(reference, transcript, Exists: true));
        }

        return ChatReferenceFormatter.BuildPromptContext(chats);
    }
}
