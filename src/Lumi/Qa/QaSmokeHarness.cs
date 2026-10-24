#if DEBUG
#pragma warning disable IL2026 // Debug-only QA evidence uses reflection-based JSON.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views.Controls;
using StrataTheme.Controls;

namespace Lumi.Qa;

internal sealed class QaSmokeHarness
{
    private const string ProjectName = "QA project";
    private const string Instructions = "Keep this project's context separate.";
    private const string ChatTitle = "QA persisted conversation";
    private static QaModelServer? _model;
    private readonly Window _window;
    private readonly MainViewModel _vm;
    private readonly DataStore _store;
    private readonly QaSmokeOptions _options;
    private readonly string _output;
    private readonly QaSmokeReport _report;

    private QaSmokeHarness(Window window, MainViewModel vm, DataStore store, QaSmokeOptions options)
    {
        _window = window;
        _vm = vm;
        _store = store;
        _options = options;
        _output = options.OutputDirectory ?? Path.Combine(DataStore.AppDirectory, "qa-results");
        Directory.CreateDirectory(_output);
        _report = new()
        {
            ExpectedScenarios = options.VerifyRestart ? QaSmokeReport.RestartScenarioIds : QaSmokeReport.ScenarioIds
        };
    }

    internal static void PrepareProfile(DataStore store, QaSmokeOptions options)
    {
        if (!DataStore.UsesIsolatedProfile)
            throw new InvalidOperationException("QA requires an isolated LUMI_APPDATA_DIR.");
        if (options.VerifyRestart)
            return;
        if (store.Data.Chats.Count > 0 || store.Data.Projects.Count > 0)
            throw new InvalidOperationException("Start QA with a fresh isolated profile; use --qa-verify-restart only for its second launch.");

        _model = new QaModelServer(Path.Combine(DataStore.AppDirectory, "workspace"));
        var settings = store.Data.Settings;
        settings.Language = "en";
        settings.IsOnboarded = true;
        settings.UseBYOKOnly = true;
        settings.DefaultApprovalMode = ApprovalMode.AskFirst;
        settings.AutoGenerateTitles = false;
        settings.SuggestFollowUps = false;
        settings.EnableMemoryAutoSave = false;
        settings.EnableDailyBriefs = false;
        settings.EnableCodingHelpers = false;
        settings.RemoteAccessEnabled = false;
        settings.LaunchAtStartup = false;
        settings.MinimizeToTray = false;
        settings.PreferredModel = "byok:qa";
        settings.ByokEndpoints =
        [
            new ByokEndpoint
            {
                Id = "qa", Name = "Local QA fixture", ProviderType = "openai",
                BaseUrl = _model.Url, WireApi = "completions", ApiKeyMode = ByokApiKeyMode.None
            }
        ];
        settings.ByokModels =
        [
            new ByokModel { Id = "qa", EndpointId = "qa", ModelId = QaModelServer.Model, DisplayName = "QA fixture" }
        ];
        store.Save();
    }

