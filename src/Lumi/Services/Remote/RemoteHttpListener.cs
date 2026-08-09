using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Remote.Protocol;

namespace Lumi.Services.Remote;

/// <summary>
/// A deliberately small HTTP/1.1 server used by the Lumi remote surface.
/// </summary>
/// <remarks>
/// Why not <see cref="System.Net.HttpListener"/>: on Windows any prefix other than
/// <c>127.0.0.1</c>/<c>localhost</c> needs a <c>netsh http add urlacl</c> reservation or an
/// elevated process. Lumi must be able to accept phone connections from the LAN without asking the
/// user to run as administrator, so the listener is built on a plain <see cref="TcpListener"/>.
/// Only the subset the protocol needs is implemented: request line, headers, Content-Length bodies,
/// fixed-length responses and open-ended streaming responses (for Server-Sent Events).
/// </remarks>
internal sealed class RemoteHttpListener : IDisposable
{
    private const int MaxHeaderBytes = 32 * 1024;
    internal const int MaxConcurrentConnections = 32;
    internal const long OrdinaryRequestBodyLimitBytes = 8L * 1024 * 1024;
    internal const int JsonCompressionThresholdBytes = 1024;
    internal static readonly TimeSpan HeaderReadTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan OrdinaryBodyReadTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan UploadBodyReadTimeout = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan ResponseWriteTimeout = TimeSpan.FromSeconds(30);
    private const string RequestBodyTooLargeJson =
        """{"ok":false,"error":"Request body is too large."}""";

    private readonly Func<RemoteHttpContext, CancellationToken, Task> _handler;
    private readonly Func<RemoteHttpRequest, EndPoint?, EndPoint?, RemoteHttpPreflightResult>? _preflight;
    private readonly SemaphoreSlim _connectionSlots = new(MaxConcurrentConnections, MaxConcurrentConnections);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public RemoteHttpListener(
        Func<RemoteHttpContext, CancellationToken, Task> handler,
        Func<RemoteHttpRequest, EndPoint?, EndPoint?, RemoteHttpPreflightResult>? preflight = null)
    {
        _handler = handler;
        _preflight = preflight;
    }

    public int Port { get; private set; }

    public void Start(int port)
    {
        // IPv6Any with dual-mode accepts both IPv4 and IPv6 clients on one socket.
        var listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
        listener.Start();

        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _acceptTask = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }

