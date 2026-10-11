#if DEBUG
using Avalonia.Controls;
using Avalonia.Threading;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;

namespace Lumi.Mobile.Desktop;

public partial class SimulatorWindow
{
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
