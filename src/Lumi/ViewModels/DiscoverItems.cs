using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services.Discover;
using Lumi.Services.Sharing;

namespace Lumi.ViewModels;

public enum DiscoverKind
{
    Plugin,
    McpServer,
}

/// <summary>A plugin or MCP server in Discover's gallery.</summary>
public sealed partial class DiscoverCard : ObservableObject
{
    private readonly Action<DiscoverCard> _open;
    private bool _avatarRequested;

    private DiscoverCard(DiscoverKind kind, string key, string title, Action<DiscoverCard> open)
    {
        Kind = kind;
        Key = key;
        Title = title;
        _open = open;
        OpenCommand = new RelayCommand(() => _open(this));
    }

    public static DiscoverCard ForPlugin(PluginListing listing, Action<DiscoverCard> open)
    {
        var card = new DiscoverCard(DiscoverKind.Plugin, PluginKey(listing.Name, listing.Marketplace), DisplayNames.Humanize(listing.Name), open);
        card.ApplyListing(listing);
        return card;
    }

    /// <summary>An installed plugin whose marketplace listing is not loaded (a direct install, or a removed source).</summary>
    public static DiscoverCard ForInstalledPlugin(PluginInstallation installation, Action<DiscoverCard> open)
        => new(DiscoverKind.Plugin, PluginKey(installation.Name, installation.Marketplace), DisplayNames.Humanize(installation.Name), open)
        {
            SourceLabel = string.IsNullOrEmpty(installation.Marketplace) ? Loc.Discover_DirectInstall : installation.Marketplace,
            Installation = installation,
        };

    public static DiscoverCard ForServer(McpRegistryServer server, string registryLabel, Action<DiscoverCard> open)
        => new(DiscoverKind.McpServer, ServerKey(server.Name), server.Title, open)
        {
            Server = server,
            Publisher = server.Publisher,
            Description = server.Description ?? "",
            Version = server.Version,
            Stars = server.Stars,
            SourceLabel = registryLabel,
            AvatarUrl = RemoteImageCache.Sized(server.AvatarUrl, 96)
                        ?? (GitHubRepoRef.TryParse(server.RepositoryUrl) is { } repository ? RemoteImageCache.GitHubAvatar(repository.Owner, 96) : null),
            Tags = server.HasRemote ? [Loc.Discover_Remote] : server.Packages.Select(static package => package.RegistryType).Distinct().Take(2).ToArray(),
        };

    /// <summary>A server added from a registry whose entry is not in the loaded results.</summary>
    public static DiscoverCard ForAddedServer(McpServer server, Action<DiscoverCard> open)
        => new(DiscoverKind.McpServer, ServerKey(server.CatalogId ?? server.Name), server.Name, open)
        {
            Description = server.Description,
            SourceLabel = server.CatalogId ?? "",
            AddedServer = server,
        };

    internal static string PluginKey(string name, string marketplace) => $"plugin:{name}@{marketplace}".ToLowerInvariant();

    internal static string ServerKey(string registryName) => $"mcp:{registryName}".ToLowerInvariant();

    public DiscoverKind Kind { get; }
    public bool IsPlugin => Kind == DiscoverKind.Plugin;
    public bool IsServer => Kind == DiscoverKind.McpServer;
    public string Key { get; }
    public string Title { get; }
    public string Initial => ManagementText.Initial(Title);
    public string? Publisher { get; private set; }
    public bool HasPublisher => !string.IsNullOrWhiteSpace(Publisher);
    public string PublisherLine => HasPublisher ? string.Format(CultureInfo.CurrentCulture, Loc.Discover_By, Publisher) : SourceLabel;
    public string Description { get; private set; } = "";
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public string? Version { get; private set; }
    public int? Stars { get; private init; }
    public string StarsText => Stars is { } stars ? FormatCount(stars) : "";
    public bool HasStars => Stars is > 0;
    public string SourceLabel { get; private set; } = "";
    public IReadOnlyList<string> Tags { get; private set; } = [];
    public bool HasTags => Tags.Count > 0;
    public string KindLabel => IsPlugin ? Loc.Discover_KindPlugin : Loc.Discover_KindServer;
    public string? AvatarUrl { get; private set; }

