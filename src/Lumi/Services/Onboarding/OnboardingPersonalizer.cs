using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Localization;

namespace Lumi.Services.Onboarding;

/// <summary>The three things onboarding asks about. Each answer becomes one durable memory.</summary>
public static class OnboardingSlots
{
    public const string Work = "work";
    public const string Help = "help";
    public const string Interests = "interests";

    public static readonly string[] All = [Work, Help, Interests];
}

public sealed record OnboardingQuestion(string Slot, string Text, IReadOnlyList<string> Options);

/// <summary>A first task to offer on the ready screen; it may belong to one of the discovered projects.</summary>
public sealed record OnboardingStarterIdea(string Title, string Prompt, string? ProjectName, string Icon);

public sealed record OnboardingPersonalization(
    string Observation,
    IReadOnlyList<OnboardingQuestion> Questions,
    IReadOnlyList<OnboardingStarterIdea> Starters,
    bool IsPersonalized);

public sealed record OnboardingMemory(string Key, string Content, string Category);

/// <summary>
/// Turns the local workspace summary into Lumi's first words, three tailored questions and three first
/// tasks with a single tool-free model call. Every piece has a deterministic fallback, so onboarding works
/// the same without an AI account, offline, or when the model answers badly.
/// </summary>
public static class OnboardingPersonalizer
{
    private const int MaxObservationLength = 280;
    private const int MaxQuestionLength = 90;
    private const int MaxOptionLength = 32;
    private const int OptionsPerQuestion = 6;
    private const int StarterCount = 3;

    public static async Task<OnboardingPersonalization> GenerateAsync(
        CopilotService copilot,
        string userName,
        WorkspaceSnapshot workspace,
        CancellationToken ct)
    {
        var fallback = Fallback(userName, workspace);
        var raw = await copilot.UseLightweightSessionAsync(
            new LightweightSessionOptions
            {
                SystemPrompt = BuildSystemPrompt(Loc.Culture),
                Streaming = false,
            },
            async (session, innerCt) =>
            {
                var result = await session.SendAndWaitAsync(
                    new MessageOptions { Prompt = BuildPrompt(userName, workspace) },
                    TimeSpan.FromSeconds(45),
                    innerCt).ConfigureAwait(false);
                return result?.Data?.Content;
            },
            ct).ConfigureAwait(false);

        return Parse(raw, workspace, fallback) ?? fallback;
    }

    internal static string BuildSystemPrompt(CultureInfo culture)
    {
        var language = culture.TwoLetterISOLanguageName switch
        {
            "iv" or "" => "English",
            _ => culture.Parent is { Name.Length: > 0 } parent ? parent.EnglishName : culture.EnglishName,
        };

        return $$"""
            You personalize the first-run welcome of Lumi, a personal AI agent that works on the user's own computer. Lumi chats, runs commands, manages files, drives the browser and desktop apps, creates documents, remembers facts, organizes work into projects and runs background jobs.

            You receive a short summary of what is on the user's computer. Reply with ONLY one JSON object (no prose, no code fences) in exactly this shape:
            {"observation":"...","questions":[{"slot":"work","question":"...","options":["..."]},{"slot":"help","question":"...","options":["..."]},{"slot":"interests","question":"...","options":["..."]}],"starters":[{"title":"...","prompt":"...","project":"","icon":"..."}]}

            Rules:
            - Write every user-facing string in {{language}}. You are Lumi, speaking in the first person to the user.
            - observation: one or two warm, specific sentences (at most 220 characters) showing you noticed what they work on or enjoy. Mention one or two concrete projects, apps or interests from the summary. Never mention paths, counts or anything sensitive (health, money, dating, adult content, politics, religion). Never sound creepy or judgmental, and do not ask a question.
            - questions: exactly three, in this slot order. Each question is short (at most 70 characters) and has exactly 6 options of 1-3 words each, in sentence case, without commas or emoji. Tailor the options to the summary, but keep at least two broad options anyone could pick. Never offer catch-alls such as "Other" or "Something else": the user can type their own answer.
              - work: their line of work. Options are fields or kinds of work, for example "Software development".
              - help: what they want Lumi's help with most. Options name kinds of help and read naturally after "help with", for example "Code review" or "Trip planning", never commands such as "Review code".
              - interests: what they enjoy outside work. Options are hobbies or interests.
            - starters: exactly three tasks the user could hand Lumi right now, tailored to the summary. "title" is at most 32 characters. "prompt" is the user's own request to Lumi (at most 200 characters): concrete and safe, so it reads, summarizes, explains, plans, organizes suggestions or drafts, and never deletes, moves, sends, buys or changes settings. When a starter is about one of the listed projects, set "project" to that project's exact name, otherwise "". "icon" is one fitting emoji.
            """;
    }

