using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;

namespace Lumi.Services;

public class DataStore
{
    private static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumi");
    private static readonly string DataFile = Path.Combine(AppDir, "data.json");

    public static string SkillsDir { get; } = Path.Combine(AppDir, "skills");
    public static string ChatsDir { get; } = Path.Combine(AppDir, "chats");

    private AppData _data;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public DataStore()
    {
        Directory.CreateDirectory(AppDir);
        Directory.CreateDirectory(SkillsDir);
        Directory.CreateDirectory(ChatsDir);
        _data = Load();
        SeedDefaults();
    }

    public AppData Data => _data;

    /// <summary>
    /// Saves the index file (settings, chat metadata, projects, skills, agents, memories).
    /// Does NOT save chat messages — use SaveChat() for that.
    /// </summary>
    public void Save()
    {
        SaveAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Saves the index file (settings, chat metadata, projects, skills, agents, memories).
    /// Does NOT save chat messages — use SaveChatAsync() for that.
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = AppDataSnapshotFactory.CreateIndexSnapshot(_data);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(
                DataFile,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous);

            await JsonSerializer.SerializeAsync(
                stream,
                snapshot,
                AppDataJsonContext.Default.AppData,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Saves a chat's messages to its per-chat file.</summary>
    public void SaveChat(Chat chat)
    {
        SaveChatAsync(chat).GetAwaiter().GetResult();
    }

    /// <summary>Saves a chat's messages to its per-chat file.</summary>
    public async Task SaveChatAsync(Chat chat, CancellationToken cancellationToken = default)
    {
        var chatFile = Path.Combine(ChatsDir, $"{chat.Id}.json");
        var messagesSnapshot = chat.Messages
            .Select(static m => new ChatMessage
            {
                Id = m.Id,
                Role = m.Role,
                Content = m.Content,
                Author = m.Author,
                Timestamp = m.Timestamp,
                ToolName = m.ToolName,
                ToolCallId = m.ToolCallId,
                ToolStatus = m.ToolStatus,
                ToolOutput = m.ToolOutput,
                IsStreaming = m.IsStreaming,
                Attachments = [..m.Attachments],
                ActiveSkills = [..m.ActiveSkills.Select(static s => new SkillReference { Name = s.Name, Glyph = s.Glyph })],
                Sources = [..m.Sources.Select(static s => new SearchSource
                {
                    Title = s.Title,
                    Snippet = s.Snippet,
                    Url = s.Url
                })]
            })
            .ToList();

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(
                chatFile,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous);

            await JsonSerializer.SerializeAsync(
                stream,
                messagesSnapshot,
                AppDataJsonContext.Default.ListChatMessage,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Loads messages from a chat's per-chat file into chat.Messages.</summary>
    public async Task LoadChatMessagesAsync(Chat chat, CancellationToken cancellationToken = default)
    {
        if (chat.Messages.Count > 0) return; // Already loaded

        var chatFile = Path.Combine(ChatsDir, $"{chat.Id}.json");
        if (!File.Exists(chatFile)) return;

        try
        {
            await using var stream = File.OpenRead(chatFile);
            var messages = await JsonSerializer.DeserializeAsync(
                stream,
                AppDataJsonContext.Default.ListChatMessage,
                cancellationToken).ConfigureAwait(false);

            if (messages is not null)
                chat.Messages.AddRange(messages);
        }
        catch (IOException)
        {
            // Ignore transient file IO issues; chat will open without history.
        }
        catch (JsonException)
        {
            // Ignore malformed chat files; chat will open without history.
        }
    }

    /// <summary>Deletes the per-chat file for a given chat ID.</summary>
    public void DeleteChatFile(Guid chatId)
    {
        var chatFile = Path.Combine(ChatsDir, $"{chatId}.json");
        if (File.Exists(chatFile))
            File.Delete(chatFile);
    }

    /// <summary>Deletes all per-chat files.</summary>
    public void DeleteAllChatFiles()
    {
        if (Directory.Exists(ChatsDir))
        {
            foreach (var file in Directory.GetFiles(ChatsDir, "*.json"))
                File.Delete(file);
        }
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
        return SyncSkillFilesForIdsAsync(skillIds).GetAwaiter().GetResult();
    }

    public async Task<string> SyncSkillFilesForIdsAsync(List<Guid> skillIds, CancellationToken cancellationToken = default)
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
            await File.WriteAllTextAsync(filePath, content, cancellationToken).ConfigureAwait(false);
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
            },
            new Skill
            {
                Name = "Website Creator",
                Description = "Creates beautiful interactive websites from chat content and opens them in Lumi's browser",
                IconGlyph = "🌐",
                IsBuiltIn = true,
                Content = """
                    # Website Creator

                    Transform any content from the conversation into a beautiful, modern, interactive single-page website and present it in Lumi's built-in browser.

                    ## When to Use
                    Use this skill whenever the user asks you to visualize, present, or turn conversation content into a website or webpage. This works for any content type: itineraries, reports, plans, guides, comparisons, portfolios, dashboards, timelines, recipes, study notes, or anything else.

                    ## How to Create the Website

                    1. **Gather the content** — Use the information already discussed in the conversation. If important details are missing, ask the user before proceeding.

                    2. **Generate a single self-contained HTML file** — The entire website must be in ONE `.html` file with all CSS and JavaScript inlined (no external dependencies except CDN links for fonts/icons). Use modern web standards:
                       - **Clean semantic HTML5** structure
                       - **Modern CSS** with gradients, shadows, smooth transitions, and animations
                       - **Responsive layout** that works at any window size
                       - **Dark/light theme** — detect system preference with `prefers-color-scheme` and include a toggle button
                       - **Smooth scrolling** and scroll-triggered reveal animations
                       - **Interactive elements** — collapsible sections, tabs, hover effects, modals, tooltips, or cards as appropriate for the content
                       - **Visual hierarchy** — use color, spacing, typography, and layout to make information scannable and beautiful
                       - **Icons** — use inline SVG icons or a CDN icon library (e.g., Lucide, Heroicons, or Font Awesome via CDN) to add visual polish
                       - **Images** — when the content involves places, items, or concepts that benefit from imagery, use placeholder images from Lorem Picsum (`https://picsum.photos/WIDTH/HEIGHT?random=N` where N is a unique number per image) or generate CSS gradient/pattern backgrounds as decorative visuals. Always provide meaningful alt text.

                    3. **Design principles**:
                       - Use a harmonious color palette (2-3 primary colors with neutrals)
                       - Typography: use Google Fonts via CDN for headings (e.g., Inter, Poppins, or Playfair Display) and system font stack for body
                       - Generous whitespace and padding
                       - Card-based layouts for grouped content
                       - Subtle micro-animations (fade-in, slide-up on scroll via IntersectionObserver)
                       - Professional, polished look — as if designed by a UI designer
                       - Ensure text contrast meets accessibility standards (WCAG AA)

                    4. **Content-specific patterns** — adapt the layout to the content type:
                       - **Itineraries/timelines**: Day-by-day cards or a vertical timeline with icons and images for each stop
                       - **Reports/analysis**: Dashboard-style with stat cards, charts (use Chart.js from CDN if needed), and sections
                       - **Guides/how-tos**: Step-by-step layout with numbered sections, progress indicator, and collapsible details
                       - **Comparisons**: Side-by-side cards or a comparison table with highlighted differences
                       - **Lists/collections**: Filterable/searchable grid of cards with images and descriptions
                       - **Plans/projects**: Kanban-style or milestone timeline with status indicators
                       - **Recipes**: Ingredient sidebar + step-by-step instructions with timers
                       - **Profiles/portfolios**: Hero banner with bio, grid of work/projects
                       - **Study notes/knowledge**: Table of contents sidebar, collapsible sections, highlight boxes for key concepts

                    5. **Save the file** — Use the `create` tool to write the HTML file. Use this exact path format:
                       - Path: `C:\Users\<username>\Documents\lumi-website-<short-slug>.html`
                       - Use the user's Documents folder (resolve from `$env:USERPROFILE` if needed via a quick PowerShell call).
                       - Use a short descriptive slug (e.g., `lumi-website-tokyo-itinerary.html`).

                    6. **Open in Lumi's browser** — Use the `browser` tool to navigate to the local file URL:
                       - Convert the file path to a `file:///` URL: replace backslashes with forward slashes and prefix with `file:///`.
                       - Example: `C:\Users\John\Documents\lumi-website-tokyo.html` → `file:///C:/Users/John/Documents/lumi-website-tokyo.html`
                       - This opens the website inside Lumi's built-in browser panel so the user sees it immediately.

                    7. **Announce it** — Call `announce_file(filePath)` with the HTML file path so the user gets a clickable attachment. Then tell the user the website is ready and summarize what it contains.

                    ## Important Rules
                    - The HTML file MUST be fully self-contained and valid. All styles and scripts are inlined or loaded from CDNs.
                    - Never use `localhost` or start a web server. Just save an `.html` file and open it with the `browser` tool.
                    - Make the website genuinely impressive — not a basic page with plain text. Use modern CSS, animations, and interactivity.
                    - If the conversation content is long, organize it into navigable sections with a sticky navigation bar or sidebar.
                    - Always include a header/hero section with a title and brief description.
                    - Include a small footer with "Created with Lumi ✦" branding.
                    - If any external CDN resources fail to load (e.g., Google Fonts), the page should still look good with fallback system fonts.
                    - Use `charset="UTF-8"` in the HTML head to support all languages and special characters.
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
        return JsonSerializer.Deserialize(json, AppDataJsonContext.Default.AppData) ?? new AppData();
    }
}
