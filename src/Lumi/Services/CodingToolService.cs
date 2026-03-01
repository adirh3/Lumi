using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

namespace Lumi.Services;

/// <summary>
/// Provides coding-focused tools that leverage the Copilot SDK's lightweight sessions
/// as sub-agents for specialized code analysis tasks.
/// </summary>
public sealed class CodingToolService
{
    private readonly CopilotService _copilotService;
    private readonly Func<CancellationToken> _getCancellationToken;

    private static void Log(string message) => Debug.WriteLine($"[CodingTools] {message}");

    public CodingToolService(CopilotService copilotService, Func<CancellationToken> getCancellationToken)
    {
        _copilotService = copilotService;
        _getCancellationToken = getCancellationToken;
    }

    public List<AIFunction> BuildCodingTools()
    {
        return
        [
            BuildCodeReviewTool(),
            BuildGenerateTestsTool(),
            BuildExplainCodeTool(),
            BuildAnalyzeProjectTool(),
        ];
    }

    private AIFunction BuildCodeReviewTool()
    {
        return AIFunctionFactory.Create(
            async (
                [Description("The source code to review. Include the full file content.")] string code,
                [Description("The programming language (e.g. csharp, python, typescript)")] string language,
                [Description("Optional context: what the code does, project constraints, or specific concerns")] string? context) =>
            {
                return await RunSubAgentAsync(
                    BuildCodeReviewPrompt(language, context),
                    $"Review this {language} code:\n\n```{language}\n{code}\n```",
                    _getCancellationToken());
            },
            "code_review",
            "Expert code review: analyze code for bugs, security vulnerabilities, performance issues, and best practice violations. Returns structured feedback with severity levels and fix suggestions.");
    }

    private AIFunction BuildGenerateTestsTool()
    {
        return AIFunctionFactory.Create(
            async (
                [Description("The source code to generate tests for")] string code,
                [Description("The programming language (e.g. csharp, python, typescript)")] string language,
                [Description("The test framework to use (e.g. xunit, nunit, pytest, jest). If not specified, the best framework for the language is chosen.")] string? framework,
                [Description("Optional context: what should be tested, edge cases to cover, project conventions")] string? context) =>
            {
                return await RunSubAgentAsync(
                    BuildTestGeneratorPrompt(language, framework, context),
                    $"Generate tests for this {language} code:\n\n```{language}\n{code}\n```",
                    _getCancellationToken());
            },
            "generate_tests",
            "Generate comprehensive unit tests for source code. Covers happy paths, edge cases, error handling, and boundary conditions. Returns ready-to-run test code.");
    }

    private AIFunction BuildExplainCodeTool()
    {
        return AIFunctionFactory.Create(
            async (
                [Description("The source code to explain")] string code,
                [Description("The programming language (e.g. csharp, python, typescript)")] string language,
                [Description("The depth of explanation: 'overview' for high-level, 'detailed' for line-by-line, 'teaching' for beginner-friendly")] string depth,
                [Description("Optional: specific aspects to focus on (e.g. 'the async pattern', 'the algorithm', 'error handling')")] string? focus) =>
            {
                return await RunSubAgentAsync(
                    BuildExplainCodePrompt(language, depth, focus),
                    $"Explain this {language} code:\n\n```{language}\n{code}\n```",
                    _getCancellationToken());
            },
            "explain_code",
            "Deep code explanation: break down code into understandable parts with call flow, data flow, and pattern identification. Adapts depth from high-level overview to line-by-line teaching.");
    }

    private AIFunction BuildAnalyzeProjectTool()
    {
        return AIFunctionFactory.Create(
            async (
                [Description("The directory listing or file tree of the project (output from ls/dir/tree command)")] string projectTree,
                [Description("Optional: content of key config files (package.json, .csproj, Cargo.toml, etc.) to identify dependencies")] string? configFiles,
                [Description("Optional: specific questions about the project architecture")] string? questions) =>
            {
                return await RunSubAgentAsync(
                    BuildProjectAnalysisPrompt(questions),
                    BuildProjectAnalysisMessage(projectTree, configFiles),
                    _getCancellationToken());
            },
            "analyze_project",
            "Analyze a project's architecture, tech stack, dependencies, entry points, and structure. Returns a comprehensive project map with actionable insights.");
    }

