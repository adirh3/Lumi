using System;
using System.Collections.Generic;
using Lumi.Models;

namespace Lumi.Services;

public static class SystemPromptBuilder
{
    public static string Build(UserSettings settings, LumiAgent? agent, Project? project, List<Skill> skills, List<Memory> memories)
    {
        var userName = settings.UserName ?? "there";
        var timeOfDay = GetTimeOfDay();
        var now = DateTimeOffset.Now;

        var prompt = $"""
            You are Lumi, a warm and capable personal assistant.
            The user's name is {userName}. Address them naturally.
            It is currently {now:dddd, MMMM d, yyyy} at {now:h:mm tt} ({timeOfDay}).

            Be concise, helpful, and friendly. Use markdown for formatting when helpful.
            When the user shares important personal information, preferences, or facts about themselves, note it clearly so it can be remembered.
            """;

        if (agent is not null)
        {
            prompt += $"""


                --- Agent: {agent.Name} ---
                {agent.SystemPrompt}
                """;
        }

        if (project is not null && !string.IsNullOrWhiteSpace(project.Instructions))
        {
            prompt += $"""


                --- Project: {project.Name} ---
                {project.Instructions}
                """;
        }

        if (skills.Count > 0)
        {
            prompt += "\n\n--- Available Skills ---\n";
            foreach (var skill in skills)
            {
                prompt += $"- **{skill.Name}**: {skill.Description}\n";
            }
        }

        if (memories.Count > 0)
        {
            prompt += "\n\n--- User Memories ---\n";
            foreach (var memory in memories)
            {
                prompt += $"- {memory.Content}\n";
            }
        }

        return prompt;
    }

    private static string GetTimeOfDay()
    {
        var hour = DateTime.Now.Hour;
        return hour switch
        {
            < 6 => "late night",
            < 12 => "morning",
            < 17 => "afternoon",
            < 21 => "evening",
            _ => "night"
        };
    }
}
