using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using StrataTheme.Controls;
using Xunit;
using Xunit.Abstractions;

namespace Lumi.Tests;

/// <summary>
/// On-demand, human-inspectable PNG capture of the REAL <see cref="StrataMermaid"/> native renderer.
/// Renders a catalogue of canonical Mermaid diagrams (plus a complex architecture diagram) on a dark
/// app-like backdrop via a Skia headless window, auto-crops each to its content, and writes one PNG
/// per diagram to disk so the rendering quality can be eyeballed and iterated on without launching the
/// full app. Gated behind <c>MERMAID_CAPTURE=1</c> so it is inert in normal CI runs.
///
/// Run:  <c>$env:MERMAID_CAPTURE=1; dotnet test --filter MermaidRenderHarness</c>
/// Output:  <c>%TEMP%\Lumi-mermaid-harness\NN-name.png</c> (override dir via MERMAID_CAPTURE_DIR).
/// Render one only:  <c>$env:MERMAID_CAPTURE_ONLY=architecture</c>.
/// </summary>
[Collection("Headless UI")]
[Trait(HeadlessTestSession.HostTrait, HeadlessTestSession.OwnHost)]
public sealed class MermaidRenderHarness
{
    private readonly ITestOutputHelper _out;

    public MermaidRenderHarness(ITestOutputHelper o) => _out = o;

    // Diagram backdrop (matches the app dark surface the native renderer falls back to).
    private static readonly Color Backdrop = Color.FromRgb(0x16, 0x16, 0x18);

    private static IReadOnlyList<(string Name, string Source)> Catalog()
    {
        // If MERMAID_CAPTURE_SRC points to a directory of *.mmd files, render those instead of the
        // built-in catalogue. Lets the same sources feed both the Strata render and an external
        // reference renderer for a fair side-by-side comparison.
        var srcDir = Environment.GetEnvironmentVariable("MERMAID_CAPTURE_SRC");
        if (!string.IsNullOrWhiteSpace(srcDir) && Directory.Exists(srcDir))
        {
            var list = new List<(string, string)>();
            foreach (var file in Directory.GetFiles(srcDir, "*.mmd"))
                list.Add((Path.GetFileNameWithoutExtension(file), File.ReadAllText(file)));
            list.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
            if (list.Count > 0)
                return list;
        }

        return new (string, string)[]
    {
        ("01-flow-decision",
            "flowchart TD\n" +
            "    A[Start] --> B{Is it working?}\n" +
            "    B -->|Yes| C[Ship it]\n" +
            "    B -->|No| D[Debug]\n" +
            "    D --> B\n" +
            "    C --> E[Done]"),

        ("02-sequence",
            "sequenceDiagram\n" +
            "    participant U as User\n" +
            "    participant A as API\n" +
            "    participant D as DB\n" +
            "    U->>A: Request\n" +
            "    A->>D: Query\n" +
            "    D-->>A: Rows\n" +
            "    A-->>U: Response"),

        ("03-state",
            "stateDiagram-v2\n" +
            "    [*] --> Idle\n" +
            "    Idle --> Running: start\n" +
            "    Running --> Idle: stop\n" +
            "    Running --> Done: finish\n" +
            "    Done --> [*]"),

        ("04-class",
            "classDiagram\n" +
            "    Animal <|-- Duck\n" +
            "    Animal <|-- Fish\n" +
            "    Animal : +int age\n" +
            "    Animal : +String gender\n" +
            "    Animal: +isMammal()\n" +
            "    Animal: +mate()\n" +
            "    class Duck{\n" +
            "      +String beakColor\n" +
            "      +swim()\n" +
            "      +quack()\n" +
            "    }\n" +
            "    class Fish{\n" +
            "      -int sizeInFeet\n" +
            "      -canEat()\n" +
            "    }"),

        ("05-er",
            "erDiagram\n" +
            "    CUSTOMER ||--o{ ORDER : places\n" +
            "    ORDER ||--|{ LINE-ITEM : contains\n" +
            "    CUSTOMER }|..|{ DELIVERY-ADDRESS : uses"),

        ("06-flow-subgraph",
            "flowchart LR\n" +
            "    A[Client] --> B\n" +
            "    subgraph Backend\n" +
            "      B[API Gateway] --> C[Service]\n" +
            "      C --> D[(Database)]\n" +
            "    end\n" +
            "    D --> E[Cache]"),

        ("07-architecture",
            "flowchart TB\n" +
            "    User([User]) --> UI\n" +
            "    subgraph Startup\n" +
            "      App[App.axaml.cs]\n" +
            "    end\n" +
            "    subgraph ViewModels\n" +
            "      Main[MainViewModel]\n" +
            "      Chat[ChatViewModel]\n" +
            "      Agents[AgentsViewModel]\n" +
            "      Projects[ProjectsViewModel]\n" +
            "    end\n" +
            "    subgraph Services\n" +
            "      Store[DataStore]\n" +
            "      Copilot[CopilotService]\n" +
            "      Prompt[SystemPromptBuilder]\n" +
            "      Background[Background Service]\n" +
            "    end\n" +
            "    subgraph Views\n" +
            "      UI[MainWindow]\n" +
            "      ChatView[ChatView]\n" +
            "    end\n" +
            "    App --> Main\n" +
            "    Main --> Chat & Agents & Projects\n" +
            "    Chat --> Copilot & Store & Prompt\n" +
            "    Agents --> Store\n" +
            "    Projects --> Store\n" +
            "    Copilot --> Background\n" +
            "    Main --> UI\n" +
            "    Chat --> ChatView\n" +
            "    UI --> ChatView"),
        };
    }

