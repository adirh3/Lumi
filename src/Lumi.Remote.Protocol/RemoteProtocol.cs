namespace Lumi.Remote.Protocol;

using System.Net;
using System.Net.Sockets;

/// <summary>
/// Constants shared by the Lumi desktop remote server and every remote client.
/// </summary>
public static class RemoteProtocol
{
    /// <summary>Bumped whenever the wire shape changes incompatibly.</summary>
    public const int Version = 5;

    /// <summary>
    /// This pre-release protocol has one compatibility baseline. Additive features use capabilities;
    /// this number changes only for a genuinely incompatible wire contract.
    /// </summary>
    public const int MinimumMobileCompatibleVersion = Version;

    public static bool IsCompatibleVersion(int version) => version == Version;

    public static bool HasRequiredCapabilities(IEnumerable<string>? capabilities)
    {
        if (capabilities is null)
            return false;
        var offered = new HashSet<string>(capabilities, StringComparer.Ordinal);
        return Capabilities.Required.All(offered.Contains);
    }

    /// <summary>Default TCP port the desktop listener binds for LAN clients.</summary>
    public const int DefaultPort = 47653;

    /// <summary>UDP port used for the LAN discovery handshake.</summary>
    public const int DiscoveryPort = 47654;

    /// <summary>Magic prefix a client broadcasts to ask Lumi desktops to announce themselves.</summary>
    public const string DiscoveryProbe = "LUMI-DISCOVER-V3";

    /// <summary>Magic prefix prepended to the JSON beacon a desktop replies with.</summary>
    public const string DiscoveryBeacon = "LUMI-BEACON-V3 ";

    /// <summary>Header carrying the device token issued during pairing.</summary>
    public const string DeviceTokenHeader = "X-Lumi-Device-Token";

    /// <summary>Header carrying the stable client-generated device id.</summary>
    public const string DeviceIdHeader = "X-Lumi-Device-Id";

    /// <summary>UTF-8 encoded leaf filename for a raw authenticated upload body.</summary>
    public const string UploadFileNameHeader = "X-Lumi-File-Name";

    /// <summary>How long a pairing code stays valid.</summary>
    public static readonly TimeSpan PairingCodeLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum raw messages projected into one mobile transcript response. Paging is deliberately
    /// defined over raw messages rather than projected turns so hidden reasoning/tool preferences
    /// cannot create gaps or make a page unbounded.
    /// </summary>
    public const int TranscriptWindowRawMessageLimit = 100;
    public const int InitialTranscriptWindowRawMessageLimit = 40;

    /// <summary>
    /// Cumulative source-text budget for one transcript window. A single oversized message is still
    /// included so paging always advances; its projected strings are then truncated by the limits
    /// below.
    /// </summary>
    public const int TranscriptWindowTextBudgetCharacters = 128 * 1024;

    public const int MobileUserTextLimit = 24 * 1024;
    public const int MobileAssistantTextLimit = 32 * 1024;
    public const int MobileReasoningTextLimit = 16 * 1024;
    public const int MobileTerminalTextLimit = 24 * 1024;
    public const int MobileToolInputLimit = 8 * 1024;
    public const int MobileToolOutputLimit = 24 * 1024;
    public const int MobileQuestionTextLimit = 8 * 1024;
    public const int MobileQuestionOptionLimit = 4 * 1024;
    public const int MobileQuestionAnswerLimit = 8 * 1024;
    public const int MobileSourceSnippetLimit = 4 * 1024;
    public const int MobilePlanTextLimit = 24 * 1024;
    public const int MobileTranscriptTitleLimit = 4 * 1024;
    public const int MobileMetadataTextLimit = 2 * 1024;
    public const int MobileStatusTextLimit = 4 * 1024;
    public const int MobileIdentifierLimit = 512;
    public const int MobilePathLimit = 8 * 1024;
    public const int MobileUrlLimit = 8 * 1024;
    public const int MobileFileNameLimit = 1024;
    public const int MobileFileExtensionLimit = 128;
    public const int MobileSourceTitleLimit = 2 * 1024;
    public const int MobileStatusValueLimit = 512;

