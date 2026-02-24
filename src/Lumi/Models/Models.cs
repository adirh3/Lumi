using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Lumi.Models;

public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Role { get; set; } = "user"; // user, assistant, system, tool, reasoning
    public string Content { get; set; } = "";
    public string? Author { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string? ToolName { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolStatus { get; set; } // InProgress, Completed, Failed
    public bool IsStreaming { get; set; }
    public List<string> Attachments { get; set; } = [];
    public List<SearchSource> Sources { get; set; } = [];
    public List<SkillReference> ActiveSkills { get; set; } = [];
}

public class SkillReference
{
    public string Name { get; set; } = "";
    public string Glyph { get; set; } = "\u26A1";
}

public class SearchSource
{
    public string Title { get; set; } = "";
    public string Snippet { get; set; } = "";
    public string Url { get; set; } = "";
}

public class Chat
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "New Chat";
    public Guid? ProjectId { get; set; }
    public Guid? AgentId { get; set; }
    public string? CopilotSessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    [JsonIgnore]
    public List<ChatMessage> Messages { get; set; } = [];
    public List<Guid> ActiveSkillIds { get; set; } = [];
    public List<string> ActiveMcpServerNames { get; set; } = [];
}

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Instructions { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public class Skill
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Content { get; set; } = ""; // Markdown instructions
    public string IconGlyph { get; set; } = "⚡";
    public bool IsBuiltIn { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public class LumiAgent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public string IconGlyph { get; set; } = "✦";
    public bool IsBuiltIn { get; set; }
    public bool IsLearningAgent { get; set; }
    public List<Guid> SkillIds { get; set; } = [];
    public List<string> ToolNames { get; set; } = [];
    public List<Guid> McpServerIds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public class McpServer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ServerType { get; set; } = "local"; // "local" or "remote"

    // Local server (stdio) properties
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = [];
    public Dictionary<string, string> Env { get; set; } = [];

    // Remote server (SSE) properties
    public string Url { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = [];

    public List<string> Tools { get; set; } = [];
    public int? Timeout { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public class Memory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = "";
    public string Content { get; set; } = "";
    public string Category { get; set; } = "General";
    public string? SourceChatId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public class UserSettings
{
    // ── General ──
    public string? UserName { get; set; }
    public string? UserSex { get; set; } // "male", "female", or null (prefer not to say)
    public bool IsOnboarded { get; set; }
    public bool DefaultsSeeded { get; set; }
    public string Language { get; set; } = "en";
    public bool LaunchAtStartup { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; }
    public string GlobalHotkey { get; set; } = "";
    public bool NotificationsEnabled { get; set; } = true;

    // ── Appearance ──
    public bool IsDarkTheme { get; set; } = true;
    public bool IsCompactDensity { get; set; }
    public int FontSize { get; set; } = 14;
    public bool ShowAnimations { get; set; } = true;

    // ── Chat ──
    public bool SendWithEnter { get; set; } = true;
    public bool ShowTimestamps { get; set; } = true;
    public bool ShowToolCalls { get; set; } = true;
    public bool ShowReasoning { get; set; } = true;
    public bool AutoGenerateTitles { get; set; } = true;
    public int MaxContextMessages { get; set; } = 50;

    // ── AI & Models ──
    public string PreferredModel { get; set; } = "";

    // ── Privacy & Data ──
    public bool EnableMemoryAutoSave { get; set; } = true;
    public bool AutoSaveChats { get; set; } = true;

    // ── Browser ──
    public bool HasImportedBrowserCookies { get; set; }
}

public class AppData
{
    public UserSettings Settings { get; set; } = new();
    public List<Chat> Chats { get; set; } = [];
    public List<Project> Projects { get; set; } = [];
    public List<Skill> Skills { get; set; } = [];
    public List<LumiAgent> Agents { get; set; } = [];
    public List<McpServer> McpServers { get; set; } = [];
    public List<Memory> Memories { get; set; } = [];
}
