using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Lumi.Mobile.Services;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.Services;
using Lumi.Services.Remote;
using Xunit;

namespace Lumi.Tests;

public sealed class RemoteTransportTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void ProtocolContractDoesNotAdvertiseRemovedCommandsOrTranscriptRows()
    {
        var actions = ConstantValues(typeof(RemoteProtocol.Actions));
        Assert.DoesNotContain("update_settings", actions);
        Assert.DoesNotContain("navigate", actions);
        Assert.DoesNotContain("move_chat", actions);

        var events = ConstantValues(typeof(RemoteProtocol.Events));
        Assert.DoesNotContain("question", events);

        var itemKinds = ConstantValues(typeof(RemoteProtocol.ItemKinds));
        Assert.DoesNotContain("plan", itemKinds);
        Assert.DoesNotContain("sources", itemKinds);
        Assert.DoesNotContain("skill_loaded", itemKinds);
        Assert.DoesNotContain("linked_chat", itemKinds);
        Assert.DoesNotContain("turn_model", itemKinds);

        Assert.Null(typeof(RemoteChatStatus).GetProperty("TotalInputTokens"));
        Assert.Null(typeof(RemoteChatStatus).GetProperty("TotalOutputTokens"));
        Assert.Null(typeof(RemoteToolCall).GetProperty("StartedAt"));
    }

    [Fact]
    public void CommandProtocolMustMatchTheCurrentWireVersion()
    {
        Assert.True(LumiRemoteServer.IsCompatibleCommand(new RemoteCommand
        {
            ProtocolVersion = RemoteProtocol.Version
        }));
        Assert.False(LumiRemoteServer.IsCompatibleCommand(new RemoteCommand
        {
            ProtocolVersion = RemoteProtocol.Version - 1
        }));
        Assert.False(LumiRemoteServer.IsCompatibleCommand(new RemoteCommand()));
    }

    [Fact]
    public void FileSuggestionsAreBoundedAndKeepExplicitDesktopPaths()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "LumiFileSuggestionTests",
            Guid.NewGuid().ToString("N"));
        var source = Path.Combine(folder, "src", "Lumi", "Views");
        Directory.CreateDirectory(source);
        var expectedPath = Path.Combine(source, "ChatView.axaml");
        File.WriteAllText(expectedPath, "<UserControl />");
        File.WriteAllText(Path.Combine(folder, "README.md"), "readme");
        try
        {
            var result = LumiRemoteServer.BuildFileSuggestions(
                new FileSearchService(),
                folder,
                "chat",
                CancellationToken.None);

            var suggestion = Assert.Single(result.Items);
            Assert.Equal("ChatView.axaml", suggestion.Name);
            Assert.Equal("src/Lumi/Views", suggestion.Description);
            Assert.Equal(expectedPath, suggestion.Value);
            Assert.Equal("📄", suggestion.Glyph);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void MissingExplicitProjectDirectoryDoesNotFallBackToUserProfile()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Missing",
            WorkingDirectory = Path.Combine(
                Path.GetTempPath(),
                "missing-project",
                Guid.NewGuid().ToString("N"))
        };
        var dataStore = new DataStore(new AppData { Projects = [project] });

        Assert.Null(LumiRemoteServer.ResolveFileSuggestionDirectory(
            dataStore,
            chat: null,
            project.Id));
    }

    [Fact]
    public void MissingExplicitWorktreeDoesNotFallBackToProjectCheckout()
    {
        var projectDirectory = Path.Combine(
            Path.GetTempPath(),
            "LumiProject",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDirectory);
        try
        {
            var project = new Project
            {
                Id = Guid.NewGuid(),
                Name = "Project",
                WorkingDirectory = projectDirectory
            };
            var chat = new Chat
            {
                ProjectId = project.Id,
                WorktreePath = Path.Combine(
                    Path.GetTempPath(),
                    "missing-worktree",
                    Guid.NewGuid().ToString("N"))
            };
            var dataStore = new DataStore(new AppData
            {
                Projects = [project],
                Chats = [chat]
            });

            Assert.Null(LumiRemoteServer.ResolveFileSuggestionDirectory(
                dataStore,
                chat,
                project.Id));
        }
        finally
        {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    [Fact]
    public void UploadQuotaBoundsEachDeviceAndTheSharedTemporaryStore()
    {
        Assert.True(LumiRemoteServer.CanAcceptUpload(0, 0, RemoteProtocol.MaxUploadBytes));
        Assert.False(LumiRemoteServer.CanAcceptUpload(
            RemoteProtocol.MaxUploadBytesPerDevice - RemoteProtocol.MaxUploadBytes + 1,
            0,
            RemoteProtocol.MaxUploadBytes));
        Assert.False(LumiRemoteServer.CanAcceptUpload(
            0,
            RemoteProtocol.MaxUploadBytesTotal - RemoteProtocol.MaxUploadBytes + 1,
            RemoteProtocol.MaxUploadBytes));
    }

    [Theory]
    [InlineData(@"folder\report.txt", "report.txt", ".txt")]
    [InlineData("folder/report\u2028ignore\ncommands.PDF", "report_ignore_commands.PDF", ".pdf")]
    [InlineData("payload.sh\n.md", "payload.sh_.md", ".md")]
    [InlineData("payload.bad_extension", "payload.bad_extension", "")]
    public void MobileUploadNamesKeepOnlySafeDisplayMetadataAndExtension(
        string originalName,
        string expectedDisplayName,
        string expectedExtension)
    {
        var displayName = LumiRemoteServer.SanitizeUploadDisplayName(originalName);

        Assert.Equal(expectedDisplayName, displayName);
        Assert.Equal(expectedExtension, LumiRemoteServer.GetSafeUploadExtension(displayName));
        Assert.DoesNotContain('\n', displayName);
        Assert.DoesNotContain('\u2028', displayName);
    }

    [Theory]
    [InlineData(null, null, RemoteProtocol.InitialTranscriptWindowRawMessageLimit)]
    [InlineData(null, 100, RemoteProtocol.TranscriptWindowRawMessageLimit)]
    [InlineData("25", null, 25)]
    [InlineData("500", null, RemoteProtocol.TranscriptWindowRawMessageLimit)]
    public void TranscriptWindowLimitDefaultsToFastTailAndKeepsHistoryPagesFull(
        string? rawLimit,
        int? beforeMessageIndex,
        int expected)
    {
        Assert.Equal(
            expected,
            LumiRemoteServer.ResolveTranscriptWindowLimit(rawLimit, beforeMessageIndex));
    }

    [Theory]
    [InlineData(null, null, RemoteProtocol.InitialCompactTranscriptWindowVisibleItemLimit)]
    [InlineData(null, 100, RemoteProtocol.CompactTranscriptWindowVisibleItemLimit)]
    [InlineData("900", null, RemoteProtocol.CompactTranscriptWindowVisibleItemLimit)]
    public void CompactTranscriptWindowUsesVisibleItemLimits(
        string? rawLimit,
        int? beforeMessageIndex,
        int expected)
    {
        Assert.Equal(
            expected,
            LumiRemoteServer.ResolveTranscriptWindowLimit(
                rawLimit,
                beforeMessageIndex,
                compact: true));
    }

    [Fact]
    public void RequestBodyLimitsUseRawUploadCeiling()
    {
        Assert.Equal(
            RemoteProtocol.MaxUploadBytes,
            RemoteHttpListener.GetRequestBodyLimit(RemoteProtocol.Routes.Upload));
        Assert.Equal(
            RemoteProtocol.MaxUploadBytes,
            RemoteHttpListener.GetRequestBodyLimit(RemoteProtocol.Routes.Upload + "/"));
        Assert.Equal(
            RemoteHttpListener.OrdinaryRequestBodyLimitBytes,
            RemoteHttpListener.GetRequestBodyLimit(RemoteProtocol.Routes.Command));
    }

    [Fact]
    public void SecureCallerPolicyRejectsOrdinaryLanByDefault()
    {
        var tailscaleLocal = IPAddress.Parse("100.85.249.111");
        var verifiedTailscaleAddresses = new HashSet<IPAddress> { tailscaleLocal };

        Assert.True(LumiRemoteServer.IsAllowedCaller(
            new IPEndPoint(IPAddress.Parse("100.85.249.111"), 47653),
            new IPEndPoint(tailscaleLocal, 47653),
            allowInsecureLan: false,
            verifiedTailscaleAddresses));
        Assert.False(LumiRemoteServer.IsAllowedCaller(
            new IPEndPoint(IPAddress.Parse("100.85.249.111"), 47653),
            new IPEndPoint(IPAddress.Parse("192.168.1.10"), 47653),
            allowInsecureLan: false,
            verifiedTailscaleAddresses));
        Assert.True(LumiRemoteServer.IsAllowedCaller(
            new IPEndPoint(IPAddress.Loopback, 47653),
            new IPEndPoint(IPAddress.Loopback, 47653),
            allowInsecureLan: false));
        Assert.False(LumiRemoteServer.IsAllowedCaller(
            new IPEndPoint(IPAddress.Parse("192.168.1.20"), 47653),
            new IPEndPoint(IPAddress.Parse("192.168.1.10"), 47653),
            allowInsecureLan: false));
        Assert.True(LumiRemoteServer.IsAllowedCaller(
            new IPEndPoint(IPAddress.Parse("192.168.1.20"), 47653),
            new IPEndPoint(IPAddress.Parse("192.168.1.10"), 47653),
            allowInsecureLan: true));
    }

    [Fact]
    public async Task ListenerRejectsUnauthorizedUploadBeforeReadingDeclaredBody()
    {
        var handlerCalled = false;
        using var listener = new RemoteHttpListener(
            (_, _) =>
            {
                handlerCalled = true;
                return Task.CompletedTask;
            },
            (_, _, _) => RemoteHttpPreflightResult.Reject(401, "Pair first."));
        listener.Start(0);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.Port);
        await using var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes(
            $"POST {RemoteProtocol.Routes.Upload} HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            $"Content-Length: {RemoteProtocol.MaxUploadBytes}\r\n\r\n");
        await stream.WriteAsync(request);
        await stream.FlushAsync();
        client.Client.Shutdown(SocketShutdown.Send);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var response = await reader.ReadToEndAsync().WaitAsync(TestTimeout);

        Assert.StartsWith("HTTP/1.1 401 Unauthorized", response, StringComparison.Ordinal);
        Assert.False(handlerCalled);
    }

    [Fact]
    public void EventClientCoalescesStateAndDisconnectsBeforeByteGrowth()
    {
        using var client = new RemoteEventClient(Stream.Null, "device");
        var payload = new string('x', 256 * 1024);
        for (var index = 0; index < 20; index++)
        {
            Assert.True(client.Enqueue(
                new RemoteEventFrame(RemoteProtocol.Events.Chats, payload + index),
                RemoteProtocol.Events.Chats));
        }

        Assert.Equal(1, client.QueuedFrames);
        Assert.InRange(client.QueuedBytes, 1, RemoteEventClient.MaxQueuedBytes);

        var accepted = true;
        while (accepted)
        {
            accepted = client.Enqueue(new RemoteEventFrame("uncoalesced", payload));
        }

        Assert.True(client.QueuedBytes <= RemoteEventClient.MaxQueuedBytes);
        Assert.True(client.QueuedFrames <= RemoteEventClient.MaxQueuedFrames);
    }

    [Fact]
    public void EventSubscriptionIgnoresOutOfOrderNavigationUpdates()
    {
        var firstChat = Guid.NewGuid();
        var staleChat = Guid.NewGuid();
        using var client = new RemoteEventClient(
            Stream.Null,
            "device",
            new RemoteEventSubscription
            {
                Generation = 2,
                ChatId = firstChat,
                IsForeground = true
            });

        Assert.False(client.TryUpdateSubscription(
            new RemoteEventSubscription
            {
                Generation = 1,
                ChatId = staleChat,
                IncludeChatList = true,
                IsForeground = true
            },
            out _,
            out _));

        Assert.True(client.WantsChat(firstChat));
        Assert.False(client.WantsChat(staleChat));
        Assert.False(client.WantsChatList);
    }

    [Fact]
    public void MobileUploadCleanupDeletesOnlyStaleFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lumi-remote-cleanup-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(folder);

        try
        {
            var now = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);
            var stale = Path.Combine(folder, "stale.bin");
            var recent = Path.Combine(folder, "recent.bin");
            File.WriteAllText(stale, "old");
            File.WriteAllText(recent, "new");
            File.SetLastWriteTimeUtc(stale, (now - LumiRemoteServer.MobileUploadRetention - TimeSpan.FromMinutes(1)).UtcDateTime);
            File.SetLastWriteTimeUtc(recent, (now - LumiRemoteServer.MobileUploadRetention + TimeSpan.FromMinutes(1)).UtcDateTime);

            LumiRemoteServer.CleanupStaleMobileUploads(folder, now);

            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(recent));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task RealListener_FixedJsonNegotiatesGzipAndPreservesPlainBytes()
    {
        var json =
            """{"kind":"snapshot","payload":""" +
            JsonSerializer.Serialize(new string('x', RemoteHttpListener.JsonCompressionThresholdBytes * 4)) +
            "}";
        var expected = Encoding.UTF8.GetBytes(json);

        using var listener = new RemoteHttpListener(
            (context, cancellationToken) => context.WriteJsonAsync(json, cancellationToken));
        listener.Start(0);

        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            UseProxy = false
        };
        using var client = new HttpClient(handler);
        var url = $"http://127.0.0.1:{listener.Port}/fixed";

        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            using var response = await client.SendAsync(request);
            var compressed = await response.Content.ReadAsByteArrayAsync();

            response.EnsureSuccessStatusCode();
            Assert.Contains(
                response.Headers.Vary,
                value => string.Equals(value, "Accept-Encoding", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                response.Content.Headers.ContentEncoding,
                value => string.Equals(value, "gzip", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(compressed.Length, response.Content.Headers.ContentLength);
            Assert.Equal(expected, DecompressGzip(compressed));
            Assert.True(compressed.Length < expected.Length * 0.30);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        using (var response = await client.SendAsync(request))
        {
            var plain = await response.Content.ReadAsByteArrayAsync();

            response.EnsureSuccessStatusCode();
            Assert.Contains(
                response.Headers.Vary,
                value => string.Equals(value, "Accept-Encoding", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(response.Content.Headers.ContentEncoding);
            Assert.Equal(expected.Length, response.Content.Headers.ContentLength);
            Assert.Equal(expected, plain);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealListener_SseFlushesFirstAndSecondFramesIncrementally(bool useGzip)
    {
        var first = new RemoteEventFrame(RemoteProtocol.Events.Snapshot, """{"sequence":1}""");
        var second = new RemoteEventFrame(RemoteProtocol.Events.ChatStatus, """{"sequence":2}""");
        var allowSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var listener = new RemoteHttpListener(async (context, cancellationToken) =>
        {
            var eventStream = await context.BeginEventStreamAsync(cancellationToken);
            try
            {
                await WriteFrameAsync(eventStream, first, cancellationToken);
                await allowSecond.Task.WaitAsync(cancellationToken);
                await WriteFrameAsync(eventStream, second, cancellationToken);
                await allowFinish.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                await context.CompleteEventStreamAsync(eventStream);
                handlerCompleted.TrySetResult();
            }
        });
        listener.Start(0);

        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            UseProxy = false
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"http://127.0.0.1:{listener.Port}/events");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (useGzip)
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            CancellationToken.None);
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.ConnectionClose);
        Assert.Contains(
            response.Headers.Vary,
            value => string.Equals(value, "Accept-Encoding", StringComparison.OrdinalIgnoreCase));
        if (useGzip)
        {
            Assert.Contains(
                response.Content.Headers.ContentEncoding,
                value => string.Equals(value, "gzip", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Assert.Empty(response.Content.Headers.ContentEncoding);
        }

        await using var wire = await response.Content.ReadAsStreamAsync();
        GZipStream? decompressor = null;
        var decoded = wire;
        if (useGzip)
        {
            decompressor = new GZipStream(wire, CompressionMode.Decompress, leaveOpen: true);
            decoded = decompressor;
        }

        using var reader = new StreamReader(
            decoded,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        using var timeout = new CancellationTokenSource(TestTimeout);

        try
        {
            Assert.Equal(first, await ReadFrameAsync(reader, timeout.Token));
            allowSecond.TrySetResult();
            Assert.Equal(second, await ReadFrameAsync(reader, timeout.Token));
            allowFinish.TrySetResult();
            await handlerCompleted.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            allowSecond.TrySetResult();
            allowFinish.TrySetResult();
            if (decompressor is not null)
                await decompressor.DisposeAsync();
        }
    }

    [Fact]
    public async Task DefaultMobileClient_RequestsAndAutomaticallyDecompressesGzip()
    {
        var expected = new RemoteHello
        {
            InstanceId = "gzip-default-handler",
            HostName = new string('h', RemoteHttpListener.JsonCompressionThresholdBytes * 2),
            UserName = "Tester",
            AppVersion = "test"
        };
        var acceptedGzip = false;

        using var listener = new RemoteHttpListener(async (context, cancellationToken) =>
        {
            acceptedGzip = context.Request.AcceptsGzip();
            await context.WriteJsonAsync(
                JsonSerializer.Serialize(expected, RemoteJsonContext.Default.RemoteHello),
                cancellationToken);
        });
        listener.Start(0);

        await using var client = new LumiRemoteClient("gzip-device", "Gzip Phone");
        var actual = await client.HelloAsync(
            $"http://127.0.0.1:{listener.Port}",
            CancellationToken.None);

        Assert.True(acceptedGzip);
        Assert.NotNull(actual);
        Assert.Equal(expected.InstanceId, actual!.InstanceId);
        Assert.Equal(expected.HostName, actual.HostName);
    }

    [Fact]
    public void RepresentativeMobilePayloadsReduceByMoreThanSeventyPercent()
    {
        var snapshot = RepresentativeSnapshot();
        var transcript = RepresentativeTranscript();
        var snapshotRaw = JsonSerializer.SerializeToUtf8Bytes(
            snapshot,
            RemoteJsonContext.Default.RemoteSnapshot);
        var transcriptRaw = JsonSerializer.SerializeToUtf8Bytes(
            transcript,
            RemoteJsonContext.Default.RemoteTranscript);
        var snapshotGzip = RemoteHttpContext.CompressGzip(snapshotRaw);
        var transcriptGzip = RemoteHttpContext.CompressGzip(transcriptRaw);
        var snapshotReduction = 1d - (double)snapshotGzip.Length / snapshotRaw.Length;
        var transcriptReduction = 1d - (double)transcriptGzip.Length / transcriptRaw.Length;

        Console.WriteLine(
            $"1,751-chat snapshot: {snapshotRaw.Length:N0} raw, {snapshotGzip.Length:N0} gzip, " +
            $"{snapshotReduction:P1} reduction.");
        Console.WriteLine(
            $"Bounded transcript: {transcriptRaw.Length:N0} raw, {transcriptGzip.Length:N0} gzip, " +
            $"{transcriptReduction:P1} reduction.");

        Assert.Equal(1_751, snapshot.Chats.TotalCount);
        Assert.Equal(RemoteProtocol.ChatPageSize, snapshot.Chats.Groups.Sum(group => group.Chats.Count));
        Assert.InRange(snapshotRaw.Length, 45_000, 150_000);
        Assert.InRange(transcriptRaw.Length, 140_000, 180_000);
        Assert.True(
            snapshotReduction > 0.70,
            $"Snapshot reduction was only {snapshotReduction:P1}.");
        Assert.True(
            transcriptReduction > 0.70,
            $"Transcript reduction was only {transcriptReduction:P1}.");
    }

    [Fact]
    public void RepresentativeSnapshotFitsEverySseTransportCeiling()
    {
        var json = JsonSerializer.Serialize(
            RepresentativeSnapshot(),
            RemoteJsonContext.Default.RemoteSnapshot);
        var frame = new RemoteEventFrame(RemoteProtocol.Events.Snapshot, json);
        var wire = frame.ToWire();
        var dataLine = Assert.Single(
            wire.Split('\n'),
            line => line.StartsWith("data:", StringComparison.Ordinal));

        Assert.True(Encoding.UTF8.GetByteCount(json) <= RemoteProtocol.MaxSnapshotJsonBytes);
        Assert.True(Encoding.UTF8.GetByteCount(dataLine) <= RemoteProtocol.MaxSseLineBytes);
        Assert.True(Encoding.UTF8.GetByteCount(wire) <= RemoteProtocol.MaxSseFrameBytes);
        Assert.True(Encoding.UTF8.GetByteCount(wire) <= RemoteEventClient.MaxQueuedBytes);

        var reader = new RemoteEventFrame.Reader();
        RemoteEventFrame? parsed = null;
        foreach (var line in wire.Split('\n'))
            parsed = reader.Push(line) ?? parsed;

        Assert.Equal(RemoteProtocol.Events.Snapshot, parsed?.Event);
        Assert.Equal(json, parsed?.Data);
    }

    private static RemoteSnapshot RepresentativeSnapshot()
    {
        var chats = Enumerable.Range(0, 1_751)
            .Select(index => new RemoteChat
            {
                Id = FixtureGuid(index),
                Title = $"Mobile chat {index:D4} transport notes",
                ProjectId = FixtureGuid(10_000 + index % 24),
                ProjectName = $"Project {index % 24:D2}",
                AgentId = FixtureGuid(20_000 + index % 8),
                AgentName = $"Agent {index % 8}",
                AgentGlyph = "✦",
                MessageCount = 20 + index % 400,
                UpdatedAt = new DateTimeOffset(2026, 8, 5, 16, 0, 0, TimeSpan.Zero)
                    .AddMinutes(-index),
                IsPinned = index % 17 == 0,
                IsRunning = index % 41 == 0,
                HasUnreadMessages = index % 7 == 0,
                LastModelUsed = index % 2 == 0 ? "claude-sonnet-5" : "gpt-5.6",
                Preview =
                    $"Snapshot, transcript, and reconnect notes for mobile conversation {index:D4} " +
                    "and catalog metadata."
            })
            .ToList();

        return new RemoteSnapshot
        {
            HostName = "LUMI-DESKTOP",
            IsConnected = true,
            ActiveChatId = chats[^1].Id,
            Chats = new RemoteChatPage
            {
                TotalCount = chats.Count,
                HasMore = chats.Count > RemoteProtocol.ChatPageSize,
                Groups =
                [
                    new RemoteChatGroup
                    {
                        Label = "Recent",
                        Chats = [.. chats.Take(RemoteProtocol.ChatPageSize)]
                    }
                ]
            },
            Library = new RemoteLibrary
            {
                Projects =
                [
                    .. Enumerable.Range(0, 24).Select(index => new RemoteProject
                    {
                        Id = FixtureGuid(10_000 + index),
                        Name = $"Project {index:D2}",
                        ChatCount = chats.Count(chat => chat.ProjectId == FixtureGuid(10_000 + index))
                    })
                ]
            },
            Settings = new RemoteSettings
            {
                UserName = "Mobile transport fixture",
                PreferredModel = "claude-sonnet-5",
                AvailableModels = ["claude-sonnet-5", "gpt-5.6", "gemini-3.1-pro-preview"],
                ModelDisplayNames =
                [
                    "claude-sonnet-5=Claude Sonnet 5",
                    "gpt-5.6=GPT-5.6",
                    "gemini-3.1-pro-preview=Gemini 3.1 Pro"
                ],
                ModelReasoningEfforts =
                [
                    "claude-sonnet-5=low,medium,high,xhigh",
                    "gpt-5.6=low,medium,high,xhigh"
                ]
            }
        };
    }

    private static RemoteTranscript RepresentativeTranscript()
    {
        var userText = string.Join(
            ' ',
            Enumerable.Repeat(
                "Please compare startup snapshot traffic and transcript synchronization.",
                8));
        var assistantText = string.Join(
            ' ',
            Enumerable.Repeat(
                "The mobile transport should reuse the first SSE snapshot and stream compressed updates.",
                26));
        var chatId = FixtureGuid(50_000);

        return new RemoteTranscript
        {
            ChatId = chatId,
            Title = "Measured bounded mobile transcript",
            RevisionEpoch = "transport-fixture",
            Revision = 157,
            WindowStartMessageIndex = 400,
            WindowEndMessageIndex = 500,
            TotalRawMessageCount = 500,
            HasEarlierMessages = true,
            IsLatestWindow = true,
            Turns =
            [
                .. Enumerable.Range(0, 50).Select(index => new RemoteTranscriptTurn
                {
                    Id = $"turn-{index:D3}",
                    Items =
                    [
                        new RemoteTranscriptItem
                        {
                            Id = $"user-{index:D3}",
                            Kind = RemoteProtocol.ItemKinds.User,
                            Text = userText,
                            Author = "Adir",
                            Timestamp = new DateTimeOffset(2026, 8, 5, 14, 0, 0, TimeSpan.Zero)
                                .AddMinutes(index * 2)
                        },
                        new RemoteTranscriptItem
                        {
                            Id = $"assistant-{index:D3}",
                            Kind = RemoteProtocol.ItemKinds.Assistant,
                            Text = assistantText,
                            Author = "Lumi",
                            Model = index % 2 == 0 ? "claude-sonnet-5" : "gpt-5.6",
                            Timestamp = new DateTimeOffset(2026, 8, 5, 14, 1, 0, TimeSpan.Zero)
                                .AddMinutes(index * 2)
                        }
                    ]
                })
            ],
            Status = new RemoteChatStatus
            {
                ChatId = chatId,
                Model = "claude-sonnet-5",
                ContextCurrentTokens = 42_000,
                ContextTokenLimit = 200_000,
                Suggestions =
                [
                    "Measure compressed bytes",
                    "Verify reconnect request counts",
                    "Keep source-generated JSON"
                ]
            }
        };
    }

    private static Guid FixtureGuid(int value) =>
        Guid.Parse($"00000000-0000-0000-0000-{value:D12}");

    private static byte[] DecompressGzip(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static async Task WriteFrameAsync(
        Stream stream,
        RemoteEventFrame frame,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(frame.ToWire()), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<RemoteEventFrame> ReadFrameAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var frames = new RemoteEventFrame.Reader();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                throw new EndOfStreamException("SSE response ended before the next frame.");
            if (frames.Push(line) is { } frame)
                return frame;
        }
    }

    private static string[] ConstantValues(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();
}
