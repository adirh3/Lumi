using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using StrataTheme.Controls;

namespace Lumi.Mobile.Behaviors;

public sealed class NativeTextInputOverlay
{
    internal readonly record struct Placement(Rect Bounds, Rect ClipBounds);

    private static readonly ConditionalWeakTable<TextBox, Controller> Controllers = new();

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<NativeTextInputOverlay, TextBox, bool>("IsEnabled");

    public static readonly AttachedProperty<bool> ForwardEnterKeyProperty =
        AvaloniaProperty.RegisterAttached<NativeTextInputOverlay, TextBox, bool>("ForwardEnterKey");

    static NativeTextInputOverlay()
    {
        IsEnabledProperty.Changed.AddClassHandler<TextBox>((textBox, change) =>
        {
            if (change.GetNewValue<bool>())
                Attach(textBox);
            else
                Detach(textBox);
        });
    }

    private NativeTextInputOverlay()
    {
    }

    public static bool GetIsEnabled(TextBox textBox) => textBox.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(TextBox textBox, bool value) =>
        textBox.SetValue(IsEnabledProperty, value);

    public static bool GetForwardEnterKey(TextBox textBox) =>
        textBox.GetValue(ForwardEnterKeyProperty);

    public static void SetForwardEnterKey(TextBox textBox, bool value) =>
        textBox.SetValue(ForwardEnterKeyProperty, value);

    public static bool TryFocus(TextBox textBox)
    {
        if (!Controllers.TryGetValue(textBox, out var controller))
            return false;

        return controller.TryFocus();
    }

    public static void Blur(TextBox textBox)
    {
        if (Controllers.TryGetValue(textBox, out var controller))
            controller.Blur();
    }

    internal static Placement? GetVisiblePlacement(Control control, TopLevel topLevel)
    {
        if (IsOccludedByModal(control, topLevel))
            return null;

        if (GetPhysicalBounds(control, topLevel) is not { } bounds)
            return null;

        Rect? visible = Intersect(
            bounds,
            new Rect(topLevel.Bounds.Size));
        if (visible is null)
            return null;

        foreach (var ancestor in control.GetVisualAncestors().OfType<Control>())
        {
            if (ReferenceEquals(ancestor, topLevel)
                || ancestor is not ScrollViewer && !ancestor.ClipToBounds
                || GetPhysicalBounds(ancestor, topLevel) is not { } ancestorBounds)
            {
                continue;
            }

            visible = Intersect(
                visible.Value,
                ancestorBounds);
            if (visible is null)
                return null;
        }

        return new Placement(bounds, visible.Value);
    }

