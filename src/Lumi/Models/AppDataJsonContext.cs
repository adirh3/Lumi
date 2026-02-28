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
[JsonSerializable(typeof(List<McpServer>))]
[JsonSerializable(typeof(List<string>))]
internal partial class AppDataJsonContext : JsonSerializerContext;
