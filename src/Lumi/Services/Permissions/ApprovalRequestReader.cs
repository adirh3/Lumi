using System.Text;
using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.Services.Permissions;

public enum ApprovalCardKind
{
    Command,
    Edit,
    Read,
    Path,
    Website,
    Connector,
    LumiTool,
    Memory,
    Other
}

/// <summary>A native permission prompt, read for an approval card.</summary>
public sealed record ApprovalRequestInfo
{
    public required ApprovalCardKind Kind { get; init; }
    public required string Title { get; init; }
    public required string IconKey { get; init; }
    public string? Subject { get; init; }
    public string? Intention { get; init; }
    public string? Detail { get; init; }
    public string? Diff { get; init; }
    public string? Warning { get; init; }
    public bool IsEscalation { get; init; }
    public bool IsRecognized { get; init; } = true;

    /// <summary>An Always-ask rule or managed policy matched: only a one-time answer is offered.</summary>
    public bool IsAlwaysAsk { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public AssistedApprovalRecommendation? Recommendation { get; init; }
    public string? ReviewerReason { get; init; }

    /// <summary>The rules "remember" saves, so the runtime keeps allowing this after a resume.</summary>
    public IReadOnlyList<ApprovalRule> RememberRules { get; init; } = [];

    /// <summary>The native approval that remembers this for the rest of the live session.</summary>
    public PermissionDecisionApproveForSessionApproval? SessionApproval { get; init; }

    /// <summary>What remembering covers, in words: "git push", "file edits", "your browser".</summary>
    public string? RememberScope { get; init; }

    public bool CanRemember => IsRecognized && !IsAlwaysAsk && !IsEscalation && RememberRules.Count > 0 && SessionApproval is not null;
}

public static class ApprovalRequestReader
{
    /// <param name="connectorName">Maps a runtime connector key back to the name the user knows.</param>
    public static ApprovalRequestInfo Read(
        PermissionPromptRequest prompt,
        bool alwaysAsk,
        Func<string, string>? connectorName = null)
    {
        var info = prompt switch
        {
            PermissionPromptRequestCommands commands => ReadCommands(commands),
            PermissionPromptRequestWrite write => ReadWrite(write),
            PermissionPromptRequestRead read => new ApprovalRequestInfo
            {
                Kind = ApprovalCardKind.Read,
                Title = Loc.Get("Approval_ReadFile"),
                IconKey = TranscriptIcons.Read,
                Subject = read.Path,
                Intention = read.Intention,
                ToolCallId = read.ToolCallId,
                IsAlwaysAsk = read.ManagedApprovalRequired == true,
            },
            PermissionPromptRequestPath path => new ApprovalRequestInfo
            {
                Kind = ApprovalCardKind.Path,
                Title = Loc.Get(path.AccessKind == PermissionPromptRequestPathAccessKind.Write
                    ? "Approval_PathWrite" : "Approval_PathRead"),
                IconKey = TranscriptIcons.Read,
                Subject = path.Paths is { Length: > 0 } paths ? paths[0] : null,
                Detail = path.Paths is { Length: > 1 } many ? string.Join("\n", many) : null,
                ToolCallId = path.ToolCallId,
            },
            PermissionPromptRequestUrl url => new ApprovalRequestInfo
            {
                Kind = ApprovalCardKind.Website,
                Title = Loc.Get("Approval_Website"),
                IconKey = TranscriptIcons.Web,
                Subject = url.Url,
                Intention = url.Intention,
                IsEscalation = url.RequestSandboxBypass == true,
                Warning = url.RequestSandboxBypass == true ? Loc.Get("Approval_SandboxBypass") : null,
                ToolCallId = url.ToolCallId,
                IsAlwaysAsk = url.ManagedApprovalRequired == true,
            },
            PermissionPromptRequestMcp mcp => ReadConnector(mcp, connectorName),
            PermissionPromptRequestCustomTool custom => ReadLumiTool(custom),
            PermissionPromptRequestMemory memory => new ApprovalRequestInfo
            {
                Kind = ApprovalCardKind.Memory,
                Title = Loc.Get("Approval_Memory"),
                IconKey = TranscriptIcons.Memory,
                Subject = memory.Fact,
                Intention = memory.Reason,
                ToolCallId = memory.ToolCallId,
            },
            PermissionPromptRequestHook hook => new ApprovalRequestInfo
            {
                Kind = ApprovalCardKind.Other,
                Title = Loc.Get("Approval_Hook"),
                IconKey = TranscriptIcons.Tool,
                Subject = hook.ToolName,
                Intention = hook.HookMessage,
                Detail = FormatJson(hook.ToolArgs),
                ToolCallId = hook.ToolCallId,
                ToolName = hook.ToolName,
            },
            PermissionPromptRequestWorkflow workflow => new ApprovalRequestInfo
            {
                Kind = ApprovalCardKind.Other,
                Title = Loc.Get("Approval_Workflow"),
                IconKey = TranscriptIcons.Plan,
                Subject = workflow.Name,
                Intention = workflow.Description,
                ToolCallId = workflow.ToolCallId,
                IsAlwaysAsk = workflow.ManagedApprovalRequired == true,
            },
            PermissionPromptRequestExtensionManagement extension => Extension(extension.ExtensionName, extension.Operation, extension.ToolCallId),
            PermissionPromptRequestExtensionPermissionAccess extension => Extension(extension.ExtensionName, null, extension.ToolCallId),
            PermissionPromptRequestExtensionEnvAccess extension => Extension(extension.ExtensionName,
                extension.EnvironmentVariables is { Length: > 0 } names ? string.Join(", ", names) : null, extension.ToolCallId),
            _ => new ApprovalRequestInfo
            {
                Kind = ApprovalCardKind.Other,
                Title = Loc.Get("Approval_Unknown"),
                IconKey = TranscriptIcons.Tool,
                Subject = prompt.Kind,
                Warning = Loc.Get("Approval_UnknownWarning"),
                IsRecognized = false,
            },
        };

        var assisted = AssistedOf(prompt);
        return info with
        {
            IsAlwaysAsk = info.IsAlwaysAsk || alwaysAsk,
            Recommendation = assisted?.Recommendation,
            ReviewerReason = ReviewerNote(assisted),
        };
    }