    internal static void Start(
        IClassicDesktopStyleApplicationLifetime desktop, MainViewModel vm, DataStore store,
        CopilotService copilot, QaSmokeOptions options)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var harness = new QaSmokeHarness(
                desktop.MainWindow ?? throw new InvalidOperationException("QA has no main window."), vm, store, options);
            var exit = 1;
            try
            {
                await harness.RunAsync(copilot).WaitAsync(TimeSpan.FromMinutes(5));
                exit = harness._report.Complete ? 0 : 1;
            }
            catch (Exception error)
            {
                harness._report.Scenarios.Add(new("harness", false, 0, error.ToString()));
                Console.Error.WriteLine("[qa] " + error);
            }
            finally
            {
                try
                {
                    if (_model is not null)
                    {
                        await _model.DisposeAsync();
                        _model = null;
                    }
                    harness.WriteReport();
                }
                catch (Exception error)
                {
                    exit = 1;
                    Console.Error.WriteLine("[qa] Could not finish QA evidence: " + error);
                }
                Console.WriteLine($"[RESULT] LUMI_QA_{(exit == 0 ? "OK" : "FAIL")}");
                Environment.ExitCode = exit;
                desktop.Shutdown(exit);
            }
        }, DispatcherPriority.Background);
    }

    private async Task RunAsync(CopilotService copilot)
    {
        _window.Width = 1280;
        _window.Height = 900;
        await UntilAsync(() => NamedOrNull<StrataChatComposer>("Composer") is not null, "the composer to render");
        if (_options.VerifyRestart)
        {
            await ScenarioAsync("restart-persistence", VerifyRestartAsync);
            return;
        }

        await ScenarioAsync("navigation", async () =>
        {
            Click("NavSettings");
            await UntilAsync(() => _vm.SelectedNavIndex == 7 && NamedOrNull<ListBox>("SettingsPagesList") is not null,
                "Settings navigation");
            _vm.LastCustomizeNavIndex = 2;
            Click("NavCustomize");
            await UntilAsync(() => _vm.SelectedNavIndex == 2 && NamedOrNull<Panel>("ProjectsOverview") is not null,
                "Customize to open Projects");
            Click("NavChat");
            await UntilAsync(() => _vm.SelectedNavIndex == 0 && NamedOrNull<StrataChatComposer>("Composer") is not null,
                "Chats navigation");
        });

        await ScenarioAsync("project-save", async () =>
        {
            _vm.LastCustomizeNavIndex = 2;
            Click("NavCustomize");
            await UntilAsync(() => NamedOrNull<Button>("ProjectsNewButton") is not null, "the Projects overview");
            Click("ProjectsNewButton");
            await UntilAsync(() => NamedOrNull<TextBox>("ProjectNameInput") is not null, "the project editor");
            Named<TextBox>("ProjectNameInput").Text = ProjectName;
            Named<TextBox>("ProjectInstructionsEditor").Text = Instructions;
            _vm.ProjectsVM.EditWorkingDirectory = Model.Workspace;
            await UntilAsync(() => NamedOrNull<Button>("SaveProjectButton") is { IsEffectivelyEnabled: true }, "Save to become enabled");
            Click("SaveProjectButton");
            await UntilAsync(() => _store.Data.Projects.Any(project => project.Name == ProjectName), "the project to save");
            var project = _store.Data.Projects.Single(project => project.Name == ProjectName);
            Require(project.Instructions == Instructions && project.WorkingDirectory == Model.Workspace,
                "The project editor did not save its instructions and working directory.");
            Require(!_vm.ProjectsVM.HasUnsavedChanges, "The saved editor still has unsaved changes.");
        });

        await ScenarioAsync("ai-settings", async () =>
        {
            Click("NavSettings");
            await UntilAsync(() => NamedOrNull<ListBoxItem>("SettingsAiFeaturesItem") is not null, "the AI Features settings entry");
            Named<ListBoxItem>("SettingsAiFeaturesItem").IsSelected = true;
            await UntilAsync(() => NamedOrNull<Button>("ToggleAllAiFeaturesButton") is not null, "the AI Features page");
            Click("ToggleAllAiFeaturesButton");
            await UntilAsync(() => !_vm.SettingsVM.AreAllAiFeaturesOff, "automatic AI features to turn on");
            Click("ToggleAllAiFeaturesButton");
            await UntilAsync(() => _vm.SettingsVM.AreAllAiFeaturesOff, "all automatic AI features to turn off");
            Require(Named<TextBlock>("AiFeaturesSummaryText").Text == _vm.SettingsVM.AiFeaturesSummary,
                "The visible AI summary did not follow the switches.");
        });

        await ScenarioAsync("chat-streaming", async () =>
        {
            _vm.LastCustomizeNavIndex = 2;
            Click("NavCustomize");
            await UntilAsync(() => NamedOrNull<Button>("ProjectNewChatButton") is not null, "the saved project's chat action");
            Click("ProjectNewChatButton");
            await UntilAsync(() => NamedOrNull<StrataChatComposer>("Composer") is not null, "the new chat composer");
            await copilot.ConnectAsync();
            await SendAsync("QA_STREAM");
            try
            {
                await UntilAsync(() => _vm.ChatVM.IsStreaming && HasVisibleText("LUMI_QA_"),
                    "the real transcript to show a partial streamed reply");
                Require(_vm.ChatVM.IsBusy, "The assistant became ready before its stream finished.");
                Capture("stream-in-progress");
            }
            finally
            {
                Model.ReleaseStream.TrySetResult();
            }
            await ReplyAsync("LUMI_QA_STREAM");
            Require(_vm.ChatVM.CurrentChat?.ProjectId == Project().Id, "The message lost the selected project.");
            Require(_vm.ChatVM.PromptText.Length == 0, "Sending did not clear the composer.");
        });

        await ScenarioAsync("approval-denied", () => VerifyApprovalAsync(allow: false));
        await ScenarioAsync("approval-allowed", () => VerifyApprovalAsync(allow: true));
        await ScenarioAsync("persist-checkpoint", async () =>
        {
            Require(Model.Errors.Count == 0, string.Join(Environment.NewLine, Model.Errors));
            Require(Model.Requests == 5, $"Expected five local inference requests, received {Model.Requests}.");
            var chat = _vm.ChatVM.CurrentChat ?? throw new InvalidOperationException("No chat was created.");
            chat.Title = ChatTitle;
            await _store.SaveChatAsync(chat);
            await _store.SaveAsync();
            var messages = await _store.ReadPersistedChatMessagesAsync(chat.Id, CancellationToken.None);
            foreach (var text in new[] { "LUMI_QA_STREAM", "LUMI_QA_DENIED", "LUMI_QA_ALLOWED" })
                Require(messages.Any(message => message.Role == "assistant" && message.Content.Contains(text, StringComparison.Ordinal)),
                    $"The persisted transcript is missing {text}.");
            await File.WriteAllTextAsync(Path.Combine(DataStore.AppDirectory, "qa-checkpoint"), chat.Id.ToString());
        });
    }

    private async Task VerifyApprovalAsync(bool allow)
    {
        var path = Path.Combine(Model.Workspace, allow ? "allowed.txt" : "denied.txt");
        Require(!File.Exists(path), "The fixture's target already exists.");
        await SendAsync(allow ? "QA_ALLOW" : "QA_DENY");
        await UntilAsync(() => _vm.ChatVM.HasPendingPermissionRequests
            && NamedOrNull<Button>(allow ? "ApprovalAllowButton" : "ApprovalDenyButton") is not null,
            "a native tool permission request to render");
        Require(!File.Exists(path), "The native tool ran before approval.");
        Require(_vm.ChatVM.PendingPermissionRequests.All(request => request.ChatId == _vm.ChatVM.CurrentChat!.Id),
            "An approval was presented in the wrong chat.");
        Capture(allow ? "approval-before-allow" : "approval-before-deny");
        Click(allow ? "ApprovalAllowButton" : "ApprovalDenyButton");
        await ReplyAsync(allow ? "LUMI_QA_ALLOWED" : "LUMI_QA_DENIED");
        Require(!_vm.ChatVM.HasPendingPermissionRequests, "The approval card did not settle.");
        if (allow)
            Require(File.Exists(path) && await File.ReadAllTextAsync(path) == QaModelServer.FileText,
                "The allowed native tool did not write the expected file.");
        else
            Require(!File.Exists(path), "A denied native tool still wrote a file.");
    }

    private async Task VerifyRestartAsync()
    {
        var checkpoint = Path.Combine(DataStore.AppDirectory, "qa-checkpoint");
        Require(File.Exists(checkpoint), "The first launch did not finish its persistence checkpoint.");
        var id = Guid.Parse(await File.ReadAllTextAsync(checkpoint));
        var project = Project();
        Require(project.Instructions == Instructions, "The project's instructions did not survive process restart.");
        Require(_store.Data.Settings.UseBYOKOnly && !_store.Data.Settings.EnableCodingHelpers
            && !_store.Data.Settings.EnableDailyBriefs && !_store.Data.Settings.AutoGenerateTitles,
            "The saved AI settings did not survive process restart.");
        var chat = _store.Data.Chats.Single(chat => chat.Id == id);
        Require(chat.Title == ChatTitle && chat.ProjectId == project.Id, "Chat metadata did not survive process restart.");
        var messages = await _store.ReadPersistedChatMessagesAsync(id, CancellationToken.None);
        foreach (var text in new[] { "LUMI_QA_STREAM", "LUMI_QA_DENIED", "LUMI_QA_ALLOWED" })
            Require(messages.Any(message => message.Role == "assistant" && message.Content.Contains(text, StringComparison.Ordinal)),
                $"The restarted transcript is missing {text}.");
        Require(!File.Exists(Path.Combine(project.WorkingDirectory!, "denied.txt")), "The denied file exists after restart.");
        Require(await File.ReadAllTextAsync(Path.Combine(project.WorkingDirectory!, "allowed.txt")) == QaModelServer.FileText,
            "The allowed file changed after restart.");
        await _vm.OpenChatByIdAsync(id);
        await UntilAsync(() => _vm.ChatVM.CurrentChat?.Id == id && HasVisibleText("LUMI_QA_ALLOWED"),
            "the saved conversation to render after process restart");
    }

    private async Task SendAsync(string prompt)
    {
        Require(!_vm.ChatVM.IsBusy, "The preceding assistant reply has not finished.");
        Named<TextBox>("PART_Input").Text = prompt;
        await UntilAsync(() => _vm.ChatVM.PromptText == prompt, "the composer binding");
        Click("PART_SendButton");
    }

    private Task ReplyAsync(string expected) => UntilAsync(
        () => !_vm.ChatVM.IsBusy && !_vm.ChatVM.IsSessionActive && HasVisibleText(expected),
        "the completed reply " + expected);

    private async Task UntilAsync(Func<bool> condition, string description)
    {
        var deadline = Stopwatch.StartNew();
        do
        {
            _window.UpdateLayout();
            if (condition())
                return;
            await Task.Delay(20);
        } while (deadline.Elapsed < TimeSpan.FromSeconds(60));
        throw new TimeoutException($"Timed out waiting for {description}. Chat status: {_vm.ChatVM.StatusText}. " +
            string.Join("; ", _model?.Errors ?? []));
    }

    private async Task ScenarioAsync(string id, Func<Task> run)
    {
        var timer = Stopwatch.StartNew();
        string? error = null;
        try
        {
            await run();
            Capture(id);
        }
        catch (Exception failure)
        {
            error = failure.ToString();
            Console.Error.WriteLine($"[qa] FAIL {id}: {failure}");
        }
        _report.Scenarios.Add(new(id, error is null, timer.Elapsed.TotalMilliseconds, error));
        if (error is null)
            Console.WriteLine($"[qa] PASS {id}");
    }

    private T? NamedOrNull<T>(string name) where T : Control =>
        _window.GetVisualDescendants().OfType<T>().SingleOrDefault(control => control.Name == name && control.IsEffectivelyVisible);

    private T Named<T>(string name) where T : Control =>
        NamedOrNull<T>(name) ?? throw new InvalidOperationException($"The visible control #{name} was not found.");

    private void Click(string name)
    {
        var button = Named<Button>(name);
        Require(button.IsEffectivelyEnabled, $"#{name} is disabled.");
        new ButtonAutomationPeer(button).Invoke();
    }

    private bool HasVisibleText(string text) => _window.GetVisualDescendants().OfType<TranscriptTextContent>()
        .Any(control => control.IsEffectivelyVisible && control.Bounds.Height > 0
            && control.Text?.Contains(text, StringComparison.Ordinal) == true);

    private Project Project() => _store.Data.Projects.Single(project => project.Name == ProjectName);
    private static QaModelServer Model => _model ?? throw new InvalidOperationException("The local QA model is not running.");
    private static void Require(bool condition, string error)
    {
        if (!condition)
            throw new InvalidOperationException(error);
    }

    private void Capture(string id)
    {
        _window.UpdateLayout();
        var scale = _window.RenderScaling;
        var size = new PixelSize((int)Math.Ceiling(_window.Bounds.Width * scale), (int)Math.Ceiling(_window.Bounds.Height * scale));
        Require(size.Width > 0 && size.Height > 0, "The real window has no rendered bounds.");
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(_window);
        bitmap.Save(Path.Combine(_output, id + ".png"));
    }

    private void WriteReport()
    {
        var report = new
        {
            schemaVersion = 1,
            commit = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processId = Environment.ProcessId,
            phase = _options.VerifyRestart ? "restart" : "workflows",
            complete = _report.Complete,
            expectedScenarios = _report.ExpectedScenarios,
            scenarios = _report.Scenarios
        };
        File.WriteAllText(Path.Combine(_output, "results.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver()
            }));
    }
}
#endif