    public PluginListing? Listing { get; private set; }
    public McpRegistryServer? Server { get; private init; }

    /// <summary>The installed plugin this card stands for.</summary>
    [ObservableProperty] private PluginInstallation? _installation;

    /// <summary>The Lumi MCP server added from this registry entry.</summary>
    [ObservableProperty] private McpServer? _addedServer;

    [ObservableProperty] private Bitmap? _avatar;

    /// <summary>An add, update or removal is running for this card.</summary>
    [ObservableProperty] private bool _isBusy;

    public bool IsAdded => Installation is not null || AddedServer is not null;
    public bool HasAvatar => Avatar is not null;

    /// <summary>The marketplace lists a newer version than the one installed.</summary>
    public bool HasUpdate => DisplayNames.IsNewerVersion(Listing?.Version, Installation?.Version);

    public string VersionText => Installation?.Version ?? Version ?? "";
    public bool HasVersion => !string.IsNullOrWhiteSpace(VersionText);
    public string AddedLabel => IsPlugin ? Loc.Discover_Installed : Loc.Discover_Added;

    public IRelayCommand OpenCommand { get; }

    internal void ApplyListing(PluginListing listing)
    {
        Listing = listing;
        Publisher = listing.Author;
        Description = listing.Description ?? "";
        Version = listing.Version;
        SourceLabel = listing.Marketplace;
        Tags = listing.Keywords.Take(3).ToArray();
        AvatarUrl = PublisherAvatar(listing);
        OnPropertyChanged(string.Empty);
    }

    /// <summary>A plugin's manifest names its author when the marketplace entry did not.</summary>
    internal void ApplyAuthor(string? author)
    {
        if (HasPublisher || string.IsNullOrWhiteSpace(author))
            return;

        Publisher = author.Trim();
        OnPropertyChanged(nameof(Publisher));
        OnPropertyChanged(nameof(HasPublisher));
        OnPropertyChanged(nameof(PublisherLine));
    }

    internal void LoadAvatar()
    {
        if (_avatarRequested || AvatarUrl is null)
            return;

        _avatarRequested = true;
        _ = LoadAvatarAsync(AvatarUrl);
    }

    private async Task LoadAvatarAsync(string url)
    {
        var bitmap = await RemoteImageCache.LoadAsync(url, 96).ConfigureAwait(false);
        if (bitmap is not null)
            Dispatcher.UIThread.Post(() => Avatar = bitmap);
    }

    partial void OnInstallationChanged(PluginInstallation? value) => NotifyState();

    partial void OnAddedServerChanged(McpServer? value) => NotifyState();

    partial void OnAvatarChanged(Bitmap? value) => OnPropertyChanged(nameof(HasAvatar));

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsAdded));
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(HasVersion));
    }

    /// <summary>The author's GitHub avatar, or the repository owner's.</summary>
    private static string? PublisherAvatar(PluginListing listing)
    {
        if (listing.AuthorUrl is { } authorUrl
            && Uri.TryCreate(authorUrl, UriKind.Absolute, out var uri)
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Trim('/') is { Length: > 0 } user
            && !user.Contains('/'))
        {
            return RemoteImageCache.GitHubAvatar(user, 96);
        }

        var repository = GitHubRepoRef.TryParse(listing.Repository) ?? listing.SourceFolder;
        return repository is null || repository.FullName.Equals("github/awesome-copilot", StringComparison.OrdinalIgnoreCase)
            ? null
            : RemoteImageCache.GitHubAvatar(repository.Owner, 96);
    }

    internal static string FormatCount(int count) => count switch
    {
        >= 1_000_000 => Loc.Get(nameof(Loc.ModelPicker_CompactMillions), (count / 1_000_000d).ToString("0.#", CultureInfo.CurrentCulture)),
        >= 1_000 => Loc.Get(nameof(Loc.Discover_CompactThousands), count / 1_000d),
        _ => count.ToString(CultureInfo.CurrentCulture),
    };
}

