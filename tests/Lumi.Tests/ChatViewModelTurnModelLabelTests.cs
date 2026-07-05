using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class ChatViewModelTurnModelLabelTests
{
    [Fact]
    public void TurnStartEcho_ResetsModelSoNextTurnCapturesCurrentSelection()
    {
        var previousTurnModelId = ChatViewModel.CaptureTurnModelId(null, "gpt-4o");

        var resetTurnModelId = ChatViewModel.ResetTurnModelIdForTurnStartEcho(
            isTurnStartEcho: true,
            previousTurnModelId);
        var nextTurnModelId = ChatViewModel.CaptureTurnModelId(resetTurnModelId, "gpt-4o-mini");

        Assert.Equal("gpt-4o-mini", nextTurnModelId);
    }

    [Fact]
    public void AgenticSubTurn_KeepsFirstModelCapturedForUserTurn()
    {
        var firstTurnModelId = ChatViewModel.CaptureTurnModelId(null, "gpt-4o");

        var subTurnModelId = ChatViewModel.CaptureTurnModelId(firstTurnModelId, "gpt-4o-mini");

        Assert.Equal("gpt-4o", subTurnModelId);
    }

    [Fact]
    public void NonTurnStartUserEcho_DoesNotResetCapturedModel()
    {
        var capturedModelId = ChatViewModel.CaptureTurnModelId(null, "gpt-4o");

        var preservedModelId = ChatViewModel.ResetTurnModelIdForTurnStartEcho(
            isTurnStartEcho: false,
            capturedModelId);

        Assert.Equal("gpt-4o", preservedModelId);
    }
}