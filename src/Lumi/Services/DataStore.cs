using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Lumi.Models;

namespace Lumi.Services;

public class DataStore
{
    private static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumi");
    private static readonly string DataFile = Path.Combine(AppDir, "data.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string SkillsDir { get; } = Path.Combine(AppDir, "skills");

    private AppData _data;

    public DataStore()
    {
        Directory.CreateDirectory(AppDir);
        Directory.CreateDirectory(SkillsDir);
        _data = Load();
        SeedDefaults();
    }

    public AppData Data => _data;

    public void Save()
    {
        var json = JsonSerializer.Serialize(_data, JsonOptions);
        File.WriteAllText(DataFile, json);
    }

    /// <summary>
    /// Writes all skills as markdown files in the skills directory for the Copilot SDK.
    /// </summary>
    public void SyncSkillFiles()
    {
        Directory.CreateDirectory(SkillsDir);

        // Remove old files that no longer correspond to a skill
        var existingFiles = Directory.GetFiles(SkillsDir, "*.md");
        var validFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var skill in _data.Skills)
        {
            var safeName = SanitizeFileName(skill.Name);
            var fileName = $"{safeName}.md";
            validFileNames.Add(fileName);

            var filePath = Path.Combine(SkillsDir, fileName);
            var content = $"""
                ---
                name: {skill.Name}
                description: {skill.Description}
                ---

                {skill.Content}
                """;
            File.WriteAllText(filePath, content);
        }

        foreach (var file in existingFiles)
        {
            if (!validFileNames.Contains(Path.GetFileName(file)))
                File.Delete(file);
        }
    }

    /// <summary>
    /// Writes specific skills (by ID) to the skills directory. Returns the directory path.
    /// </summary>
    public string SyncSkillFilesForIds(List<Guid> skillIds)
    {
        var dir = Path.Combine(AppDir, "active-skills");
        Directory.CreateDirectory(dir);

        // Clear previous
        foreach (var f in Directory.GetFiles(dir, "*.md"))
            File.Delete(f);

        var skills = skillIds.Count > 0
            ? _data.Skills.FindAll(s => skillIds.Contains(s.Id))
            : _data.Skills;

        foreach (var skill in skills)
        {
            var safeName = SanitizeFileName(skill.Name);
            var filePath = Path.Combine(dir, $"{safeName}.md");
            var content = $"""
                ---
                name: {skill.Name}
                description: {skill.Description}
                ---

                {skill.Content}
                """;
            File.WriteAllText(filePath, content);
        }

        return dir;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? "skill" : sanitized;
    }

