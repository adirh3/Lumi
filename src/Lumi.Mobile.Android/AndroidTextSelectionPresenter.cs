using Android.App;
using Android.Content;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Lumi.Mobile.Services;
using ActionMode = Android.Views.ActionMode;

namespace Lumi.Mobile.Android;

internal sealed class AndroidTextSelectionPresenter(Activity activity) : ITextSelectionPresenter
{
    private readonly Activity _activity = activity;
    private AlertDialog? _dialog;
    private ActionMode? _actionMode;

    public void Show(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        _activity.RunOnUiThread(() => ShowCore(text));
    }

    public void Dismiss() => _activity.RunOnUiThread(DismissCore);

    private void ShowCore(string text)
    {
        DismissCore();

        var editor = new EditText(_activity)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
            TextSize = 17,
            ShowSoftInputOnFocus = false,
            VerticalScrollBarEnabled = true
        };
        editor.SetMinLines(6);
        editor.SetMaxLines(18);
        editor.SetPadding(Dp(20), Dp(8), Dp(20), Dp(8));
        editor.SetText(text, TextView.BufferType.Spannable);
        editor.SetTextIsSelectable(true);
        editor.KeyListener = null;

        var callback = new NativeSelectionCallback(_activity, editor);
        editor.CustomSelectionActionModeCallback = callback;

        var builder = new AlertDialog.Builder(_activity);
        builder.SetTitle("Select text");
        builder.SetView(editor);
        builder.SetPositiveButton("Done", (_, _) =>
        {
            FinishActionMode();
            editor.ClearFocus();
        });
        var dialog = builder.Create()
                     ?? throw new InvalidOperationException("Android could not create the selection dialog.");
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

        menu.Add(0, CopyItemId, 0, "Copy")?.SetShowAsAction(ShowAsAction.Always);
        menu.Add(0, SelectAllItemId, 1, "Select all")?.SetShowAsAction(ShowAsAction.Never);
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
                    clipboard.PrimaryClip = ClipData.NewPlainText("Lumi text", selected);
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
