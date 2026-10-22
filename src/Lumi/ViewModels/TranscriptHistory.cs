using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Storage;

namespace Lumi.ViewModels;

/// <summary>What earlier exchanges outside a build already showed (see <see cref="TranscriptBuilder"/>).</summary>
public sealed record TranscriptSeed(
    int ExchangeIndex,
    IReadOnlyCollection<string> ShownSkillNames,
    IReadOnlyDictionary<string, int> AnnouncedFiles)
{
    /// <summary>
    /// Tool calls of earlier exchanges that steps of this build continue: a background run that went
    /// on working after the next message. Those steps are part of the run's card, not of this build.
    /// </summary>
    public IReadOnlyList<EarlierToolCall> EarlierToolCalls { get; init; } = [];
}

/// <summary>A tool call of an earlier exchange: its id, its parent's, and whether it is a sub-agent run.</summary>
public sealed record EarlierToolCall(string ToolCallId, string? ParentToolCallId, bool IsRun);

/// <summary>Stands in for a stored exchange that is not built, reserving the space it will take.</summary>
public sealed partial class HistoryPlaceholderItem : TranscriptItem
{
    [ObservableProperty] private double _height;

    public HistoryPlaceholderItem(string stableId, double height)
        : base(stableId)
    {
        _height = height;
    }
}

/// <summary>One stored exchange of the open chat: a user message and everything up to the next one.</summary>
internal sealed class TranscriptHistorySegment
{
    public required long StartSeq { get; init; }
    public required long EndSeq { get; init; }

    /// <summary>User messages before this exchange: the builder's exchange counter at its start.</summary>
    public required int ExchangeIndex { get; init; }

    public required TranscriptTurn Placeholder { get; init; }
    public required HistoryPlaceholderItem PlaceholderItem { get; init; }
    public bool IsFirst { get; init; }

    /// <summary>
    /// Whether the exchange starts with a message of the user's own, not a job's wake or a form's receipt
    /// (see <see cref="TranscriptBuilder.ShowsAsUserMessage"/>).
    /// </summary>
    public bool StartsWithUserMessage { get; init; }

    /// <summary>The built turns while hydrated (possibly none), or null while only the placeholder shows.</summary>
    public List<TranscriptTurn>? Turns { get; set; }

    /// <summary>The messages its build read while hydrated: all of them but the steps of its runs (see <see cref="HistoryExchangeRows"/>).</summary>
    public List<ChatMessage>? Messages { get; set; }

    /// <summary>
    /// Of <see cref="Messages"/>, those that came right after messages the build left out (see
    /// <see cref="HistoryExchangeRows.OmittedBefore"/>), so a build from them again reads the same.
    /// </summary>
    public IReadOnlySet<long>? OmittedBefore { get; set; }

    /// <summary>Steps this exchange's runs took after it, while hydrated (see <see cref="TranscriptSeed.EarlierToolCalls"/>).</summary>
    public List<ChatMessage>? LaterRunSteps { get; set; }

    /// <summary>The runs of earlier exchanges this exchange's steps continue, once read.</summary>
    public IReadOnlyList<EarlierToolCall>? EarlierToolCalls { get; set; }

    public TranscriptSeed? Seed { get; set; }
    public bool IsLoading { get; set; }
    public long LastActiveTick { get; set; }

    /// <summary>Set while something waits for this exchange to be built (a jump to one of its messages).</summary>
    public TaskCompletionSource? Waiter { get; set; }

    /// <summary>History rows the reader opened; restored when the exchange is built again.</summary>
    public HashSet<string> ExpandedSummaries { get; } = new(StringComparer.Ordinal);

    /// <summary>The heights the exchange's turns were measured at when it was released, by turn.</summary>
    public Dictionary<string, double>? TurnHeights { get; set; }

    /// <summary>Collapsed rows whose steps are being built because the reader opened them.</summary>
    public HashSet<string> PendingRestores { get; } = new(StringComparer.Ordinal);

    public bool IsHydrated => Turns is not null;
    public int HydratedMessageCount => (Messages?.Count ?? 0) + (LaterRunSteps?.Count ?? 0);
    public IReadOnlyList<TranscriptTurn> CurrentTurns => Turns ?? (IReadOnlyList<TranscriptTurn>)[Placeholder];
    public bool IsVisible => CurrentTurns.Any(static turn => turn.IsViewportActive);

    /// <summary>On screen (or near it), or awaited by a jump: worth reading and building.</summary>
    public bool IsWanted => Waiter is not null || IsVisible;

    public void ReleaseWaiter()
    {
        var waiter = Waiter;
        Waiter = null;
        waiter?.TrySetResult();
    }
}

/// <summary>
/// The stored history of the open chat, shown on demand. The chat's live tail is built by the chat's
/// own transcript builder; everything before it stays in the database. Each older exchange appears
/// as a placeholder that reserves its height; when the reader scrolls near it, its messages are read
/// and built — with a builder seeded by what earlier exchanges showed, so it reads exactly as before —
/// and swapped in. The steps its runs took are left in storage (see <see cref="HistoryExchangeRows"/>):
/// a run's card shows without them, and its page reads them when it opens. Exchanges the reader left
/// behind are released again under a budget, so memory follows what is on screen rather than the
/// chat's length. Collapsed "worked for" rows of older exchanges keep only what they show; their
/// steps are built when opened.
/// </summary>
internal sealed class TranscriptHistory
{
    internal const int MaxHydratedMessages = 3_000;
    internal const int MaxHydratedSegments = 16;

    /// <summary>
    /// The <see cref="TranscriptHistorySegment.StartSeq"/> of conversation before the chat's first user
    /// message (rare), an exchange of its own that starts with the chat.
    /// </summary>
    internal const long ChatStartSeq = long.MinValue + 1;

    /// <summary>True when <paramref name="turn"/> stands in for an older exchange that is not built.</summary>
    internal static bool IsPlaceholder(TranscriptTurn turn)
        => turn.HistorySegment is TranscriptHistorySegment segment && ReferenceEquals(turn, segment.Placeholder);

