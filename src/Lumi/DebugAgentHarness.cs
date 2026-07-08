#if DEBUG
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Microsoft.Extensions.AI;
using LumiChatMessage = Lumi.Models.ChatMessage;

namespace Lumi;

public static class DebugAgentHarness
{
    private const string ExpectedStressOutput = "LUMI_CHAT_STRESS_OK";
    private const string ExpectedToolInput = "lumi-agent-harness";
    private const string ExpectedNativeMcpOutput = "LUMI_MCP_NATIVE_OK";
    private const string ExpectedNativeMcpResumeOutput = "LUMI_MCP_NATIVE_RESUME_OK";
    private const string ExpectedProxyMcpOutput = "LUMI_MCP_PROXY_OK";
    private const string ExpectedProxyMcpResumeOutput = "LUMI_MCP_PROXY_RESUME_OK";

    public static bool IsUiHarnessFlag(string arg)
        => string.Equals(arg, "--debug-agent-harness", StringComparison.OrdinalIgnoreCase)
           || string.Equals(arg, "--debug-transcript-fixture", StringComparison.OrdinalIgnoreCase);

    public static bool IsChatStressFlag(string arg)
        => string.Equals(arg, "--test-chat-stress", StringComparison.OrdinalIgnoreCase)
           || string.Equals(arg, "--stress-chat", StringComparison.OrdinalIgnoreCase);

    public static bool IsNativeMcpStressFlag(string arg)
        => string.Equals(arg, "--test-mcp-native", StringComparison.OrdinalIgnoreCase)
           || string.Equals(arg, "--stress-mcp-native", StringComparison.OrdinalIgnoreCase);

    public static bool IsProxyMcpStressFlag(string arg)
        => string.Equals(arg, "--test-mcp-proxy", StringComparison.OrdinalIgnoreCase)
           || string.Equals(arg, "--stress-mcp-proxy", StringComparison.OrdinalIgnoreCase);

    public static bool IsSessionReapFlag(string arg)
        => string.Equals(arg, "--test-session-reap", StringComparison.OrdinalIgnoreCase)
           || string.Equals(arg, "--stress-session-reap", StringComparison.OrdinalIgnoreCase);

