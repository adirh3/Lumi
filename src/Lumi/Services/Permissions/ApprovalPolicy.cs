using System.Collections.Frozen;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Lumi.Models;

namespace Lumi.Services.Permissions;

public enum ApprovalModeSource
{
    Chat,
    Project,
    Default
}

/// <summary>
/// Maps Lumi's approval choices onto Copilot's native permission controls. Copilot enforces every
/// decision: the mode is a native permission mode, Allow and Never rules are native session rules,
/// and Always-ask rules are a native managed-settings Ask layer. Lumi only chooses and re-applies
/// them, because the runtime keeps neither rules nor session approvals across a resume.
/// </summary>
public static class ApprovalPolicy
{
    public const string BrowserGroup = "browser";
    public const string DesktopGroup = "desktop";
    public const string WebGroup = "web";
    public const string LumiDataGroup = "lumi";
    public const string ChatsGroup = "chats";

    /// <summary>
    /// Lumi tools that only read, or only talk to the user. They carry Copilot's native
    /// <c>skip_permission</c>, so no mode asks for them; a Never rule still blocks them.
    /// </summary>
    private static readonly FrozenSet<string> HarmlessTools = new[]
    {
        "ask_question", "ask_input", "announce_file", "recall_memory", "search_chats", "read_chat",
        "lumi_browser_look", "lumi_browser_find", "lumi_browser_screenshot",
        "ui_list_windows", "ui_inspect", "ui_find", "ui_read", "ui_screenshot",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string[]> ToolGroups = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [BrowserGroup] = ["lumi_browser_open", "lumi_browser_do", "lumi_browser_js", "lumi_browser_tabs"],
        [DesktopGroup] = ["ui_do", "ui_click", "ui_click_at", "ui_type", "ui_press_keys"],
        [WebGroup] = ["lumi_fetch"],
        [LumiDataGroup] = ["manage_projects", "manage_skills", "manage_lumis", "manage_mcps", "manage_jobs", "manage_memories", "manage_current_chat"],
        [ChatsGroup] = ["manage_chats"],
    };

    public static IReadOnlyList<string> LumiToolGroups { get; } = [BrowserGroup, DesktopGroup, WebGroup, LumiDataGroup, ChatsGroup];

    public static bool SkipsApproval(string toolName) => HarmlessTools.Contains(toolName);

    public static IReadOnlyList<string> ToolsInGroup(string group)
        => ToolGroups.TryGetValue(group, out var tools) ? tools : [];

    public static string? GroupOf(string toolName)
        => ToolGroups.FirstOrDefault(pair => pair.Value.Contains(toolName, StringComparer.Ordinal)).Key;

    /// <summary>
    /// The native mode for a choice. Auto-review's reviewer is a Copilot request, which "Use only
    /// your own accounts" rules out, so there it asks like Ask first.
    /// </summary>
    public static PermissionMode ToNativeMode(ApprovalMode mode, bool ownAccountsOnly = false) => mode switch
    {
        ApprovalMode.AskFirst => PermissionMode.Manual,
        ApprovalMode.AutoReview => ownAccountsOnly ? PermissionMode.Manual : PermissionMode.Assisted,
        _ => PermissionMode.AllowAll,
    };

    public static (ApprovalMode Mode, ApprovalModeSource Source) ResolveMode(Chat? chat, Project? project, UserSettings settings)
    {
        if (chat?.ApprovalMode is { } own && Enum.IsDefined(own))
            return (own, ApprovalModeSource.Chat);
        if (project?.ApprovalMode is { } projectMode && Enum.IsDefined(projectMode))
            return (projectMode, ApprovalModeSource.Project);
        return (Enum.IsDefined(settings.DefaultApprovalMode) ? settings.DefaultApprovalMode : ApprovalMode.FullAccess,
            ApprovalModeSource.Default);
    }

    /// <summary>Rules that apply in a chat: yours everywhere, the project's, then the chat's own.</summary>
    public static List<ApprovalRule> EffectiveRules(UserSettings settings, Project? project, Chat? chat)
        => [.. settings.ApprovalRules, .. project?.ApprovalRules ?? [], .. chat?.ApprovalRules ?? []];

    /// <summary>Allow and Never rules as native session rules. Never wins over everything, even Full access.</summary>
    public static PermissionRulesSet BuildSessionRules(IEnumerable<ApprovalRule> rules, Func<string, string>? connectorKey = null)
    {
        var approved = new List<PermissionRule>();
        var denied = new List<PermissionRule>();
        foreach (var rule in rules)
        {
            var target = rule.Action switch
            {
                ApprovalRuleAction.Allow => approved,
                ApprovalRuleAction.Never => denied,
                _ => null
            };
            if (target is null)
                continue;
            foreach (var native in ToSessionRules(rule, connectorKey))
                if (!target.Any(existing => existing.Kind == native.Kind && existing.Argument == native.Argument))
                    target.Add(native);
        }

        return new PermissionRulesSet { Approved = approved, Denied = denied };
    }

