using Avalonia.Input;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

public sealed class LinkedChatChipInteractionTests
{
    [Theory]
    [InlineData(Key.Enter, KeyModifiers.Control, false, true)]
    [InlineData(Key.Enter, KeyModifiers.Meta, true, true)]
    [InlineData(Key.Enter, KeyModifiers.None, false, false)]
    [InlineData(Key.Enter, KeyModifiers.Control | KeyModifiers.Shift, false, false)]
    [InlineData(Key.Enter, KeyModifiers.Meta | KeyModifiers.Alt, true, false)]
    [InlineData(Key.Space, KeyModifiers.Control, false, false)]
    public void OpenInNewWindowShortcut_MatchesNativePrimaryModifier(
        Key key,
        KeyModifiers modifiers,
        bool isMac,
        bool expected)
    {
        Assert.Equal(
            expected,
            ChatView.IsOpenLinkedChatInNewWindowShortcut(key, modifiers, isMac));
    }
}
