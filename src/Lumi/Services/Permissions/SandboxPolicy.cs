using GitHub.Copilot.Rpc;
using Lumi.Models;

namespace Lumi.Services.Permissions;

/// <summary>
/// Copilot's OS sandbox around the commands a project's chats run. The runtime enforces it: shell
/// commands, file search and local connectors run contained, and its own file tools check the
/// policy. Lumi builds the policy from your defaults, the project and the chat and sends it with every
/// create, resume and change, because the runtime keeps none of it across a resume. Lumi's own tools (the
/// browser, desktop control, web fetch) run inside Lumi, so no sandbox reaches them.
/// </summary>
public static class SandboxPolicy
{
    /// <summary>Host capability the internet toggle needs (<see cref="SandboxHostCapability.Name"/>).</summary>
    public const string NetworkCapability = "network";

    /// <summary>Host capability blocked folders need.</summary>
    public const string BlockedFoldersCapability = "denied_paths";

    /// <summary>
    /// Whether a project's chats run their commands in the sandbox: the project's own choice, else your
    /// default, which reaches only projects with a folder to contain their commands in. Chats outside
    /// projects run in your home folder and are never sandboxed.
    /// </summary>
    public static bool IsOnFor(UserSettings settings, Project? project)
        => project is not null
           && (project.Sandbox.Enabled ?? (settings.Sandbox.Enabled && !string.IsNullOrWhiteSpace(project.WorkingDirectory)));

    /// <summary>The chat's project sandboxes its commands and this chat didn't turn it off.</summary>
    public static bool IsActive(UserSettings settings, Project? project, bool chatTurnedOff)
        => !chatTurnedOff && IsOnFor(settings, project);

    /// <summary>Whether sandboxed commands may reach the internet: the project's choice, else your default.</summary>
    public static bool AllowsInternet(UserSettings settings, Project? project)
        => project?.Sandbox.AllowInternet ?? settings.Sandbox.AllowInternet;

    /// <summary>
    /// The policy for one chat's session, or null to leave the runtime's default: no sandbox, unless
    /// an organization's managed policy requires one.
    /// </summary>
    /// <param name="workingDirectory">Where the session runs; the sandbox gives it read/write only when it is the project's.</param>
    /// <param name="readOnlyRoots">Folders the session's skills and plugins live in, which their scripts read.</param>
    public static SandboxConfig? Build(
        UserSettings settings, Project? project, Chat chat, string workingDirectory, IEnumerable<string> readOnlyRoots,
        SandboxHostSupport? host)
    {
        if (project is null || !IsActive(settings, project, chat.SandboxDisabled))
            return null;

        var folders = Merge(settings.Sandbox.Folders.Concat(project.Sandbox.Folders).Concat(chat.SandboxFolders)
            .Concat(readOnlyRoots.Select(root => new SandboxFolder { Path = root, Access = SandboxFolderAccess.ReadOnly })));
        return new SandboxConfig
        {
            Enabled = true,
            // Without it the runtime offers no way past the sandbox; with it, every way past reaches a person.
            AllowBypass = true,
            // A chat whose project folder is missing runs in the user's profile, which the sandbox must not open.
            AddCurrentWorkingDirectory = IsWithin(workingDirectory, project.WorkingDirectory)
                                         || IsWithin(workingDirectory, chat.WorktreePath),
            // Signed in to GitHub, sandboxed git and gh reach GitHub through the runtime's credential proxy.
            Auth = new SandboxConfigAuth { Git = true, Gh = true },
            UserPolicy = new SandboxConfigUserPolicy
            {
                Filesystem = new SandboxConfigUserPolicyFilesystem
                {
                    ReadwritePaths = PathsWith(folders, SandboxFolderAccess.ReadWrite),
                    ReadonlyPaths = PathsWith(folders, SandboxFolderAccess.ReadOnly),
                    DeniedPaths = PathsWith(folders, SandboxFolderAccess.Blocked),
                },
                Network = new SandboxConfigUserPolicyNetwork
                {
                    AllowOutbound = AllowsInternet(settings, project) && Supports(host, NetworkCapability),
                    // On Windows, name resolution and the credential proxy need it: without it nothing reaches the internet.
                    AllowLocalNetwork = true,
                },
            },
        };
    }

