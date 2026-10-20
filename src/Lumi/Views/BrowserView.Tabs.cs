using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Services;
using Lumi.ViewModels;

namespace Lumi.Views;

internal enum BrowserSiteKind { Blank, Secure, Insecure, File, Other }

/// <summary>How the chrome presents an address: the site to emphasize, the rest of it, and the
/// site's name that its tab badge is drawn from.</summary>
internal readonly record struct BrowserLocation(BrowserSiteKind Kind, string Host, string Rest, string Site = "");

/// <summary>The tab strip and the location line of the browser chrome.</summary>
public partial class BrowserView
{
    internal const double TabMinWidth = 54;
    internal const double TabMaxWidth = 200;
    internal const double TabSpacing = 4;
    private const double NewTabSlotWidth = 32;
    private const double TabStripFadeWidth = 20;
    // Below these widths a title has no room for words, so the tab shows just its badge
    // (and the open tab its close button).
    private const double TitledTabMinWidth = 76;
    private const double TitledActiveTabMinWidth = 88;

    private readonly Dictionary<string, TabChip> _tabChips = new(StringComparer.Ordinal);
    private ControlTheme? _tabHitTheme;
    private string? _activeChipId;
    private BrowserSiteKind? _shownUrlKind;
    private (bool Left, bool Right, double Width) _tabStripFades;

    private bool AnimationsEnabled => _dataStore?.Data.Settings.ShowAnimations != false;

    private sealed class TabChip(string id, Border root, Button select, TextBlock title,
        Border badge, TextBlock badgeLetter, PathIcon glyph)
    {
        public string Id { get; } = id;
        public Border Root { get; } = root;
        public Button Select { get; } = select;
        public TextBlock Title { get; } = title;
        public Border Badge { get; } = badge;
        public TextBlock BadgeLetter { get; } = badgeLetter;
        public PathIcon Glyph { get; } = glyph;
        public string? SiteKey { get; set; }
        public bool IsActive { get; set; }
    }

    /// <summary>Shares the strip between the open tabs like a desktop browser: an equal slice
    /// between <see cref="TabMinWidth"/> and <see cref="TabMaxWidth"/>; past the minimum it scrolls.</summary>
    internal static double CalculateTabWidth(double available, int count)
    {
        if (count <= 0 || !double.IsFinite(available) || available <= 0)
            return TabMaxWidth;
        var share = (available - TabSpacing * (count - 1)) / count;
        return Math.Floor(Math.Clamp(share, TabMinWidth, TabMaxWidth));
    }

    internal static bool IsCompactTab(double width, bool isActive)
        => width < (isActive ? TitledActiveTabMinWidth : TitledTabMinWidth);

