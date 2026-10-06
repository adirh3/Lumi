using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services.Sharing;

namespace Lumi.ViewModels;

/// <summary>Formatting shared by the management pages (Projects, Skills, Lumis, Memories, MCP servers).</summary>
internal static partial class ManagementText
{
    public static string Count(int count, string one, string many)
        => count == 1 ? one : string.Format(CultureInfo.CurrentCulture, many, count);

    public static string Relative(DateTimeOffset timestamp) => LibraryViewModel.FormatRelativeTime(timestamp);

    public static string Date(DateTimeOffset timestamp)
        => timestamp.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);

    public static string Size(string? text) => PackText.FormatSize(text ?? "");

    /// <summary>The first letter or digit of a name, upper-cased, for letter avatars.</summary>
    public static string Initial(string? name)
    {
        foreach (var c in name ?? "")
        {
            if (char.IsLetterOrDigit(c))
                return char.ToUpper(c, CultureInfo.CurrentCulture).ToString();
        }

        return "?";
    }

    /// <summary>Whether rendered markdown is long enough to start folded in a preview.</summary>
    public static bool IsLongMarkdown(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return false;

        if (markdown.Length > 1600)
            return true;

        var lines = 1;
        foreach (var c in markdown)
        {
            if (c == '\n' && ++lines > 22)
                return true;
        }

        return false;
    }

    /// <summary>
    /// A one-paragraph plain-text taste of markdown for card previews: headings, list markers,
    /// emphasis, links and code fences are dropped so the preview reads as prose.
    /// </summary>
    public static string PlainPreview(string? markdown, int maxLength = 220)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return "";

        var builder = new StringBuilder();
        var inFence = false;
        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence || line.Length == 0 || line is "---" or "***" || line.StartsWith('|'))
                continue;

            line = LeadingMarkerRegex().Replace(line, "");
            line = LinkRegex().Replace(line, "$1");
            line = line.Replace("**", "").Replace("__", "").Replace("`", "");
            line = EmphasisRegex().Replace(line, "$1");
            if (line.Length == 0)
                continue;

            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append(line);
            if (builder.Length >= maxLength)
                break;
        }

        var text = WhitespaceRegex().Replace(builder.ToString(), " ").Trim();
        return text.Length <= maxLength ? text : text[..maxLength].TrimEnd() + "…";
    }

    [GeneratedRegex(@"^(#{1,6}\s+|>\s*|[-*+]\s+(\[[ xX]\]\s+)?|\d+[.)]\s+)")]
    private static partial Regex LeadingMarkerRegex();

    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"(?<![\w*])[*_]([^*_]+)[*_](?![\w*])")]
    private static partial Regex EmphasisRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

/// <summary>A link to another Lumi object shown as a chip, e.g. the Lumis that use a skill.</summary>
public sealed class RelatedItem
{
    public RelatedItem(Guid id, string name, string glyph)
    {
        Id = id;
        Name = name;
        Glyph = glyph;
    }

    public Guid Id { get; }
    public string Name { get; }
    public string Glyph { get; }
}

/// <summary>An option in a sort switch or filter row.</summary>
public sealed partial class ManagementFilterOption : ObservableObject
{
    public ManagementFilterOption(string key, string label, int count, Action<ManagementFilterOption> select)
    {
        Key = key;
        Label = label;
        Count = count;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public string Key { get; }
    public string Label { get; }
    public int Count { get; }
    public string CountText => Count.ToString(CultureInfo.CurrentCulture);
    public IRelayCommand SelectCommand { get; }

    [ObservableProperty] private bool _isSelected;
}

public sealed class SkillCard
{
    public SkillCard(Skill skill, int usedByCount, Action<Skill> open)
    {
        Model = skill;
        UsedByCount = usedByCount;
        OpenCommand = new RelayCommand(() => open(skill));
    }