    /// <summary>Why the policy changes, so an organization's managed policy can still require the sandbox.</summary>
    public static SandboxConfigSource SourceOf(UserSettings settings, Project? project, bool chatTurnedOff)
        => IsActive(settings, project, chatTurnedOff) ? SandboxConfigSource.UserEnabled
            : chatTurnedOff && IsOnFor(settings, project) ? SandboxConfigSource.SessionDisabled
            : SandboxConfigSource.UserDisabled;

    /// <summary>What a policy allows, as text: equal for equal policies, so a live session is updated only on a change.</summary>
    public static string Describe(SandboxConfig? config)
    {
        if (config is not { Enabled: true })
            return "";
        var filesystem = config.UserPolicy?.Filesystem;
        var network = config.UserPolicy?.Network;
        return string.Join('\n', new[]
            {
                $"cwd:{config.AddCurrentWorkingDirectory}", $"internet:{network?.AllowOutbound}", $"local:{network?.AllowLocalNetwork}",
            }
            .Concat((filesystem?.ReadwritePaths ?? []).Select(path => "rw:" + path))
            .Concat((filesystem?.ReadonlyPaths ?? []).Select(path => "ro:" + path))
            .Concat((filesystem?.DeniedPaths ?? []).Select(path => "deny:" + path)));
    }

    /// <summary>
    /// Saves an allowed escape's path into a folder list, so the grant outlives the session. Read-only
    /// entries the grant replaced go, as the runtime dropped them from its own policy.
    /// </summary>
    public static void Remember(List<SandboxFolder> folders, SandboxPathGrant grant)
    {
        var path = Normalize(grant.Path);
        var access = grant.CanWrite ? SandboxFolderAccess.ReadWrite : SandboxFolderAccess.ReadOnly;
        folders.RemoveAll(folder => folder.Access == SandboxFolderAccess.ReadOnly
                                    && !SamePath(folder.Path, path)
                                    && grant.RemovedReadOnlyPaths.Any(removed => SamePath(folder.Path, removed)));
        if (folders.Find(folder => SamePath(folder.Path, path)) is not { } existing)
            folders.Add(new SandboxFolder { Path = path, Access = access });
        else if (existing.Access == SandboxFolderAccess.ReadOnly)
            existing.Access = access;
    }

    /// <summary>Whether this computer can run a sandbox feature; unknown counts as yes, so nothing is hidden before the runtime answers.</summary>
    public static bool Supports(SandboxHostSupport? host, string capability)
        => host is null || (host.Supported && host.Capabilities?.FirstOrDefault(c => c.Name == capability) is not { Supported: false });

    public static bool SamePath(string? first, string? second)
        => first is { Length: > 0 } && second is { Length: > 0 }
           && string.Equals(Normalize(first), Normalize(second), PathComparison);

    /// <summary>Each folder once, with the strongest rule that names it: blocked, then read/write, then read-only.</summary>
    private static List<SandboxFolder> Merge(IEnumerable<SandboxFolder> folders)
    {
        var merged = new List<SandboxFolder>();
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder.Path))
                continue;
            var path = Normalize(folder.Path);
            if (merged.Find(existing => string.Equals(existing.Path, path, PathComparison)) is not { } existing)
                merged.Add(new SandboxFolder { Path = path, Access = folder.Access });
            else if (Strength(folder.Access) > Strength(existing.Access))
                existing.Access = folder.Access;
        }
        return merged;
    }

    private static int Strength(SandboxFolderAccess access) => access switch
    {
        SandboxFolderAccess.Blocked => 2,
        SandboxFolderAccess.ReadWrite => 1,
        _ => 0
    };

    private static List<string> PathsWith(List<SandboxFolder> folders, SandboxFolderAccess access)
        => folders.Where(folder => folder.Access == access).Select(folder => folder.Path).ToList();

    private static bool IsWithin(string path, string? folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder))
            return false;
        var child = Normalize(path);
        var parent = Normalize(folder);
        return string.Equals(child, parent, PathComparison)
               || child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, PathComparison);
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static string Normalize(string path)
    {
        var trimmed = path.Trim();
        try
        {
            trimmed = Path.GetFullPath(trimmed);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }
        return Path.TrimEndingDirectorySeparator(trimmed);
    }
}
