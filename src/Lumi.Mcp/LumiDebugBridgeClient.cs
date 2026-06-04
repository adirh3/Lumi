using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lumi.Mcp;

public sealed record LumiBridgeDiscovery(
    string InstanceId,
    int ProcessId,
    string Url,
    string Token,
    DateTimeOffset StartedAt,
    int ProtocolVersion,
    string? AppDataDir,
    string SourcePath);

public sealed class LumiDebugBridgeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public static string StatusFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lumi",
        "debug-bridge.json");

    public static string StatusDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lumi",
        "debug-bridges");

    public async Task<object> GetStatusOrOfflineAsync(JsonElement? arguments, CancellationToken cancellationToken)
    {
        var discovery = TryReadDiscovery(arguments);
        if (discovery is null)
        {
            return new
            {
                bridgeAvailable = false,
                statusFilePath = StatusFilePath,
                statusDirectory = StatusDirectory,
                target = TargetSelector.From(arguments),
                message = "No Lumi debug bridge discovery file was found. Start Lumi Debug with lumi_launch or dotnet run --project src\\Lumi\\Lumi.csproj -- --debug-agent-harness."
            };
        }

        if (!IsProcessAlive(discovery.ProcessId))
        {
            return new
            {
                bridgeAvailable = false,
                statusFilePath = StatusFilePath,
                statusDirectory = StatusDirectory,
                staleProcessId = discovery.ProcessId,
                target = TargetSelector.From(arguments),
                message = "The Lumi debug bridge discovery file points to a process that is no longer running."
            };
        }

        try
        {
            var status = await GetStatusAsync(discovery, cancellationToken).ConfigureAwait(false);
            return new
            {
                bridgeAvailable = true,
                instanceId = discovery.InstanceId,
                appProcessId = discovery.ProcessId,
                bridgeProcessId = discovery.ProcessId,
                bridgeUrl = discovery.Url,
                appDataDir = discovery.AppDataDir,
                discovery,
                status
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            return new
            {
                bridgeAvailable = false,
                discovery,
                target = TargetSelector.From(arguments),
                error = ex.Message,
                message = "The Lumi debug bridge discovery file exists, but the bridge did not respond."
            };
        }
    }

    public async Task<object> GetStatusForDiscoveryAsync(
        LumiBridgeDiscovery discovery,
        CancellationToken cancellationToken)
    {
        if (!IsProcessAlive(discovery.ProcessId))
        {
            return new
            {
                bridgeAvailable = false,
                discovery,
                staleProcessId = discovery.ProcessId,
                message = "The Lumi debug bridge discovery file points to a process that is no longer running."
            };
        }

        try
        {
            var status = await GetStatusAsync(discovery, cancellationToken).ConfigureAwait(false);
            return new
            {
                bridgeAvailable = true,
                instanceId = discovery.InstanceId,
                appProcessId = discovery.ProcessId,
                bridgeProcessId = discovery.ProcessId,
                bridgeUrl = discovery.Url,
                appDataDir = discovery.AppDataDir,
                discovery,
                status
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            return new
            {
                bridgeAvailable = false,
                discovery,
                error = ex.Message,
                message = "The Lumi debug bridge discovery file exists, but the bridge did not respond."
            };
        }
    }

    public async Task<JsonElement> InvokeAsync(string action, JsonElement? arguments, CancellationToken cancellationToken)
    {
        var discovery = TryReadDiscovery(arguments)
            ?? throw new InvalidOperationException("Lumi debug bridge is not running. Use lumi_launch first.");
        if (!IsProcessAlive(discovery.ProcessId))
            throw new InvalidOperationException($"Lumi debug bridge process {discovery.ProcessId} is not running. Use lumi_launch to start a new debug instance.");

        using var payload = BuildInvokePayload(action, arguments);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{discovery.Url.TrimEnd('/')}/invoke")
        {
            Content = new StringContent(payload.RootElement.GetRawText(), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Lumi-Debug-Token", discovery.Token);

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Lumi debug bridge returned {(int)response.StatusCode}: {body}");

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            throw new InvalidOperationException(document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : "Lumi debug bridge call failed.");
        if (!document.RootElement.TryGetProperty("result", out var result))
            throw new InvalidOperationException("Lumi debug bridge response did not contain a result.");

        return result.Clone();
    }

    public LumiBridgeDiscovery? TryReadDiscovery(JsonElement? arguments = null)
    {
        var target = TargetSelector.From(arguments);
        var discoveries = EnumerateDiscoveries()
            .Where(discovery => IsProcessAlive(discovery.ProcessId))
            .ToList();

        if (!string.IsNullOrWhiteSpace(target.InstanceId))
        {
            return discoveries.FirstOrDefault(discovery =>
                string.Equals(discovery.InstanceId, target.InstanceId, StringComparison.OrdinalIgnoreCase));
        }

        if (target.ProcessId.HasValue)
            return discoveries.FirstOrDefault(discovery => discovery.ProcessId == target.ProcessId.Value);

        if (!string.IsNullOrWhiteSpace(target.AppDataDir))
        {
            var normalizedTargetAppDataDir = NormalizePath(target.AppDataDir);
            discoveries = discoveries
                .Where(discovery => AppDataMatches(discovery.AppDataDir, normalizedTargetAppDataDir))
                .ToList();
        }

        return discoveries
            .OrderByDescending(discovery => string.Equals(discovery.SourcePath, StatusFilePath, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(discovery => discovery.StartedAt)
            .FirstOrDefault();
    }

    public IReadOnlyList<LumiBridgeDiscovery> EnumerateDiscoveries()
    {
        var paths = new List<string>();
        if (File.Exists(StatusFilePath))
            paths.Add(StatusFilePath);
        if (Directory.Exists(StatusDirectory))
        {
            paths.AddRange(Directory.EnumerateFiles(StatusDirectory, "*.json"));
        }

        return paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(TryReadDiscoveryFile)
            .Where(discovery => discovery is not null)
            .Select(discovery => discovery!)
            .GroupBy(discovery => discovery.InstanceId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(discovery =>
                string.Equals(discovery.SourcePath, StatusFilePath, StringComparison.OrdinalIgnoreCase)).First())
            .ToList();
    }

    private static LumiBridgeDiscovery? TryReadDiscoveryFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            return ParseDiscovery(document.RootElement, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static LumiBridgeDiscovery? ParseDiscovery(JsonElement root, string sourcePath)
    {
        if (!root.TryGetProperty("processId", out var processIdElement)
            || !root.TryGetProperty("url", out var urlElement)
            || !root.TryGetProperty("token", out var tokenElement))
        {
            return null;
        }

        return new LumiBridgeDiscovery(
            root.TryGetProperty("instanceId", out var instanceId) ? instanceId.GetString() ?? "" : "",
            processIdElement.GetInt32(),
            urlElement.GetString() ?? "",
            tokenElement.GetString() ?? "",
            root.TryGetProperty("startedAt", out var startedAt) && startedAt.TryGetDateTimeOffset(out var timestamp)
                ? timestamp
                : DateTimeOffset.MinValue,
            root.TryGetProperty("protocolVersion", out var protocolVersion) && protocolVersion.TryGetInt32(out var version)
                ? version
                : 0,
            root.TryGetProperty("appDataDir", out var appDataDir) ? appDataDir.GetString() : null,
            sourcePath);
    }

    public static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<JsonElement> GetStatusAsync(LumiBridgeDiscovery discovery, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{discovery.Url.TrimEnd('/')}/status");
        request.Headers.Add("X-Lumi-Debug-Token", discovery.Token);
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bridgeResponse = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, cancellationToken).ConfigureAwait(false);
        if (!bridgeResponse.TryGetProperty("result", out var result))
            throw new InvalidOperationException("Lumi debug bridge status response did not contain a result.");

        return result.Clone();
    }

    private static JsonDocument BuildInvokePayload(string action, JsonElement? arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("action", action);
            writer.WritePropertyName("arguments");
            if (arguments.HasValue)
                arguments.Value.WriteTo(writer);
            else
                writer.WriteStartObject();
            if (!arguments.HasValue)
                writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray());
    }

    private static string? NormalizePath(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));

    public static bool AppDataMatches(string? discoveredAppDataDir, string? requestedAppDataDir)
    {
        var discovered = NormalizePath(discoveredAppDataDir);
        var requested = NormalizePath(requestedAppDataDir);
        if (discovered is null || requested is null)
            return false;

        if (string.Equals(discovered, requested, StringComparison.OrdinalIgnoreCase))
            return true;

        var requestedLumiDir = Path.Combine(requested, "Lumi");
        if (string.Equals(discovered, requestedLumiDir, StringComparison.OrdinalIgnoreCase))
            return true;

        var discoveredParent = Directory.GetParent(discovered)?.FullName;
        return string.Equals(discoveredParent, requested, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record TargetSelector(string? InstanceId, int? ProcessId, string? AppDataDir)
    {
        public static TargetSelector From(JsonElement? arguments)
        {
            if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
                return new TargetSelector(null, null, null);

            var args = arguments.Value;
            var instanceId = GetString(args, "targetInstanceId")
                             ?? GetString(args, "instanceId")
                             ?? GetString(args, "bridgeInstanceId");
            var processId = GetInt(args, "targetProcessId")
                            ?? GetInt(args, "processId")
                            ?? GetInt(args, "appProcessId")
                            ?? GetInt(args, "bridgeProcessId");
            var appDataDir = GetString(args, "targetAppDataDir")
                             ?? GetString(args, "appDataDir");
            return new TargetSelector(instanceId, processId, appDataDir);
        }

        private static string? GetString(JsonElement args, string propertyName)
        {
            return args.TryGetProperty(propertyName, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static int? GetInt(JsonElement args, string propertyName)
        {
            if (!args.TryGetProperty(propertyName, out var value))
                return null;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
                return parsed;

            return null;
        }
    }
}