            if (!_connectionSlots.Wait(0))
            {
                client.Dispose();
                continue;
            }

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await ServeConnectionAsync(client, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        _connectionSlots.Release();
                    }
                },
                CancellationToken.None);
        }
    }

    private async Task ServeConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            await using var stream = client.GetStream();

            try
            {
                // Keep-alive loop: a phone polls often, so reusing the socket avoids a
                // connect + handshake per request.
                while (!cancellationToken.IsCancellationRequested)
                {
                    var readResult = await ReadRequestAsync(
                            stream,
                            client.Client.RemoteEndPoint,
                            client.Client.LocalEndPoint,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (readResult is null)
                        return;

                    var context = new RemoteHttpContext(
                        readResult.Request,
                        stream,
                        client.Client.RemoteEndPoint,
                        client.Client.LocalEndPoint,
                        readResult.RequestBody);
                    if (readResult.RejectionStatus is { } rejectionStatus)
                    {
                        context.KeepAlive = false;
                        await context.WriteJsonAsync(
                                readResult.RejectionJson ?? RequestBodyTooLargeJson,
                                cancellationToken,
                                rejectionStatus)
                            .ConfigureAwait(false);
                        return;
                    }

                    await _handler(context, cancellationToken).ConfigureAwait(false);

                    // A streaming response owns the socket for its lifetime.
                    if (context.IsStreaming || !context.KeepAlive)
                        return;
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException
                                           or OperationCanceledException)
            {
                // Normal disconnect.
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[Remote] Connection failed: {ex.Message}");
            }
        }
    }

    private async Task<RequestReadResult?> ReadRequestAsync(
        Stream stream,
        EndPoint? remoteEndPoint,
        EndPoint? localEndPoint,
        CancellationToken cancellationToken)
    {
        var header = await ReadHeaderBlockAsync(stream, cancellationToken).ConfigureAwait(false);
        if (header is null)
            return null;

        var lines = header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return null;

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
            return null;

        var method = requestLine[0];
        var target = requestLine[1];
        var path = target;
        var query = "";
        var queryIndex = target.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            path = target[..queryIndex];
            query = target[(queryIndex + 1)..];
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var separator = lines[i].IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
                continue;
            headers[lines[i][..separator].Trim()] = lines[i][(separator + 1)..].Trim();
        }

        var keepAlive = !string.Equals(
            headers.GetValueOrDefault("Connection"),
            "close",
            StringComparison.OrdinalIgnoreCase);

        var contentLength = 0L;
        if (headers.ContainsKey("Transfer-Encoding"))
        {
            return new RequestReadResult(
                new RemoteHttpRequest(method, path, query, headers, "", false),
                400,
                RemoteHttpPreflightResult.Reject(400, "Chunked request bodies are not supported.").RejectionJson);
        }

        if (headers.TryGetValue("Content-Length", out var lengthText))
        {
            if (!long.TryParse(lengthText, out var length) || length < 0)
            {
                return new RequestReadResult(
                    new RemoteHttpRequest(method, path, query, headers, "", false),
                    400,
                    RemoteHttpPreflightResult.Reject(400, "Content-Length is invalid.").RejectionJson);
            }
            contentLength = length;
        }

        var requestHead = new RemoteHttpRequest(
            method,
            path,
            query,
            headers,
            "",
            keepAlive,
            contentLength);
        var preflight = _preflight?.Invoke(requestHead, remoteEndPoint, localEndPoint)
                        ?? RemoteHttpPreflightResult.Allow(GetRequestBodyLimit(path));
        if (preflight.RejectionStatus is { } rejectionStatus)
        {
            return new RequestReadResult(
                requestHead with { KeepAlive = false },
                RejectionStatus: rejectionStatus,
                RejectionJson: preflight.RejectionJson);
        }

        var bodyLimit = preflight.BodyLimit ?? GetRequestBodyLimit(path);
        if (contentLength > bodyLimit || contentLength > int.MaxValue && !preflight.StreamBody)
        {
            return new RequestReadResult(
                requestHead with { KeepAlive = false },
                RejectionStatus: 413);
        }

        if (contentLength == 0)
            return new RequestReadResult(requestHead);

        var requestBody = new RemoteRequestBody(
            stream,
            contentLength,
            preflight.StreamBody ? UploadBodyReadTimeout : OrdinaryBodyReadTimeout);
        if (preflight.StreamBody)
        {
            return new RequestReadResult(
                requestHead with { KeepAlive = false },
                RequestBody: requestBody);
        }

        var buffer = new byte[(int)contentLength];
        try
        {
            await requestBody.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        var body = Encoding.UTF8.GetString(buffer);
        return new RequestReadResult(requestHead with { Body = body });
    }

    internal static long GetRequestBodyLimit(string path)
    {
        var normalizedPath = path.TrimEnd('/');
        return string.Equals(normalizedPath, RemoteProtocol.Routes.Upload, StringComparison.Ordinal)
            ? RemoteProtocol.MaxUploadBytes
            : OrdinaryRequestBodyLimitBytes;
    }

    private static async Task<string?> ReadHeaderBlockAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var builder = new StringBuilder();
        var matched = 0;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(HeaderReadTimeout);

        while (builder.Length < MaxHeaderBytes)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(0, 1), deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                return null;
            }

            if (read == 0)
                return builder.Length == 0 ? null : builder.ToString();

            var current = (char)buffer[0];
            builder.Append(current);

            // Detect the CRLFCRLF terminator without rescanning the whole buffer.
            matched = (matched, current) switch
            {
                (0, '\r') => 1,
                (1, '\n') => 2,
                (2, '\r') => 3,
                (3, '\n') => 4,
                (_, '\r') => 1,
                _ => 0
            };

            if (matched == 4)
                return builder.ToString();
        }

        return null;
    }

    public void Dispose()
    {
        var cancellation = _cts;
        var listener = _listener;
        _cts = null;
        _listener = null;
        _acceptTask = null;

        cancellation?.Cancel();
        try { listener?.Stop(); }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException) { }
        cancellation?.Dispose();
    }

    private sealed record RequestReadResult(
        RemoteHttpRequest Request,
        int? RejectionStatus = null,
        string? RejectionJson = null,
        RemoteRequestBody? RequestBody = null);
}

