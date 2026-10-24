namespace Lumi.Qa;

public sealed record QaSmokeOptions(bool Enabled, bool VerifyRestart, string? OutputDirectory)
{
    public static QaSmokeOptions Parse(string[] args)
    {
        var enabled = args.Contains("--qa-smoke", StringComparer.OrdinalIgnoreCase);
        var restart = args.Contains("--qa-verify-restart", StringComparer.OrdinalIgnoreCase);
        var outputIndex = Array.FindIndex(args, argument =>
            argument.Equals("--qa-output", StringComparison.OrdinalIgnoreCase));
        if (!enabled && (restart || outputIndex >= 0))
            throw new ArgumentException("QA options require --qa-smoke.");
        if (outputIndex >= 0 && (outputIndex + 1 == args.Length
            || string.IsNullOrWhiteSpace(args[outputIndex + 1])
            || args[outputIndex + 1].StartsWith("--", StringComparison.Ordinal)))
            throw new ArgumentException("--qa-output requires an output directory.");

        return new(enabled, restart, outputIndex < 0 ? null : Path.GetFullPath(args[outputIndex + 1]));
    }
}

public sealed record QaScenarioResult(string Id, bool Passed, double DurationMs, string? Error = null);

public sealed class QaSmokeReport
{
    public static IReadOnlyList<string> ScenarioIds { get; } =
    [
        "navigation", "project-save", "ai-settings", "chat-streaming",
        "approval-denied", "approval-allowed", "persist-checkpoint"
    ];

    public static IReadOnlyList<string> RestartScenarioIds { get; } = ["restart-persistence"];

    public required IReadOnlyList<string> ExpectedScenarios { get; init; }
    public List<QaScenarioResult> Scenarios { get; } = [];

    public bool Complete => ExpectedScenarios.Count > 0
        && ExpectedScenarios.Distinct(StringComparer.Ordinal).Count() == ExpectedScenarios.Count
        && Scenarios.Count == ExpectedScenarios.Count
        && Scenarios.All(result => result.Passed)
        && Scenarios.Select(result => result.Id).Order(StringComparer.Ordinal)
            .SequenceEqual(ExpectedScenarios.Order(StringComparer.Ordinal), StringComparer.Ordinal);
}
