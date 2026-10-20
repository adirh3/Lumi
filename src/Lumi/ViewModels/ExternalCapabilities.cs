using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Services;
using Lumi.Services.Capabilities;
using Lumi.Services.Sharing;
using StrataSearch;

namespace Lumi.ViewModels;

/// <summary>Where a read-only item comes from, in the order the groups are listed.</summary>
public enum ExternalSourceKind
{
    Plugin,
    Copilot,
    Folder,
    BuiltIn,
    Other,
}

/// <summary>A fact shown in a read-only item's detail.</summary>
public sealed record ExternalDetailFact(string Label, string Value, bool IsMono = false);

/// <summary>A skill, agent or MCP server Lumi can use but does not own.</summary>
public sealed partial class ExternalCapabilityItem : ObservableObject
{
    internal ExternalCapabilityItem(CapabilityDescriptor descriptor, ExternalCapabilityGroup group, Action<ExternalCapabilityItem> open)
    {
        Descriptor = descriptor;
        Group = group;
        Name = DisplayName(descriptor);
        Description = descriptor.Description?.Trim() ?? "";
        OpenCommand = new RelayCommand(() => open(this));
    }

    public CapabilityDescriptor Descriptor { get; }
    public ExternalCapabilityGroup Group { get; }
    public string Name { get; }
    public string Description { get; }
    public bool HasDescription => Description.Length > 0;
    public bool IsEnabled => Descriptor.IsEnabled;
    public string SourceLabel => Group.Title;
    public string Glyph => Descriptor.Kind switch
    {
        CapabilityKind.Agent => "🤖",
        CapabilityKind.McpServer => "🔌",
        _ => "⚡",
    };

    /// <summary>The open item in its page; drives the sidebar row's highlight.</summary>
    [ObservableProperty] private bool _isSelected;

    public IRelayCommand OpenCommand { get; }

    /// <summary>A plugin prefixes its parts' names ("azure:deploy"); people read the part's own name.</summary>
    internal static string DisplayName(CapabilityDescriptor descriptor)
    {
        var label = descriptor.Label;
        if (descriptor.PluginName is { Length: > 0 } plugin
            && label.StartsWith(plugin + ":", StringComparison.OrdinalIgnoreCase))
        {
            label = label[(plugin.Length + 1)..];
        }

        return label;
    }
}

/// <summary>Everything one plugin, or one Copilot location, contributes to a page.</summary>
public sealed partial class ExternalCapabilityGroup : ObservableObject
{
    /// <summary>Cards a page shows for a group before "Show all".</summary>
    public const int PreviewLimit = 6;

    private readonly CapabilityKind _kind;
    private List<ExternalCapabilityItem> _all = [];

    internal ExternalCapabilityGroup(string key, ExternalSourceKind source, string title, string? pluginName, CapabilityKind kind, Action<string> manage)
    {
        Key = key;
        Source = source;
        Title = title;
        PluginName = pluginName;
        _kind = kind;
        ManageCommand = new RelayCommand(() =>
        {
            if (PluginName is { } plugin)
                manage(plugin);
        });
    }

    public string Key { get; }
    public ExternalSourceKind Source { get; }
    public string Title { get; }
    public string? PluginName { get; }
    public bool IsPlugin => Source == ExternalSourceKind.Plugin;
    public string Glyph => Source switch
    {
        ExternalSourceKind.Plugin => "🧩",
        ExternalSourceKind.Copilot => "🐙",
        ExternalSourceKind.Folder => "📁",
        ExternalSourceKind.BuiltIn => "✨",
        _ => "🔗",
    };

    /// <summary>"Plugin · 3 skills".</summary>
    public string Caption => $"{SourceCaption} · {CountLabel}";

    public string SourceCaption => Source switch
    {
        ExternalSourceKind.Plugin => Loc.External_SourcePlugin,
        ExternalSourceKind.Copilot => Loc.External_SourceCopilot,
        ExternalSourceKind.Folder => Loc.External_SourceFolder,
        ExternalSourceKind.BuiltIn => Loc.External_SourceBuiltIn,
        _ => Loc.External_SourceOther,
    };

