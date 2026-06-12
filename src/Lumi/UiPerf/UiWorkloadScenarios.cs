#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.UiPerf;

/// <summary>
/// Builds a predefined set of chats (and supporting projects/skills) that replicate varied user
/// workloads — from tiny conversations to huge tool-heavy transcripts and a packed sidebar — so
/// the harness can stress every responsiveness-sensitive surface. Everything is created in memory
/// only; the harness runs against an isolated app-data directory so nothing touches real data.
/// </summary>
internal sealed class UiWorkloadScenarios
{
    private readonly DataStore _dataStore;

    public UiWorkloadScenarios(DataStore dataStore) => _dataStore = dataStore;

    public Guid TinyChatId { get; private set; }
    public Guid SmallChatId { get; private set; }
    public Guid MediumChatId { get; private set; }
    public Guid LargeChatId { get; private set; }
    public Guid HugeChatId { get; private set; }
    public Guid ToolHeavyChatId { get; private set; }
    public Guid MarkdownHeavyChatId { get; private set; }
    public Guid CodeHeavyChatId { get; private set; }
    public Guid DocHeavyChatId { get; private set; }
    public Guid ProjectId { get; private set; }
    public int TotalChats => _dataStore.Data.Chats.Count;

    private readonly List<Guid> _activeWorkChatIds = new();

    /// <summary>Chats used by the harness as concurrently "running" (streaming) agents under load.</summary>
    public IReadOnlyList<Guid> ActiveWorkChatIds => _activeWorkChatIds;

    public void Seed(int fillerChatCount = 140)
    {
        EnsureSkills();
        ProjectId = EnsureProject();

        var now = DateTimeOffset.Now;
        TinyChatId = AddChat("Tiny chat (quick question)", now.AddMinutes(-3),
            BuildConversation(seed: 1, turns: 1, assistantParagraphs: 1, withRichBlocks: false, toolHeavy: false));
        SmallChatId = AddChat("Small chat (short thread)", now.AddMinutes(-30),
            BuildConversation(seed: 2, turns: 6, assistantParagraphs: 2, withRichBlocks: false, toolHeavy: false));
        MediumChatId = AddChat("Medium chat (~80 messages)", now.AddHours(-3),
            BuildConversation(seed: 3, turns: 32, assistantParagraphs: 2, withRichBlocks: true, toolHeavy: false));
        LargeChatId = AddChat("Large chat (~240 messages)", now.AddDays(-1).AddHours(-2),
            BuildConversation(seed: 4, turns: 95, assistantParagraphs: 2, withRichBlocks: true, toolHeavy: false));
        HugeChatId = AddChat("Huge chat (~600 messages)", now.AddDays(-2),
            BuildConversation(seed: 5, turns: 250, assistantParagraphs: 2, withRichBlocks: true, toolHeavy: false));
        ToolHeavyChatId = AddChat("Tool-heavy chat (agents + tools)", now.AddDays(-1).AddHours(-6),
            BuildConversation(seed: 6, turns: 45, assistantParagraphs: 1, withRichBlocks: false, toolHeavy: true), ProjectId);
        MarkdownHeavyChatId = AddChat("Markdown-heavy chat (large documents)", now.AddDays(-4),
            BuildConversation(seed: 7, turns: 12, assistantParagraphs: 16, withRichBlocks: true, toolHeavy: false), ProjectId);

        // Power-user "real coding session" chats. Their tail turns (the ones that always mount on
        // open/switch) each carry a LARGE assistant payload — multiple big code blocks, wide tables
        // and long prose, ~15-25KB per turn. The transcript paging weight model caps per-message
        // weight (base + Min(8, len/450)), so these mount just like a normal chat yet cost far more
        // to re-realize. This replicates the heavy switch lag power users actually feel, which the
        // moderate synthetic chats above badly understate.
        CodeHeavyChatId = AddChat("Code-heavy session (large code blocks)", now.AddDays(-1).AddHours(-9),
            BuildHeavyConversation(seed: 21, turns: 18, HeavyContentKind.Code), ProjectId);
        DocHeavyChatId = AddChat("Doc-heavy session (large tables + prose)", now.AddDays(-3).AddHours(-1),
            BuildHeavyConversation(seed: 22, turns: 18, HeavyContentKind.Document), ProjectId);

        SeedFillerChats(fillerChatCount, now);
    }

