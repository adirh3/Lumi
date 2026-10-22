using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.ViewModels;

/// <summary>An app a tool opened: its card in the transcript and its row in the Workspace.</summary>
public sealed partial class EmbeddedAppItem : TranscriptItem
{
    public EmbeddedAppItem(ChatMessage message, Action<EmbeddedAppItem> open)
        : base("app:" + message.Id)
    {
        Message = message;
        OpenCommand = new RelayCommand(() => open(this));
    }

    /// <summary>The tool message that opened the app and keeps it.</summary>
    public ChatMessage Message { get; }

    public IRelayCommand OpenCommand { get; }

    /// <summary>The Workspace is showing this app.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionLabel))]
    private bool _isShowing;

    private EmbeddedApp App => Message.EmbeddedApp!;

    public string Title => App.Title;

    public bool IsCanvas => App.Kind == EmbeddedAppKind.Canvas;

    /// <summary>A canvas instance is running and can be closed.</summary>
    public bool CanClose => IsCanvas && !App.IsClosed;

    public string Subtitle
    {
        get
        {
            var text = IsCanvas ? Loc.Apps_Canvas : Loc.Apps_McpApp;
            if (!string.IsNullOrWhiteSpace(App.SourceName) && !string.Equals(App.SourceName, App.Title, StringComparison.OrdinalIgnoreCase))
                text += " · " + App.SourceName;
            return App.IsClosed ? text + " · " + Loc.Apps_Closed : text;
        }
    }

    public string ActionLabel => IsShowing ? Loc.Apps_Showing : App.IsClosed ? Loc.Apps_Reopen : Loc.Apps_Open;

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(CanClose));
    }
}