    public string CountLabel => _kind switch
    {
        CapabilityKind.Agent => ManagementText.Count(Items.Count, Loc.External_LumisOne, Loc.External_LumisMany),
        CapabilityKind.McpServer => ManagementText.Count(Items.Count, Loc.External_ServersOne, Loc.External_ServersMany),
        _ => ManagementText.Count(Items.Count, Loc.External_SkillsOne, Loc.External_SkillsMany),
    };

    public string CountText => Items.Count.ToString(CultureInfo.CurrentCulture);

    /// <summary>The group's items that match the page's search.</summary>
    public ObservableCollection<ExternalCapabilityItem> Items { get; } = [];

    /// <summary>The overview's cards: the first few unless the group is unfolded.</summary>
    public ObservableCollection<ExternalCapabilityItem> Cards { get; } = [];

    /// <summary>The sidebar shows the group's rows.</summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>The overview shows every card rather than the first few.</summary>
    [ObservableProperty] private bool _isShowingAll;

    public bool HasMore => Items.Count > PreviewLimit;
    public string ShowAllText => IsShowingAll
        ? Loc.Mg_ShowLess
        : string.Format(CultureInfo.CurrentCulture, Loc.External_ShowAll, Items.Count);

    public IRelayCommand ManageCommand { get; }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void ToggleShowAll() => IsShowingAll = !IsShowingAll;

    partial void OnIsShowingAllChanged(bool value)
    {
        RefreshCards();
        OnPropertyChanged(nameof(ShowAllText));
    }

    internal IReadOnlyList<ExternalCapabilityItem> All => _all;

    internal void SetAll(List<ExternalCapabilityItem> items) => _all = items;

    internal void ShowItems(IEnumerable<ExternalCapabilityItem> items)
    {
        Sync(Items, items as IReadOnlyList<ExternalCapabilityItem> ?? items.ToList());
        RefreshCards();
        OnPropertyChanged(nameof(Caption));
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(ShowAllText));
    }

    private void RefreshCards()
        => Sync(Cards, (IsShowingAll ? Items : Items.Take(PreviewLimit)).ToList());

    /// <summary>
    /// Makes <paramref name="target"/> list <paramref name="items"/> by removing, inserting and moving
    /// entries, so rows that stay keep their controls instead of being rebuilt.
    /// </summary>
    internal static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> items) where T : class
    {
        if (target.Count == items.Count && target.SequenceEqual(items))
            return;

        var wanted = new HashSet<T>(items, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(target[i]))
                target.RemoveAt(i);
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], items[i]))
                continue;

            var existing = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (ReferenceEquals(target[j], items[i]))
                {
                    existing = j;
                    break;
                }
            }

            if (existing >= 0)
                target.Move(existing, i);
            else
                target.Insert(i, items[i]);
        }
    }
}

/// <summary>
/// The read-only part of a Customize section: skills, Lumis (agents) or MCP servers that come
/// from installed plugins or from the user's GitHub Copilot folder. Lumi can use them in every
/// chat; they are changed where they come from, or copied to make an editable version.
/// </summary>
public sealed partial class ExternalCapabilitiesViewModel : ObservableObject
{
    private readonly HashSet<string> _expandedKeys = new(StringComparer.Ordinal);
    private readonly List<ExternalCapabilityGroup> _groups = [];
    private CapabilityDescriptor[]? _descriptors;
    private string _searchQuery = "";

    public ExternalCapabilitiesViewModel(CapabilityKind kind) => Kind = kind;

    public CapabilityKind Kind { get; }

    /// <summary>Groups with at least one item matching the search.</summary>
    public ObservableCollection<ExternalCapabilityGroup> Groups { get; } = [];

    public bool HasGroups => Groups.Count > 0;

    /// <summary>Whether anything external exists, regardless of the search.</summary>
    public bool HasAnyItems => _groups.Any(static group => group.All.Count > 0);

    public int TotalCount => _groups.Sum(static group => group.All.Count);
    public string TotalCountText => TotalCount.ToString(CultureInfo.CurrentCulture);

    /// <summary>Raised when an item opens, so the page closes its own editor.</summary>
    public event Action<ExternalCapabilityItem>? Opened;

    /// <summary>Raised to manage a plugin in Discover.</summary>
    public event Action<string>? ManageInDiscoverRequested;