    private const int MaxConcurrentReads = 2;
    private const double BuildSliceMs = 8;
    private static readonly TimeSpan BuildSliceInterval = TimeSpan.FromMilliseconds(1);

    private readonly DataStore _dataStore;
    private readonly Func<TranscriptBuilder> _createBuilder;
    private readonly Action<IReadOnlyList<TranscriptTurn>> _turnsHydrated;
    private readonly List<TranscriptHistorySegment> _segments = [];
    private readonly HashSet<TranscriptHistorySegment> _wanted = [];
    private readonly List<ExchangeBuild> _builds = [];
    private Chat? _chat;
    private ObservableCollection<TranscriptTurn>? _target;
    private TranscriptHistoryFacts _facts = TranscriptHistoryFacts.Empty;
    private IReadOnlyList<EarlierToolCall> _tailEarlierToolCalls = [];
    private ForkOrigin? _forkOrigin;
    private long _tailStartSeq;
    private int _generation;
    private long _tick;
    private int _activeReads;
    private bool _pumpQueued;
    private bool _drainQueued;

    public TranscriptHistory(
        DataStore dataStore,
        Func<TranscriptBuilder> createBuilder,
        Action<IReadOnlyList<TranscriptTurn>> turnsHydrated)
    {
        _dataStore = dataStore;
        _createBuilder = createBuilder;
        _turnsHydrated = turnsHydrated;
    }

    /// <summary>Raised after exchanges were built or released (diagnostics, view bookkeeping).</summary>
    public event Action? Changed;

    public Chat? Chat => _chat;
    public bool HasHistory => _segments.Count > 0;
    public IReadOnlyList<TranscriptHistorySegment> Segments => _segments;
    public int HydratedSegmentCount => _segments.Count(static segment => segment.IsHydrated);
    public int HydratedMessageCount => _segments.Sum(static segment => segment.HydratedMessageCount);

    /// <summary>True while an exchange the reader reached is still being read or built.</summary>
    public bool HasPendingWork => _wanted.Count > 0 || _activeReads > 0 || _builds.Count > 0;

    /// <summary>
    /// Reads the outline of the chat's stored history (positions of its conversation messages,
    /// from an index) and the few facts later exchanges need, and lays out one placeholder per
    /// exchange. Call after the chat's tail is loaded. Returns false when there is no history.
    /// </summary>
    public async Task<bool> LoadAsync(Chat chat, ForkOrigin? forkOrigin, CancellationToken cancellationToken)
    {
        Reset();
        var generation = _generation;
        _chat = chat;
        var window = _dataStore.GetTranscriptWindow(chat);
        if (!window.HasHistory)
            return false;

        var outlineTask = _dataStore.ReadChatOutlineAsync(chat, cancellationToken);
        var factsTask = _dataStore.ReadChatHistoryByKindAsync(
            chat, [ChatMessageKinds.Skill, ChatMessageKinds.Deliverable], cancellationToken);
        var tailEarlierTask = ReadEarlierToolCallsAsync(_dataStore, chat, [.. chat.Messages], window.TailStartSeq);
        var outline = await outlineTask.ConfigureAwait(true);
        var factRows = await factsTask.ConfigureAwait(true);
        var tailEarlier = await tailEarlierTask.ConfigureAwait(true);
        if (generation != _generation)
            return false;

        _forkOrigin = forkOrigin;
        _tailStartSeq = window.TailStartSeq;
        _tailEarlierToolCalls = tailEarlier;
        _facts = TranscriptHistoryFacts.Create(outline, factRows);
        BuildSegments(outline, window.TailStartSeq);
        return _segments.Count > 0;
    }

    /// <summary>What the stored history showed before the live tail, for the chat's own builder.</summary>
    public TranscriptSeed? SeedForTail()
        => _segments.Count == 0 ? null : _facts.SeedBefore(_tailStartSeq) with { EarlierToolCalls = _tailEarlierToolCalls };

    /// <summary>
    /// The tool calls of earlier exchanges (stored before <paramref name="beforeSeq"/>) that steps
    /// among <paramref name="messages"/> continue: the runs a background agent's later steps belong
    /// to, and anything between such a step and its run.
    /// </summary>
    internal static async Task<IReadOnlyList<EarlierToolCall>> ReadEarlierToolCallsAsync(
        DataStore dataStore,
        Chat chat,
        IReadOnlyList<ChatMessage> messages,
        long beforeSeq)
    {
        var stored = await dataStore.ReadEarlierToolCallsAsync(
            chat,
            messages.Select(static message => (message.ToolCallId, message.ParentToolCallId)).ToList(),
            beforeSeq).ConfigureAwait(false);
        return ToEarlierToolCalls(stored);
    }

    private static List<EarlierToolCall> ToEarlierToolCalls(IEnumerable<StoredToolCall> calls)
        => calls
            .Select(static call => new EarlierToolCall(call.ToolCallId, call.ParentToolCallId, ToolDisplayHelper.IsSubagentTool(call.ToolName)))
            .ToList();

    /// <summary>
    /// Puts the history (each exchange's built turns, or its placeholder) at the start of a freshly
    /// built live transcript. Built exchanges are reused as they are, keeping their realized views.
    /// </summary>
    public void InsertInto(ObservableCollection<TranscriptTurn> turns)
    {
        _target = turns;
        var index = 0;
        foreach (var segment in _segments)
        {
            foreach (var turn in segment.CurrentTurns)
                turns.Insert(index++, turn);
        }
    }

    /// <summary>Drops every exchange and cancels pending loads (chat switch, clear).</summary>
    public void Reset()
    {
        _generation++;
        foreach (var segment in _segments)
        {
            if (_chat is not null && segment.Messages is { } messages)
                _dataStore.DetachHistoryMessages(_chat, messages);
            segment.ReleaseWaiter();
        }

        _segments.Clear();
        _wanted.Clear();
        foreach (var build in _builds)
            build.Abandoned?.Invoke();
        _builds.Clear();
        _activeReads = 0;
        _chat = null;
        _target = null;
        _facts = TranscriptHistoryFacts.Empty;
        _tailEarlierToolCalls = [];
        _forkOrigin = null;
        _tailStartSeq = 0;
    }

