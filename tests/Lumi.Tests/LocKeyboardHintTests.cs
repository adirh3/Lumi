using Lumi.Localization;
using Xunit;

namespace Lumi.Tests;

public sealed class LocKeyboardHintTests
{
    [Theory]
    [InlineData("Search (Ctrl+K)", "Search (⌘K)")]
    [InlineData("New chat (Ctrl+N)", "New chat (⌘N)")]
    [InlineData("Settings (Ctrl+,)", "Settings (⌘,)")]
    [InlineData("Chats (Ctrl+1)", "Chats (⌘1)")]
    [InlineData("Work in a separate git worktree (Ctrl+Alt+W)", "Work in a separate git worktree (⌘⌥W)")]
    [InlineData("use Ctrl+Enter instead", "use ⌘Enter instead")]
    [InlineData("Collapse sidebar (Ctrl+B)", "Collapse sidebar (⌘B)")]
    public void AdaptKeyboardHint_OnMac_RendersNativeSymbols(string input, string expected)
    {
        Assert.Equal(expected, Loc.AdaptKeyboardHint(input, isMac: true));
    }

    [Theory]
    [InlineData("Search (Ctrl+K)")]
    [InlineData("New chat (Ctrl+N)")]
    [InlineData("Work in a separate git worktree (Ctrl+Alt+W)")]
    public void AdaptKeyboardHint_OnWindowsAndLinux_IsUnchanged(string input)
    {
        // Windows/Linux must see the exact original "Ctrl" wording (no behavior change).
        Assert.Equal(input, Loc.AdaptKeyboardHint(input, isMac: false));
    }

    [Theory]
    [InlineData("Just some text with no shortcut")]
    [InlineData("")]
    [InlineData("A sentence about C and Ctrl theory")] // no '+', must be untouched
    public void AdaptKeyboardHint_LeavesNonShortcutTextIntact(string input)
    {
        Assert.Equal(input, Loc.AdaptKeyboardHint(input, isMac: true));
    }
}