    /// <summary>Raised to make an editable Lumi copy of the open item.</summary>
    public event Action<ExternalCapabilityItem>? CopyRequested;

    [ObservableProperty] private ExternalCapabilityItem? _openItem;

    internal string? NavigationItemKey => OpenItem is { } item ? ItemKey(item.Descriptor) : null;

    internal bool ContainsNavigationItem(string key)
        => _groups.SelectMany(static group => group.All).Any(item => ItemKey(item.Descriptor) == key);

    internal void OpenNavigationItem(string key)
    {
        var item = _groups.SelectMany(static group => group.All).First(item => ItemKey(item.Descriptor) == key);
        if (!ReferenceEquals(OpenItem, item))
            Open(item);
    }

    /// <summary>A long rendered body has been unfolded.</summary>
    [ObservableProperty] private bool _isBodyExpanded;

    public bool IsViewing => OpenItem is not null;

    public ObservableCollection<ExternalDetailFact> DetailFacts { get; } = [];

    public bool HasDetailFacts => DetailFacts.Count > 0;

    public string DetailTitle => OpenItem?.Name ?? "";
    public string DetailDescription => OpenItem?.Description ?? "";
    public bool HasDetailDescription => !string.IsNullOrWhiteSpace(DetailDescription);
    public string DetailGlyph => OpenItem?.Glyph ?? "";
    public string DetailSourceTitle => OpenItem?.Group.Title ?? "";
    public string DetailSourceCaption => OpenItem?.Group.SourceCaption ?? "";
    public bool DetailIsPlugin => OpenItem?.Group.IsPlugin == true;
    public bool DetailIsDisabled => OpenItem is { IsEnabled: false };

    /// <summary>Why the item is read-only and what to do instead.</summary>
    public string DetailNotice => OpenItem?.Group switch
    {
        null => "",
        { Source: ExternalSourceKind.BuiltIn } => Loc.External_NoticeBuiltIn,
        { IsPlugin: true } group when !CanCopy => string.Format(CultureInfo.CurrentCulture, Loc.External_NoticePluginNoCopy, group.Title),
        { IsPlugin: true } group => string.Format(CultureInfo.CurrentCulture, KindText(Loc.External_NoticePluginSkill, Loc.External_NoticePluginLumi, Loc.External_NoticePluginServer), group.Title),
        var group when !CanCopy => string.Format(CultureInfo.CurrentCulture, Loc.External_NoticeOtherNoCopy, PlaceOf(group)),
        var group => string.Format(CultureInfo.CurrentCulture, KindText(Loc.External_NoticeOtherSkill, Loc.External_NoticeOtherLumi, Loc.External_NoticeOtherServer), PlaceOf(group)),
    };

    /// <summary>Instructions or persona, rendered as markdown.</summary>
    [ObservableProperty] private string _detailBody = "";

    public bool HasDetailBody => !string.IsNullOrWhiteSpace(DetailBody);
    public bool IsBodyLong => ManagementText.IsLongMarkdown(DetailBody);
    public bool IsBodyClipped => IsBodyLong && !IsBodyExpanded;
    public string BodyToggleText => IsBodyExpanded ? Loc.Mg_ShowLess : Loc.Mg_ShowMore;
    public string BodyTitle => Kind == CapabilityKind.Agent ? Loc.External_PersonaTitle : Loc.Skills_Instructions;
    public bool ShowsBody => Kind != CapabilityKind.McpServer;

    /// <summary>A Lumi copy can be made: the source's definition is readable.</summary>
    [ObservableProperty] private bool _canCopy;

    partial void OnCanCopyChanged(bool value) => OnPropertyChanged(nameof(DetailNotice));

    public string CopyHint => Kind switch
    {
        CapabilityKind.Agent => Loc.External_CopyHintLumi,
        CapabilityKind.McpServer => Loc.External_CopyHintServer,
        _ => Loc.External_CopyHintSkill,
    };

    public string BackLabel => Kind switch
    {
        CapabilityKind.Agent => Loc.Lumis_Title,
        CapabilityKind.McpServer => Loc.Mcp_Title,
        _ => Loc.Skills_Title,
    };

