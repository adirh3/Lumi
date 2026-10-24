using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Lumi.Build;
using Microsoft.Build.Framework;
using Xunit;

namespace Lumi.Tests;

[Trait("Category", "CoreQA")]
public sealed class CopilotCliDownloadTests : IDisposable
{
    private static readonly byte[] Asset = Encoding.UTF8.GetBytes("LUMI_VERIFIED_CLI_FIXTURE");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Lumi-cli-download-tests", Guid.NewGuid().ToString("N"));
    private readonly BuildEngine _engine = new();

    public CopilotCliDownloadTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(503)]
    public void TransientServerFailureRetriesAndVerifiesTheDownloadedBytes(int status)
    {
        using var server = new DownloadServer(Asset, status, 200);
        var destination = Path.Combine(_directory, "copilot");

        CreateTask().Download(server.Url, destination, Hash(Asset));

        Assert.Equal(2, server.Requests);
        Assert.Equal(Asset, File.ReadAllBytes(destination));
        Assert.Single(_engine.Warnings);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public void PermanentHttpFailureDoesNotRetryOrCreateAnAsset(int status)
    {
        using var server = new DownloadServer(Asset, status, 200);
        var destination = Path.Combine(_directory, "copilot");

        var error = Assert.Throws<HttpRequestException>(() => CreateTask().Download(server.Url, destination, Hash(Asset)));

        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(1, server.Requests);
        Assert.Empty(_engine.Warnings);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void ChecksumMismatchNeverRetriesOrPublishesUnverifiedBytes()
    {
        using var server = new DownloadServer(Encoding.UTF8.GetBytes("tampered"), 200);
        var destination = Path.Combine(_directory, "copilot");

        Assert.Throws<InvalidDataException>(() => CreateTask().Download(server.Url, destination, Hash(Asset)));

        Assert.Equal(1, server.Requests);
        Assert.Empty(_engine.Warnings);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void ConnectionFailuresRetryWithoutAnHttpResponse()
    {
        using var server = new DownloadServer(Asset, DownloadServer.ResetConnection);
        var destination = Path.Combine(_directory, "copilot");

        var error = Assert.Throws<HttpRequestException>(() => CreateTask().Download(server.Url, destination, Hash(Asset)));

        Assert.Null(error.StatusCode);
        Assert.True(server.Requests >= 3);
        Assert.Equal(2, _engine.Warnings.Count);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void TransientFailuresStopAfterThreeAttemptsAndLeaveNoAsset()
    {
        using var server = new DownloadServer(Asset, 503);
        var destination = Path.Combine(_directory, "copilot");

        var error = Assert.Throws<HttpRequestException>(() => CreateTask().Download(server.Url, destination, Hash(Asset)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal(3, server.Requests);
        Assert.Equal(2, _engine.Warnings.Count);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void TheOriginalDownloadDeadlineIncludesRetryBackoff()
    {
        using var server = new DownloadServer(Asset, 503);
        var destination = Path.Combine(_directory, "copilot");
        var task = CreateTask();
        task.DownloadTimeoutSeconds = 5;

        Assert.ThrowsAny<OperationCanceledException>(() => task.Download(server.Url, destination, Hash(Asset)));

        Assert.Equal(2, server.Requests);
        Assert.Equal(2, _engine.Warnings.Count);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    private AcquireFullCopilotCli CreateTask() => new() { BuildEngine = _engine, DownloadTimeoutSeconds = 20 };
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class BuildEngine : IBuildEngine
    {
        internal List<BuildWarningEventArgs> Warnings { get; } = [];
        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => "";
        public void LogWarningEvent(BuildWarningEventArgs e) => Warnings.Add(e);
        public void LogErrorEvent(BuildErrorEventArgs e) => throw new InvalidOperationException(e.Message);
        public void LogMessageEvent(BuildMessageEventArgs e) { }
        public void LogCustomEvent(CustomBuildEventArgs e) { }
        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties,
            IDictionary targetOutputs) => throw new NotSupportedException();
    }

    private sealed class DownloadServer : IDisposable
    {
        internal const int ResetConnection = 0;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;
        private int _requests;

        internal DownloadServer(byte[] content, params int[] statuses)
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/asset";
            _serve = ServeAsync(content, statuses);
        }

        internal string Url { get; }
        internal int Requests => Volatile.Read(ref _requests);

        private async Task ServeAsync(byte[] content, int[] statuses)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                    var request = Interlocked.Increment(ref _requests);
                    var status = statuses[Math.Min(request - 1, statuses.Length - 1)];
                    if (status == ResetConnection)
                    {
                        client.Client.LingerState = new LingerOption(true, 0);
                        continue;
                    }
                    var header = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {status} Fixture\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, _stop.Token);
                    await stream.WriteAsync(content, _stop.Token);
                }
            }
            catch (Exception error) when (_stop.IsCancellationRequested
                && error is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _serve.GetAwaiter().GetResult();
            _stop.Dispose();
        }
    }
}
