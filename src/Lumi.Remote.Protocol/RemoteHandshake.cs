namespace Lumi.Remote.Protocol;

/// <summary>Unauthenticated handshake payload returned by <c>/lumi/hello</c>.</summary>
public sealed class RemoteHello
{
    public int ProtocolVersion { get; set; } = RemoteProtocol.Version;
    public string InstanceId { get; set; } = "";
    public string HostName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string AppVersion { get; set; } = "";
    /// <summary>True when the calling device already holds a valid token.</summary>
    public bool IsPaired { get; set; }
}

/// <summary>What a Lumi desktop broadcasts over UDP so phones can find it.</summary>
public sealed class RemoteBeacon
{
    public int ProtocolVersion { get; set; } = RemoteProtocol.Version;
    public string InstanceId { get; set; } = "";
    public string HostName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; } = RemoteProtocol.DefaultPort;

    public string BaseUrl => $"http://{Address}:{Port}";
}

public sealed class RemotePairRequest
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Code { get; set; } = "";
}

public sealed class RemotePairResponse
{
    public bool Ok { get; set; }
    public string? Token { get; set; }
    public string? Error { get; set; }
    public string HostName { get; set; } = "";
    public string UserName { get; set; } = "";
}

/// <summary>Envelope for every <c>/lumi/command</c> call.</summary>
public sealed class RemoteCommand
{
    public string Action { get; set; } = "";
    /// <summary>
    /// Optional client-generated idempotency key. Retrying an unchanged command with the same ID
    /// returns the original result instead of running the command again.
    /// </summary>
    public string? RequestId { get; set; }
    public Dictionary<string, string?> Arguments { get; set; } = new();

    public RemoteCommand() { }

    public RemoteCommand(string action)
    {
        Action = action;
    }

    public RemoteCommand With(string key, string? value)
    {
        Arguments[key] = value;
        return this;
    }

    public string? Get(string key) => Arguments.TryGetValue(key, out var value) ? value : null;

    public bool? GetBool(string key) =>
        bool.TryParse(Get(key), out var parsed) ? parsed : null;

    public Guid? GetGuid(string key) =>
        Guid.TryParse(Get(key), out var parsed) ? parsed : null;

    public int? GetInt(string key) =>
        int.TryParse(Get(key), System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    /// <summary>Reads a value that was written as a newline-delimited list.</summary>
    public string[]? GetList(string key)
    {
        var raw = Get(key);
        if (raw is null)
            return null;
        return raw.Length == 0
            ? []
            : raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public RemoteCommand WithList(string key, IEnumerable<string>? values)
    {
        Arguments[key] = values is null ? null : string.Join('\n', values);
        return this;
    }
}

/// <summary>Uniform response for every <c>/lumi/command</c> call.</summary>
public sealed class RemoteCommandResult
{
    public bool Ok { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    /// <summary>The command request ID, echoed by the server and retained on client-side failures.</summary>
    public string? RequestId { get; set; }
    /// <summary>True only when the finite mobile request deadline elapsed.</summary>
    public bool IsTimeout { get; set; }
    /// <summary>Set when the command created or targeted a chat.</summary>
    public Guid? ChatId { get; set; }
}

/// <summary>Where an uploaded file landed on the PC, so a message can point Lumi at it.</summary>
public sealed class RemoteUploadResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }

    /// <summary>Absolute path on the PC. Lumi reads files by path.</summary>
    public string? Path { get; set; }

    public string? FileName { get; set; }
}
