using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Capabilities;
using Lumi.Services.Discover;

namespace Lumi.ViewModels;

public sealed partial class DiscoverViewModel
{
    private const int ReadmePreviewLength = 3500;
    private int _detailVersion;

    [ObservableProperty] private DiscoverCard? _openCard;

    [ObservableProperty] private bool _isInspecting;

    /// <summary>The plugin cannot be previewed before it is added (its files are not on GitHub).</summary>
    [ObservableProperty] private bool _isPreviewUnavailable;

    [ObservableProperty] private bool _hasHooks;
    [ObservableProperty] private Bitmap? _detailLogo;

    /// <summary>What just happened: "Added — ready in new chats".</summary>
    [ObservableProperty] private string? _resultMessage;

    /// <summary>Setup instructions a plugin shows after it is installed.</summary>
    [ObservableProperty] private string? _postInstallMessage;

    [ObservableProperty] private string? _actionError;
    [ObservableProperty] private bool _isConfirmingRemove;
    [ObservableProperty] private McpInstallPlan? _plan;
    [ObservableProperty] private bool _isReadmeExpanded;

    /// <summary>What a plugin adds, by kind.</summary>
    public ObservableCollection<DiscoverPart> Parts { get; } = [];

    /// <summary>Where to go after something was added.</summary>
    public ObservableCollection<DiscoverLink> NextSteps { get; } = [];

    /// <summary>What an MCP server needs from the user.</summary>
    public ObservableCollection<DiscoverInput> RequiredInputs { get; } = [];

    public bool IsViewingDetail => OpenCard is not null;
    public bool IsPluginDetail => OpenCard?.IsPlugin == true;
    public bool IsServerDetail => OpenCard?.IsServer == true;
    public bool IsDetailAdded => OpenCard?.IsAdded == true;
    public bool HasAddedServer => OpenCard?.AddedServer is not null;
    public bool IsDetailBusy => OpenCard?.IsBusy == true;
    public bool CanAdd => OpenCard is { IsAdded: false, IsBusy: false } card && (card.IsPlugin ? card.Listing is not null : Plan is not null);
    public bool CanUpdate => OpenCard is { IsPlugin: true, HasUpdate: true, IsBusy: false };
    public bool CanRemove => OpenCard is { IsAdded: true, IsBusy: false } card && card.Installation?.IsManaged != true;
    public bool HasParts => Parts.Count > 0;
    public bool HasNextSteps => NextSteps.Count > 0;
    public bool HasResult => !string.IsNullOrWhiteSpace(ResultMessage);
    public bool HasPostInstallMessage => !string.IsNullOrWhiteSpace(PostInstallMessage);
    public bool HasActionError => !string.IsNullOrWhiteSpace(ActionError);
    public bool HasRequiredInputs => RequiredInputs.Count > 0;
    public bool HasDetailLogo => DetailLogo is not null;
    public bool ShowsNothingToPreview => IsPluginDetail && !IsInspecting && !IsPreviewUnavailable && Parts.Count == 0 && !HasHooks;

    public string DetailMeta
    {
        get
        {
            if (OpenCard is not { } card)
                return "";

            var parts = new List<string>();
            if (card.HasVersion)
                parts.Add("v" + card.VersionText.TrimStart('v', 'V'));
            if (card.HasStars)
                parts.Add("★ " + card.StarsText);
            if ((card.Server?.License ?? card.Listing?.License) is { Length: > 0 } license)
                parts.Add(license);
            // Without a publisher the source already reads as the byline.
            if (card.SourceLabel.Length > 0 && card.HasPublisher)
                parts.Add(card.SourceLabel);
            return string.Join("  ·  ", parts);
        }
    }

    public string AddButtonText => IsServerDetail ? Loc.Discover_AddServer : Loc.Discover_AddPlugin;
    public string RemoveButtonText => IsServerDetail ? Loc.Discover_RemoveServer : Loc.Discover_RemovePlugin;

    public string RemoveConfirmText => OpenCard is not { } card
        ? ""
        : string.Format(CultureInfo.CurrentCulture, card.IsPlugin ? Loc.Discover_RemovePluginConfirm : Loc.Discover_RemoveServerConfirm, card.Title);