    /// <summary>
    /// Always-ask rules as a native managed-settings Ask layer. The runtime then requires a person
    /// for matching requests even in Full access. The layer is fixed when a session starts, so a
    /// change applies when the chat's session is next rebuilt.
    /// </summary>
    public static ManagedSettings? BuildAskLayer(IEnumerable<ApprovalRule> rules, Func<string, string>? connectorKey = null)
    {
        var patterns = rules
            .Where(rule => rule.Action == ApprovalRuleAction.Ask)
            .SelectMany(rule => ToManagedPatterns(rule, connectorKey))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return patterns.Count == 0
            ? null
            : new ManagedSettings { Permissions = new ManagedSettingsPermissions { Ask = patterns } };
    }

    /// <summary>Identifies an Ask layer, so a session can tell when its startup-only layer is out of date.</summary>
    public static string AskLayerSignature(ManagedSettings? layer)
        => layer?.Permissions?.Ask is { Count: > 0 } ask ? string.Join("\n", ask) : "";

    internal static IEnumerable<PermissionRule> ToSessionRules(ApprovalRule rule, Func<string, string>? connectorKey)
    {
        var target = NormalizeTarget(rule);
        switch (rule.Kind)
        {
            case ApprovalRuleKind.Command:
                yield return new PermissionRule { Kind = "shell", Argument = target is null ? null : target + ":*" };
                break;
            case ApprovalRuleKind.EditFiles:
                yield return new PermissionRule { Kind = "write", Argument = target };
                break;
            case ApprovalRuleKind.ReadFiles:
                yield return new PermissionRule { Kind = "read", Argument = target };
                break;
            case ApprovalRuleKind.Connector when SplitConnector(target, connectorKey) is { } connector:
                yield return new PermissionRule { Kind = connector.Server, Argument = connector.Tool };
                break;
            case ApprovalRuleKind.LumiTool:
                foreach (var tool in ExpandLumiTools(target))
                    yield return new PermissionRule { Kind = "custom-tool", Argument = tool };
                break;
        }
    }

    internal static IEnumerable<string> ToManagedPatterns(ApprovalRule rule, Func<string, string>? connectorKey)
    {
        var target = NormalizeTarget(rule);
        switch (rule.Kind)
        {
            case ApprovalRuleKind.Command:
                yield return target is null ? "Shell" : $"Shell({target})";
                break;
            case ApprovalRuleKind.EditFiles:
                yield return target is null ? "Write" : $"Write({target})";
                yield return target is null ? "Edit" : $"Edit({target})";
                break;
            case ApprovalRuleKind.ReadFiles:
                yield return target is null ? "Read" : $"Read({target})";
                break;
            case ApprovalRuleKind.Connector when SplitConnector(target, connectorKey) is { } connector:
                yield return connector.Tool is null ? $"Mcp({connector.Server})" : $"Mcp({connector.Server}/{connector.Tool})";
                break;
            case ApprovalRuleKind.LumiTool:
                foreach (var tool in ExpandLumiTools(target))
                    yield return $"CustomTool({tool})";
                break;
        }
    }

    /// <summary>A rule's target, trimmed, without a trailing wildcard; null covers everything of its kind.</summary>
    internal static string? NormalizeTarget(ApprovalRule rule)
    {
        var target = rule.Target?.Trim();
        if (rule.Kind == ApprovalRuleKind.Command)
            target = target?.TrimEnd('*').TrimEnd(':').Trim();
        return string.IsNullOrEmpty(target) ? null : target;
    }

    private static IEnumerable<string> ExpandLumiTools(string? target)
        => target is null
            ? ToolGroups.Values.SelectMany(tools => tools)
            : ToolGroups.TryGetValue(target, out var tools) ? tools : [target];

    private static (string Server, string? Tool)? SplitConnector(string? target, Func<string, string>? connectorKey)
    {
        if (target is null)
            return null;
        var slash = target.IndexOf('/');
        var server = (slash < 0 ? target : target[..slash]).Trim();
        var tool = slash < 0 ? null : target[(slash + 1)..].Trim();
        if (server.Length == 0)
            return null;
        return (connectorKey?.Invoke(server) ?? server, string.IsNullOrEmpty(tool) ? null : tool);
    }

    /// <summary>Two rules that mean the same thing, so remembering one again is a no-op.</summary>
    public static bool IsSameRule(ApprovalRule a, ApprovalRule b)
        => a.Action == b.Action && a.Kind == b.Kind
           && string.Equals(NormalizeTarget(a), NormalizeTarget(b), StringComparison.OrdinalIgnoreCase);
}
