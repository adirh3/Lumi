using System.Runtime.CompilerServices;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Runtime;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
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
            Gravity = GravityFlags.Start | GravityFlags.Top,
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
            TextSize = 17,
            VerticalScrollBarEnabled = true,
            Hint = host.Placeholder,
            Text = host.Text
        };
        editor.SetSingleLine(false);
        editor.SetHorizontallyScrolling(false);
        editor.SetMinLines(2);
        editor.SetMaxLines(5);
        editor.SetBackgroundColor(Color.Transparent);
        editor.SetPadding(
            Dp(context, 18),
            Dp(context, 7),
            Dp(context, 18),
            Dp(context, 7));
        editor.SetSelection(editor.Text?.Length ?? 0);

        var state = new EditorState(
            editor,
            FindAvaloniaView((parent as AndroidViewControlHandle)?.View)
            ?? FindAvaloniaViewDescendant(_activity.Window?.DecorView));
        state.TextChangedHandler = (_, _) =>
        {
            if (!state.ApplyingModel)
                host.SetTextFromNative(editor.Text ?? "");
        };
        state.TouchHandler = (_, args) =>
        {
            // This listener only protects focus; consuming the event bypasses EditText selection.
            args.Handled = false;
            if (args.Event?.ActionMasked == MotionEventActions.Down)
            {
                state.HoldAvaloniaFocus();
                editor.RequestFocus();
                editor.Post(() =>
                {
                    if (state.IsDestroyed || editor.Visibility != ViewStates.Visible)
                        return;
                    editor.Context
                        ?.GetSystemService(Context.InputMethodService)
                        ?.JavaCast<InputMethodManager>()
                        ?.ShowSoftInput(editor, ShowFlags.Implicit);
                });
            }
            else if (args.Event?.ActionMasked == MotionEventActions.Up)
            {
                editor.Post(() =>
                {
                    if (state.IsDestroyed
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
        };
        state.ThemeChangedHandler = (_, _) => ApplyTheme(host, editor);
        editor.TextChanged += state.TextChangedHandler;
        editor.Touch += state.TouchHandler;
        editor.FocusChange += state.FocusChangedHandler;
        host.ActualThemeVariantChanged += state.ThemeChangedHandler;
        _states.Add(host, state);
        ApplyTheme(host, editor);
        return new AndroidViewControlHandle(editor);
    }

    public void Destroy(
        NativeComposerEditorHost host,
        IPlatformHandle control)
    {
        if (!_states.TryGetValue(host, out var state))
            return;

        state.IsDestroyed = true;
        if (state.ThemeChangedHandler is not null)
            host.ActualThemeVariantChanged -= state.ThemeChangedHandler;
        if (ReferenceEquals(_focusedEditor, state.Editor))
            _focusedEditor = null;
        state.ReleaseAvaloniaFocus();
        _states.Remove(host);
    }

    public bool TryDispatchKeyEvent(KeyEvent? keyEvent)
    {
        var editor = _focusedEditor;
        return keyEvent is not null
               && editor is { HasFocus: true, Visibility: ViewStates.Visible }
               && editor.DispatchKeyEvent(keyEvent);
    }

    public void ApplyText(NativeComposerEditorHost host, string text)
    {
        if (!_states.TryGetValue(host, out var state)
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
    }

    public void ApplyPlaceholder(
        NativeComposerEditorHost host,
        string placeholder)
    {
        if (_states.TryGetValue(host, out var state))
            state.Editor.Hint = placeholder;
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
        if (!_states.TryGetValue(host, out var state))
            return;

        state.Editor.Post(() =>
        {
            if (state.IsDestroyed)
                return;

            var clamped = Math.Clamp(caretIndex, 0, state.Editor.Text?.Length ?? 0);
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

        public EditText Editor { get; } = editor;
        public bool IsDestroyed { get; set; }
        public bool ApplyingModel { get; set; }
        public EventHandler<global::Android.Text.TextChangedEventArgs>? TextChangedHandler { get; set; }
        public EventHandler<View.TouchEventArgs>? TouchHandler { get; set; }
        public EventHandler<View.FocusChangeEventArgs>? FocusChangedHandler { get; set; }
        public EventHandler? ThemeChangedHandler { get; set; }

        public void HoldAvaloniaFocus()
        {
            if (_holdingAvaloniaFocus || avaloniaView is null)
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

            avaloniaView.Focusable = _avaloniaFocusable;
            avaloniaView.FocusableInTouchMode = _avaloniaFocusableInTouchMode;
            _holdingAvaloniaFocus = false;
        }
    }
}
