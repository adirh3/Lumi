using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lumi.MemoryDiagnostics;

/// <summary>Parsed configuration for Lumi's DEBUG-only memory stress harness.</summary>
public sealed class MemoryHarnessOptions
{
    public static readonly IReadOnlyList<string> EnableFlags = new[]
    {
        "--memory-stress-harness",
        "--memory-leak-harness",
        "--stress-memory",
        "--memory-harness",
    };

    public bool Enabled { get; private set; }
    public int Cycles { get; private set; } = 6;
    public int WarmupCycles { get; private set; } = 2;
    public int ActionsPerCycle { get; private set; } = 12;
    public int SettleMilliseconds { get; private set; } = 100;
    public int GcPasses { get; private set; } = 4;
    public long MaxManagedGrowthBytes { get; private set; } = 24L * 1024 * 1024;
    public long MaxManagedSlopeBytesPerCycle { get; private set; } = 2L * 1024 * 1024;
    public bool KeepOpen { get; private set; }
    public string? OutputPath { get; private set; }

    private readonly List<string> _rawScenarios = [];
    private readonly HashSet<string> _includedScenarios = new(StringComparer.Ordinal);

    public IReadOnlyList<string> RequestedScenarios => _rawScenarios;
    public bool IsFull => _includedScenarios.Count == 0;
    public string Mode => IsFull ? "full" : "filtered";

    public bool IncludesScenario(string scenario)
        => IsFull || _includedScenarios.Contains(NormalizeScenario(scenario));

    public static string NormalizeScenario(string scenario)
    {
        if (string.IsNullOrWhiteSpace(scenario))
            return string.Empty;

        Span<char> buffer = stackalloc char[scenario.Length];
        var length = 0;
        foreach (var ch in scenario)
        {
            if (ch is '-' or '_' or ' ')
                continue;
            buffer[length++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer[..length]);
    }

    public static MemoryHarnessOptions Parse(IReadOnlyList<string>? args)
    {
        var options = new MemoryHarnessOptions();
        if (args is null || args.Count == 0)
            return options;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg))
                continue;

            var (key, inlineValue) = SplitFlag(arg);

            string? TakeValue()
            {
                if (inlineValue is not null)
                    return inlineValue;
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    return args[++i];
                return null;
            }

            switch (key.ToLowerInvariant())
            {
                case "--memory-stress-harness":
                case "--memory-leak-harness":
                case "--stress-memory":
                case "--memory-harness":
                    options.Enabled = true;
                    break;
                case "--memory-cycles":
                case "--memory-iterations":
                    if (TryInt(TakeValue(), out var cycles))
                        options.Cycles = Math.Clamp(cycles, 1, 100);
                    break;
                case "--memory-warmup":
                case "--memory-warmup-cycles":
                    if (TryInt(TakeValue(), out var warmup))
                        options.WarmupCycles = Math.Clamp(warmup, 0, 20);
                    break;
                case "--memory-actions":
                case "--memory-actions-per-cycle":
                    if (TryInt(TakeValue(), out var actions))
                        options.ActionsPerCycle = Math.Clamp(actions, 1, 200);
                    break;
                case "--memory-settle-ms":
                    if (TryInt(TakeValue(), out var settleMs))
                        options.SettleMilliseconds = Math.Clamp(settleMs, 0, 5000);
                    break;
                case "--memory-gc-passes":
                    if (TryInt(TakeValue(), out var gcPasses))
                        options.GcPasses = Math.Clamp(gcPasses, 1, 10);
                    break;
                case "--memory-max-growth-mb":
                    if (TryInt(TakeValue(), out var growthMb))
                        options.MaxManagedGrowthBytes = Math.Clamp(growthMb, 1, 4096) * 1024L * 1024L;
                    break;
                case "--memory-max-slope-mb":
                    if (TryInt(TakeValue(), out var slopeMb))
                        options.MaxManagedSlopeBytesPerCycle = Math.Clamp(slopeMb, 1, 1024) * 1024L * 1024L;
                    break;
                case "--memory-keep-open":
                case "--memory-keepopen":
                    options.KeepOpen = true;
                    break;
                case "--memory-output":
                case "--memory-out":
                    options.OutputPath = TakeValue();
                    break;
                case "--memory-filter":
                case "--memory-scenarios":
                case "--memory-only":
                    options.AddScenarios(TakeValue());
                    break;
                case "--memory-mode":
                    if (string.Equals(TakeValue(), "full", StringComparison.OrdinalIgnoreCase))
                    {
                        options._rawScenarios.Clear();
                        options._includedScenarios.Clear();
                    }
                    break;
            }
        }

        return options;
    }

    private void AddScenarios(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        foreach (var token in value.Split(
                     new[] { ',', ';', ' ', '|' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = NormalizeScenario(token);
            if (normalized.Length == 0)
                continue;
            if (_includedScenarios.Add(normalized))
                _rawScenarios.Add(token.Trim());
        }
    }

    private static (string Key, string? Value) SplitFlag(string arg)
    {
        var equalsIndex = arg.IndexOf('=');
        return equalsIndex < 0
            ? (arg, null)
            : (arg[..equalsIndex], arg[(equalsIndex + 1)..]);
    }

    private static bool TryInt(string? value, out int result)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
}