    public string UpdateButtonText => OpenCard?.Listing?.Version is { Length: > 0 } version
        ? string.Format(CultureInfo.CurrentCulture, Loc.Discover_UpdateTo, version.TrimStart('v', 'V'))
        : Loc.Discover_Update;

    public string? HomepageUrl => OpenCard?.Listing?.Homepage ?? OpenCard?.Server?.WebsiteUrl;
    public string? RepositoryUrl => OpenCard?.Listing?.Repository
                                    ?? OpenCard?.Server?.RepositoryUrl
                                    ?? OpenCard?.Listing?.SourceFolder?.WebUrl;
    public bool HasHomepage => !string.IsNullOrWhiteSpace(HomepageUrl) && !string.Equals(HomepageUrl, RepositoryUrl, StringComparison.OrdinalIgnoreCase);
    public bool HasRepository => !string.IsNullOrWhiteSpace(RepositoryUrl);

    /// <summary>The server the page describes: the one adding it would create, else the one already added.</summary>
    private McpServer? RunsServer => Plan?.Server ?? OpenCard?.AddedServer;

    /// <summary>"Hosted by api.example.com" or "Runs on this PC".</summary>
    public string RunsTitle => RunsServer is not { } server
        ? Loc.Discover_CannotRun
        : server.ServerType == "remote"
            ? string.Format(CultureInfo.CurrentCulture, Loc.Discover_RunsRemote, Uri.TryCreate(server.Url, UriKind.Absolute, out var uri) ? uri.Host : server.Url)
            : string.Format(CultureInfo.CurrentCulture, Loc.Discover_RunsLocal, RuntimeName(Plan?.RuntimeCommand ?? Path.GetFileNameWithoutExtension(server.Command)));

    public string RunsCommand => RunsServer is not { } server
        ? ""
        : server.ServerType == "remote"
            ? server.Url
            : string.Join(' ', new[] { server.Command }.Concat(server.Args.Select(static arg => arg.Contains(' ') ? $"\"{arg}\"" : arg)));

    public bool HasRunsCommand => RunsCommand.Length > 0;
    public bool IsRemotePlan => RunsServer?.ServerType == "remote";

    /// <summary>The package runner the server needs is not installed.</summary>
    public string? RuntimeWarning => Plan is { RuntimeCommand: { } command } && !CommandLocator.IsAvailable(command)
        ? string.Format(CultureInfo.CurrentCulture, Loc.Discover_RuntimeMissing, RuntimeName(command))
        : null;

    public bool HasRuntimeWarning => RuntimeWarning is not null;

    public string ReadmeMarkdown => OpenCard?.Server?.Readme is { Length: > 0 } readme ? CleanReadme(readme, IsReadmeExpanded ? int.MaxValue : ReadmePreviewLength) : "";
    public bool HasReadme => ReadmeMarkdown.Length > 0;
    public bool IsReadmeLong => (OpenCard?.Server?.Readme?.Length ?? 0) > ReadmePreviewLength;
    public string ReadmeToggleText => IsReadmeExpanded ? Loc.Mg_ShowLess : Loc.Mg_ShowMore;

    private void OpenCardFromGallery(DiscoverCard card) => OpenDetail(card);

    /// <summary>Opens a plugin's page by name — used by "Manage in Discover" from the other sections.</summary>
    public async Task OpenInstalledPluginAsync(string pluginName)
    {
        await EnsureLoadedAsync();
        var installation = _installed.FirstOrDefault(plugin => plugin.Name.Equals(pluginName, StringComparison.OrdinalIgnoreCase));
        var card = _pluginCards.FirstOrDefault(candidate => candidate.Installation is not null
                                                            && candidate.Listing?.Name.Equals(pluginName, StringComparison.OrdinalIgnoreCase) == true)
                   ?? _pluginCards.FirstOrDefault(candidate => candidate.Listing?.Name.Equals(pluginName, StringComparison.OrdinalIgnoreCase) == true)
                   ?? (installation is null ? null : InstalledPluginCard(installation));
        if (card is null)
        {
            SetFilter(DiscoverFilter.Installed, null);
            return;
        }

        if (SearchQuery.Length > 0)
            SearchQuery = "";
        SetFilter(DiscoverFilter.Installed, null);
        OpenDetail(card);
    }