    /// <summary>
    /// Forgets the exchanges at and after <paramref name="cutSeq"/>: the chat's history was cut there
    /// (an older message was edited and sent again).
    /// </summary>
    public void TruncateFrom(long cutSeq)
    {
        for (var i = _segments.Count - 1; i >= 0; i--)
        {
            var segment = _segments[i];
            if (segment.StartSeq < cutSeq)
                break;
            RemoveFromTarget(segment);
            if (_chat is not null && segment.Messages is { } messages)
                _dataStore.DetachHistoryMessages(_chat, messages);
            _wanted.Remove(segment);
            _segments.RemoveAt(i);
            segment.ReleaseWaiter();
        }

        _tailStartSeq = Math.Min(_tailStartSeq, cutSeq);
        // What the cut conversation showed no longer seeds what follows.
        _facts = _facts.Before(cutSeq);
        Changed?.Invoke();
    }

    /// <summary>Releases every built exchange (a surface that is no longer shown keeps placeholders only).</summary>
    public void ReleaseAll()
    {
        foreach (var segment in _segments)
        {
            if (segment.IsHydrated)
                Dehydrate(segment);
        }

        _wanted.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// Takes exchanges that left the chat's live tail (see <see cref="DataStore.TryRollTail"/>) into
    /// the history as they are shown: each becomes an exchange whose turns are already built, which
    /// the history releases like any other once the reader leaves it. Nothing on screen changes. The
    /// facts later exchanges depend on, and the runs the remaining tail continues, are carried over.
    /// </summary>
    /// <param name="exchanges">The rolled exchanges in order, each with its turns and messages.</param>
    /// <param name="tail">The messages that stay in the live tail.</param>
    public void AdoptRolledTail(
        Chat chat,
        ObservableCollection<TranscriptTurn> target,
        IReadOnlyList<(List<TranscriptTurn> Turns, List<ChatMessage> Messages)> exchanges,
        IReadOnlyList<ChatMessage> tail,
        long tailStartSeq,
        ForkOrigin? forkOrigin)
    {
        if (_chat is not null && !ReferenceEquals(_chat, chat))
            throw new InvalidOperationException("The history shows another chat.");

        _chat = chat;
        _target = target;
        if (_segments.Count == 0)
            _forkOrigin = forkOrigin;

        var rolled = exchanges.SelectMany(static exchange => exchange.Messages).ToList();
        _facts = _facts.Extend(rolled);
        _tailEarlierToolCalls = EarlierToolCallsOf(tail, rolled, _tailEarlierToolCalls);
        for (var e = 0; e < exchanges.Count; e++)
        {
            var (turns, messages) = exchanges[e];
            var isFirst = _segments.Count == 0;
            var startSeq = isFirst && messages[0].Role != "user" ? ChatStartSeq : messages[0].StorageSeq;
            var endSeq = e + 1 < exchanges.Count ? exchanges[e + 1].Messages[0].StorageSeq : tailStartSeq;
            var height = turns.Sum(static turn => turn.MeasuredHeight > 0 ? turn.MeasuredHeight : TranscriptLayoutMetrics.MinimumEstimatedTurnHeight)
                         + Math.Max(0, turns.Count - 1) * TranscriptLayoutMetrics.TurnSpacing;
            var key = $"history:{(startSeq == ChatStartSeq ? "start" : startSeq.ToString(System.Globalization.CultureInfo.InvariantCulture))}";
            var item = new HistoryPlaceholderItem(key, height);
            var placeholder = new TranscriptTurn("turn:" + key) { MeasuredHeight = height };
            placeholder.Items.Add(item);
            var segment = new TranscriptHistorySegment
            {
                StartSeq = startSeq,
                EndSeq = endSeq,
                ExchangeIndex = _facts.ExchangeIndexAt(startSeq),
                Placeholder = placeholder,
                PlaceholderItem = item,
                IsFirst = isFirst,
                StartsWithUserMessage = TranscriptBuilder.ShowsAsUserMessage(
                    messages[0].Role, messages[0].Author, messages[0].Content, messages[0].InteractionReceipt is not null),
                Turns = turns,
                Messages = messages,
                LaterRunSteps = [],
                LastActiveTick = ++_tick,
            };
            Own(placeholder, segment);
            foreach (var turn in turns)
                Own(turn, segment);
            _segments.Add(segment);
        }

        _tailStartSeq = tailStartSeq;
        // Search adopts the turns' new owners before the budget releases any of them.
        Changed?.Invoke();
        EnforceBudget(justBuilt: null);
        Changed?.Invoke();
    }

    /// <summary>
    /// The tool calls before <paramref name="tail"/> that its steps continue, from the calls that
    /// just left the tail and those of earlier history (as <see cref="ReadEarlierToolCallsAsync"/>
    /// reads them from storage).
    /// </summary>
    private static IReadOnlyList<EarlierToolCall> EarlierToolCallsOf(
        IReadOnlyList<ChatMessage> tail,
        IReadOnlyList<ChatMessage> rolled,
        IReadOnlyList<EarlierToolCall> older)
    {
        var known = new Dictionary<string, EarlierToolCall>(StringComparer.Ordinal);
        foreach (var call in older)
            known[call.ToolCallId] = call;
        foreach (var message in rolled)
        {
            if (message.ToolCallId is { Length: > 0 } toolCallId)
                known[toolCallId] = new EarlierToolCall(toolCallId, message.ParentToolCallId, ToolDisplayHelper.IsSubagentTool(message.ToolName ?? ""));
        }

        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in tail)
        {
            if (message.ToolCallId is { Length: > 0 } toolCallId)
                defined.Add(toolCallId);
        }

        var calls = new List<EarlierToolCall>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(tail
            .Select(static message => message.ParentToolCallId)
            .OfType<string>()
            .Where(parent => parent.Length > 0 && !defined.Contains(parent)));
        while (pending.TryDequeue(out var toolCallId))
        {
            if (!seen.Add(toolCallId) || !known.TryGetValue(toolCallId, out var call))
                continue;
            calls.Add(call);
            if (!call.IsRun && call.ParentToolCallId is { Length: > 0 } parent && !defined.Contains(parent))
                pending.Enqueue(parent);
        }

        return calls;
    }

