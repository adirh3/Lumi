using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Lumi.Services;

/// <summary>A downloadable model file, pinned by repository revision, size and SHA-256.</summary>
/// <param name="TrimAudioWindow">
/// Whether decoding looks only at as much audio as each phrase holds instead of Whisper's full 30 second window: four
/// times faster on a processor, with the same accuracy for this model.
/// </param>
internal sealed record VoiceModel(
    string FileName, long Size, string Sha256, bool TrimAudioWindow = false,
    string Repository = "ggerganov/whisper.cpp", string Revision = "5359861c739e955e79d9a303bcbc70fb988958b1")
{
    /// <summary>
    /// Whisper large-v3-turbo (Q5_0), for computers whose GPU runs it in a fraction of a second. Its audio window stays
    /// whole: trimming it costs this model a lot of accuracy.
    /// </summary>
    public static VoiceModel Turbo { get; } = new(
        "ggml-large-v3-turbo-q5_0.bin", 574_041_195,
        "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2", TrimAudioWindow: false);

    /// <summary>Whisper small (Q5_1), fast enough on a processor alone.</summary>
    public static VoiceModel Small { get; } = new(
        "ggml-small-q5_1.bin", 190_085_487,
        "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb", TrimAudioWindow: true);

    /// <summary>
    /// Silero VAD (v6.2.0), which tells speech from other sound in about two milliseconds per second of audio. Whisper
    /// only hears what it calls speech: given a fan or a click, Whisper invents words such as "Thank you."
    /// </summary>
    public static VoiceModel SpeechGate { get; } = new(
        "ggml-silero-v6.2.0.bin", 885_098,
        "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987",
        Repository: "ggml-org/whisper-vad", Revision: "9ffd54a1e1ee413ddf265af9913beaf518d1639b");

    public Uri Source => new($"https://huggingface.co/{Repository}/resolve/{Revision}/{FileName}");
}

/// <summary>One file of a speech model, with where it comes from and what it must be.</summary>
internal sealed record VoiceModelFile(string Path, Func<CancellationToken, Task<Stream>> Download, long Size, string? Sha256);

/// <summary>
/// An on-device speech model and the speech gate in front of it: where they live, and their one-time download, checked
/// against pinned sizes and SHA-256 before anything loads them. Nothing is downloaded until the user agrees to set voice
/// typing up.
/// </summary>
internal sealed class VoiceModelStore
{
    private const int ProgressStep = 256 * 1024;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly Lazy<Task<VoiceModelStore>> RecommendedModel = new(() => Task.Run(ChooseModel));

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly VoiceModelFile[] _files;

    internal VoiceModelStore(
        string modelPath, Func<CancellationToken, Task<Stream>> download, long downloadSize, string? sha256,
        bool trimAudioWindow = false, VoiceModelFile? speechGate = null)
    {
        var model = new VoiceModelFile(modelPath, download, downloadSize, sha256);
        _files = speechGate is null ? [model] : [speechGate, model];
        ModelPath = modelPath;
        SpeechGatePath = speechGate?.Path;
        DownloadSize = _files.Sum(file => file.Size);
        TrimAudioWindow = trimAudioWindow;
    }

    public string ModelPath { get; }

    /// <summary>The speech gate's file, or null when everything heard is transcribed.</summary>
    public string? SpeechGatePath { get; }

    /// <summary>The size of the one-time download, in bytes.</summary>
    public long DownloadSize { get; }

    /// <inheritdoc cref="VoiceModel.TrimAudioWindow"/>
    public bool TrimAudioWindow { get; }

    public bool IsInstalled => _files.All(file => File.Exists(file.Path));

    /// <summary>
    /// The model for this computer: large-v3-turbo where a GPU runs Whisper, small otherwise. A model already downloaded
    /// is kept rather than asking to set voice typing up again, and needs no look at the GPU. Chosen once, off the UI
    /// thread, since finding the GPU loads the native runtime.
    /// </summary>
    public static Task<VoiceModelStore> RecommendedAsync() => RecommendedModel.Value;

    internal static VoiceModelStore For(VoiceModel model, string directory)
        => new(Path.Combine(directory, model.FileName), token => DownloadAsync(model.Source, token), model.Size, model.Sha256,
            model.TrimAudioWindow, FileFor(VoiceModel.SpeechGate, directory));

    private static VoiceModelFile FileFor(VoiceModel model, string directory)
        => new(Path.Combine(directory, model.FileName), token => DownloadAsync(model.Source, token), model.Size, model.Sha256);

    private static VoiceModelStore ChooseModel()
    {
        var directory = Path.Combine(DataStore.AppDirectory, "voice");
        var turbo = For(VoiceModel.Turbo, directory);
        var small = For(VoiceModel.Small, directory);
        var hasTurbo = File.Exists(turbo.ModelPath);
        if (hasTurbo != File.Exists(small.ModelPath))
            return hasTurbo ? turbo : small;
        return VoiceHardware.HasGpu() ? turbo : small;
    }

