using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.ViewModels;
using StrataTheme.Animation;

namespace Lumi.Views;

/// <summary>
/// The view side of the new-chat launchpad: it keeps the launchpad observing only while it is on screen, fits it to
/// short windows, composes it in when the welcome state appears, and fits the starters row to the composer, gathering
/// what doesn't fit into the stack whose shelf it unfolds.
/// </summary>
public partial class ChatView
{
    /// <summary>
    /// Below this much room for the whole welcome state the launchpad goes compact: no mark, and four starters.
    /// It is read from the welcome panel, which the compact layout doesn't change, so it can't flip back and forth.
    /// </summary>
    private const double LaunchpadCompactHeight = 680;

    // A starter capsule's chrome around its name (ChatView.axaml): its host's margins (4 + 4), rim (1 + 1), padding
    // (5 + 15), badge (28) and the name's margin (9); a tell's icon (12 + 5) and text margin (6); the divider (1 + 4 + 4);
    // the new starter button (38 + 4 + 4); the stack's rim, padding and count margin (1 + 5 + 13 + 1 + 14), its margins
    // (4 + 4) and its badges, each 16 wide once overlapped.
    private const double StarterChrome = 67;
    private const double StarterNameMax = 150;
    private const double StarterTellIcon = 17;
    private const double StarterTellGap = 6;
    private const double StarterTellMax = 140;
    private const double StarterDivider = 9;
    private const double StarterNewButton = 46;
    private const double StarterStackChrome = 42;
    private const double StarterStackBadge = 16;

