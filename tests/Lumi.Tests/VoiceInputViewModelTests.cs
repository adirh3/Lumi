using System.Reflection;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class VoiceInputViewModelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task FirstUseAsksBeforeDownloadingAndNotNowKeepsTheMicrophoneClosed()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var backend = new FakeVoiceInputBackend();
            var downloads = 0;
            using var voice = new VoiceInputService(() => backend, TestVoiceModels.Missing(_ =>
            {
                downloads++;
                return Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4]));
            }));
            using var vm = CreateViewModel(voice);

            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            Assert.True(vm.IsVoiceSetupVisible);
            Assert.False(vm.IsRecording);
            Assert.Equal(string.Format(Loc.Voice_SetupBody, 0), vm.VoiceSetupMessage);
            vm.DismissVoiceCommand.Execute(null);
            Assert.False(vm.IsVoiceSetupVisible);

            // The microphone asks again next time, and pressing it once more puts the question away.
            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            Assert.True(vm.IsVoiceSetupVisible);
            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            Assert.False(vm.IsVoiceSetupVisible);
            Assert.Equal(0, downloads);
            Assert.Equal(0, backend.StartCount);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AgreeingShowsTheDownloadThenListensWithoutAnotherClick()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var backend = new FakeVoiceInputBackend();
            var release = VoiceInputServiceTests.Signal();
            var model = TestVoiceModels.Missing(async token =>
            {
                await release.Task.WaitAsync(token);
                return new MemoryStream([1, 2, 3, 4]);
            });
            using var voice = new VoiceInputService(() => backend, model);
            using var vm = CreateViewModel(voice);

            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            vm.DownloadVoiceModelCommand.Execute(null);
            Assert.True(vm.IsVoiceDownloadVisible);
            Assert.False(vm.IsVoiceSetupVisible);
            Assert.Equal(0, vm.VoiceDownloadPercent);

            release.TrySetResult();
            await WaitUntil(() => vm.IsRecording, "listening should start once the model is ready");
            Assert.False(vm.IsVoiceDownloadVisible);
            Assert.Equal(100, vm.VoiceDownloadPercent);
            Assert.True(model.IsInstalled);
            Assert.Equal(1, backend.StartCount);
            await voice.CancelAsync();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PuttingTheProgressAwayKeepsDownloadingWithoutListeningByItself()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var backend = new FakeVoiceInputBackend();
            var release = VoiceInputServiceTests.Signal();
            var model = TestVoiceModels.Missing(async token =>
            {
                await release.Task.WaitAsync(token);
                return new MemoryStream([1, 2, 3, 4]);
            });
            using var voice = new VoiceInputService(() => backend, model);
            using var vm = CreateViewModel(voice);

            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            vm.DownloadVoiceModelCommand.Execute(null);
            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            Assert.False(vm.IsVoiceDownloadVisible);

            release.TrySetResult();
            await WaitUntil(() => model.IsInstalled, "the download should carry on");
            await FlushVoiceEvents();
            Assert.False(vm.IsRecording);
            Assert.Equal(0, backend.StartCount);

            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            await FlushVoiceEvents();
            Assert.True(vm.IsRecording);
            await voice.CancelAsync();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CancellingTheDownloadInstallsNothing()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var cancelled = VoiceInputServiceTests.Signal();
            var model = TestVoiceModels.Missing(async token =>
            {
                try { await Task.Delay(System.Threading.Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                return Stream.Null;
            });
            using var voice = new VoiceInputService(() => new FakeVoiceInputBackend(), model);
            using var vm = CreateViewModel(voice);

            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            vm.DownloadVoiceModelCommand.Execute(null);
            vm.DismissVoiceCommand.Execute(null);
            await cancelled.Task.WaitAsync(Timeout);
            await FlushVoiceEvents();

            Assert.False(vm.IsVoiceDownloadVisible);
            Assert.False(vm.IsVoiceProblemVisible);
            Assert.False(vm.IsRecording);
            Assert.False(model.IsInstalled);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ADownloadThatFailsOffersToTryAgainAndThenListens()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var attempts = 0;
            var model = TestVoiceModels.Missing(_ => ++attempts == 1
                ? throw new HttpRequestException("No such host is known.")
                : Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4])));
            var backend = new FakeVoiceInputBackend();
            using var voice = new VoiceInputService(() => backend, model);
            using var vm = CreateViewModel(voice);

            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            vm.DownloadVoiceModelCommand.Execute(null);
            await WaitUntil(() => vm.IsVoiceProblemVisible, "a failed download should explain itself");
            Assert.Equal(Loc.Voice_DownloadFailedTitle, vm.VoiceProblemTitle);
            Assert.Contains("No such host", vm.VoiceProblemDetail);
            Assert.False(vm.CanOpenMicrophoneSettings);

            await vm.RetryVoiceCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.IsRecording, "trying again should download and listen");
            Assert.Equal(2, attempts);
            await voice.CancelAsync();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DictationAppendsToTheLatestDraftInsteadOfOverwritingTyping()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var backend = new FakeVoiceInputBackend { TranscribeGate = VoiceInputServiceTests.Signal() };
            using var voice = new VoiceInputService(() => backend, TestVoiceModels.Installed());
            using var vm = CreateViewModel(voice);
            vm.PromptText = "initial draft";
            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            backend.Emit(VoiceAudioBufferTests.Said(3200));
            backend.Emit(VoiceAudioBufferTests.Pause());
            await backend.Transcribing.Task.WaitAsync(Timeout);
            vm.PromptText = "draft edited while speaking";
            backend.TranscribeGate!.TrySetResult();
            await voice.StopAsync();
            await FlushVoiceEvents();

            Assert.Equal("draft edited while speaking voice phrase", vm.PromptText);
            Assert.False(vm.IsRecording);
            Assert.False(vm.IsVoiceProblemVisible);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ListeningShowsTheLevelAndTheWordsHeardSoFar()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var backend = new FakeVoiceInputBackend { Preview = "voice phr" };
            using var voice = new VoiceInputService(() => backend, TestVoiceModels.Installed());
            using var vm = CreateViewModel(voice);

            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            await FlushVoiceEvents();
            Assert.True(vm.IsRecording);
            Assert.Equal("", vm.VoiceStatus);
            Assert.Equal(Loc.Voice_Placeholder, vm.ComposerPlaceholder);

            backend.Emit(VoiceAudioBufferTests.Said(16000));
            await FlushVoiceEvents();
            Assert.InRange(vm.VoiceLevel, 0.5, 1);
            await WaitUntil(() => vm.VoicePreview == "voice phr", "the words heard so far should show");

            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            Assert.Equal("voice phrase", vm.PromptText);
            Assert.Equal("", vm.VoicePreview);
            Assert.Equal(0, vm.VoiceLevel);
            Assert.False(vm.IsRecording);
            Assert.Equal(Loc.Chat_Placeholder, vm.ComposerPlaceholder);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DiscardingPutsTheDraftBackUnlessItWasEditedSince()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var backends = new List<FakeVoiceInputBackend>();
            using var voice = new VoiceInputService(() =>
            {
                var backend = new FakeVoiceInputBackend();
                backends.Add(backend);
                return backend;
            }, TestVoiceModels.Installed());
            using var vm = CreateViewModel(voice);
            vm.PromptText = "Before";

            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            backends[^1].Emit(VoiceAudioBufferTests.Said(3200));
            backends[^1].Emit(VoiceAudioBufferTests.Pause());
            await WaitUntil(() => vm.PromptText == "Before voice phrase", "the phrase should land");
            vm.DiscardVoiceCommand.Execute(null);
            Assert.Equal("Before", vm.PromptText);
            Assert.False(vm.IsRecording);

            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            backends[^1].Emit(VoiceAudioBufferTests.Said(3200));
            backends[^1].Emit(VoiceAudioBufferTests.Pause());
            await WaitUntil(() => vm.PromptText == "Before voice phrase", "the phrase should land again");
            vm.PromptText = "Before voice phrase, edited";
            vm.DiscardVoiceCommand.Execute(null);
            Assert.Equal("Before voice phrase, edited", vm.PromptText);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SendingWhileDictatingTakesTheWordsStillBeingHeard()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var backend = new FakeVoiceInputBackend();
            using var voice = new VoiceInputService(() => backend, TestVoiceModels.Installed());
            var chat = new Chat { IsPaused = true };
            using var vm = CreateViewModel(voice, chat);
            vm.CurrentChat = chat;

            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            backend.Emit(VoiceAudioBufferTests.Said(3200));
            await vm.SendMessageCommand.ExecuteAsync(null);

            Assert.Equal("voice phrase", Assert.Single(chat.Messages).Content);
            Assert.Equal("", vm.PromptText);
            Assert.False(vm.IsRecording);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MovingToAnotherChatWhileTheLastWordsFinishSendsNothing()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var backend = new FakeVoiceInputBackend { TranscribeGate = VoiceInputServiceTests.Signal() };
            using var voice = new VoiceInputService(() => backend, TestVoiceModels.Installed());
            var first = new Chat { IsPaused = true };
            var second = new Chat { IsPaused = true };
            using var vm = CreateViewModel(voice, first, second);
            vm.CurrentChat = first;

            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            backend.Emit(VoiceAudioBufferTests.Said(3200));
            var sending = vm.SendMessageCommand.ExecuteAsync(null);
            await backend.Transcribing.Task.WaitAsync(Timeout);
            vm.CurrentChat = second;
            await sending.WaitAsync(Timeout);

            Assert.Empty(first.Messages);
            Assert.Empty(second.Messages);
            Assert.False(vm.IsRecording);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task FinishingARestartBeforeTheMicrophoneIsFreeLeavesItClosed()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var first = new FakeVoiceInputBackend
            {
                TranscribeGate = VoiceInputServiceTests.Signal(),
                IgnoreTranscriptionCancellation = true,
            };
            var attempts = 0;
            using var voice = new VoiceInputService(
                () => ++attempts == 1 ? first : new FakeVoiceInputBackend(), TestVoiceModels.Installed());
            using var vm = CreateViewModel(voice);
            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            first.Emit(VoiceAudioBufferTests.Said(3200));
            first.Emit(VoiceAudioBufferTests.Pause());
            await first.Transcribing.Task.WaitAsync(Timeout);
            vm.DiscardVoiceCommand.Execute(null);

            // The new dictation waits for the old one to let go of the microphone, and is finished meanwhile.
            var restart = vm.ToggleVoiceCommand.ExecuteAsync(null);
            Assert.True(vm.IsRecording);
            var finish = vm.ToggleVoiceCommand.ExecuteAsync(null);
            first.TranscribeGate!.TrySetResult();
            await Task.WhenAll(restart, finish).WaitAsync(Timeout);
            await FlushVoiceEvents();

            Assert.Equal(1, attempts);
            Assert.False(vm.IsRecording);
            Assert.Equal(VoiceInputState.Idle, voice.State);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RestartingImmediatelyAfterDiscardingStartsANewDictation()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var first = new FakeVoiceInputBackend
            {
                TranscribeGate = VoiceInputServiceTests.Signal(),
                IgnoreTranscriptionCancellation = true,
            };
            var second = new FakeVoiceInputBackend();
            var attempts = 0;
            using var voice = new VoiceInputService(() => ++attempts == 1 ? first : second, TestVoiceModels.Installed());
            using var vm = CreateViewModel(voice);
            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            await FlushVoiceEvents();
            first.Emit(VoiceAudioBufferTests.Said(3200));
            first.Emit(VoiceAudioBufferTests.Pause());
            await first.Transcribing.Task.WaitAsync(Timeout);
            vm.DiscardVoiceCommand.Execute(null);

            var restarting = vm.ToggleVoiceCommand.ExecuteAsync(null);
            first.TranscribeGate!.TrySetResult();
            await restarting.WaitAsync(Timeout);
            await FlushVoiceEvents();

            Assert.Equal(1, second.StartCount);
            Assert.True(vm.IsRecording);
            await voice.CancelAsync();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EditingAMessageCancelsPendingDictationWithoutChangingTheEditedText()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var backend = new FakeVoiceInputBackend
            {
                TranscribeGate = VoiceInputServiceTests.Signal(),
                IgnoreTranscriptionCancellation = true,
            };
            using var voice = new VoiceInputService(() => backend, TestVoiceModels.Installed());
            using var vm = CreateViewModel(voice);
            var message = new ChatMessage { Role = "user", Content = "Original message" };
            vm.CurrentChat = new Chat { Messages = [message] };
            vm.PromptText = "Unsent draft";
            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            await FlushVoiceEvents();
            backend.Emit(VoiceAudioBufferTests.Said(3200));
            backend.Emit(VoiceAudioBufferTests.Pause());
            await backend.Transcribing.Task.WaitAsync(Timeout);

            typeof(ChatViewModel).GetMethod("BeginComposerEdit", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [message]);
            backend.TranscribeGate!.TrySetResult();
            await voice.StopAsync().WaitAsync(Timeout);
            await FlushVoiceEvents();

            Assert.True(vm.IsEditingMessage);
            Assert.Equal("Original message", vm.PromptText);
            Assert.False(vm.IsRecording);
            await vm.CancelComposerEditCommand.ExecuteAsync(null);
            Assert.Equal("Unsent draft", vm.PromptText);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ClearingTheChatRejectsPendingDictationForTheNextDraft()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var backend = new FakeVoiceInputBackend
            {
                TranscribeGate = VoiceInputServiceTests.Signal(),
                IgnoreTranscriptionCancellation = true,
            };
            using var voice = new VoiceInputService(() => backend, TestVoiceModels.Installed());
            using var vm = CreateViewModel(voice);
            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            backend.Emit(VoiceAudioBufferTests.Said(3200));
            backend.Emit(VoiceAudioBufferTests.Pause());
            await backend.Transcribing.Task.WaitAsync(Timeout);
            vm.ClearChat();
            vm.PromptText = "next draft";
            backend.TranscribeGate!.TrySetResult();
            await voice.CancelAsync();
            await FlushVoiceEvents();

            Assert.Equal("next draft", vm.PromptText);
            Assert.False(vm.IsRecording);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MicrophoneProblemsSayWhatToDoAndTryingAgainListens()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var failed = new FakeVoiceInputBackend
            {
                CaptureError = new VoiceInputException(VoiceInputErrorKind.Microphone, "The microphone could not be opened: busy")
            };
            var healthy = new FakeVoiceInputBackend();
            var attempts = 0;
            using var voice = new VoiceInputService(() => ++attempts == 1 ? failed : healthy, TestVoiceModels.Installed());
            using var vm = CreateViewModel(voice);
            vm.StatusText = "assistant activity";

            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            await FlushVoiceEvents();
            Assert.True(vm.IsVoiceProblemVisible);
            Assert.False(vm.IsRecording);
            Assert.Equal(Loc.Voice_MicrophoneTitle, vm.VoiceProblemTitle);
            Assert.True(vm.IsVoiceMicrophoneProblem);
            Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), vm.CanOpenMicrophoneSettings);
            Assert.Contains("busy", vm.VoiceProblemDetail);
            Assert.Equal("assistant activity", vm.StatusText);

            await vm.RetryVoiceCommand.ExecuteAsync(null);
            await FlushVoiceEvents();
            Assert.False(vm.IsVoiceProblemVisible);
            Assert.True(vm.IsRecording);
            Assert.Equal(1, healthy.StartCount);
            await voice.CancelAsync();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task HearingNoWordsIsAGentleNoteThatTypingPutsAway()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var backend = new FakeVoiceInputBackend();
            using var voice = new VoiceInputService(() => backend, TestVoiceModels.Installed());
            using var vm = CreateViewModel(voice);

            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            backend.Emit(Enumerable.Repeat(0.0005f, 16000).ToArray());
            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            Assert.True(vm.IsVoiceProblemVisible);
            Assert.True(vm.IsVoiceNoSpeech);
            Assert.Equal(Loc.Voice_NoSpeechTitle, vm.VoiceProblemTitle);
            Assert.Equal("", vm.VoiceProblemDetail);

            vm.PromptText = "typing instead";
            Assert.False(vm.IsVoiceProblemVisible);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ADamagedModelAsksToSetVoiceTypingUpAgain()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var model = TestVoiceModels.Installed();
            var backend = new FakeVoiceInputBackend
            {
                ModelError = new VoiceInputException(VoiceInputErrorKind.Model, "damaged")
            };
            using var voice = new VoiceInputService(() => backend, model);
            using var vm = CreateViewModel(voice);

            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.IsVoiceSetupVisible, "a damaged model should be set up again");
            Assert.False(vm.IsRecording);
            Assert.False(model.IsInstalled);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheMicrophoneBesideSendStartsAndFinishesDictationWithoutSending()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var backend = new FakeVoiceInputBackend();
            using var voice = new VoiceInputService(() => backend, TestVoiceModels.Installed());
            using var vm = CreateViewModel(voice);
            var view = new ChatView { DataContext = vm };
            var window = new Window { Width = 1000, Height = 800, Content = view };
            window.Show();
            try
            {
                await FlushVoiceEvents();
                window.UpdateLayout();
                var composer = view.FindControl<StrataChatComposer>("Composer")!;
                var send = Part<Panel>(composer, "PART_SendWrap");
                var microphone = new ButtonAutomationPeer(Part<Button>(composer, "PART_VoiceButton"));
                Assert.True(send.IsEffectivelyVisible);
                microphone.Invoke();
                await vm.ToggleVoiceCommand.ExecutionTask!;
                await FlushVoiceEvents();
                window.UpdateLayout();
                Assert.True(vm.IsRecording);
                // Send steps aside while listening: the microphone, now a check, finishes.
                Assert.False(send.IsEffectivelyVisible);
                Assert.Equal(composer.StopDictatingLabel, microphone.GetName());

                backend.Emit(VoiceAudioBufferTests.Said(3200));
                microphone.Invoke();
                await vm.ToggleVoiceCommand.ExecutionTask!;
                await FlushVoiceEvents();
                window.UpdateLayout();

                Assert.False(vm.IsRecording);
                Assert.Equal("voice phrase", vm.PromptText);
                Assert.Null(vm.CurrentChat);
                Assert.Empty(vm.Messages);
                Assert.True(send.IsEffectivelyVisible);
                Assert.Equal(composer.DictateLabel, microphone.GetName());
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheDictationLanguageIsChosenInTheVoiceBarAndRemembered()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var backend = new FakeVoiceInputBackend();
            using var voice = new VoiceInputService(() => backend, TestVoiceModels.Installed());
            var store = CreateStore();
            using var vm = new ChatViewModel(voice, store, TestCopilot.Shared);
            var view = new ChatView { DataContext = vm };
            var window = new Window { Width = 1000, Height = 800, Content = view };
            window.Show();
            try
            {
                Assert.Equal(new VoiceLanguageOption("", Loc.Voice_LanguageAuto), vm.SelectedVoiceLanguage);
                Assert.Equal(vm.SelectedVoiceLanguage, vm.VoiceLanguages[0]);
                Assert.Contains(vm.VoiceLanguages, language => language.Code == "en");

                await vm.ToggleVoiceCommand.ExecuteAsync(null);
                await FlushVoiceEvents();
                window.UpdateLayout();
                var composer = view.FindControl<StrataChatComposer>("Composer")!;
                Assert.True(Part<Button>(composer, "PART_VoiceLanguageButton").IsEffectivelyVisible);
                Assert.Equal(Loc.Voice_LanguageAuto, Part<TextBlock>(composer, "PART_VoiceLanguageText").Text);

                // Choosing in the voice bar applies from the next words, and the language joins those offered first.
                var french = vm.MoreVoiceLanguages.Single(language => language.Code == "fr");
                composer.SelectedVoiceLanguage = french;
                Assert.Same(french, vm.SelectedVoiceLanguage);
                Assert.Equal("fr", store.Data.Settings.VoiceLanguage);
                Assert.Contains(french, vm.VoiceLanguages);
                Assert.DoesNotContain(french, vm.MoreVoiceLanguages);
                Assert.Equal("Français", Part<TextBlock>(composer, "PART_VoiceLanguageText").Text);
                backend.Emit(VoiceAudioBufferTests.Said(3200));
                await vm.ToggleVoiceCommand.ExecuteAsync(null);
                Assert.Equal(["fr"], backend.FinalLanguages.ToArray());
            }
            finally { window.Close(); }

            using var next = new ChatViewModel(voice, store, TestCopilot.Shared);
            Assert.Equal("fr", next.SelectedVoiceLanguage.Code);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ALanguageChosenInAnotherChatIsTheOneThisChatListensFor()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var store = CreateStore();
            var backend = new FakeVoiceInputBackend();
            using var voice = new VoiceInputService(() => backend, TestVoiceModels.Installed());
            using var otherVoice = new VoiceInputService(() => new FakeVoiceInputBackend(), TestVoiceModels.Installed());
            using var chat = new ChatViewModel(voice, store, TestCopilot.Shared);
            using var other = new ChatViewModel(otherVoice, store, TestCopilot.Shared);
            Assert.Equal("", chat.SelectedVoiceLanguage.Code);

            other.SelectedVoiceLanguage = other.MoreVoiceLanguages.Single(language => language.Code == "fr");
            await chat.ToggleVoiceCommand.ExecuteAsync(null);
            Assert.Equal("fr", chat.SelectedVoiceLanguage.Code);
            backend.Emit(VoiceAudioBufferTests.Said(3200));
            await chat.ToggleVoiceCommand.ExecuteAsync(null);
            Assert.Equal(["fr"], backend.FinalLanguages.ToArray());
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheDownloadCarriesOnWhenItsChatGoesAway()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var release = VoiceInputServiceTests.Signal();
            var model = TestVoiceModels.Missing(async token =>
            {
                await release.Task.WaitAsync(token);
                return new MemoryStream([1, 2, 3, 4]);
            });
            using var voice = new VoiceInputService(() => new FakeVoiceInputBackend(), model);
            var vm = CreateViewModel(voice);
            await vm.ToggleVoiceCommand.ExecuteAsync(null);
            vm.DownloadVoiceModelCommand.Execute(null);
            Assert.True(vm.IsVoiceDownloadVisible);

            // A new chat that was never saved goes away as soon as another chat opens.
            vm.Dispose();
            release.TrySetResult();
            await WaitUntil(() => model.IsInstalled, "the download should finish for every chat");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheComposerTurnsIntoAVoiceBarAndEscapeDiscards()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var backend = new FakeVoiceInputBackend();
            using var voice = new VoiceInputService(() => backend, TestVoiceModels.Installed());
            using var vm = CreateViewModel(voice);
            vm.PromptText = "Draft";
            var view = new ChatView { DataContext = vm };
            var window = new Window { Width = 1000, Height = 800, Content = view };
            window.Show();
            try
            {
                await vm.ToggleVoiceCommand.ExecuteAsync(null);
                await FlushVoiceEvents();
                window.UpdateLayout();
                var composer = view.FindControl<StrataChatComposer>("Composer")!;
                Assert.True(composer.IsRecording);
                Assert.Same(vm.DiscardVoiceCommand, composer.VoiceCancelCommand);
                Assert.True(Part<Grid>(composer, "PART_VoiceBar").IsEffectivelyVisible);
                Assert.False(Part<WrapPanel>(composer, "PART_SecondaryActions").IsEffectivelyVisible);
                Assert.True(Part<StrataVoiceWaveform>(composer, "PART_VoiceWaveform").IsActive);

                backend.Emit(VoiceAudioBufferTests.Said(3200));
                backend.Emit(VoiceAudioBufferTests.Pause());
                await WaitUntil(() => vm.PromptText == "Draft voice phrase", "the phrase should land");

                Part<TextBox>(composer, "PART_Input").RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Escape,
                });
                Assert.False(vm.IsRecording);
                Assert.Equal("Draft", vm.PromptText);
                window.UpdateLayout();
                Assert.False(Part<Grid>(composer, "PART_VoiceBar").IsEffectivelyVisible);
                Assert.True(Part<WrapPanel>(composer, "PART_SecondaryActions").IsEffectivelyVisible);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task VoiceCardsShowInTheComposerAndDetachingItCancelsTheMicrophone()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var backends = new List<FakeVoiceInputBackend>();
            var model = TestVoiceModels.Missing();
            using var voice = new VoiceInputService(() =>
            {
                var backend = new FakeVoiceInputBackend();
                backends.Add(backend);
                return backend;
            }, model);
            using var vm = CreateViewModel(voice);
            var view = new ChatView { DataContext = vm };
            var window = new Window { Width = 1000, Height = 800, Content = view };
            window.Show();
            try
            {
                await vm.ToggleVoiceCommand.ExecuteAsync(null);
                window.UpdateLayout();
                Assert.True(view.FindControl<Border>("VoiceSetupCard")!.IsEffectivelyVisible);
                var download = view.FindControl<Button>("VoiceSetupDownloadButton")!;
                new ButtonAutomationPeer(download).Invoke();
                await WaitUntil(() => vm.IsRecording, "listening should start after the download");
                window.UpdateLayout();
                Assert.False(view.FindControl<Border>("VoiceSetupCard")!.IsEffectivelyVisible);
                Assert.False(view.FindControl<Border>("VoiceDownloadCard")!.IsEffectivelyVisible);

                window.Content = null;
                await voice.CancelAsync();
                await FlushVoiceEvents();
                Assert.False(vm.IsRecording);
                Assert.False(Assert.Single(backends).IsCapturing);
                Assert.True(backends[0].Disposed);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    private static T Part<T>(StrataChatComposer composer, string name) where T : Control
        => composer.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static ChatViewModel CreateViewModel(VoiceInputService voice, params Chat[] chats)
        => new(voice, CreateStore(chats), TestCopilot.Shared);

    private static DataStore CreateStore(params Chat[] chats)
        => new(new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false,
                ShowAnimations = false,
                NotificationsEnabled = false,
                AutoGenerateTitles = false,
            },
            Chats = [.. chats],
        });

    private static async Task FlushVoiceEvents()
        => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

    private static async Task WaitUntil(Func<bool> condition, string because)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(20);
            await FlushVoiceEvents();
        }
        Assert.True(condition(), because);
    }
}
