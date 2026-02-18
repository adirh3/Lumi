using System;
using System.Collections.Generic;
using System.Linq;
using Lumi.Models;

namespace Lumi.Services;

public static class SystemPromptBuilder
{
    public static string Build(UserSettings settings, LumiAgent? agent, Project? project,
        List<Skill> allSkills, List<Skill> activeSkills, List<Memory> memories)
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


                --- Active Agent: {agent.Name} ---
                {agent.SystemPrompt}
                """;

            // Include agent's linked skills
            if (agent.SkillIds.Count > 0)
            {
                var agentSkills = allSkills.Where(s => agent.SkillIds.Contains(s.Id)).ToList();
                if (agentSkills.Count > 0)
                {
                    prompt += "\n\n--- Agent Skills ---\n";
                    foreach (var skill in agentSkills)
                    {
                        prompt += $"\n### {skill.Name}\n{skill.Content}\n";
                    }
                }
            }
        }

        if (project is not null && !string.IsNullOrWhiteSpace(project.Instructions))
        {
            prompt += $"""


                --- Project: {project.Name} ---
                {project.Instructions}
                """;
        }

        // Active skills selected by the user for this chat (full content)
        if (activeSkills.Count > 0)
        {
            prompt += "\n\n--- Active Skills (use these to help the user) ---\n";
            foreach (var skill in activeSkills)
            {
                prompt += $"\n### {skill.Name}\n{skill.Content}\n";
            }
        }

        // All available skills (summary only, so LLM knows what's available)
        if (allSkills.Count > 0)
        {
            prompt += "\n\n--- All Available Skills (user can activate these with /skill) ---\n";
            foreach (var skill in allSkills)
            {
                var activeMarker = activeSkills.Any(s => s.Id == skill.Id) ? " ✓" : "";
                prompt += $"- **{skill.Name}**: {skill.Description}{activeMarker}\n";
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
