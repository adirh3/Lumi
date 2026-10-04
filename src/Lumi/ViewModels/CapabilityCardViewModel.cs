using System.Globalization;
using Avalonia.Media;
using Lumi.Localization;
using Lumi.Services.Sharing;

namespace Lumi.ViewModels;

public enum ReceiptTone
{
    Neutral,
    Safe,
    Info,
    Caution
}

/// <summary>One line of a share or import receipt: a glyph, a sentence, and optionally the exact text it is about.</summary>
public sealed class ReceiptNoteViewModel
{
    public ReceiptNoteViewModel(string glyph, string text, ReceiptTone tone = ReceiptTone.Neutral, string? code = null, string? detail = null)
    {
        Glyph = glyph;
        Text = text;
        Tone = tone;
        Code = string.IsNullOrWhiteSpace(code) ? null : code;
        Detail = string.IsNullOrWhiteSpace(detail) ? null : detail;
    }

    public string Glyph { get; }
    public string Text { get; }
    public ReceiptTone Tone { get; }

    /// <summary>Verbatim text shown in monospace: a command line, a URL, a file name.</summary>
    public string? Code { get; }

    public string? Detail { get; }

    public bool HasCode => Code is not null;
    public bool HasDetail => Detail is not null;
    public bool IsSafe => Tone == ReceiptTone.Safe;
    public bool IsInfo => Tone == ReceiptTone.Info;
    public bool IsCaution => Tone == ReceiptTone.Caution;
}

/// <summary>
/// The "ticket" at the top of the share and import sheets: what the capability is, how it travels,
/// and what it carries. Each kind has its own hue so a skill, a Lumi and an MCP server are
/// recognizable at a glance, in the sheet and on the recipient's side.
/// </summary>
public sealed class CapabilityCardViewModel
{
    private static readonly Dictionary<SharedCapabilityKind, Color> Hues = new()
    {
        [SharedCapabilityKind.Skill] = Color.Parse("#FBBF24"),
        [SharedCapabilityKind.Lumi] = Color.Parse("#A78BFA"),
        [SharedCapabilityKind.McpServer] = Color.Parse("#2DD4BF")
    };

    private readonly Color _hue;

    public CapabilityCardViewModel(
        SharedCapabilityKind kind,
        string glyph,
        string name,
        string description,
        string formatLabel,
        string contentsLabel,
        string footerLabel)
    {
        Kind = kind;
        Glyph = glyph;
        Name = name;
        Description = description;
        FormatLabel = formatLabel;
        ContentsLabel = contentsLabel;
        FooterLabel = footerLabel;
        _hue = Hues[kind];
    }

    public SharedCapabilityKind Kind { get; }
    public string Glyph { get; }
    public string Name { get; }
    public string Description { get; }
    public string KindLabel => KindName(Kind);
    public string FormatLabel { get; }
    public string ContentsLabel { get; }
    public string FooterLabel { get; }
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public IBrush Accent => Brush(0xFF);
    public IBrush AccentTint => Brush(0x2E);
    public IBrush CardBorder => Brush(0x4D);
    public IBrush GlyphBackground => Brush(0x26);
    public IBrush GlyphBorder => Brush(0x59);

