using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;

namespace Lumi.ViewModels;

/// <summary>The kinds of content the Workspace overview can narrow to, in category-bar order.</summary>
public enum WorkspaceCategory
{
    All,
    Plan,
    Agents,
    Changes,
    Files,
    Edits,
    Skills,
    Links,
    Sources,
    Messages,
    Activity,
    Browser,
}

/// <summary>What every Workspace overview section exposes, whatever its row type — enough for the
/// shared section chrome (<c>WorkspaceSectionView</c>), the category bar and the workspace-wide search.</summary>
public interface IWorkspaceSection : INotifyPropertyChanged
{
    WorkspaceCategory Category { get; }
    IEnumerable Items { get; }
    int TotalCount { get; }
    bool HasItems { get; }
    bool IsShown { get; }
    bool IsFocused { get; }
    bool HasMore { get; }
    string CountLabel { get; }
    string ShowAllLabel { get; }
    IRelayCommand ShowAllCommand { get; }
    Action<WorkspaceCategory>? ShowAllRequested { get; set; }
    void SetQuery(string? query);
    void SetFocus(WorkspaceCategory focus);
}

/// <summary>
/// One list in the Workspace overview (files, edits, links, activity…). It keeps every item the chat
/// produced for that kind, applies the workspace search, and exposes only the rows the overview
/// renders: a short preview beside everything else, and every row once the overview is narrowed to
/// this kind or searched. A new kind of workspace content is one section plus its row template.
/// </summary>
public sealed partial class WorkspaceSection<T> : ObservableObject, IWorkspaceSection where T : class
{
    private readonly Func<T, string, bool> _matches;
    private IReadOnlyList<T> _all = [];
    private IReadOnlyList<T> _matching = [];
    private string _query = "";
    private WorkspaceCategory _focus = WorkspaceCategory.All;

    public WorkspaceSection(WorkspaceCategory category, int previewLimit, Func<T, string, bool> matches)
    {
        Category = category;
        PreviewLimit = Math.Max(1, previewLimit);
        _matches = matches;
    }

    public WorkspaceCategory Category { get; }

    /// <summary>How many rows the overview previews beside the other kinds.</summary>
    public int PreviewLimit { get; }

    /// <summary>The rows the overview renders right now.</summary>
    public ObservableCollection<T> Items { get; } = [];

    IEnumerable IWorkspaceSection.Items => Items;

    public int TotalCount => _all.Count;

    /// <summary>Items matching the current search (every item when not searching).</summary>
    public int MatchCount => _matching.Count;

    /// <summary>True when the section has anything to show for the current search.</summary>
    public bool HasItems => _matching.Count > 0;

    /// <summary>On screen: it has matches and the overview isn't narrowed to another kind.</summary>
    public bool IsShown => HasItems && (_focus == WorkspaceCategory.All || _focus == Category);

    /// <summary>The overview is narrowed to this kind (its chip already names it).</summary>
    public bool IsFocused => _focus == Category;

    /// <summary>Rows beyond the preview, one "Show all" away.</summary>
    public bool HasMore => !ShowsEveryRow && _matching.Count > PreviewLimit;

    public string CountLabel => MatchCount.ToString(CultureInfo.CurrentCulture);

    public string ShowAllLabel => string.Format(Loc.Culture, Loc.Workspace_ShowAll, MatchCount);

    /// <summary>Asks the overview to narrow to this kind; wired by the owning view-model.</summary>
    public Action<WorkspaceCategory>? ShowAllRequested { get; set; }

    private bool ShowsEveryRow => IsFocused || _query.Length > 0;

    public void SetItems(IEnumerable<T> items)
    {
        _all = items as IReadOnlyList<T> ?? items.ToList();
        Refresh();
    }

    public void SetQuery(string? query)
    {
        var normalized = query?.Trim() ?? "";
        if (string.Equals(normalized, _query, StringComparison.Ordinal))
            return;

        _query = normalized;
        Refresh();
    }

    /// <summary>The kind the overview is narrowed to (<see cref="WorkspaceCategory.All"/> for everything).</summary>
    public void SetFocus(WorkspaceCategory focus)
    {
        if (_focus == focus)
            return;

        _focus = focus;
        Refresh();
    }

    [RelayCommand]
    private void ShowAll() => ShowAllRequested?.Invoke(Category);

    private void Refresh()
    {
        _matching = _query.Length == 0
            ? _all
            : _all.Where(item => _matches(item, _query)).ToList();

        SyncItems();

        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(MatchCount));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsShown));
        OnPropertyChanged(nameof(IsFocused));
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(ShowAllLabel));
    }

    private void SyncItems()
    {
        var visible = ShowsEveryRow ? _matching : _matching.Take(PreviewLimit).ToList();

        // Rebuilds usually hand back the same leading items; leave the rows alone when nothing moved.
        if (Items.Count == visible.Count && Items.SequenceEqual(visible, ReferenceEqualityComparer.Instance))
            return;

        Items.Clear();
        foreach (var item in visible)
            Items.Add(item);
    }
}

/// <summary>
/// One chip in the overview's category bar: a kind of content, how much of it there is, and whether
/// the overview is narrowed to it. Every kind stays one click away, however long the chat gets.
/// </summary>
public sealed partial class WorkspaceCategoryChip : ObservableObject
{
    public WorkspaceCategoryChip(WorkspaceCategory category, string title, bool opensPage = false)
    {
        Category = category;
        Title = title;
        OpensPage = opensPage;
    }

    public WorkspaceCategory Category { get; }

    public string Title { get; }

    public Geometry Icon => _icon ??= WorkspaceIcons.For(Category);

    private Geometry? _icon;

    /// <summary>A single thing (the plan, the browser) opens its page rather than narrowing a list.</summary>
    public bool OpensPage { get; }

    [ObservableProperty] private bool _isAvailable;

    [ObservableProperty] private bool _isSelected;

    /// <summary>Work in this kind is running right now (agents).</summary>
    [ObservableProperty] private bool _isLive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCount))]
    private string? _countLabel;

    public bool HasCount => !string.IsNullOrEmpty(CountLabel);
}
