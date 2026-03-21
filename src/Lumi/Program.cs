using Avalonia;
using System;
using System.Linq;
#if DEBUG
using AvaloniaMcp.Diagnostics;
#endif

namespace Lumi;

class Program
{
    /// <summary>When true, the onboarding flow is shown even if the user is already onboarded (debug only).</summary>
    public static bool ForceOnboarding { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        ForceOnboarding = args.Contains("--onboarding", StringComparer.OrdinalIgnoreCase);

#if DEBUG
        // Headless agent test mode — no UI, just runs the onboarding agent and prints output
        if (args.Contains("--test-onboarding-agent", StringComparer.OrdinalIgnoreCase))
        {
            RunAgentTestAsync().GetAwaiter().GetResult();
            return;
        }
#endif

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

#if DEBUG
    private static async System.Threading.Tasks.Task RunAgentTestAsync()
    {
        var copilotService = new Services.CopilotService();
        await copilotService.ConnectAsync(default);
        await OnboardingAgentTest.RunAsync(copilotService, default);
    }
#endif

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

        if (OperatingSystem.IsWindows())
        {
            builder = builder.With(new Win32PlatformOptions
            {
                OverlayPopups = true,
            });
        }

#if DEBUG
        builder = builder.UseMcpDiagnostics();
#endif

        return builder;
    }
}
