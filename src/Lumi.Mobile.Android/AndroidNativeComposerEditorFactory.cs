using System.Runtime.CompilerServices;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Runtime;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Avalonia;
using Avalonia.Android;
using Avalonia.Platform;
using Avalonia.Styling;
using Lumi.Mobile.Services;
using Lumi.Mobile.Views;

namespace Lumi.Mobile.Android;

internal sealed class AndroidNativeComposerEditorFactory(Activity activity)
    : INativeComposerEditorFactory
{
    private readonly Activity _activity = activity;
    private readonly ConditionalWeakTable<NativeComposerEditorHost, EditorState> _states = new();
    private EditText? _focusedEditor;

    public bool IsAvailable => true;

    public IPlatformHandle Create(
        NativeComposerEditorHost host,
        IPlatformHandle parent)
    {
        var context = (parent as AndroidViewControlHandle)?.View.Context
                      ?? _activity;
        var editor = new EditText(context)
        {
            Gravity = GravityFlags.Start | GravityFlags.CenterVertical,
            InputType = InputTypes.ClassText
                        | InputTypes.TextFlagMultiLine
                        | InputTypes.TextFlagAutoCorrect
                        | InputTypes.TextFlagCapSentences,
            ImeOptions = ImeAction.None
                         | (ImeAction)ImeFlags.NoEnterAction
                         | (ImeAction)ImeFlags.NoExtractUi
                         | (ImeAction)ImeFlags.NoFullscreen,
            Clickable = true,
            LongClickable = true,
            Focusable = true,
            FocusableInTouchMode = true,
            DefaultFocusHighlightEnabled = false,
            Alpha = host.IsVisible ? 1f : 0f,
            TextSize = 17,
            VerticalScrollBarEnabled = true,
            Hint = host.Placeholder,
            Text = host.Text
        };
        editor.SetBackgroundColor(Color.Transparent);
        editor.SetSingleLine(false);
        editor.SetHorizontallyScrolling(false);
        editor.SetMinLines(1);
        editor.SetMaxLines(8);
        editor.SetPadding(Dp(context, 12), Dp(context, 10), Dp(context, 12), Dp(context, 10));
        editor.SetSelection(editor.Text?.Length ?? 0);

        var state = new EditorState(
            editor,
            FindAvaloniaView((parent as AndroidViewControlHandle)?.View)
            ?? FindAvaloniaViewDescendant(_activity.Window?.DecorView));
        state.TextChangedHandler = (_, _) =>
        {
            if (!state.ApplyingModel)
                host.SetTextFromNative(editor.Text ?? "");
            QueueHeightUpdate(host, state);
        };
        state.LayoutChangedHandler = (_, _) => QueueHeightUpdate(host, state);
        state.TouchHandler = (_, args) =>
        {
            // This listener only protects focus; consuming the event bypasses EditText selection.
            args.Handled = false;
            if (args.Event?.ActionMasked == MotionEventActions.Down)
            {
                var focusVersion = ++state.FocusVersion;
                state.HoldAvaloniaFocus();
                host.SetInputFocusFromNative(true);
                editor.RequestFocus();
                editor.Post(() =>
                {
                    if (state.IsDestroyed || focusVersion != state.FocusVersion
                        || !host.IsVisible || editor.Visibility != ViewStates.Visible)
                        return;
                    editor.Context
                        ?.GetSystemService(Context.InputMethodService)
                        ?.JavaCast<InputMethodManager>()
                        ?.ShowSoftInput(editor, ShowFlags.Implicit);
                });
            }
            else if (args.Event?.ActionMasked == MotionEventActions.Up)
            {
                var focusVersion = state.FocusVersion;
                editor.Post(() =>
                {
                    if (state.IsDestroyed
                        || focusVersion != state.FocusVersion
                        || !host.IsInputFocused
                        || !host.IsVisible
                        || editor.Visibility != ViewStates.Visible
                        || editor.HasFocus)
                        return;

                    state.HoldAvaloniaFocus();
                    editor.RequestFocus();
                    editor.Context
                        ?.GetSystemService(Context.InputMethodService)
                        ?.JavaCast<InputMethodManager>()
                        ?.ShowSoftInput(editor, ShowFlags.Implicit);
                });
            }
        };
        state.FocusChangedHandler = (_, args) =>
        {
            if (args.HasFocus)
            {
                _focusedEditor = editor;
                state.HoldAvaloniaFocus();
            }
            else
            {
                if (ReferenceEquals(_focusedEditor, editor))
                    _focusedEditor = null;
                state.ReleaseAvaloniaFocus();
            }
            host.SetInputFocusFromNative(args.HasFocus);
        };
        state.ThemeChangedHandler = (_, _) =>
        {
            if (!state.IsDestroyed)
                ApplyTheme(host, editor);
        };
        state.VisibilityChangedHandler = (_, args) =>
        {
            if (state.IsDestroyed
                || args.Property != NativeComposerEditorHost.IsVisibleProperty)
                return;
            // The platform host applies visibility during layout; hide its old surface immediately.
            editor.Alpha = host.IsVisible ? 1f : 0f;
            if (!host.IsVisible)
                Blur(host);
        };
        editor.TextChanged += state.TextChangedHandler;
        editor.LayoutChange += state.LayoutChangedHandler;
        editor.Touch += state.TouchHandler;
        editor.FocusChange += state.FocusChangedHandler;
        host.ActualThemeVariantChanged += state.ThemeChangedHandler;
        host.PropertyChanged += state.VisibilityChangedHandler;
        _states.Add(host, state);
        ApplyTheme(host, editor);
        QueueHeightUpdate(host, state);
        return new AndroidViewControlHandle(editor);
    }

    private static void QueueHeightUpdate(NativeComposerEditorHost host, EditorState state)
    {
        if (state.IsDestroyed || state.HeightUpdateQueued)
            return;

        state.HeightUpdateQueued = true;
        state.Editor.Post(() =>
        {
            state.HeightUpdateQueued = false;
            if (state.IsDestroyed || state.Editor.Layout is not { } layout || state.Editor.Width <= 0)
                return;

            var density = state.Editor.Resources?.DisplayMetrics?.Density ?? 1f;
            var height = layout.Height + state.Editor.CompoundPaddingTop + state.Editor.CompoundPaddingBottom;
            host.SetContentHeightFromNative(height / density);

        });
    }

    public void Destroy(
        NativeComposerEditorHost host,
        IPlatformHandle control)
    {
        if (!_states.TryGetValue(host, out var state))
            return;

        state.IsDestroyed = true;
        // Android can dispose the native peer before Avalonia's deferred destruction callback.
        if (state.Editor.PeerReference.IsValid)
        {
            state.Editor.TextChanged -= state.TextChangedHandler;
            state.Editor.LayoutChange -= state.LayoutChangedHandler;
            state.Editor.Touch -= state.TouchHandler;
            state.Editor.FocusChange -= state.FocusChangedHandler;
        }
        if (state.ThemeChangedHandler is not null)
            host.ActualThemeVariantChanged -= state.ThemeChangedHandler;
        if (state.VisibilityChangedHandler is not null)
            host.PropertyChanged -= state.VisibilityChangedHandler;
        if (ReferenceEquals(_focusedEditor, state.Editor))
            _focusedEditor = null;
        state.ReleaseAvaloniaFocus();
        host.SetInputFocusFromNative(false);
        _states.Remove(host);
    }

    public bool TryDispatchKeyEvent(KeyEvent? keyEvent)
    {
        var editor = _focusedEditor;
        return keyEvent is not null
               && editor is not null && editor.PeerReference.IsValid
               && editor is { HasFocus: true, Visibility: ViewStates.Visible }
               && editor.DispatchKeyEvent(keyEvent);
    }

    public void ApplyText(NativeComposerEditorHost host, string text)
    {
        if (!_states.TryGetValue(host, out var state)
            || state.IsDestroyed
            || string.Equals(state.Editor.Text, text, StringComparison.Ordinal))
        {
            return;
        }

        state.ApplyingModel = true;
        try
        {
            state.Editor.Text = text;
            state.Editor.SetSelection(state.Editor.Text?.Length ?? 0);
        }
        finally
        {
            state.ApplyingModel = false;
        }
        QueueHeightUpdate(host, state);
    }

    public void ApplyPlaceholder(
        NativeComposerEditorHost host,
        string placeholder)
    {
        if (_states.TryGetValue(host, out var state) && !state.IsDestroyed)
            state.Editor.Hint = placeholder;
    }

    public void Blur(NativeComposerEditorHost host)
    {
        if (!_states.TryGetValue(host, out var state) || state.IsDestroyed)
        {
            host.SetInputFocusFromNative(false);
            return;
        }

        state.FocusVersion++;
        var hadFocus = host.IsInputFocused || state.Editor.HasFocus;
        host.SetInputFocusFromNative(false);
        if (!hadFocus)
            return;

        if (_activity.Window?.DecorView is ViewGroup decor)
        {
            // A neutral focus target avoids reactivating either the editor or Avalonia's input surface.
            var focusability = decor.DescendantFocusability;
            decor.DescendantFocusability = DescendantFocusability.BeforeDescendants;
            decor.FocusableInTouchMode = true;
            decor.DefaultFocusHighlightEnabled = false;
            decor.RequestFocus();
            decor.DescendantFocusability = focusability;
        }
        state.ReleaseAvaloniaFocus();
        state.Editor.ClearFocus();
        if (ReferenceEquals(_focusedEditor, state.Editor))
            _focusedEditor = null;
        state.Editor.Context?.GetSystemService(Context.InputMethodService)
            ?.JavaCast<InputMethodManager>()
            ?.HideSoftInputFromWindow(state.Editor.WindowToken, HideSoftInputFlags.None);
    }

    public int GetCaretIndex(NativeComposerEditorHost host)
    {
        if (!_states.TryGetValue(host, out var state))
            return host.Text.Length;
        if (state.IsDestroyed)
            return host.Text.Length;

        return Math.Clamp(state.Editor.SelectionStart, 0, state.Editor.Text?.Length ?? 0);
    }

    public void FocusAt(NativeComposerEditorHost host, int caretIndex)
    {
        if (!_states.TryGetValue(host, out var state) || state.IsDestroyed)
            return;

        var focusVersion = ++state.FocusVersion;
        state.Editor.Post(() =>
        {
            if (state.IsDestroyed || focusVersion != state.FocusVersion || !host.IsVisible)
                return;

            var clamped = Math.Clamp(caretIndex, 0, state.Editor.Text?.Length ?? 0);
            host.SetInputFocusFromNative(true);
            state.Editor.RequestFocus();
            state.Editor.SetSelection(clamped);
            state.Editor.Context
                ?.GetSystemService(Context.InputMethodService)
                ?.JavaCast<InputMethodManager>()
                ?.ShowSoftInput(state.Editor, ShowFlags.Implicit);
        });
    }

    public void FocusAtEnd(NativeComposerEditorHost host)
    {
        FocusAt(host, host.Text.Length);
    }

    private static void ApplyTheme(
        NativeComposerEditorHost host,
        EditText editor)
    {
        var dark = host.ActualThemeVariant == ThemeVariant.Dark;
        editor.SetTextColor(dark
            ? Color.Rgb(242, 242, 247)
            : Color.Rgb(24, 24, 28));
        editor.SetHintTextColor(dark
            ? Color.Rgb(151, 151, 164)
            : Color.Rgb(126, 126, 142));
    }

    private static int Dp(Context context, int value) =>
        (int)Math.Round(
            value * (context.Resources?.DisplayMetrics?.Density ?? 1f));

    private static AvaloniaView? FindAvaloniaView(View? view)
    {
        while (view is not null)
        {
            if (view is AvaloniaView avaloniaView)
                return avaloniaView;
            view = view.Parent as View;
        }

        return null;
    }

    private static AvaloniaView? FindAvaloniaViewDescendant(View? view)
    {
        if (view is AvaloniaView avaloniaView)
            return avaloniaView;
        if (view is not ViewGroup group)
            return null;

        for (var index = 0; index < group.ChildCount; index++)
        {
            if (FindAvaloniaViewDescendant(group.GetChildAt(index)) is { } child)
                return child;
        }

        return null;
    }

    private sealed class EditorState(
        EditText editor,
        AvaloniaView? avaloniaView)
    {
        private bool _holdingAvaloniaFocus;
        private bool _avaloniaFocusable;
        private bool _avaloniaFocusableInTouchMode;
        private bool _isDestroyed;

        public EditText Editor { get; } = editor;
        public bool IsDestroyed
        {
            get => _isDestroyed || !Editor.PeerReference.IsValid;
            set => _isDestroyed = value;
        }
        public bool ApplyingModel { get; set; }
        public bool HeightUpdateQueued { get; set; }
        public int FocusVersion { get; set; }
        public EventHandler<global::Android.Text.TextChangedEventArgs>? TextChangedHandler { get; set; }
        public EventHandler<View.LayoutChangeEventArgs>? LayoutChangedHandler { get; set; }
        public EventHandler<View.TouchEventArgs>? TouchHandler { get; set; }
        public EventHandler<View.FocusChangeEventArgs>? FocusChangedHandler { get; set; }
        public EventHandler<AvaloniaPropertyChangedEventArgs>? VisibilityChangedHandler { get; set; }
        public EventHandler? ThemeChangedHandler { get; set; }

        public void HoldAvaloniaFocus()
        {
            if (_holdingAvaloniaFocus || avaloniaView is null || !avaloniaView.PeerReference.IsValid)
                return;

            _avaloniaFocusable = avaloniaView.Focusable;
            _avaloniaFocusableInTouchMode = avaloniaView.FocusableInTouchMode;
            // AvaloniaView otherwise reclaims focus after dispatch and closes Android's selection UI.
            avaloniaView.Focusable = false;
            avaloniaView.FocusableInTouchMode = false;
            _holdingAvaloniaFocus = true;
        }

        public void ReleaseAvaloniaFocus()
        {
            if (!_holdingAvaloniaFocus || avaloniaView is null)
                return;

            _holdingAvaloniaFocus = false;
            if (avaloniaView.PeerReference.IsValid)
            {
                avaloniaView.Focusable = _avaloniaFocusable;
                avaloniaView.FocusableInTouchMode = _avaloniaFocusableInTouchMode;
            }
        }

    }
}