    /// <summary>Fades the launchpad's lower edge while more of it lies below, so the cut reads as scrollable.</summary>
    private static readonly IImmutableBrush LaunchpadOverflowMask = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Colors.Black, 0),
            new GradientStop(Colors.Black, 0.9),
            new GradientStop(Colors.Transparent, 1),
        },
    }.ToImmutable();

    private ScrollViewer? _welcomeScroller;
    private Panel? _welcomePanel;
    private StackPanel? _welcomeContent;
    private StackPanel? _composerContainer;
    private Control? _launchpadHero;
    private Control? _launchpadIdeas;
    private Control? _launchpadStarters;
    private Control? _launchpadSetups;
    private Control? _launchpadSetupRow;
    private ItemsControl? _launchpadSetupTiles;
    private Control? _launchpadDraftChip;
    private Button? _launchpadStackButton;
    private Button? _launchpadNewStarterButton;
    private Popup? _launchpadShelf;
    private Control? _launchpadShelfRoot;
    private ItemsControl? _launchpadShelfTiles;
    private TextBlock? _launchpadIdeaReason;
    private Control? _launchpadNeedsYouCard;
    private TextBox? _launchpadPlaceSearch;
    private TextBox? _launchpadStarterName;
    private LaunchpadViewModel? _activeLaunchpad;
    private bool _launchpadFitQueued;

    /// <summary>True while a starter's open card moves to another starter, which mustn't let go of the card's state.</summary>
    private bool _launchpadCardMoving;

    private void InitializeLaunchpad()
    {
        _welcomeScroller = this.FindControl<ScrollViewer>("WelcomeScroller");
        _welcomePanel = this.FindControl<Panel>("WelcomePanel");
        _welcomeContent = this.FindControl<StackPanel>("WelcomeGreeting");
        _composerContainer = this.FindControl<StackPanel>("ComposerContainer");
        _launchpadHero = this.FindControl<Control>("LaunchpadHero");
        _launchpadIdeas = this.FindControl<Control>("LaunchpadIdeas");
        _launchpadStarters = this.FindControl<Control>("WelcomeSuggestions");
        _launchpadSetups = this.FindControl<Control>("LaunchpadSetups");
        _launchpadSetupRow = this.FindControl<Control>("LaunchpadSetupRow");
        _launchpadSetupTiles = this.FindControl<ItemsControl>("LaunchpadSetupTiles");
        _launchpadDraftChip = this.FindControl<Control>("LaunchpadDraftChip");
        _launchpadStackButton = this.FindControl<Button>("LaunchpadStackButton");
        _launchpadNewStarterButton = this.FindControl<Button>("LaunchpadNewStarterButton");
        _launchpadShelf = this.FindControl<Popup>("LaunchpadShelf");
        _launchpadShelfRoot = this.FindControl<Control>("LaunchpadShelfRoot");
        _launchpadShelfTiles = this.FindControl<ItemsControl>("LaunchpadShelfTiles");
        _launchpadIdeaReason = this.FindControl<TextBlock>("LaunchpadIdeaReason");
        _launchpadNeedsYouCard = this.FindControl<Control>("LaunchpadNeedsYouCard");
        _launchpadPlaceSearch = this.FindControl<TextBox>("LaunchpadPlaceSearch");
        _launchpadStarterName = this.FindControl<TextBox>("LaunchpadStarterName");

        if (_composerContainer is not null)
            _composerContainer.SizeChanged += (_, _) => ReserveComposerSpaceForLaunchpad();

        if (_welcomeScroller is not null)
        {
            _welcomeScroller.SizeChanged += (_, _) => UpdateLaunchpadOverflowFade();
            _welcomeScroller.ScrollChanged += (_, _) => UpdateLaunchpadOverflowFade();
        }

        // The panel's own size decides compact: a child's resize fires before the panel's new size is known.
        if (_welcomePanel is not null)
            _welcomePanel.SizeChanged += (_, e) => UpdateLaunchpadFit(e.NewSize.Height);

        // The row is as wide as the composer: a new width fits the starters again.
        if (_launchpadSetups is not null)
            _launchpadSetups.SizeChanged += (_, e) =>
            {
                if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
                    QueueLaunchpadRowFit();
            };

        // A right-click (or the context key) on a starter opens its card, as its ⋯ does.
        _launchpadSetupRow?.AddHandler(ContextRequestedEvent, OnLaunchpadSetupContextRequested);
        _launchpadShelfTiles?.AddHandler(ContextRequestedEvent, OnLaunchpadSetupContextRequested);

        if (_launchpadSetupRow is not null && FlyoutBase.GetAttachedFlyout(_launchpadSetupRow) is PopupFlyoutBase card)
        {
            // The name typed in the card is kept as it closes; the card lets go of its starter once closed. A new
            // starter's card opens on its name, to type it first.
            card.Opened += (_, _) =>
            {
                if (_activeLaunchpad?.Editor.IsNew == true)
                    Dispatcher.UIThread.Post(() => FocusLaunchpadStarterName(selectAll: false), DispatcherPriority.Loaded);
            };
            card.Closing += (_, _) => _activeLaunchpad?.Editor.CommitNameCommand.Execute(null);
            card.Closed += (_, _) =>
            {
                if (!_launchpadCardMoving)
                    _activeLaunchpad?.Editor.Close();
            };
        }

        if (_launchpadShelf is not null)
        {
            _launchpadShelf.Opened += (_, _) => OnLaunchpadShelfOpened();
            _launchpadShelf.Closed += (_, _) => _launchpadStackButton?.Classes.Set("open", false);
        }

        // In the new starter's places, Enter picks the first match, so "type a few letters, Enter" chooses without the
        // mouse, and Escape goes back to its form rather than closing the card.
        _launchpadPlaceSearch?.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (_activeLaunchpad is not { } launchpad)
                return;

            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                launchpad.Editor.SelectFirstPlace();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                launchpad.Editor.BackToFormCommand.Execute(null);
            }
        }, RoutingStrategies.Tunnel);

        // Enter adds a new starter as the card has it, or keeps a kept one's name.
        if (_launchpadStarterName is not null)
        {
            _launchpadStarterName.AddHandler(KeyDownEvent, (_, e) =>
            {
                if (e.Key != Key.Enter || _activeLaunchpad is not { } launchpad)
                    return;

                e.Handled = true;
                if (launchpad.Editor.IsNew)
                    launchpad.Editor.AddCommand.Execute(null);
                else
                    launchpad.Editor.CommitNameCommand.Execute(null);
            }, RoutingStrategies.Tunnel);
            _launchpadStarterName.LostFocus += (_, _) => _activeLaunchpad?.Editor.CommitNameCommand.Execute(null);
        }
    }

    /// <summary>A starter went into the composer: focus it and walk the user through its blanks.</summary>
    private void OnComposerFillInRequested(IReadOnlyList<string> blanks) => _composer?.BeginFillIn(blanks);

    private void OnLaunchpadChatOpening() => CloseLaunchpadCard(_launchpadNeedsYouCard);

    /// <summary>
    /// Closes a launchpad card through its popup, as Escape does, so it fades out as it looked. A flyout's
    /// Hide takes the card out of the window at once, and its exit would play without its styles.
    /// </summary>
    private static void CloseLaunchpadCard(Control? card)
    {
        if (card?.FindLogicalAncestorOfType<Popup>() is { IsOpen: true } popup)
            popup.IsOpen = false;
    }

    private void OnLaunchpadSetupContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is LaunchpadSetupItem setup
            && _activeLaunchpad?.Setups.Contains(setup) == true)
        {
            e.Handled = true;
            setup.EditCommand.Execute(null);
        }
    }

    /// <summary>
    /// Opens a starter's card above it. The card hangs from the row, which outlives the starter's capsule (keeping or
    /// renaming a starter rebuilds the row), at the capsule's place, or the stack's for one on its shelf.
    /// </summary>
    private void OnLaunchpadEditorRequested(LaunchpadSetupItem setup)
    {
        if (_launchpadSetupRow is not { } row)
            return;

        CloseLaunchpadShelf();
        var anchor = setup.IsInRow && _launchpadSetupTiles?.ContainerFromItem(setup) is Control container
            ? container.GetVisualDescendants().OfType<Panel>().FirstOrDefault(static host => host.Classes.Contains("launchpad-setup-host"))
              ?? container
            : _launchpadStackButton;
        // The card's glass keeps a 16px margin for its shadow; its rim lines up with the capsule's.
        ShowLaunchpadStarterCard(PlacementMode.TopEdgeAlignedLeft, (anchor?.TranslatePoint(default, row)?.X ?? 0) - 16);
    }

    /// <summary>The + opens the new starter card above the row's end, its rim in line with the +.</summary>
    private void OnLaunchpadNewStarterClick(object? sender, RoutedEventArgs e) => OpenLaunchpadNewStarterCard(_launchpadNewStarterButton);

    /// <summary>Opens the new starter card filled from the draft, right-aligned with <paramref name="anchor"/> at the row's end.</summary>
    private void OpenLaunchpadNewStarterCard(Control? anchor)
    {
        if (_activeLaunchpad is not { } launchpad || _launchpadSetupRow is not { } row)
            return;

        CloseLaunchpadShelf();
        launchpad.OpenNewStarterCommand.Execute(null);
        var right = anchor is { IsEffectivelyVisible: true } && anchor.TranslatePoint(new Point(anchor.Bounds.Width, 0), row) is { } end
            ? end.X
            : row.Bounds.Width;
        ShowLaunchpadStarterCard(PlacementMode.TopEdgeAlignedRight, right - row.Bounds.Width + 16);
    }

    /// <summary>Shows the starter's card from the row, moving it if it is open for another starter.</summary>
    private void ShowLaunchpadStarterCard(PlacementMode placement, double horizontalOffset)
    {
        if (_launchpadSetupRow is not { } row || FlyoutBase.GetAttachedFlyout(row) is not PopupFlyoutBase card)
            return;

        card.Placement = placement;
        card.HorizontalOffset = horizontalOffset;
        if (card.IsOpen)
        {
            _launchpadCardMoving = true;
            try
            {
                card.Hide();
            }
            finally
            {
                _launchpadCardMoving = false;
            }
        }

        FlyoutBase.ShowAttachedFlyout(row);
    }

    /// <summary>
    /// The card closes once its starter is gone (removed, no longer suggested, or a new one added). A new starter's places
    /// take the keyboard to their search as they open, and back to its name as they close.
    /// </summary>
    private void OnLaunchpadEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_activeLaunchpad?.Editor is not { } editor)
            return;

        if (e.PropertyName == nameof(LaunchpadStarterEditor.Target) && editor.Target is null)
        {
            CloseLaunchpadCard(this.FindControl<Control>("LaunchpadStarterCard"));
        }
        else if (e.PropertyName == nameof(LaunchpadStarterEditor.IsChoosingPlace) && editor.IsNew)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (editor.IsChoosingPlace)
                    _launchpadPlaceSearch?.Focus();
                else
                    FocusLaunchpadStarterName(selectAll: false);
            }, DispatcherPriority.Loaded);
        }
    }

    /// <summary>Rename keeps a learned starter and puts its name in the card's field, selected, to type over.</summary>
    private void OnLaunchpadEditorNameFocusRequested()
        => Dispatcher.UIThread.Post(() => FocusLaunchpadStarterName(selectAll: true), DispatcherPriority.Loaded);

    private void FocusLaunchpadStarterName(bool selectAll)
    {
        if (_launchpadStarterName is not { IsEffectivelyVisible: true } name)
            return;

        name.Focus();
        if (selectAll)
            name.SelectAll();
        else
            name.CaretIndex = name.Text?.Length ?? 0;
    }

    private void OnLaunchpadStackClick(object? sender, RoutedEventArgs e)
    {
        if (_launchpadShelf is not { } shelf || _activeLaunchpad is not { } launchpad)
            return;

        if (shelf.IsOpen)
        {
            shelf.IsOpen = false;
            return;
        }

        launchpad.OpenShelf();
        if (_launchpadShelfRoot is { } root && _launchpadSetups is { Bounds.Width: > 0 } setups)
            root.MaxWidth = setups.Bounds.Width;
        _launchpadStackButton?.Classes.Set("open", true);
        shelf.IsOpen = true;
    }

    /// <summary>The starters fold out of the stack, the nearest first.</summary>
    private void OnLaunchpadShelfOpened()
    {
        if (_launchpadShelfTiles is not { } tiles || _subscribedVm is not { AreAnimationsEnabled: true })
            return;

        Dispatcher.UIThread.Post(() =>
        {
            var delay = 0;
            foreach (var tile in tiles.GetRealizedContainers())
            {
                SlideFadeEntrance.Play(tile, offsetY: 8, duration: TimeSpan.FromMilliseconds(220),
                    delay: TimeSpan.FromMilliseconds(delay));
                delay += 22;
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Picking a starter on the shelf folds it back.</summary>
    private void OnLaunchpadShelfStarterClick(object? sender, RoutedEventArgs e)
        => Dispatcher.UIThread.Post(CloseLaunchpadShelf, DispatcherPriority.Background);

    /// <summary>The shelf's + opens the new starter card where the stack is.</summary>
    private void OnLaunchpadShelfNewStarterClick(object? sender, RoutedEventArgs e)
    {
        CloseLaunchpadShelf();
        Dispatcher.UIThread.Post(() => OpenLaunchpadNewStarterCard(_launchpadStackButton), DispatcherPriority.Background);
    }

    private void CloseLaunchpadShelf()
    {
        if (_launchpadShelf is { IsOpen: true } shelf)
            shelf.IsOpen = false;
    }

    /// <summary>Another tab's starters rise in one after another, so switching tabs reads as the capsules changing rather than a blink.</summary>
    private void OnLaunchpadPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // The draft's starter may take a learned starter's place on the row, and its dashed chip takes room of its own.
            case nameof(LaunchpadViewModel.ActiveSetup):
            case nameof(LaunchpadViewModel.HasUnsavedDraft):
            case nameof(LaunchpadViewModel.DraftPlace):
                QueueLaunchpadRowFit();
                return;
            case nameof(LaunchpadViewModel.SelectedCategory):
                break;
            default:
                return;
        }

        if (_launchpadStarters is not ItemsControl starters || _subscribedVm is not { AreAnimationsEnabled: true })
            return;

        _launchpadIdeaReason?.Classes.Set("shown", false);
        // The capsules are made at the next layout pass; stagger them once they are.
        Dispatcher.UIThread.Post(() =>
        {
            var delay = 0;
            foreach (var capsule in starters.GetRealizedContainers())
            {
                SlideFadeEntrance.Play(capsule, offsetY: 6, duration: TimeSpan.FromMilliseconds(240),
                    delay: TimeSpan.FromMilliseconds(delay));
                delay += 24;
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnLaunchpadRowChanged() => QueueLaunchpadRowFit();

    private void OnLaunchpadSetupsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueLaunchpadRowFit();

    private void QueueLaunchpadRowFit()
    {
        if (_launchpadFitQueued || _activeLaunchpad is null)
            return;

        _launchpadFitQueued = true;
        Dispatcher.UIThread.Post(FitLaunchpadRow, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Shows on the row as many starters as fit the composer's width, in order, and the draft's always; the rest gather
    /// into the stack. Widths come from each starter's words, so a starter hidden in the stack is measured too.
    /// </summary>
    private void FitLaunchpadRow()
    {
        _launchpadFitQueued = false;
        if (_activeLaunchpad is not { } launchpad || _launchpadSetups is not { Bounds.Width: > 0 } setups)
            return;

        var items = launchpad.Setups;
        var widths = new double[items.Count];
        var kept = new bool[items.Count];
        for (var index = 0; index < items.Count; index++)
        {
            widths[index] = MeasureStarter(items[index]);
            kept[index] = items[index].IsKept;
        }

        var draft = launchpad.HasUnsavedDraft && _launchpadDraftChip is { } chip
            ? chip.DesiredSize.Width + chip.Margin.Left + chip.Margin.Right
            : 0;
        var shown = LaunchpadPlanner.FitRow(
            widths,
            kept,
            setups.Bounds.Width,
            draft,
            StarterNewButton,
            StackWidth(items.Count),
            StarterDivider,
            launchpad.ActiveSetup is { } active ? items.IndexOf(active) : -1);
        launchpad.ApplyRowFit(shown);
    }

    private double MeasureStarter(LaunchpadSetupItem setup)
    {
        var width = StarterChrome + Math.Min(StarterNameMax, MeasureText(setup.Title, 13, FontWeight.SemiBold));
        if (setup.TellsWorktree)
            width += StarterTellIcon;
        if (setup.HasTellText)
            width += StarterTellGap + Math.Min(StarterTellMax, MeasureText(setup.TellText, 12, FontWeight.Normal));
        return width;
    }

    /// <summary>The stack's width at most: three badges and the count of every starter.</summary>
    private double StackWidth(int count)
        => StarterStackChrome + StarterStackBadge * Math.Min(3, count) + MeasureText("+" + count, 12.5, FontWeight.SemiBold);

    private double MeasureText(string text, double size, FontWeight weight)
    {
        using var layout = new TextLayout(text, new Typeface(FontFamily, FontStyle.Normal, weight), size, null);
        return Math.Ceiling(layout.WidthIncludingTrailingWhitespace);
    }

    /// <summary>
    /// Says why a For you starter is offered while the pointer rests on it, on one line under the starters: the
    /// capsules themselves carry no tooltips, since they change with the tab under the pointer.
    /// </summary>
    private void OnLaunchpadIdeaPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_launchpadIdeaReason is null)
            return;

        if (sender is Control { DataContext: LaunchpadStarterItem { HasReason: true } starter })
        {
            _launchpadIdeaReason.Text = starter.Reason;
            _launchpadIdeaReason.Classes.Set("shown", true);
        }
        else
        {
            _launchpadIdeaReason.Classes.Set("shown", false);
        }
    }

    private void OnLaunchpadIdeaPointerExited(object? sender, PointerEventArgs e)
        => _launchpadIdeaReason?.Classes.Set("shown", false);

    /// <summary>Keeps the launchpad in the space above the composer and the starters on it, whatever their height.</summary>
    private void ReserveComposerSpaceForLaunchpad()
    {
        // An open chat never sees the launchpad, so its composer animating must not relayout it.
        if (_welcomeScroller is null || _composerContainer is null || _activeLaunchpad is null)
            return;

        var reserve = _composerContainer.Bounds.Height + _composerContainer.Margin.Bottom + 12;
        _welcomeScroller.Margin = new Thickness(0, 0, 0, Math.Max(reserve, 120));
    }

    private void UpdateLaunchpadFit(double? panelHeight = null)
    {
        if (_welcomeScroller is null || _welcomeContent is null || _activeLaunchpad is null)
            return;

        var compact = (panelHeight ?? _welcomePanel?.Bounds.Height ?? _welcomeScroller.Bounds.Height) < LaunchpadCompactHeight;
        _welcomeContent.Classes.Set("compact", compact);
        _launchpadIdeas?.Classes.Set("compact", compact);
        UpdateLaunchpadOverflowFade();
    }

    private void UpdateLaunchpadOverflowFade()
    {
        if (_welcomeScroller is not { } scroller)
            return;

        var moreBelow = scroller.Extent.Height - scroller.Viewport.Height - scroller.Offset.Y > 1;
        scroller.OpacityMask = moreBelow ? LaunchpadOverflowMask : null;
    }

    /// <summary>
    /// Points the launchpad subscription at the surface this view shows in its new-chat state, so a
    /// cached draft or an open chat never keeps watching chats and the clock.
    /// </summary>
    private void UpdateLaunchpadActivation()
    {
        var target = this.IsAttachedToVisualTree() && _subscribedVm is { IsWelcomeVisible: true } vm
            ? vm.Launchpad
            : null;
        if (ReferenceEquals(target, _activeLaunchpad))
            return;

        DeactivateLaunchpad();
        _activeLaunchpad = target;
        if (target is null)
            return;

        target.ChatOpening += OnLaunchpadChatOpening;
        target.EditorRequested += OnLaunchpadEditorRequested;
        target.PropertyChanged += OnLaunchpadPropertyChanged;
        target.RowChanged += OnLaunchpadRowChanged;
        target.Setups.CollectionChanged += OnLaunchpadSetupsChanged;
        target.Editor.PropertyChanged += OnLaunchpadEditorPropertyChanged;
        target.Editor.NameFocusRequested += OnLaunchpadEditorNameFocusRequested;
        target.Activate();
        ReserveComposerSpaceForLaunchpad();
        UpdateLaunchpadFit();
        QueueLaunchpadRowFit();
        Dispatcher.UIThread.Post(PlayLaunchpadEntrance, DispatcherPriority.Loaded);
    }

    private void DeactivateLaunchpad()
    {
        CloseLaunchpadShelf();
        CloseLaunchpadCard(_launchpadNeedsYouCard);
        CloseLaunchpadCard(this.FindControl<Control>("LaunchpadStarterCard"));
        if (_activeLaunchpad is { } launchpad)
        {
            launchpad.ChatOpening -= OnLaunchpadChatOpening;
            launchpad.EditorRequested -= OnLaunchpadEditorRequested;
            launchpad.PropertyChanged -= OnLaunchpadPropertyChanged;
            launchpad.RowChanged -= OnLaunchpadRowChanged;
            launchpad.Setups.CollectionChanged -= OnLaunchpadSetupsChanged;
            launchpad.Editor.PropertyChanged -= OnLaunchpadEditorPropertyChanged;
            launchpad.Editor.NameFocusRequested -= OnLaunchpadEditorNameFocusRequested;
            launchpad.Deactivate();
        }

        _activeLaunchpad = null;
    }

    /// <summary>Composes the launchpad in: the greeting, then what to start, rise in turn; the starters settle onto the composer.</summary>
    private void PlayLaunchpadEntrance()
    {
        if (_subscribedVm is not { IsWelcomeVisible: true, AreAnimationsEnabled: true })
            return;

        var delay = 0;
        foreach (var host in new[] { _launchpadHero, _launchpadIdeas })
        {
            if (host is null)
                continue;

            SlideFadeEntrance.Play(
                host,
                offsetY: 10,
                duration: TimeSpan.FromMilliseconds(340),
                delay: TimeSpan.FromMilliseconds(delay));
            delay += 80;
        }

        if (_launchpadSetupRow is not null)
        {
            SlideFadeEntrance.Play(
                _launchpadSetupRow,
                offsetY: 6,
                duration: TimeSpan.FromMilliseconds(300),
                delay: TimeSpan.FromMilliseconds(120));
        }
    }
}
