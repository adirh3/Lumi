using System.Linq;
using Lumi.Models;

namespace Lumi.Services;

internal static class AppDataSnapshotFactory
{
    public static AppData CreateIndexSnapshot(AppData source)
    {
        var settings = source.Settings;

        return new AppData
        {
            Settings = new UserSettings
            {
                UserName = settings.UserName,
                UserSex = settings.UserSex,
                IsOnboarded = settings.IsOnboarded,
                DefaultsSeeded = settings.DefaultsSeeded,
                CodingLumiSeeded = settings.CodingLumiSeeded,
                Language = settings.Language,
                LaunchAtStartup = settings.LaunchAtStartup,
                StartMinimized = settings.StartMinimized,
                MinimizeToTray = settings.MinimizeToTray,
                GlobalHotkey = settings.GlobalHotkey,
                NotificationsEnabled = settings.NotificationsEnabled,
                IsDarkTheme = settings.IsDarkTheme,
                IsCompactDensity = settings.IsCompactDensity,
                FontSize = settings.FontSize,
                ShowAnimations = settings.ShowAnimations,
                SendWithEnter = settings.SendWithEnter,
                ShowTimestamps = settings.ShowTimestamps,
                ShowToolCalls = settings.ShowToolCalls,
                ShowReasoning = settings.ShowReasoning,
                AutoGenerateTitles = settings.AutoGenerateTitles,
                PreferredModel = settings.PreferredModel,
                EnableMemoryAutoSave = settings.EnableMemoryAutoSave,
                AutoSaveChats = settings.AutoSaveChats,
                HasImportedBrowserCookies = settings.HasImportedBrowserCookies,
            },
            Chats = source.Chats
                .Select(static c => new Chat
                {
                    Id = c.Id,
                    Title = c.Title,
                    ProjectId = c.ProjectId,
                    AgentId = c.AgentId,
                    CopilotSessionId = c.CopilotSessionId,
                    CreatedAt = c.CreatedAt,
                    UpdatedAt = c.UpdatedAt,
                    ActiveSkillIds = [..c.ActiveSkillIds],
                    ActiveMcpServerNames = [..c.ActiveMcpServerNames]
                })
                .ToList(),
            Projects = source.Projects
                .Select(static p => new Project
                {
                    Id = p.Id,
                    Name = p.Name,
                    Instructions = p.Instructions,
                    CreatedAt = p.CreatedAt
                })
                .ToList(),
            Skills = source.Skills
                .Select(static s => new Skill
                {
                    Id = s.Id,
                    Name = s.Name,
                    Description = s.Description,
                    Content = s.Content,
                    IconGlyph = s.IconGlyph,
                    IsBuiltIn = s.IsBuiltIn,
                    CreatedAt = s.CreatedAt
                })
                .ToList(),
            Agents = source.Agents
                .Select(static a => new LumiAgent
                {
                    Id = a.Id,
                    Name = a.Name,
                    Description = a.Description,
                    SystemPrompt = a.SystemPrompt,
                    IconGlyph = a.IconGlyph,
                    IsBuiltIn = a.IsBuiltIn,
                    IsLearningAgent = a.IsLearningAgent,
                    SkillIds = [..a.SkillIds],
                    ToolNames = [..a.ToolNames],
                    McpServerIds = [..a.McpServerIds],
                    CreatedAt = a.CreatedAt
                })
                .ToList(),
            McpServers = source.McpServers
                .Select(static s => new McpServer
                {
                    Id = s.Id,
                    Name = s.Name,
                    Description = s.Description,
                    ServerType = s.ServerType,
                    Command = s.Command,
                    Args = [..s.Args],
                    Env = new(s.Env),
                    Url = s.Url,
                    Headers = new(s.Headers),
                    Tools = [..s.Tools],
                    Timeout = s.Timeout,
                    IsEnabled = s.IsEnabled,
                    CreatedAt = s.CreatedAt
                })
                .ToList(),
            Memories = source.Memories
                .Select(static m => new Memory
                {
                    Id = m.Id,
                    Key = m.Key,
                    Content = m.Content,
                    Category = m.Category,
                    Source = m.Source,
                    SourceChatId = m.SourceChatId,
                    CreatedAt = m.CreatedAt,
                    UpdatedAt = m.UpdatedAt
                })
                .ToList(),
        };
    }
}
