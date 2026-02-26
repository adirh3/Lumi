using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using System;
using System.Collections.Generic;

namespace Lumi.Views;

public partial class DiffView : UserControl
{
    private StackPanel? _diffContent;

    public DiffView()
    {
        InitializeComponent();
        _diffContent = this.FindControl<StackPanel>("DiffContent");
    }

    /// <summary>
    /// Sets the diff content to display. For edit tools: old text (removed) → new text (added).
    /// For create tools: all new content.
    /// </summary>
    public void SetDiff(string filePath, string? oldText, string? newText)
    {
        if (_diffContent is null) return;
        _diffContent.Children.Clear();

        var isDark = Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;

        var removedBg = isDark
            ? new SolidColorBrush(Color.FromArgb(40, 248, 81, 73))
            : new SolidColorBrush(Color.FromArgb(50, 255, 0, 0));
        var addedBg = isDark
            ? new SolidColorBrush(Color.FromArgb(40, 63, 185, 80))
            : new SolidColorBrush(Color.FromArgb(50, 0, 160, 0));
        var gutterRemoved = isDark
            ? new SolidColorBrush(Color.FromArgb(60, 248, 81, 73))
            : new SolidColorBrush(Color.FromArgb(80, 255, 0, 0));
        var gutterAdded = isDark
            ? new SolidColorBrush(Color.FromArgb(60, 63, 185, 80))
            : new SolidColorBrush(Color.FromArgb(80, 0, 160, 0));

        var monoFont = new FontFamily("Cascadia Code, Cascadia Mono, Consolas, Courier New, monospace");
        var fontSize = 12.5;

        // If both old and new are present, show a unified diff hunk
        if (!string.IsNullOrEmpty(oldText) && !string.IsNullOrEmpty(newText))
        {
            var oldLines = oldText.Split('\n');
            var newLines = newText.Split('\n');

            // Removed lines
            foreach (var line in oldLines)
            {
                _diffContent.Children.Add(BuildDiffLine(
                    "-", line.TrimEnd('\r'), removedBg, gutterRemoved, monoFont, fontSize, isDark));
            }

            // Added lines
            foreach (var line in newLines)
            {
                _diffContent.Children.Add(BuildDiffLine(
                    "+", line.TrimEnd('\r'), addedBg, gutterAdded, monoFont, fontSize, isDark));
            }
        }
        else if (!string.IsNullOrEmpty(newText))
        {
            // Create / insert — all new content
            var newLines = newText.Split('\n');
            foreach (var line in newLines)
            {
                _diffContent.Children.Add(BuildDiffLine(
                    "+", line.TrimEnd('\r'), addedBg, gutterAdded, monoFont, fontSize, isDark));
            }
        }
        else if (!string.IsNullOrEmpty(oldText))
        {
            // Deletion — all removed content
            var oldLines = oldText.Split('\n');
            foreach (var line in oldLines)
            {
                _diffContent.Children.Add(BuildDiffLine(
                    "-", line.TrimEnd('\r'), removedBg, gutterRemoved, monoFont, fontSize, isDark));
            }
        }
    }

    /// <summary>
    /// Sets the diff for a multi-replace operation (multiple hunks in one file or across files).
    /// </summary>
    public void SetMultiDiff(List<(string FilePath, string? OldText, string? NewText)> replacements)
    {
        if (_diffContent is null) return;
        _diffContent.Children.Clear();

        foreach (var (filePath, oldText, newText) in replacements)
        {
            // Section header for each file/hunk
            var header = new TextBlock
            {
                Text = $"── {System.IO.Path.GetFileName(filePath)} ──",
                FontWeight = FontWeight.SemiBold,
                Foreground = Application.Current?.FindResource("Brush.TextSecondary") as IBrush
                    ?? Brushes.Gray,
                Margin = new Thickness(0, 8, 0, 4),
                FontSize = 12,
            };
            _diffContent.Children.Add(header);

            // Reuse single diff rendering for this hunk
            var hunkView = new DiffView();
            hunkView.SetDiff(filePath, oldText, newText);
            if (hunkView._diffContent is not null)
            {
                foreach (var child in new List<Control>(hunkView._diffContent.Children))
                {
                    hunkView._diffContent.Children.Remove(child);
                    _diffContent.Children.Add(child);
                }
            }
        }
    }

    private static Border BuildDiffLine(
        string prefix, string text, IBrush background, IBrush gutterBg,
        FontFamily font, double fontSize, bool isDark)
    {
        var gutterFg = isDark
            ? new SolidColorBrush(Color.FromArgb(180, 200, 200, 200))
            : new SolidColorBrush(Color.FromArgb(180, 80, 80, 80));

        var gutter = new Border
        {
            Background = gutterBg,
            Width = 24,
            Padding = new Thickness(4, 0),
            Child = new TextBlock
            {
                Text = prefix,
                FontFamily = font,
                FontSize = fontSize,
                Foreground = gutterFg,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };

        var content = new TextBlock
        {
            Text = text,
            FontFamily = font,
            FontSize = fontSize,
            Foreground = Application.Current?.FindResource("Brush.TextPrimary") as IBrush ?? Brushes.White,
            Margin = new Thickness(8, 0, 0, 0),
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        };
        Grid.SetColumn(gutter, 0);
        Grid.SetColumn(content, 1);
        grid.Children.Add(gutter);
        grid.Children.Add(content);

        return new Border
        {
            Background = background,
            MinHeight = 22,
            Padding = new Thickness(0, 1),
            Child = grid,
        };
    }
}
