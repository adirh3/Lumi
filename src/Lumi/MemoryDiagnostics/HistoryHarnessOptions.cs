using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lumi.MemoryDiagnostics;

/// <summary>
/// Options for the DEBUG-only history-scale harness, which measures how Lumi behaves on a large,
/// real-shaped profile: opening very long chats, scrolling through their history and switching
/// between them. It only runs against a caller-provided <c>LUMI_APPDATA_DIR</c>.
/// </summary>
public sealed class HistoryHarnessOptions
{
    public bool Enabled { get; private set; }
    public IReadOnlyList<Guid> ChatIds => _chatIds;
    public int TopChats { get; private set; } = 4;
    public int ScrollSteps { get; private set; } = 80;

    /// <summary>
    /// Scrolls with touchpad gestures instead of page steps (<c>--history-touchpad</c>), measuring frames,
    /// input delay and how much of the viewport is still empty (TouchpadScroll).
    /// </summary>
    public bool Touchpad { get; private set; }
    public int Gestures { get; private set; } = 20;
    public int OpenTimeoutSeconds { get; private set; } = 180;
    public bool KeepOpen { get; private set; }
    public bool WaitForMigration { get; private set; }

    /// <summary>
    /// Instead of measuring, checks that each target chat reads the same built exchange by exchange
    /// as built whole (<c>--history-fidelity</c>).
    /// </summary>
    public bool CheckFidelity { get; private set; }

    /// <summary>
    /// Instead of scrolling, measures what each target chat's history costs to hold
    /// (<c>--history-costs</c>): the Workspace's rows of its older exchanges, and its largest older
    /// exchange built — post-GC memory held, bytes allocated and time taken.
    /// </summary>
    public bool MeasureCosts { get; private set; }

    /// <summary>
    /// Instead of measuring, waits for the legacy import and compares every chat the legacy index
    /// listed with what was stored: its metadata and each message (<c>--history-verify-migration</c>).
    /// </summary>
    public bool VerifyMigration { get; private set; }
    public string? OutputPath { get; private set; }
    public string Label { get; private set; } = "run";

    private readonly List<Guid> _chatIds = [];

    public static bool IsFlag(string arg)
        => string.Equals(arg, "--history-harness", StringComparison.OrdinalIgnoreCase)
           || string.Equals(arg, "--history-scale-harness", StringComparison.OrdinalIgnoreCase);

    public static HistoryHarnessOptions Parse(IReadOnlyList<string>? args)
    {
        var options = new HistoryHarnessOptions();
        if (args is null)
            return options;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? Next() => i + 1 < args.Count ? args[++i] : null;

            if (IsFlag(arg))
            {
                options.Enabled = true;
                continue;
            }

            switch (arg.ToLowerInvariant())
            {
                case "--history-chats":
                    foreach (var token in (Next() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (Guid.TryParse(token, out var id))
                            options._chatIds.Add(id);
                    }
                    break;
                case "--history-top":
                    if (int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var top))
                        options.TopChats = Math.Clamp(top, 1, 5000);
                    break;
                case "--history-scroll-steps":
                    if (int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var steps))
                        options.ScrollSteps = Math.Clamp(steps, 1, 2000);
                    break;
                case "--history-touchpad":
                    options.Touchpad = true;
                    break;
                case "--history-gestures":
                    if (int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var gestures))
                        options.Gestures = Math.Clamp(gestures, 1, 500);
                    break;
                case "--history-open-timeout":
                    if (int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout))
                        options.OpenTimeoutSeconds = Math.Clamp(timeout, 5, 1800);
                    break;
                case "--history-keep-open":
                    options.KeepOpen = true;
                    break;
                case "--history-wait-migration":
                    options.WaitForMigration = true;
                    break;
                case "--history-fidelity":
                    options.CheckFidelity = true;
                    break;
                case "--history-costs":
                    options.MeasureCosts = true;
                    break;
                case "--history-verify-migration":
                    options.VerifyMigration = true;
                    options.WaitForMigration = true;
                    break;
                case "--history-output":
                    options.OutputPath = Next();
                    break;
                case "--history-label":
                    options.Label = Next() ?? options.Label;
                    break;
            }
        }

        return options;
    }
}