    public static Chat CreateTranscriptFixtureChat(DataStore dataStore)
    {
        var root = EnsureFixtureDirectory();
        var attachmentPath = Path.Combine(root, "fixture-attachment.md");
        var editedPath = Path.Combine(root, "FixtureWidget.cs");
        var createdPath = Path.Combine(root, "generated-fixture-output.md");
        var comparisonPath = Path.Combine(root, "OLED-TV-Comparison.html");
        var shortlistPath = Path.Combine(root, "TV-Shortlist.csv");
        var dealSummaryPath = Path.Combine(root, "Deal-Summary.md");
        var researchNotesPath = Path.Combine(root, "OLED-Research-Notes.md");

        File.WriteAllText(attachmentPath, "# Debug fixture attachment\n\nThis file exists so attachment chips can resolve size and icon metadata.\n");
        File.WriteAllText(editedPath, "public class FixtureWidget\n{\n    public string State => \"before\";\n}\n");
        File.WriteAllText(createdPath, "# Generated fixture output\n\nThis file is announced by the debug transcript fixture.\n");
        File.WriteAllText(comparisonPath, "<!doctype html><meta charset=\"utf-8\"><title>65\\\" OLED TV Comparison</title>\n<h1>65\\\" OLED TV Comparison</h1>\n<p>Synthetic deliverable produced by the debug fixture so the Workspace rail has a real announced file.</p>\n");
        File.WriteAllText(shortlistPath, "Model,Panel,Price,Rating\nLG C4,OLED evo,1799,9.1\nSamsung S90D,QD-OLED,1899,9.0\nSony Bravia 8,OLED,1999,8.8\n");
        File.WriteAllText(dealSummaryPath, "# 65\\\" OLED Deal Summary\n\n- LG C4 - $1,799 (Best Buy)\n- Samsung S90D - $1,899\n- Sony Bravia 8 - $1,999\n");
        File.WriteAllText(researchNotesPath, "# OLED research notes\n\nCreated by the debug fixture so the Workspace Changes tab shows a created file.\n");

        var codingSkill = dataStore.Data.Skills.FirstOrDefault(s =>
            s.Name.Equals("Code Helper", StringComparison.OrdinalIgnoreCase))
            ?? new Skill
            {
                Name = "Code Helper",
                Description = "Writes, explains, and debugs code.",
                IconGlyph = "{}"
            };

        var skillRef = new SkillReference
        {
            Name = codingSkill.Name,
            Glyph = codingSkill.IconGlyph,
            Description = codingSkill.Description
        };

        // A second, distinct skill that is loaded at runtime (via fetch_skill) mid-turn —
        // intentionally NOT attached to the user message, so the inline "skill loaded" chip
        // is visible in the fixture instead of being de-duplicated away.
        var researchSkill = dataStore.Data.Skills.FirstOrDefault(s =>
            s.Name.Equals("Web Researcher", StringComparison.OrdinalIgnoreCase))
            ?? new Skill
            {
                Name = "Web Researcher",
                Description = "Searches the web and summarizes findings on any topic.",
                IconGlyph = "\U0001F50E"
            };

        var fetchedSkillRef = new SkillReference
        {
            Name = researchSkill.Name,
            Glyph = researchSkill.IconGlyph,
            Description = researchSkill.Description
        };

        // A third, distinct skill reported by the SDK on the assistant message (the
        // SkillInvokedEvent path, with no inline fetch_skill tool call). It surfaces as a
        // chip at the end of the assistant turn.
        var documentSkill = dataStore.Data.Skills.FirstOrDefault(s =>
            s.Name.Equals("Document Creator", StringComparison.OrdinalIgnoreCase))
            ?? new Skill
            {
                Name = "Document Creator",
                Description = "Creates Word, Excel, and PowerPoint documents from descriptions.",
                IconGlyph = "\U0001F4C4"
            };

        var assistantSkillRef = new SkillReference
        {
            Name = documentSkill.Name,
            Glyph = documentSkill.IconGlyph,
            Description = documentSkill.Description
        };

        // A fourth skill representing a *builtin / plugin / remote* Copilot skill whose full body
        // is delivered by the SDK's skill.invoked event. It is NOT a Lumi appdata skill and has NO
        // SKILL.md anywhere Lumi scans on disk, so its preview can only render from the Content the
        // SDK handed us on the chip. It rides on the fixture's active-skill turn (as the first chip)
        // and is the live regression guard for the "standard skill chip shows an empty preview" bug.
        var builtinSkillRef = new SkillReference
        {
            Name = "Remote Copilot Skill",
            Glyph = "\U0001F310",
            Description = "A builtin/remote Copilot skill with no SKILL.md on this machine.",
            Content = """
                # Remote Copilot Skill

                This body was delivered by the Copilot SDK `skill.invoked` event — it was **not**
                read from a `SKILL.md` on disk. Builtin, plugin, and remote skills have no local
                file where Lumi scans, so the preview must render this SDK-provided content
                directly instead of re-scanning the filesystem.
                """
        };

        var chat = new Chat
        {
            Title = "Debug transcript fixture (not saved)",
            CreatedAt = DateTimeOffset.Now.AddMinutes(-12),
            UpdatedAt = DateTimeOffset.Now,
            LastModelUsed = dataStore.Data.Settings.PreferredModel,
            LastReasoningEffortUsed = dataStore.Data.Settings.ReasoningEffort,
            TotalInputTokens = 12345,
            TotalOutputTokens = 6789,
            PlanContent = """
                # Debug plan

                - Render every transcript item type.
                - Keep this chat out of persisted history.
                - Use this fixture when changing transcript UI.
                """
        };

        var userName = dataStore.Data.Settings.UserName ?? "You";
        var t = DateTimeOffset.Now.AddMinutes(-10);
        LumiChatMessage Message(string role, string content)
            => new()
            {
                Role = role,
                Content = content,
                Timestamp = t = t.AddSeconds(18)
            };

        var user = Message("user", """
            Debug fixture request:

            - Show a normal user bubble.
            - Show attachment chips.
            - Show an active skill chip.
            """);
        user.Author = userName;
        user.Attachments.Add(attachmentPath);
        user.ActiveSkills.Add(builtinSkillRef);
        user.ActiveSkills.Add(skillRef);
        chat.Messages.Add(user);

        chat.Messages.Add(Tool("view", JsonObject(
            JsonProperty("path", JsonString(attachmentPath))), "Completed", output: "1. # Debug fixture attachment"));

        var firstAssistant = Message("assistant", """
            ### Transcript fixture is active

            This assistant message verifies **markdown**, `inline code`, selectable plain text, and source chips.

            | Item | Expected |
            | --- | --- |
            | Markdown | rendered |
            | Sources | visible below |
            | Model label | visible after turn |

            A wide table wraps to fit, then switches to a horizontal scrollbar with readable columns when there are too many columns to fit:

            | Column A | Column B | Column C | Column D | Column E | Column F | Column G | Column H |
            | --- | --- | --- | --- | --- | --- | --- | --- |
            | Markdown rendering pipeline | rendered correctly | full fidelity incl. tables | Strata Markdown | verified | passing now | extra column | last column here |
            | Sources visible below | visible below message | search source chips appear | ChatView | in progress | still good | another value | trailing value |
            | Model label visibility | visible after turn | the model name label renders | ChatViewModel | pending | confirmed ok | one more | the very end |

            The two-option comparison control (StrataFork) should switch when either tab is clicked:

            ```comparison
            {"optionA":{"title":"LG C4","content":"- OLED evo panel\n- Excellent for gaming\n- **$1,799**"},"optionB":{"title":"Samsung S90D","content":"- QD-OLED panel\n- Brighter highlights\n- **$1,899**"}}
            ```

            This next comparison block is intentionally **malformed** (the model dropped the final closing brace). Before the tolerant-parse fix it collapsed to a ⚖️ placeholder; now it must still render a working StrataFork:

            ```comparison
            {"optionA":{"title":"Ship the fix now","content":"- Comparison renders again\n- **Lower user-visible risk**"},"optionB":{"title":"Wait for more testing","content":"- Extra soak time\n- **Slower to land**"}
            ```

            A native Mermaid **architecture diagram** verifies subgraph containers and distributed (fan-out / fan-in) arrows:

            ```mermaid
            flowchart TB
                User[User] --> LB[Load Balancer]
                subgraph Frontend
                    Web[Web App]
                    Mobile[Mobile App]
                end
                subgraph Backend
                    API[API Gateway]
                    Auth[Auth Service]
                    Orders[Order Service]
                end
                subgraph Data
                    DB[(Database)]
                    Cache[(Redis)]
                end
                LB --> Web
                LB --> Mobile
                Web --> API
                Mobile --> API
                API --> Auth
                API --> Orders
                Orders --> DB
                Orders --> Cache
                Auth --> DB
            ```

            A **decision flowchart** verifies rank-skipping edges route around intermediate nodes — the `No` path leaves *Tests pass?* and drops down its own lane past the *Security scan* row instead of behind it:

            ```mermaid
            flowchart TD
                Start([Push to main]) --> Build[Build]
                Build --> Test{Tests pass?}
                Test -->|No| Notify[Notify devs]
                Test -->|Yes| Scan{Security scan}
                Scan -->|Fail| Notify
                Scan -->|Pass| Stage[Deploy staging]
                Notify --> Fix[Fix issues]
                Fix --> Build
                Stage --> Done([Released])
            ```

            A **class diagram** verifies hierarchical layout, three compartments, and inheritance markers:

            ```mermaid
            classDiagram
                Animal <|-- Duck
                Animal <|-- Fish
                Animal : +int age
                Animal : +String gender
                Animal : +isMammal()
                Animal : +mate()
                Duck : +String beakColor
                Duck : +swim()
                Duck : +quack()
                Fish : -int sizeInFeet
                Fish : -canEat()
            ```

            An **ER diagram** verifies crow's-foot cardinality and orthogonal routing:

            ```mermaid
            erDiagram
                CUSTOMER ||--o{ ORDER : places
                CUSTOMER ||--o{ DELIVERY-ADDRESS : uses
                ORDER ||--|{ LINE-ITEM : contains
            ```
            """);
        firstAssistant.Author = "Lumi";
        firstAssistant.Model = "gpt-5.5";
        firstAssistant.ActiveSkills.Add(assistantSkillRef);
        firstAssistant.Sources.Add(new SearchSource
        {
            Title = "Lumi debug fixture",
            Snippet = "Synthetic source used by the Debug-only transcript fixture.",
            Url = "https://example.com/lumi-debug-fixture"
        });
        chat.Messages.Add(firstAssistant);

        var secondUser = Message("user", "Run the full debug transcript pass with tools, reasoning, a subagent, a question, and file changes.");
        secondUser.Author = userName;
        chat.Messages.Add(secondUser);

        chat.Messages.Add(Message("reasoning", """
            I need to exercise completed reasoning, grouped tools, terminal output, todo progress, subagent nesting, and generated artifacts.
            """));

        chat.Messages.Add(Tool("report_intent", JsonObject(
            JsonProperty("intent", JsonString("Exercising fixture"))), "Completed"));
        chat.Messages.Add(Tool("web_search", JsonObject(
            JsonProperty("query", JsonString("best 65-inch OLED TV 2026"))), "Completed", output: "Returned 8 results"));
        chat.Messages.Add(Tool("report_intent", JsonObject(
            JsonProperty("intent", JsonString("Comparing OLED panels"))), "Completed"));
        chat.Messages.Add(Tool("web_search", JsonObject(
            JsonProperty("query", JsonString("LG C4 vs Samsung S90D picture quality"))), "Completed", output: "Returned 6 results"));
        chat.Messages.Add(Tool("report_intent", JsonObject(
            JsonProperty("intent", JsonString("Summarizing the best deals"))), "Completed"));
        chat.Messages.Add(Tool("fetch_skill", JsonObject(
            JsonProperty("name", JsonString(fetchedSkillRef.Name))), "Completed", output: $"Fetched skill: {fetchedSkillRef.Name}"));
        chat.Messages.Add(Tool("powershell", JsonObject(
            JsonProperty("command", JsonString("Write-Output 'fixture terminal output'")),
            JsonProperty("description", JsonString("Emit fixture output"))), "Completed", output: "fixture terminal output\nexit code: 0"));
        chat.Messages.Add(Tool("example_mcp_lookup", JsonObject(
            JsonProperty("query", JsonString("Busy"))), "Failed", output: "MCP server returned an example lookup failure."));
        var todoArgs = JsonObject(
            JsonProperty("todoList", JsonArray(
                JsonObject(
                    JsonProperty("id", "1"),
                    JsonProperty("title", JsonString("Render fixture chat")),
                    JsonProperty("status", JsonString("completed"))),
                JsonObject(
                    JsonProperty("id", "2"),
                    JsonProperty("title", JsonString("Validate tool grouping")),
                    JsonProperty("status", JsonString("completed"))),
                JsonObject(
                    JsonProperty("id", "3"),
                    JsonProperty("title", JsonString("Keep stress harness ready")),
                    JsonProperty("status", JsonString("in-progress"))))));
        chat.Messages.Add(Tool("manage_todo_list", todoArgs, "InProgress"));
        chat.Messages.Add(Tool("edit", JsonObject(
            JsonProperty("filePath", JsonString(editedPath)),
            JsonProperty("oldString", JsonString("public class FixtureWidget")),
            JsonProperty("newString", JsonString("public partial class FixtureWidget"))), "Completed", output: "Updated FixtureWidget.cs"));
        chat.Messages.Add(Tool("create", JsonObject(
            JsonProperty("filePath", JsonString(researchNotesPath)),
            JsonProperty("file_text", JsonString("# OLED research notes\n\n- Compared LG C4, Samsung S90D, Sony Bravia 8.\n- Tracked retailer pricing for 65-inch panels.\n"))), "Completed", output: "Created OLED-Research-Notes.md"));

        var subagentId = "debug-subagent-fixture";
        chat.Messages.Add(Tool("task", JsonObject(
            JsonProperty("description", JsonString("Inspect transcript fixture in a separate coding-agent card")),
            JsonProperty("agent_type", JsonString("explore")),
            JsonProperty("agentName", JsonString("explore")),
            JsonProperty("agentDisplayName", JsonString("Explore agent")),
            JsonProperty("agentDescription", JsonString("Fast codebase exploration agent used by coding agents.")),
            JsonProperty("mode", JsonString("background")),
            JsonProperty("model", JsonString("claude-haiku-4.5")),
            JsonProperty("reasoning", JsonString("The fixture should show nested activity under this subagent card.")),
            JsonProperty("transcript", JsonString("Found ChatView.axaml templates, transcript builders, and debug entry points."))), "Completed", toolCallId: subagentId, output: "Subagent completed"));
        chat.Messages.Add(Tool("powershell", JsonObject(
            JsonProperty("command", JsonString("dotnet build src\\Lumi\\Lumi.csproj --no-restore")),
            JsonProperty("description", JsonString("Build Lumi"))), "Completed", parentToolCallId: subagentId, output: "Build succeeded."));

        var question = Tool("ask_question", JsonObject(
            JsonProperty("question", JsonString("Which debug action should an agent try next?")),
            JsonProperty("options", JsonArray(
                JsonString("Run fixture"),
                JsonString("Run stress harness"),
                JsonString("Inspect UI map"))),
            JsonProperty("allowFreeText", "true"),
            JsonProperty("allowMultiSelect", "false")), "Completed", output: "User answered: Run stress harness");
        question.QuestionId = "debug-question-fixture";
        question.QuestionText = "Which debug action should an agent try next?";
        question.QuestionOptions = JsonSerializer.Serialize(
            new[] { "Run fixture", "Run stress harness", "Inspect UI map" },
            AppDataJsonContext.Default.StringArray);
        question.QuestionAllowFreeText = true;
        question.QuestionAllowMultiSelect = false;
        chat.Messages.Add(question);

        chat.Messages.Add(Tool("announce_file", JsonObject(
            JsonProperty("filePath", JsonString(createdPath))), "Completed", output: createdPath));
        chat.Messages.Add(Tool("announce_file", JsonObject(
            JsonProperty("filePath", JsonString(comparisonPath))), "Completed", output: comparisonPath));
        chat.Messages.Add(Tool("announce_file", JsonObject(
            JsonProperty("filePath", JsonString(shortlistPath))), "Completed", output: shortlistPath));
        chat.Messages.Add(Tool("announce_file", JsonObject(
            JsonProperty("filePath", JsonString(dealSummaryPath))), "Completed", output: dealSummaryPath));

        var finalAssistant = Message("assistant", """
            The fixture turn includes:

            1. Grouped tool calls.
            2. Todo progress.
            3. A nested subagent card.
            4. A question card with a selected answer.
            5. Announced file chips and a file-change summary.
            """);
        finalAssistant.Author = "Lumi";
        finalAssistant.Model = "claude-sonnet-4.6";
        finalAssistant.Sources.Add(new SearchSource
        {
            Title = "Agent debug map",
            Snippet = "The debug map names stable controls and nav indices.",
            Url = "https://example.com/lumi-agent-debug-map"
        });
        finalAssistant.Sources.Add(new SearchSource
        {
            Title = "The 5 Best 65\" OLED TVs (early 2026)",
            Snippet = "Hands-on rankings of this year's 65-inch OLED panels.",
            Url = "https://www.rtings.com/tv/reviews/best/oled"
        });
        finalAssistant.Sources.Add(new SearchSource
        {
            Title = "LG C4 OLED review",
            Snippet = "Full review of the LG C4 evo panel and gaming features.",
            Url = "https://www.techradar.com/televisions/lg-c4-oled-review"
        });
        finalAssistant.Sources.Add(new SearchSource
        {
            Title = "Samsung S90D vs LG C4",
            Snippet = "QD-OLED versus OLED evo head-to-head comparison.",
            Url = "https://www.whathifi.com/features/samsung-s90d-vs-lg-c4"
        });
        finalAssistant.Sources.Add(new SearchSource
        {
            Title = "65-inch OLED TV deals",
            Snippet = "Current retailer pricing on 65-inch OLED models.",
            Url = "https://www.bestbuy.com/site/searchpage.jsp?st=65+oled"
        });
        finalAssistant.Sources.Add(new SearchSource
        {
            Title = "C4 vs G4 for gaming",
            Snippet = "Community thread weighing the C4 against the brighter G4.",
            Url = "https://www.reddit.com/r/OLED_Gaming/comments/lg-c4-vs-g4"
        });
        finalAssistant.Sources.Add(new SearchSource
        {
            Title = "Best OLED TV 2026",
            Snippet = "Editor picks for the best OLED TVs to buy right now.",
            Url = "https://www.tomsguide.com/best-picks/best-oled-tv"
        });
        chat.Messages.Add(finalAssistant);

        // ── Parallel multi-subagent fan-out turn ──────────────────────────────
        // Exercises the grouped layout used when several sub-agents run at once.
        var parallelUser = Message("user", "Research the three OLED contenders in parallel and tell me which wins.");
        parallelUser.Author = userName;
        chat.Messages.Add(parallelUser);

        chat.Messages.Add(Message("reasoning", """
            I'll fan out three agents at once — one per panel — then compare their findings.
            """));

        const string agentA = "debug-parallel-agent-a";
        const string agentB = "debug-parallel-agent-b";
        const string agentC = "debug-parallel-agent-c";

        // Launch all three agents together (the fan-out the user sees as one batch).
        chat.Messages.Add(Tool("task", JsonObject(
            JsonProperty("description", JsonString("Benchmark the LG C4 evo panel")),
            JsonProperty("agent_type", JsonString("research")),
            JsonProperty("agentName", JsonString("research")),
            JsonProperty("agentDisplayName", JsonString("Research agent")),
            JsonProperty("agentDescription", JsonString("Deep web research agent.")),
            JsonProperty("mode", JsonString("background")),
            JsonProperty("model", JsonString("claude-sonnet-4.6")),
            JsonProperty("reasoning", JsonString("Pulling rtings measurements for the C4.")),
            JsonProperty("transcript", JsonString("LG C4: 1,000 nits peak, 144Hz, excellent near-black handling."))),
            "Completed", toolCallId: agentA, output: "Research agent completed"));
        chat.Messages.Add(Tool("task", JsonObject(
            JsonProperty("description", JsonString("Benchmark the Samsung S90D QD-OLED")),
            JsonProperty("agent_type", JsonString("research")),
            JsonProperty("agentName", JsonString("research")),
            JsonProperty("agentDisplayName", JsonString("Research agent")),
            JsonProperty("agentDescription", JsonString("Deep web research agent.")),
            JsonProperty("mode", JsonString("background")),
            JsonProperty("model", JsonString("claude-sonnet-4.6")),
            JsonProperty("reasoning", JsonString("Checking QD-OLED brightness claims.")),
            JsonProperty("transcript", JsonString("Samsung S90D: brighter highlights, wider color volume, 144Hz."))),
            "Completed", toolCallId: agentB, output: "Research agent completed"));
        chat.Messages.Add(Tool("task", JsonObject(
            JsonProperty("description", JsonString("Benchmark the Sony Bravia 8")),
            JsonProperty("agent_type", JsonString("explore")),
            JsonProperty("agentName", JsonString("explore")),
            JsonProperty("agentDisplayName", JsonString("Explore agent")),
            JsonProperty("agentDescription", JsonString("Fast codebase/data exploration agent.")),
            JsonProperty("mode", JsonString("background")),
            JsonProperty("model", JsonString("claude-haiku-4.5")),
            JsonProperty("reasoning", JsonString("Still gathering Bravia 8 motion data."))),
            "InProgress", toolCallId: agentC, output: null));

        // Each agent's own activity streams in under its card.
        chat.Messages.Add(Tool("report_intent", JsonObject(
            JsonProperty("intent", JsonString("Measuring C4 peak brightness"))), "Completed", parentToolCallId: agentA));
        chat.Messages.Add(Tool("web_search", JsonObject(
            JsonProperty("query", JsonString("LG C4 rtings peak brightness 10% window"))), "Completed", parentToolCallId: agentA, output: "Returned 5 results"));
        chat.Messages.Add(Tool("web_search", JsonObject(
            JsonProperty("query", JsonString("LG C4 input lag 4k 120hz"))), "Completed", parentToolCallId: agentA, output: "Returned 4 results"));

        chat.Messages.Add(Tool("report_intent", JsonObject(
            JsonProperty("intent", JsonString("Comparing QD-OLED color volume"))), "Completed", parentToolCallId: agentB));
        chat.Messages.Add(Tool("web_search", JsonObject(
            JsonProperty("query", JsonString("Samsung S90D color volume measurement"))), "Completed", parentToolCallId: agentB, output: "Returned 6 results"));
        chat.Messages.Add(Tool("powershell", JsonObject(
            JsonProperty("command", JsonString("Write-Output 'S90D brightness: 1320 nits'")),
            JsonProperty("description", JsonString("Record measurement"))), "Completed", parentToolCallId: agentB, output: "S90D brightness: 1320 nits\nexit code: 0"));

        chat.Messages.Add(Tool("report_intent", JsonObject(
            JsonProperty("intent", JsonString("Checking Bravia 8 motion handling"))), "Completed", parentToolCallId: agentC));
        chat.Messages.Add(Tool("web_search", JsonObject(
            JsonProperty("query", JsonString("Sony Bravia 8 motion interpolation review"))), "InProgress", parentToolCallId: agentC));

        var parallelAssistant = Message("assistant", """
            All three agents reported back. The **Samsung S90D** edges ahead on brightness and
            color volume, with the **LG C4** close behind for gaming value.
            """);
        parallelAssistant.Author = "Lumi";
        parallelAssistant.Model = "claude-sonnet-4.6";
        chat.Messages.Add(parallelAssistant);

        chat.Messages.Add(Message("error", "Debug fixture error bubble: simulated recoverable Copilot error with retry styling."));

        return chat;

        LumiChatMessage Tool(
            string name,
            string argsJson,
            string status,
            string? toolCallId = null,
            string? parentToolCallId = null,
            string? output = null)
        {
            var msg = Message("tool", argsJson);
            msg.ToolName = name;
            msg.ToolStatus = status;
            msg.ToolCallId = toolCallId ?? $"debug-{name}-{Guid.NewGuid():N}";
            msg.ParentToolCallId = parentToolCallId;
            msg.ToolOutput = output;
            return msg;
        }

        static string JsonObject(params string[] properties)
            => "{" + string.Join(",", properties) + "}";

        static string JsonArray(params string[] items)
            => "[" + string.Join(",", items) + "]";

        static string JsonProperty(string name, string valueJson)
            => $"{JsonString(name)}:{valueJson}";

        static string JsonString(string value)
            => $"\"{JsonEncodedText.Encode(value).ToString()}\"";
    }

