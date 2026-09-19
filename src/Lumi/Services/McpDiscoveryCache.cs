using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lumi.Services;

/// <summary>Disposable discovery metadata, never backend sessions or tool results.</summary>
internal sealed class McpDiscoveryCache(string? directory)
{
    private const int FormatVersion = 1;
    private const int MaximumBytes = 4 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly HashSet<string> _invalidatedKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _keyRevisions = new(StringComparer.Ordinal);
    private long _nextRevision;
    private long _refreshRevision;

    internal long RefreshRevision
    {
        get { lock (_gate) return _refreshRevision; }
    }

    internal long GetRevision(string? key)
    {
        lock (_gate)
            return key is not null && _keyRevisions.TryGetValue(key, out var revision)
                ? revision
                : _refreshRevision;
    }

    internal sealed record Snapshot(JsonElement InitializeResult, JsonElement ToolsResult)
    {
        internal DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    }

    internal static bool IsPlainListRequest(JsonElement message)
    {
        if (!message.TryGetProperty("params", out var parameters))
            return true;
        // Progress tokens correlate optional notifications; they do not select a catalog.
        return parameters.ValueKind == JsonValueKind.Object
            && parameters.EnumerateObject().All(parameter =>
                parameter.Name == "_meta"
                && parameter.Value.ValueKind == JsonValueKind.Object
                && parameter.Value.EnumerateObject().All(metadata =>
                    metadata.Name == "progressToken"
                    && metadata.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number));
    }

    internal static bool IsCacheableClient(JsonElement parameters)
        => parameters.ValueKind == JsonValueKind.Object
           && parameters.EnumerateObject().All(p => p.Name is "protocolVersion" or "capabilities" or "clientInfo")
           && parameters.TryGetProperty("protocolVersion", out var version)
           && version.ValueKind == JsonValueKind.String
           && version.GetString() is "2024-11-05" or "2025-03-26" or "2025-06-18" or "2025-11-25"
           && parameters.TryGetProperty("capabilities", out var capabilities)
           && IsCacheableClientCapabilities(capabilities)
           && parameters.TryGetProperty("clientInfo", out var clientInfo)
           && clientInfo.ValueKind == JsonValueKind.Object;

