// Onboarding harness (Debug only)
// Run: dotnet run --project src\Lumi\Lumi.csproj -- --test-onboarding-agent
//
// Runs onboarding's local workspace discovery on this computer and prints what it found, then asks the
// background model to personalize onboarding and prints the result next to the deterministic fallback.
// Use it to tune discovery and the personalization prompt without clicking through the UI. Nothing is
// saved: no projects or memories are created.

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Localization;
using Lumi.Services;
using Lumi.Services.Onboarding;

namespace Lumi;

public static class OnboardingAgentTest
{
    private const string UserName = "TestUser";

    public static async Task RunAsync(CopilotService copilotService, CancellationToken ct)
    {
        Loc.Load("en");
        Heading("ONBOARDING HARNESS · workspace discovery");

        var discovery = new WorkspaceDiscovery();
        var projects = await Timed("Projects", () => discovery.FindProjectsAsync(ct));
        foreach (var project in projects)
        {
            Console.WriteLine($"  • {project.Name} [{project.Kind}] {project.Path}");
            Console.WriteLine($"      {project.Stack ?? "-"} · {project.Remote ?? "-"} · active {project.LastActive:yyyy-MM-dd HH:mm} · editor rank {project.EditorRank}");
            if (project.Description is { } description)
                Console.WriteLine($"      {description}");
        }

        var apps = await Timed("Apps", () => discovery.FindAppsAsync(ct));
        Console.WriteLine($"  {string.Join(", ", apps)}");
        var sites = await Timed("Sites", () => discovery.FindSitesAsync(ct));
        Console.WriteLine($"  {string.Join(", ", sites)}");
        var documents = await Timed("Recent files", () => discovery.FindDocumentsAsync(ct));
        Console.WriteLine($"  {documents}");

        var workspace = new WorkspaceSnapshot(projects, apps, sites, documents);
        Heading("Prompt sent to the background model");
        Console.WriteLine(OnboardingPersonalizer.BuildPrompt(UserName, workspace));

        Heading("Deterministic fallback");
        Print(OnboardingPersonalizer.Fallback(UserName, workspace));

        Heading("Personalized by the background model");
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var personalization = await OnboardingPersonalizer.GenerateAsync(copilotService, UserName, workspace, ct);
            Console.WriteLine($"  ({stopwatch.Elapsed.TotalSeconds:F1}s, personalized: {personalization.IsPersonalized})");
            Print(personalization);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  Personalization failed: {ex.Message}");
            Console.ResetColor();
        }
    }

    private static void Print(OnboardingPersonalization personalization)
    {
        Console.WriteLine($"  Lumi: {personalization.Observation}");
        foreach (var question in personalization.Questions)
        {
            Console.WriteLine($"  [{question.Slot}] {question.Text}");
            Console.WriteLine($"      {string.Join(" | ", question.Options)}");
            var answer = string.Join(", ", question.Options.Take(2));
            var memory = OnboardingPersonalizer.BuildDurableMemory(question.Slot, UserName, answer);
            Console.WriteLine(memory is null
                ? "      → (no lasting memory)"
                : $"      → {memory.Key}: {memory.Content}");
        }

        foreach (var starter in personalization.Starters)
            Console.WriteLine($"  {starter.Icon} {starter.Title}{(starter.ProjectName is { } project ? $" ({project})" : "")}: {starter.Prompt}");
    }

    private static async Task<T> Timed<T>(string label, Func<Task<T>> run)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await run();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n✓ {label} ({stopwatch.ElapsedMilliseconds} ms)");
        Console.ResetColor();
        return result;
    }

    private static void Heading(string text)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine();
        Console.WriteLine($"═══ {text} ═══");
        Console.ResetColor();
    }
}
