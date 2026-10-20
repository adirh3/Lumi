using Lumi.Models;

namespace Lumi.Services;

internal static class InteractionHistory
{
    public const string InterruptedReceiptError = "The session ended before a reply was confirmed. The submitted inputs are kept.";

    public static bool SettleInterrupted(Chat chat)
    {
        var changed = false;
        foreach (var message in chat.Messages)
        {
            if (message.InteractionReceipt is { Status: "Submitted" or "Working" } receipt)
            {
                receipt.Status = "Stopped";
                receipt.Error = InterruptedReceiptError;
                changed = true;
            }
            if (message.ToolName == "ask_input" && message.ToolStatus == "InProgress" && message.InputAnswer is null && !message.InputCanceled)
            {
                message.ToolStatus = "Stopped";
                message.MarkToolFinished(DateTimeOffset.UtcNow);
                changed = true;
            }
        }
        return changed;
    }
}
