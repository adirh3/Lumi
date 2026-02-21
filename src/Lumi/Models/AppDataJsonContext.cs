using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumi.Models;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppData))]
[JsonSerializable(typeof(JsonDocument))]
[JsonSerializable(typeof(List<ChatMessage>))]
internal partial class AppDataJsonContext : JsonSerializerContext;