    public IBrush CardBackground => new LinearGradientBrush
    {
        StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
        EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x38, _hue.R, _hue.G, _hue.B), 0),
            new GradientStop(Color.FromArgb(0x0A, _hue.R, _hue.G, _hue.B), 0.75)
        }
    }.ToImmutable();

    private IBrush Brush(byte alpha)
        => new SolidColorBrush(Color.FromArgb(alpha, _hue.R, _hue.G, _hue.B)).ToImmutable();

    public static IBrush AccentFor(SharedCapabilityKind kind)
        => new SolidColorBrush(Hues[kind]).ToImmutable();

    public static IBrush TintFor(SharedCapabilityKind kind)
    {
        var hue = Hues[kind];
        return new SolidColorBrush(Color.FromArgb(0x2E, hue.R, hue.G, hue.B)).ToImmutable();
    }

    public static string KindName(SharedCapabilityKind kind) => kind switch
    {
        SharedCapabilityKind.Skill => Loc.Capability_KindSkill,
        SharedCapabilityKind.Lumi => Loc.Capability_KindLumi,
        _ => Loc.Capability_KindMcp
    };

    public static string FormatName(CapabilityPackFormat format) => format switch
    {
        CapabilityPackFormat.SkillMarkdown => Loc.Capability_FormatSkill,
        CapabilityPackFormat.LumiPack => Loc.Capability_FormatPack,
        _ => Loc.Capability_FormatMcpConfig
    };

    public static CapabilityCardViewModel ForPack(CapabilityPack pack, string footerLabel)
        => new(
            pack.Kind,
            string.IsNullOrWhiteSpace(pack.IconGlyph) ? DefaultGlyph(pack.Kind) : HiddenText.Strip(pack.IconGlyph, out _),
            // Shown before anything is cleaned by the importer, so invisible characters are stripped here too.
            PackText.SingleLine(HiddenText.Strip(pack.Name, out _)),
            PackText.SingleLine(HiddenText.Strip(pack.Description, out _)),
            FormatName(pack.Format),
            DescribeContents(pack),
            footerLabel);

    public static string DefaultGlyph(SharedCapabilityKind kind) => kind switch
    {
        SharedCapabilityKind.Skill => "⚡",
        SharedCapabilityKind.Lumi => "✦",
        _ => "🔌"
    };

    /// <summary>"2 skills · 1 MCP server · all Lumi tools", "Instructions · 1.2 KB", "Local program · needs 1 key".</summary>
    public static string DescribeContents(CapabilityPack pack)
    {
        var parts = new List<string>();
        if (pack.Lumi is { } lumi)
        {
            if (pack.Skills.Count > 0)
                parts.Add(Count(pack.Skills.Count, Loc.Capability_OneSkill, Loc.Capability_ManySkills));
            if (pack.McpServers.Count > 0)
                parts.Add(Count(pack.McpServers.Count, Loc.Capability_OneServer, Loc.Capability_ManyServers));
            parts.Add(lumi.ToolNames is null
                ? Loc.Capability_AllTools
                : lumi.ToolNames.Count == 0
                    ? Loc.Capability_NoTools
                    : Count(lumi.ToolNames.Count, Loc.Capability_OneTool, Loc.Capability_ManyTools));
        }
        else if (pack.McpServers.Count == 1 && pack.Skills.Count == 0)
        {
            var server = pack.McpServers[0];
            parts.Add(server.IsRemote ? Loc.Capability_OnlineService : Loc.Capability_LocalProgram);
            if (server.RequiredKeys.Count > 0)
                parts.Add(Count(server.RequiredKeys.Count, Loc.Capability_NeedsOneKey, Loc.Capability_NeedsManyKeys));
        }
        else if (pack.McpServers.Count > 1 && pack.Skills.Count == 0)
        {
            parts.Add(Count(pack.McpServers.Count, Loc.Capability_OneServer, Loc.Capability_ManyServers));
        }
        else if (pack.Skills.Count == 1)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, Loc.Capability_Instructions, PackText.FormatSize(pack.Skills[0].Content)));
        }
        else
        {
            if (pack.Skills.Count > 0)
                parts.Add(Count(pack.Skills.Count, Loc.Capability_OneSkill, Loc.Capability_ManySkills));
            if (pack.McpServers.Count > 0)
                parts.Add(Count(pack.McpServers.Count, Loc.Capability_OneServer, Loc.Capability_ManyServers));
        }

        return string.Join(" · ", parts);
    }

    public static string Count(int count, string one, string many)
        => count == 1 ? one : string.Format(CultureInfo.CurrentCulture, many, count);
}