    // ── Loading and building ────────────────────────────────────────────────
    //
    // Viewport changes are reported while the transcript enumerates and lays out its turns, so that
    // callback only notes which exchanges are wanted. Reads run on the thread pool; building runs on
    // the UI thread in short slices between frames, like the realization of turns, so an exchange of
    // thousands of messages never stalls scrolling; and turns are swapped in only from those slices.

    private void OnTurnViewportActivity(TranscriptTurn turn, bool isActive)
    {
        if (!isActive || turn.HistorySegment is not TranscriptHistorySegment segment || !_segments.Contains(segment))
            return;

        segment.LastActiveTick = ++_tick;
        if (!segment.IsHydrated && !segment.IsLoading)
        {
            _wanted.Add(segment);
            QueuePump();
        }
    }

    private void QueuePump()
    {
        if (_pumpQueued)
            return;
        _pumpQueued = true;
        Dispatcher.UIThread.Post(PumpLoads, DispatcherPriority.Normal);
    }

    private void PumpLoads()
    {
        _pumpQueued = false;
        while (_activeReads < MaxConcurrentReads && _wanted.Count > 0)
        {
            // The exchange the reader reached most recently first; ones scrolled past are skipped.
            var next = _wanted.MaxBy(static segment => segment.LastActiveTick)!;
            _wanted.Remove(next);
            if (next.IsHydrated || next.IsLoading || !next.IsVisible || !_segments.Contains(next))
                continue;
            _ = LoadAsync(next);
        }
    }

    /// <summary>
    /// Builds an exchange now (a jump to one of its messages), whether or not it is on screen. Returns
    /// once its turns are laid out, so a caller can bring them into view.
    /// </summary>
    public async Task<IReadOnlyList<TranscriptTurn>?> EnsureHydratedAsync(TranscriptHistorySegment segment)
    {
        if (!_segments.Contains(segment))
            return null;
        segment.LastActiveTick = ++_tick;
        if (segment.IsHydrated)
            return segment.Turns;

        segment.Waiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = segment.Waiter.Task;
        if (!segment.IsLoading)
            _ = LoadAsync(segment);
        await waiter.ConfigureAwait(true);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        return segment.Turns;
    }

    /// <summary>The exchange holding a stored position, or null when it is part of the live tail.</summary>
    public TranscriptHistorySegment? FindSegment(long seq)
    {
        foreach (var segment in _segments)
        {
            if (seq >= segment.StartSeq && seq < segment.EndSeq)
                return segment;
        }

        return null;
    }

    /// <summary>
    /// Finds a message of the stored history, building its exchange so it can be shown: from a built
    /// exchange first, otherwise by its stored position.
    /// </summary>
    public async Task<(ChatMessage Message, IReadOnlyList<TranscriptTurn> Turns)?> RevealMessageAsync(Guid messageId)
    {
        var chat = _chat;
        if (chat is null || _segments.Count == 0)
            return null;

        foreach (var segment in _segments)
        {
            if (segment.Messages?.FirstOrDefault(message => message.Id == messageId) is { } built)
                return (built, segment.Turns!);
        }

        var stored = await _dataStore.FindChatMessageAsync(chat, messageId).ConfigureAwait(true);
        if (stored is null || !ReferenceEquals(chat, _chat) || FindSegment(stored.StorageSeq) is not { } owner)
            return null;

        var turns = await EnsureHydratedAsync(owner).ConfigureAwait(true);
        var message = owner.Messages?.FirstOrDefault(candidate => candidate.Id == messageId);
        return turns is null || message is null ? null : (message, turns);
    }

    /// <summary>Messages of built exchanges, for lookups that must find the object the transcript shows.</summary>
    public IEnumerable<ChatMessage> HydratedMessages
        => _segments.Where(static segment => segment.Messages is not null).SelectMany(static segment => segment.Messages!);

    private async Task LoadAsync(TranscriptHistorySegment segment)
    {
        var chat = _chat;
        if (chat is null || segment.IsLoading || segment.IsHydrated)
            return;

        var generation = _generation;
        segment.IsLoading = true;
        _activeReads++;
        ExchangeRead? read = null;
        try
        {
            read = await _dataStore.ReadHistoryExchangeAsync(chat, segment.StartSeq, segment.EndSeq, HistoryExchangeRows.Pick)
                .ConfigureAwait(true);
            // A background run can span exchanges: steps of an earlier run seen here belong to it.
            segment.EarlierToolCalls ??= ToEarlierToolCalls(read.Outline.EarlierToolCalls);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceWarning("[Transcript] Could not load an earlier exchange: {0}", ex);
        }
        finally
        {
            if (generation == _generation)
            {
                _activeReads = Math.Max(0, _activeReads - 1);
                QueuePump();
            }
        }

        if (generation != _generation)
            return;
        if (read is null || !_segments.Contains(segment) || segment.IsHydrated || !segment.IsWanted)
        {
            segment.IsLoading = false;
            if (read is null || !_segments.Contains(segment) || segment.IsHydrated)
                segment.ReleaseWaiter();
            return;
        }

        // An exchange that was cut off by a crash shows its work as stopped, as the tail does. Its
        // messages are saved once they are part of the transcript.
        var settled = InteractionHistory.SettleInterrupted(read.Messages);
        InteractionHistory.SettleInterrupted(read.LaterRunSteps);
        StartBuild(
            segment,
            read.Messages,
            read.LaterRunSteps,
            HistoryExchangeRows.OmittedBefore(read.Outline, read.Messages),
            settled,
            isRestore: false,
            CompleteHydration);
    }

