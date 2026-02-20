using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumi.Models;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppData))]
[JsonSerializable(typeof(JsonDocument))]
internal partial class AppDataJsonContext : JsonSerializerContext;
