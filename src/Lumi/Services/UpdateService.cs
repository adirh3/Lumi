using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Lumi.Services;

/// <summary>
/// Checks for app updates via GitHub Releases using Velopack.
/// Gracefully no-ops when running in dev/debug (not installed via Velopack).
/// </summary>
public sealed class UpdateService
{
    public const string RepoUrl = "https://github.com/adirh3/Lumi";
    public const string ReleasesPageUrl = RepoUrl + "/releases";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);
    private static readonly HttpClient ReleaseMetadataClient = CreateReleaseMetadataClient();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IUpdateBlockerService _blockerService;
    private readonly IUpdateRestartLauncher _restartLauncher;
    private readonly Func<IReadOnlyList<string>> _updateResourceRootsProvider;
    private UpdateManager? _manager;
    private UpdateInfo? _pendingUpdate;
    private Timer? _periodicTimer;
    private IReadOnlyList<string> _updateResourceRoots = [];

    /// <summary>Raised on UI thread when update state changes.</summary>
    public event Action<UpdateStatus>? StatusChanged;

    /// <summary>Raised after Velopack starts waiting for Lumi to exit gracefully.</summary>
    public event Action? RestartRequested;

    /// <summary>Current update status.</summary>
    public UpdateStatus CurrentStatus { get; private set; } = UpdateStatus.Idle;

    /// <summary>Version string of the available update, if any.</summary>
    public string? AvailableVersion { get; private set; }

    /// <summary>Download progress 0-100.</summary>
    public int DownloadProgress { get; private set; }

    /// <summary>Markdown release notes for the available update.</summary>
    public string ReleaseNotesMarkdown { get; private set; } = string.Empty;

    /// <summary>Human-friendly release title, typically from GitHub releases.</summary>
    public string ReleaseTitle { get; private set; } = string.Empty;

    /// <summary>Release page URL for the available update or the releases page.</summary>
    public string ReleasePageUrl { get; private set; } = ReleasesPageUrl;

    /// <summary>When the available release was published, if known.</summary>
    public DateTimeOffset? ReleasePublishedAt { get; private set; }

    /// <summary>When the update service last checked for updates.</summary>
    public DateTimeOffset? LastCheckedAt { get; private set; }

    /// <summary>Processes currently preventing Velopack from replacing Lumi's files.</summary>
    public IReadOnlyList<UpdateBlockingProcess> BlockingProcesses { get; private set; } = [];

    /// <summary>Details from the latest blocker-close attempt, if it could not finish.</summary>
    public string BlockerErrorMessage { get; private set; } = string.Empty;

    /// <summary>Details from the latest update operation failure.</summary>
    public string ErrorMessage { get; private set; } = string.Empty;

    /// <summary>Whether the app was installed via Velopack (vs running from IDE).</summary>
    public bool IsInstalled => _manager?.IsInstalled == true;

    public UpdateService()
        : this(
            new UpdateBlockerService(),
            new VelopackUpdateRestartLauncher(),
            VelopackUpdatePathResolver.GetUpdateResourceRoots)
    {
    }

    internal UpdateService(
        IUpdateBlockerService blockerService,
        IUpdateRestartLauncher restartLauncher,
        Func<IReadOnlyList<string>> updateResourceRootsProvider)
    {
        _blockerService = blockerService;
        _restartLauncher = restartLauncher;
        _updateResourceRootsProvider = updateResourceRootsProvider;
    }

    public void Initialize()
    {
        try
        {
            var source = new GithubSource(RepoUrl, null, prerelease: false);
            _manager = new UpdateManager(source, ResolveUpdateOptions());
            CurrentStatus = _manager.IsInstalled ? UpdateStatus.Idle : UpdateStatus.NotInstalled;
            if (_manager.IsInstalled)
            {
                try
                {
                    _updateResourceRoots = _updateResourceRootsProvider();
                }
                catch (Exception ex) when (ex is IOException
                                           or InvalidOperationException
                                           or ArgumentException
                                           or NotSupportedException)
                {
                    Trace.TraceWarning(
                        $"[UpdateService] Could not resolve every Velopack update path: {ex.Message}");
                    _updateResourceRoots = [AppContext.BaseDirectory];
                }
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[UpdateService] Failed to initialize: {ex.Message}");
            CurrentStatus = UpdateStatus.NotInstalled;
        }
    }

    /// <summary>
    /// Velopack's default update channel per OS is <c>win</c> / <c>linux</c> / <c>osx</c>
    /// (<see cref="VelopackRuntimeInfo.GetOsShortName"/>). The release pipeline publishes Windows and
    /// Linux on those exact default channels, but macOS is published per-architecture
    /// (<c>osx-arm64</c> / <c>osx-x64</c>) because there is no universal build — so on macOS the client
    /// must request the architecture-matched channel explicitly, otherwise the default <c>osx</c> feed
    /// doesn't exist and update checks silently find nothing. We key off the running process
    /// architecture (not the OS architecture) so an <c>osx-x64</c> build under Rosetta on Apple Silicon
    /// correctly stays on the <c>osx-x64</c> channel it was installed from. Windows/Linux return
    /// <c>null</c> here, preserving their exact previous default-channel behavior.
    /// </summary>
    private static UpdateOptions? ResolveUpdateOptions()
    {
        if (!OperatingSystem.IsMacOS())
            return null;

        var channel = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "osx-arm64"
            : "osx-x64";
        return new UpdateOptions { ExplicitChannel = channel };
    }

    /// <summary>Start periodic background checks. Call once after UI is ready.</summary>
    public void StartPeriodicChecks()
    {
        if (_manager is null || !_manager.IsInstalled) return;

        CheckInBackground();
        _periodicTimer = new Timer(_ => CheckInBackground(), null, CheckInterval, CheckInterval);
    }

    private async void CheckInBackground()
    {
        try { await CheckForUpdateAsync(); }
        catch { /* already handled inside */ }
    }

    public async Task CheckForUpdateAsync()
    {
        // Once the update is downloaded, preserve the restart-required state until
        // the user restarts. A later background or manual check should not downgrade it.
        if (CurrentStatus is UpdateStatus.ReadyToRestart
            or UpdateStatus.PreparingToRestart
            or UpdateStatus.BlockedByProcesses
            or UpdateStatus.ClosingBlockingProcesses)
            return;

#if DEBUG
        if (TryGetDebugSimulation(out var debugSimulation) && (_manager is null || !_manager.IsInstalled))
        {
            if (!await _gate.WaitAsync(0)) return;
            try
            {
                ApplyDebugSimulation(debugSimulation);
            }
            finally
            {
                _gate.Release();
            }
            return;
        }
#endif

        var checkedAt = DateTimeOffset.Now;

        if (_manager is null || !_manager.IsInstalled)
        {
            LastCheckedAt = checkedAt;
            ClearReleaseMetadata();
            SetStatus(UpdateStatus.NotInstalled);
            return;
        }

        if (!await _gate.WaitAsync(0)) return; // skip if already checking/downloading
        try
        {
            ErrorMessage = string.Empty;
            SetStatus(UpdateStatus.Checking);
            var update = await _manager.CheckForUpdatesAsync();
            LastCheckedAt = checkedAt;

            if (update is null)
            {
                _pendingUpdate = null;
                AvailableVersion = null;
                DownloadProgress = 0;
                ClearBlockingProcesses();
                ClearReleaseMetadata();
                SetStatus(UpdateStatus.UpToDate);
            }
            else
            {
                _pendingUpdate = update;
                AvailableVersion = update.TargetFullRelease.Version.ToString();
                DownloadProgress = 0;
                ClearBlockingProcesses();
                ApplyAssetMetadata(update.TargetFullRelease);

                var releaseMetadata = await TryGetReleaseMetadataAsync(AvailableVersion);
                ApplyReleaseMetadata(releaseMetadata);

                SetStatus(UpdateStatus.UpdateAvailable);
            }
        }
        catch (Exception ex)
        {
            LastCheckedAt = checkedAt;
            Trace.TraceWarning($"[UpdateService] Update check failed: {ex.Message}");
            ErrorMessage = ex.Message;
            SetStatus(UpdateStatus.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DownloadUpdateAsync()
    {
#if DEBUG
        if (TryGetDebugSimulation(out _) && (_manager is null || !_manager.IsInstalled))
        {
            if (!await _gate.WaitAsync(0)) return;
            try
            {
                foreach (var progress in new[] { 8, 24, 47, 73, 91, 100 })
                {
                    DownloadProgress = progress;
                    SetStatus(UpdateStatus.Downloading);
                    await Task.Delay(120);
                }

                SetStatus(UpdateStatus.ReadyToRestart);
            }
            finally
            {
                _gate.Release();
            }
            return;
        }
#endif

        if (_manager is null) return;

        if (!await _gate.WaitAsync(0)) return;
        try
        {
            var update = _pendingUpdate;
            if (update is null) return;

            ErrorMessage = string.Empty;
            SetStatus(UpdateStatus.Downloading);
            ClearBlockingProcesses();
            await _manager.DownloadUpdatesAsync(
                update,
                progress =>
                {
                    DownloadProgress = progress;
                    SetStatus(UpdateStatus.Downloading);
                });
            SetStatus(UpdateStatus.ReadyToRestart);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[UpdateService] Update download failed: {ex.Message}");
            ErrorMessage = ex.Message;
            SetStatus(UpdateStatus.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplyUpdateAndRestartAsync()
    {
#if DEBUG
        if (TryGetDebugSimulation(out _) && (_manager is null || !_manager.IsInstalled))
        {
            if (!await _gate.WaitAsync(0))
                return;

            try
            {
                SetStatus(UpdateStatus.PreparingToRestart);
                if (TryGetDebugUpdateBlockers(out var simulatedBlockers))
                {
                    SetBlockingProcesses(simulatedBlockers);
                    SetStatus(UpdateStatus.BlockedByProcesses);
                    return;
                }

                CompleteDebugUpdate();
            }
            finally
            {
                _gate.Release();
            }
            return;
        }
#endif

        var update = _pendingUpdate;
        if (_manager is null || update is null)
            return;

        if (!await _gate.WaitAsync(0))
            return;

        try
        {
            ErrorMessage = string.Empty;
            BlockerErrorMessage = string.Empty;
            SetStatus(UpdateStatus.PreparingToRestart);

            var blockers = await _blockerService.FindBlockingProcessesAsync(_updateResourceRoots);
            if (blockers.Count > 0)
            {
                SetBlockingProcesses(blockers);
                SetStatus(UpdateStatus.BlockedByProcesses);
                return;
            }

            LaunchUpdateAndRequestShutdown(update);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[UpdateService] Failed to apply update: {ex.Message}");
            ErrorMessage = ex.Message;
            SetStatus(UpdateStatus.ReadyToRestart);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseBlockingProcessesAndRestartAsync()
    {
#if DEBUG
        if (TryGetDebugSimulation(out _) && (_manager is null || !_manager.IsInstalled))
        {
            if (!await _gate.WaitAsync(0))
                return;

            try
            {
                SetStatus(UpdateStatus.ClosingBlockingProcesses);
                await Task.Delay(180);
                CompleteDebugUpdate();
            }
            finally
            {
                _gate.Release();
            }
            return;
        }
#endif

        var update = _pendingUpdate;
        if (_manager is null || update is null || BlockingProcesses.Count == 0)
            return;

        if (!await _gate.WaitAsync(0))
            return;

        try
        {
            BlockerErrorMessage = string.Empty;
            SetStatus(UpdateStatus.ClosingBlockingProcesses);

            var closeResult = await _blockerService.CloseBlockingProcessesAsync(BlockingProcesses);
            var remainingBlockers = await _blockerService.FindBlockingProcessesAsync(_updateResourceRoots);
            SetBlockingProcesses(remainingBlockers);

            if (remainingBlockers.Count > 0)
            {
                if (closeResult.FailedProcessIds.Count > 0
                    || remainingBlockers.All(static process => !process.CanClose))
                {
                    BlockerErrorMessage = "Some processes could not be closed automatically.";
                }

                SetStatus(UpdateStatus.BlockedByProcesses);
                return;
            }

            LaunchUpdateAndRequestShutdown(update);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[UpdateService] Failed while closing update blockers: {ex.Message}");
            BlockerErrorMessage = ex.Message;
            SetStatus(UpdateStatus.BlockedByProcesses);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void CancelBlockedRestart()
    {
        if (CurrentStatus != UpdateStatus.BlockedByProcesses)
            return;

        ClearBlockingProcesses();
        SetStatus(UpdateStatus.ReadyToRestart);
    }

    public void Dispose()
    {
        _periodicTimer?.Dispose();
    }

    private void SetStatus(UpdateStatus status)
    {
        CurrentStatus = status;
        StatusChanged?.Invoke(status);
    }

    private void LaunchUpdateAndRequestShutdown(UpdateInfo update)
    {
        if (_manager is null)
            throw new InvalidOperationException("The update manager is not initialized.");
        if (RestartRequested is null)
            throw new InvalidOperationException("No application shutdown handler is registered.");

        _restartLauncher.Launch(_manager, update.TargetFullRelease);
        RestartRequested.Invoke();
    }

    private void SetBlockingProcesses(IReadOnlyList<UpdateBlockingProcess> processes)
    {
        BlockingProcesses = processes;
    }

    private void ClearBlockingProcesses()
    {
        BlockingProcesses = [];
        BlockerErrorMessage = string.Empty;
    }

    private static HttpClient CreateReleaseMetadataClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Lumi", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private void ApplyAssetMetadata(VelopackAsset asset)
    {
        ReleaseNotesMarkdown = asset.NotesMarkdown?.Trim() ?? string.Empty;
        ReleaseTitle = string.IsNullOrWhiteSpace(AvailableVersion)
            ? string.Empty
            : $"Lumi v{AvailableVersion}";
        ReleasePageUrl = string.IsNullOrWhiteSpace(AvailableVersion)
            ? ReleasesPageUrl
            : BuildReleasePageUrl(AvailableVersion);
        ReleasePublishedAt = null;
    }

    private void ApplyReleaseMetadata(GitHubReleaseMetadata? metadata)
    {
        if (metadata is null)
            return;

        if (!string.IsNullOrWhiteSpace(metadata.Name))
            ReleaseTitle = metadata.Name.Trim();

        if (string.IsNullOrWhiteSpace(ReleaseNotesMarkdown) && !string.IsNullOrWhiteSpace(metadata.Body))
            ReleaseNotesMarkdown = metadata.Body.Trim();

        if (!string.IsNullOrWhiteSpace(metadata.HtmlUrl))
            ReleasePageUrl = metadata.HtmlUrl.Trim();

        ReleasePublishedAt = metadata.PublishedAt;
    }

    private void ClearReleaseMetadata()
    {
        ReleaseNotesMarkdown = string.Empty;
        ReleaseTitle = string.Empty;
        ReleasePageUrl = ReleasesPageUrl;
        ReleasePublishedAt = null;
    }

    private static string BuildReleasePageUrl(string version)
        => $"{ReleasesPageUrl}/tag/v{version}";

    private static async Task<GitHubReleaseMetadata?> TryGetReleaseMetadataAsync(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        try
        {
            using var response = await ReleaseMetadataClient.GetAsync(
                $"https://api.github.com/repos/adirh3/Lumi/releases/tags/v{version}");

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);

            var root = document.RootElement;
            var name = root.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            var body = root.TryGetProperty("body", out var bodyElement)
                ? bodyElement.GetString()
                : null;
            var htmlUrl = root.TryGetProperty("html_url", out var htmlUrlElement)
                ? htmlUrlElement.GetString()
                : null;
            DateTimeOffset? publishedAt = root.TryGetProperty("published_at", out var publishedAtElement)
                && publishedAtElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(publishedAtElement.GetString(), out var parsedPublishedAt)
                    ? parsedPublishedAt
                    : null;

            return new GitHubReleaseMetadata(name, body, htmlUrl, publishedAt);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[UpdateService] Failed to load GitHub release metadata: {ex.Message}");
            return null;
        }
    }

#if DEBUG
    private void CompleteDebugUpdate()
    {
        _pendingUpdate = null;
        AvailableVersion = null;
        DownloadProgress = 0;
        ErrorMessage = string.Empty;
        ClearBlockingProcesses();
        ClearReleaseMetadata();
        SetStatus(UpdateStatus.UpToDate);
    }

    private void ApplyDebugSimulation(DebugUpdateSimulation simulation)
    {
        _pendingUpdate = null;
        AvailableVersion = simulation.Version;
        DownloadProgress = 0;
        ErrorMessage = string.Empty;
        ClearBlockingProcesses();
        LastCheckedAt = DateTimeOffset.Now;
        ReleaseNotesMarkdown = simulation.NotesMarkdown;
        ReleaseTitle = $"Lumi v{simulation.Version}";
        ReleasePageUrl = BuildReleasePageUrl(simulation.Version);
        ReleasePublishedAt = simulation.PublishedAt;
        SetStatus(UpdateStatus.UpdateAvailable);
    }

    private static bool TryGetDebugSimulation(out DebugUpdateSimulation simulation)
    {
        var version = Environment.GetEnvironmentVariable("LUMI_DEBUG_UPDATE_VERSION");
        if (string.IsNullOrWhiteSpace(version))
        {
            simulation = default;
            return false;
        }

        var notes = Environment.GetEnvironmentVariable("LUMI_DEBUG_UPDATE_NOTES");
        if (string.IsNullOrWhiteSpace(notes))
        {
            notes = "## What's new\n\n"
                + "- A clearer update callout across the app.\n"
                + "- Better guidance inside Settings > About.\n"
                + "- Rich release notes and a direct link to the GitHub release.";
        }

        var publishedAt = DateTimeOffset.TryParse(
            Environment.GetEnvironmentVariable("LUMI_DEBUG_UPDATE_PUBLISHED_AT"),
            out var parsedPublishedAt)
            ? parsedPublishedAt
            : DateTimeOffset.Now.AddDays(-2);

        simulation = new DebugUpdateSimulation(version.Trim(), notes.Trim(), publishedAt);
        return true;
    }

    private static bool TryGetDebugUpdateBlockers(out IReadOnlyList<UpdateBlockingProcess> blockers)
    {
        var rawBlockers = Environment.GetEnvironmentVariable("LUMI_DEBUG_UPDATE_BLOCKERS");
        if (string.IsNullOrWhiteSpace(rawBlockers))
        {
            blockers = [];
            return false;
        }

        var names = string.Equals(rawBlockers.Trim(), "1", StringComparison.Ordinal)
            ? ["PowerShell", "GitHub Copilot CLI"]
            : rawBlockers.Split(
                [';', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        blockers = names
            .Select((name, index) => new UpdateBlockingProcess(
                41000 + index,
                null,
                name,
                null,
                canClose: true,
                isSimulated: true))
            .ToArray();
        return blockers.Count > 0;
    }
#endif

    private sealed record GitHubReleaseMetadata(
        string? Name,
        string? Body,
        string? HtmlUrl,
        DateTimeOffset? PublishedAt);

#if DEBUG
    private readonly record struct DebugUpdateSimulation(
        string Version,
        string NotesMarkdown,
        DateTimeOffset? PublishedAt);
#endif
}

public enum UpdateStatus
{
    Idle,
    NotInstalled,
    Checking,
    UpToDate,
    UpdateAvailable,
    Downloading,
    ReadyToRestart,
    PreparingToRestart,
    BlockedByProcesses,
    ClosingBlockingProcesses,
    Error
}

internal interface IUpdateRestartLauncher
{
    void Launch(UpdateManager manager, VelopackAsset release);
}

internal sealed class VelopackUpdateRestartLauncher : IUpdateRestartLauncher
{
    public void Launch(UpdateManager manager, VelopackAsset release)
        => manager.WaitExitThenApplyUpdates(release);
}