    private void StartBuild(
        TranscriptHistorySegment segment,
        List<ChatMessage> messages,
        List<ChatMessage> laterSteps,
        IReadOnlySet<long>? omittedBefore,
        bool settled,
        bool isRestore,
        Action<ExchangeBuild, List<TranscriptTurn>> complete,
        Action? abandoned = null)
    {
        segment.Seed ??= _facts.SeedBefore(segment.StartSeq) with { EarlierToolCalls = segment.EarlierToolCalls ?? [] };
        var builder = _createBuilder();
        builder.KeepsRunDetailInStorage = true;
        builder.BeginRebuild(segment.IsFirst ? _forkOrigin : null, segment.Seed);
        _builds.Add(new ExchangeBuild(segment, messages, laterSteps, omittedBefore, builder, _createBuilder, complete, _generation, isRestore, settled)
        {
            Priority = isRestore ? ++_tick : segment.LastActiveTick,
            Abandoned = abandoned,
        });
        QueueDrain();
    }

    private void QueueDrain()
    {
        if (_drainQueued || _builds.Count == 0)
            return;
        _drainQueued = true;
        // A short timer lets input and rendering run between slices; Background priority would wait
        // for a moment without pending input, which never comes while the reader scrolls.
        DispatcherTimer.RunOnce(DrainBuilds, BuildSliceInterval, DispatcherPriority.Normal);
    }

    private void DrainBuilds()
    {
        _drainQueued = false;
        var slice = Stopwatch.StartNew();
        while (slice.Elapsed.TotalMilliseconds < BuildSliceMs && PickBuild() is { } build)
        {
            var started = slice.Elapsed.TotalMilliseconds;
            var done = build.Advance(slice, BuildSliceMs);
            build.BuildMs += slice.Elapsed.TotalMilliseconds - started;
            if (!done)
                continue;

            _builds.Remove(build);
            started = slice.Elapsed.TotalMilliseconds;
            var turns = build.Builder.EndRebuild().ToList();
            build.BuildMs += slice.Elapsed.TotalMilliseconds - started;
            build.Complete(build, turns);
        }

        QueueDrain();
    }

    // The build of the exchange the reader reached most recently (or just opened) goes first. Builds
    // that are no longer needed — the chat changed, the reader scrolled away, the row is gone — stop.
    private ExchangeBuild? PickBuild()
    {
        ExchangeBuild? best = null;
        for (var i = _builds.Count - 1; i >= 0; i--)
        {
            var build = _builds[i];
            if (!IsStillNeeded(build))
            {
                _builds.RemoveAt(i);
                build.Abandoned?.Invoke();
                if (!build.IsRestore)
                {
                    build.Segment.IsLoading = false;
                    if (!_segments.Contains(build.Segment))
                        build.Segment.ReleaseWaiter();
                }

                continue;
            }

            if (best is null || build.Priority > best.Priority)
                best = build;
        }

        return best;
    }

    private bool IsStillNeeded(ExchangeBuild build)
    {
        var segment = build.Segment;
        if (build.Generation != _generation || !_segments.Contains(segment))
            return false;
        return build.IsRestore ? segment.IsHydrated : !segment.IsHydrated && segment.IsWanted;
    }

