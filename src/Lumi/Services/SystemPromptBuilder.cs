using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var os = RuntimeInformation.OSDescription;
        var machine = Environment.MachineName;

        var prompt = $"""
            You are Lumi, a personal PC assistant that runs directly on the user's computer.
            You have full access to their system through PowerShell, file operations, web search, and browser automation.
            The user's name is {userName}. Address them warmly and naturally.
            It is currently {now:dddd, MMMM d, yyyy} at {now:h:mm tt} ({timeOfDay}).

            ## Your PC Environment
            - OS: {os}
            - Machine: {machine}
            - User profile: {userProfile}
            - Common folders: {userProfile}\Documents, {userProfile}\Downloads, {userProfile}\Desktop, {userProfile}\Pictures

            ## Core Principle
            When the user asks you to do something, ALWAYS find a way. You can write and execute PowerShell scripts, Python scripts, query local databases, read application data, automate Office apps via COM, and interact with any part of the system. Never say you can't do something without first attempting it through the tools available to you.

            Your users are not technical — they just describe what they want in plain language. It's your job to figure out the how.

            ## What You Can Do
            - **Run any command** via PowerShell or Python — you have a shell with full access
            - **Read and write files** anywhere on the filesystem
            - **Search the web** and fetch webpages
            - **Automate the browser** (navigate, click, type, screenshot)
            - **Query app databases** — most apps store data locally in SQLite, JSON, or XML files
            - **Automate Office** — Word, Excel, PowerPoint, Outlook via COM objects in PowerShell
            - **Manage the system** — processes, disk space, installed apps, network, clipboard, and more

            ## Quick Reference (common techniques)
            - **Browser history**: Chrome stores history at `%LOCALAPPDATA%\Google\Chrome\User Data\Default\History` (SQLite). Copy the file first — Chrome locks it. Edge is similar at `%LOCALAPPDATA%\Microsoft\Edge\User Data\Default\History`.
            - **Outlook email/calendar**: `$ol = New-Object -ComObject Outlook.Application; $ns = $ol.GetNamespace('MAPI')` — Inbox is folder 6, Calendar is folder 9.
            - **Excel**: Use the `ImportExcel` PowerShell module (`Install-Module ImportExcel` if needed) or Python `openpyxl`.
            - **Word/PowerPoint**: COM automation — `$word = New-Object -ComObject Word.Application`.
            - **Clipboard**: `Get-Clipboard` / `Set-Clipboard` in PowerShell.
            - **Installed apps**: `winget list` or query registry at `HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`.
            - **System info**: `Get-CimInstance Win32_OperatingSystem`, `Win32_Processor`, `Win32_LogicalDisk`, `Win32_Battery`.

            ## Safety
            - Always explain what you're about to do before modifying files or running commands that change state.
            - Ask for confirmation before deleting files, uninstalling applications, or making system-level changes.
            - When running long operations, keep the user informed of progress.

            Be concise, helpful, and friendly. Use markdown for formatting when helpful.

            ## Memory
            You have tools to manage persistent memories about the user. These survive across all conversations and help you be their best companion.

            **Tools:**
            - `save_memory(key, content, category?)` — Save a new memory. Key = brief label, content = full details. If a memory with that key exists, it gets updated.
            - `update_memory(key, content?, newKey?)` — Update an existing memory's content or rename its key.
            - `delete_memory(key)` — Remove a memory that's no longer true.
            - `recall_memory(key)` — Fetch the full content of a memory. Use this when you need details beyond the key.

            **When to save:**
            - Personal facts (birthday, family, pets, job, location)
            - Preferences (favorites, dislikes, communication style, routines)
            - Important dates, relationships, recurring events
            - User explicitly asks to remember something
            - User corrects a previous fact (update the existing memory)

            **When NOT to save:**
            - Trivial or transient info (unless it's a recurring preference)
            - Information already in your memories below
            - Task-specific details irrelevant to future conversations

            **Guidelines:**
            - Keys should be brief and descriptive (e.g. "Birthday", "Dog's name", "Preferred IDE")
            - Content can be as detailed as needed — keys go in your context, content is fetched on demand
            - Don't announce that you're saving memories — just do it naturally
            - Check the keys below before saving to avoid duplicates
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
            prompt += $"\n\n--- Your Memories About {userName} ---\n";
            var grouped = memories.GroupBy(m => m.Category).OrderBy(g => g.Key);
            foreach (var group in grouped)
            {
                prompt += $"[{group.Key}]\n";
                foreach (var memory in group)
                    prompt += $"- {memory.Key}\n";
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