    public Skill Model { get; }
    public string Name => Model.Name;
    public string Glyph => string.IsNullOrWhiteSpace(Model.IconGlyph) ? "⚡" : Model.IconGlyph;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Model.Description);
    public string Summary => HasDescription ? Model.Description : ManagementText.PlainPreview(Model.Content, 180);
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
    public bool IsBuiltIn => Model.IsBuiltIn;
    public int UsedByCount { get; }
    public bool IsUsed => UsedByCount > 0;
    public string UsedByLabel => IsUsed
        ? ManagementText.Count(UsedByCount, Loc.Skills_UsedByOne, Loc.Skills_UsedByMany)
        : Loc.Skills_NotUsed;
    public string SizeLabel => ManagementText.Size(Model.Content);
    public IRelayCommand OpenCommand { get; }
}

public sealed class AgentCard
{
    public AgentCard(LumiAgent agent, int chatCount, Action<LumiAgent> open, Action<LumiAgent> chat)
    {
        Model = agent;
        ChatCount = chatCount;
        OpenCommand = new RelayCommand(() => open(agent));
        ChatCommand = new RelayCommand(() => chat(agent));
    }

    public LumiAgent Model { get; }
    public string Name => Model.Name;
    public string Glyph => string.IsNullOrWhiteSpace(Model.IconGlyph) ? "✦" : Model.IconGlyph;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Model.Description);
    public string Summary => HasDescription ? Model.Description : ManagementText.PlainPreview(Model.SystemPrompt, 180);
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
    public bool IsBuiltIn => Model.IsBuiltIn;
    public int ChatCount { get; }
    public string ChatCountLabel => ChatCount > 0
        ? ManagementText.Count(ChatCount, Loc.Lumis_ChatCountOne, Loc.Lumis_ChatCountMany)
        : Loc.Lumis_NoChats;
    public string SkillsLabel => ManagementText.Count(Model.SkillIds.Count, Loc.Lumis_CapSkillsOne, Loc.Lumis_CapSkillsMany);
    public string ServersLabel => ManagementText.Count(Model.McpServerIds.Count, Loc.Lumis_CapServersOne, Loc.Lumis_CapServersMany);
    public string ToolsLabel => AgentsViewModel.DescribeTools(Model);
    public bool HasSkills => Model.SkillIds.Count > 0;
    public bool HasServers => Model.McpServerIds.Count > 0;
    public IRelayCommand OpenCommand { get; }
    public IRelayCommand ChatCommand { get; }
}

public sealed partial class ProjectCard : ObservableObject
{
    public ProjectCard(Project project, int chatCount, DateTimeOffset? lastActivity, Action<Project> open, Action<Project> newChat)
    {
        Model = project;
        _chatCount = chatCount;
        _lastActivity = lastActivity;
        OpenCommand = new RelayCommand(() => open(project));
        NewChatCommand = new RelayCommand(() => newChat(project));
    }

    public Project Model { get; }
    public string Name => Model.Name;
    public string Initial => ManagementText.Initial(Model.Name);
    public bool HasFolder => !string.IsNullOrWhiteSpace(Model.WorkingDirectory);
    public string FolderLabel => HasFolder ? Model.WorkingDirectory!.TrimEnd('\\', '/') : Loc.Projects_NoFolder;
    public string InstructionsPreview => ManagementText.PlainPreview(Model.Instructions, 160);
    public bool HasInstructions => !string.IsNullOrWhiteSpace(InstructionsPreview);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChatCountLabel))]
    private int _chatCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivityLabel))]
    private DateTimeOffset? _lastActivity;

    public string ChatCountLabel => ManagementText.Count(ChatCount, Loc.Project_ChatCount, Loc.Project_ChatCounts);
    public string ActivityLabel => LastActivity is { } last
        ? string.Format(CultureInfo.CurrentCulture, Loc.Projects_LastActive, ManagementText.Relative(last))
        : Loc.Projects_NoActivity;
    public IRelayCommand OpenCommand { get; }
    public IRelayCommand NewChatCommand { get; }
}

public sealed class MemoryCard
{
    public MemoryCard(Memory memory, string scopeLabel, Action<Memory> open)
    {
        Model = memory;
        ScopeLabel = scopeLabel;
        OpenCommand = new RelayCommand(() => open(memory));
    }

