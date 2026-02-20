using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Media.SpeechRecognition;

namespace Lumi.Services;

/// <summary>
/// Provides push-to-talk voice input using the platform speech recognizer.
/// Windows-only via WinRT SpeechRecognizer; no-ops on other platforms.
/// </summary>
internal sealed class VoiceInputService : IDisposable
{
    /// <summary>Fired on the recognizer thread with partial (hypothesis) text.</summary>
    public event Action<string>? HypothesisGenerated;

    /// <summary>Fired on the recognizer thread with final recognized text for a segment.</summary>
    public event Action<string>? ResultGenerated;

    /// <summary>Fired when recognition stops (success, error, or user-initiated).</summary>
    public event Action? Stopped;

    /// <summary>Fired when starting fails, with the error message.</summary>
    public event Action<string>? Error;

    public bool IsRecording { get; private set; }
    public bool IsAvailable { get; } = OperatingSystem.IsWindows();

    private SpeechRecognizer? _recognizer;
    private static bool _dispatcherQueueCreated;

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(
        ref DispatcherQueueOptions options,
        out nint dispatcherQueueController);

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int DwSize;
        public int ThreadType;
        public int ApartmentType;
    }

    private static void EnsureDispatcherQueue()
    {
        if (_dispatcherQueueCreated) return;

        var options = new DispatcherQueueOptions
        {
            DwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            ThreadType = 2,   // DQTYPE_THREAD_CURRENT
            ApartmentType = 0 // DQTAT_COM_NONE
        };
        CreateDispatcherQueueController(ref options, out _);
        _dispatcherQueueCreated = true;
    }

    public async Task StartAsync(string language = "")
    {
        if (IsRecording || !IsAvailable) return;

        try
        {
            EnsureDispatcherQueue();
            _recognizer?.Dispose();

            if (!string.IsNullOrEmpty(language))
                _recognizer = new SpeechRecognizer(new Windows.Globalization.Language(language));
            else
                _recognizer = new SpeechRecognizer();

            var dictation = new SpeechRecognitionTopicConstraint(
                SpeechRecognitionScenario.Dictation, "dictation");
            _recognizer.Constraints.Add(dictation);

            var compileResult = await _recognizer.CompileConstraintsAsync();
            if (compileResult.Status != SpeechRecognitionResultStatus.Success)
            {
                Cleanup();
                Error?.Invoke($"Speech compile failed: {compileResult.Status}");
                Stopped?.Invoke();
                return;
            }

            _recognizer.ContinuousRecognitionSession.ResultGenerated += (_, args) =>
            {
                if (args.Result.Status == SpeechRecognitionResultStatus.Success
                    && !string.IsNullOrWhiteSpace(args.Result.Text))
                    ResultGenerated?.Invoke(args.Result.Text);
            };

            _recognizer.HypothesisGenerated += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Hypothesis.Text))
                    HypothesisGenerated?.Invoke(args.Hypothesis.Text);
            };

            _recognizer.ContinuousRecognitionSession.Completed += (_, _) =>
            {
                IsRecording = false;
                Stopped?.Invoke();
            };

            await _recognizer.ContinuousRecognitionSession.StartAsync();
            IsRecording = true;
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80045509))
        {
            // Speech privacy policy not accepted — open Windows speech settings
            IsRecording = false;
            Cleanup();
            try { Process.Start(new ProcessStartInfo("ms-settings:privacy-speech") { UseShellExecute = true }); }
            catch { /* best effort */ }
            Error?.Invoke("speech_privacy");
            Stopped?.Invoke();
        }
        catch (Exception ex)
        {
            IsRecording = false;
            Cleanup();
            Error?.Invoke(ex.Message);
            Stopped?.Invoke();
        }
    }

    public async Task StopAsync()
    {
        if (!IsRecording) return;

        try
        {
            if (_recognizer is not null)
                await _recognizer.ContinuousRecognitionSession.StopAsync();
        }
        catch { /* Already stopped */ }
        finally
        {
            IsRecording = false;
            Cleanup();
        }
    }

    private void Cleanup()
    {
        _recognizer?.Dispose();
        _recognizer = null;
    }

    public void Dispose()
    {
        if (IsRecording)
        {
            try { _recognizer?.ContinuousRecognitionSession.StopAsync().AsTask().GetAwaiter().GetResult(); }
            catch { /* best effort */ }
        }
        Cleanup();
    }
}