    public const int MobileQuestionOptionCountLimit = 32;
    public const int MobileSourceCountLimit = 24;
    public const int MobileAttachmentCountLimit = 24;
    public const int MobileToolCallCountLimit = 32;
    public const int MobileActivityToolCountLimit = 32;
    public const int MobileFileChangeCountLimit = 64;
    public const int MobileActivityToolInputLimit = 4 * 1024;
    public const int MobileActivityToolOutputLimit = 12 * 1024;
    public const int MobileStatusCollectionCountLimit = 32;
    public const int ChatPageSize = 120;
    public const int MaxChatPageSize = 240;
    public const int MobileLibraryPreviewLimit = 512;

    /// <summary>
    /// Hard UTF-8 JSON ceiling for a projected transcript. The source window remains 100 raw
    /// messages / 128 KiB of source text; pathological metadata is compacted explicitly if the
    /// fully projected wire shape would exceed this independent response limit.
    /// </summary>
    public const int MobileTranscriptJsonByteLimit = 1_250_000;

    public const int MaxHandshakeJsonBytes = 64 * 1024;
    public const int MaxCommandResponseJsonBytes = 256 * 1024;
    public const int MaxSnapshotJsonBytes = 4 * 1024 * 1024;
    public const int MaxChatsJsonBytes = 3 * 1024 * 1024;
    public const int MaxLibraryJsonBytes = 4 * 1024 * 1024;
    public const int MaxLibraryItemJsonBytes = 2 * 1024 * 1024;
    public const int MaxActivityJsonBytes = 768 * 1024;
    // Snapshot/library payloads are compact single-line JSON. The SSE reader and queue must accept
    // every payload the protocol permits, plus the small event/data framing overhead.
    public const int MaxSseLineBytes = MaxSnapshotJsonBytes + 1024;
    public const int MaxSseFrameBytes = MaxSnapshotJsonBytes + 16 * 1024;

    public const string MobileTruncationMarker =
        "\n\n[truncated on mobile; open desktop for full output]";

    /// <summary>Keeps a wire string within a mobile-safe ceiling while making data loss explicit.</summary>
    public static string? TruncateForMobile(string? value, int maxCharacters)
    {
        if (value is null || value.Length <= maxCharacters)
            return value;

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxCharacters, MobileTruncationMarker.Length);

        var prefixLength = maxCharacters - MobileTruncationMarker.Length;
        if (prefixLength > 0 && char.IsHighSurrogate(value[prefixLength - 1]))
            prefixLength--;