    /// <summary>
    /// Downloads and installs whatever is missing, reporting the bytes received. Concurrent callers share one download;
    /// a cancelled, stalled or damaged download leaves nothing behind.
    /// </summary>
    public async Task DownloadAsync(IProgress<long>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            long done = 0;
            foreach (var file in _files)
            {
                if (!File.Exists(file.Path))
                    await DownloadFileAsync(file, done, progress, cancellationToken);
                done += file.Size;
            }
            progress?.Report(done);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Removes a model that failed to load, so the next attempt sets voice typing up again.</summary>
    public void Discard()
    {
        foreach (var file in _files)
        {
            try { File.Delete(file.Path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task DownloadFileAsync(
        VoiceModelFile file, long before, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
        // One partial file per model, so a download cut short by quitting Lumi is replaced by the next attempt.
        var partialPath = file.Path + ".download";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var received = await CopyAsync(file, partialPath, hash, before, progress, cancellationToken);
            if (received != file.Size
                || file.Sha256 is not null && !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), file.Sha256, StringComparison.Ordinal))
                throw new VoiceInputException(VoiceInputErrorKind.Download, "The downloaded voice model was incomplete or damaged.");

            File.Move(partialPath, file.Path);
        }
        finally
        {
            if (File.Exists(partialPath))
                File.Delete(partialPath);
        }
    }

    private static async Task<long> CopyAsync(
        VoiceModelFile file, string path, IncrementalHash hash, long before, IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            await using var source = await file.Download(cancellationToken);
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            long received = 0;
            long reported = 0;
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                int read;
                try { read = await source.ReadAsync(buffer, stall.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new VoiceInputException(VoiceInputErrorKind.Download, "The download stopped receiving data.");
                }

                if (read == 0)
                    break;
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                if (received - reported >= ProgressStep)
                {
                    reported = received;
                    progress?.Report(before + received);
                }
            }

            await target.FlushAsync(cancellationToken);
            return received;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static async Task<Stream> DownloadAsync(Uri source, CancellationToken cancellationToken)
    {
        var response = await Http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }
}

/// <summary>
/// Whether Whisper runs on a strong GPU here: Metal on Apple silicon, or a dedicated Vulkan GPU on Windows and Linux.
/// </summary>
internal static class VoiceHardware
{
    // ggml's device kinds: 0 the processor, 1 a dedicated GPU, 2 an integrated one. Integrated GPUs vary too much in
    // speed to count on large-v3-turbo keeping up with live dictation, so they get the smaller model.
    private const int GpuDevice = 1;

    public static bool HasGpu()
    {
        UseRunnableRuntimes();
        if (OperatingSystem.IsMacOS())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        try
        {
            // Loads Whisper.net's preferred runtime: Vulkan where a Vulkan driver is installed, the CPU one otherwise.
            WhisperFactory.GetRuntimeInfo();
            return RuntimeOptions.LoadedLibrary == RuntimeLibrary.Vulkan && CountGpus() > 0;
        }
        catch (Exception ex)
        {
            // Without a usable GPU runtime, the processor's model is the right one.
            Debug.WriteLine($"[Voice] GPU detection failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Keeps Whisper.net from loading a runtime this processor can't run, before anything uses Whisper. It tries its
    /// Vulkan runtime before its processor ones but, unlike them, never checks the processor: that runtime is built for
    /// AVX2 and FMA, and on an older or low-end processor without them, loading it ends the app.
    /// </summary>
    public static void UseRunnableRuntimes()
        => RuntimeOptions.RuntimeLibraryOrder = RunnableRuntimes(RuntimeOptions.RuntimeLibraryOrder,
            RuntimeInformation.ProcessArchitecture != Architecture.X64 || Avx.IsSupported && Avx2.IsSupported && Fma.IsSupported);

    internal static List<RuntimeLibrary> RunnableRuntimes(IEnumerable<RuntimeLibrary> order, bool processorRunsVulkanRuntime)
        => [.. order.Where(runtime => processorRunsVulkanRuntime || runtime != RuntimeLibrary.Vulkan)];

    /// <summary>
    /// Counts the GPUs ggml found, asking the copies Whisper.net loaded: the device list lives in ggml-whisper, each
    /// device's kind in ggml-base-whisper. A Vulkan driver without a usable GPU leaves only the processor.
    /// </summary>
    private static unsafe int CountGpus()
    {
        var registry = NativeLibrary.Load(LoadedModule("ggml-whisper"));
        var devices = NativeLibrary.Load(LoadedModule("ggml-base-whisper"));
        var count = (delegate* unmanaged[Cdecl]<nuint>)NativeLibrary.GetExport(registry, "ggml_backend_dev_count");
        var get = (delegate* unmanaged[Cdecl]<nuint, nint>)NativeLibrary.GetExport(registry, "ggml_backend_dev_get");
        var type = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(devices, "ggml_backend_dev_type");
        var gpus = 0;
        for (nuint index = 0, total = count(); index < total; index++)
        {
            if (type(get(index)) == GpuDevice)
                gpus++;
        }
        return gpus;
    }

    private static string LoadedModule(string name)
    {
        var fileName = OperatingSystem.IsWindows() ? $"{name}.dll" : $"lib{name}.so";
        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
        {
            if (string.Equals(module.ModuleName, fileName, StringComparison.OrdinalIgnoreCase))
                return module.FileName;
        }
        throw new FileNotFoundException($"{fileName} is not loaded.");
    }
}