/// <summary>A source row in Discover's sidebar: a plugin marketplace or an MCP registry.</summary>
public sealed partial class DiscoverSourceRow : ObservableObject
{
    public DiscoverSourceRow(string key, DiscoverKind kind, string title, Action<DiscoverSourceRow> select)
    {
        Key = key;
        Kind = kind;
        Title = title;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public string Key { get; }
    public DiscoverKind Kind { get; }
    public string Title { get; }
    public bool IsPluginSource => Kind == DiscoverKind.Plugin;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _countText = "";

    public IRelayCommand SelectCommand { get; }
}

/// <summary>A registered plugin marketplace on the Sources page.</summary>
public sealed class MarketplaceRow
{
    public MarketplaceRow(PluginMarketplace marketplace, int pluginCount, Action<MarketplaceRow> remove)
    {
        Marketplace = marketplace;
        PluginCountText = ManagementText.Count(pluginCount, Loc.Discover_PluginsOne, Loc.Discover_PluginsMany);
        RemoveCommand = new RelayCommand(() => remove(this), () => CanRemove);
    }

    public PluginMarketplace Marketplace { get; }
    public string Name => Marketplace.Name;
    public string Location => Marketplace.Repository?.FullName ?? Marketplace.Source;
    public string PluginCountText { get; }
    public bool IsDefault => Marketplace.IsDefault;
    public bool IsManaged => Marketplace.IsManaged;
    public bool CanRemove => !Marketplace.IsDefault && !Marketplace.IsManaged;
    public IRelayCommand RemoveCommand { get; }
}

/// <summary>A configured MCP registry on the Sources page.</summary>
public sealed class RegistryRow
{
    public RegistryRow(string url, Action<RegistryRow> remove)
    {
        Url = url;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Url { get; }
    public string Name => DiscoverViewModel.RegistryLabel(Url);
    public bool IsDefault => string.Equals(Url.TrimEnd('/'), UserSettings.DefaultMcpRegistryUrl, StringComparison.OrdinalIgnoreCase);
    public IRelayCommand RemoveCommand { get; }
}

/// <summary>A suggested source the user has not added yet.</summary>
public sealed class SourceSuggestion
{
    public SourceSuggestion(string value, string title, string caption, Func<SourceSuggestion, Task> add)
    {
        Value = value;
        Title = title;
        Caption = caption;
        AddCommand = new AsyncRelayCommand(() => add(this));
    }

    public string Value { get; }
    public string Title { get; }
    public string Caption { get; }
    public IAsyncRelayCommand AddCommand { get; }
}

/// <summary>A labelled list in a plugin's "What it adds", or a fact in a server's detail.</summary>
public sealed class DiscoverPart
{
    public DiscoverPart(string glyph, string title, IReadOnlyList<string> items, string? note = null)
    {
        Glyph = glyph;
        Title = title;
        Items = items;
        Note = note;
    }

    public string Glyph { get; }
    public string Title { get; }
    public IReadOnlyList<string> Items { get; }
    public string? Note { get; }
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
    public bool HasItems => Items.Count > 0;
}

/// <summary>Something an MCP server needs from the user before it works.</summary>
public sealed class DiscoverInput
{
    public DiscoverInput(McpRegistryInput input)
    {
        Name = input.Name;
        Description = input.Description ?? "";
        IsSecret = input.IsSecret;
    }

    public string Name { get; }
    public string Description { get; }
    public bool HasDescription => Description.Length > 0;
    public bool IsSecret { get; }
}

/// <summary>A navigation target after something was added: "See skills", "Open in Connectors".</summary>
public sealed class DiscoverLink
{
    public DiscoverLink(string label, Action action)
    {
        Label = label;
        Command = new RelayCommand(action);
    }

    public string Label { get; }
    public IRelayCommand Command { get; }
}
