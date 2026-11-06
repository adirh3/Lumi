using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Services;
using Lumi.Services.WhatsNew;

namespace Lumi.ViewModels;

/// <summary>
/// What's new: shown once after an update, with what came out since the version the user last saw, or from Settings →
/// About with the latest highlights. The shell decides when it opens and what it covers (MainViewModel.WhatsNew.cs);
/// this is what the overlay shows.
/// </summary>
public sealed partial class WhatsNewViewModel : ObservableObject
{
    private string _releaseNotesUrl = UpdateService.ReleasesPageUrl;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private string _subtitle = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSpotlight))]
    private WhatsNewItemViewModel? _spotlight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHighlights))]
    private IReadOnlyList<WhatsNewItemViewModel> _highlights = [];

    public bool HasSpotlight => Spotlight is not null;

    public bool HasHighlights => Highlights.Count > 0;

    /// <summary>Raised when the user closes What's new, however they close it.</summary>
    public event Action? Closed;

    /// <summary>Raised after an item's action closed What's new, so the shell takes the user where it leads.</summary>
    public event Action<WhatsNewDestination>? ActionRequested;

    /// <param name="since">The version the update came from, or null for the latest highlights.</param>
    internal void Show(
        WhatsNewSelection selection,
        Version version,
        Version? since,
        bool isPhoneConnected,
        string releaseNotesUrl)
    {
        VersionText = Loc.Get("WhatsNew_Version", WhatsNewSchedule.Display(version));
        Subtitle = since is null
            ? Loc.WhatsNew_Recent
            : Loc.Get("WhatsNew_Since", WhatsNewSchedule.Display(since));
        Spotlight = selection.Spotlight is { } spotlight
            ? new WhatsNewItemViewModel(spotlight, Act, isPhoneConnected)
            : null;
        Highlights = selection.Highlights
            .Select(item => new WhatsNewItemViewModel(item, Act, isPhoneConnected))
            .ToList();
        _releaseNotesUrl = releaseNotesUrl;
        IsOpen = true;
    }

    [RelayCommand]
    private void Dismiss()
    {
        if (!IsOpen)
            return;

        IsOpen = false;
        Closed?.Invoke();
    }

    [RelayCommand]
    private void OpenReleaseNotes()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_releaseNotesUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.IO.IOException)
        {
            Trace.TraceWarning($"[WhatsNew] Could not open the release notes: {ex.Message}");
        }
    }

    private void Act(WhatsNewItemViewModel item)
    {
        if (!IsOpen)
            return;

        Dismiss();
        ActionRequested?.Invoke(item.Destination);
    }
}

/// <summary>One item of What's new, in the user's language.</summary>
public sealed partial class WhatsNewItemViewModel
{
    private readonly Action<WhatsNewItemViewModel> _act;

    internal WhatsNewItemViewModel(WhatsNewItem item, Action<WhatsNewItemViewModel> act, bool isPhoneConnected)
    {
        _act = act;
        Icon = item.Icon;
        Title = Loc.Get(item.TitleKey);
        Body = Loc.Get(item.BodyKey);
        Eyebrow = item.EyebrowKey is { } eyebrow ? Loc.Get(eyebrow) : "";
        Points = item.Points.Select(point => new WhatsNewPointViewModel(point.Icon, Loc.Get(point.TextKey))).ToList();
        Destination = item.Destination;
        IsPhone = item.Destination == WhatsNewDestination.PhoneSettings;

        // A phone that is already paired leads to its settings rather than to setting one up.
        IsConnected = IsPhone && isPhoneConnected;
        ActionLabel = IsConnected
            ? Loc.WhatsNew_Phone_Manage
            : item.ActionKey is { } action ? Loc.Get(action) : "";
        Note = IsConnected || item.NoteKey is null ? "" : Loc.Get(item.NoteKey);
    }

    public string Icon { get; }

    public string Title { get; }

    public string Body { get; }

    public string Eyebrow { get; }

    public bool HasEyebrow => Eyebrow.Length > 0;

    public IReadOnlyList<WhatsNewPointViewModel> Points { get; }

    public bool HasPoints => Points.Count > 0;

    public WhatsNewDestination Destination { get; }

    public string ActionLabel { get; }

    public bool HasAction => Destination != WhatsNewDestination.None && ActionLabel.Length > 0;

    public string Note { get; }

    public bool HasNote => Note.Length > 0;

    /// <summary>Whether the item is about Lumi on the user's phone, which the overlay pictures as a phone.</summary>
    public bool IsPhone { get; }

    /// <summary>Whether this PC already has a paired phone, for the phone item.</summary>
    public bool IsConnected { get; }

    public string ConnectedText => Loc.WhatsNew_Phone_Connected;

    [RelayCommand]
    private void Act() => _act(this);
}

public sealed record WhatsNewPointViewModel(string Icon, string Text);
