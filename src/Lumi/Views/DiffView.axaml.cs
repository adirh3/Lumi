using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Lumi.ViewModels;
using StrataTheme.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using TextMateSharp.Themes;
using Theme = TextMateSharp.Themes.Theme;

namespace Lumi.Views;

public partial class DiffView : UserControl
{
    private StackPanel? _diffContent;
    private ScrollViewer? _diffScroller;
    private TextBlock? _statsText;
    private TextBlock? _changeCountText;
    private Button? _prevBtn;
    private Button? _nextBtn;
    private Panel? _loadingOverlay;

    private readonly List<Border> _changeRegionControls = [];
    private int _currentChangeIndex = -1;
    private EventHandler? _pendingScrollHandler;

    public DiffView()
    {
        InitializeComponent();
        _diffContent = this.FindControl<StackPanel>("DiffContent");
        _diffScroller = this.FindControl<ScrollViewer>("DiffScroller");
        _statsText = this.FindControl<TextBlock>("StatsText");
        _changeCountText = this.FindControl<TextBlock>("ChangeCountText");
        _prevBtn = this.FindControl<Button>("PrevChangeBtn");
        _nextBtn = this.FindControl<Button>("NextChangeBtn");
        _loadingOverlay = this.FindControl<Panel>("LoadingOverlay");

        if (_prevBtn is not null) _prevBtn.Click += (_, _) => NavigateChange(-1);
        if (_nextBtn is not null) _nextBtn.Click += (_, _) => NavigateChange(1);
    }

    /// <summary>
    /// Shows the entire current file with changed regions highlighted.
    /// Finds where each edit's NewText appears in the file and highlights those lines.
    /// </summary>
    public async void SetFileChangeDiff(FileChangeItem fileChange)
    {
        if (_diffContent is null) return;
        _diffContent.Children.Clear();
        _changeRegionControls.Clear();
        _currentChangeIndex = -1;
        CancelPendingScroll();

        // Show centered loading overlay
        if (_loadingOverlay is not null) _loadingOverlay.IsVisible = true;

        var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        var filePath = fileChange.FilePath;
        var edits = fileChange.Edits.ToList();
        var isCreate = fileChange.IsCreate;

        // Determine language from file extension
        var ext = Path.GetExtension(filePath)?.TrimStart('.');
        var language = ext ?? "";

        // Heavy work on background thread: file I/O + change detection + tokenization
        var result = await Task.Run(() =>
        {
            string content;
            try { content = File.Exists(filePath) ? File.ReadAllText(filePath) : ""; }
            catch { content = ""; }

            if (string.IsNullOrEmpty(content))
                return (Lines: Array.Empty<string>(), ChangedLines: new HashSet<int>(), TokenizedLines: Array.Empty<TokenizedLine>());

            var lines = content.Split('\n');
            var changed = new HashSet<int>();

            foreach (var (_, newText) in edits)
            {
                if (string.IsNullOrEmpty(newText)) continue;
                MarkChangedLines(content, lines, newText, changed);
            }

            if (isCreate)
                for (int i = 0; i < lines.Length; i++)
                    changed.Add(i);

            // Tokenize all lines on background thread (TextMate is not UI-bound)
            var tokenized = TokenizeLines(lines, language, isDark);

            return (Lines: lines, ChangedLines: changed, TokenizedLines: tokenized);
        });

        if (result.Lines.Length == 0)
        {
            if (_loadingOverlay is not null) _loadingOverlay.IsVisible = false;
            UpdateStats(fileChange.LinesAdded, fileChange.LinesRemoved);
            UpdateNavigation();
            return;
        }

        // Yield to ensure loading overlay renders before heavy UI control creation
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

        // Build UI controls in batches so the loading animation stays alive
        await BuildFileViewAsync(result.Lines, isDark, result.ChangedLines, result.TokenizedLines);

        if (_loadingOverlay is not null) _loadingOverlay.IsVisible = false;
        UpdateStats(fileChange.LinesAdded, fileChange.LinesRemoved);
        UpdateNavigation();

        ScheduleScrollToFirstChange();
    }

    /// <summary>Pre-tokenized line data produced on background thread.</summary>
    private readonly record struct TokenRun(int Start, int End, int FgColorId, int FontStyleBits);
    private readonly record struct TokenizedLine(TokenRun[] Runs);

