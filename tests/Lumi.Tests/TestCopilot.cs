using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Services;

namespace Lumi.Tests;

/// <summary>
/// Process-wide <see cref="CopilotService"/> for tests that only need a service <em>reference</em> to
/// construct a <c>ChatViewModel</c>, <c>ChatSessionStore</c>, <c>MainViewModel</c>, etc.
///
/// <para><b>Why this exists.</b> <see cref="CopilotService"/> is <see cref="IAsyncDisposable"/> and owns a real
/// <c>copilot.exe</c> child process, spawned lazily the first time a code path reaches
/// <c>ConnectAsync</c> (e.g. <c>ChatViewModel.LoadChatAsync</c>). Tests used to construct it inline —
/// <c>new ChatViewModel(dataStore, new CopilotService())</c> — which leaves the service with no owner,
/// so nothing ever disposed it. Every test that connected stranded another CLI process for the rest of
/// the run: a full suite peaked at 41 concurrent <c>copilot.exe</c> processes, and running several
/// worktrees at once saturated the machine and stalled unrelated headless UI suites.</para>
///
/// <para><b>Why sharing is safe.</b> <c>ChatViewModel</c> does not own the service, and its <c>Dispose</c>
/// detaches from <see cref="CopilotService.Reconnected"/> and
/// <see cref="CopilotService.SessionDeletedRemotely"/>, so a disposed surface leaves no residue on the
/// shared instance. Per-chat state lives on the surface, not the service.</para>
///
/// <para><b>When NOT to use this.</b> Tests that own connection state — connecting, forcing a reconnect,
/// or exercising authentication — must keep creating their own instance and dispose it
/// (<c>CopilotIntegrationTests</c>, <c>SuggestionAuditHarness</c>).</para>
/// </summary>
public static class TestCopilot
{
    private static readonly Lazy<CopilotService> LazyShared = new(
        static () => new CopilotService(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The shared service handed to test surfaces that just need a non-null CopilotService.</summary>
    public static CopilotService Shared => LazyShared.Value;

    // Use only in the non-parallel Headless UI collection. No CLI process is started.
    internal sealed class SimulatedConnection : IAsyncDisposable
    {
        private static readonly FieldInfo ClientField = Field("_client");
        private static readonly FieldInfo StateField = Field("_state");
        private static readonly FieldInfo GenerationField = Field("_connectionGeneration");
        private readonly object? _previousClient = ClientField.GetValue(Shared);
        private readonly object? _previousState = StateField.GetValue(Shared);
        private readonly object? _previousGeneration = GenerationField.GetValue(Shared);
        private readonly CopilotClient _client = new(new CopilotClientOptions
        {
            Connection = RuntimeConnection.ForUri("127.0.0.1:1")
        });

        public SimulatedConnection()
        {
            ClientField.SetValue(Shared, _client);
            SetState(ConnectionState.Connected);
        }

        public void SetState(ConnectionState state) => StateField.SetValue(Shared, state);

        public void AdvanceGeneration()
            => GenerationField.SetValue(Shared, Shared.ConnectionGeneration + 1);

        public async ValueTask DisposeAsync()
        {
            ClientField.SetValue(Shared, _previousClient);
            StateField.SetValue(Shared, _previousState);
            GenerationField.SetValue(Shared, _previousGeneration);
            await _client.DisposeAsync();
        }

        private static FieldInfo Field(string name)
            => typeof(CopilotService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    }

    [ModuleInitializer]
    internal static void RegisterShutdown() =>
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => Shutdown();

    private static void Shutdown()
    {
        if (!LazyShared.IsValueCreated)
            return;

        // Best effort only: ProcessExit runs on a limited budget, and a wedged CLI must not hang the
        // test host. Anything still alive dies with the process anyway.
        try { LazyShared.Value.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); }
        catch { /* teardown is advisory */ }
    }
}