    /// <summary>The reviewer's own reason when it held a request; plain words when it couldn't judge it.</summary>
    private static string? ReviewerNote(PermissionAssistedApproval? assisted)
    {
        if (assisted?.Recommendation is not { } recommendation || recommendation == AssistedApprovalRecommendation.Approve)
            return null;
        if (recommendation == AssistedApprovalRecommendation.Error)
            return Loc.Get("Approval_ReviewerUnsure");
        if (recommendation == AssistedApprovalRecommendation.Excluded)
            return Loc.Get("Approval_ReviewerSkipped");
        return assisted.Reason;
    }

    public static PermissionAssistedApproval? AssistedOf(PermissionPromptRequest prompt) => prompt switch
    {
        PermissionPromptRequestCommands p => p.AssistedApproval,
        PermissionPromptRequestWrite p => p.AssistedApproval,
        PermissionPromptRequestRead p => p.AssistedApproval,
        PermissionPromptRequestPath p => p.AssistedApproval,
        PermissionPromptRequestUrl p => p.AssistedApproval,
        PermissionPromptRequestMcp p => p.AssistedApproval,
        PermissionPromptRequestCustomTool p => p.AssistedApproval,
        PermissionPromptRequestMemory p => p.AssistedApproval,
        PermissionPromptRequestHook p => p.AssistedApproval,
        PermissionPromptRequestWorkflow p => p.AssistedApproval,
        PermissionPromptRequestExtensionManagement p => p.AssistedApproval,
        PermissionPromptRequestExtensionPermissionAccess p => p.AssistedApproval,
        PermissionPromptRequestExtensionEnvAccess p => p.AssistedApproval,
        _ => null
    };

    private static ApprovalRequestInfo ReadCommands(PermissionPromptRequestCommands commands)
    {
        var identifiers = commands.CommandIdentifiers?.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct().ToList() ?? [];
        var canRemember = commands.CanOfferSessionApproval && identifiers.Count > 0;
        var escalation = commands.RequestSandboxBypass == true;
        return new ApprovalRequestInfo
        {
            Kind = ApprovalCardKind.Command,
            Title = Loc.Get("Approval_RunCommand"),
            IconKey = TranscriptIcons.Terminal,
            Subject = commands.FullCommandText,
            Intention = commands.Intention,
            Warning = escalation
                ? commands.RequestSandboxBypassReason is { Length: > 0 } reason
                    ? Loc.Get("Approval_SandboxBypassReason", reason) : Loc.Get("Approval_SandboxBypass")
                : commands.Warning,
            IsEscalation = escalation,
            ToolCallId = commands.ToolCallId,
            IsAlwaysAsk = commands.ManagedApprovalRequired == true,
            RememberRules = canRemember
                ? identifiers.Select(id => new ApprovalRule { Action = ApprovalRuleAction.Allow, Kind = ApprovalRuleKind.Command, Target = id, Remembered = true }).ToList()
                : [],
            SessionApproval = canRemember
                ? new PermissionDecisionApproveForSessionApprovalCommands { CommandIdentifiers = identifiers }
                : null,
            RememberScope = canRemember ? string.Join(", ", identifiers) : null,
        };
    }