    public Memory Model { get; }
    public string Key => Model.Key;
    public string Content => Model.Content;
    public bool HasContent => !string.IsNullOrWhiteSpace(Model.Content);
    public string Category => string.IsNullOrWhiteSpace(Model.Category) ? MemoriesViewModel.DefaultCategory : Model.Category;
    public bool IsProjectScoped => string.Equals(Model.Scope, MemoryScopes.Project, StringComparison.OrdinalIgnoreCase);
    public string ScopeLabel { get; }
    public string UpdatedLabel => ManagementText.Relative(Model.UpdatedAt);
    public IRelayCommand OpenCommand { get; }
}

/// <summary>Memories of one category in the overview.</summary>
public sealed class MemoryGroup
{
    public MemoryGroup(string category, IReadOnlyList<MemoryCard> items)
    {
        Category = category;
        Items = items;
    }

    public string Category { get; }
    public IReadOnlyList<MemoryCard> Items { get; }
    public string CountText => Items.Count.ToString(CultureInfo.CurrentCulture);
}

public sealed partial class McpServerCard : ObservableObject
{
    private readonly Action<McpServer> _toggle;

    public McpServerCard(McpServer server, int usedByCount, Action<McpServer> open, Action<McpServer> toggle)
    {
        Model = server;
        UsedByCount = usedByCount;
        _toggle = toggle;
        OpenCommand = new RelayCommand(() => open(server));
        ToggleCommand = new RelayCommand(() => _toggle(server));
    }

    public McpServer Model { get; }
    public string Name => Model.Name;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Model.Description);
    public string Description => Model.Description;
    public bool IsEnabled => Model.IsEnabled;
    public bool IsRemote => string.Equals(Model.ServerType, "remote", StringComparison.OrdinalIgnoreCase);
    public string TypeLabel => IsRemote ? Loc.Mcp_Remote : Loc.Mcp_Local;
    public string Endpoint => McpServersViewModel.DescribeEndpoint(Model);
    public string StatusLabel => IsEnabled ? Loc.Mcp_StatusOn : Loc.Mcp_StatusOff;
    public int UsedByCount { get; }
    public string UsedByLabel => UsedByCount > 0
        ? ManagementText.Count(UsedByCount, Loc.Skills_UsedByOne, Loc.Skills_UsedByMany)
        : Loc.Skills_NotUsed;
    public IRelayCommand OpenCommand { get; }
    public IRelayCommand ToggleCommand { get; }
}

/// <summary>A context folder of a project, shown as a removable row.</summary>
public sealed class ContextFolderItem
{
    public ContextFolderItem(string path, Action<ContextFolderItem> remove)
    {
        Path = path;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Path { get; }
    public string Name
    {
        get
        {
            var trimmed = Path.TrimEnd('\\', '/');
            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }
    }

    public IRelayCommand RemoveCommand { get; }
}

/// <summary>The tools of one group in the Lumi editor, with bulk on/off.</summary>
public sealed partial class ToolGroup : ObservableObject
{
    public ToolGroup(string key, string displayName, IReadOnlyList<ToolToggle> tools)
    {
        Key = key;
        DisplayName = displayName;
        Tools = new ObservableCollection<ToolToggle>(tools);
        foreach (var tool in Tools)
            tool.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ToolToggle.IsSelected))
                    NotifyCounts();
            };
    }

    public string Key { get; }
    public string DisplayName { get; }
    public ObservableCollection<ToolToggle> Tools { get; }
    public int SelectedCount => Tools.Count(static tool => tool.IsSelected);
    public string CountText => string.Format(CultureInfo.CurrentCulture, Loc.Lumis_SelectedOf, SelectedCount, Tools.Count);
    public bool AllSelected => SelectedCount == Tools.Count;

    [RelayCommand]
    private void ToggleAll()
    {
        var target = !AllSelected;
        foreach (var tool in Tools)
            tool.IsSelected = target;
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(AllSelected));
    }
}
