using System;
using Xunit;

namespace Lumi.Tests;

public sealed class AvaloniaTextSelectionExceptionTests
{
    [Fact]
    public void IsAvaloniaTextSelectionBoundsFailure_MatchesSelectionHandleCrash()
    {
        var exception = Capture(TextSelectionHandleCanvas.Render);

        Assert.True(Program.IsAvaloniaTextSelectionBoundsFailure(exception));
    }

    [Fact]
    public void IsAvaloniaTextSelectionBoundsFailure_MatchesSelectableTextBlockRenderCrash()
    {
        var exception = Capture(SelectableTextBlock.RenderTextLayout);

        Assert.True(Program.IsAvaloniaTextSelectionBoundsFailure(exception));
    }

    [Fact]
    public void IsAvaloniaTextSelectionBoundsFailure_MatchesTextPresenterRenderCrash()
    {
        var exception = Capture(TextPresenter.Render);

        Assert.True(Program.IsAvaloniaTextSelectionBoundsFailure(exception));
    }

    [Fact]
    public void IsAvaloniaTextSelectionBoundsFailure_MatchesMarkdownHitTestCrash()
    {
        var exception = Capture(StrataMarkdown.GetLinkAtRenderedPoint);

        Assert.True(Program.IsAvaloniaTextSelectionBoundsFailure(exception));
    }

    [Fact]
    public void IsAvaloniaTextSelectionBoundsFailure_IgnoresUnrelatedInvalidOperation()
    {
        var exception = new InvalidOperationException("Covered length must be greater than zero.");

        Assert.False(Program.IsAvaloniaTextSelectionBoundsFailure(exception));
    }

    [Fact]
    public void IsAvaloniaTextSelectionBoundsFailure_IgnoresIncompleteHitTestStack()
    {
        var exception = Capture(IncompleteTextLayout.HitTestTextRange);

        Assert.False(Program.IsAvaloniaTextSelectionBoundsFailure(exception));
    }

    private static Exception Capture(Action action)
    {
        try
        {
            action();
            throw new InvalidOperationException("Expected simulated crash.");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    private static class TextSelectionHandleCanvas
    {
        public static void Render() => TextLayout.HitTestTextRange();
    }

    private static class SelectableTextBlock
    {
        public static void RenderTextLayout() => TextLayout.HitTestTextRange();
    }

    private static class TextPresenter
    {
        public static void Render() => TextLayout.HitTestTextRange();
    }

    private static class StrataMarkdown
    {
        public static void GetLinkAtRenderedPoint() => TextLayout.HitTestTextRange();
    }

    private static class TextLayout
    {
        public static void HitTestTextRange() => TextLineImpl.GetTextBounds();
    }

    private static class TextLineImpl
    {
        public static void GetTextBounds() =>
            throw new InvalidOperationException("Covered length must be greater than zero.");
    }

    private static class IncompleteTextLayout
    {
        public static void HitTestTextRange() =>
            throw new InvalidOperationException("Covered length must be greater than zero.");
    }
}
