using System.Security.Cryptography;
using Lumi.Services;
using Whisper.net.LibraryLoader;
using Xunit;

namespace Lumi.Tests;

public sealed class VoiceModelStoreTests : IDisposable
{
    private static readonly byte[] Model = [1, 2, 3, 4];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"Lumi-voice-test-{Guid.NewGuid():N}");
    private string ModelPath => Path.Combine(_directory, "model.bin");

    [Fact]
    public async Task OneDownloadIsKeptAndLaterUseIsOffline()
    {
        var downloads = 0;
        var store = Store(_ =>
        {
            downloads++;
            return Task.FromResult<Stream>(new MemoryStream(Model));
        });

        Assert.False(store.IsInstalled);
        await store.DownloadAsync(null, default);
        await store.DownloadAsync(null, default);
        Assert.Equal(1, downloads);
        Assert.True(store.IsInstalled);
        Assert.Equal(Model, await File.ReadAllBytesAsync(ModelPath));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task ProgressReportsTheBytesReceived()
    {
        var reports = new List<long>();
        var store = Store(_ => Task.FromResult<Stream>(new MemoryStream(Model)));
        await store.DownloadAsync(new SynchronousProgress(reports.Add), default);
        Assert.Equal(Model.Length, reports.Last());
    }

    [Fact]
    public async Task AnInterruptedDownloadLeavesNothingBehindAndCanBeRetried()
    {
        using var cancellation = new CancellationTokenSource();
        var store = Store(_ =>
        {
            cancellation.Cancel();
            return Task.FromResult<Stream>(new MemoryStream(Model));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DownloadAsync(null, cancellation.Token));
        Assert.False(store.IsInstalled);
        Assert.Empty(Directory.GetFiles(_directory));

        await Store(_ => Task.FromResult<Stream>(new MemoryStream(Model))).DownloadAsync(null, default);
        Assert.Equal(Model, await File.ReadAllBytesAsync(ModelPath));
    }

    [Fact]
    public async Task APartialDownloadLeftByAnEarlierRunIsReplaced()
    {
        Directory.CreateDirectory(_directory);
        // Lumi quit in the middle of a download, before it could clean up.
        await File.WriteAllBytesAsync(ModelPath + ".download", [9, 9, 9, 9, 9, 9, 9, 9]);
        await Store(_ => Task.FromResult<Stream>(new MemoryStream(Model))).DownloadAsync(null, default);
        Assert.Equal(Model, await File.ReadAllBytesAsync(ModelPath));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task ConcurrentRequestsShareOneDownload()
    {
        var downloads = 0;
        var store = Store(_ =>
        {
            Interlocked.Increment(ref downloads);
            return Task.FromResult<Stream>(new MemoryStream(Model));
        });
        await Task.WhenAll(store.DownloadAsync(null, default), store.DownloadAsync(null, default));
        Assert.Equal(1, downloads);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3, 5 })]
    [InlineData(new byte[] { 1, 2, 3 })]
    public async Task ADamagedOrIncompleteDownloadIsNeverInstalled(byte[] received)
    {
        var store = new VoiceModelStore(
            ModelPath, _ => Task.FromResult<Stream>(new MemoryStream(received)), Model.Length,
            Convert.ToHexStringLower(SHA256.HashData(Model)));

        var error = await Assert.ThrowsAsync<VoiceInputException>(() => store.DownloadAsync(null, default));
        Assert.Equal(VoiceInputErrorKind.Download, error.Kind);
        Assert.False(store.IsInstalled);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task TheVerifiedModelIsInstalled()
    {
        var store = new VoiceModelStore(
            ModelPath, _ => Task.FromResult<Stream>(new MemoryStream(Model)), Model.Length,
            Convert.ToHexStringLower(SHA256.HashData(Model)));
        await store.DownloadAsync(null, default);
        Assert.True(store.IsInstalled);
    }

    [Fact]
    public async Task DiscardingRemovesTheModelSoSetupRunsAgain()
    {
        var store = Store(_ => Task.FromResult<Stream>(new MemoryStream(Model)));
        await store.DownloadAsync(null, default);
        store.Discard();
        Assert.False(store.IsInstalled);
        store.Discard();
    }

    [Fact]
    public void TheSourcesArePinnedToTheKnownModels()
    {
        foreach (var model in new[] { VoiceModel.Turbo, VoiceModel.Small })
        {
            var store = VoiceModelStore.For(model, _directory);
            Assert.Equal(Path.Combine(_directory, model.FileName), store.ModelPath);
            Assert.Equal(Path.Combine(_directory, VoiceModel.SpeechGate.FileName), store.SpeechGatePath);
            Assert.Equal(model.Size + VoiceModel.SpeechGate.Size, store.DownloadSize);
            Assert.Equal(model.TrimAudioWindow, store.TrimAudioWindow);
            Assert.Equal(64, model.Sha256.Length);
            Assert.Matches("^https://huggingface.co/ggerganov/whisper.cpp/resolve/[0-9a-f]{40}/", model.Source.AbsoluteUri);
            Assert.EndsWith(model.FileName, model.Source.AbsoluteUri, StringComparison.Ordinal);
        }

        Assert.Equal(574_041_195, VoiceModel.Turbo.Size);
        Assert.False(VoiceModel.Turbo.TrimAudioWindow);
        Assert.Equal(190_085_487, VoiceModel.Small.Size);
        Assert.True(VoiceModel.Small.TrimAudioWindow);
        Assert.Equal(64, VoiceModel.SpeechGate.Sha256.Length);
        Assert.Matches("^https://huggingface.co/ggml-org/whisper-vad/resolve/[0-9a-f]{40}/ggml-silero-", VoiceModel.SpeechGate.Source.AbsoluteUri);
    }

    [Fact]
    public async Task TheSpeechGateComesWithTheModelInOneDownload()
    {
        Directory.CreateDirectory(_directory);
        byte[] gate = [9, 8, 7];
        var gatePath = Path.Combine(_directory, "gate.bin");
        var gateDownloads = 0;
        var store = new VoiceModelStore(
            ModelPath, _ => Task.FromResult<Stream>(new MemoryStream(Model)), Model.Length, sha256: null,
            speechGate: new VoiceModelFile(gatePath, _ =>
            {
                gateDownloads++;
                return Task.FromResult<Stream>(new MemoryStream(gate));
            }, gate.Length, Convert.ToHexStringLower(SHA256.HashData(gate))));
        var reports = new List<long>();

        Assert.Equal(Model.Length + gate.Length, store.DownloadSize);
        await store.DownloadAsync(new SynchronousProgress(reports.Add), default);
        Assert.True(store.IsInstalled);
        Assert.Equal(gate, await File.ReadAllBytesAsync(gatePath));
        Assert.Equal(Model.Length + gate.Length, reports.Last());

        // Either file missing means setting up again, which fetches only what is missing.
        File.Delete(gatePath);
        Assert.False(store.IsInstalled);
        await store.DownloadAsync(null, default);
        Assert.Equal(2, gateDownloads);
        Assert.True(store.IsInstalled);

        store.Discard();
        Assert.False(File.Exists(ModelPath));
        Assert.False(File.Exists(gatePath));
    }

    [Fact]
    public void TheGpuRuntimeIsSkippedOnProcessorsItCannotRun()
    {
        List<RuntimeLibrary> order = [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];

        Assert.Equal(order, VoiceHardware.RunnableRuntimes(order, processorRunsVulkanRuntime: true));
        Assert.Equal([RuntimeLibrary.Cuda, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            VoiceHardware.RunnableRuntimes(order, processorRunsVulkanRuntime: false));
    }

    private VoiceModelStore Store(Func<CancellationToken, Task<Stream>> download)
    {
        Directory.CreateDirectory(_directory);
        return new VoiceModelStore(ModelPath, download, Model.Length, sha256: null);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class SynchronousProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
