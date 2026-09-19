using System;
using System.Text.Json;

namespace Lumi.Services;

internal static class AgencyMcpSessionRecovery
{
    internal const McpToolCallPreflightPolicy Policy =
        McpToolCallPreflightPolicy.ToolsListSessionHealth
        | McpToolCallPreflightPolicy.AgencyNotDispatchedSignal;

    internal static bool IsTrustedNotDispatchedResponse(JsonElement response)
        => response.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("agencySessionRecovery", out var recovery)
            && recovery.ValueKind == JsonValueKind.Object
            && recovery.TryGetProperty("businessDispatch", out var businessDispatch)
            && businessDispatch.ValueKind == JsonValueKind.String
            && string.Equals(
                businessDispatch.GetString(),
                "notDispatched",
                StringComparison.Ordinal);
}
