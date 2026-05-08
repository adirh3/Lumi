#if DEBUG
using System.Linq;
using Lumi.Models;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    public void LoadDebugTranscriptFixture()
    {
        ClearChat();

        var fixture = DebugAgentHarness.CreateTranscriptFixtureChat(_dataStore);

        BrowserHideRequested?.Invoke();
        DiffHideRequested?.Invoke();
        PlanHideRequested?.Invoke();
        ClearSuggestions();

        _pendingSkillInjections.Clear();
        _activeExternalSkillNames.Clear();
        _transcriptBuilder.PendingFetchedSkillRefs.Clear();

        IsBusy = false;
        IsStreaming = false;
        HasUsedBrowser = false;
        IsBrowserOpen = false;
        StatusText = "Debug transcript fixture";
        TotalInputTokens = fixture.TotalInputTokens;
        TotalOutputTokens = fixture.TotalOutputTokens;
        fixture.ContextCurrentTokens = fixture.TotalInputTokens + fixture.TotalOutputTokens;
        fixture.ContextTokenLimit = 128000;
        ContextCurrentTokens = fixture.ContextCurrentTokens;
        ContextTokenLimit = fixture.ContextTokenLimit;
        HasPlan = true;
        PlanContent = fixture.PlanContent;
        ActiveAgent = null;
        SelectedSdkAgentName = null;
        SelectedAgentName = null;
        SelectedAgentGlyph = "D";

        ActiveSkillIds.Clear();
        ActiveSkillChips.Clear();
        ActiveMcpServerNames.Clear();
        ActiveMcpChips.Clear();
        PendingAttachments.Clear();
        PendingAttachmentItems.Clear();

        _isBulkLoadingMessages = true;
        try
        {
            Messages.Clear();
            foreach (var msg in fixture.Messages)
                Messages.Add(new ChatMessageViewModel(msg));

            CurrentChat = fixture;
            PromptText = "";
            RebuildTranscript();
            _transcriptBuilder.AppendPlanCardToLastTurn("Debug plan", () => PlanShowRequested?.Invoke());
        }
        finally
        {
            _isBulkLoadingMessages = false;
        }

        if (fixture.Messages.SelectMany(m => m.ActiveSkills).FirstOrDefault() is { } skill)
        {
            ActiveSkillChips.Add(new StrataTheme.Controls.StrataComposerChip(skill.Name, skill.Glyph));
        }

        RefreshProjectBadge();
        RefreshAgentBadge();
        RefreshComposerCatalogs();
        ScrollToEndRequested?.Invoke();
    }
}
#endif