    private void OpenDetail(DiscoverCard card)
    {
        var version = ++_detailVersion;
        OpenCard = card;
        IsConfirmingRemove = false;
        ActionError = null;
        ResultMessage = null;
        PostInstallMessage = null;
        IsReadmeExpanded = false;
        DetailLogo = null;
        HasHooks = false;
        IsInspecting = false;
        IsPreviewUnavailable = false;
        Parts.Clear();
        NextSteps.Clear();
        RequiredInputs.Clear();
        Plan = null;
        card.LoadAvatar();

        if (card.IsServer && card.Server is { } server)
        {
            Plan = McpRegistryMapper.Plan(server);
            foreach (var input in Plan?.RequiredInputs ?? [])
                RequiredInputs.Add(new DiscoverInput(input));
        }
        else if (card.IsPlugin)
        {
            if (card.Installation is not null)
                ShowInstalledParts(card);
            else
                _ = InspectAsync(card, version);
        }

        NotifyDetail();
    }

    [RelayCommand]
    private void CloseDetail()
    {
        _detailVersion++;
        OpenCard = null;
        IsConfirmingRemove = false;
        Parts.Clear();
        NextSteps.Clear();
        RequiredInputs.Clear();
        NotifyDetail();
    }

    private async Task InspectAsync(DiscoverCard card, int version)
    {
        if (card.Listing is not { } listing)
            return;

        if (listing.SourceFolder is null)
        {
            IsPreviewUnavailable = true;
            NotifyDetail();
            return;
        }

        IsInspecting = true;
        NotifyDetail();
        try
        {
            var contents = await _plugins.InspectAsync(listing, CancellationToken.None);
            if (version != _detailVersion)
                return;

            ShowContents(contents);
            card.ApplyAuthor(contents.Author);
            NotifyDetail();
            if (contents.LogoUrl is { } logo)
            {
                var bitmap = await RemoteImageCache.LoadAsync(logo, 128);
                if (version == _detailVersion)
                    DetailLogo = bitmap;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Discover] Preview of '{listing.Name}' failed: {ex.Message}");
            if (version == _detailVersion)
                IsPreviewUnavailable = true;
        }
        finally
        {
            if (version == _detailVersion)
            {
                IsInspecting = false;
                NotifyDetail();
            }
        }
    }

    private void ShowContents(PluginContents contents)
    {
        Parts.Clear();
        if (contents.Skills.Count > 0)
            Parts.Add(new DiscoverPart("⚡", ManagementText.Count(contents.Skills.Count, Loc.External_SkillsOne, Loc.External_SkillsMany), contents.Skills));
        if (contents.Agents.Count > 0)
            Parts.Add(new DiscoverPart("🤖", ManagementText.Count(contents.Agents.Count, Loc.External_LumisOne, Loc.External_LumisMany), contents.Agents));
        if (contents.McpServers.Count > 0)
        {
            Parts.Add(new DiscoverPart(
                "🔌",
                ManagementText.Count(contents.McpServers.Count, Loc.External_ServersOne, Loc.External_ServersMany),
                contents.McpServers.Select(static server => $"{server.Name}  —  {server.Summary}").ToArray(),
                Loc.Discover_PluginServersNote));
        }

        HasHooks = contents.HasHooks;
        if (contents.LogoUrl is null)
            DetailLogo = null;
        NotifyDetail();
    }