internal sealed record RemoteHttpRequest(
    string Method,
    string Path,
    string Query,
    Dictionary<string, string> Headers,
    string Body,
    bool KeepAlive,
    long ContentLength = 0)
{
    public string? Header(string name) => Headers.GetValueOrDefault(name);

    public bool AcceptsGzip()
    {
        var acceptEncoding = Header("Accept-Encoding");
        if (string.IsNullOrWhiteSpace(acceptEncoding))
            return false;

        double? gzipQuality = null;
        double? wildcardQuality = null;
        foreach (var item in acceptEncoding.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
                continue;

            var quality = 1d;
            foreach (var parameter in parts.Skip(1))
            {
                var separator = parameter.IndexOf('=', StringComparison.Ordinal);
                if (separator <= 0
                    || !string.Equals(parameter[..separator].Trim(), "q", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!double.TryParse(
                        parameter[(separator + 1)..].Trim(),
                        NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture,
                        out quality)
                    || !double.IsFinite(quality)
                    || quality is < 0 or > 1)
                {
                    quality = 0;
                }
            }

            if (string.Equals(parts[0], "gzip", StringComparison.OrdinalIgnoreCase))
                gzipQuality = Math.Max(gzipQuality ?? 0, quality);
            else if (parts[0] == "*")
                wildcardQuality = Math.Max(wildcardQuality ?? 0, quality);
        }

        return (gzipQuality ?? wildcardQuality ?? 0) > 0;
    }

    public string? QueryValue(string name)
    {
        foreach (var pair in Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = separator < 0 ? pair : pair[..separator];
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                continue;
            return separator < 0 ? "" : Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return null;
    }
}

internal readonly record struct RemoteHttpPreflightResult(
    int? RejectionStatus,
    string? RejectionJson,
    bool StreamBody,
    long? BodyLimit)
{
    public static RemoteHttpPreflightResult Allow(long bodyLimit, bool streamBody = false) =>
        new(null, null, streamBody, bodyLimit);

    public static RemoteHttpPreflightResult Reject(int status, string error)
    {
        var escaped = JsonSerializer.Serialize(error, RemoteJsonContext.Default.String);
        return new(status, $"{{\"ok\":false,\"error\":{escaped}}}", false, 0);
    }
}

internal sealed class RemoteRequestBody
{
    private readonly Stream _stream;
    private readonly TimeSpan _timeout;
    private long _remaining;

    public RemoteRequestBody(Stream stream, long length, TimeSpan timeout)
    {
        _stream = stream;
        _remaining = length;
        _timeout = timeout;
    }

    public long Remaining => _remaining;

    public async Task ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (destination.Length != _remaining)
            throw new ArgumentException("The destination must match the declared request body length.", nameof(destination));

        var written = 0;
        while (_remaining > 0)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_timeout);
            var read = await _stream.ReadAsync(
                    destination.Slice(written, checked((int)_remaining)),
                    deadline.Token)
                .ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("The request body ended before Content-Length bytes arrived.");
            written += read;
            _remaining -= read;
        }
    }

    public async Task CopyToAsync(Stream destination, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (_remaining > 0)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(_timeout);
                var read = await _stream.ReadAsync(
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, _remaining)),
                        deadline.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("The request body ended before Content-Length bytes arrived.");

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                _remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

internal sealed class RemoteHttpContext
{
    public RemoteHttpContext(
        RemoteHttpRequest request,
        Stream stream,
        EndPoint? remoteEndPoint,
        EndPoint? localEndPoint,
        RemoteRequestBody? requestBody = null)
    {
        Request = request;
        Stream = stream;
        RemoteEndPoint = remoteEndPoint;
        LocalEndPoint = localEndPoint;
        RequestBody = requestBody;
        KeepAlive = request.KeepAlive;
    }

    public RemoteHttpRequest Request { get; }
    public Stream Stream { get; }
    public EndPoint? RemoteEndPoint { get; }
    public EndPoint? LocalEndPoint { get; }
    public RemoteRequestBody? RequestBody { get; }
    public bool KeepAlive { get; set; }

    /// <summary>True once the response has been switched to an open-ended stream.</summary>
    public bool IsStreaming { get; private set; }

    public Task WriteJsonAsync(string json, CancellationToken cancellationToken, int status = 200) =>
        WriteAsync(
            status,
            "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes(json),
            cancellationToken,
            allowCompression: true);

