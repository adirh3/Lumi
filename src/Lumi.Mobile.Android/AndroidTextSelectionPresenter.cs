using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Lumi.Mobile.Services;
using ActionMode = Android.Views.ActionMode;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.Android;

/// <summary>
/// Native text selection for an answer: Android's own handles and toolbar, on a panel that wears the
/// app's popup glass (its theme, its typeface, a frost of the page where the phone allows window blur)
/// rather than the system dialog's look.
/// </summary>
internal sealed class AndroidTextSelectionPresenter(Activity activity) : ITextSelectionPresenter
{
    private readonly Activity _activity = activity;
    private Dialog? _dialog;
    private ActionMode? _actionMode;

    public void Show(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        // Read on the UI thread: the panel follows the app's theme, not the phone's.
        var dark = Avalonia.Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        _activity.RunOnUiThread(() => ShowCore(text, dark));
    }

    public void Dismiss() => _activity.RunOnUiThread(DismissCore);

    private void ShowCore(string text, bool dark)
    {
        DismissCore();

        var ink = dark ? Color.Rgb(0xEC, 0xEC, 0xF0) : Color.Rgb(0x18, 0x18, 0x1C);
        var accent = dark ? Color.Rgb(0xA5, 0xB4, 0xFC) : Color.Rgb(0x4F, 0x46, 0xE5);

        // Sizes in sp, at the shared rungs (sheet title, body), so the panel follows the phone's font size.
        var title = new TextView(_activity) { Text = Strings.Get("SelectText"), TextSize = 20.5f };
        title.SetTextColor(ink);
        AndroidAppTypeface.ApplySemiBold(title);
        title.SetPadding(Dp(24), Dp(22), Dp(24), Dp(6));

        var editor = new EditText(_activity)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
            TextSize = 16f,
            ShowSoftInputOnFocus = false,
            VerticalScrollBarEnabled = true
        };
        editor.SetMinLines(6);
        editor.SetMaxLines(18);
        AndroidAppTypeface.Apply(editor);
        editor.SetTextColor(ink);
        editor.SetHighlightColor(dark ? Color.Argb(0x66, 0x81, 0x8C, 0xF8) : Color.Argb(0x40, 0x4F, 0x46, 0xE5));
        editor.SetBackgroundColor(Color.Transparent);
        editor.SetPadding(Dp(24), Dp(8), Dp(24), Dp(8));
        editor.SetText(text, TextView.BufferType.Spannable);
        editor.SetTextIsSelectable(true);
        editor.KeyListener = null;

        var callback = new NativeSelectionCallback(_activity, editor);
        editor.CustomSelectionActionModeCallback = callback;

        var done = new TextView(_activity)
        {
            Text = Strings.Get("Done"), TextSize = 16f, Gravity = GravityFlags.Center, Clickable = true, Focusable = true
        };
        done.SetTextColor(accent);
        AndroidAppTypeface.ApplySemiBold(done);
        done.SetMinHeight(Dp(48));
        done.SetMinWidth(Dp(72));
        done.SetPadding(Dp(18), 0, Dp(18), 0);
        var pressMask = new GradientDrawable();
        pressMask.SetCornerRadius(Dp(24));
        pressMask.SetColor(Color.White);
        done.Background = new RippleDrawable(
            ColorStateList.ValueOf(dark ? Color.Argb(0x33, 0xFF, 0xFF, 0xFF) : Color.Argb(0x1F, 0x18, 0x18, 0x1C)),
            null, pressMask);
        var actions = new LinearLayout(_activity) { Orientation = global::Android.Widget.Orientation.Horizontal };
        actions.SetGravity(GravityFlags.End);
        actions.SetPadding(Dp(12), Dp(4), Dp(12), Dp(12));
        actions.AddView(done);

