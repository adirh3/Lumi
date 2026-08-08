using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Lumi.Remote.Protocol;

namespace Lumi.Mobile.Tests;

/// <summary>
/// A loopback stand-in for Lumi desktop that speaks the real wire protocol. Tests drive the real
/// <c>LumiRemoteClient</c> over real sockets against it, so pairing, auth, SSE framing and JSON are
/// all exercised for real rather than mocked away.
/// </summary>
public sealed class FakeLumiDesktop : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<StreamWriter> _subscribers = [];
    private readonly SemaphoreSlim _subscriberGate = new(1, 1);
    private readonly TaskCompletionSource _firstSubscriber =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _eventRequestCount;
    private int _snapshotRequestCount;
    private int _transcriptRequestCount;

    private Task? _loop;
    private UdpClient? _discovery;
    private Task? _discoveryLoop;

    public FakeLumiDesktop()
    {
        Port = GetFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    public int Port { get; private set; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public string HostName { get; set; } = "TEST-PC";

    public string UserName { get; set; } = "Adir";

    public string? PairingCode { get; set; } = "123456";

    public string? IssuedToken { get; private set; }

    public RemoteSnapshot Snapshot { get; set; } = new();
    public List<RemoteChatGroup>? ChatCatalog { get; set; }
    public TaskCompletionSource? ChatRequestStarted { get; set; }
    public TaskCompletionSource? ReleaseChatResponse { get; set; }

    public RemoteTranscript Transcript { get; set; } = new();

    public List<RemoteCommand> ReceivedCommands { get; } = [];
    public Func<RemoteCommand?, RemoteCommandResult>? CommandResultFactory { get; set; }

    /// <summary>The last file the phone uploaded, so tests can assert on the transfer itself.</summary>
    public string? UploadedFileName { get; private set; }

    public byte[]? UploadedBytes { get; private set; }

    /// <summary>Where the fake claims the file landed — the path a message should then reference.</summary>
    public string? UploadedPath { get; private set; }

    /// <summary>Completes as soon as a client has attached to the event stream.</summary>
    public Task SubscriberConnected => _firstSubscriber.Task;

    public int EventRequestCount => Volatile.Read(ref _eventRequestCount);

    public int SnapshotRequestCount => Volatile.Read(ref _snapshotRequestCount);

    public int TranscriptRequestCount => Volatile.Read(ref _transcriptRequestCount);

    /// <summary>
    /// Binds and starts serving.
    ///
    /// <para>Retries on a port collision. <see cref="GetFreePort"/> asks the OS for a free port and
    /// then releases it before <see cref="HttpListener"/> binds, so a test running in parallel can
    /// take that port in the gap — which showed up as roughly one spurious failure per three full
    /// runs. Re-rolling the port is enough; the window is tiny and uncorrelated.</para>
    /// </summary>
    public void Start()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                _listener.Start();
                break;
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                _listener.Prefixes.Clear();
                Port = GetFreePort();
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            }
        }

        _loop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Answers UDP discovery probes, so the client's LAN search can be tested for real.</summary>
    public void StartDiscovery(int port = RemoteProtocol.DiscoveryPort)
    {
        _discovery = new UdpClient(AddressFamily.InterNetwork);
        _discovery.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _discovery.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        _discoveryLoop = Task.Run(DiscoveryLoopAsync);
    }

    public async Task PushAsync(string eventName, string json)
    {
        var wire = new RemoteEventFrame(eventName, json).ToWire();

        await _subscriberGate.WaitAsync();
        try
        {
            foreach (var writer in _subscribers.ToList())
            {
                try
                {
                    await writer.WriteAsync(wire);
                    await writer.FlushAsync();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    _subscribers.Remove(writer);
                }
            }
        }
        finally
        {
            _subscriberGate.Release();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "";
        var token = context.Request.Headers[RemoteProtocol.DeviceTokenHeader];

        switch (path)
        {
            case RemoteProtocol.Routes.Hello:
                await WriteJsonAsync(context, JsonSerializer.Serialize(
                    new RemoteHello
                    {
                        InstanceId = "fake",
                        HostName = HostName,
                        UserName = UserName,
                        AppVersion = "test",
                        IsPaired = IssuedToken is not null && token == IssuedToken
                    },
                    RemoteJsonContext.Default.RemoteHello));
                return;

            case RemoteProtocol.Routes.Pair:
            {
                var body = await new StreamReader(context.Request.InputStream).ReadToEndAsync();
                var request = JsonSerializer.Deserialize(body, RemoteJsonContext.Default.RemotePairRequest);
                var response = new RemotePairResponse { HostName = HostName, UserName = UserName };

                if (PairingCode is null)
                    response.Error = "No pairing code is active.";
                else if (request?.Code != PairingCode)
                    response.Error = "That pairing code is not correct.";
                else
                {
                    IssuedToken = "token-" + Guid.NewGuid().ToString("n");
                    PairingCode = null;
                    response.Ok = true;
                    response.Token = IssuedToken;
                }

                await WriteJsonAsync(
                    context,
                    JsonSerializer.Serialize(response, RemoteJsonContext.Default.RemotePairResponse),
                    response.Ok ? 200 : 401);
                return;
            }
        }

        if (IssuedToken is null || token != IssuedToken)
        {
            context.Response.StatusCode = 401;
            context.Response.Close();
            return;
        }

        switch (path)
        {
            case RemoteProtocol.Routes.Snapshot:
                Interlocked.Increment(ref _snapshotRequestCount);
                await WriteJsonAsync(context,
                    JsonSerializer.Serialize(Snapshot, RemoteJsonContext.Default.RemoteSnapshot));
                return;

            case RemoteProtocol.Routes.Chats:
                ChatRequestStarted?.TrySetResult();
                if (ReleaseChatResponse is { } release)
                    await release.Task;
                await WriteJsonAsync(context,
                    JsonSerializer.Serialize(BuildChatPage(context.Request), RemoteJsonContext.Default.RemoteChatPage));
                return;

            case RemoteProtocol.Routes.LibraryItem:
            {
                var resource = context.Request.QueryString["resource"];
                var identifier = context.Request.QueryString["identifier"];
                RemoteLibraryItem? item = resource switch
                {
                    RemoteProtocol.Resources.Projects => Snapshot.Library.Projects
                        .Where(project => project.Id.ToString() == identifier)
                        .Select(project => new RemoteLibraryItem
                        {
                            Resource = resource,
                            Identifier = identifier!,
                            Name = project.Name,
                            Body = project.Instructions,
                            WorkingDirectory = project.WorkingDirectory
                        })
                        .FirstOrDefault(),
                    RemoteProtocol.Resources.Skills => Snapshot.Library.Skills
                        .Where(skill => skill.Id.ToString() == identifier)
                        .Select(skill => new RemoteLibraryItem
                        {
                            Resource = resource,
                            Identifier = identifier!,
                            Name = skill.Name,
                            Description = skill.Description,
                            Body = skill.Content,
                            Glyph = skill.IconGlyph
                        })
                        .FirstOrDefault(),
                    RemoteProtocol.Resources.Lumis => Snapshot.Library.Lumis
                        .Where(lumi => lumi.Id.ToString() == identifier)
                        .Select(lumi => new RemoteLibraryItem
                        {
                            Resource = resource,
                            Identifier = identifier!,
                            Name = lumi.Name,
                            Description = lumi.Description,
                            Body = lumi.SystemPrompt,
                            Glyph = lumi.IconGlyph
                        })
                        .FirstOrDefault(),
                    RemoteProtocol.Resources.Memories => Snapshot.Library.Memories
                        .Where(memory => memory.Id.ToString() == identifier)
                        .Select(memory => new RemoteLibraryItem
                        {
                            Resource = resource,
                            Identifier = identifier!,
                            Name = memory.Key,
                            Body = memory.Content
                        })
                        .FirstOrDefault(),
                    _ => null
                };

                if (item is null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                await WriteJsonAsync(
                    context,
                    JsonSerializer.Serialize(item, RemoteJsonContext.Default.RemoteLibraryItem));
                return;
            }

            case RemoteProtocol.Routes.Transcript:
                Interlocked.Increment(ref _transcriptRequestCount);
                await WriteJsonAsync(context,
                    JsonSerializer.Serialize(Transcript, RemoteJsonContext.Default.RemoteTranscript));
                return;

            case RemoteProtocol.Routes.Command:
            {
                var body = await new StreamReader(context.Request.InputStream).ReadToEndAsync();
                var command = JsonSerializer.Deserialize(body, RemoteJsonContext.Default.RemoteCommand);
                if (command is not null)
                    lock (ReceivedCommands)
                        ReceivedCommands.Add(command);

                var result = CommandResultFactory?.Invoke(command)
                    ?? new RemoteCommandResult { Ok = true, Message = "ok" };
                if (result.Ok && command?.Action == RemoteProtocol.Actions.CreateChat)
                    result.ChatId = Guid.NewGuid();

                await WriteJsonAsync(context,
                    JsonSerializer.Serialize(result, RemoteJsonContext.Default.RemoteCommandResult));
                return;
            }

            case RemoteProtocol.Routes.Upload:
            {
                var encodedName = context.Request.Headers[RemoteProtocol.UploadFileNameHeader] ?? "";
                UploadedFileName = Encoding.UTF8.GetString(Convert.FromBase64String(encodedName));
                using var body = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(body);
                UploadedBytes = body.ToArray();
                UploadedPath = $@"C:\Temp\Lumi-mobile-uploads\{UploadedFileName}";

                await WriteJsonAsync(context, JsonSerializer.Serialize(
                    new RemoteUploadResponse { Ok = true, Path = UploadedPath, FileName = UploadedFileName },
                    RemoteJsonContext.Default.RemoteUploadResponse));
                return;
            }

            case RemoteProtocol.Routes.Events:
                Interlocked.Increment(ref _eventRequestCount);
                await ServeEventsAsync(context);
                return;

            default:
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
        }
    }

    private async Task ServeEventsAsync(HttpListenerContext context)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/event-stream";
        context.Response.SendChunked = true;

        var writer = new StreamWriter(context.Response.OutputStream, new UTF8Encoding(false)) { AutoFlush = false };

        await writer.WriteAsync(new RemoteEventFrame(
            RemoteProtocol.Events.Snapshot,
            JsonSerializer.Serialize(Snapshot, RemoteJsonContext.Default.RemoteSnapshot)).ToWire());
        await writer.FlushAsync();

        await _subscriberGate.WaitAsync();
        try
        {
            _subscribers.Add(writer);
        }
        finally
        {
            _subscriberGate.Release();
        }

        _firstSubscriber.TrySetResult();

        try
        {
            await Task.Delay(Timeout.Infinite, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task DiscoveryLoopAsync()
    {
        while (!_cts.IsCancellationRequested && _discovery is not null)
        {
            UdpReceiveResult received;
            try
            {
                received = await _discovery.ReceiveAsync(_cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            if (Encoding.UTF8.GetString(received.Buffer) != RemoteProtocol.DiscoveryProbe)
                continue;

            var beacon = new RemoteBeacon
            {
                InstanceId = "fake",
                HostName = HostName,
                UserName = UserName,
                Address = "127.0.0.1",
                Port = Port
            };

            var payload = Encoding.UTF8.GetBytes(
                RemoteProtocol.DiscoveryBeacon +
                JsonSerializer.Serialize(beacon, RemoteJsonContext.Default.RemoteBeacon));

            await _discovery.SendAsync(payload, payload.Length, received.RemoteEndPoint);
        }
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, string json, int status = 200)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private RemoteChatPage BuildChatPage(HttpListenerRequest request)
    {
        var query = request.QueryString["q"];
        var projectId = Guid.TryParse(request.QueryString["projectId"], out var parsedProjectId)
            ? parsedProjectId
            : (Guid?)null;
        var offset = int.TryParse(request.QueryString["offset"], out var parsedOffset)
            ? Math.Max(0, parsedOffset)
            : 0;
        var limit = int.TryParse(request.QueryString["limit"], out var parsedLimit)
            ? Math.Clamp(parsedLimit, 1, RemoteProtocol.MaxChatPageSize)
            : RemoteProtocol.ChatPageSize;

        var matching = (ChatCatalog ?? Snapshot.Chats.Groups)
            .SelectMany(group => group.Chats.Select(chat => (group.Label, Chat: chat)))
            .Where(item => projectId is null || item.Chat.ProjectId == projectId)
            .Where(item =>
                string.IsNullOrWhiteSpace(query)
                || item.Chat.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (item.Chat.Preview?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (item.Chat.ProjectName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
        var pageItems = matching.Skip(offset).Take(limit).ToList();
        var groups = pageItems
            .GroupBy(item => item.Label)
            .Select(group => new RemoteChatGroup
            {
                Label = group.Key,
                Chats = group.Select(item => item.Chat).ToList()
            })
            .ToList();

        return new RemoteChatPage
        {
            Offset = offset,
            TotalCount = matching.Count,
            HasMore = offset + pageItems.Count < matching.Count,
            Query = query,
            ProjectId = projectId,
            Groups = groups
        };
    }

    internal static int GetFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    internal static int GetFreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        // Idempotent: a test that shuts the desktop down mid-scenario still has the `await using`
        // dispose waiting at the end of the method, and a second pass must not throw.
        if (_disposed)
            return;

        _disposed = true;

        await _cts.CancelAsync();

        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already closed.
        }

        _discovery?.Dispose();

        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        if (_discoveryLoop is not null)
        {
            try
            {
                await _discoveryLoop;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        _cts.Dispose();
        _subscriberGate.Dispose();
    }
}
