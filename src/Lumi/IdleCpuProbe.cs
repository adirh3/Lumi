#if DEBUG
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Themes.Simple;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.ViewModels;

namespace Lumi;

internal static class IdleCpuProbe
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    public static bool Enabled => Environment.GetEnvironmentVariable("LUMI_IDLE_CPU_PROBE") == "1";
    public static bool Bare => Enabled && Environment.GetEnvironmentVariable("LUMI_IDLE_CPU_BARE") == "1";

    public static AppBuilder Configure(AppBuilder builder)
    {
        if (!Enabled)
            return builder;

        return Environment.GetEnvironmentVariable("LUMI_IDLE_CPU_RENDERER") switch
        {
            null or "" or "default" => builder,
            "software" when OperatingSystem.IsMacOS() => builder.With(new AvaloniaNativePlatformOptions
            {
                RenderingMode = [AvaloniaNativeRenderingMode.Software],
            }),
            "software" when OperatingSystem.IsWindows() => builder.With(new Win32PlatformOptions
            {
                OverlayPopups = true,
                RenderingMode = [Win32RenderingMode.Software],
            }),
            _ => throw new InvalidOperationException("Unsupported idle-probe renderer."),
        };
    }

    public static void Start(IClassicDesktopStyleApplicationLifetime desktop, Window window, MainViewModel? vm = null)
        => _ = RunAsync(desktop, window, vm);

    private static async Task RunAsync(
        IClassicDesktopStyleApplicationLifetime desktop, Window window, MainViewModel? vm)
    {
        var output = Environment.GetEnvironmentVariable("LUMI_IDLE_CPU_OUTPUT")
            ?? throw new InvalidOperationException("The idle probe requires an isolated output directory.");
        var phases = new List<object>();
        var exitCode = 1;
        try
        {
            Directory.CreateDirectory(output);
            window.Activate();
            await Task.Delay(5000);
            var visual = ElementComposition.GetElementVisual(window)
                ?? throw new InvalidOperationException("The native window has no composition visual.");
            var server = ReadMember(visual.Compositor, "Server");
            var renderLoop = ReadMember(server, "_renderLoop");
            var resolver = typeof(AvaloniaLocator)
                .GetProperty("Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?
                .GetValue(null) ?? throw new MissingMemberException("AvaloniaLocator.Current");
            var getService = resolver.GetType().GetMethod("GetService", [typeof(Type)])
                ?? throw new MissingMethodException("AvaloniaLocator.GetService");
            var graphics = getService.Invoke(resolver, [typeof(IPlatformGraphics)]);
            var environment = new
            {
                Variant = Environment.GetEnvironmentVariable("LUMI_IDLE_CPU_VARIANT"),
                Bare,
                RequestedRenderer = Environment.GetEnvironmentVariable("LUMI_IDLE_CPU_RENDERER") ?? "default",
                Graphics = graphics?.GetType().FullName ?? "Software (no IPlatformGraphics)",
                RenderLoop = renderLoop.GetType().FullName,
                AvaloniaVersion = typeof(Application).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                OS = RuntimeInformation.OSDescription,
                Runtime = RuntimeInformation.FrameworkDescription,
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                OSArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                Environment.ProcessorCount,
                Environment.ProcessId,
                WindowBounds = window.Bounds.ToString(),
                window.RenderScaling,
                Transparency = window.ActualTransparencyLevel.ToString(),
                CopilotStartupDisabled = true,
            };
            Console.WriteLine("IDLE_PROBE_ENV " + JsonSerializer.Serialize(environment, JsonOptions));

            async Task MeasureAsync(string name)
            {
                await Task.Delay(3000);
                var samples = new List<object>();
                using var process = Process.GetCurrentProcess();
                for (var index = 0; index < 3; index++)
                {
                    process.Refresh();
                    var cpuStart = process.TotalProcessorTime.TotalMilliseconds;
                    var clock = Stopwatch.StartNew();
                    await Task.Delay(4000);
                    process.Refresh();
                    var cpuMs = process.TotalProcessorTime.TotalMilliseconds - cpuStart;
                    var seconds = clock.Elapsed.TotalSeconds;
                    var animations = ReadMember(server, "Animations");
                    var state = new
                    {
                        Seconds = seconds,
                        OneCoreCpuPercent = cpuMs / (seconds * 10),
                        WorkingSetBytes = process.WorkingSet64,
                        RenderLoopRunning = ReadMember(renderLoop, "_running"),
                        AnimationsNeedNextTick = ReadMember(animations, "NeedNextTick"),
                        TicksSinceLastCommit = ReadMember(server, "_ticksSinceLastCommit"),
                        LastBatchId = ReadMember(server, "LastBatchId"),
                        CompositorTime = ReadMember(server, "ServerNow").ToString(),
                        ActiveCompositionTargets = ((System.Collections.ICollection)ReadMember(server, "_activeTargets")).Count,
                    };
                    samples.Add(state);
                    Console.WriteLine($"IDLE_PROBE_SAMPLE {name} " + JsonSerializer.Serialize(state, JsonOptions));
                }

                phases.Add(new
                {
                    Name = name,
                    Samples = samples,
                    FocusedElement = window.FocusManager?.GetFocusedElement()?.GetType().Name,
                    WindowState = window.WindowState.ToString(),
                    WindowActive = window.IsActive,
                    WindowVisible = window.IsVisible,
                    IsBusy = vm?.ChatVM.IsBusy,
                    IsStreaming = vm?.ChatVM.IsStreaming,
                });
                await File.WriteAllTextAsync(Path.Combine(output, "report.json"),
                    JsonSerializer.Serialize(new { Environment = environment, Phases = phases }, JsonOptions));

                if (window.WindowState != WindowState.Minimized)
                {
                    using var bitmap = new RenderTargetBitmap(
                        new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height)));
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(output, name + ".png"));
                }
                if (OperatingSystem.IsMacOS() && name is "unfocused" or "focused")
                    await CaptureNativeSampleAsync(output, name);
            }

            window.FocusManager?.Focus(null);
            await MeasureAsync("unfocused");
            var input = window.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(control => control.IsEffectivelyVisible && control.IsEnabled
                    && (vm is null || control.Name == "PART_Input"))
                ?? throw new InvalidOperationException("The probe expected a visible input.");
            if (!input.Focus())
                throw new InvalidOperationException("Unable to focus the probe input.");
            await MeasureAsync("focused");

            if (vm is not null)
            {
                var chat = new Chat { Title = "Isolated idle CPU fixture" };
                for (var index = 0; index < 8; index++)
                {
                    chat.Messages.Add(new ChatMessage { Role = "user", Content = $"Completed request {index + 1}" });
                    chat.Messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content = "## Completed response\n\nThis is static fixture content. No assistant, tool, or background agent is running.\n\n- First item\n- Second item\n\n`var result = 42;`",
                    });
                }
                await vm.ChatVM.LoadChatAsync(chat);
                input.Focus();
                await MeasureAsync("completed-chat");
            }

            window.WindowState = WindowState.Minimized;
            await MeasureAsync("minimized");
            exitCode = 0;
            Console.WriteLine("IDLE_PROBE_COMPLETE");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("IDLE_PROBE_FAILED " + exception);
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "error.txt"), exception.ToString());
        }
        finally
        {
            Environment.ExitCode = exitCode;
            desktop.Shutdown(exitCode);
        }
    }

    private static object ReadMember(object instance, string name)
        => instance.GetType().GetField(name, Members)?.GetValue(instance)
            ?? instance.GetType().GetProperty(name, Members)?.GetValue(instance)
            ?? throw new MissingMemberException(instance.GetType().FullName, name);

    private static async Task CaptureNativeSampleAsync(string output, string name)
    {
        using var sample = new Process
        {
            StartInfo = new ProcessStartInfo("/usr/bin/sample")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        sample.StartInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        sample.StartInfo.ArgumentList.Add("3");
        sample.StartInfo.ArgumentList.Add("-file");
        sample.StartInfo.ArgumentList.Add(Path.Combine(output, name + "-native-sample.txt"));
        if (!sample.Start())
            throw new InvalidOperationException("Could not start the native stack sampler.");
        var stdout = sample.StandardOutput.ReadToEndAsync();
        var stderr = sample.StandardError.ReadToEndAsync();
        await sample.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var diagnostics = $"Exit code: {sample.ExitCode}\n{await stdout}\n{await stderr}";
        await File.WriteAllTextAsync(Path.Combine(output, name + "-sampler.log"), diagnostics);
        if (sample.ExitCode != 0)
            Console.Error.WriteLine("IDLE_PROBE_NATIVE_SAMPLE_FAILED " + diagnostics);
    }
}

public sealed class IdleCpuProbeApplication : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            throw new InvalidOperationException("The bare probe requires a native desktop lifetime.");
        var window = new Window
        {
            Width = 1100,
            Height = 760,
            Title = "Avalonia native idle probe",
            Background = Brushes.White,
            Content = new TextBox { Text = "Static input", Margin = new Thickness(24), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top },
        };
        desktop.MainWindow = window;
        window.Opened += (_, _) => IdleCpuProbe.Start(desktop, window);
        base.OnFrameworkInitializationCompleted();
    }
}
#endif
