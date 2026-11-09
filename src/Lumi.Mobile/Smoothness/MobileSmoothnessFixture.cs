#if DEBUG || LUMI_MOBILE_SMOOTHNESS
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;

namespace Lumi.Mobile.Smoothness;

/// <summary>A deterministic, paged PC in memory. No model, pairing, disk profile or real network is used.</summary>
internal sealed class MobileSmoothnessFixture : HttpMessageHandler
{
    internal const string Version = "mobile-smoothness-v5";
    internal const int ChatCount = 1800;
    internal const int TurnCount = 6000;
    internal const int ModelCount = 72;
    private static readonly DateTimeOffset Date = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Kinds = ["plain", "rich", "code", "mixed"];
    private static readonly string[] Capabilities =
    [
        RemoteProtocol.Capabilities.ScopedEventsV1,
        RemoteProtocol.Capabilities.CompactTranscriptV1,
        RemoteProtocol.Capabilities.ModelPinsV1
    ];

    internal static Guid ChatId(int index) => new(index + 1, 0, 0, new byte[8]);
    private static Guid ProjectId(int index) => new(index + 1, 1, 0, new byte[8]);

    internal static MobileShellViewModel CreateShell()
    {
        var fixture = new MobileSmoothnessFixture();
        return new MobileShellViewModel(
            new LumiRemoteClient("smoothness-fixture", "Smoothness fixture", fixture),
            store: new MemorySettings());
    }

    private sealed class MemorySettings : IMobileSettingsStore
    {
        public MobileConnectionSettings Load() => new()
        {
            DeviceId = "smoothness-fixture",
            DeviceName = "Smoothness fixture",
            BaseUrl = "http://127.0.0.1:47699",
            Token = "in-memory-fixture-only",
            HostName = "Benchmark PC",
            Theme = "Dark",
            Language = "en",
            NotificationsPromptAnswered = true
        };

        public void Save(MobileConnectionSettings settings) { }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidDataException("Missing fixture route.");
        if (uri.Host != "127.0.0.1" || uri.Port != 47699)
            throw new InvalidDataException($"A benchmark attempted to leave its fixture: {uri}.");
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(part => part[0], part => Uri.UnescapeDataString(part.ElementAtOrDefault(1) ?? ""));

        int Number(string key, int fallback) => query.TryGetValue(key, out var value)
            ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;