    public static async Task<int> RunChatStressAsync(CopilotService copilotService, CancellationToken ct)
    {
        Console.WriteLine("Lumi chat stress harness");
        Console.WriteLine("Connecting to Copilot...");

        await copilotService.ConnectAsync(ct).ConfigureAwait(false);
        var model = await copilotService.GetFastestModelIdAsync(ct).ConfigureAwait(false);
        Console.WriteLine($"Model: {model ?? "(default)"}");

        var toolInputs = new List<string>();
        var toolStarted = 0;
        var toolCompleted = 0;
        var streamed = new StringBuilder();

        var echoTool = AIFunctionFactory.Create(
            ([Description("Echo payload. Must be exactly lumi-agent-harness for the stress test.")] string value) =>
            {
                toolInputs.Add(value);
                return $"debug_echo_result:{value}:ok";
            },
            "debug_echo",
            "Deterministic debug echo tool for Lumi chat stress tests.");

        string? finalContent = null;
        await copilotService.UseLightweightSessionAsync(
            new LightweightSessionOptions
            {
                SystemPrompt = $"""
                    You are running a deterministic Lumi debug harness.
                    You must call debug_echo exactly once with value "{ExpectedToolInput}".
                    After the tool result, answer with a short sentence that contains "{ExpectedStressOutput}".
                    """,
                Model = model,
                Streaming = true,
                Tools = [echoTool]
            },
            async (session, innerCt) =>
            {
                using var sub = session.On<SessionEvent>(evt =>
                {
                    switch (evt)
                    {
                        case AssistantMessageDeltaEvent delta:
                            streamed.Append(delta.Data?.DeltaContent);
                            break;
                        case ToolExecutionStartEvent start when start.Data?.ToolName == "debug_echo":
                            Interlocked.Increment(ref toolStarted);
                            break;
                        case ToolExecutionCompleteEvent:
                            Interlocked.Increment(ref toolCompleted);
                            break;
                    }
                });

                var result = await session.SendAndWaitAsync(
                    new MessageOptions
                    {
                        Prompt = $"Run the stress contract. Call debug_echo with {ExpectedToolInput}, then include {ExpectedStressOutput} in the final answer."
                    },
                    TimeSpan.FromMinutes(2),
                    innerCt).ConfigureAwait(false);

                finalContent = result?.Data?.Content;
            },
            ct).ConfigureAwait(false);

        var combined = string.Join("\n", finalContent, streamed.ToString());
        var hasExpectedOutput = combined.Contains(ExpectedStressOutput, StringComparison.Ordinal);
        var hasExpectedToolInput = toolInputs.Contains(ExpectedToolInput, StringComparer.Ordinal);
        var hasToolLifecycle = toolStarted > 0 && toolCompleted > 0;

        Console.WriteLine($"Tool started: {toolStarted}");
        Console.WriteLine($"Tool completed: {toolCompleted}");
        Console.WriteLine($"Tool inputs: {string.Join(", ", toolInputs)}");
        Console.WriteLine($"Final content: {finalContent}");

        if (hasExpectedOutput && hasExpectedToolInput && hasToolLifecycle)
        {
            Console.WriteLine("PASS: real Copilot stress check completed.");
            return 0;
        }

        Console.Error.WriteLine("FAIL: Copilot stress check did not satisfy the contract.");
        if (!hasToolLifecycle)
            Console.Error.WriteLine("- debug_echo tool lifecycle events were not observed.");
        if (!hasExpectedToolInput)
            Console.Error.WriteLine($"- debug_echo was not called with {ExpectedToolInput}.");
        if (!hasExpectedOutput)
            Console.Error.WriteLine($"- final response did not contain {ExpectedStressOutput}.");
        return 1;
    }

