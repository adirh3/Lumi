#if DEBUG || LUMI_MOBILE_SMOOTHNESS
using System.Runtime.InteropServices.JavaScript;
using Avalonia.Controls.ApplicationLifetimes;
using Lumi.Mobile.Smoothness;
using Lumi.Mobile.Views;

namespace Lumi.Mobile.Browser;

internal static partial class BrowserSmoothness
{
    private static MobileSmoothnessSession? _session;

    internal static void Initialize()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime
            { MainView: MobileShellView view })
            _session = new(view, GetPerformanceTime);
        else
            throw new InvalidOperationException("The mobile benchmark view did not initialize.");
    }

    [JSExport]
    public static async Task<string> Command(string json) => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
        () => _session?.CommandAsync(json)
            ?? throw new InvalidOperationException("Build with -p:LumiMobileSmoothness=true and open ?smoothness=1."));

    [JSExport]
    public static Task ObserveTouch(double x, double y, bool pressed) =>
        BrowserUiThread.InvokeAsync(() => _session?.ObserveHostTouch(x, y, pressed));

    [JSImport("getPerformanceTime", "./browserHost.js")]
    private static partial double GetPerformanceTime();
}
#endif