    private static bool IsCacheableClientCapabilities(JsonElement capabilities)
    {
        if (capabilities.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var capability in capabilities.EnumerateObject())
        {
            // Copilot advertises these even for callback-free, tools-only servers. Actual
            // callbacks taint discovery; roots and unknown/context-bearing extensions stay live.
            if (capability.Name == "sampling" && IsEmptyObject(capability.Value))
                continue;
            if (capability.Name != "extensions" || capability.Value.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var extension in capability.Value.EnumerateObject())
            {
                if (extension.Name == "io.modelcontextprotocol/tasks" && IsEmptyObject(extension.Value))
                    continue;
                if (extension.Name != "io.modelcontextprotocol/ui" || extension.Value.ValueKind != JsonValueKind.Object
                    || extension.Value.EnumerateObject().Any(p => p.Name != "mimeTypes")
                    || !extension.Value.TryGetProperty("mimeTypes", out var mimeTypes)
                    || mimeTypes.ValueKind != JsonValueKind.Array
                    || mimeTypes.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
                    return false;
            }
        }
        return true;
    }

    internal static bool IsCacheableInitialization(JsonElement parameters, JsonElement initialize)
    {
        return IsCacheableClient(parameters)
            && initialize.ValueKind == JsonValueKind.Object
            && initialize.EnumerateObject().All(p => p.Name is "protocolVersion" or "capabilities" or "serverInfo" or "instructions")
            && initialize.TryGetProperty("protocolVersion", out var version)
            && JsonElement.DeepEquals(version, parameters.GetProperty("protocolVersion"))
            && initialize.TryGetProperty("serverInfo", out var serverInfo)
            && serverInfo.ValueKind == JsonValueKind.Object
            && serverInfo.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(name.GetString())
            && (!initialize.TryGetProperty("instructions", out var instructions) || instructions.ValueKind == JsonValueKind.String)
            && initialize.TryGetProperty("capabilities", out var capabilities)
            && capabilities.ValueKind == JsonValueKind.Object
            && capabilities.EnumerateObject().All(p => p.Name == "tools")
            && capabilities.TryGetProperty("tools", out var toolsCapability)
            && toolsCapability.ValueKind == JsonValueKind.Object
            && toolsCapability.EnumerateObject().All(p =>
                p.Name == "listChanged" && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False);
    }

    internal static bool IsCacheable(JsonElement parameters, JsonElement initialize, JsonElement list)
    {
        if (!IsCacheableInitialization(parameters, initialize)
            || list.ValueKind != JsonValueKind.Object
            || list.EnumerateObject().Any(p => p.Name != "tools")
            || !list.TryGetProperty("tools", out var tools)
            || tools.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        return tools.EnumerateArray().All(tool =>
            tool.ValueKind == JsonValueKind.Object
            && tool.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(name.GetString())
            && names.Add(name.GetString()!)
            && tool.TryGetProperty("inputSchema", out var schema)
            && schema.ValueKind == JsonValueKind.Object);
    }

    internal static JsonElement CreateProxyInitializeResult(JsonElement initialize)
    {
        var result = JsonNode.Parse(initialize.GetRawText())!.AsObject();
        // The proxy serves a client-specific snapshot, not the backend's notification stream.
        result["capabilities"]!["tools"]!["listChanged"] = false;
        using var document = JsonDocument.Parse(result.ToJsonString());
        return document.RootElement.Clone();
    }

    internal static bool HasCompatibleInitialization(JsonElement expected, JsonElement actual)
        => SameProperty(expected, actual, "protocolVersion")
           && expected.TryGetProperty("serverInfo", out var expectedInfo)
           && actual.TryGetProperty("serverInfo", out var actualInfo)
           && expectedInfo.ValueKind == JsonValueKind.Object
           && actualInfo.ValueKind == JsonValueKind.Object
           && SameProperty(expectedInfo, actualInfo, "name")
           && SameProperty(expected, actual, "instructions");

    internal static bool IsToolCompatible(JsonElement expectedList, JsonElement actualList, string toolName)
    {
        if (!TryFindTool(expectedList, toolName, out var expected)
            || !TryFindTool(actualList, toolName, out var actual))
            return false;
        return JsonElement.DeepEquals(expected, actual);
    }

    internal static bool TryFindTool(JsonElement list, string toolName, out JsonElement result)
    {
        result = default;
        if (list.ValueKind != JsonValueKind.Object || !list.TryGetProperty("tools", out var tools)
            || tools.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind == JsonValueKind.Object && tool.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && string.Equals(name.GetString(), toolName, StringComparison.Ordinal))
            {
                if (result.ValueKind != JsonValueKind.Undefined)
                    return false;
                result = tool;
            }
        }
        return result.ValueKind != JsonValueKind.Undefined;
    }

    private static bool SameProperty(JsonElement expected, JsonElement actual, string property)
    {
        if (expected.ValueKind != JsonValueKind.Object || actual.ValueKind != JsonValueKind.Object)
            return false;
        var expectedHasValue = expected.TryGetProperty(property, out var expectedValue);
        var actualHasValue = actual.TryGetProperty(property, out var actualValue);
        return expectedHasValue == actualHasValue
               && (!expectedHasValue || JsonElement.DeepEquals(expectedValue, actualValue));
    }

    private static bool IsEmptyObject(JsonElement value)
        => value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any();

    internal static string CreateKey(string identity, JsonElement parameters, ProcessStartInfo startInfo)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(identity);
            WriteCanonical(writer, parameters);
            writer.WriteStringValue(Path.GetFullPath(
                string.IsNullOrEmpty(startInfo.WorkingDirectory) ? Environment.CurrentDirectory : startInfo.WorkingDirectory));
            foreach (var pair in startInfo.Environment.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WriteStringValue(pair.Key);
                writer.WriteStringValue(pair.Value);
            }

            var command = startInfo.FileName;
            if (!Path.IsPathRooted(command) && !command.Contains(Path.DirectorySeparatorChar))
            {
                var path = startInfo.Environment.FirstOrDefault(p =>
                    p.Key.Equals("PATH", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).Value;
                command = (path ?? "").Split(Path.PathSeparator)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => Path.Combine(p.Trim('"'), startInfo.FileName))
                    .FirstOrDefault(File.Exists) ?? command;
            }
            if (OperatingSystem.IsWindows() && !Path.IsPathRooted(command))
                command = Path.GetFullPath(command, Environment.CurrentDirectory);
            writer.WriteStringValue(command);

            // Track direct executable/script changes without pretending to fingerprint a package's dependencies.
            foreach (var candidate in new[] { command }.Concat(startInfo.ArgumentList))
            {
                if (candidate.Length == 0 || candidate.Length > 1024 || candidate.StartsWith('-')
                    || candidate.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                    continue;
                var file = new FileInfo(Path.IsPathRooted(candidate)
                    ? candidate
                    : Path.Combine(string.IsNullOrEmpty(startInfo.WorkingDirectory)
                        ? Environment.CurrentDirectory : startInfo.WorkingDirectory, candidate));
                if (!file.Exists)
                    continue;
                writer.WriteStringValue(file.FullName);
                writer.WriteNumberValue(file.Length);
                writer.WriteNumberValue(file.LastWriteTimeUtc.Ticks);
            }
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray())
                WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else
            value.WriteTo(writer);
    }

