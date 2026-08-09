using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumi.Mobile.Services;

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

    public bool SendWithEnter { get; set; }

    /// <summary>Docked sidebar collapsed on a tablet or unfolded foldable.</summary>
    public bool IsSidebarCollapsed { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(MobileConnectionSettings))]
internal sealed partial class MobileSettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Tiny JSON store under the platform app-data folder. Deliberately mirrors Lumi desktop's
/// "one small JSON file, no database" convention.
/// </summary>
public sealed class MobileSettingsStore
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
        try
        {
            var machine = Environment.MachineName;
            return string.IsNullOrWhiteSpace(machine) ? "Phone" : machine;
        }
        catch (InvalidOperationException)
        {
            return "Phone";
        }
    }
}