    internal static BrowserLocation DescribeLocation(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new(BrowserSiteKind.Blank, "", "about:blank");
        if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            return new(BrowserSiteKind.Blank, "", url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new(BrowserSiteKind.Other, "", url);
        if (uri.IsFile)
            return new(BrowserSiteKind.File, "", uri.LocalPath);
        if ((uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.Host.Length > 0)
        {
            var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            if (!uri.IsDefaultPort)
                host += ":" + uri.Port;
            var rest = Uri.UnescapeDataString(uri.PathAndQuery + uri.Fragment);
            return new(uri.Scheme == Uri.UriSchemeHttps ? BrowserSiteKind.Secure : BrowserSiteKind.Insecure,
                host, rest == "/" ? "" : rest, GetSiteName(uri.Host));
        }
        return new(BrowserSiteKind.Other, "", url);
    }

    /// <summary>The name a person knows a site by: <c>en.wikipedia.org</c> is Wikipedia and
    /// <c>bbc.co.uk</c> is the BBC, so a badge shows W or B rather than a subdomain's initial.</summary>
    internal static string GetSiteName(string host)
    {
        if (System.Net.IPAddress.TryParse(host, out _))
            return host;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            host = host[4..];
        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2)
            return host;
        var index = labels.Length - 2;
        // A country's own second level (co.uk, com.au) is not the site.
        if (index > 0 && labels[^1].Length == 2 && CountrySecondLevels.Contains(labels[index]))
            index--;
        return labels[index];
    }

    private static readonly HashSet<string> CountrySecondLevels =
        new(["ac", "co", "com", "edu", "go", "gov", "ne", "net", "or", "org"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Brings the strip in line with the service's tabs, keeping each tab's controls for
    /// as long as the tab is open: a title or address change never rebuilds the strip under the
    /// pointer (and its tooltip).</summary>
    private void UpdateTabs()
    {
        if (_tabsPanel is null || _browserService is null)
            return;

        var tabs = _browserService.Tabs;
        var animate = _tabChips.Count > 0 && AnimationsEnabled;
        var open = tabs.Select(tab => tab.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var closed in _tabChips.Values.Where(chip => !open.Contains(chip.Id)).ToArray())
            RemoveChip(closed);

        for (var i = 0; i < tabs.Count; i++)
        {
            var tab = tabs[i];
            if (!_tabChips.TryGetValue(tab.Id, out var chip))
            {
                chip = CreateChip(tab.Id);
                _tabChips[tab.Id] = chip;
                // A tab joins the strip in its final state (open, titled, badged), so only its
                // entrance animates and a rebuilt strip never fades its highlight back in.
                UpdateChip(chip, tab);
                if (animate)
                    PlayEntrance(chip.Root);
                _tabsPanel.Children.Insert(i, chip.Root);
                continue;
            }

            var index = _tabsPanel.Children.IndexOf(chip.Root);
            if (index != i)
                _tabsPanel.Children.Move(index, i);
            UpdateChip(chip, tab);
        }

        UpdateTabWidths();

        var activeId = tabs.FirstOrDefault(tab => tab.IsActive)?.Id;
        if (activeId != _activeChipId)
        {
            _activeChipId = activeId;
            // After layout, so the strip's extent already reflects the new widths.
            Dispatcher.UIThread.Post(ScrollActiveTabIntoView, DispatcherPriority.Loaded);
        }
    }

    private void ScrollActiveTabIntoView()
    {
        if (_tabsScroller is null || _activeChipId is not { } id || !_tabChips.TryGetValue(id, out var active))
            return;
        var bounds = active.Root.Bounds;
        var viewport = _tabsScroller.Viewport.Width;
        var offset = _tabsScroller.Offset.X;
        if (bounds.Left < offset)
            offset = bounds.Left;
        else if (bounds.Right > offset + viewport)
            offset = bounds.Right - viewport;
        var max = Math.Max(0, _tabsScroller.Extent.Width - viewport);
        _tabsScroller.Offset = new Vector(Math.Clamp(offset, 0, max), 0);
    }

    private TabChip CreateChip(string id)
    {
        var badgeLetter = new TextBlock();
        var badge = new Border { Child = badgeLetter, IsVisible = false };
        badge.Classes.Add("browser-tab-badge");
        var glyph = new PathIcon();
        glyph.Classes.Add("browser-tab-glyph");

        var title = new TextBlock();
        title.Classes.Add("browser-tab-title");
        Grid.SetColumn(title, 1);
        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Children = { new Panel { Children = { badge, glyph }, VerticalAlignment = VerticalAlignment.Center }, title },
        };

        var select = new Button { Name = "BrowserTab_" + id, Content = content };
        select.Classes.Add("browser-tab-hit");
        if (_tabHitTheme is not null)
            select.Theme = _tabHitTheme;

        var close = new Button { Name = "BrowserTabClose_" + id, Content = new PathIcon { Data = WorkspaceIcons.Dismiss } };
        close.Classes.Add("subtle");
        close.Classes.Add("browser-tab-close");
        AutomationProperties.SetName(close, Loc.Browser_CloseTab);

        var root = new Border { Child = new Panel { Children = { select, close } } };
        root.Classes.Add("browser-tab");

        var chip = new TabChip(id, root, select, title, badge, badgeLetter, glyph);
        select.Click += async (_, _) =>
        {
            if (!chip.IsActive)
                await RunTabActionAsync("switch", id);
        };
        close.Click += async (_, _) => await RunTabActionAsync("close", id);
        // Middle-click closes a tab, as in any desktop browser.
        root.PointerReleased += async (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Middle)
                return;
            e.Handled = true;
            await RunTabActionAsync("close", id);
        };
        return chip;
    }

    private static void UpdateChip(TabChip chip, BrowserTabInfo tab)
    {
        chip.IsActive = tab.IsActive;
        chip.Root.Classes.Set("active", tab.IsActive);

        var location = DescribeLocation(tab.Url);
        var title = !string.IsNullOrWhiteSpace(tab.Title) ? tab.Title.Trim()
            : location.Host.Length > 0 ? location.Host
            : Loc.Browser_NewTab;
        chip.Title.Text = title;
        AutomationProperties.SetName(chip.Select, title);
        ToolTip.SetTip(chip.Select, location.Kind == BrowserSiteKind.Blank ? title : title + "\n" + tab.Url);

        var isSite = location.Site.Length > 0;
        var siteKey = isSite ? location.Site : location.Kind.ToString();
        if (siteKey == chip.SiteKey)
            return;

        chip.SiteKey = siteKey;
        chip.Badge.IsVisible = isSite;
        chip.Glyph.IsVisible = !isSite;
        if (isSite)
        {
            chip.BadgeLetter.Text = SourceItem.ComputeInitial(siteKey, title);
            chip.Badge.Background = SourceItem.ComputeAccent(siteKey);
        }
        else
        {
            chip.Glyph.Data = location.Kind == BrowserSiteKind.File ? WorkspaceIcons.File : WorkspaceIcons.Browser;
        }
    }

    private void RemoveChip(TabChip chip)
    {
        // Close its tip while the tab is still in the tree: a tip closing after its control left
        // the tree crashes Avalonia.
        ToolTip.SetIsOpen(chip.Select, false);
        _tabsPanel?.Children.Remove(chip.Root);
        _tabChips.Remove(chip.Id);
    }

    private void ClearTabChips()
    {
        foreach (var chip in _tabChips.Values.ToArray())
            RemoveChip(chip);
        _tabsPanel?.Children.Clear();
        _activeChipId = null;
    }

    private void UpdateTabWidths()
    {
        if (_tabsPanel is null || _tabsScroller is null || _tabStripRow is null)
            return;
        var rowWidth = _tabStripRow.Bounds.Width;
        if (rowWidth <= 0)
            return;

        var newTabWidth = _newTabButton is { Bounds.Width: > 0 } button
            ? button.Bounds.Width + button.Margin.Left + button.Margin.Right
            : NewTabSlotWidth;
        var available = Math.Max(0, rowWidth - newTabWidth);
        _tabsScroller.MaxWidth = available;
        var width = CalculateTabWidth(available, _tabsPanel.Children.Count);
        foreach (var chip in _tabChips.Values)
        {
            chip.Root.Width = width;
            chip.Root.Classes.Set("compact", IsCompactTab(width, chip.IsActive));
        }
    }

    private static void PlayEntrance(Control root)
    {
        root.Opacity = 0;
        root.RenderTransform = TransformOperations.Parse("translateY(4px) scale(0.96)");
        Dispatcher.UIThread.Post(() =>
        {
            root.Opacity = 1;
            root.RenderTransform = TransformOperations.Identity;
        }, DispatcherPriority.Loaded);
    }

    /// <summary>A plain mouse wheel scrolls an overflowing strip sideways.</summary>
    private void OnTabStripWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_tabsScroller is null || Math.Abs(e.Delta.Y) <= Math.Abs(e.Delta.X))
            return;
        var max = Math.Max(0, _tabsScroller.Extent.Width - _tabsScroller.Viewport.Width);
        if (max <= 0)
            return;
        _tabsScroller.Offset = new Vector(Math.Clamp(_tabsScroller.Offset.X - e.Delta.Y * 48, 0, max), 0);
        e.Handled = true;
    }

    /// <summary>Fades the edges an overflowing strip can still scroll towards, so tabs out of view
    /// are hinted at instead of cut off.</summary>
    private void UpdateTabStripFades()
    {
        if (_tabsScroller is null)
            return;
        var width = Math.Round(_tabsScroller.Viewport.Width);
        var max = _tabsScroller.Extent.Width - _tabsScroller.Viewport.Width;
        var left = max > 1 && _tabsScroller.Offset.X > 1;
        var right = max > 1 && _tabsScroller.Offset.X < max - 1;
        if ((left, right, width) == _tabStripFades)
            return;
        _tabStripFades = (left, right, width);
        if (!(left || right) || width <= 0)
        {
            _tabsScroller.OpacityMask = null;
            return;
        }

        var fade = Math.Min(TabStripFadeWidth / width, 0.45);
        _tabsScroller.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(left ? Colors.Transparent : Colors.Black, 0),
                new GradientStop(Colors.Black, fade),
                new GradientStop(Colors.Black, 1 - fade),
                new GradientStop(right ? Colors.Transparent : Colors.Black, 1),
            },
        };
    }

    private void ShowLocation(string? url)
    {
        var location = DescribeLocation(url);
        if (_urlHostRun is not null)
            _urlHostRun.Text = location.Host;
        if (_urlRestRun is not null)
            _urlRestRun.Text = location.Rest;
        if (_urlText is not null)
            ToolTip.SetTip(_urlText, location.Kind == BrowserSiteKind.Blank ? null : url);
        if (_urlIcon is not null && _shownUrlKind != location.Kind)
        {
            _shownUrlKind = location.Kind;
            _urlIcon.Data = location.Kind switch
            {
                BrowserSiteKind.Secure => WorkspaceIcons.Lock,
                BrowserSiteKind.File => WorkspaceIcons.File,
                _ => WorkspaceIcons.Browser,
            };
        }
    }
}
