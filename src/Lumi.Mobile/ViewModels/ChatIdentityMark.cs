using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Lumi.Mobile.ViewModels;

/// <summary>
/// A chat's identity mark, as the desktop draws it: its agent's glyph, or its project's initial, on
/// a tint of the project's own hue. The hues and their order match the desktop exactly, so a project
/// wears the same colour on the PC and on the phone.
/// </summary>
public sealed record ChatIdentityMark(string Glyph, Color Hue)
{
    private static readonly Color[] ProjectHues =
    [
        Color.Parse("#818CF8"),
        Color.Parse("#34D399"),
        Color.Parse("#F472B6"),
        Color.Parse("#38BDF8"),
        Color.Parse("#A78BFA"),
        Color.Parse("#2DD4BF"),
    ];

    private static readonly Color AgentOnlyHue = Color.Parse("#9CA3AF");

    /// <summary>No identity: a chat with neither project nor agent, or every chat at once.</summary>
    public static ChatIdentityMark Empty { get; } = new("", Colors.Transparent);

    public IBrush Foreground => new ImmutableSolidColorBrush(Hue);

    public IBrush Background => new ImmutableSolidColorBrush(Color.FromArgb(0x2E, Hue.R, Hue.G, Hue.B));

    /// <summary>The project's hue. Guid hashes are stable across processes, so PC and phone agree.</summary>
    public static Color HueFor(Guid projectId) =>
        ProjectHues[(int)((uint)projectId.GetHashCode() % (uint)ProjectHues.Length)];

    public static string InitialOf(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return "•";

        return StringInfo.GetNextTextElement(trimmed).ToUpper(CultureInfo.CurrentCulture);
    }

    /// <summary>Null when the chat has neither a project nor an agent: the title stands on its own.</summary>
    public static ChatIdentityMark? Create(Guid? projectId, string? projectName, string? agentName, string? agentGlyph)
    {
        var hasProject = projectId is not null && !string.IsNullOrWhiteSpace(projectName);
        var hasAgent = !string.IsNullOrWhiteSpace(agentName);
        if (!hasProject && !hasAgent)
            return null;

        var glyph = hasAgent && !string.IsNullOrWhiteSpace(agentGlyph) && agentGlyph != "◉"
            ? agentGlyph!
            : InitialOf(hasProject ? projectName! : agentName!);
        return new ChatIdentityMark(glyph, hasProject ? HueFor(projectId!.Value) : AgentOnlyHue);
    }
}