    private static ApprovalRequestInfo ReadWrite(PermissionPromptRequestWrite write)
    {
        var creates = string.IsNullOrEmpty(write.Diff) && write.NewFileContents is not null;
        return new ApprovalRequestInfo
        {
            Kind = ApprovalCardKind.Edit,
            Title = Loc.Get(creates ? "Approval_CreateFile" : "Approval_EditFile"),
            IconKey = creates ? TranscriptIcons.Create : TranscriptIcons.Edit,
            Subject = write.FileName,
            Intention = write.Intention,
            Diff = string.IsNullOrEmpty(write.Diff) ? null : write.Diff,
            Detail = creates ? Truncate(write.NewFileContents, 4000) : null,
            ToolCallId = write.ToolCallId,
            IsAlwaysAsk = write.ManagedApprovalRequired == true,
            RememberRules = write.CanOfferSessionApproval
                ? [new ApprovalRule { Action = ApprovalRuleAction.Allow, Kind = ApprovalRuleKind.EditFiles, Remembered = true }]
                : [],
            SessionApproval = write.CanOfferSessionApproval ? new PermissionDecisionApproveForSessionApprovalWrite() : null,
            RememberScope = write.CanOfferSessionApproval ? Loc.Get("Approval_ScopeFileEdits") : null,
        };
    }

    private static ApprovalRequestInfo ReadConnector(PermissionPromptRequestMcp mcp, Func<string, string>? connectorName)
    {
        var server = connectorName?.Invoke(mcp.ServerName) ?? mcp.ServerName;
        var tool = mcp.ToolTitle is { Length: > 0 } title ? title : mcp.ToolName;
        return new ApprovalRequestInfo
        {
            Kind = ApprovalCardKind.Connector,
            Title = Loc.Get("Approval_UseConnector", server),
            IconKey = TranscriptIcons.Connector,
            Subject = tool,
            Detail = FormatJson(mcp.Args),
            ToolCallId = mcp.ToolCallId,
            ToolName = mcp.ToolName,
            RememberRules = [new ApprovalRule { Action = ApprovalRuleAction.Allow, Kind = ApprovalRuleKind.Connector, Target = $"{server}/{mcp.ToolName}", Remembered = true }],
            SessionApproval = new PermissionDecisionApproveForSessionApprovalMcp { ServerName = mcp.ServerName, ToolName = mcp.ToolName },
            RememberScope = $"{server} › {tool}",
        };
    }

    private static ApprovalRequestInfo ReadLumiTool(PermissionPromptRequestCustomTool custom)
    {
        var argsJson = FormatJson(custom.Args);
        var (name, info) = ToolDisplayHelper.GetFriendlyToolDisplay(custom.ToolName, null, argsJson);
        var group = ApprovalPolicy.GroupOf(custom.ToolName);
        var groupName = group is null ? null : Loc.Get("Approval_Group_" + group);
        return new ApprovalRequestInfo
        {
            Kind = ApprovalCardKind.LumiTool,
            Title = group is null ? Loc.Get("Approval_UseTool", name) : Loc.Get("Approval_GroupTitle_" + group),
            IconKey = ToolDisplayHelper.GetToolIconKey(custom.ToolName),
            Subject = info is { Length: > 0 } ? $"{name} · {info}" : name,
            Intention = custom.ToolDescription is { Length: > 0 } description && group is null ? description : null,
            Detail = argsJson,
            ToolCallId = custom.ToolCallId,
            ToolName = custom.ToolName,
            RememberRules = [new ApprovalRule { Action = ApprovalRuleAction.Allow, Kind = ApprovalRuleKind.LumiTool, Target = group ?? custom.ToolName, Remembered = true }],
            SessionApproval = new PermissionDecisionApproveForSessionApprovalCustomTool { ToolName = custom.ToolName },
            RememberScope = groupName ?? name,
        };
    }

    private static ApprovalRequestInfo Extension(string? extensionName, string? detail, string? toolCallId) => new()
    {
        Kind = ApprovalCardKind.Other,
        Title = Loc.Get("Approval_Extension", extensionName ?? ""),
        IconKey = TranscriptIcons.Tool,
        Subject = detail,
        ToolCallId = toolCallId,
    };

    private static string? FormatJson(object? value)
    {
        if (value is null)
            return null;
        try
        {
            var element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, PermissionDisplayJsonContext.Default.Object);
            if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                return null;
            if (element.ValueKind == JsonValueKind.Object && !element.EnumerateObject().Any())
                return null;
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                element.WriteTo(writer);
            return Truncate(Encoding.UTF8.GetString(stream.ToArray()), 4000);
        }
        catch (Exception)
        {
            return Truncate(value.ToString(), 4000);
        }
    }

    private static string? Truncate(string? text, int max)
        => text is null || text.Length <= max ? text : text[..max] + "…";
}

/// <summary>The SDK's own event JSON context is internal, so approval cards serialize request parts here.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(object))]
[System.Text.Json.Serialization.JsonSerializable(typeof(JsonElement))]
[System.Text.Json.Serialization.JsonSerializable(typeof(PermissionPromptRequest))]
internal partial class PermissionDisplayJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