    private void SeedDefaults()
    {
        if (_data.Settings.DefaultsSeeded) return;

        // ── Default Skills ──
        _data.Skills.AddRange([
            new Skill
            {
                Name = "Document Creator",
                Description = "Creates Word, Excel, and PowerPoint documents from user descriptions",
                IconGlyph = "📄",
                IsBuiltIn = true,
                Content = """
                    # Document Creator

                    You can create Office documents for the user. When asked to create a document:

                    1. **Word (.docx)**: Use PowerShell with COM automation or python-docx to create Word documents.
                       Write the content with proper headings, formatting, and structure.
                    2. **Excel (.xlsx)**: Use PowerShell with COM automation or openpyxl to create spreadsheets.
                       Include headers, data formatting, and formulas where appropriate.
                    3. **PowerPoint (.pptx)**: Use PowerShell with COM automation or python-pptx to create presentations.
                       Create slides with titles, content, and professional layout.

                    Always save files to the user's working directory and report the file path.
                    Ask the user what content they want if not specified.
                    """
            },
            new Skill
            {
                Name = "Web Researcher",
                Description = "Searches the web and summarizes findings on any topic",
                IconGlyph = "🔍",
                IsBuiltIn = true,
                Content = """
                    # Web Researcher

                    When the user asks you to research a topic:

                    1. Search the web for relevant, up-to-date information
                    2. Visit the most promising results to gather details
                    3. Synthesize findings into a clear, well-organized summary
                    4. Include key facts, different perspectives, and source references
                    5. Highlight anything the user should be aware of

                    Present research in a readable format with sections and bullet points.
                    If the topic is broad, ask clarifying questions first.
                    """
            },
            new Skill
            {
                Name = "File Organizer",
                Description = "Helps organize, rename, and manage files and folders",
                IconGlyph = "📁",
                IsBuiltIn = true,
                Content = """
                    # File Organizer

                    Help the user organize their files and folders:

                    1. **Analyze**: List and categorize files in specified directories
                    2. **Organize**: Move files into logical folder structures (by type, date, project, etc.)
                    3. **Rename**: Batch rename files using patterns the user specifies
                    4. **Clean up**: Find duplicates, empty folders, or temporary files
                    5. **Summary**: Provide a report of what was organized

                    Always confirm with the user before moving or deleting files.
                    Create a backup plan for destructive operations.
                    """
            },
            new Skill
            {
                Name = "Code Helper",
                Description = "Writes, explains, and debugs code in any language",
                IconGlyph = "💻",
                IsBuiltIn = true,
                Content = """
                    # Code Helper

                    Assist the user with programming tasks:

                    1. **Write code**: Create scripts, applications, or utilities in any language
                    2. **Explain code**: Break down existing code into understandable explanations
                    3. **Debug**: Help find and fix issues in code
                    4. **Refactor**: Improve code structure and readability
                    5. **Convert**: Translate code between languages

                    Write clean, well-commented code. Save files with appropriate extensions.
                    Test scripts when possible by running them.
                    """
            },
            new Skill
            {
                Name = "Email Drafter",
                Description = "Composes professional emails based on context and tone",
                IconGlyph = "✉️",
                IsBuiltIn = true,
                Content = """
                    # Email Drafter

                    Help the user compose emails:

                    1. Ask for the recipient, purpose, and desired tone if not provided
                    2. Draft a clear, professional email with proper greeting and sign-off
                    3. Match the tone: formal, casual, urgent, appreciative, etc.
                    4. Keep emails concise and action-oriented
                    5. Offer to revise based on feedback

                    Adapt writing style to the user's preferences as you learn them.
                    """
            }
        ]);

        // ── Default Agents ──
        _data.Agents.AddRange([
            new LumiAgent
            {
                Name = "Daily Planner",
                Description = "Plans your day, manages tasks, and keeps you on track",
                IconGlyph = "📋",
                IsBuiltIn = true,
                SystemPrompt = """
                    You are the Daily Planner Lumi. Your purpose is to help the user plan and manage their day effectively.

                    Your responsibilities:
                    - Help create daily schedules and to-do lists
                    - Prioritize tasks using urgency and importance
                    - Set time blocks for focused work
                    - Suggest breaks and balance
                    - Review what was accomplished at end of day
                    - Track recurring tasks and habits

                    Be encouraging but realistic about time estimates. Help the user avoid overcommitting.
                    Use the current time of day to provide contextually relevant suggestions.
                    """
            },
            new LumiAgent
            {
                Name = "Creative Writer",
                Description = "Helps with writing, storytelling, and creative content",
                IconGlyph = "✍️",
                IsBuiltIn = true,
                SystemPrompt = """
                    You are the Creative Writer Lumi. You help the user with all forms of creative writing.

                    Your capabilities:
                    - Write stories, poems, essays, and articles
                    - Help brainstorm ideas and overcome writer's block
                    - Edit and improve existing writing
                    - Adapt to different styles and genres
                    - Create outlines and story structures
                    - Generate dialogue and character descriptions

                    Be imaginative and expressive. Match the user's creative vision.
                    Offer constructive suggestions without being prescriptive.
                    """
            },
            new LumiAgent
            {
                Name = "Learning Lumi",
                Description = "Learns about you from your computer to create personalized skills and agents",
                IconGlyph = "🧠",
                IsBuiltIn = true,
                IsLearningAgent = true,
                SystemPrompt = """
                    You are Learning Lumi, a specialized agent that learns about the user to create personalized experiences.

                    Your mission is to understand the user by exploring their computer (with permission) and conversations:

                    ## What to Learn
                    - **Work patterns**: What software they use, what files they work with, their profession
                    - **Interests**: Topics they research, content they create, hobbies
                    - **Workflows**: Repetitive tasks they do that could become skills
                    - **Communication style**: How they write, preferred tone, language patterns
                    - **Tools & preferences**: Editors, browsers, apps they rely on

                    ## How to Learn
                    1. Ask the user what you can explore (documents folder, desktop, recent files, etc.)
                    2. Look at file types, folder structures, and recently modified files
                    3. Note patterns in their work (e.g., "they work with spreadsheets a lot")
                    4. Suggest new Skills or Agents based on what you discover

                    ## Creating Skills & Agents
                    When you identify a pattern or need, propose creating:
                    - **Skills**: For specific tasks (e.g., "Budget Tracker" if they use many spreadsheets)
                    - **Agents**: For workflow areas (e.g., "Project Manager" if they manage multiple projects)

                    Format your proposals clearly:
                    ```
                    🆕 Proposed Skill: [Name]
                    Description: [What it does]
                    Why: [What you observed that suggests this would help]
                    ```

                    Always ask permission before exploring files. Be transparent about what you find.
                    Never read sensitive files (passwords, keys, financial data) - skip them.
                    Focus on patterns and metadata, not private content.
                    """
            }
        ]);

        _data.Settings.DefaultsSeeded = true;
        Save();
        SyncSkillFiles();
    }

    private static AppData Load()
    {
        if (!File.Exists(DataFile))
            return new AppData();

        var json = File.ReadAllText(DataFile);
        return JsonSerializer.Deserialize<AppData>(json, JsonOptions) ?? new AppData();
    }
}
