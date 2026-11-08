using System;
using Lumi.Services;
using Lumi.Services.WhatsNew;

namespace Lumi.ViewModels;

/// <summary>
/// What's new after an update: decided as Lumi starts, shown once the window is in sight, and remembered as seen when
/// the user closes it. Settings → About opens the latest highlights again.
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// One showing of What's new: the versions it covers (<see cref="Since"/> is null for the latest highlights), the
    /// version its header names, and whether closing it marks the update as seen.
    /// </summary>
    private sealed record WhatsNewRequest(Version? Since, Version UpTo, Version Shown, bool MarksSeen);

    private WhatsNewRequest? _pendingWhatsNew;
    private WhatsNewRequest? _shownWhatsNew;

    public WhatsNewViewModel WhatsNewVM { get; } = new();

    /// <summary>
    /// Decides as Lumi starts whether it shows What's new. Only installed builds do: a development build's version says
    /// nothing about what its user has seen, so it neither shows nor records anything. <paramref name="force"/>
    /// (--whats-new) shows everything the catalog has since <paramref name="forceSince"/> (--whats-new-from) or 1.0,
    /// without recording it. Only the first window's shell is asked.
    /// </summary>
    public void PrepareWhatsNew(bool isInstalledBuild, bool force = false, string? forceSince = null)
        => PrepareWhatsNew(isInstalledBuild, WhatsNewSchedule.CurrentVersion, force, forceSince);

    internal void PrepareWhatsNew(bool isInstalledBuild, Version current, bool force = false, string? forceSince = null)
    {
        if (force)
        {
            var newest = WhatsNewSchedule.Newest(WhatsNewCatalog.Releases);
            var upTo = newest > current ? newest : current;
            _pendingWhatsNew = new WhatsNewRequest(
                WhatsNewSchedule.Parse(forceSince) ?? WhatsNewSchedule.Baseline, upTo, upTo, MarksSeen: false);
            return;
        }

        if (!isInstalledBuild)
            return;

        var since = WhatsNewSchedule.PendingSince(
            _dataStore.Data.Settings, current, WhatsNewCatalog.Releases, WhatsNewSchedule.CurrentPlatform);
        if (since is not null)
            _pendingWhatsNew = new WhatsNewRequest(since, current, current, MarksSeen: true);
    }

    /// <summary>Shows the What's new this start found, once the window is in sight and the welcome is over.</summary>
    public void ShowPendingWhatsNew()
    {
        if (_pendingWhatsNew is not { } request || !IsOnboarded || WhatsNewVM.IsOpen)
            return;

        _pendingWhatsNew = null;
        ShowWhatsNew(request);
    }

    /// <summary>The latest highlights, from Settings → About.</summary>
    private void OpenWhatsNew()
    {
        if (WhatsNewVM.IsOpen)
            return;

        var current = WhatsNewSchedule.CurrentVersion;
        ShowWhatsNew(new WhatsNewRequest(null, current, current, MarksSeen: false));
    }

    private void ShowWhatsNew(WhatsNewRequest request)
    {
        var selection = WhatsNewSchedule.Select(
            WhatsNewCatalog.Releases,
            request.Since ?? WhatsNewSchedule.Baseline,
            request.UpTo,
            WhatsNewSchedule.CurrentPlatform);
        if (selection.IsEmpty)
            return;

        _shownWhatsNew = request;
        var settings = _dataStore.Data.Settings;
        var isPhoneConnected = settings.RemoteAccessEnabled && _dataStore.SnapshotRemotePairedDevices().Count > 0;
        var releaseNotesUrl = request.Shown == WhatsNewSchedule.CurrentVersion
            ? $"{UpdateService.ReleasesPageUrl}/tag/v{WhatsNewSchedule.Format(request.Shown)}"
            : UpdateService.ReleasesPageUrl;
        WhatsNewVM.Show(selection, request.Shown, request.Since, isPhoneConnected, releaseNotesUrl);
    }

    private void OnWhatsNewClosed()
    {
        if (_shownWhatsNew is { MarksSeen: true } request)
        {
            WhatsNewSchedule.MarkSeen(_dataStore.Data.Settings, request.UpTo);
            _ = _dataStore.SaveAsync();
        }

        _shownWhatsNew = null;
    }

    /// <summary>Takes the user where a What's new action leads; false for a destination that leads nowhere yet.</summary>
    internal bool OpenWhatsNewDestination(WhatsNewDestination destination)
    {
        switch (destination)
        {
            case WhatsNewDestination.PhoneSettings:
                SettingsVM.ShowSetting(SettingsViewModel.MobilePageIndex);
                SelectedNavIndex = 7;
                return true;
            case WhatsNewDestination.ImageSettings:
                SettingsVM.ShowSetting(SettingsViewModel.AiModelsPageIndex, "ImageGenerationGroup");
                SelectedNavIndex = 7;
                return true;
            case WhatsNewDestination.ResponseDetailSettings:
                SettingsVM.ShowSetting(SettingsViewModel.AiModelsPageIndex, "ResponseDetailSetting");
                SelectedNavIndex = 7;
                return true;
            case WhatsNewDestination.Jobs:
                SelectedNavIndex = 1;
                return true;
            case WhatsNewDestination.SandboxSettings:
                SettingsVM.ShowSetting(SettingsViewModel.PermissionsPageIndex, "SandboxGroup");
                SelectedNavIndex = 7;
                return true;
            default:
                return false;
        }
    }
}