    private static Rect? GetPhysicalBounds(Control control, TopLevel topLevel)
    {
        var width = control.Bounds.Width;
        var height = control.Bounds.Height;
        if (control.TransformToVisual(topLevel) is not { } transform)
            return null;

        var topLeft = transform.Transform(default);
        var topRight = transform.Transform(new Point(width, 0));
        var bottomLeft = transform.Transform(new Point(0, height));
        var bottomRight = transform.Transform(new Point(width, height));
        var minX = Math.Min(Math.Min(topLeft.X, topRight.X),
            Math.Min(bottomLeft.X, bottomRight.X));
        var minY = Math.Min(Math.Min(topLeft.Y, topRight.Y),
            Math.Min(bottomLeft.Y, bottomRight.Y));
        var maxX = Math.Max(Math.Max(topLeft.X, topRight.X),
            Math.Max(bottomLeft.X, bottomRight.X));
        var maxY = Math.Max(Math.Max(topLeft.Y, topRight.Y),
            Math.Max(bottomLeft.Y, bottomRight.Y));
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static bool IsOccludedByModal(Control control, TopLevel topLevel)
    {
        var ancestors = control.GetVisualAncestors().ToArray();
        var shell = ancestors.OfType<MobileShellView>().FirstOrDefault()?.DataContext
            as MobileShellViewModel;
        if (shell?.HasPageOverlay == true && ancestors.OfType<ChatDetailView>().Any())
            return true;

        var presentedSheets = topLevel.GetVisualDescendants()
            .OfType<StrataBottomSheet>()
            .Where(sheet => (sheet.IsOpen || sheet.IsPresented) && sheet.IsEffectivelyVisible)
            .ToArray();
        if (presentedSheets.Length > 0)
            return !presentedSheets.Any(sheet => ancestors.Any(ancestor => ReferenceEquals(ancestor, sheet)));

        return shell is { IsDrawerOverlay: true } or { IsNavigationCoveringContent: true };
    }

    private static Rect? Intersect(Rect left, Rect right)
    {
        var x = Math.Max(left.X, right.X);
        var y = Math.Max(left.Y, right.Y);
        var farX = Math.Min(left.Right, right.Right);
        var farY = Math.Min(left.Bottom, right.Bottom);
        return farX > x && farY > y
            ? new Rect(x, y, farX - x, farY - y)
            : null;
    }

    private static void Attach(TextBox textBox) =>
        Controllers.GetValue(textBox, static control => new Controller(control));

    private static void Detach(TextBox textBox)
    {
        if (Controllers.TryGetValue(textBox, out var controller))
            controller.Dispose();
        Controllers.Remove(textBox);
    }

    private sealed class Controller : IDisposable
    {
        private readonly TextBox _textBox;
        private readonly List<ScrollViewer> _scrollViewers = [];
        private INativeTextInputOverlaySession? _session;
        private bool _isAttached;
        private bool _hasNativeFocus;
        private bool _isApplyingNativeText;
        private bool _disposed;

        public Controller(TextBox textBox)
        {
            _textBox = textBox;
            _textBox.AttachedToVisualTree += OnAttachedToVisualTree;
            _textBox.DetachedFromVisualTree += OnDetachedFromVisualTree;
            _textBox.LayoutUpdated += OnLayoutUpdated;
            _textBox.PropertyChanged += OnTextBoxPropertyChanged;
            _textBox.GotFocus += OnGotFocus;
            _textBox.ActualThemeVariantChanged += OnThemeChanged;
            if (_textBox.IsAttachedToVisualTree())
                AttachSession();
        }

        public bool TryFocus()
        {
            if (_session is null)
                return false;

            _session.FocusAt(_textBox.Text?.Length ?? 0);
            return true;
        }

        public void Blur()
        {
            _hasNativeFocus = false;
            _textBox.Classes.Set("native-input-focused", false);
            _session?.Blur();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _textBox.AttachedToVisualTree -= OnAttachedToVisualTree;
            _textBox.DetachedFromVisualTree -= OnDetachedFromVisualTree;
            _textBox.LayoutUpdated -= OnLayoutUpdated;
            _textBox.PropertyChanged -= OnTextBoxPropertyChanged;
            _textBox.GotFocus -= OnGotFocus;
            _textBox.ActualThemeVariantChanged -= OnThemeChanged;
            DetachSession();
        }

        private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e) =>
            AttachSession();

        private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e) =>
            DetachSession();

        private void AttachSession()
        {
            if (_disposed || _isAttached)
                return;

            _isAttached = true;
            var presenter = MobilePlatformServices.TextInputOverlayPresenter;
            if (!presenter.IsAvailable)
                return;

            _textBox.Classes.Set("native-input-overlay", true);
            _session = presenter.Create(
                OnNativeTextChanged,
                OnNativeKeyPressed,
                OnNativeFocusChanged);
            foreach (var scrollViewer in _textBox.GetVisualAncestors().OfType<ScrollViewer>())
            {
                scrollViewer.ScrollChanged += OnScrollChanged;
                _scrollViewers.Add(scrollViewer);
            }
            UpdateSession();
            if (_textBox.IsFocused)
                _session.FocusAt(_textBox.CaretIndex);
        }

        private void DetachSession()
        {
            if (!_isAttached)
                return;

            _isAttached = false;
            foreach (var scrollViewer in _scrollViewers)
                scrollViewer.ScrollChanged -= OnScrollChanged;
            _scrollViewers.Clear();
            _session?.Dispose();
            _session = null;
            _hasNativeFocus = false;
            _textBox.Classes.Set("native-input-overlay", false);
            _textBox.Classes.Set("native-input-focused", false);
        }

        private void OnLayoutUpdated(object? sender, EventArgs e) => UpdateSession();

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => UpdateSession();

        private void OnThemeChanged(object? sender, EventArgs e) => UpdateSession();

        private void OnGotFocus(object? sender, FocusChangedEventArgs e)
        {
            if (_session is not null)
                _session.FocusAt(_textBox.CaretIndex);
        }