    /// <summary>Tokenizes all lines using TextMate on a background thread.</summary>
    private static TokenizedLine[] TokenizeLines(string[] lines, string language, bool isDark)
    {
        var (options, registry) = GetRegistryPair(isDark);
        IGrammar? grammar = null;
        if (!string.IsNullOrWhiteSpace(language))
            grammar = StrataCodeBlock.ResolveGrammar(options, registry, language);
        Theme? theme = grammar is not null ? registry.GetTheme() : null;

        var result = new TokenizedLine[lines.Length];
        IStateStack? ruleStack = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var lineText = lines[i].TrimEnd('\r');
            if (grammar is null || theme is null || string.IsNullOrEmpty(lineText))
            {
                // Advance ruleStack for blank lines to keep state consistent
                if (grammar is not null)
                {
                    try { ruleStack = grammar.TokenizeLine(lineText, ruleStack, TimeSpan.FromMilliseconds(50))?.RuleStack ?? ruleStack; }
                    catch { }
                }
                result[i] = new TokenizedLine([]);
                continue;
            }

            try
            {
                var tokenResult = grammar.TokenizeLine(lineText, ruleStack, TimeSpan.FromMilliseconds(200));
                if (tokenResult?.Tokens is { Length: > 0 })
                {
                    ruleStack = tokenResult.RuleStack;
                    var runs = new TokenRun[tokenResult.Tokens.Length];
                    for (int j = 0; j < tokenResult.Tokens.Length; j++)
                    {
                        var token = tokenResult.Tokens[j];
                        int start = token.StartIndex;
                        int end = (j + 1 < tokenResult.Tokens.Length) ? tokenResult.Tokens[j + 1].StartIndex : lineText.Length;
                        if (start >= lineText.Length) { runs[j] = new TokenRun(0, 0, 0, -1); continue; }
                        if (end > lineText.Length) end = lineText.Length;

                        int fgColorId = 0;
                        int fontStyleBits = -1;
                        var rules = theme.Match(token.Scopes);
                        if (rules is not null)
                            foreach (var rule in rules)
                            {
                                if (rule.foreground > 0) fgColorId = rule.foreground;
                                if ((int)rule.fontStyle >= 0) fontStyleBits = (int)rule.fontStyle;
                            }
                        runs[j] = new TokenRun(start, end, fgColorId, fontStyleBits);
                    }
                    result[i] = new TokenizedLine(runs);
                }
                else
                {
                    ruleStack = tokenResult?.RuleStack ?? ruleStack;
                    result[i] = new TokenizedLine([]);
                }
            }
            catch
            {
                result[i] = new TokenizedLine([]);
            }
        }
        return result;
    }

    /// <summary>Finds which line indices contain the given newText and adds them to changedLines.</summary>
    private static void MarkChangedLines(string fullContent, string[] lines, string newText, HashSet<int> changedLines)
    {
        // Normalize to \n for consistent matching regardless of line ending style
        var normalizedContent = fullContent.Replace("\r\n", "\n");
        var normalizedNew = newText.Replace("\r\n", "\n");

        var idx = normalizedContent.IndexOf(normalizedNew, StringComparison.Ordinal);
        if (idx < 0) return;

        // Convert character offset to line indices (all in normalized space)
        int charPos = 0;
        for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            int lineLen = lines[lineIdx].TrimEnd('\r').Length + 1; // +1 for \n
            if (charPos + lineLen > idx)
            {
                int endIdx = idx + normalizedNew.Length;
                for (int j = lineIdx; j < lines.Length; j++)
                {
                    changedLines.Add(j);
                    charPos += lines[j].TrimEnd('\r').Length + 1;
                    if (charPos >= endIdx) break;
                }
                return;
            }
            charPos += lineLen;
        }
    }

    /// <summary>
    /// Shows the file with explicit changed line numbers highlighted.
    /// Used for git diffs where we know exactly which lines changed.
    /// </summary>
    public async void SetFileDiffWithChangedLines(string filePath, HashSet<int> changedLineNumbers)
    {
        if (_diffContent is null) return;
        _diffContent.Children.Clear();
        _changeRegionControls.Clear();
        _currentChangeIndex = -1;
        CancelPendingScroll();

        if (_loadingOverlay is not null) _loadingOverlay.IsVisible = true;

        var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        var ext = Path.GetExtension(filePath)?.TrimStart('.');
        var language = ext ?? "";

        var result = await Task.Run(() =>
        {
            string content;
            try { content = File.Exists(filePath) ? File.ReadAllText(filePath) : ""; }
            catch { content = ""; }

            if (string.IsNullOrEmpty(content))
                return (Lines: Array.Empty<string>(), TokenizedLines: Array.Empty<TokenizedLine>());

            var lines = content.Split('\n');
            var tokenized = TokenizeLines(lines, language, isDark);
            return (Lines: lines, TokenizedLines: tokenized);
        });

        if (result.Lines.Length == 0)
        {
            if (_loadingOverlay is not null) _loadingOverlay.IsVisible = false;
            UpdateStats(changedLineNumbers.Count, 0);
            UpdateNavigation();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await BuildFileViewAsync(result.Lines, isDark, changedLineNumbers, result.TokenizedLines);

        if (_loadingOverlay is not null) _loadingOverlay.IsVisible = false;
        UpdateStats(changedLineNumbers.Count, 0);
        UpdateNavigation();

        ScheduleScrollToFirstChange();
    }

    private async Task BuildFileViewAsync(string[] lines, bool isDark, HashSet<int> changedLines, TokenizedLine[] tokenizedLines)
    {
        if (_diffContent is null) return;

        var monoFont = new FontFamily("Cascadia Code, Cascadia Mono, Consolas, Courier New, monospace");
        const double fontSize = 12.5;
        const int batchSize = 30;

        var changeBg = isDark
            ? new SolidColorBrush(Color.FromArgb(30, 63, 185, 80))
            : new SolidColorBrush(Color.FromArgb(35, 0, 160, 0));
        var changeGutterBg = isDark
            ? new SolidColorBrush(Color.FromArgb(60, 63, 185, 80))
            : new SolidColorBrush(Color.FromArgb(70, 0, 160, 0));
        var gutterBg = isDark
            ? new SolidColorBrush(Color.FromArgb(20, 128, 128, 128))
            : new SolidColorBrush(Color.FromArgb(15, 0, 0, 0));
        var gutterFg = isDark
            ? new SolidColorBrush(Color.FromArgb(120, 200, 200, 200))
            : new SolidColorBrush(Color.FromArgb(120, 80, 80, 80));
        var changeGutterFg = isDark
            ? new SolidColorBrush(Color.FromArgb(180, 63, 185, 80))
            : new SolidColorBrush(Color.FromArgb(200, 0, 140, 0));

        var brushMap = GetBrushMap(isDark);
        var (_, registry) = GetRegistryPair(isDark);
        Theme? theme = registry.GetTheme();

        bool inChangeRegion = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var lineText = lines[i].TrimEnd('\r');
            bool isChanged = changedLines.Contains(i);

            var tokenized = i < tokenizedLines.Length ? tokenizedLines[i] : new TokenizedLine([]);
            var row = BuildHighlightedLine(
                lineText, i + 1, tokenized, theme, brushMap,
                monoFont, fontSize,
                isChanged ? changeBg : null,
                isChanged ? changeGutterBg : gutterBg,
                isChanged ? changeGutterFg : gutterFg,
                isDark);
            _diffContent.Children.Add(row);

            // Track change regions for navigation (after row is created for Bounds access)
            if (isChanged && !inChangeRegion)
            {
                _changeRegionControls.Add(row);
                inChangeRegion = true;
            }
            else if (!isChanged)
            {
                inChangeRegion = false;
            }

            // Yield every batch to keep loading animation alive
            if ((i + 1) % batchSize == 0)
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    private void UpdateStats(int added, int removed)
    {
        if (_statsText is null) return;
        _statsText.Text = removed == 0 ? $"+{added}" : $"+{added}  −{removed}";
    }

    private void UpdateNavigation()
    {
        if (_changeCountText is null) return;
        _changeCountText.Text = _changeRegionControls.Count > 0
            ? $"{_changeRegionControls.Count} changes"
            : "";
    }

    private void NavigateChange(int direction)
    {
        if (_changeRegionControls.Count == 0 || _diffScroller is null) return;
        _currentChangeIndex += direction;
        if (_currentChangeIndex >= _changeRegionControls.Count) _currentChangeIndex = 0;
        if (_currentChangeIndex < 0) _currentChangeIndex = _changeRegionControls.Count - 1;
        ScrollToCurrentChange();
    }

    private void ScrollToCurrentChange()
    {
        if (_changeRegionControls.Count == 0 || _diffScroller is null) return;
        if (_currentChangeIndex < 0 || _currentChangeIndex >= _changeRegionControls.Count) return;
        var control = _changeRegionControls[_currentChangeIndex];
        if (control.Bounds.Height > 0)
            _diffScroller.Offset = new Vector(_diffScroller.Offset.X, Math.Max(0, control.Bounds.Y - 40));
    }

    private void ScheduleScrollToFirstChange()
    {
        CancelPendingScroll();
        if (_changeRegionControls.Count == 0 || _diffContent is null || _diffScroller is null) return;

        _pendingScrollHandler = (_, _) =>
        {
            if (_changeRegionControls.Count == 0) { CancelPendingScroll(); return; }
            var first = _changeRegionControls[0];
            if (first.Bounds.Height <= 0) return; // Not laid out yet
            CancelPendingScroll();
            _currentChangeIndex = 0;
            ScrollToCurrentChange();
        };
        _diffContent.LayoutUpdated += _pendingScrollHandler;
    }

    private void CancelPendingScroll()
    {
        if (_pendingScrollHandler is not null && _diffContent is not null)
        {
            _diffContent.LayoutUpdated -= _pendingScrollHandler;
            _pendingScrollHandler = null;
        }
    }

    private static Border BuildHighlightedLine(
        string text, int lineNumber,
        TokenizedLine tokenized, Theme? theme, Dictionary<int, IBrush> brushMap,
        FontFamily font, double fontSize,
        IBrush? lineBg, IBrush gutterBg, IBrush gutterFg, bool isDark)
    {
        var gutter = new Border
        {
            Background = gutterBg,
            Width = 48,
            Padding = new Thickness(4, 0),
            Child = new TextBlock
            {
                Text = lineNumber.ToString(),
                FontFamily = font,
                FontSize = fontSize,
                Foreground = gutterFg,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };

        var content = new SelectableTextBlock
        {
            FontFamily = font,
            FontSize = fontSize,
            LineHeight = StrataCodeBlock.CodeLineHeight,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var inlines = content.Inlines ??= new InlineCollection();

        if (tokenized.Runs.Length > 0 && theme is not null && !string.IsNullOrEmpty(text))
        {
            foreach (var run in tokenized.Runs)
            {
                if (run.Start >= run.End || run.Start >= text.Length) continue;
                int end = Math.Min(run.End, text.Length);
                var r = new Run(text[run.Start..end]);

                if (run.FgColorId > 0)
                {
                    var brush = GetOrCreateBrush(brushMap, theme, run.FgColorId);
                    if (brush != Brushes.Transparent) r.Foreground = brush;
                }
                if (run.FontStyleBits > 0)
                {
                    if ((run.FontStyleBits & (int)TextMateSharp.Themes.FontStyle.Italic) != 0)
                        r.FontStyle = Avalonia.Media.FontStyle.Italic;
                    if ((run.FontStyleBits & (int)TextMateSharp.Themes.FontStyle.Bold) != 0)
                        r.FontWeight = FontWeight.Bold;
                }
                inlines.Add(r);
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(text)) inlines.Add(new Run(text));
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(gutter, 0);
        Grid.SetColumn(content, 1);
        grid.Children.Add(gutter);
        grid.Children.Add(content);

        return new Border
        {
            Background = lineBg ?? Brushes.Transparent,
            MinHeight = 20,
            Padding = new Thickness(0, 1),
            Child = grid,
        };
    }

    // ── TextMate helpers ──────────────────────────────────────

    private static (RegistryOptions options, Registry registry) GetRegistryPair(bool isDark)
    {
        if (isDark)
        {
            s_darkOptions ??= new RegistryOptions(ThemeName.DarkPlus);
            s_darkRegistry ??= new Registry(s_darkOptions);
            return (s_darkOptions, s_darkRegistry);
        }
        s_lightOptions ??= new RegistryOptions(ThemeName.LightPlus);
        s_lightRegistry ??= new Registry(s_lightOptions);
        return (s_lightOptions, s_lightRegistry);
    }

    private static RegistryOptions? s_darkOptions;
    private static Registry? s_darkRegistry;
    private static RegistryOptions? s_lightOptions;
    private static Registry? s_lightRegistry;
    private static Dictionary<int, IBrush>? s_darkBrushMap;
    private static Dictionary<int, IBrush>? s_lightBrushMap;

    private static Dictionary<int, IBrush> GetBrushMap(bool isDark)
        => isDark ? (s_darkBrushMap ??= []) : (s_lightBrushMap ??= []);

    private static IBrush GetOrCreateBrush(Dictionary<int, IBrush> brushMap, Theme theme, int colorId)
    {
        if (brushMap.TryGetValue(colorId, out var brush))
            return brush;
        var hex = theme.GetColor(colorId);
        brush = Color.TryParse(hex, out var color)
            ? new SolidColorBrush(color).ToImmutable()
            : Brushes.Transparent;
        brushMap[colorId] = brush;
        return brush;
    }
}