        var content = new LinearLayout(_activity) { Orientation = global::Android.Widget.Orientation.Vertical };
        content.AddView(title);
        // The text takes what room is left, scrolling when the window is short (a phone held sideways, split
        // screen), so Done always stays on screen.
        content.AddView(editor, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, 1f));
        content.AddView(actions);

        var dialog = new Dialog(_activity);
        dialog.RequestWindowFeature((int)WindowFeatures.NoTitle);
        dialog.SetContentView(content);
        done.Click += (_, _) =>
        {
            FinishActionMode();
            editor.ClearFocus();
            dialog.Dismiss();
        };
        ApplyGlass(dialog.Window, dark);
        dialog.DismissEvent += (_, _) =>
        {
            if (ReferenceEquals(_dialog, dialog))
            {
                FinishActionMode();
                _dialog = null;
            }
        };
        dialog.Window?.SetSoftInputMode(SoftInput.StateAlwaysHidden);
        dialog.Show();
        dialog.Window?.SetSoftInputMode(SoftInput.StateAlwaysHidden);
        _dialog = dialog;

        editor.Post(() =>
        {
            if (!ReferenceEquals(_dialog, dialog) || !dialog.IsShowing)
                return;

            editor.RequestFocus();
            editor.SetSelection(0, editor.Text?.Length ?? 0);
            _actionMode = editor.StartActionMode(callback, ActionModeType.Floating);
        });
    }

    private void DismissCore()
    {
        FinishActionMode();
        var dialog = _dialog;
        _dialog = null;
        dialog?.Dismiss();
    }

    private void FinishActionMode()
    {
        var actionMode = _actionMode;
        _actionMode = null;
        actionMode?.Finish();
    }

    /// <summary>
    /// The app's popup glass on a native window: its tint and rim, a light dim around it, and a frost of
    /// the page behind it where the phone allows window blur. The tint is as dense as the desktop's menus
    /// (80% over their frost), so a phone that reports blur yet cannot draw it still keeps the text legible;
    /// without blur it is nearly solid.
    /// </summary>
    private void ApplyGlass(Window? window, bool dark)
    {
        if (window is null)
            return;

        var blur = OperatingSystem.IsAndroidVersionAtLeast(31)
                   && window.WindowManager?.IsCrossWindowBlurEnabled == true;
        var surface = new GradientDrawable();
        surface.SetCornerRadius(Dp(28));
        surface.SetColor(dark
            ? Color.Argb(blur ? 0xD6 : 0xF5, 0x1F, 0x1F, 0x24)
            : Color.Argb(blur ? 0xE0 : 0xF7, 0xFF, 0xFF, 0xFF));
        surface.SetStroke(Math.Max(1, Dp(1)), dark ? Color.Argb(0x26, 0xFF, 0xFF, 0xFF) : Color.Argb(0x1A, 0x18, 0x18, 0x1C));
        window.SetBackgroundDrawable(new InsetDrawable(surface, Dp(16)));
        var width = Math.Min(_activity.Resources?.DisplayMetrics?.WidthPixels ?? Dp(592), Dp(592));
        window.SetLayout(width, ViewGroup.LayoutParams.WrapContent);
        window.SetDimAmount(dark ? 0.25f : 0.1f);
        if (blur && OperatingSystem.IsAndroidVersionAtLeast(31))
            window.SetBackgroundBlurRadius(Dp(28));
    }

    private int Dp(int value) =>
        (int)Math.Round(value * (_activity.Resources?.DisplayMetrics?.Density ?? 1f));

}

// Keep the callback registered for trimmed APKs; the dialog creates it through Java interop.
[global::Android.Runtime.Register("com/lumi/mobile/NativeSelectionCallback")]
public sealed class NativeSelectionCallback(Activity activity, EditText editor)
    : Java.Lang.Object, ActionMode.ICallback
{
    private const int CopyItemId = 1;
    private const int SelectAllItemId = 2;

    public bool OnCreateActionMode(ActionMode? mode, IMenu? menu)
    {
        if (menu is null)
            return false;

        menu.Add(0, CopyItemId, 0, Strings.Get("Copy"))?.SetShowAsAction(ShowAsAction.Always);
        menu.Add(0, SelectAllItemId, 1, Strings.Get("SelectAll"))?.SetShowAsAction(ShowAsAction.Never);
        return true;
    }

    public bool OnPrepareActionMode(ActionMode? mode, IMenu? menu) => false;

    public bool OnActionItemClicked(ActionMode? mode, IMenuItem? item)
    {
        if (item is null)
            return false;

        switch (item.ItemId)
        {
            case CopyItemId:
                var start = Math.Max(0, Math.Min(editor.SelectionStart, editor.SelectionEnd));
                var end = Math.Max(start, Math.Max(editor.SelectionStart, editor.SelectionEnd));
                var selected = editor.Text?.Substring(start, end - start) ?? "";
                if (selected.Length > 0
                    && activity.GetSystemService(Context.ClipboardService)
                        is global::Android.Content.ClipboardManager clipboard)
                {
                    clipboard.PrimaryClip = ClipData.NewPlainText(Strings.Get("LumiText"), selected);
                }
                mode?.Finish();
                return true;

            case SelectAllItemId:
                editor.SetSelection(0, editor.Text?.Length ?? 0);
                return true;

            default:
                return false;
        }
    }

    public void OnDestroyActionMode(ActionMode? mode)
    {
    }
}