    internal static string BuildPrompt(string userName, WorkspaceSnapshot workspace)
    {
        var prompt = new StringBuilder();
        prompt.Append("Name: ").AppendLine(string.IsNullOrWhiteSpace(userName) ? "(not given)" : userName.Trim());

        if (workspace.IsEmpty)
        {
            prompt.AppendLine("The user chose not to share details about their computer, so keep everything broad.");
            return prompt.ToString();
        }

        var code = workspace.Projects.Where(project => project.Kind == DiscoveredProjectKind.Code).ToList();
        if (code.Count > 0)
        {
            prompt.AppendLine("Code projects, most recent first:");
            foreach (var project in code)
            {
                prompt.Append("- ").Append(project.Name);
                if (project.Stack is { Length: > 0 } stack)
                    prompt.Append(" (").Append(stack).Append(')');
                if (Activity(project.LastActive) is { } activity)
                    prompt.Append(", ").Append(activity);
                if (project.Description is { Length: > 0 } description)
                    prompt.Append(": ").Append(description);
                prompt.AppendLine();
            }
        }

        var folders = workspace.Projects.Where(project => project.Kind == DiscoveredProjectKind.Folder).ToList();
        if (folders.Count > 0)
        {
            prompt.AppendLine("Folders of recent documents:");
            foreach (var folder in folders)
                prompt.Append("- ").Append(folder.Name).Append(" (").Append(folder.DocumentCount).AppendLine(" recent documents)");
        }

        if (workspace.Apps.Count > 0)
            prompt.Append("Apps: ").AppendLine(string.Join(", ", workspace.Apps));
        if (workspace.Sites.Count > 0)
            prompt.Append("Most visited sites: ").AppendLine(string.Join(", ", workspace.Sites));
        if (workspace.Documents.Recent > 0 || workspace.Documents.Downloads > 0)
        {
            prompt.Append(CultureInfo.InvariantCulture,
                $"Recent files: {workspace.Documents.Recent} documents this month ({workspace.Documents.Pdfs} PDFs, {workspace.Documents.Office} Office files); the Downloads folder holds {workspace.Documents.Downloads} files.");
            prompt.AppendLine();
        }

        return prompt.ToString();
    }

    private static string? Activity(DateTimeOffset? lastActive)
    {
        if (lastActive is not { } active)
            return null;
        var days = (int)Math.Floor((DateTimeOffset.Now - active).TotalDays);
        return days switch
        {
            <= 0 => "active today",
            1 => "active yesterday",
            < 30 => $"active {days} days ago",
            _ => $"active {days / 30} months ago",
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Parsing
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads the model's JSON, keeping every well-formed piece and filling the rest from
    /// <paramref name="fallback"/>. Returns null when nothing usable came back.
    /// </summary>
    internal static OnboardingPersonalization? Parse(string? raw, WorkspaceSnapshot workspace, OnboardingPersonalization fallback)
    {
        if (ExtractJsonObject(raw) is not { } json)
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var observation = CleanText(GetString(root, "observation"), MaxObservationLength);
            var personalized = observation.Length > 0;

            var questionElements = root.TryGetProperty("questions", out var questions) && questions.ValueKind == JsonValueKind.Array
                ? questions.EnumerateArray().Where(element => element.ValueKind == JsonValueKind.Object).ToList()
                : [];
            var parsedQuestions = new List<OnboardingQuestion>();
            for (var index = 0; index < OnboardingSlots.All.Length; index++)
            {
                var slot = OnboardingSlots.All[index];
                var element = questionElements.FirstOrDefault(candidate =>
                    string.Equals(GetString(candidate, "slot"), slot, StringComparison.OrdinalIgnoreCase));
                if (element.ValueKind != JsonValueKind.Object && index < questionElements.Count)
                    element = questionElements[index];

                var text = CleanText(GetString(element, "question"), MaxQuestionLength);
                var options = CleanOptions(element, slot);
                if (text.Length > 0 && options.Count >= 3)
                {
                    parsedQuestions.Add(new OnboardingQuestion(slot, text, options));
                    personalized = true;
                }
                else
                {
                    parsedQuestions.Add(fallback.Questions[index]);
                }
            }

            var starters = ParseStarters(root, workspace);
            personalized |= starters.Count > 0;
            if (!personalized)
                return null;

            return new OnboardingPersonalization(
                observation.Length > 0 ? observation : fallback.Observation,
                parsedQuestions,
                starters.Concat(fallback.Starters.Where(idea => starters.All(starter =>
                        !string.Equals(starter.Title, idea.Title, StringComparison.OrdinalIgnoreCase))))
                    .Take(StarterCount)
                    .ToList(),
                IsPersonalized: true);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<OnboardingStarterIdea> ParseStarters(JsonElement root, WorkspaceSnapshot workspace)
    {
        var starters = new List<OnboardingStarterIdea>();
        if (!root.TryGetProperty("starters", out var array) || array.ValueKind != JsonValueKind.Array)
            return starters;

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;

            var title = CleanText(GetString(element, "title"), 40).TrimEnd('.');
            var prompt = CleanText(GetString(element, "prompt"), 300);
            if (title.Length == 0 || prompt.Length == 0)
                continue;

            var projectName = GetString(element, "project")?.Trim();
            var project = string.IsNullOrEmpty(projectName)
                ? null
                : workspace.Projects.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, projectName, StringComparison.OrdinalIgnoreCase))?.Name;
            starters.Add(new OnboardingStarterIdea(title, prompt, project, CleanIcon(GetString(element, "icon"))));
            if (starters.Count == StarterCount)
                break;
        }

        return starters;
    }

