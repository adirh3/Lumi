using Lumi.Localization;
using Lumi.Models;

namespace Lumi.Services;

internal static class InteractionHistory
{
    public static string InterruptedReceiptError => Loc.Interaction_Interrupted;

    public static bool SettleInterrupted(Chat chat) => SettleInterrupted(chat.Messages);

    public static bool SettleInterrupted(IEnumerable<ChatMessage> messages)
    {
        var changed = false;
        foreach (var message in messages)
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