    /// <summary>
    /// Runs a sub-agent using a lightweight Copilot session with a specialized system prompt.
    /// The sub-agent is isolated, disposable, and optimized for a single analysis task.
    /// </summary>
    private async Task<string> RunSubAgentAsync(string systemPrompt, string userMessage, CancellationToken ct)
    {
        if (!_copilotService.IsConnected)
            return "Error: Copilot is not connected.";

        CopilotSession? session = null;
        try
        {
            session = await _copilotService.CreateLightweightSessionAsync(
                systemPrompt, null, [], ct);

            var result = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = userMessage },
                TimeSpan.FromSeconds(120),
                ct);

            var content = result?.Data?.Content?.Trim();
            return string.IsNullOrWhiteSpace(content)
                ? "Analysis completed but produced no output."
                : content;
        }
        catch (OperationCanceledException)
        {
            return "Analysis was cancelled.";
        }
        catch (Exception ex)
        {
            Log($"Sub-agent failed: {ex.Message}");
            return $"Analysis failed: {ex.Message}";
        }
        finally
        {
            if (session is not null)
                await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ── System Prompts for Sub-Agents ───────────────────────────────────────

    private static string BuildCodeReviewPrompt(string language, string? context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            You are an elite code reviewer — one of the best in the industry. You have deep expertise in software engineering, security, and performance optimization.

            Your review must be thorough, actionable, and organized. Analyze the code for:

            ## Review Categories

            1. **🐛 Bugs & Logic Errors** — Race conditions, off-by-one errors, null dereferences, incorrect logic, unhandled states
            2. **🔒 Security Vulnerabilities** — Injection attacks, data exposure, improper validation, OWASP Top 10 violations, insecure defaults
            3. **⚡ Performance Issues** — Unnecessary allocations, N+1 queries, inefficient algorithms, missing caching opportunities, blocking async code
            4. **🏗️ Architecture & Design** — SOLID violations, coupling issues, missing abstractions, god classes, responsibility confusion
            5. **✨ Code Quality** — Naming clarity, dead code, duplication, magic numbers, missing error handling
            6. **🧪 Testability** — Hard-to-test code, hidden dependencies, side effects, missing seams for testing

            ## Output Format

            For each finding, provide:
            - **Severity**: 🔴 Critical / 🟡 Warning / 🔵 Info
            - **Location**: Line number or code snippet reference
            - **Issue**: Clear description of the problem
            - **Fix**: Concrete code suggestion

            End with a **Summary** section: overall code health score (1-10), top 3 priorities, and what the code does well.

            Be specific and honest. Don't sugarcoat real issues, but acknowledge good patterns too.
            """);

        if (!string.IsNullOrWhiteSpace(context))
            sb.AppendLine($"\n## Additional Context\n{context}");

        sb.AppendLine($"\nThe code is written in **{language}**. Apply {language}-specific best practices and idioms.");
        return sb.ToString();
    }

    private static string BuildTestGeneratorPrompt(string language, string? framework, string? context)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"""
            You are a test engineering expert. Generate comprehensive, production-quality unit tests.

            ## Requirements

            - Language: **{language}**
            - Framework: **{framework ?? "choose the most appropriate for " + language}**
            - Generate COMPLETE, RUNNABLE test code — not pseudocode or snippets
            - Include all necessary imports/usings
            - Use the Arrange-Act-Assert pattern
            - Name tests descriptively: MethodName_Scenario_ExpectedResult

            ## Test Coverage Strategy

            Generate tests for ALL of these categories:

            1. **Happy Path** — Normal expected inputs and outputs
            2. **Edge Cases** — Empty inputs, single elements, boundary values, min/max
            3. **Error Handling** — Invalid inputs, null arguments, exceptions, error states
            4. **Boundary Conditions** — Integer overflow, empty strings, max size collections
            5. **State Transitions** — If the code has state, test all valid transitions
            6. **Concurrency** (if applicable) — Thread safety, race condition detection

            ## Quality Standards

            - Each test should test ONE thing
            - Tests should be independent and idempotent
            - Use descriptive variable names in tests
            - Add brief comments for non-obvious test rationale
            - Mock external dependencies, don't test framework code
            - Include both positive and negative test cases

            Output ONLY the test code file contents, ready to save and run.
            """);

        if (!string.IsNullOrWhiteSpace(context))
            sb.AppendLine($"\n## Additional Context\n{context}");

        return sb.ToString();
    }

    private static string BuildExplainCodePrompt(string language, string depth, string? focus)
    {
        var depthInstruction = depth switch
        {
            "overview" => "Give a high-level overview. Focus on what the code does, its purpose, and how the major pieces fit together. Skip implementation details.",
            "detailed" => "Provide a detailed walkthrough. Cover the data flow, control flow, each function/method's purpose, and how they interact. Reference specific lines.",
            "teaching" => "Explain as if teaching someone who knows programming basics but is new to this language/pattern. Define terms, explain why things are done this way, and suggest further learning.",
            _ => "Provide a balanced explanation covering both high-level purpose and key implementation details."
        };

        var sb = new StringBuilder();
        sb.AppendLine($"""
            You are a patient, expert code educator. Explain the following {language} code clearly and accurately.

            ## Depth Level
            {depthInstruction}

            ## Structure Your Explanation

            1. **Purpose** — What does this code accomplish? What problem does it solve?
            2. **Architecture** — How is it organized? What are the key components?
            3. **Data Flow** — How does data move through the code?
            4. **Key Patterns** — What design patterns, idioms, or techniques are used?
            5. **Dependencies** — What does this code depend on? What depends on it?
            6. **Potential Gotchas** — What's subtle or easy to misunderstand?

            Use concrete references to the code. When explaining a concept, point to the exact line or function that demonstrates it.
            """);

        if (!string.IsNullOrWhiteSpace(focus))
            sb.AppendLine($"\n## Focus Area\nThe user is specifically interested in: **{focus}**. Prioritize this in your explanation.");

        return sb.ToString();
    }

    private static string BuildProjectAnalysisPrompt(string? questions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            You are a software architect analyzing a project's structure. Provide a comprehensive project analysis.

            ## Analysis Output

            ### 🏗️ Project Overview
            - Project type (web app, CLI, library, desktop app, mobile, etc.)
            - Primary programming language(s)
            - Framework(s) and major dependencies
            - Build system

            ### 📁 Architecture Map
            - Source code organization pattern (layers, features, modules)
            - Entry point(s)
            - Key directories and their purposes
            - Configuration files and their roles

            ### 🔗 Dependency Analysis
            - Third-party dependencies and their purposes
            - Internal module dependencies
            - Any concerning dependency patterns (circular deps, outdated packages)

            ### 🧭 Navigation Guide
            - Where to find the main business logic
            - Where to find tests
            - Where to find configuration
            - Where to find API definitions or routes
            - Key files a new developer should read first

            ### 💡 Observations
            - Code organization strengths
            - Potential improvements
            - Notable patterns or conventions used
            - Missing elements (tests, docs, CI/CD, etc.)

            Be specific and reference actual file paths from the project tree.
            """);

        if (!string.IsNullOrWhiteSpace(questions))
            sb.AppendLine($"\n## Specific Questions\n{questions}");

        return sb.ToString();
    }

    private static string BuildProjectAnalysisMessage(string projectTree, string? configFiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Project File Tree\n```");
        sb.AppendLine(projectTree);
        sb.AppendLine("```");

        if (!string.IsNullOrWhiteSpace(configFiles))
        {
            sb.AppendLine("\n## Configuration Files\n");
            sb.AppendLine(configFiles);
        }

        return sb.ToString();
    }
}
