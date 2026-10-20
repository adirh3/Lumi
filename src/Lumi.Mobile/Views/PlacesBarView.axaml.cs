using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Lumi.Mobile.ViewModels;

namespace Lumi.Mobile.Views;

/// <summary>
/// The places pill shared by every place's list: Chats, Jobs, Customize and Settings.
///
/// <para>Moving to another place glides one highlight over to it while the place left behind folds its
/// name away and the new one slides its name open, as the desktop's bar does. Each place's list has its
/// own bar at the same spot, and a page's bar is first built when the page first shows, so the movement
/// belongs to the window rather than to a bar: every bar draws the same glide, and a bar that comes into
/// view partway through joins it where it is. The glide is computed from the places' own metrics rather
/// than read back from layout, which a hidden bar does not run.</para>
/// </summary>
public partial class PlacesBarView : UserControl
{
    private const int PlaceCount = 4;

    // The open place's name sits 8 dp after its icon and 2 dp before the end of its target.
    private const double NameLead = 8;
    private const double NameTrail = 2;

    /// <summary>How long the highlight takes to glide to another place.</summary>
    internal static readonly TimeSpan GlideDuration = TimeSpan.FromMilliseconds(380);

    private static readonly ConditionalWeakTable<TopLevel, PlacesMotion> Motions = new();

    private readonly Button[] _places;
    private readonly Control[] _icons;
    private readonly TextBlock[] _names;
    private readonly TranslateTransform[] _nameOffsets;
    private readonly StackPanel _row;
    private readonly Border _selection;
    private readonly TranslateTransform _selectionOffset = new();
    private readonly double[] _openness = new double[PlaceCount];
    private readonly double[] _nameWidths = new double[PlaceCount];
    private MobileShellViewModel? _shell;
    private PlacesMotion? _motion;
    private TopLevel? _topLevel;
    private int _frameVersion;
    private bool _isGliding;