    /// <summary>Replaces the items with what the latest capability snapshot reports.</summary>
    public void Update(CapabilitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var descriptors = snapshot.External
            .Where(capability => capability.Kind == Kind && !capability.Origin.IsLumi)
            .ToArray();

        // Every Customize section switch re-reads the catalog. Rebuilding identical groups would
        // recreate every card and sidebar row for nothing, so only a real change does.
        if (_descriptors is { } previous && previous.SequenceEqual(descriptors))
            return;

        _descriptors = descriptors;
        var openKey = OpenItem is { } open ? ItemKey(open.Descriptor) : null;
        var previousGroups = _groups.ToDictionary(static group => group.Key, StringComparer.OrdinalIgnoreCase);
        _groups.Clear();

        foreach (var bySource in descriptors.GroupBy(GroupKey, StringComparer.OrdinalIgnoreCase))
        {
            // A plugin that did not change keeps its group, so its cards and rows are not rebuilt
            // when another plugin is installed or removed.
            var ordered = bySource
                .OrderBy(static descriptor => ExternalCapabilityItem.DisplayName(descriptor), StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            if (previousGroups.Remove(bySource.Key, out var kept)
                && kept.All.Select(static item => item.Descriptor).SequenceEqual(ordered))
            {
                _groups.Add(kept);
                continue;
            }

            if (kept is not null)
                kept.PropertyChanged -= OnGroupPropertyChanged;

            var first = ordered[0];
            var source = SourceOf(first);
            var title = source == ExternalSourceKind.Plugin
                ? Lumi.Services.Discover.DisplayNames.Humanize(first.PluginName!)
                : TitleOf(source, first.Origin);
            var group = new ExternalCapabilityGroup(bySource.Key, source, title, first.PluginName, Kind, plugin => ManageInDiscoverRequested?.Invoke(plugin))
            {
                IsExpanded = _expandedKeys.Contains(bySource.Key),
            };
            group.PropertyChanged += OnGroupPropertyChanged;
            group.SetAll(ordered.Select(descriptor => new ExternalCapabilityItem(descriptor, group, Open)).ToList());
            _groups.Add(group);
        }

        foreach (var removed in previousGroups.Values)
            removed.PropertyChanged -= OnGroupPropertyChanged;

        _groups.Sort(static (left, right) =>
        {
            var bySource = left.Source.CompareTo(right.Source);
            return bySource != 0 ? bySource : StringComparer.CurrentCultureIgnoreCase.Compare(left.Title, right.Title);
        });

        ApplyFilter();

        if (openKey is not null)
        {
            var reopened = _groups.SelectMany(static group => group.All).FirstOrDefault(item => ItemKey(item.Descriptor) == openKey);
            if (reopened is null)
                Close();
            else if (!ReferenceEquals(reopened, OpenItem))
                ShowDetail(reopened);
        }

        OnPropertyChanged(nameof(HasAnyItems));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(TotalCountText));
    }

    public void ApplySearch(string? query)
    {
        _searchQuery = query?.Trim() ?? "";
        ApplyFilter();
    }

    /// <summary>Opens an item in the page's read-only detail.</summary>
    public void Open(ExternalCapabilityItem item)
    {
        // The sidebar shows where the open item lives, even when it was opened from a card.
        item.Group.IsExpanded = true;
        ShowDetail(item);
        Opened?.Invoke(item);
    }

    /// <summary>Unfolds a plugin's rows in the sidebar.</summary>
    public void Reveal(string pluginName)
    {
        foreach (var group in _groups.Where(group => string.Equals(group.PluginName, pluginName, StringComparison.OrdinalIgnoreCase)))
            group.IsExpanded = true;
    }

    [RelayCommand]
    public void Close()
    {
        if (OpenItem is { } item)
            item.IsSelected = false;
        OpenItem = null;
    }

    [RelayCommand]
    private void MakeCopy()
    {
        if (OpenItem is { } item && CanCopy)
            CopyRequested?.Invoke(item);
    }

