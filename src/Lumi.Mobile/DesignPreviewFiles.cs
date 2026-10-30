#if DEBUG
using System.Globalization;
using System.Text;
using Avalonia.Platform;

namespace Lumi.Mobile;

/// <summary>
/// DEBUG only: the files the design preview's chat made, served as a PC would send them, so every kind of
/// file preview can be judged on the simulator and an Android Debug build without a PC.
/// </summary>
internal static class DesignPreviewFiles
{
    public static async Task<string?> DownloadAsync(Guid messageId, string fileName, CancellationToken cancellationToken)
    {
        // A short wait, as a real download over Tailscale takes, so the loading state is seen.
        await Task.Delay(TimeSpan.FromMilliseconds(450), cancellationToken);
        var bytes = Content(fileName);
        if (bytes is null)
            return null;

        var folder = Path.Combine(Path.GetTempPath(), "LumiMobile", "downloads");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{Guid.NewGuid():N}-{Path.GetFileName(fileName)}");
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return path;
    }

    private static byte[]? Content(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => Asset("avares://Lumi.Mobile/Assets/lumi-icon.png"),
        ".md" => Encoding.UTF8.GetBytes(Report),
        ".cs" => Encoding.UTF8.GetBytes(Code),
        ".csv" => Encoding.UTF8.GetBytes(Metrics()),
        ".pdf" => Pdf(),
        // A real document is a zip; its start is enough for another app to be offered it.
        ".docx" => [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00],
        _ => null
    };

    private static byte[] Asset(string uri)
    {
        using var asset = AssetLoader.Open(new Uri(uri));
        using var copy = new MemoryStream();
        asset.CopyTo(copy);
        return copy.ToArray();
    }

    // Joined from lines rather than a raw string: in Release this file is skipped, and the compiler
    // still reads a skipped line that starts with '#' (a Markdown heading) as a directive.
    private static readonly string Report = string.Join('\n',
        "# Keyboard gap on resume",
        "",
        "**Fixed.** Android kept the keyboard's last height while Lumi was in the background, so the composer came back lifted by a keyboard that was no longer there.",
        "",
        "## What changed",
        "",
        "- Cached input-pane geometry is cleared when the app is deactivated",
        "- Fresh IME insets are read on resume, already in device-independent pixels",
        "- The composer no longer animates from a stale height",
        "",
        "## Verified on",
        "",
        "| Device | Posture | Result |",
        "| --- | --- | --- |",
        "| Galaxy Z Fold7 | Cover | ✅ Pass |",
        "| Galaxy Z Fold7 | Unfolded | ✅ Pass |",
        "| Pixel 8 Pro | Portrait | ✅ Pass |",
        "",
        "## The fix",
        "",
        "```csharp",
        "public void NotifyApplicationDeactivated()",
        "{",
        "    _keyboardInset = 0;",
        "    _keyboardTop = double.NaN;",
        "    ApplyInsets();",
        "}",
        "```",
        "",
        "> All 699 mobile tests pass, including the new `ComposerInsetTests`.");

    private const string Code = """
        using Avalonia;
        using Lumi.Mobile.Views;
        using Xunit;

        namespace Lumi.Mobile.Tests;

        /// <summary>The composer sits on the keyboard, never on a keyboard that has gone.</summary>
        public sealed class ComposerInsetTests
        {
            [Fact]
            public void ResumeClearsTheKeyboardsLastHeight()
            {
                var view = new MobileShellView();
                view.ApplyPlatformKeyboardInset(320);

                view.NotifyApplicationDeactivated();
                view.NotifyApplicationActivated();

                Assert.Equal(0, view.KeyboardInset);
            }

            [Theory]
            [InlineData(412, 892)]
            [InlineData(984, 1092)]
            public void TheComposerStaysAboveTheGestureBar(double width, double height)
            {
                var insets = new Thickness(0, 24, 0, 20);
                Assert.True(height - insets.Bottom > width / 2, $"{width}×{height} keeps its composer in view");
            }
        }
        """;

    private static string Metrics()
    {
        var rows = new StringBuilder("Device,Posture,Width (dp),Height (dp),Panes,Chat (dp),Preview (dp),Result\n");
        (string Device, string Posture, int Width, int Height)[] devices =
        [
            ("Pixel 8 Pro", "Portrait", 412, 892), ("Galaxy Z Fold7", "Cover", 412, 960),
            ("Galaxy Z Fold7", "Open", 984, 1092), ("Galaxy Z Fold7", "Book", 984, 1092),
            ("Galaxy Z Fold7", "Tabletop", 1092, 984), ("Pixel Fold", "Unfolded", 884, 908),
            ("iPhone 15 Pro", "Portrait", 393, 852), ("Tablet", "Portrait", 834, 1112),
            ("Tablet", "Landscape", 1112, 834), ("Large tablet", "Landscape", 1280, 800)
        ];
        for (var run = 1; run <= 4; run++)
        {
            foreach (var (device, posture, width, height) in devices)
            {
                var twoPanes = width >= 700;
                var chat = twoPanes ? Math.Clamp(Math.Round(width * 0.42), 360, 520) : width;
                rows.Append(CultureInfo.InvariantCulture,
                    $"{device},{posture} (run {run}),{width},{height},{(twoPanes ? 2 : 1)},{chat},{(twoPanes ? width - chat : width)},Pass\n");
            }
        }

        return rows.ToString();
    }

    /// <summary>A two-page PDF report, written by hand so the fixture needs nothing to make one.</summary>
    private static byte[] Pdf()
    {
        string[][] pages =
        [
            [
                "Fold layout report",
                "Every posture of the Galaxy Z Fold7 and the Pixel Fold was checked with the file preview open.",
                "",
                "Unfolded: the chat moves into the chat list's place and the preview opens beside it.",
                "Book posture: the chat and the preview meet at the crease.",
                "Physical hinge: nothing is painted under the hinge.",
                "Cover screen: the preview covers the chat and Back returns to it.",
                "",
                "Result: all 10 postures pass."
            ],
            [
                "Next steps",
                "Run the device tests on the Fold.",
                "Check the preview in light and dark themes.",
                "Ship the build to the Fold for a final look."
            ]
        ];

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', pages.Select((_, index) => $"{5 + index * 2} 0 R"))}] /Count {pages.Length} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>"
        };
        foreach (var lines in pages)
        {
            var content = new StringBuilder("0.39 0.40 0.95 rg 0 806 595 36 re f\n");
            content.Append("BT /F2 22 Tf 56 760 Td 0.1 0.1 0.12 rg (").Append(Escape(lines[0])).Append(") Tj ET\n");
            content.Append("BT /F1 12 Tf 56 724 Td 18 TL 0.2 0.2 0.24 rg\n");
            foreach (var line in lines.Skip(1))
                content.Append('(').Append(Escape(line)).Append(") Tj T*\n");
            content.Append("ET\n");
            var stream = content.ToString();
            var pageObject = objects.Count + 1;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {pageObject + 1} 0 R >>");
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream");
        }

        var output = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(output.ToString()));
            output.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        var xref = Encoding.ASCII.GetByteCount(output.ToString());
        output.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f\r\n");
        foreach (var offset in offsets)
            output.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n\r\n");
        output.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(output.ToString());

        static string Escape(string text) =>
            text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
    }
}
#endif