    private static string? ExtractJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : null;
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static string CleanText(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var clean = Whitespace.Replace(text, " ").Trim().Trim('"');
        return WorkspaceDiscovery.Shorten(clean, maxLength);
    }

    /// <summary>
    /// The model's options for one question. An option Lumi's memory rules would not keep is never offered,
    /// so a pick never silently disappears from what onboarding remembers.
    /// </summary>
    private static List<string> CleanOptions(JsonElement question, string slot)
    {
        if (question.ValueKind != JsonValueKind.Object
            || !question.TryGetProperty("options", out var options)
            || options.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return options.EnumerateArray()
            .Where(option => option.ValueKind == JsonValueKind.String)
            .Select(option => CleanOption(option.GetString()))
            .Where(option => option.Length > 0 && option.Length <= MaxOptionLength && !IsCatchAll(option))
            .Where(option => BuildMemory(slot, "", [option]) is { } memory && IsKept(slot, memory))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(OptionsPerQuestion)
            .ToList();
    }

    private static readonly HashSet<string> CatchAlls = new(StringComparer.CurrentCultureIgnoreCase)
    {
        "something else", "other", "others", "anything else", "none", "none of these", "none of the above",
        "not sure", "prefer not to say", "משהו אחר", "אחר", "אחרים", "אף אחד", "לא בטוח",
    };

    /// <summary>"Other" says nothing worth remembering; the card's free-text box covers it.</summary>
    internal static bool IsCatchAll(string option) => CatchAlls.Contains(option.Trim().TrimEnd('.', '…'));

    /// <summary>Options are joined with commas when several are picked, so they must not contain any.</summary>
    internal static string CleanOption(string? option)
    {
        if (string.IsNullOrWhiteSpace(option))
            return "";
        var clean = option.Replace(',', ' ').Replace(';', ' ');
        return Whitespace.Replace(clean, " ").Trim().TrimEnd('.');
    }

    private static string CleanIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
            return "✨";
        var first = StringInfo.GetNextTextElement(icon.Trim());
        return first.Any(char.IsLetterOrDigit) ? "✨" : first;
    }

    // ═══════════════════════════════════════════════════════════════
    // Fallback
    // ═══════════════════════════════════════════════════════════════

    /// <summary>What onboarding says and asks when no model can personalize it.</summary>
    internal static OnboardingPersonalization Fallback(string userName, WorkspaceSnapshot workspace)
    {
        var name = userName.Trim();
        var projects = workspace.Projects.Take(2).Select(project => project.Name).ToList();
        var apps = workspace.Apps.Take(projects.Count > 0 ? 2 : 3).ToList();

        string observation;
        if (projects.Count > 0)
        {
            observation = string.Format(CultureInfo.CurrentCulture, Loc.Onboarding_ObservationProjects, JoinNaturally(projects));
            if (apps.Count > 0)
                observation += " " + string.Format(CultureInfo.CurrentCulture, Loc.Onboarding_ObservationApps, JoinNaturally(apps));
            observation += " " + Loc.Onboarding_ObservationAsk;
        }
        else if (apps.Count > 0)
        {
            observation = string.Format(CultureInfo.CurrentCulture, Loc.Onboarding_ObservationAppsOnly, JoinNaturally(apps))
                          + " " + Loc.Onboarding_ObservationAsk;
        }
        else
        {
            observation = string.Format(CultureInfo.CurrentCulture, Loc.Onboarding_ObservationDefault, name);
        }

        return new OnboardingPersonalization(observation, DefaultQuestions(), FallbackStarters(workspace), IsPersonalized: false);
    }