        switch (uri.AbsolutePath)
        {
            case RemoteProtocol.Routes.Hello:
                return Json(new RemoteHello
                {
                    HostName = "Benchmark PC", UserName = "Benchmark", IsPaired = true,
                    InstanceId = Version, Capabilities = [.. Capabilities]
                }, RemoteJsonContext.Default.RemoteHello);
            case RemoteProtocol.Routes.Snapshot:
                return Json(Snapshot(), RemoteJsonContext.Default.RemoteSnapshot);
            case RemoteProtocol.Routes.Events:
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new EventStream(Encoding.UTF8.GetBytes(
                        new RemoteEventFrame(RemoteProtocol.Events.Snapshot,
                            JsonSerializer.Serialize(Snapshot(), RemoteJsonContext.Default.RemoteSnapshot)).ToWire())))
                    {
                        Headers = { ContentType = new("text/event-stream") }
                    }
                };
            case RemoteProtocol.Routes.Subscription:
                return Json(new RemoteCommandResult { Ok = true }, RemoteJsonContext.Default.RemoteCommandResult);
            case RemoteProtocol.Routes.Chats:
                return Json(ChatPage(Number("offset", 0), Number("limit", 50),
                    query.GetValueOrDefault("q"),
                    query.TryGetValue("projectId", out var project) ? Guid.Parse(project) : null),
                    RemoteJsonContext.Default.RemoteChatPage);
            case RemoteProtocol.Routes.Transcript:
                return Json(Transcript(Guid.Parse(query["chatId"]), Number("limit", 48),
                    query.ContainsKey("beforeMessageIndex") ? Number("beforeMessageIndex", 0) : null),
                    RemoteJsonContext.Default.RemoteTranscript);
            case RemoteProtocol.Routes.Command:
                var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                var command = JsonSerializer.Deserialize(bytes, RemoteJsonContext.Default.RemoteCommand)
                    ?? throw new InvalidDataException("Missing fixture command.");
                // Only the existing read/selection paths exercised by these scenarios are supported.
                if (command.Action is not (RemoteProtocol.Actions.OpenChat
                    or RemoteProtocol.Actions.ConfigureChat or RemoteProtocol.Actions.PinModel))
                    throw new InvalidDataException($"Unexpected benchmark command: {command.Action}.");
                return Json(new RemoteCommandResult { Ok = true, RequestId = command.RequestId },
                    RemoteJsonContext.Default.RemoteCommandResult);
            default:
                throw new InvalidDataException($"Unexpected benchmark route: {uri.AbsolutePath}.");
        }
    }

    private static HttpResponseMessage Json<T>(T value, JsonTypeInfo<T> type)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, type))
            {
                Headers = { ContentType = new("application/json") }
            }
        };
        response.Headers.Add(RemoteProtocol.ServerResponseHeader, RemoteProtocol.ServerResponseValue);
        return response;
    }

    internal static RemoteChatPage ChatPage(int offset, int limit, string? query = null, Guid? projectId = null)
    {
        var matching = Enumerable.Range(0, ChatCount).Where(index =>
            (projectId is null || ProjectId(index % 20) == projectId)
            && (string.IsNullOrWhiteSpace(query) || Title(index).Contains(query, StringComparison.OrdinalIgnoreCase)));
        var count = matching.Count();
        var rows = matching.Skip(offset).Take(Math.Clamp(limit, 1, RemoteProtocol.MaxChatPageSize))
            .Select(index => new RemoteChat
            {
                Id = ChatId(index), Title = Title(index), ProjectId = ProjectId(index % 20),
                ProjectName = $"Project {index % 20 + 1}", MessageCount = TurnCount * 2,
                UpdatedAt = Date.AddMinutes(-index), LastModelUsed = "gpt-6.1-sol",
                Preview = $"Conversation {index + 1}: a deterministic, bounded mobile workload."
            }).ToList();
        return new RemoteChatPage
        {
            Offset = offset, TotalCount = count, HasMore = offset + rows.Count < count,
            Query = query, ProjectId = projectId,
            Groups = [new() { Label = "Benchmark conversations", Chats = rows }]
        };
    }

    private static string Title(int index) => index < Kinds.Length
        ? $"Benchmark {Kinds[index]} transcript" : $"Conversation {index + 1:0000}";

    internal static RemoteSnapshot Snapshot()
    {
        var models = Enumerable.Range(0, ModelCount).Select(index => new RemoteModelOption
        {
            Model = index == 0 ? "gpt-6.1-sol" : $"benchmark-model-{index:00}",
            DisplayName = index == 0 ? "GPT-6.1 Sol" : $"Model {index:00}",
            ProviderName = index < 24 ? "GitHub Copilot" : $"Account {index / 12}",
            Account = index < 24 ? null : $"Account {index / 12}",
            IsPinned = index is 0 or 24, Cost = "1x", ContextSize = "400K"
        }).ToList();
        return new RemoteSnapshot
        {
            HostName = "Benchmark PC", IsConnected = true, Capabilities = [.. Capabilities],
            Chats = ChatPage(0, 50),
            Settings = new()
            {
                UserName = "Benchmark", PreferredModel = "gpt-6.1-sol",
                AvailableModels = models.Select(model => model.Model).ToList(),
                ModelOptions = models,
                ModelReasoningEfforts = ["gpt-6.1-sol=low,medium,high"],
                ModelContextWindowTiers = ["gpt-6.1-sol=Default,Long"],
                ModelDefaults = [new() { Model = "gpt-6.1-sol", Quality = "medium", ContextWindowTier = "Default" }]
            },
            Library = new()
            {
                Projects = Enumerable.Range(0, 20).Select(index => new RemoteProject
                {
                    Id = ProjectId(index), Name = $"Project {index + 1}",
                    Instructions = "Deterministic benchmark project."
                }).ToList(),
                Skills = Enumerable.Range(0, 30).Select(index => new RemoteSkill
                {
                    Id = ChatId(index + ChatCount), Name = $"Skill {index + 1}",
                    Description = "A representative reusable capability", IconGlyph = "S"
                }).ToList(),
                Jobs = Enumerable.Range(0, 20).Select(index => new RemoteJob
                {
                    Id = ChatId(index + ChatCount * 2), Name = $"Job {index + 1}",
                    Description = "A representative scheduled task", IsEnabled = true,
                    ScheduleSummary = "Every day", LastRunStatus = "Completed", TriggerType = "time"
                }).ToList()
            }
        };
    }

    internal static RemoteTranscript Transcript(Guid chatId, int limit, int? before = null)
    {
        var index = Enumerable.Range(0, ChatCount).FirstOrDefault(i => ChatId(i) == chatId, -1);
        if (index < 0)
            throw new InvalidDataException($"Unknown fixture chat: {chatId}.");
        var kind = Kinds[index % Kinds.Length];
        var end = Math.Clamp((before ?? TurnCount * 2) / 2, 1, TurnCount);
        var count = Math.Clamp(limit / (kind is "rich" or "mixed" ? 3 : 2), 1, 24);
        var start = Math.Max(0, end - count);
        var turns = Enumerable.Range(start, end - start).Select(turn =>
        {
            var items = new List<RemoteTranscriptItem>
            {
                new()
                {
                    Id = $"{index}-user-{turn}", Kind = RemoteProtocol.ItemKinds.User,
                    Text = $"Explain step {turn + 1} and its trade-offs.", Timestamp = Date.AddSeconds(turn * 2)
                }
            };
            if (kind == "rich")
                items.Add(new()
                {
                    Id = $"{index}-work-{turn}", Kind = RemoteProtocol.ItemKinds.Activity,
                    ActivityId = $"{index}-work-{turn}", Status = "Completed",
                    ActionCount = 8, DurationMs = 12000, Label = "Checked the implementation",
                    FileChangeCount = 1,
                    FileChanges = [new() { Path = "Views\\Chat.axaml", FileName = "Chat.axaml", LinesAdded = 18, LinesRemoved = 6 }]
                });
            items.Add(new()
            {
                Id = $"{index}-answer-{turn}", Kind = RemoteProtocol.ItemKinds.Assistant,
                Text = Answer(kind, turn), Author = "Lumi", Model = "gpt-6.1-sol",
                Timestamp = Date.AddSeconds(turn * 2 + 1)
            });
            var answerId = items[^1].Id;
            if (kind == "mixed")
                items.Add(MixedItem(index, turn));
            return new RemoteTranscriptTurn
            {
                Id = $"{index}-turn-{turn}", FinalAnswerId = answerId, IsSettled = true, Items = items
            };
        }).ToList();
        return new()
        {
            ChatId = chatId, Title = Title(index), Revision = 1, RevisionEpoch = Version,
            WindowStartMessageIndex = start * 2, WindowEndMessageIndex = end * 2,
            TotalRawMessageCount = TurnCount * 2, HasEarlierMessages = start > 0,
            HasLaterMessages = end < TurnCount, IsLatestWindow = end == TurnCount, Turns = turns,
            Status = new()
            {
                ChatId = chatId, Model = "gpt-6.1-sol", ProjectId = ProjectId(index % 20),
                ProjectName = $"Project {index % 20 + 1}", HasUnreadMessages = false,
                Quality = "medium", QualityLevels = ["low", "medium", "high"],
                ContextWindowTier = "Default", ContextWindowTiers = ["Default", "Long"]
            }
        };
    }

    private static string Answer(string kind, int turn)
    {
        if (kind == "mixed")
            return $"### Step {turn + 1}\n\nA short answer before the next transcript card. "
                + "**Keep the reader's place** when this content appears.\n\n"
                + (turn % 3 == 0 ? "| Check | Result |\n|---|---|\n| Layout | Preserved |\n| Paging | Bounded |"
                    : turn % 3 == 1 ? "```csharp\nawait RenderAsync(viewport);\nreturn frame;\n```"
                    : "- Measure cold presentation\n- Keep the same actions and appearance");
        var text = $"### Step {turn + 1}\n\n" + string.Concat(Enumerable.Repeat(
            "A **bounded transcript** keeps real markdown wrapping, `inline code`, and readable content "
            + "on screen while the thumb moves. Preserve the existing appearance and behavior. ", 5));
        if (kind == "plain")
            return text.Replace("**", "").Replace("`", "");
        if (kind == "rich")
            return text + "\n\n- Preserve the reader's position\n- Keep asynchronous work scoped to the chat\n\n"
                + "| Check | Result |\n|---|---|\n| Layout | Preserved |\n| Paging | Bounded |\n\n"
                + "> Measure the content that actually appears.\n\n```csharp\nreturn await BuildAsync();\n```";
        return text + "\n\n```csharp\n" + string.Concat(Enumerable.Range(0, 120).Select(line =>
            $"var frame{line} = RenderFrame({turn}, {line}, viewport, cancellationToken);\n")) + "```";
    }

    private static RemoteTranscriptItem MixedItem(int index, int turn)
    {
        var item = new RemoteTranscriptItem { Id = $"{index}-card-{turn}", Status = "Completed" };
        switch (turn % 6)
        {
            case 0:
                item.Kind = RemoteProtocol.ItemKinds.Question;
                item.Question = new()
                {
                    QuestionId = item.Id, Text = "Which part should we work on next?",
                    Options = ["Scrolling", "Keyboard", "Chat switching"], IsAnswered = true, Answer = "Scrolling"
                };
                break;
            case 1:
                item.Kind = RemoteProtocol.ItemKinds.Input;
                item.InputRequest = new()
                {
                    Status = "Submitted",
                    Definition = """{"id":"review","title":"Review preferences","fields":[{"id":"area","label":"Area","value":"Mobile"},{"id":"priority","label":"Priority","type":"select","options":["Normal","High"],"value":"High"}]}"""
                };
                break;
            case 2:
                item.Kind = RemoteProtocol.ItemKinds.ToolGroup;
                item.Tools = [new()
                {
                    Id = item.Id, Name = "view", DisplayName = "Reading transcript layout",
                    Input = "src\\Views\\Chat.axaml", Output = "The existing layout was preserved.", Status = "Completed"
                }, new()
                {
                    Id = item.Id + "-test", Name = "powershell", DisplayName = "Checking the implementation",
                    Input = "dotnet test", Output = "All focused cases passed.", Status = "Completed"
                }];
                break;
            case 3:
                item.Kind = RemoteProtocol.ItemKinds.Terminal;
                item.Label = "Run focused checks";
                item.Text = "dotnet test\nAll focused cases passed.";
                break;
            case 4:
                item.Kind = RemoteProtocol.ItemKinds.File;
                item.Attachments = [new() { Path = "report.txt", FileName = "report.txt", Extension = ".txt" }];
                break;
            default:
                item.Kind = RemoteProtocol.ItemKinds.Activity;
                item.ActivityId = item.Id;
                item.Label = "Checked the implementation";
                item.ActionCount = 8;
                item.DurationMs = 12000;
                item.FileChangeCount = 1;
                item.FileChanges = [new() { Path = "Views\\Chat.axaml", FileName = "Chat.axaml", LinesAdded = 18, LinesRemoved = 6 }];
                break;
        }
        return item;
    }

    private sealed class EventStream(byte[] bootstrap) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position == bootstrap.Length)
            {
                await Task.Delay(TimeSpan.FromSeconds(25), cancellationToken);
                var ping = Encoding.UTF8.GetBytes(new RemoteEventFrame("ping", "{}").ToWire());
                ping.CopyTo(buffer);
                return ping.Length;
            }
            var count = Math.Min(buffer.Length, bootstrap.Length - _position);
            bootstrap.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
#endif