    /// <summary>
    /// Seeds <paramref name="count"/> moderate chats that the harness treats as concurrently running
    /// agents. They are realistic (mixed markdown + tool content) so streaming on them exercises the
    /// real transcript renderer rather than trivial text.
    /// </summary>
    public IReadOnlyList<Guid> SeedActiveWorkChats(int count)
    {
        _activeWorkChatIds.Clear();
        var now = DateTimeOffset.Now;
        for (var i = 0; i < count; i++)
        {
            var id = AddChat(
                $"Running agent #{i + 1}",
                now.AddMinutes(-i),
                BuildConversation(
                    seed: 5000 + i,
                    turns: 10 + i * 2,
                    assistantParagraphs: 2,
                    withRichBlocks: i % 2 == 0,
                    toolHeavy: i % 3 == 0),
                i % 2 == 0 ? ProjectId : (Guid?)null);
            _activeWorkChatIds.Add(id);
        }

        return _activeWorkChatIds;
    }

    private void SeedFillerChats(int count, DateTimeOffset now)
    {
        var random = new Random(99);
        for (var i = 0; i < count; i++)
        {
            // Spread across time buckets so the grouped sidebar list is realistic and heavy.
            var updated = now.AddHours(-random.Next(0, 24 * 45)).AddMinutes(-random.Next(0, 60));
            var projectId = i % 5 == 0 ? ProjectId : (Guid?)null;
            AddChat($"Workload chat #{i + 1}", updated,
                BuildConversation(seed: 1000 + i, turns: 2, assistantParagraphs: 1, withRichBlocks: false, toolHeavy: false),
                projectId);
        }
    }

    private Guid AddChat(string title, DateTimeOffset updatedAt, List<ChatMessage> messages, Guid? projectId = null)
    {
        var chat = new Chat
        {
            Title = title,
            CreatedAt = updatedAt.AddMinutes(-messages.Count),
            UpdatedAt = updatedAt,
            ProjectId = projectId,
            Messages = messages,
            LastModelUsed = _dataStore.Data.Settings.PreferredModel,
            TotalInputTokens = messages.Count * 180,
            TotalOutputTokens = messages.Count * 90,
        };
        _dataStore.Data.Chats.Add(chat);
        return chat.Id;
    }

