using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Lumi.ViewModels;

namespace Lumi.Views.Controls;

/// <summary>
/// A launchpad starter's words as one run of text, its blanks in <see cref="BlankForeground"/> over a dotted line. Being
/// one text block, the label trims as a whole, which words and blanks laid side by side can't.
/// </summary>
public sealed class LaunchpadStarterLabel : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<LaunchpadTemplatePart>?> PartsProperty =
        AvaloniaProperty.Register<LaunchpadStarterLabel, IReadOnlyList<LaunchpadTemplatePart>?>(nameof(Parts));

    public static readonly StyledProperty<IBrush?> BlankForegroundProperty =
        AvaloniaProperty.Register<LaunchpadStarterLabel, IBrush?>(nameof(BlankForeground));

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public IReadOnlyList<LaunchpadTemplatePart>? Parts
    {
        get => GetValue(PartsProperty);
        set => SetValue(PartsProperty, value);
    }

    /// <summary>The colour of a blank's words: the words that will fill it, before the user types their own.</summary>
    public IBrush? BlankForeground
    {
        get => GetValue(BlankForegroundProperty);
        set => SetValue(BlankForegroundProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PartsProperty || change.Property == BlankForegroundProperty)
            Compose();
    }

    private void Compose()
    {
        var inlines = Inlines ??= [];
        inlines.Clear();
        foreach (var part in Parts ?? [])
        {
            if (part.SpaceBefore && inlines.Count > 0)
                inlines.Add(new Run(" "));

            var run = new Run(part.Text);
            if (part.IsBlank)
            {
                run.Foreground = BlankForeground;
                run.TextDecorations =
                [
                    new TextDecoration
                    {
                        Location = TextDecorationLocation.Underline,
                        StrokeThickness = 1,
                        StrokeDashArray = new AvaloniaList<double> { 1, 2 },
                        StrokeOffset = 2,
                        StrokeOffsetUnit = TextDecorationUnit.Pixel,
                    },
                ];
            }

            inlines.Add(run);
        }
    }
}