    internal Snapshot? Read(string key, JsonElement parameters)
    {
        if (directory is null)
            return null;
        lock (_gate)
        {
            if (_invalidatedKeys.Contains(key))
                return null;
            var path = Path.Combine(directory, key + ".json");
            try
            {
                if (!File.Exists(path))
                    return null;
                if (new FileInfo(path).Length > MaximumBytes)
                    throw new JsonException("Discovery cache exceeds the size limit.");
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                    || !version.TryGetInt32(out var number) || number != FormatVersion
                    || !root.TryGetProperty("createdAtUtc", out var created) || created.ValueKind != JsonValueKind.String
                    || !created.TryGetDateTimeOffset(out var timestamp)
                    || !root.TryGetProperty("initializeResult", out var initialize)
                    || !root.TryGetProperty("toolsResult", out var tools)
                    || !IsCacheable(parameters, initialize, tools))
                {
                    DeleteEntry(key);
                    return null;
                }
                return new Snapshot(initialize.Clone(), tools.Clone()) { CreatedAtUtc = timestamp };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Trace.TraceWarning("MCP discovery cache read failed: {0}", ex.Message);
                DeleteEntry(key);
                return null;
            }
        }
    }

    internal void Write(string key, Snapshot snapshot, long? expectedRevision = null)
    {
        if (directory is null)
            return;
        lock (_gate)
        {
            // Invalidation can arrive on stdout while a discovery response is being recorded.
            if (expectedRevision is { } revision && revision != GetRevision(key))
                return;
            string? temporaryPath = null;
            try
            {
                var json = new JsonObject
                {
                    ["version"] = FormatVersion,
                    ["createdAtUtc"] = DateTimeOffset.UtcNow,
                    ["initializeResult"] = JsonNode.Parse(snapshot.InitializeResult.GetRawText()),
                    ["toolsResult"] = JsonNode.Parse(snapshot.ToolsResult.GetRawText())
                }.ToJsonString();
                if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
                {
                    Trace.TraceWarning("MCP discovery catalog exceeds the cache size limit; keeping discovery live.");
                    return;
                }
                Directory.CreateDirectory(directory);
                temporaryPath = Path.Combine(directory, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, Path.Combine(directory, key + ".json"), overwrite: true);
                _invalidatedKeys.Remove(key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("MCP discovery cache write failed: {0}", ex.Message);
            }
            finally
            {
                if (temporaryPath is not null)
                {
                    try { File.Delete(temporaryPath); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Trace.TraceWarning("MCP discovery cache temporary file cleanup failed: {0}", ex.Message);
                    }
                }
            }
        }
    }

    internal void Invalidate(string key)
    {
        lock (_gate)
        {
            _keyRevisions[key] = ++_nextRevision;
            _invalidatedKeys.Add(key);
            DeleteEntry(key);
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _refreshRevision = ++_nextRevision;
            _keyRevisions.Clear();
            if (directory is null || !Directory.Exists(directory))
                return;
            var files = Directory.GetFiles(directory, "*.json");
            foreach (var file in files)
                _invalidatedKeys.Add(Path.GetFileNameWithoutExtension(file));
            foreach (var file in files)
                File.Delete(file);
        }
    }

    private void DeleteEntry(string key)
    {
        if (directory is null)
            return;
        try { File.Delete(Path.Combine(directory, key + ".json")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("MCP discovery cache invalidation failed: {0}", ex.Message);
        }
    }
}
