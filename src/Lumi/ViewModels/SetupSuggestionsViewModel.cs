using System.Diagnostics;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Services;
using Lumi.Services.Capabilities;
using Lumi.Services.Discover;
using Lumi.Services.Onboarding;

namespace Lumi.ViewModels;

/// <summary>One well-known setup: what it brings, whether it is set up, and its single action.</summary>
public sealed partial class SetupSuggestionItem : ObservableObject
{
    private readonly SetupSuggestionsViewModel _owner;
    private IImage? _mark;

    internal SetupSuggestionItem(SetupScenario scenario, SetupSuggestionsViewModel owner)
    {
        Scenario = scenario;
        _owner = owner;
    }

    public SetupScenario Scenario { get; }
    public string Name => Loc.Get(Scenario.NameKey);
    public string Description => Loc.Get(Scenario.DescriptionKey);
    public IImage Mark => _mark ??= CreateMark(Scenario.Mark);

    internal PluginInstallation? Installation { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(Detail), nameof(ActionLabel), nameof(ShowAction))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(Detail), nameof(ActionLabel), nameof(ShowAction))]
    private bool _isEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionLabel))]
    [NotifyCanExecuteChangedFor(nameof(ActCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(Detail), nameof(ActionLabel))]
    private string? _error;

    /// <summary>What the plugin said when it was added, if anything.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private string? _note;

    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool IsReady => IsInstalled && IsEnabled;

    public string Detail => HasError ? Error!
        : IsReady ? Note ?? Loc.Setup_Ready
        : IsInstalled ? Loc.Setup_TurnedOff
        : Description;

    public string ActionLabel => IsBusy ? Loc.Setup_Working
        : IsReady ? Loc.Setup_Try
        : IsInstalled ? Loc.Setup_Manage
        : HasError ? Loc.Setup_Retry
        : Loc.Setup_SetUp;

    /// <summary>Welcome cannot open a chat, so a setup that is ready there only confirms itself.</summary>
    public bool ShowAction => !(IsReady && _owner.IsWelcome);

    public bool Matches(string? query)
        => string.IsNullOrWhiteSpace(query)
           || $"{Name} {Description} {Scenario.SearchTerms}".Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    internal void NotifyWelcomeChanged() => OnPropertyChanged(nameof(ShowAction));

    private bool CanAct() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task Act() => _owner.ActAsync(this);

    [RelayCommand]
    private Task Dismiss() => _owner.DismissAsync(this);

    private static DrawingImage CreateMark(IReadOnlyList<SetupMarkShape> shapes)
    {
        var drawing = new DrawingGroup();
        foreach (var shape in shapes)
        {
            drawing.Children.Add(new GeometryDrawing
            {
                Geometry = StreamGeometry.Parse(shape.Geometry),
                Brush = new SolidColorBrush(Color.Parse(shape.Color)),
            });
        }

        return new DrawingImage { Drawing = drawing };
    }
}

/// <summary>
/// Well-known setups (<see cref="SetupScenarios"/>) and the one Lumi suggests now: from the scan the user
/// allowed in welcome, or once per start from app names alone. A suggestion the user answered is not offered
/// again; every setup stays listed in Discover.
/// </summary>
public sealed partial class SetupSuggestionsViewModel : ObservableObject, IDisposable
{
    private readonly DataStore _dataStore;
    private readonly Func<CancellationToken, Task<IReadOnlyList<PluginInstallation>>> _listInstalled;
    private readonly Func<SetupScenario, CancellationToken, Task<PluginInstallOutcome>> _install;
    private readonly Func<CapabilitySnapshot?> _capabilities;
    private readonly IWorkspaceDiscovery _discovery;
    private readonly CancellationTokenSource _lifetime = new();
    private Task<bool>? _refresh;
    private bool _startupChecked;
    private bool _disposed;

