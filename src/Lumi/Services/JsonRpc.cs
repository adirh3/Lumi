using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lumi.Services;

internal static class JsonRpc
{
    public static JsonElement DefaultInitializeParams()
    {
        using var document = JsonDocument.Parse("""
            {
              "protocolVersion": "2025-06-18",
              "capabilities": {},
              "clientInfo": {
                "name": "lumi-mcp-proxy",
                "version": "1"
              }
            }
            """);
        return document.RootElement.Clone();
    }

    public static string Request(int id, string method, JsonElement? parameters)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method
        };
        if (parameters is not null)
            obj["params"] = JsonNode.Parse(parameters.Value.GetRawText());
        return obj.ToJsonString();
    }

    public static string Notification(string method, JsonElement? parameters)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method
        };
        if (parameters is not null)
            obj["params"] = JsonNode.Parse(parameters.Value.GetRawText());
        return obj.ToJsonString();
    }

    public static string WithId(JsonElement message, int id)
    {
        var obj = JsonNode.Parse(message.GetRawText())!.AsObject();
        obj["id"] = id;
        return obj.ToJsonString();
    }

    public static string ReplaceId(JsonElement message, JsonElement id)
    {
        var obj = JsonNode.Parse(message.GetRawText())!.AsObject();
        obj["id"] = JsonNode.Parse(id.GetRawText());
        return obj.ToJsonString();
    }

    public static string Response(JsonElement id, JsonElement result)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["result"] = JsonNode.Parse(result.GetRawText())
        };
        return obj.ToJsonString();
    }

    public static string Error(JsonElement? id, int code, string message)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id is null ? null : JsonNode.Parse(id.Value.GetRawText()),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
        return obj.ToJsonString();
    }

    public static string IdKey(JsonElement id)
        => id.ValueKind switch
        {
            JsonValueKind.String => id.GetString() ?? string.Empty,
            JsonValueKind.Number => id.GetRawText(),
            _ => id.GetRawText()
        };
}