    /// <summary>An installed plugin's parts, as Lumi actually sees them.</summary>
    private void ShowInstalledParts(DiscoverCard card)
    {
        if (card.Installation is not { } installation || _capabilities() is not { } snapshot)
            return;

        var parts = snapshot.External
            .Where(capability => string.Equals(capability.PluginName, installation.Name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var skills = parts.Where(static part => part.Kind == CapabilityKind.Skill).Select(ExternalCapabilityItem.DisplayName).OrderBy(static name => name).ToArray();
        var agents = parts.Where(static part => part.Kind == CapabilityKind.Agent).Select(ExternalCapabilityItem.DisplayName).OrderBy(static name => name).ToArray();
        var servers = parts.Where(static part => part.Kind == CapabilityKind.McpServer).Select(ExternalCapabilityItem.DisplayName).OrderBy(static name => name).ToArray();

        Parts.Clear();
        NextSteps.Clear();
        if (skills.Length > 0)
        {
            Parts.Add(new DiscoverPart("⚡", ManagementText.Count(skills.Length, Loc.External_SkillsOne, Loc.External_SkillsMany), skills));
            NextSteps.Add(new DiscoverLink(Loc.Discover_SeeSkills, () => ShowPluginPartsRequested?.Invoke(installation.Name, CapabilityKind.Skill)));
        }

        if (agents.Length > 0)
        {
            Parts.Add(new DiscoverPart("🤖", ManagementText.Count(agents.Length, Loc.External_LumisOne, Loc.External_LumisMany), agents));
            NextSteps.Add(new DiscoverLink(Loc.Discover_SeeLumis, () => ShowPluginPartsRequested?.Invoke(installation.Name, CapabilityKind.Agent)));
        }

        if (servers.Length > 0)
        {
            Parts.Add(new DiscoverPart("🔌", ManagementText.Count(servers.Length, Loc.External_ServersOne, Loc.External_ServersMany), servers, Loc.Discover_PluginServersNote));
            NextSteps.Add(new DiscoverLink(Loc.Discover_SeeServers, () => ShowPluginPartsRequested?.Invoke(installation.Name, CapabilityKind.McpServer)));
        }

        NotifyDetail();
    }


    [RelayCommand]
    private async Task AddAsync()
    {
        if (OpenCard is not { } card || card.IsBusy || card.IsAdded)
            return;

        if (card.IsPlugin)
            await AddPluginAsync(card);
        else
            AddServer(card);
    }

    private async Task AddPluginAsync(DiscoverCard card)
    {
        if (card.Listing is not { } listing)
            return;

        if (!_plugins.IsAvailable)
        {
            ActionError = Loc.Discover_WaitingForCopilot;
            return;
        }

        card.IsBusy = true;
        ActionError = null;
        NotifyDetail();
        try
        {
            var outcome = await _plugins.InstallAsync(listing, CancellationToken.None);
            var installation = outcome.Plugin
                               ?? new PluginInstallation(listing.Name, listing.Marketplace, listing.Version, true, null, false);
            _installed.RemoveAll(plugin => DiscoverCard.PluginKey(plugin.Name, plugin.Marketplace) == card.Key);
            _installed.Add(installation);
            card.Installation = installation;
            Toast = string.Format(CultureInfo.CurrentCulture, Loc.Discover_AddedToast, card.Title);
            if (IsOpen(card))
            {
                ResultMessage = Loc.Discover_PluginAdded;
                PostInstallMessage = outcome.PostInstallMessage;
                if (!string.IsNullOrWhiteSpace(outcome.Warning))
                    ActionError = outcome.Warning;
                ShowInstalledParts(card);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Discover] Installing '{listing.Spec}' failed: {ex}");
            if (IsOpen(card))
                ActionError = string.Format(CultureInfo.CurrentCulture, Loc.Discover_AddFailed, Describe(ex));
        }
        finally
        {
            card.IsBusy = false;
            RefreshView();
            NotifyDetail();
        }
    }

    /// <summary>
    /// The page still shows this card. Adding, updating or removing takes a while, and the user can
    /// open another item meanwhile; only the card's own state may change then, not that item's page.
    /// </summary>
    private bool IsOpen(DiscoverCard card) => ReferenceEquals(OpenCard, card);

    private void AddServer(DiscoverCard card)
    {
        if (card.Server is not { } entry || McpRegistryMapper.Plan(entry) is not { } plan)
        {
            ActionError = Loc.Discover_CannotRun;
            return;
        }

        var server = plan.Server;
        server.Name = ExternalCapabilityCopier.UniqueName(server.Name, _dataStore.Data.McpServers.Select(static existing => existing.Name));
        _dataStore.Data.McpServers.Add(server);
        _ = _dataStore.SaveAsync();
        card.AddedServer = server;
        ActionError = null;
        Toast = string.Format(CultureInfo.CurrentCulture, Loc.Discover_AddedToast, card.Title);
        McpServersChanged?.Invoke();

        if (plan.NeedsSetup)
        {
            OpenMcpServerRequested?.Invoke(server.Id, true);
            return;
        }

        // "Open in Connectors" sits with the other actions while the server is added.
        ResultMessage = Loc.Discover_ServerAdded;
        RefreshView();
        NotifyDetail();
    }

    [RelayCommand]
    private async Task UpdateAsync()
    {
        if (OpenCard is not { Installation: { } installation } card || card.IsBusy)
            return;

        card.IsBusy = true;
        ActionError = null;
        NotifyDetail();
        try
        {
            var outcome = await _plugins.UpdateAsync(installation, CancellationToken.None);
            var version = outcome.NewVersion ?? card.Listing?.Version ?? installation.Version;
            card.Installation = installation with { Version = version };
            _installed.RemoveAll(plugin => DiscoverCard.PluginKey(plugin.Name, plugin.Marketplace) == card.Key);
            _installed.Add(card.Installation);
            if (IsOpen(card))
            {
                ResultMessage = string.Equals(outcome.PreviousVersion, outcome.NewVersion, StringComparison.OrdinalIgnoreCase)
                    ? Loc.Discover_UpToDate
                    : string.Format(CultureInfo.CurrentCulture, Loc.Discover_Updated, version);
            }
        }
        catch (Exception ex)
        {
            if (IsOpen(card))
                ActionError = string.Format(CultureInfo.CurrentCulture, Loc.Discover_UpdateFailed, Describe(ex));
        }
        finally
        {
            card.IsBusy = false;
            RefreshView();
            NotifyDetail();
        }
    }

    [RelayCommand]
    private void RequestRemove()
    {
        if (CanRemove)
            IsConfirmingRemove = true;
    }

    [RelayCommand]
    private void CancelRemove() => IsConfirmingRemove = false;

    [RelayCommand]
    private async Task ConfirmRemoveAsync()
    {
        IsConfirmingRemove = false;
        if (OpenCard is not { } card || card.IsBusy)
            return;

        if (card.IsServer)
        {
            if (card.AddedServer is { } server)
            {
                new LumiFeatureManager(_dataStore).ManageMcps("delete", identifier: server.Id.ToString());
                _ = _dataStore.SaveAsync();
                card.AddedServer = null;
                ResultMessage = null;
                NextSteps.Clear();
                Toast = string.Format(CultureInfo.CurrentCulture, Loc.Discover_RemovedToast, card.Title);
                McpServersChanged?.Invoke();
                RefreshView();
                NotifyDetail();
            }

            return;
        }

        if (card.Installation is not { } installation)
            return;

        card.IsBusy = true;
        ActionError = null;
        NotifyDetail();
        try
        {
            await _plugins.UninstallAsync(installation, CancellationToken.None);
            _installed.RemoveAll(plugin => DiscoverCard.PluginKey(plugin.Name, plugin.Marketplace) == card.Key);
            card.Installation = null;
            Toast = string.Format(CultureInfo.CurrentCulture, Loc.Discover_RemovedToast, card.Title);
            if (IsOpen(card))
            {
                ResultMessage = null;
                PostInstallMessage = null;
                NextSteps.Clear();
                if (card.Listing is not null)
                    _ = InspectAsync(card, _detailVersion);
                else
                    Parts.Clear();
            }
        }
        catch (Exception ex)
        {
            if (IsOpen(card))
                ActionError = string.Format(CultureInfo.CurrentCulture, Loc.Discover_RemoveFailed, Describe(ex));
        }
        finally
        {
            card.IsBusy = false;
            RefreshView();
            NotifyDetail();
        }
    }

    [RelayCommand]
    private void OpenAddedServer()
    {
        if (OpenCard?.AddedServer is { } server)
            OpenMcpServerRequested?.Invoke(server.Id, false);
    }

    [RelayCommand]
    private void OpenHomepage() => OpenUrl(HomepageUrl);

    [RelayCommand]
    private void OpenRepository() => OpenUrl(RepositoryUrl);

    [RelayCommand]
    private void ToggleReadme() => IsReadmeExpanded = !IsReadmeExpanded;

    partial void OnIsReadmeExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ReadmeMarkdown));
        OnPropertyChanged(nameof(ReadmeToggleText));
    }

    partial void OnOpenCardChanged(DiscoverCard? value) => NotifyDetail();
    partial void OnIsConfirmingRemoveChanged(bool value) => OnPropertyChanged(nameof(RemoveConfirmText));
    partial void OnResultMessageChanged(string? value) => OnPropertyChanged(nameof(HasResult));
    partial void OnPostInstallMessageChanged(string? value) => OnPropertyChanged(nameof(HasPostInstallMessage));
    partial void OnActionErrorChanged(string? value) => OnPropertyChanged(nameof(HasActionError));
    partial void OnDetailLogoChanged(Bitmap? value) => OnPropertyChanged(nameof(HasDetailLogo));
    partial void OnPlanChanged(McpInstallPlan? value) => NotifyDetail();

    private void NotifyDetail()
    {
        OnPropertyChanged(nameof(IsViewingDetail));
        OnPropertyChanged(nameof(ShowGallery));
        OnPropertyChanged(nameof(IsPluginDetail));
        OnPropertyChanged(nameof(IsServerDetail));
        OnPropertyChanged(nameof(IsDetailAdded));
        OnPropertyChanged(nameof(HasAddedServer));
        OnPropertyChanged(nameof(IsDetailBusy));
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(HasParts));
        OnPropertyChanged(nameof(HasNextSteps));
        OnPropertyChanged(nameof(HasRequiredInputs));
        OnPropertyChanged(nameof(ShowsNothingToPreview));
        OnPropertyChanged(nameof(DetailMeta));
        OnPropertyChanged(nameof(AddButtonText));
        OnPropertyChanged(nameof(RemoveButtonText));
        OnPropertyChanged(nameof(RemoveConfirmText));
        OnPropertyChanged(nameof(UpdateButtonText));
        OnPropertyChanged(nameof(HomepageUrl));
        OnPropertyChanged(nameof(RepositoryUrl));
        OnPropertyChanged(nameof(HasHomepage));
        OnPropertyChanged(nameof(HasRepository));
        OnPropertyChanged(nameof(RunsTitle));
        OnPropertyChanged(nameof(RunsCommand));
        OnPropertyChanged(nameof(HasRunsCommand));
        OnPropertyChanged(nameof(IsRemotePlan));
        OnPropertyChanged(nameof(RuntimeWarning));
        OnPropertyChanged(nameof(HasRuntimeWarning));
        OnPropertyChanged(nameof(ReadmeMarkdown));
        OnPropertyChanged(nameof(HasReadme));
        OnPropertyChanged(nameof(IsReadmeLong));
        OnPropertyChanged(nameof(ReadmeToggleText));
    }

    private static string RuntimeName(string? command) => command switch
    {
        "npx" => "Node.js",
        "uvx" => "uv",
        "dnx" => ".NET 10",
        "docker" => "Docker",
        null => "",
        _ => command,
    };

    /// <summary>
    /// A README is written for GitHub's renderer: badges, inline HTML and images do not belong in a
    /// preview, so they are dropped and the text is cut at a paragraph near the limit.
    /// </summary>
    internal static string CleanReadme(string readme, int maxLength)
    {
        var text = Regex.Replace(readme, @"\[!\[[^\]]*\]\([^)]*\)\]\([^)]*\)|!\[[^\]]*\]\([^)]*\)", "");
        text = Regex.Replace(text, @"<!--.*?-->|<[^>]+>", "", RegexOptions.Singleline);
        text = Regex.Replace(text.Replace("\r\n", "\n"), @"\n{3,}", "\n\n").Trim();
        if (text.Length <= maxLength)
            return text;

        var cut = text.LastIndexOf("\n\n", maxLength, StringComparison.Ordinal);
        return (cut > maxLength / 2 ? text[..cut] : text[..maxLength]).TrimEnd() + "\n\n…";
    }

    private static void OpenUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"[Discover] Could not open '{uri}': {ex.Message}");
        }
    }
}
