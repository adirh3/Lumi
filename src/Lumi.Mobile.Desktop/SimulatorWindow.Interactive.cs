#if DEBUG
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;

namespace Lumi.Mobile.Desktop;

public partial class SimulatorWindow
{
    /// <summary>A paired, populated app on local fixture data; see <see cref="DesignPreview"/>.</summary>
    internal void OpenDesignPreview()
    {
        if (DataContext is not MobileShellViewModel shell || _shell is null)
            throw new InvalidOperationException("The mobile shell must be mounted before opening the design preview.");

        Title = "Lumi Mobile - design preview";
        DesignPreview.Apply(shell);
    }

    internal void OpenPopupPreview()
    {
        if (DataContext is not MobileShellViewModel shell || _shell is null)
            throw new InvalidOperationException("The mobile shell must be mounted before opening the popup preview.");

        Title = "Lumi Mobile - polished popup preview";
        shell.IsPaired = shell.IsConnected = shell.IsHostReady = true;
        shell.HostName = "Preview PC";
        shell.UserName = "Adir";
        var projects = new List<RemoteProject>
        {
            new() { Id = Guid.NewGuid(), Name = "Lumi", Instructions = "Build a thoughtful personal assistant." },
            new() { Id = Guid.NewGuid(), Name = "Everyday life" },
            new() { Id = Guid.NewGuid(), Name = "Ideas & inspiration" },
            new() { Id = Guid.NewGuid(), Name = "A workspace with a longer name that wraps gracefully" }
        };
        foreach (var project in projects)
            shell.Projects.Add(new ProjectPickViewModel { Id = project.Id, Name = project.Name });
        shell.Library.Apply(new RemoteLibrary { Projects = projects });
        shell.Chat.Reset(Guid.Empty, "New chat");
        shell.Chat.ApplyCatalogs(new RemoteSettings
        {
            PreferredModel = "gpt-6.1-sol",
            AvailableModels = ["auto", "gpt-6.1-sol", "gpt-6-sol", "claude-opus-5.5", "claude-sonnet-5.5", "gemini-3.8-flash"],
            ModelDisplayNames =
            [
                "auto=Auto", "gpt-6.1-sol=GPT-6.1 Sol", "gpt-6-sol=GPT-6 Sol",
                "claude-opus-5.5=Claude Opus 5.5", "claude-sonnet-5.5=Claude Sonnet 5.5",
                "gemini-3.8-flash=Gemini 3.8 Flash"
            ],
            ModelReasoningEfforts = ["gpt-6.1-sol=low,medium,high,xhigh"],
            ModelContextWindowTiers = ["gpt-6.1-sol=Default,Long context"],
            ModelDefaults = [new() { Model = "gpt-6.1-sol", Quality = "medium", ContextWindowTier = "Default" }]
        });
        shell.Chat.PlanContent = "## A calmer mobile experience\n\n- [x] Create a consistent visual language\n- [x] Add gentle background frost\n- [ ] Review the final details on phone and Fold";
        shell.Chat.SelectedSourceAnswer = new AssistantItemViewModel(new RemoteTranscriptItem
        {
            Id = "preview-answer", Text = "A focused, touch-friendly mobile experience.",
            Sources =
            [
                new() { Title = "Mobile design guidelines", Snippet = "Clear hierarchy, generous touch targets and restrained motion." },
                new() { Title = "Accessible interaction patterns", Snippet = "Visible dismissal and consistent selection states." }
            ]
        });
        shell.Chat.SelectedActivity = new ActivitySummaryItemViewModel(new RemoteTranscriptItem
        {
            Id = "preview-work", Status = "Completed", ActionCount = 3, DurationMs = 8400,
            FileChanges = [new() { Path = @"Views\Popup.axaml", FileName = "Popup.axaml", LinesAdded = 24, LinesRemoved = 8 }]
        });
        shell.Chat.SelectedJobWake = new JobWakeItemViewModel(new RemoteTranscriptItem
        {
            Id = "preview-job", Author = "Lumi Job - Morning briefing",
            Text = "Background job triggered: Morning briefing\n\nJob instructions:\nPrepare a short morning briefing.\n\nTrigger context:\nYour briefing is ready.\nExit code: 0\nStarted: 08:00\nCompleted: 08:01\nFull script output:\nAll checks completed.\n\nRespond as Lumi"
        });
        var actionChat = new ChatListItemViewModel(new RemoteChat
        {
            Id = Guid.NewGuid(), Title = "Designing a calmer mobile experience"
        });
        shell.OpenChatActionsCommand.Execute(actionChat);
        shell.IsChatActionsOpen = false;

        var root = _shell.FindControl<Panel>("Root")!;
        var form = new StrataInteractiveBlock
        {
            Name = "PopupPreviewForm", TouchMode = true, PickerHost = root, IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20),
            Definition = InteractiveDefinition.Parse("form",
                """{"id":"preview","title":"Local picker preview","fields":[{"id":"pace","label":"Response style","type":"select","value":"Balanced","help":"Choose the style that works best for you.","options":["Concise","Balanced","Detailed","Step-by-step","Technical","Conversational"]},{"id":"date","label":"Start date","type":"date","value":"2026-10-13","help":"Choose a day to begin."}]}""")
        };
        root.Children.Add(form);
        (string Label, string Name, MobilePage Page)[] previews =
        [
            ("Projects", "ProjectPickerSheet", MobilePage.Chat),
            ("Models", "ModelSheet", MobilePage.Chat),
            ("Reasoning effort", "EffortSheet", MobilePage.Chat),
            ("Context window", "ContextSheet", MobilePage.Chat),
            ("Chat details", "RunSettingsSheet", MobilePage.Chat),
            ("Chat actions", "ChatActionsSheet", MobilePage.Chat),
            ("Plan", "PlanSheet", MobilePage.Chat),
            ("Sources", "SourcesSheet", MobilePage.Chat),
            ("Activity", "ActivitySheet", MobilePage.Chat),
            ("Job details", "JobWakeSheet", MobilePage.Chat),
            ("Library actions", "LibraryActionsSheet", MobilePage.Library),
            ("Discard changes", "LibraryDiscardSheet", MobilePage.Library),
            ("Disconnect", "DisconnectSheet", MobilePage.Settings),
            ("Form choices", "Field_pace", MobilePage.Chat),
            ("Date picker", "Field_date", MobilePage.Chat)
        ];
        var picker = new ComboBox
        {
            Name = "PopupPreviewPicker", MinWidth = 140,
            ItemsSource = previews.Select(preview => preview.Label).ToArray()
        };
        if (this.FindControl<Border>("SimulatorBar")?.Child is StackPanel bar)
            bar.Children.Insert(0, picker);
        if (_readout is not null)
            _readout.IsVisible = false;
        if (_showBezel is not null)
            _showBezel.IsVisible = false;
        if (_devicePicker is not null)
            _devicePicker.MinWidth = 160;
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedIndex < 0)
                return;
            form.ClosePicker();
            foreach (var sheet in _shell.GetVisualDescendants().OfType<StrataBottomSheet>())
                sheet.SetCurrentValue(StrataBottomSheet.IsOpenProperty, false);
            var preview = previews[picker.SelectedIndex];
            shell.Page = preview.Page;
            shell.IsNavigationOpen = preview.Name == "ProjectPickerSheet";
            form.IsVisible = preview.Name.StartsWith("Field_", StringComparison.Ordinal);
            if (form.IsVisible)
            {
                Dispatcher.UIThread.Post(() =>
                    form.GetVisualDescendants().OfType<Button>().Single(button => button.Name == preview.Name)
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), DispatcherPriority.Loaded);
            }
            else
            {
                if (preview.Name == "LibraryActionsSheet")
                    shell.Library.OpenRowActionsCommand.Execute(shell.Library.Entries[0]);
                var target = _shell.GetLogicalDescendants().OfType<StrataBottomSheet>()
                    .Single(sheet => sheet.Name == preview.Name);
                target.SetCurrentValue(StrataBottomSheet.IsOpenProperty, true);
            }
        };
        Dispatcher.UIThread.Post(() => picker.SelectedIndex = 1, DispatcherPriority.Loaded);
    }

    internal void OpenInteractiveControls(string profileRoot)
    {
        var folder = Path.Combine(Path.GetFullPath(profileRoot), "Lumi", "chats");
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException("Use the isolated desktop UI Lab profile for the mobile fixture.");
        var answers = new List<(string Title, string Text, Dictionary<string, string>? State)>();
        var polishedScreens = new List<(string Title, List<RemoteTranscriptItem> Items)>();
        foreach (var file in Directory.GetFiles(folder, "*.json"))
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            var polishedItems = new List<RemoteTranscriptItem>();
            var hasReceipt = false;
            foreach (var message in json.RootElement.EnumerateArray())
            {
                var messageId = message.GetProperty("id").GetGuid().ToString("N");
                if (message.TryGetProperty("interactionReceipt", out var receiptJson) && receiptJson.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    var receipt = System.Text.Json.JsonSerializer.Deserialize(receiptJson, RemoteJsonContext.Default.RemoteInteractionReceipt);
                    polishedItems.Add(new RemoteTranscriptItem
                    {
                        Id = messageId, Kind = RemoteProtocol.ItemKinds.User, InteractionReceipt = receipt,
                        InteractionLanguage = "en"
                    });
                    hasReceipt = true;
                    continue;
                }
                if (message.TryGetProperty("inputDefinition", out var inputJson) && inputJson.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var input = new RemoteTranscriptItem
                    {
                        Id = messageId, Kind = RemoteProtocol.ItemKinds.Input, InteractionLanguage = "en",
                        InputRequest = new RemoteInputRequest
                        {
                            Kind = message.GetProperty("inputKind").GetString()!, Definition = inputJson.GetString()!,
                            StateJson = message.TryGetProperty("inputAnswer", out var answer) && answer.ValueKind == System.Text.Json.JsonValueKind.String ? answer.GetString() : null,
                            Status = message.TryGetProperty("inputAnswer", out answer) && answer.ValueKind == System.Text.Json.JsonValueKind.String ? "Submitted" : "Expired"
                        }
                    };
                    polishedScreens.Add(("Submitted input", [input]));
                }
                if (message.GetProperty("role").GetString() != "assistant")
                    continue;
                var text = message.GetProperty("content").GetString() ?? "";
                var block = StrataTheme.Controls.MarkdownParser.Parse(text)
                    .FirstOrDefault(block => block.Kind == StrataTheme.Controls.MdBlockKind.Interactive);
                if (block.Kind != StrataTheme.Controls.MdBlockKind.Interactive)
                    continue;
                var definition = StrataTheme.Controls.InteractiveDefinition.Parse(block.Language, block.Content);
                var state = new Dictionary<string, string>();
                if (message.TryGetProperty("interactionStates", out var states))
                    foreach (var entry in states.EnumerateObject())
                        state[entry.Name] = entry.Value.GetString()!;
                answers.Add((definition.Kind, text, state));
                polishedItems.Add(new RemoteTranscriptItem
                {
                    Id = messageId, Text = text, Author = "Lumi",
                    InteractionStates = state, InteractionLanguage = "en"
                });
            }
            if (hasReceipt)
                polishedScreens.Add(("Action receipts", polishedItems));
        }
        if (answers.Count == 0)
            throw new InvalidOperationException("The profile contains no interactive fixture answers.");

        var picker = new ComboBox
        {
            Name = "InteractiveFixturePicker",
            ItemsSource = answers.Select(answer => answer.Title).Concat(polishedScreens.Select(screen => screen.Title)).ToList(),
            MinWidth = 140
        };
        if (this.FindControl<Border>("SimulatorBar")?.Child is StackPanel bar)
        {
            bar.Children.Insert(0, picker);
            if (_readout is not null)
                _readout.IsVisible = false;
            if (_devicePicker is not null)
                _devicePicker.MinWidth = 160;
        }
        picker.SelectionChanged += (_, _) =>
        {
            if (DataContext is not MobileShellViewModel shell || picker.SelectedIndex < 0)
                return;
            var polished = picker.SelectedIndex >= answers.Count;
            var screen = polished ? polishedScreens[picker.SelectedIndex - answers.Count] : default;
            var answer = polished ? default : answers[picker.SelectedIndex];
            var id = Guid.NewGuid();
            shell.IsPaired = true;
            shell.IsConnected = true;
            shell.IsHostReady = true;
            shell.Page = MobilePage.Chat;
            shell.Chat.Reset(id, "UI Lab - " + (polished ? screen.Title : answer.Title));
            shell.Chat.ApplyTranscript(new RemoteTranscript
            {
                ChatId = id, Title = shell.Chat.Title, Revision = 1,
                Status = new RemoteChatStatus { ChatId = id, StatusText = "Local UI fixture" },
                Turns = [new()
                {
                    Id = "fixture",
                    Items = polished ? screen.Items : [new()
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Text = answer.Text, Author = "Lumi",
                        InteractionStates = answer.State, InteractionLanguage = "en"
                    }]
                }]
            });
        };
        Dispatcher.UIThread.Post(() => picker.SelectedIndex = answers.FindIndex(answer => answer.Title == "form"),
            DispatcherPriority.Loaded);
    }
}
#endif