    public SetupSuggestionsViewModel(
        DataStore dataStore,
        CopilotService copilot,
        PluginCatalogService plugins,
        Func<CapabilitySnapshot?> capabilities)
        : this(
            dataStore,
            async ct =>
            {
                await copilot.ConnectAsync(ct);
                return await plugins.ListInstalledAsync(ct);
            },
            (scenario, ct) => InstallAsync(plugins, scenario.Plugin, ct),
            capabilities)
    {
    }

    internal SetupSuggestionsViewModel(
        DataStore dataStore,
        Func<CancellationToken, Task<IReadOnlyList<PluginInstallation>>> listInstalled,
        Func<SetupScenario, CancellationToken, Task<PluginInstallOutcome>> install,
        Func<CapabilitySnapshot?>? capabilities = null,
        IWorkspaceDiscovery? discovery = null,
        IReadOnlyList<SetupScenario>? scenarios = null)
    {
        _dataStore = dataStore;
        _listInstalled = listInstalled;
        _install = install;
        _capabilities = capabilities ?? (static () => null);
        _discovery = discovery ?? new WorkspaceDiscovery();
        Items = (scenarios ?? SetupScenarios.All).Select(scenario => new SetupSuggestionItem(scenario, this)).ToList();
    }

    public IReadOnlyList<SetupSuggestionItem> Items { get; }

    /// <summary>The setup suggested now, shown on the new-chat screen and in welcome.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrent))]
    private SetupSuggestionItem? _current;

    public bool HasCurrent => Current is not null;

    [ObservableProperty] private bool _isWelcome;

    /// <summary>"Try it": open a new chat with this prompt ready to review. Nothing is sent.</summary>
    public event Action<string>? TryRequested;

    /// <summary>A setup that is already there but turned off: show where to turn it on.</summary>
    public event Action<SetupSuggestionItem>? ManageRequested;

    partial void OnIsWelcomeChanged(bool value)
    {
        foreach (var item in Items)
            item.NotifyWelcomeChanged();
    }

    public Task RefreshAsync() => _disposed ? Task.CompletedTask : RefreshInstalledAsync();

    private Task<bool> RefreshInstalledAsync()
        => _refresh is { IsCompleted: false } running ? running : _refresh = RefreshCoreAsync();

    private async Task<bool> RefreshCoreAsync()
    {
        try
        {
            var installed = await _listInstalled(_lifetime.Token);
            var connectors = _capabilities()?.McpServers ?? [];
            foreach (var item in Items)
            {
                var plugin = item.Scenario.Plugin;
                item.Installation = installed.FirstOrDefault(candidate => plugin.Matches(candidate.Name));
                // A connector added another way (by hand, or from an older plugin) counts as set up too.
                var connector = connectors.FirstOrDefault(candidate =>
                    plugin.Matches(candidate.PluginName) || plugin.Matches(candidate.Name));
                item.IsInstalled = item.Installation is not null || connector is not null;
                item.IsEnabled = item.Installation?.Enabled ?? connector?.IsEnabled ?? true;
            }

            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("[SetupSuggestions] Couldn't check installed plugins: {0}", ex);
            return false;
        }
    }

    /// <summary>Once per start: suggest a setup the user's app names point to. Never reads sites or files.</summary>
    public async Task CheckAtStartupAsync()
    {
        if (_disposed || _startupChecked)
            return;

        _startupChecked = true;
        if (!await RefreshInstalledAsync() || _disposed
            || !_dataStore.Data.Settings.AllowAppSetupSuggestions || !Items.Any(IsOpen))
            return;

        try
        {
            Suggest(await _discovery.FindAppsAsync(_lifetime.Token), []);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("[SetupSuggestions] Couldn't read app names: {0}", ex);
        }
    }

    /// <summary>Welcome's scan, limited to the sources the user allowed.</summary>
    public void ConsiderWorkspace(WorkspaceSnapshot workspace) => Suggest(workspace.Apps, workspace.Sites);

    /// <summary>Startup reads app names only while the user lets welcome read them.</summary>
    public void RememberAppDiscoveryChoice(bool allowed)
    {
        _dataStore.Data.Settings.AllowAppSetupSuggestions = allowed;
        _dataStore.Save();
    }