    [SkippableFact]
    public void Capture_AllDiagrams()
    {
        Skip.IfNot(
            Environment.GetEnvironmentVariable("MERMAID_CAPTURE") == "1",
            "Set MERMAID_CAPTURE=1 to run the on-demand Mermaid render harness.");

        var outDir = Environment.GetEnvironmentVariable("MERMAID_CAPTURE_DIR")
                     ?? Path.Combine(Path.GetTempPath(), "Lumi-mermaid-harness");
        Directory.CreateDirectory(outDir);

        var only = Environment.GetEnvironmentVariable("MERMAID_CAPTURE_ONLY");
        var width = 720;
        if (int.TryParse(Environment.GetEnvironmentVariable("MERMAID_CAPTURE_WIDTH"), out var w) && w > 200)
            width = w;

        HeadlessUnitTestSession? session = null;
        string? skipReason = null;
        try
        {
            session = HeadlessTestSession.StartOwn(typeof(SkiaHeadlessTestApp));
        }
        catch (Exception ex)
        {
            skipReason = $"Skia headless session unavailable: {ex.Message}";
        }

        Skip.If(session is null, skipReason ?? "Skia headless session unavailable.");

        var rendered = false;

        try
        {
            session!.Dispatch(() =>
            {
                foreach (var (name, source) in Catalog())
                {
                    if (!string.IsNullOrWhiteSpace(only) &&
                        !name.Contains(only!, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var mermaid = new StrataMermaid { Source = source };

                    var window = new Window
                    {
                        Width = width + 24,
                        Height = 1600,
                        Background = new SolidColorBrush(Backdrop),
                        Content = new ScrollViewer
                        {
                            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                            Content = mermaid,
                        },
                    };
                    window.Show();

                    // Attach + template + layout, then drain the 500ms entrance animation.
                    for (int i = 0; i < 50; i++)
                        Tick(16);

                    var frame = window.CaptureRenderedFrame();
                    if (frame is null)
                    {
                        window.Close();
                        continue;
                    }
                    rendered = true;

                    var cropped = AutoCrop(frame, Backdrop, margin: 16);
                    var path = Path.Combine(outDir, name + ".png");
                    cropped.Save(path);
                    _out.WriteLine($"{name}: {cropped.PixelSize.Width}x{cropped.PixelSize.Height} -> {path}");

                    frame.Dispose();
                    cropped.Dispose();
                    window.Close();
                }
            }, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            SafeDispose(session);
        }

        Skip.IfNot(rendered, "Skia headless capture returned no rendered frames (drawing-free platform).");
        _out.WriteLine($"artefacts in: {outDir}");
    }

    private static void Tick(int realMs)
    {
        if (realMs > 0)
            System.Threading.Thread.Sleep(realMs);
        try { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        catch { /* render timer variant w/o manual tick */ }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Crops a captured frame down to the bounding box of pixels that differ from the
    /// backdrop, plus a margin — so each diagram PNG is tight to its content for easy comparison.</summary>
    private static WriteableBitmap AutoCrop(WriteableBitmap src, Color backdrop, int margin)
    {
        int w = src.PixelSize.Width, h = src.PixelSize.Height;
        int minX = w, minY = h, maxX = -1, maxY = -1;

        using (var fb = src.Lock())
        {
            unsafe
            {
                byte* p = (byte*)fb.Address;
                int stride = fb.RowBytes;
                for (int y = 0; y < h; y++)
                {
                    byte* row = p + y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        byte* px = row + x * 4; // BGRA
                        int db = Math.Abs(px[0] - backdrop.B);
                        int dg = Math.Abs(px[1] - backdrop.G);
                        int dr = Math.Abs(px[2] - backdrop.R);
                        if (db + dg + dr > 24)
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }
            }
        }

        if (maxX < 0)
            return src; // nothing drawn; return as-is

        minX = Math.Max(0, minX - margin);
        minY = Math.Max(0, minY - margin);
        maxX = Math.Min(w - 1, maxX + margin);
        maxY = Math.Min(h - 1, maxY + margin);

        int cw = maxX - minX + 1, ch = maxY - minY + 1;
        var dst = new WriteableBitmap(new PixelSize(cw, ch), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var sfb = src.Lock())
        using (var dfb = dst.Lock())
        {
            unsafe
            {
                byte* sp = (byte*)sfb.Address;
                byte* dp = (byte*)dfb.Address;
                for (int y = 0; y < ch; y++)
                {
                    byte* srow = sp + (minY + y) * sfb.RowBytes + minX * 4;
                    byte* drow = dp + y * dfb.RowBytes;
                    Buffer.MemoryCopy(srow, drow, cw * 4, cw * 4);
                }
            }
        }
        return dst;
    }

    private static void SafeDispose(HeadlessUnitTestSession? session)
    {
        try { session?.Dispose(); }
        catch { /* teardown races in the headless session are harmless here */ }
    }
}