        return string.Concat(
            value.AsSpan(0, prefixLength),
            MobileTruncationMarker.AsSpan());
    }

    /// <summary>True for Tailscale's IPv4 CGNAT and IPv6 ULA address ranges.</summary>
    public static bool IsTailscaleAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        var bytesV6 = address.GetAddressBytes();
        return bytesV6[0] == 0xfd
               && bytesV6[1] == 0x7a
               && bytesV6[2] == 0x11
               && bytesV6[3] == 0x5c
               && bytesV6[4] == 0xa1
               && bytesV6[5] == 0xe0;
    }

    public static class Routes
    {
        /// <summary>Unauthenticated: reports protocol version, host name and pairing state.</summary>
        public const string Hello = "/lumi/hello";

        /// <summary>Unauthenticated: exchanges a pairing code for a long-lived device token.</summary>
        public const string Pair = "/lumi/pair";

        /// <summary>Authenticated snapshot of everything the client needs to render.</summary>
        public const string Snapshot = "/lumi/snapshot";

        /// <summary>Authenticated Server-Sent Events stream of live updates.</summary>
        public const string Events = "/lumi/events";
        /// <summary>Updates which live entities the current device wants pushed over SSE.</summary>
        public const string Subscription = "/lumi/subscription";

        /// <summary>Authenticated chat list.</summary>
        public const string Chats = "/lumi/chats";

        /// <summary>Authenticated full editable library item.</summary>
        public const string LibraryItem = "/lumi/library-item";

        /// <summary>Authenticated transcript read: <c>/lumi/transcript?chatId=...</c></summary>
        public const string Transcript = "/lumi/transcript";

        /// <summary>Authenticated technical details for one compact activity summary.</summary>
        public const string Activity = "/lumi/activity";

        /// <summary>Authenticated announced-file download by chat/message identity.</summary>
        public const string File = "/lumi/file";

        /// <summary>Authenticated command endpoint: <c>{ action, arguments }</c>.</summary>
        public const string Command = "/lumi/command";

        /// <summary>
        /// Authenticated file upload: a raw <c>application/octet-stream</c> body with the leaf
        /// filename in <see cref="UploadFileNameHeader"/>, returning the absolute path on the PC.
        ///
        /// <para>Lumi reads files by path — it runs on the PC and has the filesystem. So attaching
        /// something from the phone means getting the bytes onto the PC first and then telling Lumi
        /// where they landed. Raw bytes avoid the several-hundred-megabyte base64/JSON peak that can
        /// otherwise kill a phone near the upload ceiling.</para>
        /// </summary>
        public const string Upload = "/lumi/upload";
    }

    /// <summary>
    /// Additive protocol features. Clients enable behavior by capability rather than assuming every
    /// future server version has an identical feature set.
    /// </summary>
    public static class Capabilities
    {
        public const string ScopedEventsV1 = "scoped-events-v1";
        public const string CompactTranscriptV1 = "compact-transcript-v1";

        public static IReadOnlyList<string> Required { get; } = [ScopedEventsV1];
        public static IReadOnlyList<string> Server { get; } = [ScopedEventsV1, CompactTranscriptV1];
    }

    /// <summary>Largest upload the desktop will accept, so a phone cannot exhaust its disk.</summary>
    public const long MaxUploadBytes = 64L * 1024 * 1024;
    public const long MaxUploadBytesPerDevice = 256L * 1024 * 1024;
    public const long MaxUploadBytesTotal = 1024L * 1024 * 1024;

    /// <summary>Largest produced file the phone will download into its app cache.</summary>
    public const long MaxDownloadBytes = 64L * 1024 * 1024;

    /// <summary>Command verbs accepted by <see cref="Routes.Command"/>.</summary>
    public static class Actions
    {
        public const string CreateChat = "create_chat";
        /// <summary>
        /// Compatibility command for pre-1.1.3 clients. Browsing no longer activates the desktop
        /// surface; the server only validates that the chat still exists.
        /// </summary>
        public const string OpenChat = "open_chat";
        public const string DeleteChat = "delete_chat";
        public const string RenameChat = "rename_chat";
        public const string PinChat = "pin_chat";
        public const string SendMessage = "send_message";
        public const string StopGeneration = "stop_generation";
        public const string RevokeDevice = "revoke_device";
        public const string AnswerQuestion = "answer_question";

        /// <summary>
        /// Sets composer configuration on the open chat — model, reasoning effort, context-window
        /// tier, agent, project, and skill / MCP attachment. Only the arguments present are applied,
        /// so the phone can change one picker without echoing the whole configuration back.
        /// </summary>
        public const string ConfigureChat = "configure_chat";

        public const string ConfigureFeature = "configure_feature";
    }

    /// <summary>Event names emitted on the SSE stream.</summary>
    public static class Events
    {
        /// <summary>Full snapshot, always sent first on a fresh stream.</summary>
        public const string Snapshot = "snapshot";

        /// <summary>Chat list / grouping changed.</summary>
        public const string Chats = "chats";

        /// <summary>Busy / streaming / status / token usage of a chat changed.</summary>
        public const string ChatStatus = "chat-status";

        /// <summary>A chat transcript changed and should be refetched.</summary>
        public const string TranscriptInvalidated = "transcript-invalidated";

        /// <summary>Hot streaming text for the active assistant message.</summary>
        public const string StreamDelta = "stream-delta";

        /// <summary>A library collection (projects/skills/lumis/...) changed.</summary>
        public const string Library = "library";

        /// <summary>Copilot connection state changed.</summary>
        public const string Connection = "connection";

        /// <summary>Keep-alive comment ping.</summary>
        public const string Ping = "ping";
    }

    /// <summary>Library resource names accepted by <see cref="Actions.ConfigureFeature"/>.</summary>
    public static class Resources
    {
        public const string Projects = "projects";
        public const string Skills = "skills";
        public const string Lumis = "lumis";
        public const string Mcps = "mcps";
        public const string Memories = "memories";
        public const string Jobs = "jobs";
    }

    /// <summary>Discriminators for <see cref="RemoteTranscriptItem.Kind"/>.</summary>
    public static class ItemKinds
    {
        public const string User = "user";
        public const string Assistant = "assistant";
        public const string Reasoning = "reasoning";
        public const string ToolGroup = "tool-group";
        public const string Tool = "tool";
        public const string Terminal = "terminal";
        public const string Question = "question";
        public const string Error = "error";
        public const string Typing = "typing";
        public const string Activity = "activity";

        /// <summary>A file Lumi produced and announced, shown as a tappable attachment chip.</summary>
        public const string File = "file";
    }
}