    public static Task<int> RunNativeMcpStressAsync(CopilotService copilotService, CancellationToken ct)
        => RunMcpStressAsync(copilotService, useProxy: false, ct);

    public static Task<int> RunProxyMcpStressAsync(CopilotService copilotService, CancellationToken ct)
        => RunMcpStressAsync(copilotService, useProxy: true, ct);

    private static async Task<int> RunMcpStressAsync(CopilotService copilotService, bool useProxy, CancellationToken ct)
    {
        var harnessName = useProxy ? "proxy" : "native";
        var marker = useProxy ? "PROXY_GLOBAL" : "NATIVE_GLOBAL";
        var serverName = useProxy ? "proxy-global" : "native-global";
        var expectedOutput = useProxy ? ExpectedProxyMcpOutput : ExpectedNativeMcpOutput;
        var expectedResumeOutput = useProxy ? ExpectedProxyMcpResumeOutput : ExpectedNativeMcpResumeOutput;
        var expectedMarker = $"MCP_MARKER:{marker}:SDK_NATIVE";
        var expectedResumeMarker = $"MCP_MARKER:{marker}:SDK_RESUME";

        Console.WriteLine($"Lumi {harnessName} MCP stress harness");
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine($"FAIL: {harnessName} MCP stress harness currently uses a Windows PowerShell fake MCP server.");
            return 1;
        }

        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (!File.Exists(powershell))
        {
            Console.Error.WriteLine($"FAIL: PowerShell not found at {powershell}");
            return 1;
        }

