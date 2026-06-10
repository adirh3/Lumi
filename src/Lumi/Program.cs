using Avalonia;
using Avalonia.Threading;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Velopack;
#if DEBUG
using AvaloniaMcp.Diagnostics;
#endif

namespace Lumi;

class Program
{
    /// <summary>When true, the onboarding flow is shown even if the user is already onboarded (debug only).</summary>
    public static bool ForceOnboarding { get; private set; }
#if DEBUG
    /// <summary>When true, opens Lumi directly into the agent debug transcript fixture.</summary>
    public static bool OpenAgentDebugHarness { get; private set; }

    /// <summary>When true, debug automation starts in the main app without first-run onboarding.</summary>
    public static bool SkipOnboarding { get; private set; }
#endif

    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack must run before anything else — it may apply updates and exit immediately
        VelopackApp.Build().Run();

        ForceOnboarding = args.Contains("--onboarding", StringComparer.OrdinalIgnoreCase);

#if DEBUG
        OpenAgentDebugHarness = args.Any(DebugAgentHarness.IsUiHarnessFlag);
        SkipOnboarding = args.Contains("--skip-onboarding", StringComparer.OrdinalIgnoreCase)
            || args.Contains("--no-onboarding", StringComparer.OrdinalIgnoreCase);

        if (args.Any(DebugAgentHarness.IsChatStressFlag))
        {
            AttachParentConsole();
            RunChatStressAsync().GetAwaiter().GetResult();
            return;
        }

        if (args.Any(DebugAgentHarness.IsNativeMcpStressFlag))
        {
            AttachParentConsole();
            RunNativeMcpStressAsync().GetAwaiter().GetResult();
            return;
        }

        if (args.Any(DebugAgentHarness.IsProxyMcpStressFlag))
        {
            AttachParentConsole();
            RunProxyMcpStressAsync().GetAwaiter().GetResult();
            return;
        }

        // Headless agent test mode — no UI, just runs the onboarding agent and prints output
        if (args.Contains("--test-onboarding-agent", StringComparer.OrdinalIgnoreCase))
        {
            AttachParentConsole();
            RunAgentTestAsync().GetAwaiter().GetResult();
            return;
        }
#endif

        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (!IsAvaloniaTextSelectionHandleBoundsFailure(e.Exception))
            return;

        System.Diagnostics.Trace.TraceWarning(
            "Suppressed known Avalonia text-selection exception: {0}", e.Exception);
        e.Handled = true;
    }

    private static bool IsAvaloniaTextSelectionHandleBoundsFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is InvalidOperationException
                && string.Equals(current.Message, "Covered length must be greater than zero.", StringComparison.Ordinal)
                && current.StackTrace?.Contains("TextSelectionHandleCanvas", StringComparison.Ordinal) == true
                && current.StackTrace?.Contains("HitTestTextRange", StringComparison.Ordinal) == true)
            {
                return true;
            }
        }

        return false;
    }

#if DEBUG
    private static async System.Threading.Tasks.Task RunAgentTestAsync()
    {
        var copilotService = new Services.CopilotService();
        await copilotService.ConnectAsync(default);
        await OnboardingAgentTest.RunAsync(copilotService, default);
    }

    private static async System.Threading.Tasks.Task RunChatStressAsync()
    {
        var copilotService = new Services.CopilotService();
        var exitCode = await DebugAgentHarness.RunChatStressAsync(copilotService, default);
        Environment.ExitCode = exitCode;
    }

    private static async System.Threading.Tasks.Task RunNativeMcpStressAsync()
    {
        var copilotService = new Services.CopilotService();
        var exitCode = await DebugAgentHarness.RunNativeMcpStressAsync(copilotService, default);
        Environment.ExitCode = exitCode;
    }

    private static async System.Threading.Tasks.Task RunProxyMcpStressAsync()
    {
        var copilotService = new Services.CopilotService();
        var exitCode = await DebugAgentHarness.RunProxyMcpStressAsync(copilotService, default);
        Environment.ExitCode = exitCode;
    }

    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    private static void AttachParentConsole()
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (!AttachConsole(AttachParentProcess))
            return;

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
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