    internal static IReadOnlyList<OnboardingQuestion> DefaultQuestions() =>
    [
        new(OnboardingSlots.Work, Loc.Onboarding_QuestionWork, SplitOptions(Loc.Onboarding_OptionsWork)),
        new(OnboardingSlots.Help, Loc.Onboarding_QuestionHelp, SplitOptions(Loc.Onboarding_OptionsHelp)),
        new(OnboardingSlots.Interests, Loc.Onboarding_QuestionInterests, SplitOptions(Loc.Onboarding_OptionsInterests)),
    ];

    private static List<string> SplitOptions(string options)
        => options.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>First tasks that fit what discovery found, without a model.</summary>
    internal static List<OnboardingStarterIdea> FallbackStarters(WorkspaceSnapshot workspace)
    {
        var starters = new List<OnboardingStarterIdea>();
        foreach (var project in workspace.Projects.Take(4))
        {
            if (project.Kind == DiscoveredProjectKind.Code && starters.All(starter => starter.Icon != "🧭"))
            {
                starters.Add(new OnboardingStarterIdea(
                    string.Format(CultureInfo.CurrentCulture, Loc.Onboarding_StarterTourTitle, project.Name),
                    string.Format(CultureInfo.CurrentCulture, Loc.Onboarding_StarterTourPrompt, project.Name),
                    project.Name,
                    "🧭"));
            }
            else if (project.Kind == DiscoveredProjectKind.Folder && starters.All(starter => starter.Icon != "🗂️"))
            {
                starters.Add(new OnboardingStarterIdea(
                    string.Format(CultureInfo.CurrentCulture, Loc.Onboarding_StarterFolderTitle, project.Name),
                    string.Format(CultureInfo.CurrentCulture, Loc.Onboarding_StarterFolderPrompt, project.Name),
                    project.Name,
                    "🗂️"));
            }
        }

        starters.AddRange(GeneralStarters(workspace.Documents));
        return starters;
    }

    private static IEnumerable<OnboardingStarterIdea> GeneralStarters(WorkspaceDocuments documents)
    {
        yield return new OnboardingStarterIdea(Loc.Chat_SuggestionA, Loc.Chat_SuggestionA, null, "☀️");
        if (documents.Downloads >= 20)
            yield return new OnboardingStarterIdea(Loc.Onboarding_StarterDownloadsTitle, Loc.Onboarding_StarterDownloadsPrompt, null, "🧹");
        yield return new OnboardingStarterIdea(Loc.Onboarding_StarterShowTitle, Loc.Onboarding_StarterShowPrompt, null, "✨");
    }

