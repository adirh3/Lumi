using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services.Remote;

namespace Lumi.ViewModels;

/// <summary>A choice of how long the PC waits without input before the user counts as away.</summary>
public sealed record PhoneAwayChoice(int Minutes, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Settings → Mobile → Phone notifications: whether paired phones hear about replies and questions
/// while the user is away from this PC, and when the user counts as away.
/// </summary>
public partial class SettingsViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPhoneNotificationsEnabledModified))]
    [NotifyCanExecuteChangedFor(nameof(SendTestPhoneNotificationCommand))]
    private bool _phoneNotificationsEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPhoneNotificationAwayModified))]
    private PhoneAwayChoice? _selectedPhoneAwayChoice;

    [ObservableProperty] private string _phoneNotificationStatusText = "";

    [ObservableProperty] private string _phoneNotificationTestResult = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendTestPhoneNotificationCommand))]
    private bool _hasPhoneNotificationListener;

    public IReadOnlyList<PhoneAwayChoice> PhoneAwayChoices { get; private set; } = [];

    public bool IsPhoneNotificationsEnabledModified =>
        PhoneNotificationsEnabled != _defaults.PhoneNotificationsEnabled;

    public bool IsPhoneNotificationAwayModified =>
        (SelectedPhoneAwayChoice?.Minutes ?? _defaults.PhoneNotificationAwayMinutes)
        != _defaults.PhoneNotificationAwayMinutes;

    private void InitializePhoneNotifications(UserSettings settings)
    {
        PhoneAwayChoices = PhoneNotificationCenter.AwayMinuteChoices
            .Select(static minutes => new PhoneAwayChoice(minutes, Loc.Get("PhoneNotification_AwayMinutes", minutes)))
            .ToArray();
        // Loading, not changing: skip the change handlers that save and tell the server.
#pragma warning disable MVVMTK0034
        _phoneNotificationsEnabled = settings.PhoneNotificationsEnabled;
        _selectedPhoneAwayChoice = AwayChoiceFor(settings.PhoneNotificationAwayMinutes);
#pragma warning restore MVVMTK0034
    }

    private PhoneAwayChoice AwayChoiceFor(int minutes) =>
        PhoneAwayChoices.FirstOrDefault(choice => choice.Minutes == minutes)
        ?? PhoneAwayChoices.First(choice => choice.Minutes == PhoneNotificationCenter.DefaultAwayMinutes);

    partial void OnPhoneNotificationsEnabledChanged(bool value)
    {
        _dataStore.Data.Settings.PhoneNotificationsEnabled = value;
        Save();
        _remoteServer?.PhoneNotificationSettingsChanged();
        PhoneNotificationTestResult = "";
        RefreshPhoneNotificationStatus();
    }

    partial void OnSelectedPhoneAwayChoiceChanged(PhoneAwayChoice? value)
    {
        if (value is null || _dataStore.Data.Settings.PhoneNotificationAwayMinutes == value.Minutes)
            return;

        _dataStore.Data.Settings.PhoneNotificationAwayMinutes = value.Minutes;
        Save();
        _remoteServer?.PhoneNotificationSettingsChanged();
    }

    [RelayCommand]
    private void RevertPhoneNotificationsEnabled() =>
        PhoneNotificationsEnabled = _defaults.PhoneNotificationsEnabled;

    [RelayCommand]
    private void RevertPhoneNotificationAway() =>
        SelectedPhoneAwayChoice = AwayChoiceFor(_defaults.PhoneNotificationAwayMinutes);

    private bool CanSendTestPhoneNotification() => PhoneNotificationsEnabled && HasPhoneNotificationListener;

    /// <summary>Sends a test to every phone that can hear one, whether or not the user is here.</summary>
    [RelayCommand(CanExecute = nameof(CanSendTestPhoneNotification))]
    private async Task SendTestPhoneNotificationAsync()
    {
        PhoneNotificationTestResult = "";
        var result = _remoteServer is { } server ? await server.SendTestNotificationAsync() : default;
        PhoneNotificationTestResult = result switch
        {
            { Heard: > 0 } => Loc.PhoneNotification_TestSent,
            { PushError: { } error } => Loc.Get("Remote_TestNotificationPushFailed", error),
            _ => Loc.PhoneNotification_NoListener
        };
    }

    /// <summary>Names the phones that can hear notifications now.</summary>
    private void RefreshPhoneNotificationStatus()
    {
        var listening = _remoteServer is { IsRunning: true } server
            ? server.NotificationDeviceIds
            : new HashSet<string>();
        var names = _dataStore.SnapshotRemotePairedDevices()
            .Where(device => listening.Contains(device.DeviceId))
            .Select(static device => string.IsNullOrWhiteSpace(device.DeviceName)
                ? Loc.Get("Remote_UnknownDevice")
                : device.DeviceName)
            .Distinct(StringComparer.CurrentCulture)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        HasPhoneNotificationListener = names.Count > 0;
        PhoneNotificationStatusText = names.Count > 0
            ? Loc.Get("PhoneNotification_Listening", string.Join(", ", names))
            : Loc.PhoneNotification_NoListener;
        if (names.Count == 0)
            PhoneNotificationTestResult = "";
    }
}
