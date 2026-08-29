using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Lumi.Services.Capabilities;

/// <summary>
/// The context a capability load runs against: the working directory of the chat plus any
/// additional project context folders.
/// </summary>
public sealed class CapabilityQuery : IEquatable<CapabilityQuery>
{
    internal static readonly StringComparer DirectoryComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public CapabilityQuery(IReadOnlyList<string> workingDirectories)
    {
        ArgumentNullException.ThrowIfNull(workingDirectories);
        WorkingDirectories = workingDirectories
            .Where(static dir => !string.IsNullOrWhiteSpace(dir))
            // Not TrimEnd: that turns a filesystem root such as "C:\" into "C:", which resolves to
            // the process's current directory on that drive rather than the root itself.
            .Select(static dir => Path.TrimEndingDirectorySeparator(dir))
            .Distinct(DirectoryComparer)
            .ToArray();
        CacheKey = WorkingDirectories.Count == 0
            ? "<none>"
            : string.Join(
                '\0',
                WorkingDirectories.Select(static dir =>
                    OperatingSystem.IsWindows() ? dir.ToUpperInvariant() : dir));
    }

    public static CapabilityQuery Empty { get; } = new([]);

    public IReadOnlyList<string> WorkingDirectories { get; }

    /// <summary>Stable key used to cache the loaded capabilities for this set of directories.</summary>
    public string CacheKey { get; }

    public bool Equals(CapabilityQuery? other)
        => other is not null && string.Equals(CacheKey, other.CacheKey, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as CapabilityQuery);

    public override int GetHashCode() => CacheKey.GetHashCode(StringComparison.Ordinal);
}

/// <summary>What a provider contributed for one query.</summary>
/// <param name="Capabilities">Everything the provider could load.</param>
/// <param name="IsAvailable">
/// False when the source could not be consulted at all (for example the Copilot runtime is not
/// connected yet). An incomplete result never becomes authoritative, so the next load retries it.
/// </param>
/// <param name="SessionSkillRoots">
/// Skill roots a session must be told about explicitly because its own configuration would not
/// reach them. These come from the runtime's own discovery paths, never from Lumi scanning disk.
/// </param>
public sealed record CapabilityProviderResult(
    IReadOnlyList<CapabilityDescriptor> Capabilities,
    bool IsAvailable = true,
    IReadOnlyList<string>? SessionSkillRoots = null)
{
    public static readonly CapabilityProviderResult Empty = new([]);

    /// <summary>The source could not be consulted.</summary>
    public static readonly CapabilityProviderResult Unavailable = new([], IsAvailable: false);
}

/// <summary>
/// An asynchronous source of capabilities. Adding a new source to Lumi means implementing this once
/// and passing it to the <see cref="CapabilityCatalog"/> — no consumer has to change.
/// </summary>
/// <remarks>
/// Lumi's own store is not a provider: it is read synchronously on the consumer's thread so in-app
/// edits appear immediately, which is a different calling protocol from everything else.
/// </remarks>
public interface ICapabilityProvider
{
    /// <summary>Stable id used in diagnostics.</summary>
    string Id { get; }

    Task<CapabilityProviderResult> LoadAsync(CapabilityQuery query, CancellationToken cancellationToken);
}
