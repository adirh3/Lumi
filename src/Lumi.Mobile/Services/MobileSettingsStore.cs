using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.Services;

public interface IMobileSettingsStore
{
    MobileConnectionSettings Load();

    void Save(MobileConnectionSettings settings);
}

/// <summary>What the phone remembers between launches.</summary>
public sealed class MobileConnectionSettings
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Token { get; set; } = "";
    public string HostName { get; set; } = "";

    /// <summary>
    /// "System", "Light" or "Dark". A string rather than the enum so an unrecognised value from a
    /// newer build degrades to the default instead of failing to deserialise the whole file.
    /// </summary>
    public string Theme { get; set; } = "System";

    /// <summary>Empty follows the device at startup; explicit choices use a supported UI culture code.</summary>
    public string Language { get; set; } = "";

    /// <summary>The accent palette's id, as the desktop names its palettes. Unknown ids show Lumi's own.</summary>
    public string AccentColor { get; set; } = "lumi";

    /// <summary>Lumi's text size, in percent of the phone's own; on top of the phone's font size.</summary>
    public int TextSize { get; set; } = 100;

    /// <summary>"Off", "Subtle", "Balanced" or "Vivid": how brightly Lumi's glow shines behind the chat.</summary>
    public string Glow { get; set; } = "Balanced";

    /// <summary>Lets the glow drift and breathe while Lumi works, which costs battery; off by default.</summary>
    public bool AnimateGlowWhileWorking { get; set; }

    /// <summary>Docked sidebar collapsed on a tablet or unfolded foldable.</summary>
    public bool IsSidebarCollapsed { get; set; }

    /// <summary>The PC may notify this phone while the user is away from it. Off until the user turns it on.</summary>
    public bool NotificationsEnabled { get; set; }

    /// <summary>The user answered the offer to turn notifications on, either way; it is not offered again.</summary>
    public bool NotificationsPromptAnswered { get; set; }

    /// <summary>The Home Screen suggestion was answered; Settings can still open it again.</summary>
    public bool WebAppInstallPromptAnswered { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(MobileConnectionSettings))]
internal sealed partial class MobileSettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Tiny JSON store under the platform app-data folder. Deliberately mirrors Lumi desktop's
/// "one small JSON file, no database" convention.
/// </summary>
public sealed class MobileSettingsStore : IMobileSettingsStore
{
    private readonly string _path;

    public MobileSettingsStore(string? directory = null)
    {
        var root = directory
                   ?? Environment.GetEnvironmentVariable("LUMI_MOBILE_DATA_DIR")
                   ?? Path.Combine(
                       Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                       "LumiMobile");

        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "connection.json");
    }

    public MobileConnectionSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize(
                    File.ReadAllText(_path),
                    MobileSettingsJsonContext.Default.MobileConnectionSettings);

                if (loaded is not null)
                    return EnsureIdentity(loaded);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"[Mobile] Could not read settings: {ex.Message}");
        }

        return EnsureIdentity(new MobileConnectionSettings());
    }

    public void Save(MobileConnectionSettings settings)
    {
        try
        {
            File.WriteAllText(
                _path,
                JsonSerializer.Serialize(settings, MobileSettingsJsonContext.Default.MobileConnectionSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"[Mobile] Could not save settings: {ex.Message}");
        }
    }

    private static MobileConnectionSettings EnsureIdentity(MobileConnectionSettings settings)
    {
        if (settings.DeviceId.Length == 0)
            settings.DeviceId = Guid.NewGuid().ToString("n");

        if (settings.DeviceName.Length == 0)
            settings.DeviceName = DefaultDeviceName();

        return settings;
    }

    private static string DefaultDeviceName()
    {
        if (MobilePlatformServices.DeviceNameOverride is { Length: > 0 } platformName)
            return platformName;

        try
        {
            var machine = Environment.MachineName;
            return string.IsNullOrWhiteSpace(machine) ? Strings.Get("Phone") : machine;
        }
        catch (InvalidOperationException)
        {
            return Strings.Get("Phone");
        }
    }
}
