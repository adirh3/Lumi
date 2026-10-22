using System;
using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Lumi.Models;

namespace Lumi.Services.Storage;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(Chat))]
internal partial class ChatStorageJsonContext : JsonSerializerContext;

/// <summary>
/// The JSON shape of chats and messages in the chat database: the same camel-cased properties as the
/// legacy files, without indentation, nulls, empty collections or false flags. Every omitted value is
/// what a freshly constructed object already holds, so reading a row back yields an equal object.
/// </summary>
internal static class ChatStorageJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static JsonTypeInfo<ChatMessage> Message { get; } =
        (JsonTypeInfo<ChatMessage>)Options.GetTypeInfo(typeof(ChatMessage));

    public static JsonTypeInfo<Chat> Chat { get; } =
        (JsonTypeInfo<Chat>)Options.GetTypeInfo(typeof(Chat));

    public static string SerializeMessage(ChatMessage message)
        => JsonSerializer.Serialize(message, Message);

    public static ChatMessage DeserializeMessage(string json)
        => JsonSerializer.Deserialize(json, Message)
           ?? throw new JsonException("A stored chat message was null.");

    public static string SerializeChat(Chat chat)
        => JsonSerializer.Serialize(chat, Chat);

    public static Chat DeserializeChat(string json)
        => JsonSerializer.Deserialize(json, Chat)
           ?? throw new JsonException("A stored chat was null.");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(ChatStorageJsonContext.Default.Options)
        {
            TypeInfoResolver = ChatStorageJsonContext.Default.WithAddedModifier(OmitDefaults),
        };
        options.MakeReadOnly();
        return options;
    }

    // Booleans in the stored types default to false and collections to empty, so both can be left out.
    private static void OmitDefaults(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
            return;

        foreach (var property in typeInfo.Properties)
        {
            if (property.PropertyType == typeof(bool))
                property.ShouldSerialize = static (_, value) => value is true;
            else if (typeof(ICollection).IsAssignableFrom(property.PropertyType))
                property.ShouldSerialize = static (_, value) => value is ICollection { Count: > 0 };
        }
    }
}
