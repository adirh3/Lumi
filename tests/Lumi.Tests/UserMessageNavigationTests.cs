using Avalonia.Input;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

public sealed class UserMessageNavigationTests
{
    [Theory]
    [InlineData(Key.Up, KeyModifiers.Control, false, -1)]
    [InlineData(Key.Down, KeyModifiers.Control, false, 1)]
    [InlineData(Key.Up, KeyModifiers.Meta | KeyModifiers.Alt, true, -1)]
    [InlineData(Key.Down, KeyModifiers.Meta | KeyModifiers.Alt, true, 1)]
    [InlineData(Key.Up, KeyModifiers.None, false, 0)]
    [InlineData(Key.Up, KeyModifiers.Control | KeyModifiers.Shift, false, 0)]
    [InlineData(Key.Up, KeyModifiers.Meta, true, 0)]
    [InlineData(Key.Up, KeyModifiers.Control, true, 0)]
    [InlineData(Key.Left, KeyModifiers.Control, false, 0)]
    public void Shortcut_MapsToDirection(Key key, KeyModifiers modifiers, bool isMac, int expected)
    {
        Assert.Equal(expected, ChatView.GetUserMessageNavigationDirection(key, modifiers, isMac));
    }

    // Turns: 0 user, 1 assistant, 2 user, 3 assistant, 4 assistant, 5 user, 6 assistant
    private static readonly bool[] Turns = [true, false, true, false, false, true, false];

    [Theory]
    [InlineData(2, false, -1, 0)] // at a user message's start → the one before it
    [InlineData(2, true, -1, 2)]  // partway through a user message → its own start
    [InlineData(4, true, -1, 2)]  // partway through a reply → the prompt it answers
    [InlineData(4, false, -1, 2)]
    [InlineData(0, false, -1, -1)]
    [InlineData(0, true, -1, 0)]
    [InlineData(0, false, 1, 2)]
    [InlineData(2, true, 1, 5)]
    [InlineData(3, false, 1, 5)]
    [InlineData(5, false, 1, -1)] // past the last user message
    [InlineData(6, true, 1, -1)]
    public void FindTarget_StepsBetweenUserTurns(int anchor, bool anchorAbove, int direction, int expected)
    {
        Assert.Equal(expected, ChatView.FindUserMessageTarget(Turns, anchor, anchorAbove, direction));
    }

    [Fact]
    public void FindTarget_EmptyTranscript_ReturnsNone()
    {
        Assert.Equal(-1, ChatView.FindUserMessageTarget([], 0, false, 1));
        Assert.Equal(-1, ChatView.FindUserMessageTarget([], 0, false, -1));
    }
}
