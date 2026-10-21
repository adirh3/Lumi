using System.Globalization;

namespace Lumi.Services;

/// <summary>A language dictation can listen for, named in that language. An empty code detects the language.</summary>
public sealed record VoiceLanguageOption(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>The languages offered first, the rest, and the one dictation listens for.</summary>
internal sealed record VoiceLanguageChoices(
    IReadOnlyList<VoiceLanguageOption> Suggested,
    IReadOnlyList<VoiceLanguageOption> More,
    VoiceLanguageOption Selected);

/// <summary>
/// The languages Whisper transcribes. The user's own come first, so the right one is a click away: Whisper can mistake
/// one language for a similar one (Hebrew for Arabic, say), and naming the language avoids that.
/// </summary>
internal static class VoiceLanguageCatalog
{
    private static readonly string[] Codes = (
        "en zh de es ru ko fr ja pt tr pl ca nl ar sv it id hi fi vi he uk el ms cs ro da hu ta no th ur hr bg lt la mi " +
        "ml cy sk te fa lv bn sr az sl kn et mk br eu is hy ne mn bs kk sq sw gl mr pa si km sn yo so af oc ka be tg sd " +
        "gu am yi lo uz fo ht ps tk nn mt sa lb my bo tl mg as tt haw ln ha ba jw su").Split(' ');

    private static readonly Lazy<IReadOnlyDictionary<string, VoiceLanguageOption>> Languages = new(() =>
        Codes.ToDictionary(code => code, code => new VoiceLanguageOption(code, NativeName(code))));

    /// <summary>
    /// Detecting the language, then <paramref name="userLanguages"/> (culture names, in order), English and
    /// <paramref name="chosen"/>; every other language follows in <see cref="VoiceLanguageChoices.More"/>.
    /// </summary>
    public static VoiceLanguageChoices Build(string autoLabel, string? chosen, IEnumerable<string?> userLanguages)
    {
        var auto = new VoiceLanguageOption("", autoLabel);
        var chosenCode = ToWhisperCode(chosen);
        var suggestedCodes = userLanguages.Select(ToWhisperCode)
            .Append("en")
            .Append(chosenCode)
            .Where(code => code is not null && Languages.Value.ContainsKey(code))
            .Distinct()
            .ToList();
        List<VoiceLanguageOption> suggested = [auto, .. suggestedCodes.Select(code => Languages.Value[code!])];
        var more = Languages.Value.Values
            .Where(language => !suggestedCodes.Contains(language.Code))
            .OrderBy(language => language.Name, StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true))
            .ToList();
        return new(suggested, more, suggested.FirstOrDefault(language => language.Code == chosenCode) ?? auto);
    }

    /// <summary>The languages this user reads Lumi and their computer in, and on Windows the ones they type.</summary>
    public static IEnumerable<string?> UserLanguages(CultureInfo appCulture)
    {
        yield return appCulture.Name;
        yield return CultureInfo.InstalledUICulture.Name;
        if (!OperatingSystem.IsWindows())
            yield break;

        string[]? typed = null;
        try
        {
            using var profile = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\International\User Profile");
            typed = profile?.GetValue("Languages") as string[];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        foreach (var language in typed ?? [])
            yield return language;
    }

    /// <summary>Whisper's code for a culture name such as "he-IL", or null when it has none.</summary>
    internal static string? ToWhisperCode(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
            return null;
        var language = culture.Split('-', '_')[0].Trim().ToLowerInvariant();
        return language switch
        {
            "nb" => "no",
            "fil" => "tl",
            "jv" => "jw",
            "iw" => "he",
            "in" => "id",
            "ji" => "yi",
            _ => language,
        };
    }

    private static string NativeName(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code switch { "jw" => "jv", "tl" => "fil", _ => code });
            var name = culture.NativeName;
            return string.IsNullOrWhiteSpace(name) ? code : char.ToUpper(name[0], culture) + name[1..];
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }
}