        private void OnTextBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TextBox.CaretIndexProperty)
            {
                if (!_isApplyingNativeText
                    && _session is not null
                    && (_hasNativeFocus || _textBox.IsFocused))
                {
                    _session.FocusAt(_textBox.CaretIndex);
                }
                return;
            }

            if (e.Property == TextBox.TextProperty
                || e.Property == TextBox.PlaceholderTextProperty
                || e.Property == TextBox.AcceptsReturnProperty
                || e.Property == TextBox.MaxLengthProperty
                || e.Property == TextBox.IsReadOnlyProperty
                || e.Property == InputElement.IsEnabledProperty
                || e.Property == Visual.IsVisibleProperty)
            {
                UpdateSession();
            }
        }

        private void UpdateSession()
        {
            if (_session is null)
                return;

            var topLevel = TopLevel.GetTopLevel(_textBox);
            if (!_textBox.IsEffectivelyVisible
                || topLevel is null
                || _textBox.Bounds.Width <= 0
                || _textBox.Bounds.Height <= 0)
            {
                _session.Hide();
                return;
            }

            var placement = GetVisiblePlacement(_textBox, topLevel);
            if (placement is null)
            {
                _session.Hide();
                return;
            }

            _session.Show(
                placement.Value.Bounds,
                placement.Value.ClipBounds,
                _textBox.Text ?? "",
                new NativeTextInputOverlayOptions(
                    _textBox.AcceptsReturn,
                    _textBox.MaxLength,
                    _textBox.PlaceholderText ?? "",
                    GetInputMode(_textBox),
                    GetEnterKeyHint(_textBox),
                    TextInputOptions.GetIsSensitive(_textBox),
                    TextInputOptions.GetAutoCapitalization(_textBox),
                    TextInputOptions.GetShowSuggestions(_textBox) ?? true,
                    _textBox.IsEnabled && !_textBox.IsReadOnly,
                    _textBox.FontFamily.Name,
                    _textBox.FontSize,
                    (int)_textBox.FontWeight,
                    _textBox.FontStyle.ToString().ToLowerInvariant(),
                    double.IsNaN(_textBox.LineHeight) ? 0 : _textBox.LineHeight,
                    _textBox.LetterSpacing,
                    _textBox.Padding,
                    _textBox.TextAlignment.ToString().ToLowerInvariant(),
                    _textBox.FlowDirection == FlowDirection.RightToLeft ? "rtl" : "ltr",
                    _textBox.ActualThemeVariant == ThemeVariant.Dark));
        }

        private void OnNativeTextChanged(string value, int caretIndex)
        {
            _isApplyingNativeText = true;
            try
            {
                if (!string.Equals(_textBox.Text, value, StringComparison.Ordinal))
                    _textBox.SetCurrentValue(TextBox.TextProperty, value);
                _textBox.CaretIndex = Math.Clamp(caretIndex, 0, value.Length);
            }
            finally
            {
                _isApplyingNativeText = false;
            }
        }

        private bool OnNativeKeyPressed(Key key, KeyModifiers modifiers)
        {
            if (key == Key.Enter
                && _textBox.AcceptsReturn
                && (!GetForwardEnterKey(_textBox)
                    || modifiers.HasFlag(KeyModifiers.Shift)))
            {
                return false;
            }

            var args = new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = key,
                KeyModifiers = modifiers
            };
            _textBox.RaiseEvent(args);
            return args.Handled;
        }

        private void OnNativeFocusChanged(bool focused)
        {
            _hasNativeFocus = focused;
            _textBox.Classes.Set("native-input-focused", focused);
            if (!focused && _session is not null)
                _textBox.CaretIndex = Math.Clamp(_session.CaretIndex, 0, _textBox.Text?.Length ?? 0);
        }

        private static string GetInputMode(TextBox textBox) =>
            TextInputOptions.GetContentType(textBox) switch
            {
                TextInputContentType.Digits or
                TextInputContentType.Pin or
                TextInputContentType.Number => "numeric",
                TextInputContentType.Email => "email",
                TextInputContentType.Url => "url",
                TextInputContentType.Search => "search",
                _ => "text"
            };

        private static string GetEnterKeyHint(TextBox textBox) =>
            TextInputOptions.GetReturnKeyType(textBox) switch
            {
                TextInputReturnKeyType.Done => "done",
                TextInputReturnKeyType.Go => "go",
                TextInputReturnKeyType.Send => "send",
                TextInputReturnKeyType.Search => "search",
                TextInputReturnKeyType.Next => "next",
                TextInputReturnKeyType.Previous => "previous",
                _ => "enter"
            };
    }
}