    public Task WriteTextAsync(string text, CancellationToken cancellationToken, int status = 200) =>
        WriteAsync(
            status,
            "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes(text),
            cancellationToken,
            allowCompression: false);

    public async Task WriteFileAsync(
        Stream source,
        long length,
        string fileName,
        CancellationToken cancellationToken)
    {
        KeepAlive = false;
        var encodedName = Uri.EscapeDataString(Path.GetFileName(fileName));
        var header = new StringBuilder()
            .Append("HTTP/1.1 200 OK\r\n")
            .Append("Content-Type: application/octet-stream\r\n")
            .Append("Content-Disposition: attachment; filename*=UTF-8''").Append(encodedName).Append("\r\n")
            .Append("Content-Length: ").Append(length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n\r\n");
        await WriteWithDeadlineAsync(Encoding.UTF8.GetBytes(header.ToString()), cancellationToken)
            .ConfigureAwait(false);

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;
                await WriteWithDeadlineAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await FlushWithDeadlineAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAsync(
        int status,
        string contentType,
        byte[] body,
        CancellationToken cancellationToken,
        bool allowCompression)
    {
        var useGzip =
            allowCompression
            && body.Length >= RemoteHttpListener.JsonCompressionThresholdBytes
            && Request.AcceptsGzip();
        var responseBody = useGzip ? CompressGzip(body) : body;

        var header = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(ReasonPhrase(status)).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n");

        if (allowCompression)
            header.Append("Vary: Accept-Encoding\r\n");
        if (useGzip)
            header.Append("Content-Encoding: gzip\r\n");

        header
            .Append("Content-Length: ").Append(responseBody.Length).Append("\r\n")
            .Append("Connection: ").Append(KeepAlive ? "keep-alive" : "close").Append("\r\n")
            .Append("\r\n");

        await WriteWithDeadlineAsync(Encoding.UTF8.GetBytes(header.ToString()), cancellationToken).ConfigureAwait(false);
        if (responseBody.Length > 0)
            await WriteWithDeadlineAsync(responseBody, cancellationToken).ConfigureAwait(false);
        await FlushWithDeadlineAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static byte[] CompressGzip(ReadOnlySpan<byte> body)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(body);
        return output.ToArray();
    }

    /// <summary>Switches the response into Server-Sent Events mode and returns its output stream.</summary>
    public async Task<Stream> BeginEventStreamAsync(CancellationToken cancellationToken)
    {
        KeepAlive = false;

        var useGzip = Request.AcceptsGzip();
        var header = new StringBuilder()
            .Append("HTTP/1.1 200 OK\r\n")
            .Append("Content-Type: text/event-stream; charset=utf-8\r\n")
            .Append("Cache-Control: no-cache, no-store\r\n")
            // This socket belongs to the indefinite SSE response and is never reused. Close-delimited
            // framing is the simplest valid HTTP/1.1 representation for the raw TCP listener.
            .Append("Connection: close\r\n")
            .Append("X-Accel-Buffering: no\r\n")
            .Append("Vary: Accept-Encoding\r\n");
        if (useGzip)
            header.Append("Content-Encoding: gzip\r\n");
        header.Append("\r\n");

        await WriteWithDeadlineAsync(Encoding.UTF8.GetBytes(header.ToString()), cancellationToken).ConfigureAwait(false);
        await FlushWithDeadlineAsync(cancellationToken).ConfigureAwait(false);
        IsStreaming = true;
        return useGzip
            ? new GZipStream(Stream, CompressionLevel.Fastest, leaveOpen: true)
            : Stream;
    }

    /// <summary>Finalizes a negotiated SSE encoder without taking ownership of the TCP stream.</summary>
    public async ValueTask CompleteEventStreamAsync(Stream responseStream)
    {
        if (ReferenceEquals(responseStream, Stream))
            return;

        try
        {
            await responseStream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            // The phone disconnected before the gzip trailer could be written.
        }
    }

    private async Task WriteWithDeadlineAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RemoteHttpListener.ResponseWriteTimeout);
        await Stream.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
    }

    private async Task FlushWithDeadlineAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RemoteHttpListener.ResponseWriteTimeout);
        await Stream.FlushAsync(deadline.Token).ConfigureAwait(false);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        429 => "Too Many Requests",
        413 => "Payload Too Large",
        500 => "Internal Server Error",
        _ => "OK"
    };
}
