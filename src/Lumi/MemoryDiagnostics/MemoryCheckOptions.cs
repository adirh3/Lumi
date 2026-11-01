using System.Globalization;

namespace Lumi.MemoryDiagnostics;

/// <summary>The repeatable desktop budget check, separate from forced-GC stress measurements.</summary>
public sealed record MemoryCheckOptions
{
    public bool Enabled { get; init; }
    public bool Fast { get; init; }
    public bool CiGuardrails { get; init; }
    public const long CiRamBytes = 1024 * 1024L * 1024;
    public const long CiPrivateBytes = 2048 * 1024L * 1024;
    public const long CiManagedBytes = 200 * 1024L * 1024;
    public int Chats { get; init; } = 12;
    public int Rounds { get; init; } = 3;
    public int SampleSeconds { get; init; } = 10;
    public long MaxRamBytes { get; init; } = 500 * 1024L * 1024;
    public long MaxPrivateBytes { get; init; } = 1024 * 1024L * 1024;
    public long MaxManagedBytes { get; init; } = 200 * 1024L * 1024;
    public string? SourceProfile { get; init; }
    public string? SnapshotProfile { get; init; }
    public bool PrepareOnly { get; init; }
    public string? PreparedFingerprint { get; init; }
    public string? EvaluationFile { get; init; }
    public int InjectRetainedMiB { get; init; }
    public string? OutputDirectory { get; init; }

    public static MemoryCheckOptions Parse(IReadOnlyList<string> args)
    {
        var options = new MemoryCheckOptions
        {
            Enabled = args.Any(arg => arg.Equals("--memory-check", StringComparison.OrdinalIgnoreCase)),
        };
        for (var index = 0; index < args.Count; index++)
        {
            var flag = args[index].ToLowerInvariant();
            if (!flag.StartsWith("--memory-check-", StringComparison.Ordinal))
                continue;
            if (!options.Enabled)
                throw new ArgumentException($"{args[index]} requires --memory-check.");
            if (++index == args.Count || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{flag} requires a value.");
            var value = args[index];
            options = flag switch
            {
                "--memory-check-chats" => options with { Chats = Number(2, 100) },
                "--memory-check-fast" => options with { Fast = bool.TryParse(value, out var fast) ? fast : throw new ArgumentException($"{flag} requires true or false.") },
                "--memory-check-ci" => options with { CiGuardrails = bool.TryParse(value, out var ci) ? ci : throw new ArgumentException($"{flag} requires true or false.") },
                "--memory-check-rounds" => options with { Rounds = Number(3, 20) },
                "--memory-check-sample-seconds" => options with { SampleSeconds = Number(5, 120) },
                "--memory-check-max-ram-mib" => options with { MaxRamBytes = Number(1, 16384) * 1024L * 1024 },
                "--memory-check-max-private-mib" => options with { MaxPrivateBytes = Number(1, 32768) * 1024L * 1024 },
                "--memory-check-max-managed-mib" => options with { MaxManagedBytes = Number(1, 8192) * 1024L * 1024 },
                "--memory-check-profile" => options with { SourceProfile = Path.GetFullPath(value) },
                "--memory-check-snapshot" => options with { SnapshotProfile = Path.GetFullPath(value) },
                "--memory-check-prepare-only" => options with { PrepareOnly = bool.TryParse(value, out var prepare) ? prepare : throw new ArgumentException($"{flag} requires true or false.") },
                "--memory-check-prepared" => options with { PreparedFingerprint = value },
                "--memory-check-evaluate" => options with { EvaluationFile = Path.GetFullPath(value) },
                "--memory-check-inject-retained-mib" => options with { InjectRetainedMiB = Number(0, 1024) },
                "--memory-check-output" => options with { OutputDirectory = Path.GetFullPath(value) },
                _ => throw new ArgumentException($"Unknown memory check option: {flag}."),
            };

            int Number(int minimum, int maximum)
                => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                   && number >= minimum && number <= maximum
                    ? number
                    : throw new ArgumentException($"{flag} requires an integer between {minimum} and {maximum}.");
        }
        if (options.SourceProfile is not null && options.SnapshotProfile is not null)
            throw new ArgumentException("Use a live source profile or a frozen snapshot, not both.");
        if (options.CiGuardrails)
        {
            if (!options.Fast)
                throw new ArgumentException("CI guardrails are a Fast synthetic screen, not a Deep baseline comparison.");
            if (Specified("--memory-check-max-ram-mib") && options.MaxRamBytes != CiRamBytes
                || Specified("--memory-check-max-private-mib") && options.MaxPrivateBytes != CiPrivateBytes
                || Specified("--memory-check-max-managed-mib") && options.MaxManagedBytes != CiManagedBytes)
                throw new ArgumentException("CI policy uses declared fixed large-regression limits: RAM 1024 MiB, commitment 2048 MiB, managed 200 MiB.");
            options = options with { MaxRamBytes = CiRamBytes, MaxPrivateBytes = CiPrivateBytes, MaxManagedBytes = CiManagedBytes };
        }
        if (options.PreparedFingerprint is { } fingerprint
            && (fingerprint.Length != 64 || fingerprint.Any(character => !Uri.IsHexDigit(character)) || options.PrepareOnly))
            throw new ArgumentException("A measured check requires a valid SHA-256 prepared-profile identity, separate from preparation.");
        return options;

        bool Specified(string flag) => args.Any(argument => argument.Equals(flag, StringComparison.OrdinalIgnoreCase));
    }
}
