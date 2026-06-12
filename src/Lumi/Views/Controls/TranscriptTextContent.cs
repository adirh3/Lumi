using System;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StrataTheme.Controls;

namespace Lumi.Views.Controls;

public readonly record struct TranscriptTextContentDiagnosticsSnapshot(
    long InstanceCount,
    long MarkdownBranchCount,
    long PlainTextCount,
    long PlainTextCharacterCount)
{
    public double AveragePlainTextLength => PlainTextCount == 0
        ? 0
        : PlainTextCharacterCount / (double)PlainTextCount;

    public static TranscriptTextContentDiagnosticsSnapshot operator -(
        TranscriptTextContentDiagnosticsSnapshot after,
        TranscriptTextContentDiagnosticsSnapshot before) => new(
            after.InstanceCount - before.InstanceCount,
            after.MarkdownBranchCount - before.MarkdownBranchCount,
            after.PlainTextCount - before.PlainTextCount,
            after.PlainTextCharacterCount - before.PlainTextCharacterCount);
}

public sealed class TranscriptTextContent : ContentControl
{
    private static readonly Regex MarkdownBlockPattern = new(
        @"(^|\n)\s{0,3}(#{1,6}\s+|[-*+]\s+|\d+\.\s+|>\s+|```|~~~)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex MarkdownInlinePattern = new(
        @"(\[[^\]\r\n]+\]\([^)\r\n]+\)|`[^`\r\n]+`|\*\*[^*\r\n]+\*\*|__[^_\r\n]+__)",
        RegexOptions.Compiled);

    private static readonly Regex MarkdownTablePattern = new(
        @"^\s*\|?.+\|.+\n\s*\|?\s*:?-{3,}",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex MarkdownRulePattern = new(
        @"(^|\n)\s{0,3}([-*_])(?:\s*\2){2,}\s*(\n|$)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<TranscriptTextContent, string?>(nameof(Text));

    public static readonly StyledProperty<bool> PreferPlainTextProperty =
        AvaloniaProperty.Register<TranscriptTextContent, bool>(nameof(PreferPlainText));

    public static readonly StyledProperty<bool> RenderMarkdownWhileStreamingProperty =
        AvaloniaProperty.Register<TranscriptTextContent, bool>(nameof(RenderMarkdownWhileStreaming));

    private static long _diagnosticInstanceCount;
    private static long _diagnosticMarkdownBranchCount;
    private static long _diagnosticPlainTextCount;
    private static long _diagnosticPlainTextCharacterCount;

    private readonly SelectableTextBlock _textBlock;
    private readonly StrataMarkdown _markdown;

    static TranscriptTextContent()
    {
        TextProperty.Changed.AddClassHandler<TranscriptTextContent>((control, _) => control.UpdateContent());
        PreferPlainTextProperty.Changed.AddClassHandler<TranscriptTextContent>((control, _) => control.UpdateContent());
        RenderMarkdownWhileStreamingProperty.Changed.AddClassHandler<TranscriptTextContent>((control, _) => control.UpdateContent());
    }

    public TranscriptTextContent()
    {
        System.Threading.Interlocked.Increment(ref _diagnosticInstanceCount);

        _textBlock = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 14,
            LineHeight = 21.3,
        };

        _markdown = new StrataMarkdown
        {
            IsInline = true,
            RetainContentOnDetach = true,
        };

        UpdateContent();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool PreferPlainText
    {
        get => GetValue(PreferPlainTextProperty);
        set => SetValue(PreferPlainTextProperty, value);
    }

    public bool RenderMarkdownWhileStreaming
    {
        get => GetValue(RenderMarkdownWhileStreamingProperty);
        set => SetValue(RenderMarkdownWhileStreamingProperty, value);
    }

    public static TranscriptTextContentDiagnosticsSnapshot CaptureDiagnostics() => new(
        System.Threading.Interlocked.Read(ref _diagnosticInstanceCount),
        System.Threading.Interlocked.Read(ref _diagnosticMarkdownBranchCount),
        System.Threading.Interlocked.Read(ref _diagnosticPlainTextCount),
        System.Threading.Interlocked.Read(ref _diagnosticPlainTextCharacterCount));

    private void UpdateContent()
    {
        var text = Text ?? string.Empty;
        var direction = StrataTextDirectionDetector.Detect(text);

        if (ShouldRenderMarkdown(text) && (!PreferPlainText || RenderMarkdownWhileStreaming))
        {
            System.Threading.Interlocked.Increment(ref _diagnosticMarkdownBranchCount);
            _markdown.Markdown = text;
            var markdownDirection = direction ?? FlowDirection.LeftToRight;
            if (_markdown.FlowDirection != markdownDirection)
                _markdown.FlowDirection = markdownDirection;

            if (!ReferenceEquals(Content, _markdown))
                Content = _markdown;

            return;
        }

        var textDirection = direction ?? FlowDirection.LeftToRight;
        var targetAlignment = textDirection == FlowDirection.RightToLeft
            ? TextAlignment.Right
            : TextAlignment.Left;

        System.Threading.Interlocked.Add(ref _diagnosticPlainTextCharacterCount, text.Length);
        System.Threading.Interlocked.Increment(ref _diagnosticPlainTextCount);
        _textBlock.Text = text;
        if (_textBlock.FlowDirection != textDirection)
            _textBlock.FlowDirection = textDirection;
        if (_textBlock.TextAlignment != targetAlignment)
            _textBlock.TextAlignment = targetAlignment;

        if (!ReferenceEquals(Content, _textBlock))
            Content = _textBlock;
    }

    private static bool ShouldRenderMarkdown(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (text.IndexOfAny(['`', '#', '*', '_', '[', '|', '>', '~']) < 0)
            return false;

        return MarkdownBlockPattern.IsMatch(text)
            || MarkdownInlinePattern.IsMatch(text)
            || MarkdownTablePattern.IsMatch(text)
            || MarkdownRulePattern.IsMatch(text);
    }
}
