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

    /// <summary>Parsed options for the UI responsiveness harness (enabled via CLI flag, debug only).</summary>
    public static UiPerf.UiHarnessOptions? UiHarnessOptions { get; private set; }

    /// <summary>When true, runs the DEBUG-only ProgressBar animation retention repro once the window opens.</summary>
    public static bool ProgressBarLeakReproEnabled { get; private set; }
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

        // The UI responsiveness harness boots the real headed app, so it integrates with normal
        // startup. It must run in an isolated app-data dir (set BEFORE any DataStore access) so the
        // user's real Lumi data is never touched, and it skips onboarding on the fresh data dir.
        UiHarnessOptions = UiPerf.UiHarnessOptions.Parse(args);
        if (UiHarnessOptions.Enabled)
        {
            AttachParentConsole();
            EnsureIsolatedUiHarnessAppDataDir();
            SkipOnboarding = true;
        }

        // ProgressBar animation retention repro — boots the real headed app (needs a real Compositor),
        // runs in an isolated app-data dir, and skips onboarding on the fresh data dir.
        ProgressBarLeakReproEnabled = args.Any(ProgressBarLeakRepro.IsFlag);
        if (ProgressBarLeakReproEnabled)
        {
            AttachParentConsole();
            EnsureIsolatedUiHarnessAppDataDir();
            SkipOnboarding = true;
        }

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

        if (args.Any(DebugAgentHarness.IsSessionReapFlag))
        {
            AttachParentConsole();
            RunSessionReapStressAsync().GetAwaiter().GetResult();
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
        if (!IsAvaloniaTextSelectionBoundsFailure(e.Exception))
            return;

        System.Diagnostics.Trace.TraceWarning(
            "Suppressed known Avalonia text-selection exception: {0}", e.Exception);
        e.Handled = true;
    }

    internal static bool IsAvaloniaTextSelectionBoundsFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is InvalidOperationException
                && string.Equals(current.Message, "Covered length must be greater than zero.", StringComparison.Ordinal)
                && IsAvaloniaTextSelectionStack(current.StackTrace))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAvaloniaTextSelectionStack(string? stackTrace)
    {
        if (string.IsNullOrEmpty(stackTrace)
            || !stackTrace.Contains("HitTestTextRange", StringComparison.Ordinal))
        {
            return false;
        }

        return stackTrace.Contains("TextLineImpl.GetTextBounds", StringComparison.Ordinal)
            && stackTrace.Contains("TextLayout.HitTestTextRange", StringComparison.Ordinal);
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

    private static async System.Threading.Tasks.Task RunSessionReapStressAsync()
    {
        var copilotService = new Services.CopilotService();
        // Bound the whole harness so a hung transport / session.destroy RPC can never wedge CI: on
        // timeout the ct fires, the awaits (all ct- or WaitAsync(ct)-bounded) surface cancellation,
        // and the harness reports FAIL (exit 1) instead of hanging indefinitely.
        using var cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromMinutes(10));
        var exitCode = await DebugAgentHarness.RunSessionReapStressAsync(copilotService, cts.Token);
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

    /// <summary>Points Lumi at a throwaway app-data directory so the harness never touches real data.</summary>
    private static void EnsureIsolatedUiHarnessAppDataDir()
    {
        var existing = Environment.GetEnvironmentVariable("LUMI_APPDATA_DIR");
        if (!string.IsNullOrWhiteSpace(existing))
        {
            Console.WriteLine($"[ui-perf] Using caller-provided LUMI_APPDATA_DIR: {existing}");
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "Lumi-ui-perf-appdata", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("LUMI_APPDATA_DIR", dir);
        Console.WriteLine($"[ui-perf] Using isolated app-data dir: {dir}");
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