    [RelayCommand]
    private void ManageInDiscover()
    {
        if (OpenItem?.Group.PluginName is { } plugin)
            ManageInDiscoverRequested?.Invoke(plugin);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (OpenItem is null || LocationOf(OpenItem.Descriptor) is not { } path)
            return;

        var folder = File.Exists(path) ? Path.GetDirectoryName(path) : Directory.Exists(path) ? path : null;
        if (folder is null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"[Customize] Could not open '{folder}': {ex.Message}");
        }
    }

    [RelayCommand]
    private void ToggleBodyExpanded() => IsBodyExpanded = !IsBodyExpanded;

    public bool CanOpenFolder => OpenItem is not null
                                 && LocationOf(OpenItem.Descriptor) is { } path
                                 && (File.Exists(path) || Directory.Exists(path));

    /// <summary>
    /// Where an item lives: a skill's or agent's file, or the file that declares a connector. A
    /// connector's source path is only the folder it was discovered for, so it has none without a file.
    /// </summary>
    private static string? LocationOf(CapabilityDescriptor descriptor)
        => descriptor.Kind == CapabilityKind.McpServer ? descriptor.ConfigFilePath : descriptor.SourcePath;

    partial void OnOpenItemChanged(ExternalCapabilityItem? oldValue, ExternalCapabilityItem? newValue)
    {
        if (oldValue is not null && !ReferenceEquals(oldValue, newValue))
            oldValue.IsSelected = false;
        if (newValue is not null)
            newValue.IsSelected = true;

        OnPropertyChanged(nameof(IsViewing));
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(DetailDescription));
        OnPropertyChanged(nameof(HasDetailDescription));
        OnPropertyChanged(nameof(DetailGlyph));
        OnPropertyChanged(nameof(DetailSourceTitle));
        OnPropertyChanged(nameof(DetailSourceCaption));
        OnPropertyChanged(nameof(DetailIsPlugin));
        OnPropertyChanged(nameof(DetailIsDisabled));
        OnPropertyChanged(nameof(DetailNotice));
        OnPropertyChanged(nameof(CanOpenFolder));
    }

    partial void OnDetailBodyChanged(string value)
    {
        OnPropertyChanged(nameof(HasDetailBody));
        OnPropertyChanged(nameof(IsBodyLong));
        OnPropertyChanged(nameof(IsBodyClipped));
    }

    partial void OnIsBodyExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBodyClipped));
        OnPropertyChanged(nameof(BodyToggleText));
    }

    private void ShowDetail(ExternalCapabilityItem item)
    {
        var descriptor = item.Descriptor;
        IsBodyExpanded = false;
        DetailBody = Kind switch
        {
            CapabilityKind.Agent => descriptor.Content
                                    ?? (CapabilityContent.TryReadBody(descriptor, out var persona) ? persona : ""),
            CapabilityKind.Skill => CapabilityContent.TryReadBody(descriptor, out var instructions) ? instructions : "",
            _ => "",
        };

        DetailFacts.Clear();
        foreach (var fact in BuildFacts(descriptor))
            DetailFacts.Add(fact);
        OnPropertyChanged(nameof(HasDetailFacts));

        CanCopy = Kind switch
        {
            CapabilityKind.McpServer => ExternalCapabilityCopier.ReadMcpServer(descriptor, out var signsIn) is not null && !signsIn,
            CapabilityKind.Agent => HasDetailBody,
            _ => File.Exists(descriptor.SourcePath),
        };

        if (ReferenceEquals(OpenItem, item))
            OnOpenItemChanged(item, item);
        else
            OpenItem = item;
    }

    private IEnumerable<ExternalDetailFact> BuildFacts(CapabilityDescriptor descriptor)
    {
        switch (Kind)
        {
            case CapabilityKind.Skill:
                yield return new ExternalDetailFact(Loc.External_FactInvoke, "/" + (descriptor.SkillInvocationName ?? descriptor.Name), IsMono: true);
                break;

            case CapabilityKind.Agent:
                if (descriptor.Behavior?.Model is { Length: > 0 } model)
                    yield return new ExternalDetailFact(Loc.External_FactModel, model);
                yield return new ExternalDetailFact(
                    Loc.External_FactTools,
                    descriptor.Behavior?.Tools is { } tools
                        ? tools.Count == 0 ? Loc.External_FactToolsNone : string.Join(", ", tools)
                        : Loc.External_FactToolsAll);
                if (descriptor.Behavior?.Skills is { Count: > 0 } skills)
                    yield return new ExternalDetailFact(Loc.External_FactSkills, string.Join(", ", skills));
                break;

            case CapabilityKind.McpServer:
                if (ExternalCapabilityCopier.ReadMcpServer(descriptor) is { } server)
                {
                    var runs = string.Equals(server.ServerType, "remote", StringComparison.OrdinalIgnoreCase)
                        ? server.Url
                        : string.Join(' ', new[] { server.Command }.Concat(server.Args));
                    yield return new ExternalDetailFact(Loc.External_FactRuns, runs, IsMono: true);
                    if (server.Env.Count > 0)
                        yield return new ExternalDetailFact(Loc.External_FactEnv, string.Join(", ", server.Env.Keys), IsMono: true);
                }

                if (descriptor.McpTransport is { Length: > 0 } transport)
                    yield return new ExternalDetailFact(Loc.External_FactTransport, transport);
                yield return new ExternalDetailFact(Loc.External_FactStatus, descriptor.IsEnabled ? Loc.Mcp_StatusOn : Loc.Mcp_StatusOff);
                break;
        }

        if (LocationOf(descriptor) is { Length: > 0 } location && Path.IsPathRooted(location))
            yield return new ExternalDetailFact(Loc.External_FactLocation, location, IsMono: true);
    }

    private void ApplyFilter()
    {
        var visible = new List<ExternalCapabilityGroup>();
        foreach (var group in _groups)
        {
            var items = string.IsNullOrWhiteSpace(_searchQuery)
                ? group.All
                : SearchPipeline.Rank(
                    group.All,
                    _searchQuery,
                    static item =>
                    [
                        SearchField.Primary(item.Name, 3.2),
                        new SearchField(item.Description, 1.7),
                        new SearchField(item.Group.Title, 0.9),
                    ],
                    static item => new SearchSortMetadata(Text: item.Name)).ToList();

            group.ShowItems(items);
            if (items.Count > 0)
                visible.Add(group);
        }

        ExternalCapabilityGroup.Sync(Groups, visible);
        OnPropertyChanged(nameof(HasGroups));
    }

    private void OnGroupPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not ExternalCapabilityGroup group || e.PropertyName != nameof(ExternalCapabilityGroup.IsExpanded))
            return;

        if (group.IsExpanded)
            _expandedKeys.Add(group.Key);
        else
            _expandedKeys.Remove(group.Key);
    }

    private string KindText(string skill, string lumi, string server) => Kind switch
    {
        CapabilityKind.Agent => lumi,
        CapabilityKind.McpServer => server,
        _ => skill,
    };

    private static string GroupKey(CapabilityDescriptor descriptor)
        => descriptor.PluginName is { Length: > 0 } plugin ? "plugin:" + plugin : "origin:" + SourceOf(descriptor);

    private static string ItemKey(CapabilityDescriptor descriptor) => $"{GroupKey(descriptor)}|{descriptor.Name}";

    private static ExternalSourceKind SourceOf(CapabilityDescriptor descriptor)
    {
        if (descriptor.PluginName is { Length: > 0 })
            return ExternalSourceKind.Plugin;

        return descriptor.Origin.Id switch
        {
            CapabilityOrigin.PersonalId => ExternalSourceKind.Copilot,
            CapabilityOrigin.ProjectId or CapabilityOrigin.WorkspaceId => ExternalSourceKind.Folder,
            CapabilityOrigin.BuiltInId => ExternalSourceKind.BuiltIn,
            _ => ExternalSourceKind.Other,
        };
    }

    private static string TitleOf(ExternalSourceKind source, CapabilityOrigin origin) => source switch
    {
        ExternalSourceKind.Copilot => Loc.External_GroupCopilot,
        ExternalSourceKind.Folder => Loc.External_GroupFolder,
        ExternalSourceKind.BuiltIn => Loc.External_GroupBuiltIn,
        _ => origin.Label,
    };

    /// <summary>Where a group's items live, the way it reads inside a sentence ("your Copilot folder").</summary>
    private static string PlaceOf(ExternalCapabilityGroup group) => group.Source switch
    {
        ExternalSourceKind.Copilot => Loc.External_PlaceCopilot,
        ExternalSourceKind.Folder => Loc.External_PlaceFolder,
        _ => group.Title,
    };
}