    private bool IsOpen(SetupSuggestionItem item)
        => !item.IsInstalled && !_dataStore.Data.Settings.HandledSetupSuggestions.Contains(item.Scenario.Id);

    private void Suggest(IReadOnlyCollection<string> apps, IReadOnlyCollection<string> sites)
    {
        // Keep a setup the user is working through on screen.
        if (_disposed || Current is { IsBusy: true } or { IsInstalled: true })
            return;

        Current = Items.FirstOrDefault(item => IsOpen(item) && item.Scenario.Matches(apps, sites));
    }

    internal async Task ActAsync(SetupSuggestionItem item)
    {
        if (_disposed)
            return;

        if (item.IsReady)
        {
            if (IsWelcome)
                return;
            if (ReferenceEquals(Current, item))
                Current = null;
            TryRequested?.Invoke(Loc.Get(item.Scenario.PromptKey));
            return;
        }

        if (item.IsInstalled)
        {
            // Lumi never turns on something the user turned off.
            ManageRequested?.Invoke(item);
            return;
        }

        item.IsBusy = true;
        item.Error = null;
        try
        {
            await RememberAsync(item);
            if (await RefreshInstalledAsync() && item.IsInstalled)
                return;

            var outcome = await _install(item.Scenario, _lifetime.Token);
            item.Installation = outcome.Plugin ?? throw new InvalidOperationException(Loc.Setup_NotConfirmed);
            var note = string.Join(" ", new[] { outcome.Warning, outcome.PostInstallMessage }
                .Where(static text => !string.IsNullOrWhiteSpace(text)));
            item.Note = note.Length > 0 ? note : null;
            item.IsEnabled = item.Installation.Enabled;
            item.IsInstalled = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("[SetupSuggestions] Setting up {0} failed: {1}", item.Scenario.Id, ex);
            item.Error = string.Format(Loc.Culture, Loc.Setup_Failed, ex.Message);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    internal async Task DismissAsync(SetupSuggestionItem item)
    {
        if (ReferenceEquals(Current, item))
            Current = null;

        try
        {
            await RememberAsync(item);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("[SetupSuggestions] Couldn't save the answer to {0}: {1}", item.Scenario.Id, ex);
        }
    }

    private Task RememberAsync(SetupSuggestionItem item)
    {
        var handled = _dataStore.Data.Settings.HandledSetupSuggestions;
        if (handled.Contains(item.Scenario.Id))
            return Task.CompletedTask;

        handled.Add(item.Scenario.Id);
        return _dataStore.SaveAsync(_lifetime.Token);
    }

    private static async Task<PluginInstallOutcome> InstallAsync(
        PluginCatalogService plugins, SetupPlugin plugin, CancellationToken ct)
    {
        var existing = (await plugins.ListInstalledAsync(ct)).FirstOrDefault(installed => plugin.Matches(installed.Name));
        if (existing is not null)
            return new PluginInstallOutcome(existing, null, null);

        bool IsSource(PluginMarketplace marketplace)
            => marketplace.Repository is { } repo
               && $"{repo.Owner}/{repo.Repo}".Equals(plugin.Marketplace, StringComparison.OrdinalIgnoreCase);

        var source = (await plugins.ListMarketplacesAsync(ct)).FirstOrDefault(IsSource);
        if (source is null)
        {
            await plugins.AddMarketplaceAsync(plugin.Marketplace, ct);
            source = (await plugins.ListMarketplacesAsync(ct)).FirstOrDefault(IsSource)
                ?? throw new InvalidOperationException(Loc.Setup_SourceUnavailable);
        }

        var listing = (await plugins.BrowseAsync(source, ct))
            .FirstOrDefault(candidate => candidate.Name.Equals(plugin.Name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(Loc.Setup_PluginUnavailable);
        return await plugins.InstallAsync(listing, ct);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
