using Avalonia;
using System;
#if DEBUG
using AvaloniaMcp.Diagnostics;
#endif

namespace Lumi;

class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

#if DEBUG
        builder = builder.UseMcpDiagnostics();
#endif

        return builder;
    }
}
