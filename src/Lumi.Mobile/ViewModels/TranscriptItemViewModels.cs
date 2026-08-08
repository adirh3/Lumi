using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Remote.Protocol;

namespace Lumi.Mobile.ViewModels;

/// <summary>
/// Base for one renderable row. Concrete subclasses exist per kind so the transcript can use plain
/// <c>DataTemplate DataType=</c> matching instead of a hand-rolled template selector.
/// </summary>
public abstract partial class TranscriptItemViewModel : ObservableObject
{
    protected TranscriptItemViewModel(RemoteTranscriptItem item)
    {
        Id = item.Id;
        Kind = item.Kind;
    }

    public string Id { get; }

    public string Kind { get; }

    /// <summary>Applies a newer server projection of the same row in place, keeping the control alive.</summary>
    public abstract void Update(RemoteTranscriptItem item);
}

public sealed partial class UserTurnItemViewModel : TranscriptItemViewModel
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _author = "You";
    [ObservableProperty] private DateTimeOffset? _timestamp;
    [ObservableProperty] private string? _steerState;

    public ObservableCollection<RemoteAttachment> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    public bool HasSteerStatus => SteerState is "Queued" or "Steering" or "Steered" or "Failed";

    public string SteerStatusText => SteerState switch
    {
        "Queued" => "Queued...",
        "Steering" => "Steering...",
        "Steered" => "Steered",
        "Failed" => "Could not steer",
        _ => ""
    };

    public UserTurnItemViewModel(RemoteTranscriptItem item) : base(item) => Update(item);

    public override void Update(RemoteTranscriptItem item)
    {
        Text = item.Text ?? "";
        Author = string.IsNullOrWhiteSpace(item.Author) ? "You" : item.Author!;
        Timestamp = item.Timestamp;
        SteerState = item.SteerState;

        Attachments.Clear();
        foreach (var attachment in item.Attachments ?? [])
            Attachments.Add(attachment);

        OnPropertyChanged(nameof(HasAttachments));
    }

    partial void OnSteerStateChanged(string? value)
    {
        OnPropertyChanged(nameof(HasSteerStatus));
        OnPropertyChanged(nameof(SteerStatusText));
    }
}

public sealed partial class AssistantItemViewModel : TranscriptItemViewModel
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string? _model;

    public ObservableCollection<RemoteSource> Sources { get; } = [];

    public bool HasSources => Sources.Count > 0;

    public AssistantItemViewModel(RemoteTranscriptItem item) : base(item) => Update(item);

    /// <summary>Actions appear once the answer is finished — copying a half-written one is a trap.</summary>
    public bool ShowActions => !IsStreaming && Text.Length > 0;

    public override void Update(RemoteTranscriptItem item)
    {
        Text = item.Text ?? "";
        IsStreaming = item.IsStreaming;
        Model = item.Model;

        Sources.Clear();
        foreach (var source in item.Sources ?? [])
            Sources.Add(source);
        OnPropertyChanged(nameof(HasSources));
    }

    partial void OnIsStreamingChanged(bool value) => OnPropertyChanged(nameof(ShowActions));

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(ShowActions));

    /// <summary>
    /// Copies the answer to the device clipboard. Reached through the top level rather than an
    /// injected service because a transcript row is created per frame and must stay allocation-cheap.
    /// </summary>
    [RelayCommand]
    private async Task CopyAsync()
    {
        if (Text.Length == 0)
            return;

        var clipboard = Avalonia.Application.Current?.ApplicationLifetime switch
        {
            IClassicDesktopStyleApplicationLifetime desktop =>
                desktop.MainWindow?.Clipboard,
            ISingleViewApplicationLifetime single =>
                TopLevel.GetTopLevel(single.MainView)?.Clipboard,
            _ => null
        };

        if (clipboard is null)
            return;

        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(Text));
        await clipboard.SetDataAsync(data);
    }
}

public sealed partial class ReasoningItemViewModel : TranscriptItemViewModel
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _label = "Thinking";
    [ObservableProperty] private bool _isStreaming;

    public ReasoningItemViewModel(RemoteTranscriptItem item) : base(item) => Update(item);

    public override void Update(RemoteTranscriptItem item)
    {
        Text = item.Text ?? "";
        Label = string.IsNullOrWhiteSpace(item.Label) ? "Thinking" : item.Label!;
        IsStreaming = item.IsStreaming;
    }
}

public sealed partial class ToolCallViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string? _input;
    [ObservableProperty] private string? _output;
    [ObservableProperty] private string _status = "Completed";
    [ObservableProperty] private double? _durationMs;

    public string Id { get; private set; } = "";

    public bool IsRunning => Status == "InProgress";

    public ToolCallViewModel(RemoteToolCall tool) => Update(tool);

    public void Update(RemoteToolCall tool)
    {
        Id = tool.Id;
        Name = tool.Name;
        DisplayName = string.IsNullOrWhiteSpace(tool.DisplayName) ? tool.Name : tool.DisplayName!;
        Input = tool.Input;
        Output = tool.Output;
        Status = tool.Status;
        DurationMs = tool.DurationMs;
        OnPropertyChanged(nameof(IsRunning));
    }
}

public sealed partial class ToolGroupItemViewModel : TranscriptItemViewModel
{
    [ObservableProperty] private string _label = "Working";
    [ObservableProperty] private bool _isRunning;

    public ObservableCollection<ToolCallViewModel> Tools { get; } = [];