        Console.WriteLine("Connecting to Copilot...");
        await copilotService.ConnectAsync(ct).ConfigureAwait(false);
        var model = await copilotService.GetFastestModelIdAsync(ct).ConfigureAwait(false);
        Console.WriteLine($"Model: {model ?? "(default)"}");

        var root = Path.Combine(Path.GetTempPath(), $"lumi-mcp-{harnessName}-stress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var scriptPath = Path.Combine(root, "fake-mcp.ps1");
        var logPath = Path.Combine(root, "starts.log");
        McpProxyRuntime? proxyRuntime = null;

        try
        {
            await File.WriteAllTextAsync(scriptPath, """
                [System.IO.File]::AppendAllText($env:MCP_TEST_LOG, "$($env:MCP_MARKER)|$PID`n")
                function Write-Json($obj) {
                    [Console]::Out.WriteLine(($obj | ConvertTo-Json -Compress -Depth 30))
                    [Console]::Out.Flush()
                }
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    $msg = $line | ConvertFrom-Json
                    if ($msg.method -eq "initialize") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ protocolVersion = "2025-11-25"; capabilities = @{}; serverInfo = @{ name = "lumi-native-fake-mcp"; version = "1" } } }
                    } elseif ($msg.method -eq "tools/list") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ tools = @(@{ name = "emit_marker"; description = "Return the configured native MCP stress marker and requested value."; inputSchema = @{ type = "object"; properties = @{ value = @{ type = "string" } }; required = @("value") } }) } }
                    } elseif ($msg.method -eq "tools/call") {
                        $value = [string]$msg.params.arguments.value
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ content = @(@{ type = "text"; text = "MCP_MARKER:$($env:MCP_MARKER):$value" }) } }
                    }
                }
                """, ct).ConfigureAwait(false);

            var server = new McpServer
            {
                Name = serverName,
                Command = powershell,
                Args = ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                Env =
                {
                    ["MCP_MARKER"] = marker,
                    ["MCP_TEST_LOG"] = logPath,
                },
            };
            var data = new AppData { McpServers = [server] };
            var chat = new Chat { ActiveMcpServerNames = [serverName] };
            if (useProxy)
                proxyRuntime = new McpProxyRuntime();
            var mcpServers = McpSessionPlanner.Build(data, root, new ProjectContextCatalogSnapshot([], [], []), chat, [serverName], null, proxyRuntime);
            if (!mcpServers.ContainsKey(serverName))
            {
                Console.Error.WriteLine($"FAIL: {serverName} MCP server was not included in the SDK config.");
                return 1;
            }
            if (useProxy)
            {
                if (mcpServers[serverName] is not McpHttpServerConfig { Url: var proxyUrl }
                    || !proxyUrl.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("FAIL: local MCP server was not routed through the loopback proxy.");
                    return 1;
                }
            }

            var toolStarted = 0;
            var toolCompleted = 0;
            string? finalContent = null;
            var config = SessionConfigBuilder.Build(
                systemPrompt: $"""
                    You are running Lumi's deterministic {harnessName} MCP debug harness.
                    You have an MCP tool named emit_marker available through the Copilot SDK MCP integration.
                    You must call emit_marker with value "SDK_NATIVE".
                    After the tool call, include "{expectedOutput}" and the exact MCP marker text in the final answer.
                    """,
                model: model,
                workingDirectory: root,
                skillDirectories: null,
                customAgents: null,
                tools: null,
                mcpServers: mcpServers,
                reasoningEffort: null,
                userInputHandler: null,
                onPermission: null,
                hooks: null);

            CopilotSession? session = null;
            try
            {
                session = await copilotService.CreateSessionAsync(config, ct).ConfigureAwait(false);
                using var sub = session.On<SessionEvent>(evt =>
                {
                    switch (evt)
                    {
                        case ToolExecutionStartEvent:
                            Interlocked.Increment(ref toolStarted);
                            break;
                        case ToolExecutionCompleteEvent:
                            Interlocked.Increment(ref toolCompleted);
                            break;
                    }
                });

                var result = await session.SendAndWaitAsync(
                    new MessageOptions
                    {
                        Prompt = $"Run the {harnessName} MCP validation now. Use emit_marker with {{\"value\":\"SDK_NATIVE\"}}.\n"
                            + $"Final answer must include {expectedOutput} and {expectedMarker}."
                    },
                    TimeSpan.FromMinutes(3),
                    ct).ConfigureAwait(false);
                finalContent = result?.Data?.Content;
            }
            finally
            {
                await copilotService.DisposeAndDeleteSessionAsync(session).ConfigureAwait(false);
            }

            var starts = File.Exists(logPath)
                ? await File.ReadAllLinesAsync(logPath, ct).ConfigureAwait(false)
                : [];
            var serverStarts = starts.Count(line => line.StartsWith(marker + "|", StringComparison.Ordinal));
            var hasExpectedOutput = finalContent?.Contains(expectedMarker, StringComparison.Ordinal) == true;
            var hasContract = finalContent?.Contains(expectedOutput, StringComparison.Ordinal) == true;
            var hasToolLifecycle = toolStarted > 0 && toolCompleted > 0;
            var startedServer = useProxy ? serverStarts == 1 : serverStarts > 0;
            var createPassed = hasExpectedOutput && hasContract && hasToolLifecycle && startedServer;

            Console.WriteLine($"Tool started: {toolStarted}");
            Console.WriteLine($"Tool completed: {toolCompleted}");
            Console.WriteLine($"{harnessName} MCP starts: {serverStarts}");
            Console.WriteLine($"Final content: {finalContent}");

            var resumeToolStarted = 0;
            var resumeToolCompleted = 0;
            var resumeServerStarts = 0;
            string? resumeFinalContent = null;
            var resumePassed = false;
            if (createPassed)
            {
                string? resumeSessionId = null;
                CopilotSession? seedSession = null;
                CopilotSession? resumedSession = null;
                try
                {
                    var seedConfig = SessionConfigBuilder.Build(
                        systemPrompt: $"You are seeding Lumi's {harnessName} MCP resume validation. Reply normally.",
                        model: model,
                        workingDirectory: root,
                        skillDirectories: null,
                        customAgents: null,
                        tools: null,
                        mcpServers: [],
                        reasoningEffort: null,
                        userInputHandler: null,
                        onPermission: null,
                        hooks: null);
                    seedSession = await copilotService.CreateSessionAsync(seedConfig, ct).ConfigureAwait(false);
                    resumeSessionId = seedSession.SessionId;
                    await seedSession.SendAndWaitAsync(
                        new MessageOptions { Prompt = "Reply exactly MCP_RESUME_SEED_READY." },
                        TimeSpan.FromMinutes(1),
                        ct).ConfigureAwait(false);
                    await seedSession.DisposeAsync().ConfigureAwait(false);
                    seedSession = null;

                    var resumeConfig = SessionConfigBuilder.BuildForResume(
                        systemPrompt: $"""
                            You are running Lumi's deterministic {harnessName} MCP resume harness.
                            You have an MCP tool named emit_marker available through the Copilot SDK MCP integration.
                            You must call emit_marker with value "SDK_RESUME".
                            After the tool call, include "{expectedResumeOutput}" and the exact MCP marker text in the final answer.
                            """,
                        model: model,
                        workingDirectory: root,
                        skillDirectories: null,
                        customAgents: null,
                        tools: null,
                        mcpServers: mcpServers,
                        reasoningEffort: null,
                        userInputHandler: null,
                        onPermission: null,
                        hooks: null);
                    resumedSession = await copilotService.ResumeSessionAsync(resumeSessionId, resumeConfig, ct).ConfigureAwait(false);
                    using var resumeSub = resumedSession.On<SessionEvent>(evt =>
                    {
                        switch (evt)
                        {
                            case ToolExecutionStartEvent:
                                Interlocked.Increment(ref resumeToolStarted);
                                break;
                            case ToolExecutionCompleteEvent:
                                Interlocked.Increment(ref resumeToolCompleted);
                                break;
                        }
                    });

                    var resumeResult = await resumedSession.SendAndWaitAsync(
                        new MessageOptions
                        {
                            Prompt = $"Run the {harnessName} MCP resume validation now. Use emit_marker with {{\"value\":\"SDK_RESUME\"}}.\n"
                                + $"Final answer must include {expectedResumeOutput} and {expectedResumeMarker}."
                        },
                        TimeSpan.FromMinutes(3),
                        ct).ConfigureAwait(false);
                    resumeFinalContent = resumeResult?.Data?.Content;
                }
                finally
                {
                    if (seedSession is not null)
                        await seedSession.DisposeAsync().ConfigureAwait(false);

                    await copilotService.DisposeAndDeleteSessionAsync(resumedSession).ConfigureAwait(false);

                    if (resumedSession is null && !string.IsNullOrWhiteSpace(resumeSessionId))
                        await copilotService.DeleteSessionAsync(resumeSessionId, ct).ConfigureAwait(false);
                }

                var startsAfterResume = File.Exists(logPath)
                    ? await File.ReadAllLinesAsync(logPath, ct).ConfigureAwait(false)
                    : [];
                var totalStartsAfterResume = startsAfterResume.Count(line => line.StartsWith(marker + "|", StringComparison.Ordinal));
                resumeServerStarts = Math.Max(0, totalStartsAfterResume - serverStarts);
                var hasResumeExpectedOutput = resumeFinalContent?.Contains(expectedResumeMarker, StringComparison.Ordinal) == true;
                var hasResumeContract = resumeFinalContent?.Contains(expectedResumeOutput, StringComparison.Ordinal) == true;
                var hasResumeToolLifecycle = resumeToolStarted > 0 && resumeToolCompleted > 0;
                var hasExpectedResumeStarts = useProxy ? resumeServerStarts == 0 : resumeServerStarts > 0;
                resumePassed = hasResumeExpectedOutput && hasResumeContract && hasResumeToolLifecycle && hasExpectedResumeStarts;
            }

            Console.WriteLine($"Resume tool started: {resumeToolStarted}");
            Console.WriteLine($"Resume tool completed: {resumeToolCompleted}");
            Console.WriteLine($"Resume MCP starts: {resumeServerStarts}");
            Console.WriteLine($"Resume final content: {resumeFinalContent}");

            if (createPassed && resumePassed)
            {
                Console.WriteLine($"PASS: {harnessName} Copilot SDK MCP stress check completed.");
                return 0;
            }

            Console.Error.WriteLine($"FAIL: {harnessName} MCP stress check did not satisfy the contract.");
            if (!hasToolLifecycle)
                Console.Error.WriteLine($"- {harnessName} MCP tool lifecycle events were not observed.");
            if (!startedServer)
                Console.Error.WriteLine(useProxy
                    ? "- fake MCP server did not start exactly once."
                    : "- fake MCP server did not start.");
            if (!hasExpectedOutput)
                Console.Error.WriteLine($"- final response did not include {expectedMarker}.");
            if (!hasContract)
                Console.Error.WriteLine($"- final response did not include {expectedOutput}.");
            if (!resumePassed)
                Console.Error.WriteLine($"- {harnessName} MCP resume validation did not satisfy the contract.");
            return 1;
        }
        finally
        {
            if (proxyRuntime is not null)
                await proxyRuntime.DisposeAsync().ConfigureAwait(false);
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    /// <summary>
    /// Real end-to-end validation of the session-release code paths that reap MCP subprocesses.
    /// Part A proves <see cref="CopilotService.ReleaseSessionAsync"/> actually sends
    /// <c>session.destroy</c> and reaps the session's live MCP subprocess (asserted against the real
    /// OS process exiting). Part B proves the cross-surface destroy-before-resume gate: it fires a
    /// release for a session id (registering the in-flight destroy) and then immediately resumes the
    /// SAME id, asserting the resumed session is fully live (its MCP tool works), the old MCP
    /// subprocess was reaped, and a fresh MCP subprocess was spawned — i.e. the late destroy did not
    /// reap the resumed session. Uses the same Windows PowerShell fake MCP server as the MCP stress
    /// harness, which logs each subprocess PID to a file so reaping can be observed directly.
    /// </summary>
    public static async Task<int> RunSessionReapStressAsync(CopilotService copilotService, CancellationToken ct)
    {
        const string marker = "REAP_GLOBAL";
        const string serverName = "reap-global";

        Console.WriteLine("Lumi session-reap + destroy-before-resume stress harness");
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("FAIL: session-reap harness currently uses a Windows PowerShell fake MCP server.");
            return 1;
        }

        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (!File.Exists(powershell))
        {
            Console.Error.WriteLine($"FAIL: PowerShell not found at {powershell}");
            return 1;
        }

        Console.WriteLine("Connecting to Copilot...");
        await copilotService.ConnectAsync(ct).ConfigureAwait(false);
        var model = await copilotService.GetFastestModelIdAsync(ct).ConfigureAwait(false);
        Console.WriteLine($"Model: {model ?? "(default)"}");

        var root = Path.Combine(Path.GetTempPath(), "lumi-session-reap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var scriptPath = Path.Combine(root, "fake-mcp.ps1");
        var logPath = Path.Combine(root, "starts.log");
        var createdSessionIds = new List<string>();

        try
        {
            await File.WriteAllTextAsync(scriptPath, """
                [System.IO.File]::AppendAllText($env:MCP_TEST_LOG, "$($env:MCP_MARKER)|$PID`n")
                function Write-Json($obj) {
                    [Console]::Out.WriteLine(($obj | ConvertTo-Json -Compress -Depth 30))
                    [Console]::Out.Flush()
                }
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    $msg = $line | ConvertFrom-Json
                    if ($msg.method -eq "initialize") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ protocolVersion = "2025-11-25"; capabilities = @{}; serverInfo = @{ name = "lumi-reap-fake-mcp"; version = "1" } } }
                    } elseif ($msg.method -eq "tools/list") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ tools = @(@{ name = "emit_marker"; description = "Return the configured MCP marker and requested value."; inputSchema = @{ type = "object"; properties = @{ value = @{ type = "string" } }; required = @("value") } }) } }
                    } elseif ($msg.method -eq "tools/call") {
                        $value = [string]$msg.params.arguments.value
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ content = @(@{ type = "text"; text = "MCP_MARKER:$($env:MCP_MARKER):$value" }) } }
                    }
                }
                """, ct).ConfigureAwait(false);

            var server = new McpServer
            {
                Name = serverName,
                Command = powershell,
                Args = ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                Env =
                {
                    ["MCP_MARKER"] = marker,
                    ["MCP_TEST_LOG"] = logPath,
                },
            };
            var data = new AppData { McpServers = [server] };
            var chat = new Chat { ActiveMcpServerNames = [serverName] };
            var mcpServers = McpSessionPlanner.Build(data, root, new ProjectContextCatalogSnapshot([], [], []), chat, [serverName], null, null);
            if (!mcpServers.ContainsKey(serverName))
            {
                Console.Error.WriteLine($"FAIL: {serverName} MCP server was not included in the SDK config.");
                return 1;
            }

            int[] ReadStartPids() => File.Exists(logPath)
                ? File.ReadAllLines(logPath)
                    .Where(l => l.StartsWith(marker + "|", StringComparison.Ordinal))
                    .Select(l => int.TryParse(l.AsSpan(l.IndexOf('|') + 1), out var pid) ? pid : -1)
                    .Where(pid => pid > 0)
                    .ToArray()
                : [];

            static bool IsProcessAlive(int pid)
            {
                try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
                catch (ArgumentException) { return false; }
            }

            int[] AliveStartPids() => ReadStartPids().Where(IsProcessAlive).Distinct().ToArray();

            static bool WaitForProcessExit(int pid, TimeSpan timeout)
            {
                Process proc;
                try { proc = Process.GetProcessById(pid); }
                catch (ArgumentException) { return true; }
                try
                {
                    var sw = Stopwatch.StartNew();
                    while (sw.Elapsed < timeout)
                    {
                        if (proc.HasExited) return true;
                        Thread.Sleep(150);
                    }
                    return proc.HasExited;
                }
                finally { proc.Dispose(); }
            }

            async Task<(bool toolOk, bool contractOk, string? content)> RunToolTurnAsync(
                CopilotSession session, string value, string contract)
            {
                var markerText = $"MCP_MARKER:{marker}:{value}";
                var started = 0;
                var completed = 0;
                using var sub = session.On<SessionEvent>(evt =>
                {
                    switch (evt)
                    {
                        case ToolExecutionStartEvent:
                            Interlocked.Increment(ref started);
                            break;
                        case ToolExecutionCompleteEvent:
                            Interlocked.Increment(ref completed);
                            break;
                    }
                });
                var result = await session.SendAndWaitAsync(
                    new MessageOptions
                    {
                        Prompt = $"Use emit_marker with {{\"value\":\"{value}\"}} now.\n"
                            + $"Final answer must include {contract} and {markerText}."
                    },
                    TimeSpan.FromMinutes(3),
                    ct).ConfigureAwait(false);
                var content = result?.Data?.Content;
                var contractOk = content?.Contains(contract, StringComparison.Ordinal) == true
                    && content?.Contains(markerText, StringComparison.Ordinal) == true;
                return (started > 0 && completed > 0, contractOk, content);
            }

            const string sysPrompt = "You are Lumi's session-reap harness. You have an MCP tool named "
                + "emit_marker available through the Copilot SDK MCP integration. When asked, call it "
                + "with the requested value and include the contract token and exact MCP marker text in "
                + "your final answer.";

            SessionConfig BuildCreateConfig() => SessionConfigBuilder.Build(
                systemPrompt: sysPrompt,
                model: model, workingDirectory: root, skillDirectories: null, customAgents: null,
                tools: null, mcpServers: mcpServers, reasoningEffort: null, userInputHandler: null,
                onPermission: null, hooks: null);

            ResumeSessionConfig BuildResumeConfig() => SessionConfigBuilder.BuildForResume(
                systemPrompt: sysPrompt,
                model: model, workingDirectory: root, skillDirectories: null, customAgents: null,
                tools: null, mcpServers: mcpServers, reasoningEffort: null, userInputHandler: null,
                onPermission: null, hooks: null);

            // ---------- Part A: ReleaseSessionAsync reaps the live MCP subprocess ----------
            Console.WriteLine("== Part A: ReleaseSessionAsync reaps the live MCP subprocess ==");
            var sessionA = await copilotService.CreateSessionAsync(BuildCreateConfig(), ct).ConfigureAwait(false);
            createdSessionIds.Add(sessionA.SessionId);
            var turnA = await RunToolTurnAsync(sessionA, "REAP_A", "LUMI_REAP_A_OK").ConfigureAwait(false);
            var aliveAfterA = AliveStartPids();
            var pidA = aliveAfterA.Length > 0 ? aliveAfterA[^1] : -1;
            var mcpAliveBeforeRelease = pidA > 0;
            Console.WriteLine($"Part A tool ok: {turnA.toolOk}, contract ok: {turnA.contractOk}");
            Console.WriteLine($"Part A MCP subprocess PID: {pidA}, alive before release: {mcpAliveBeforeRelease}");

            await copilotService.ReleaseSessionAsync(sessionA, deleteServerSession: false).WaitAsync(ct).ConfigureAwait(false);
            var mcpReaped = pidA > 0 && WaitForProcessExit(pidA, TimeSpan.FromSeconds(20));
            Console.WriteLine($"Part A MCP subprocess exited after ReleaseSessionAsync: {mcpReaped}");
            var reapPassed = turnA.toolOk && turnA.contractOk && mcpAliveBeforeRelease && mcpReaped;

            // ---------- Part B: fire release (registers destroy) + resume the SAME id ----------
            Console.WriteLine("== Part B: fire ReleaseSessionAsync then resume the same id (destroy-before-resume gate) ==");
            var sessionB = await copilotService.CreateSessionAsync(BuildCreateConfig(), ct).ConfigureAwait(false);
            var idB = sessionB.SessionId;
            createdSessionIds.Add(idB);
            var turnB1 = await RunToolTurnAsync(sessionB, "REAP_B", "LUMI_REAP_B_OK").ConfigureAwait(false);
            var aliveAfterCreateB = AliveStartPids();
            var pidB1 = aliveAfterCreateB.Length > 0 ? aliveAfterCreateB[^1] : -1;
            Console.WriteLine($"Part B create tool ok: {turnB1.toolOk}, contract ok: {turnB1.contractOk}, MCP PID: {pidB1}");

            // Fire the release WITHOUT awaiting: this registers the in-flight destroy for idB in the
            // shared CopilotService registry (synchronously, before this call returns). NOTE: the
            // *deterministic* proof that ResumeSessionAsync BLOCKS on an in-flight release of the same
            // id is the unit test ResumeGate_WaitsForInFlightReleaseOfSameSessionId (a
            // TaskCompletionSource holds the release open and asserts the gate does not complete until
            // it settles). This part is the real-process complement: it proves the whole path works end
            // to end against real sessions + real MCP subprocesses under realistic timing. We record
            // whether the release was still in flight when resume began so a run shows when the gate
            // actually had to wait (informational only — real destroy timing is not deterministic).
            var releaseTaskB = copilotService.ReleaseSessionAsync(sessionB, deleteServerSession: false);
            var releaseStillInFlightAtResume = !releaseTaskB.IsCompleted;
            Console.WriteLine($"Part B release still in-flight when resume began: {releaseStillInFlightAtResume}");

            // Immediately resume the SAME id. The CopilotService gate must wait for any in-flight
            // destroy of this id to finish before handing back the resumed session.
            var resumed = await copilotService.ResumeSessionAsync(idB, BuildResumeConfig(), ct).ConfigureAwait(false);

            // The resumed session must be fully live end-to-end: its MCP tool must work.
            var turnB2 = await RunToolTurnAsync(resumed, "RESUME_B", "LUMI_RESUME_B_OK").ConfigureAwait(false);

            await releaseTaskB.WaitAsync(ct).ConfigureAwait(false); // ensure the fired destroy has settled
            var oldMcpReaped = pidB1 > 0 && WaitForProcessExit(pidB1, TimeSpan.FromSeconds(20));
            var aliveAfterResume = AliveStartPids();
            var pidB2 = aliveAfterResume.FirstOrDefault(p => p != pidB1);
            var newMcpSpawned = pidB2 > 0 && pidB2 != pidB1;
            Console.WriteLine($"Part B resumed tool ok: {turnB2.toolOk}, contract ok: {turnB2.contractOk}");
            Console.WriteLine($"Part B old MCP {pidB1} reaped: {oldMcpReaped}; resumed MCP PID: {pidB2} (new: {newMcpSpawned})");

            await copilotService.DisposeAndDeleteSessionAsync(resumed).WaitAsync(ct).ConfigureAwait(false);
            createdSessionIds.Remove(idB);

            var resumePassed = turnB1.toolOk && turnB2.toolOk && turnB2.contractOk && oldMcpReaped && newMcpSpawned;

            if (reapPassed && resumePassed)
            {
                Console.WriteLine("PASS: session-reap + destroy-before-resume validation completed against real sessions and MCP subprocesses.");
                return 0;
            }

            Console.Error.WriteLine("FAIL: session-reap validation did not satisfy the contract.");
            if (!reapPassed)
            {
                if (!turnA.toolOk) Console.Error.WriteLine("- Part A tool lifecycle events were not observed.");
                if (!turnA.contractOk) Console.Error.WriteLine("- Part A final response did not include the contract/marker.");
                if (!mcpAliveBeforeRelease) Console.Error.WriteLine("- Part A MCP subprocess was not observed alive before release.");
                if (!mcpReaped) Console.Error.WriteLine("- Part A MCP subprocess was NOT reaped after ReleaseSessionAsync (leak).");
            }
            if (!resumePassed)
            {
                if (!turnB1.toolOk) Console.Error.WriteLine("- Part B create tool lifecycle events were not observed.");
                if (!turnB2.toolOk || !turnB2.contractOk) Console.Error.WriteLine("- Part B resumed session tool call did not succeed (possible over-destroy of the resumed session).");
                if (!oldMcpReaped) Console.Error.WriteLine("- Part B original MCP subprocess was NOT reaped (leak).");
                if (!newMcpSpawned) Console.Error.WriteLine("- Part B resumed session did not spawn a fresh MCP subprocess.");
            }
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: session-reap harness threw: {ex}");
            return 1;
        }
        finally
        {
            foreach (var id in createdSessionIds)
            {
                try { await copilotService.DeleteSessionAsync(id, ct).ConfigureAwait(false); }
                catch { }
            }
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    private static string EnsureFixtureDirectory()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Lumi",
            "debug-fixtures");
        Directory.CreateDirectory(path);
        return path;
    }
}
#endif
