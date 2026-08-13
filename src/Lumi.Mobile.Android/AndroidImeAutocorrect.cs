using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Android.App;
using Android.Content;
using Android.Runtime;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Avalonia.Android;

namespace Lumi.Mobile.Android;

internal static class AndroidImeAutocorrect
{
    private const string LogTag = "LumiIme";
    private static readonly TimeSpan[] RefreshDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250)
    ];
    private static readonly ConditionalWeakTable<AvaloniaView, HookState> InstalledViews = new();

    public static void Install(Activity activity)
    {
        var decor = activity.Window?.DecorView;
        if (decor is null)
            return;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            decor.PostDelayed(
                () => TryInstall(activity, restartInput: false),
                attempt * 50L);
        }
    }

    public static void Refresh(Activity activity)
    {
        var decor = activity.Window?.DecorView;
        if (decor is null)
            return;

        foreach (var delay in RefreshDelays)
        {
            decor.PostDelayed(
                () => TryInstall(activity, restartInput: true),
                (long)delay.TotalMilliseconds);
        }
    }

    private static bool TryInstall(Activity activity, bool restartInput)
    {
        var avaloniaView = FindAvaloniaView(activity);
        if (avaloniaView is null)
            return false;

        var field = typeof(AvaloniaView).GetField(
            "_initEditorInfo",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(avaloniaView) is not Delegate original)
            return false;

        var state = InstalledViews.GetValue(avaloniaView, static _ => new HookState());
        if (ReferenceEquals(original, state.Wrapper))
            return true;

        var wrapper = CreateWrapper(field.FieldType, original);
        state.Wrapper = wrapper;
        field.SetValue(avaloniaView, wrapper);
        global::Android.Util.Log.Debug(
            LogTag,
            $"Installed editor-info wrapper (restart={restartInput}).");
        if (restartInput)
            RestartInput(avaloniaView);
        return true;
    }

    private static AvaloniaView? FindAvaloniaView(Activity activity)
    {
        var field = typeof(AvaloniaActivity).GetField(
            "_view",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(activity) as AvaloniaView
               ?? FindAvaloniaView(activity.Window?.DecorView);
    }

    private static AvaloniaView? FindAvaloniaView(View? view)
    {
        if (view is AvaloniaView avaloniaView)
            return avaloniaView;
        if (view is not ViewGroup group)
            return null;

        for (var index = 0; index < group.ChildCount; index++)
        {
            if (FindAvaloniaView(group.GetChildAt(index)) is { } child)
                return child;
        }

        return null;
    }

    private static void RestartInput(AvaloniaView view)
    {
        var manager = view.Context
            ?.GetSystemService(Context.InputMethodService)
            ?.JavaCast<InputMethodManager>();
        if (manager?.InvokeIsActive(view) != true)
            return;

        manager.RestartInput(view);
        manager.ShowSoftInput(view, ShowFlags.Implicit);
    }

    private static Delegate CreateWrapper(Type delegateType, Delegate original)
    {
        var invoke = delegateType.GetMethod("Invoke")
                     ?? throw new InvalidOperationException("Editor-info delegate has no Invoke method.");
        var parameters = invoke.GetParameters()
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
            .ToArray();
        var call = Expression.Call(
            typeof(AndroidImeAutocorrect),
            nameof(InvokeAndEnableAutocorrect),
            Type.EmptyTypes,
            Expression.Constant(original),
            Expression.Convert(parameters[0], typeof(object)),
            parameters[1]);
        return Expression.Lambda(delegateType, call, parameters)
            .Compile(preferInterpretation: true);
    }

    private static IInputConnection InvokeAndEnableAutocorrect(
        Delegate original,
        object topLevel,
        EditorInfo editorInfo)
    {
        var connection = (IInputConnection?)original.DynamicInvoke(topLevel, editorInfo);
        if ((editorInfo.InputType & InputTypes.ClassText) == InputTypes.ClassText
            && (editorInfo.InputType & InputTypes.TextFlagMultiLine) != 0
            && (editorInfo.InputType & InputTypes.TextFlagNoSuggestions) == 0)
        {
            editorInfo.InputType |= InputTypes.TextFlagAutoCorrect;
            global::Android.Util.Log.Debug(
                LogTag,
                $"Enabled autocorrect: 0x{(int)editorInfo.InputType:X}.");
        }

        return connection is null
            ? null!
            : new OrderedInputConnection(connection);
    }

    private sealed class HookState
    {
        public Delegate? Wrapper { get; set; }
    }

    /// <summary>
    /// Backports AvaloniaUI/Avalonia#21900 until the fix ships in a stable Avalonia package.
    /// Android can batch "commit autocorrect + Enter"; Avalonia 12.1 dispatches Enter immediately,
    /// before its queued composition commit. Holding soft-key events until the outer batch ends
    /// preserves the IME's FIFO order without changing hardware-key handling.
    /// </summary>
    private sealed class OrderedInputConnection(IInputConnection target)
        : InputConnectionWrapper(target, false)
    {
        private readonly object _sync = new();
        private readonly List<KeyEvent?> _queuedKeyEvents = [];
        private int _batchDepth;

        public override bool BeginBatchEdit()
        {
            lock (_sync)
                _batchDepth++;
            return base.BeginBatchEdit();
        }

        public override bool EndBatchEdit()
        {
            var result = base.EndBatchEdit();
            List<KeyEvent?>? queued = null;
            lock (_sync)
            {
                if (_batchDepth > 0)
                    _batchDepth--;
                if (_batchDepth == 0 && _queuedKeyEvents.Count > 0)
                {
                    queued = [.. _queuedKeyEvents];
                    _queuedKeyEvents.Clear();
                }
            }

            if (queued is not null)
            {
                foreach (var keyEvent in queued)
                    base.SendKeyEvent(keyEvent);
            }

            return result;
        }

        public override bool SendKeyEvent(KeyEvent? e)
        {
            lock (_sync)
            {
                if (_batchDepth > 0)
                {
                    _queuedKeyEvents.Add(e);
                    return true;
                }
            }

            return base.SendKeyEvent(e);
        }

        public override void CloseConnection()
        {
            lock (_sync)
            {
                _batchDepth = 0;
                _queuedKeyEvents.Clear();
            }
            base.CloseConnection();
        }
    }
}