    public ToolGroupItemViewModel(RemoteTranscriptItem item) : base(item) => Update(item);

    public override void Update(RemoteTranscriptItem item)
    {
        Label = string.IsNullOrWhiteSpace(item.Label) ? "Working" : item.Label!;

        var incoming = item.Tools ?? [];
        for (var i = 0; i < incoming.Count; i++)
        {
            if (i < Tools.Count)
                Tools[i].Update(incoming[i]);
            else
                Tools.Add(new ToolCallViewModel(incoming[i]));
        }

        while (Tools.Count > incoming.Count)
            Tools.RemoveAt(Tools.Count - 1);

        IsRunning = Tools.Any(t => t.IsRunning);
    }
}

public sealed partial class TerminalItemViewModel : TranscriptItemViewModel
{
    [ObservableProperty] private string _command = "";
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private string _status = "Completed";
    [ObservableProperty] private double? _durationMs;
    [ObservableProperty] private string _toolName = "terminal";

    public TerminalItemViewModel(RemoteTranscriptItem item) : base(item) => Update(item);

    public override void Update(RemoteTranscriptItem item)
    {
        var tool = item.Tools is { Count: > 0 } tools ? tools[0] : null;
        Command = tool?.Input ?? item.Text ?? "";
        Output = tool?.Output ?? "";
        Status = tool?.Status ?? item.Status ?? "Completed";
        DurationMs = tool?.DurationMs ?? item.DurationMs;
        ToolName = string.IsNullOrWhiteSpace(item.Label) ? tool?.Name ?? "terminal" : item.Label!;
    }
}

public sealed partial class QuestionItemViewModel : TranscriptItemViewModel
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _allowFreeText = true;
    [ObservableProperty] private bool _allowMultiSelect;
    [ObservableProperty] private bool _isAnswered;
    [ObservableProperty] private string? _answer;

    public string QuestionId { get; private set; } = "";

    public ObservableCollection<string> Options { get; } = [];

    public QuestionItemViewModel(RemoteTranscriptItem item) : base(item) => Update(item);

    public override void Update(RemoteTranscriptItem item)
    {
        var question = item.Question;
        QuestionId = question?.QuestionId ?? item.Id;
        Text = question?.Text ?? item.Text ?? "";
        AllowFreeText = question?.AllowFreeText ?? true;
        AllowMultiSelect = question?.AllowMultiSelect ?? false;
        IsAnswered = question?.IsAnswered ?? false;
        Answer = question?.Answer;

        Options.Clear();
        foreach (var option in question?.Options ?? [])
            Options.Add(option);

    }
}

public sealed partial class ErrorItemViewModel : TranscriptItemViewModel
{
    [ObservableProperty] private string _text = "";

    public ErrorItemViewModel(RemoteTranscriptItem item) : base(item) => Update(item);

    public override void Update(RemoteTranscriptItem item) => Text = item.Text ?? "";
}

/// <summary>A file Lumi produced, surfaced as a tappable attachment chip.</summary>
public sealed partial class FileItemViewModel : TranscriptItemViewModel
{
    public FileItemViewModel(RemoteTranscriptItem item) : base(item) => Update(item);

    public ObservableCollection<RemoteAttachment> Files { get; } = [];

    public override void Update(RemoteTranscriptItem item)
    {
        Files.Clear();
        foreach (var file in item.Attachments ?? [])
            Files.Add(file);
    }
}

public static class TranscriptItemFactory
{
    public static TranscriptItemViewModel Create(RemoteTranscriptItem item) => item.Kind switch
    {
        RemoteProtocol.ItemKinds.User => new UserTurnItemViewModel(item),
        RemoteProtocol.ItemKinds.Reasoning => new ReasoningItemViewModel(item),
        RemoteProtocol.ItemKinds.ToolGroup or RemoteProtocol.ItemKinds.Tool => new ToolGroupItemViewModel(item),
        RemoteProtocol.ItemKinds.Terminal => new TerminalItemViewModel(item),
        RemoteProtocol.ItemKinds.Question => new QuestionItemViewModel(item),
        RemoteProtocol.ItemKinds.Error => new ErrorItemViewModel(item),
        RemoteProtocol.ItemKinds.File => new FileItemViewModel(item),

        // Unknown kinds degrade to plain assistant text rather than breaking the transcript.
        _ => new AssistantItemViewModel(item)
    };

    /// <summary>True when an existing row can be updated in place instead of being replaced.</summary>
    public static bool CanReuse(TranscriptItemViewModel existing, RemoteTranscriptItem item) =>
        existing.Id == item.Id && existing.Kind == item.Kind;
}

/// <summary>One user turn plus everything the assistant produced in response.</summary>
public sealed partial class TranscriptTurnViewModel : ObservableObject
{
    public TranscriptTurnViewModel(string id) => Id = id;

    public string Id { get; }

    public ObservableCollection<TranscriptItemViewModel> Items { get; } = [];

    public void Apply(RemoteTranscriptTurn turn)
    {
        for (var i = 0; i < turn.Items.Count; i++)
        {
            var incoming = turn.Items[i];

            if (i < Items.Count && TranscriptItemFactory.CanReuse(Items[i], incoming))
            {
                Items[i].Update(incoming);
                continue;
            }

            var created = TranscriptItemFactory.Create(incoming);
            if (i < Items.Count)
                Items[i] = created;
            else
                Items.Add(created);
        }

        while (Items.Count > turn.Items.Count)
            Items.RemoveAt(Items.Count - 1);
    }
}
