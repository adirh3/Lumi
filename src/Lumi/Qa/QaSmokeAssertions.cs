using Lumi.Models;

namespace Lumi.Qa;

internal static class QaSmokeAssertions
{
    internal const int SwitchableAiFeatureCount = 4;

    internal static bool AiFeaturesMatch(UserSettings settings, bool enabled) =>
        settings.AutoGenerateTitles == enabled
        && settings.SuggestFollowUps == enabled
        && settings.EnableMemoryAutoSave == enabled
        && settings.EnableDailyBriefs == enabled;

    internal static bool RepliesAreStored(IReadOnlyList<ChatMessage> messages, IReadOnlyList<string> expected) =>
        expected.Count > 0 && expected.All(text => messages.Any(message =>
            message.Role == "assistant" && !message.IsStreaming
            && string.Equals(message.Content, text, StringComparison.Ordinal)));
}