    private void EnsureSkills()
    {
        string[,] skills =
        {
            { "Code Helper", "Writes, explains, and debugs code.", "{}" },
            { "Web Researcher", "Searches the web and summarizes findings.", "\U0001F50E" },
            { "Document Creator", "Creates Word, Excel, and PowerPoint documents.", "\U0001F4C4" },
        };
        for (var i = 0; i < skills.GetLength(0); i++)
        {
            var name = skills[i, 0];
            if (_dataStore.Data.Skills.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                continue;
            _dataStore.Data.Skills.Add(new Skill { Name = name, Description = skills[i, 1], IconGlyph = skills[i, 2] });
        }
    }

    private Guid EnsureProject()
    {
        var existing = _dataStore.Data.Projects.FirstOrDefault(p => p.Name == "Performance Lab");
        if (existing is not null)
            return existing.Id;

        var project = new Project
        {
            Name = "Performance Lab",
            Instructions = "Synthetic project used by the UI responsiveness harness.",
        };
        _dataStore.Data.Projects.Add(project);
        return project.Id;
    }

    private static List<ChatMessage> BuildConversation(
        int seed,
        int turns,
        int assistantParagraphs,
        bool withRichBlocks,
        bool toolHeavy)
    {
        var random = new Random(seed);
        var messages = new List<ChatMessage>();
        var timestamp = DateTimeOffset.Now.AddMinutes(-turns * 4);

        ChatMessage Add(string role, string content)
        {
            timestamp = timestamp.AddSeconds(20 + random.Next(0, 40));
            var message = new ChatMessage { Role = role, Content = content, Timestamp = timestamp };
            messages.Add(message);
            return message;
        }

        for (var turn = 0; turn < turns; turn++)
        {
            var user = Add("user", Paragraph(random, 1 + random.Next(2)));
            user.Author = "Adir";

            if (turn % 3 == 0)
                Add("reasoning", Paragraph(random, 2 + random.Next(3)));

            if (toolHeavy)
                AppendToolBurst(random, Add, turn);

            var assistant = Add("assistant", BuildAssistantBody(random, assistantParagraphs, withRichBlocks && turn % 4 == 0));
            assistant.Author = "Lumi";
            assistant.Model = "gpt-5.5";
            if (turn % 7 == 0)
            {
                assistant.Sources.Add(new SearchSource
                {
                    Title = "Reference " + turn,
                    Snippet = Paragraph(random, 1),
                    Url = "https://example.com/ref/" + turn,
                });
            }
        }

        return messages;
    }

    private enum HeavyContentKind
    {
        Code,
        Document,
    }

    /// <summary>
    /// Builds a conversation whose every assistant turn carries a large payload (~15-25KB) so the
    /// mounted transcript tail is genuinely expensive to realize on open/switch — matching a real
    /// power-user coding/document session rather than the moderate synthetic chats.
    /// </summary>
    private static List<ChatMessage> BuildHeavyConversation(int seed, int turns, HeavyContentKind kind)
    {
        var random = new Random(seed);
        var messages = new List<ChatMessage>();
        var timestamp = DateTimeOffset.Now.AddMinutes(-turns * 5);

        ChatMessage Add(string role, string content)
        {
            timestamp = timestamp.AddSeconds(20 + random.Next(0, 40));
            var message = new ChatMessage { Role = role, Content = content, Timestamp = timestamp };
            messages.Add(message);
            return message;
        }

        for (var turn = 0; turn < turns; turn++)
        {
            var user = Add("user", Paragraph(random, 2 + random.Next(3)));
            user.Author = "Adir";

            if (turn % 2 == 0)
                Add("reasoning", Paragraph(random, 3 + random.Next(4)));

            var assistant = Add("assistant", BuildHeavyAssistantBody(random, kind));
            assistant.Author = "Lumi";
            assistant.Model = "gpt-5.5";
            assistant.Sources.Add(new SearchSource
            {
                Title = "Reference " + turn,
                Snippet = Paragraph(random, 1),
                Url = "https://example.com/ref/" + turn,
            });
        }

        return messages;
    }

    private static string BuildHeavyAssistantBody(Random random, HeavyContentKind kind)
    {
        var sb = new StringBuilder();
        sb.Append("## ").Append(Sentence(random, 5)).Append("\n\n");
        sb.Append(Paragraph(random, 5 + random.Next(4))).Append("\n\n");

        if (kind == HeavyContentKind.Code)
        {
            // A coding answer: a couple of large code blocks interleaved with explanation.
            sb.Append(CodeBlock(random, 70 + random.Next(50))).Append('\n');
            sb.Append(Paragraph(random, 4 + random.Next(3))).Append("\n\n");
            sb.Append("- ").Append(Sentence(random, 8)).Append('\n');
            sb.Append("- ").Append(Sentence(random, 10)).Append('\n');
            sb.Append("- ").Append(Sentence(random, 7)).Append("\n\n");
            sb.Append(CodeBlock(random, 60 + random.Next(40))).Append('\n');
            sb.Append(Paragraph(random, 4 + random.Next(3))).Append("\n\n");
            sb.Append(MarkdownTable(random, 8)).Append('\n');
        }
        else
        {
            // A document answer: long prose, wide tables and a smaller code sample.
            for (var i = 0; i < 4; i++)
            {
                sb.Append("### ").Append(Sentence(random, 4)).Append("\n\n");
                sb.Append(Paragraph(random, 6 + random.Next(4))).Append("\n\n");
            }

            sb.Append(MarkdownTable(random, 22)).Append('\n');
            sb.Append(Paragraph(random, 5 + random.Next(3))).Append("\n\n");
            sb.Append(MarkdownTable(random, 14)).Append('\n');
            sb.Append(CodeBlock(random, 24)).Append('\n');
        }

        return sb.ToString();
    }

    private static void AppendToolBurst(Random random, Func<string, string, ChatMessage> add, int turn)
    {
        string[] tools = { "view", "grep", "edit", "powershell", "glob" };
        var toolCount = 3 + random.Next(4);
        for (var i = 0; i < toolCount; i++)
        {
            var tool = tools[random.Next(tools.Length)];
            var message = add("tool", $"{{\"target\":\"src/Lumi/file{turn}_{i}.cs\"}}");
            message.ToolName = tool;
            message.ToolStatus = i == toolCount - 1 && random.Next(6) == 0 ? "Failed" : "Completed";
            message.ToolOutput = Paragraph(random, 1);
        }

        if (turn % 5 == 0)
        {
            var subagentId = $"subagent-{turn}";
            var subagent = add("tool", "{\"agent_type\":\"explore\",\"description\":\"Investigate module\"}");
            subagent.ToolName = "task";
            subagent.ToolCallId = subagentId;
            subagent.ToolStatus = "Completed";
            subagent.ToolOutput = "Subagent completed";

            var child = add("tool", "{\"command\":\"dotnet build\"}");
            child.ToolName = "powershell";
            child.ParentToolCallId = subagentId;
            child.ToolStatus = "Completed";
            child.ToolOutput = "Build succeeded.";
        }
    }

    private static string BuildAssistantBody(Random random, int paragraphs, bool withRichBlocks)
    {
        var sb = new StringBuilder();
        sb.Append("### ").Append(Sentence(random, 4)).Append("\n\n");
        for (var i = 0; i < paragraphs; i++)
        {
            sb.Append(Paragraph(random, 3 + random.Next(3))).Append("\n\n");
            if (i == 1)
            {
                sb.Append("- ").Append(Sentence(random, 5)).Append('\n');
                sb.Append("- ").Append(Sentence(random, 6)).Append('\n');
                sb.Append("- ").Append(Sentence(random, 4)).Append("\n\n");
            }
        }

        if (withRichBlocks)
        {
            sb.Append(MarkdownTable(random, 4)).Append('\n');
            sb.Append(CodeBlock(random, 10)).Append('\n');
        }

        return sb.ToString();
    }

    private static string MarkdownTable(Random random, int rows)
    {
        var sb = new StringBuilder();
        sb.Append("| Metric | Before | After | Delta |\n");
        sb.Append("| --- | --- | --- | --- |\n");
        for (var i = 0; i < rows; i++)
        {
            sb.Append("| ").Append(Word(random)).Append(" | ")
              .Append(random.Next(50, 900)).Append("ms | ")
              .Append(random.Next(10, 200)).Append("ms | ")
              .Append(random.Next(5, 80)).Append("% |\n");
        }

        return sb.ToString();
    }

    private static string CodeBlock(Random random, int lines)
    {
        var sb = new StringBuilder();
        sb.Append("```csharp\n");
        for (var i = 0; i < lines; i++)
            sb.Append("var ").Append(Word(random)).Append(" = ").Append(random.Next(0, 1000)).Append("; // ").Append(Word(random)).Append('\n');
        sb.Append("```\n");
        return sb.ToString();
    }

    private static readonly string[] WordBank =
    {
        "lumi", "agent", "responsive", "latency", "dispatcher", "transcript", "render", "layout",
        "binding", "scroll", "composer", "navigation", "throughput", "frame", "thread", "workload",
        "measure", "profile", "optimize", "cache", "stream", "token", "markdown", "viewport",
        "paging", "mounted", "turn", "message", "skill", "project", "memory", "session",
    };

    private static string Word(Random random) => WordBank[random.Next(WordBank.Length)];

    private static string Sentence(Random random, int words)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < words; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(Word(random));
        }

        var text = sb.ToString();
        return char.ToUpperInvariant(text[0]) + text[1..] + ".";
    }

    private static string Paragraph(Random random, int sentences)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < sentences; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(Sentence(random, 5 + random.Next(8)));
        }

        return sb.ToString();
    }
}
#endif
