using System.Text.Json.Serialization;

namespace Lumi.Remote.Protocol;

/// <summary>
/// Source-generated metadata for the whole wire contract. Both sides serialize through
/// this context so trimmed/AOT mobile heads never fall back to reflection.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(RemoteHello))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(RemoteBeacon))]
[JsonSerializable(typeof(RemotePairRequest))]
[JsonSerializable(typeof(RemotePairResponse))]
[JsonSerializable(typeof(RemoteEventSubscription))]
[JsonSerializable(typeof(RemoteCommand))]
[JsonSerializable(typeof(RemoteCommandResult))]
[JsonSerializable(typeof(RemoteUploadResponse))]
[JsonSerializable(typeof(RemoteSnapshot))]
[JsonSerializable(typeof(RemoteLibrary))]
[JsonSerializable(typeof(RemoteLibraryItem))]
[JsonSerializable(typeof(RemoteSettings))]
[JsonSerializable(typeof(RemoteTranscript))]
[JsonSerializable(typeof(RemoteTranscriptTurn))]
[JsonSerializable(typeof(RemoteTranscriptItem))]
[JsonSerializable(typeof(RemoteChatStatus))]
[JsonSerializable(typeof(RemoteConnectionStatus))]
[JsonSerializable(typeof(RemoteStreamDelta))]
[JsonSerializable(typeof(RemoteTranscriptInvalidated))]
[JsonSerializable(typeof(RemoteQuestion))]
[JsonSerializable(typeof(RemoteChip))]
[JsonSerializable(typeof(List<RemoteChip>))]
[JsonSerializable(typeof(RemoteChatPage))]
[JsonSerializable(typeof(List<RemoteChatGroup>))]
[JsonSerializable(typeof(List<RemoteChat>))]
public sealed partial class RemoteJsonContext : JsonSerializerContext;