    /// <summary>
    /// The three starters to offer once onboarding knows which projects exist. A starter that belongs to a
    /// project the user didn't set up is replaced, so every starter opens in a project that is really there.
    /// Each project gets one starter before any gets a second, so the first tasks show some range.
    /// </summary>
    internal static IReadOnlyList<OnboardingStarterIdea> SelectStarters(
        IEnumerable<OnboardingStarterIdea> preferred,
        WorkspaceSnapshot workspace,
        IReadOnlyCollection<string> projectNames)
    {
        bool IsAvailable(OnboardingStarterIdea idea)
            => idea.ProjectName is null
               || projectNames.Contains(idea.ProjectName, StringComparer.OrdinalIgnoreCase);

        var fallback = FallbackStarters(new WorkspaceSnapshot(
            workspace.Projects.Where(project => projectNames.Contains(project.Name, StringComparer.OrdinalIgnoreCase)).ToList(),
            workspace.Apps,
            workspace.Sites,
            workspace.Documents));
        var candidates = preferred.Where(IsAvailable).Concat(fallback).ToList();

        var chosen = new List<OnboardingStarterIdea>();
        foreach (var varied in new[] { true, false })
        {
            foreach (var idea in candidates)
            {
                if (chosen.Count == StarterCount)
                    return chosen;
                if (chosen.Any(existing => string.Equals(existing.Title, idea.Title, StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(existing.Prompt, idea.Prompt, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                if (varied && idea.ProjectName is not null
                    && chosen.Any(existing => string.Equals(existing.ProjectName, idea.ProjectName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                chosen.Add(idea);
            }
        }

        return chosen;
    }

    // ═══════════════════════════════════════════════════════════════
    // Memories
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// The memory an answer becomes, keeping only the picks Lumi's memory rules keep as lasting facts of
    /// this kind, so what onboarding shows as remembered is exactly what stays remembered.
    /// </summary>
    internal static OnboardingMemory? BuildDurableMemory(string slot, string userName, string answer)
    {
        var items = SplitAnswer(answer);
        if (BuildMemory(slot, userName, items) is not { } memory)
            return null;
        if (IsKept(slot, memory))
            return memory;

        var kept = items.Where(item => BuildMemory(slot, userName, [item]) is { } single && IsKept(slot, single)).ToList();
        return kept.Count > 0 && BuildMemory(slot, userName, kept) is { } filtered && IsKept(slot, filtered) ? filtered : null;
    }

    internal static OnboardingMemory? BuildMemory(string slot, string userName, IReadOnlyList<string> items)
    {
        var picks = items
            .Select(item => Whitespace.Replace(item, " ").Trim().TrimEnd('.'))
            .Where(item => item.Length > 0 && !IsCatchAll(item))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (picks.Count == 0)
            return null;

        var name = string.IsNullOrWhiteSpace(userName) ? Loc.Onboarding_MemoryUnnamed : userName.Trim();
        var list = JoinNaturally(picks.Select(InSentence));
        return slot switch
        {
            OnboardingSlots.Work => new OnboardingMemory("Line of work",
                string.Format(CultureInfo.CurrentCulture, Loc.Onboarding_MemoryWork, name, list), "Work"),
            OnboardingSlots.Help => new OnboardingMemory("Preferred Lumi help",
                string.Format(CultureInfo.CurrentCulture, Loc.Onboarding_MemoryHelp, name, list), "Goals"),
            OnboardingSlots.Interests => new OnboardingMemory("Outside-work interests",
                string.Format(CultureInfo.CurrentCulture, Loc.Onboarding_MemoryInterests, name, list), "Interests"),
            _ => null,
        };
    }

    /// <summary>
    /// The topics each answer's memory may have. Lumi keeps one memory per topic and merges the rest away,
    /// so an answer that reads as another fact, like "theme parks" (the preferred theme) or "food &amp; drink"
    /// (a favorite food), would replace that memory.
    /// </summary>
    private static readonly Dictionary<string, string?[]> SlotTopics = new()
    {
        [OnboardingSlots.Work] = [null, "work:job"],
        [OnboardingSlots.Help] = ["goal:lumi-help"],
        [OnboardingSlots.Interests] = [null],
    };

    private static bool IsKept(string slot, OnboardingMemory memory)
        => MemoryAgentService.EvaluateMemoryCandidate(memory.Key, memory.Content, memory.Category).ShouldSave
           && SlotTopics.TryGetValue(slot, out var topics)
           && topics.Contains(MemoryAgentService.ExtractMemoryTopic(memory.Key, memory.Content, memory.Category));

    /// <summary>A question card joins several picks with ", ".</summary>
    internal static IReadOnlyList<string> SplitAnswer(string? answer)
        => string.IsNullOrWhiteSpace(answer)
            ? []
            : answer.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>"Music", "Travel and Cooking" read as "music, travel and cooking" mid-sentence.</summary>
    internal static string JoinNaturally(IEnumerable<string> items)
    {
        var list = items.Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + Loc.Onboarding_ListAnd + list[^1],
        };
    }

    /// <summary>
    /// Lower-cases a sentence-case pick so it reads naturally inside a sentence, but leaves names and
    /// acronyms ("YouTube", "Home Assistant", "AI tools") alone. Only applies to scripts with case.
    /// </summary>
    internal static string InSentence(string item)
    {
        var words = item.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words[0].Length < 2 || !char.IsUpper(words[0][0]))
            return item;
        if (words[0].Skip(1).Any(char.IsUpper) || words.Skip(1).Any(word => word.Length > 0 && char.IsUpper(word[0])))
            return item;

        return char.ToLower(item[0], CultureInfo.CurrentCulture) + item[1..];
    }
}