    private void CompleteHydration(ExchangeBuild build, List<TranscriptTurn> turns)
    {
        var segment = build.Segment;
        segment.IsLoading = false;
        var target = _target;
        var index = target?.IndexOf(segment.Placeholder) ?? -1;
        if (_chat is not { } chat || target is null || index < 0)
        {
            segment.ReleaseWaiter();
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        _dataStore.AttachHistoryMessages(chat, build.Messages);
        // Settled work is saved like any other change: only when chats are saved automatically.
        if (build.Settled && _dataStore.Data.Settings.AutoSaveChats)
            _ = _dataStore.SaveChatAsync(chat);
        Compact(segment, turns);

        foreach (var turn in turns)
        {
            Own(turn, segment);
            // Turns shown before keep their measured height, so the exchange takes the space it took.
            if (segment.TurnHeights is { } heights && heights.TryGetValue(turn.StableId, out var height))
                turn.MeasuredHeight = height;
        }

        target.RemoveAt(index);
        for (var i = 0; i < turns.Count; i++)
            target.Insert(index + i, turns[i]);

        segment.Turns = turns;
        segment.Messages = build.Messages;
        segment.OmittedBefore = build.OmittedBefore;
        segment.LaterRunSteps = build.LaterSteps;
        _turnsHydrated(turns);
        EnforceBudget(segment);
        segment.ReleaseWaiter();
        TranscriptHistoryDiagnostics.RecordHydration(build.Messages.Count, build.BuildMs, stopwatch.Elapsed.TotalMilliseconds);
        Changed?.Invoke();
    }

    // Collapsed history rows keep only what they show; their steps are built again when opened.
    private void Compact(TranscriptHistorySegment segment, List<TranscriptTurn> turns)
    {
        foreach (var turn in turns)
        {
            foreach (var item in turn.Items)
            {
                if (item is not TurnSummaryItem { InnerItems.Count: > 0 } summary)
                    continue;

                if (segment.ExpandedSummaries.Contains(summary.StableId))
                {
                    summary.IsExpanded = true;
                    continue;
                }

                if (!summary.IsExpanded)
                    summary.DeferInnerItems(opened => RestoreSummary(segment, opened));
            }
        }
    }

    // Opening a collapsed row of an older exchange builds the exchange again (in slices) and moves
    // the row's steps over; the row shows them a few frames later.
    private void RestoreSummary(TranscriptHistorySegment segment, TurnSummaryItem summary)
    {
        if (segment.Messages is not { } messages || !segment.PendingRestores.Add(summary.StableId))
            return;

        segment.ExpandedSummaries.Add(summary.StableId);
        StartBuild(segment, messages, segment.LaterRunSteps ?? [], segment.OmittedBefore, settled: false, isRestore: true, (_, turns) =>
        {
            segment.PendingRestores.Remove(summary.StableId);
            if (segment.Turns is not { } shown || !shown.Any(turn => turn.Items.Contains(summary)))
                return;

            var rebuilt = turns
                .SelectMany(static turn => turn.Items)
                .OfType<TurnSummaryItem>()
                .FirstOrDefault(candidate => candidate.StableId == summary.StableId);
            summary.RestoreDeferredItems(rebuilt?.InnerItems.ToList() ?? []);
            rebuilt?.InnerItems.Clear();
        }, abandoned: () => segment.PendingRestores.Remove(summary.StableId));
    }

    /// <summary>
    /// Builds the folded steps of an older exchange's collapsed rows, without opening them, so that
    /// something inside them can be found and revealed (a reply to a progress update, a search hit).
    /// Completes once they are built, or when the exchange is released first.
    /// </summary>
    public Task BuildFoldedStepsAsync(TranscriptHistorySegment segment)
    {
        if (segment.Turns is not { } turns || segment.Messages is not { } messages)
            return Task.CompletedTask;
        var deferred = turns
            .SelectMany(static turn => turn.Items)
            .OfType<TurnSummaryItem>()
            .Where(static summary => summary.HasDeferredItems)
            .ToList();
        if (deferred.Count == 0)
            return Task.CompletedTask;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StartBuild(segment, messages, segment.LaterRunSteps ?? [], segment.OmittedBefore, settled: false, isRestore: true, (_, rebuiltTurns) =>
        {
            var rebuilt = rebuiltTurns
                .SelectMany(static turn => turn.Items)
                .OfType<TurnSummaryItem>()
                .GroupBy(static summary => summary.StableId, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
            foreach (var summary in deferred)
            {
                if (summary.HasDeferredItems && rebuilt.TryGetValue(summary.StableId, out var twin))
                {
                    summary.RestoreDeferredItems(twin.InnerItems.ToList());
                    twin.InnerItems.Clear();
                }
            }

            completion.TrySetResult();
        }, abandoned: () => completion.TrySetResult());
        return completion.Task;
    }

    /// <summary>The built exchange holding a message, or null.</summary>
    public TranscriptHistorySegment? FindHydratedSegment(Guid messageId)
        => _segments.FirstOrDefault(segment => segment.Messages?.Any(message => message.Id == messageId) == true);

    /// <summary>An exchange's messages being built into turns, a slice at a time.</summary>
    private sealed class ExchangeBuild(
        TranscriptHistorySegment segment,
        List<ChatMessage> messages,
        List<ChatMessage> laterSteps,
        IReadOnlySet<long>? omittedBefore,
        TranscriptBuilder builder,
        Func<TranscriptBuilder> createBuilder,
        Action<ExchangeBuild, List<TranscriptTurn>> complete,
        int generation,
        bool isRestore,
        bool settled)
    {
        private int _next;
        private int _nextLater;
        private TranscriptBuilder? _continuation;

        public TranscriptHistorySegment Segment { get; } = segment;
        public List<ChatMessage> Messages { get; } = messages;
        public List<ChatMessage> LaterSteps { get; } = laterSteps;
        public IReadOnlySet<long>? OmittedBefore { get; } = omittedBefore;
        public TranscriptBuilder Builder { get; } = builder;
        public Action<ExchangeBuild, List<TranscriptTurn>> Complete { get; } = complete;
        public int Generation { get; } = generation;
        public bool IsRestore { get; } = isRestore;
        public bool Settled { get; } = settled;
        public long Priority { get; init; }
        public double BuildMs { get; set; }

        /// <summary>Called when the build is dropped before completing (the chat changed, the exchange was released).</summary>
        public Action? Abandoned { get; init; }

        /// <summary>Feeds messages to the builder until the slice's budget is spent. True once all were fed.</summary>
        public bool Advance(Stopwatch slice, double budgetMs)
        {
            while (_next < Messages.Count)
            {
                var message = Messages[_next++];
                if (OmittedBefore?.Contains(message.StorageSeq) == true)
                    Builder.NoteOmittedMessage();
                // Empty assistant messages (tool-only steps) are not shown, as in a full build.
                if (message.Role != "assistant" || !string.IsNullOrWhiteSpace(message.Content))
                    Builder.ProcessMessageToTranscript(new ChatMessageViewModel(message));
                if ((_next & 7) == 0 && slice.Elapsed.TotalMilliseconds >= budgetMs)
                    return _next >= Messages.Count && LaterSteps.Count == 0;
            }

            // Steps this exchange's runs took after it join their cards before the exchange folds, as
            // in a full build. A second builder takes them, so what else they showed — in the later
            // exchanges they belong to — is not shown here.
            if (_nextLater < LaterSteps.Count)
            {
                if (_continuation is null)
                {
                    _continuation = createBuilder();
                    _continuation.KeepsRunDetailInStorage = true;
                    _continuation.BeginRebuild(forkOrigin: null);
                    _continuation.ContinueRuns(Builder.RunsByToolCallId);
                }

                while (_nextLater < LaterSteps.Count)
                {
                    _continuation.ProcessMessageToTranscript(new ChatMessageViewModel(LaterSteps[_nextLater++]));
                    if ((_nextLater & 7) == 0 && slice.Elapsed.TotalMilliseconds >= budgetMs)
                        break;
                }

                if (_nextLater < LaterSteps.Count)
                    return false;
            }

            _continuation = null;
            return true;
        }
    }
    private void Dehydrate(TranscriptHistorySegment segment)
    {
        if (segment.Turns is not { } turns || _target is not { } target)
            return;

        var height = 0d;
        var measured = 0;
        var turnHeights = new Dictionary<string, double>(turns.Count, StringComparer.Ordinal);
        foreach (var turn in turns)
        {
            if (turn.MeasuredHeight > 0)
            {
                height += turn.MeasuredHeight;
                measured++;
                turnHeights[turn.StableId] = turn.MeasuredHeight;
            }

            foreach (var item in turn.Items)
            {
                if (item is TurnSummaryItem { IsExpanded: true } summary)
                    segment.ExpandedSummaries.Add(summary.StableId);
                else if (item is TurnSummaryItem collapsed)
                    segment.ExpandedSummaries.Remove(collapsed.StableId);
            }
        }

        if (measured == turns.Count && turns.Count > 0)
        {
            height += (turns.Count - 1) * TranscriptLayoutMetrics.TurnSpacing;
            segment.PlaceholderItem.Height = height;
            segment.Placeholder.MeasuredHeight = height;
        }

        segment.TurnHeights = turnHeights.Count > 0 ? turnHeights : null;

        var index = turns.Count > 0 ? target.IndexOf(turns[0]) : -1;
        if (index >= 0)
        {
            foreach (var turn in turns)
                target.Remove(turn);
            target.Insert(index, segment.Placeholder);
        }

        foreach (var turn in turns)
        {
            turn.ViewportActivityChanged = null;
            turn.HistorySegment = null;
        }

        if (_chat is not null && segment.Messages is { } messages)
            _dataStore.DetachHistoryMessages(_chat, messages);
        segment.Turns = null;
        segment.Messages = null;
        segment.OmittedBefore = null;
        segment.LaterRunSteps = null;
        segment.PendingRestores.Clear();
        TranscriptHistoryDiagnostics.RecordDehydration();
    }

    private void EnforceBudget(TranscriptHistorySegment? justBuilt)
    {
        while (HydratedMessageCount > MaxHydratedMessages || HydratedSegmentCount > MaxHydratedSegments)
        {
            var victim = _segments
                .Where(segment => segment.IsHydrated
                                  && segment.Turns!.Count > 0
                                  && !ReferenceEquals(segment, justBuilt)
                                  && !segment.IsVisible)
                .MinBy(static segment => segment.LastActiveTick);
            if (victim is null)
                break;
            Dehydrate(victim);
        }
    }

    private void RemoveFromTarget(TranscriptHistorySegment segment)
    {
        if (_target is not { } target)
            return;
        foreach (var turn in segment.CurrentTurns)
            target.Remove(turn);
    }

    private void Own(TranscriptTurn turn, TranscriptHistorySegment segment)
    {
        turn.HistorySegment = segment;
        turn.ViewportActivityChanged = OnTurnViewportActivity;
    }

    // ── Layout ──────────────────────────────────────────────────────────────

    private void BuildSegments(IReadOnlyList<ChatOutlineEntry> outline, long tailStartSeq)
    {
        var starts = new List<int>();
        for (var i = 0; i < outline.Count; i++)
        {
            if (outline[i].Role == "user")
                starts.Add(i);
        }

        // Conversation before the first user message (rare) is an exchange of its own.
        if (outline.Count > 0 && (starts.Count == 0 || starts[0] > 0))
            starts.Insert(0, 0);

        for (var s = 0; s < starts.Count; s++)
        {
            var first = starts[s];
            var last = s + 1 < starts.Count ? starts[s + 1] : outline.Count;
            var startSeq = s == 0 && outline[first].Role != "user" ? ChatStartSeq : outline[first].Seq;
            var endSeq = s + 1 < starts.Count ? outline[starts[s + 1]].Seq : tailStartSeq;
            var entries = outline.Skip(first).Take(last - first).ToList();
            var height = EstimateHeight(entries, startSeq == ChatStartSeq ? 1 : startSeq, endSeq);
            var key = $"history:{(startSeq == ChatStartSeq ? "start" : startSeq.ToString(System.Globalization.CultureInfo.InvariantCulture))}";
            var item = new HistoryPlaceholderItem(key, height);
            var placeholder = new TranscriptTurn("turn:" + key) { MeasuredHeight = height };
            placeholder.Items.Add(item);
            var segment = new TranscriptHistorySegment
            {
                StartSeq = startSeq,
                EndSeq = endSeq,
                ExchangeIndex = _facts.ExchangeIndexAt(startSeq),
                Placeholder = placeholder,
                PlaceholderItem = item,
                IsFirst = s == 0,
                StartsWithUserMessage = TranscriptBuilder.ShowsAsUserMessage(
                    outline[first].Role, outline[first].Author, outline[first].TextStart, outline[first].IsReceipt),
            };
            Own(placeholder, segment);
            _segments.Add(segment);
        }
    }

    /// <summary>
    /// A first guess at an exchange's height from its outline: the user's message, a "worked for" row
    /// when there was work between the conversation messages, and the final answer. Replaced by the
    /// measured height once the exchange has been shown.
    /// </summary>
    internal static double EstimateHeight(IReadOnlyList<ChatOutlineEntry> entries, long startSeq, long endSeq)
    {
        const double lineHeight = 22d;
        var height = 0d;
        var turns = 0;
        var user = entries.FirstOrDefault(static entry => entry.Role == "user");
        if (user.Role is not null)
        {
            height += 56 + Math.Min(12, Math.Ceiling(user.TextLength / 80d)) * lineHeight;
            turns++;
        }

        var assistants = entries.Where(static entry => entry.Role is "assistant" or "error").ToList();
        var hasWork = endSeq - startSeq > entries.Count || assistants.Count > 1;
        if (hasWork || assistants.Count > 0)
            turns++;
        if (hasWork)
            height += 48;
        if (assistants.Count > 0)
            height += 40 + Math.Min(160, Math.Ceiling(assistants[^1].TextLength / 90d)) * lineHeight;

        height += Math.Max(0, turns - 1) * TranscriptLayoutMetrics.TurnSpacing;
        return Math.Max(TranscriptLayoutMetrics.MinimumEstimatedTurnHeight, height);
    }
}

/// <summary>
/// Process-wide counters for history loading, read by diagnostics and harnesses. Set
/// <c>LUMI_TRANSCRIPT_DEBUG=1</c> to also print one line per built exchange.
/// </summary>
internal static class TranscriptHistoryDiagnostics
{
    private static readonly bool TraceToConsole =
        string.Equals(Environment.GetEnvironmentVariable("LUMI_TRANSCRIPT_DEBUG"), "1", StringComparison.Ordinal);

    private static readonly object Sync = new();

    public static int Hydrations { get; private set; }
    public static long HydratedMessages { get; private set; }
    public static double TotalBuildMs { get; private set; }
    public static double MaxBuildMs { get; private set; }
    public static double TotalUiMs { get; private set; }
    public static double MaxUiMs { get; private set; }
    public static int MaxMessages { get; private set; }
    public static int Dehydrations { get; private set; }

    public static void RecordHydration(int messages, double buildMs, double uiMs)
    {
        lock (Sync)
        {
            Hydrations++;
            HydratedMessages += messages;
            TotalBuildMs += buildMs;
            MaxBuildMs = Math.Max(MaxBuildMs, buildMs);
            TotalUiMs += uiMs;
            MaxUiMs = Math.Max(MaxUiMs, uiMs);
            MaxMessages = Math.Max(MaxMessages, messages);
        }

        if (TraceToConsole)
            Console.WriteLine($"[history] built exchange: {messages} msgs, build {buildMs:0.0} ms, ui {uiMs:0.0} ms");
    }

    public static void RecordDehydration()
    {
        lock (Sync)
            Dehydrations++;
    }

    public static string Describe()
    {
        lock (Sync)
        {
            return $"hydrations={Hydrations} dehydrations={Dehydrations} messages={HydratedMessages} " +
                   $"build total={TotalBuildMs:0} ms max={MaxBuildMs:0.0} ms ui max={MaxUiMs:0.0} ms largest={MaxMessages} msgs";
        }
    }
}

/// <summary>
/// Facts about a chat's stored history that later exchanges depend on: where each exchange starts,
/// which skills were already shown, and which files were announced (so a later edit of one shows as
/// an "edited" chip). Read once per chat from small indexes.
/// </summary>
internal sealed class TranscriptHistoryFacts
{
    public static TranscriptHistoryFacts Empty { get; } = new([], [], []);

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly long[] _userSeqs;
    private readonly List<(long Seq, string Name)> _skills;
    private readonly List<(long Seq, string Path)> _deliverables;

    private TranscriptHistoryFacts(long[] userSeqs, List<(long, string)> skills, List<(long, string)> deliverables)
    {
        _userSeqs = userSeqs;
        _skills = skills;
        _deliverables = deliverables;
    }

    public static TranscriptHistoryFacts Create(IReadOnlyList<ChatOutlineEntry> outline, IReadOnlyList<ChatMessage> factRows)
    {
        var userSeqs = outline.Where(static entry => entry.Role == "user").Select(static entry => entry.Seq).ToArray();
        var skills = new List<(long, string)>();
        var deliverables = new List<(long, string)>();
        foreach (var message in factRows)
            AddFacts(message, skills, deliverables);

        return new TranscriptHistoryFacts(userSeqs, skills, deliverables);
    }

    /// <summary>
    /// These facts and those of <paramref name="messages"/>, which joined the history after it: the
    /// same messages a reload reads them from (the skill and deliverable facets).
    /// </summary>
    public TranscriptHistoryFacts Extend(IReadOnlyList<ChatMessage> messages)
    {
        var userSeqs = _userSeqs
            .Concat(messages.Where(static message => message.Role == "user").Select(static message => message.StorageSeq))
            .ToArray();
        var skills = new List<(long, string)>(_skills);
        var deliverables = new List<(long, string)>(_deliverables);
        foreach (var message in messages)
        {
            if (ChatMessageKinds.Classify(message) is ChatMessageKinds.Skill or ChatMessageKinds.Deliverable)
                AddFacts(message, skills, deliverables);
        }

        return new TranscriptHistoryFacts(userSeqs, skills, deliverables);
    }

    /// <summary>These facts without those at or after <paramref name="seq"/> (the history was cut there).</summary>
    public TranscriptHistoryFacts Before(long seq)
        => new(
            _userSeqs.Where(userSeq => userSeq < seq).ToArray(),
            _skills.Where(skill => skill.Seq < seq).ToList(),
            _deliverables.Where(deliverable => deliverable.Seq < seq).ToList());

    private static void AddFacts(ChatMessage message, List<(long, string)> skills, List<(long, string)> deliverables)
    {
        foreach (var skill in message.ActiveSkills)
        {
            if (!string.IsNullOrWhiteSpace(skill.Name))
                skills.Add((message.StorageSeq, skill.Name));
        }

        if (message.ToolName == "fetch_skill"
            && ToolDisplayHelper.ExtractJsonField(message.Content, "name") is { Length: > 0 } fetched)
        {
            skills.Add((message.StorageSeq, fetched));
        }

        if (message.ToolName == "announce_file"
            && message.ToolStatus == "Completed"
            && ToolDisplayHelper.ExtractJsonField(message.Content, "filePath") is { Length: > 0 } announced
            && NormalizeAnnouncedPath(announced) is { } path)
        {
            deliverables.Add((message.StorageSeq, path));
        }
    }

    /// <summary>User messages strictly before <paramref name="seq"/>.</summary>
    public int ExchangeIndexAt(long seq)
    {
        var index = Array.BinarySearch(_userSeqs, seq);
        return index >= 0 ? index : ~index;
    }

    public TranscriptSeed SeedBefore(long seq)
    {
        var skills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (skillSeq, name) in _skills)
        {
            if (skillSeq < seq)
                skills.Add(name);
        }

        var files = new Dictionary<string, int>(PathComparer);
        foreach (var (fileSeq, path) in _deliverables)
        {
            if (fileSeq < seq)
                files.TryAdd(path, ExchangeIndexAt(fileSeq));
        }

        return new TranscriptSeed(ExchangeIndexAt(seq), skills, files);
    }

    private static string? NormalizeAnnouncedPath(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }
}