    public PlacesBarView()
    {
        InitializeComponent();
        _row = this.FindControl<StackPanel>("PlacesRow")!;
        _selection = this.FindControl<Border>("PlacesSelection")!;
        _selection.RenderTransform = _selectionOffset;
        _places = new[] { "PlacesChatsButton", "PlacesJobsButton", "PlacesCustomizeButton", "PlacesSettingsButton" }
            .Select(name => this.FindControl<Button>(name)!)
            .ToArray();
        var contents = _places.Select(place => (StackPanel)place.Content!).ToArray();
        _icons = contents.Select(content => content.Children[0]).ToArray();
        _names = contents.Select(content => (TextBlock)content.Children[1]).ToArray();
        _nameOffsets = _names.Select(name =>
        {
            var offset = new TranslateTransform();
            name.RenderTransform = offset;
            name.PropertyChanged += OnNamePropertyChanged;
            return offset;
        }).ToArray();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>True while the highlight is on its way to another place.</summary>
    internal bool IsGliding => _isGliding;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _motion = _topLevel is null ? null : Motions.GetValue(_topLevel, _ => new PlacesMotion());
        Follow(DataContext as MobileShellViewModel);
        Join();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Follow(null);
        _frameVersion++;
        _isGliding = false;
        _motion = null;
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_motion is null)
            return;
        Follow(DataContext as MobileShellViewModel);
        Join();
    }

    private void Follow(MobileShellViewModel? shell)
    {
        if (ReferenceEquals(shell, _shell))
            return;
        if (_shell is not null)
            _shell.PropertyChanged -= OnShellPropertyChanged;
        _shell = shell;
        if (_shell is not null)
            _shell.PropertyChanged += OnShellPropertyChanged;
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MobileShellViewModel.Place))
            return;
        if (Dispatcher.UIThread.CheckAccess())
            MoveToPlace();
        else
            Dispatcher.UIThread.Post(MoveToPlace);
    }

    // A new type size changes how much room the open place's name needs.
    private void OnNamePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != TextBlock.FontSizeProperty
            && e.Property != TextBlock.FontFamilyProperty
            && e.Property != TextBlock.FontWeightProperty)
            return;

        MeasureNames();
        if (!_isGliding)
            Draw();
    }

    /// <summary>The place moved: start the window's glide to it, or follow one another bar started.</summary>
    private void MoveToPlace()
    {
        if (_motion is not { } motion || _shell is null)
            return;

        var place = (int)_shell.Place;
        if (motion.Target != place)
        {
            MeasureNames();
            if (motion.Target < 0)
            {
                motion.Rest(place);
            }
            else
            {
                // From wherever the window's bars are now, even partway through another glide.
                StateAt(motion, out var selection);
                motion.Begin(place, _openness, selection);
            }
        }

        Join();
    }

    /// <summary>Shows the window's movement as it is now and follows it to its end.</summary>
    private void Join()
    {
        if (_motion is not { } motion || _shell is null || _topLevel is not { } topLevel)
            return;

        var place = (int)_shell.Place;
        if (motion.Target != place)
            motion.Rest(place);

        MeasureNames();
        Draw();
        if (!motion.IsMoving || _isGliding)
            return;

        _isGliding = true;
        var version = ++_frameVersion;
        topLevel.RequestAnimationFrame(time => OnFrame(topLevel, version, time));
    }

    private void OnFrame(TopLevel topLevel, int version, TimeSpan time)
    {
        if (version != _frameVersion || _motion is not { } motion)
            return;

        // Every bar drawing this frame passes the same frame time, so they all draw the same moment.
        motion.Frame(time);
        Draw();
        if (motion.IsMoving)
            topLevel.RequestAnimationFrame(next => OnFrame(topLevel, version, next));
        else
            _isGliding = false;
    }

    /// <summary>Draws the window's movement as it stands this moment.</summary>
    private void Draw()
    {
        if (_motion is not { } motion)
            return;

        StateAt(motion, out var selection);
        for (var i = 0; i < PlaceCount; i++)
        {
            var openness = _openness[i];
            var name = _names[i];
            name.MaxWidth = _nameWidths[i] * openness;
            name.Margin = new Thickness(NameLead * openness, 0, NameTrail * openness, 0);
            // The name follows the room opening for it: it fades in once there is space and drifts out of the icon.
            name.Opacity = Math.Clamp((openness - 0.35) / 0.65, 0, 1);
            _nameOffsets[i].X = -4 * (1 - openness);
        }

        _selection.IsVisible = motion.Target >= 0;
        _selection.Width = Math.Max(0, selection.Width);
        _selectionOffset.X = selection.X;
    }

    /// <summary>The names' openness (into <see cref="_openness"/>) and the highlight's place as of the last frame.</summary>
    private void StateAt(PlacesMotion motion, out (double X, double Width) selection)
    {
        var eased = Ease(motion.Progress);
        for (var i = 0; i < PlaceCount; i++)
            _openness[i] = Lerp(motion.FromOpenness[i], i == motion.Target ? 1 : 0, eased);

        // The highlight heads for where the new place sits in this frame, as the names around it move.
        var target = SelectionFor(motion.Target);
        selection = motion.IsAtRest
            ? target
            : (Lerp(motion.FromSelection.X, target.X, eased), Lerp(motion.FromSelection.Width, target.Width, eased));
    }

    /// <summary>Where the place's target sits in the row for the names' current openness.</summary>
    private (double X, double Width) SelectionFor(int index)
    {
        if (index < 0)
            return default;

        var x = 0d;
        for (var i = 0; i < index; i++)
            x += PlaceWidth(i) + _row.Spacing;
        return (x, PlaceWidth(index));
    }

    private double PlaceWidth(int index)
    {
        var place = _places[index];
        var icon = _icons[index];
        var iconWidth = double.IsNaN(icon.Width) ? icon.DesiredSize.Width : icon.Width;
        var closed = Math.Max(place.MinWidth, place.Padding.Left + place.Padding.Right + iconWidth);
        return closed + (NameLead + NameTrail + _nameWidths[index]) * _openness[index];
    }

    private void MeasureNames()
    {
        for (var i = 0; i < PlaceCount; i++)
        {
            var name = _names[i];
            var probe = new TextBlock
            {
                Text = name.Text,
                FontFamily = name.FontFamily,
                FontSize = name.FontSize,
                FontStyle = name.FontStyle,
                FontWeight = name.FontWeight,
                FontStretch = name.FontStretch,
                LetterSpacing = name.LetterSpacing,
                TextWrapping = TextWrapping.NoWrap,
            };
            probe.Measure(Size.Infinity);
            _nameWidths[i] = Math.Ceiling(probe.DesiredSize.Width);
        }
    }

    // The desktop's ease: quick to leave, landing softly on the new place.
    private static double Ease(double progress) => 1 - Math.Pow(1 - progress, 3);

    private static double Lerp(double from, double to, double amount) => from + (to - from) * amount;

    /// <summary>The places' movement in one window, shared by every bar in it.</summary>
    private sealed class PlacesMotion
    {
        // The glide eases in over its first frames however long they take: the page being moved to builds
        // the first time it shows, right after the glide's first frame, and the highlight would otherwise
        // jump that stall's worth of its way. Afterwards no frame moves it on by more than two frames' worth.
        private const int EasedInFrames = 3;
        private static readonly TimeSpan EasedInStep = TimeSpan.FromMilliseconds(8);
        private static readonly TimeSpan MaxFrameStep = TimeSpan.FromMilliseconds(34);

        private TimeSpan _elapsed;
        private TimeSpan _lastFrame;
        private int _frames = -1;

        public int Target { get; private set; } = -1;

        public double[] FromOpenness { get; } = new double[PlaceCount];

        public (double X, double Width) FromSelection { get; private set; }

        public bool IsAtRest { get; private set; } = true;

        /// <summary>How far the glide has come as of the last frame, so a bar drawn between frames shows what the others show.</summary>
        public double Progress => IsAtRest ? 1 : Math.Clamp(_elapsed / GlideDuration, 0, 1);

        public bool IsMoving => Progress < 1;

        public void Rest(int place)
        {
            Target = place;
            IsAtRest = true;
            for (var i = 0; i < PlaceCount; i++)
                FromOpenness[i] = i == place ? 1 : 0;
        }

        public void Begin(int place, double[] openness, (double X, double Width) selection)
        {
            Array.Copy(openness, FromOpenness, PlaceCount);
            FromSelection = selection;
            Target = place;
            IsAtRest = false;
            _elapsed = TimeSpan.Zero;
            _frames = -1;
        }

        /// <summary>A frame is drawing at <paramref name="time"/>; bars drawing the same frame pass the same time.</summary>
        public void Frame(TimeSpan time)
        {
            if (IsAtRest)
                return;
            if (_frames < 0)
            {
                // The glide sets off with the first frame drawn, as the desktop's animations do.
                _frames = 0;
                _lastFrame = time;
                return;
            }

            var step = time - _lastFrame;
            if (step <= TimeSpan.Zero)
                return;
            var limit = _frames < EasedInFrames ? EasedInStep : MaxFrameStep;
            _elapsed += step < limit ? step : limit;
            _lastFrame = time;
            _frames++;
        }
    }
}