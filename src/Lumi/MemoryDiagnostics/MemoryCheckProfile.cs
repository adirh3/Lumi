using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Lumi.Models;
using Lumi.Services;
using Microsoft.Data.Sqlite;
#if DEBUG || MEMORY_CHECK
using Lumi.UiPerf;
#endif

namespace Lumi.MemoryDiagnostics;

internal static class MemoryCheckProfile
{
    internal const string ManifestFile = "memory-profile.json";

    internal static MemoryProfileManifest VerifySnapshot(string directory)
    {
        var manifest = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(directory, ManifestFile)),
            MemoryCheckJsonContext.Default.MemoryProfileManifest)
            ?? throw new InvalidDataException("The frozen profile has no manifest.");
        if (manifest.Version != 1 || manifest.MetadataSha256 != FileHash(Path.Combine(directory, "data.json"))
            || manifest.DatabaseSha256 != FileHash(Path.Combine(directory, "lumi.db"))
            || File.Exists(Path.Combine(directory, "lumi.db-wal")) && new FileInfo(Path.Combine(directory, "lumi.db-wal")).Length > 0)
            throw new InvalidDataException("The frozen profile changed or contains uncheckpointed data; it is not a valid baseline input.");
        return manifest;
    }

    internal static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static MemoryProfileManifest Seal(string directory, string kind, string corpusVersion)
    {
        var manifest = new MemoryProfileManifest(1, kind, corpusVersion,
            FileHash(Path.Combine(directory, "data.json")), FileHash(Path.Combine(directory, "lumi.db")));
        File.WriteAllText(Path.Combine(directory, ManifestFile),
            JsonSerializer.Serialize(manifest, MemoryCheckJsonContext.Default.MemoryProfileManifest));
        return manifest;
    }

    internal static void CopySnapshot(string source, string destination)
    {
        var manifest = VerifySnapshot(source);
        if (File.Exists(Path.Combine(destination, "data.json")) || File.Exists(Path.Combine(destination, "lumi.db")))
            throw new InvalidOperationException("A measured profile copy must have a fresh destination.");
        Directory.CreateDirectory(destination);
        foreach (var name in new[] { "data.json", "lumi.db", ManifestFile })
            File.Copy(Path.Combine(source, name), Path.Combine(destination, name), overwrite: false);
        if (VerifySnapshot(destination).Fingerprint != manifest.Fingerprint)
            throw new InvalidDataException("The measured profile differs from its frozen snapshot.");
    }

    internal static void Copy(string sourceDirectory, string destinationDirectory)
    {
        sourceDirectory = Path.GetFullPath(sourceDirectory);
        destinationDirectory = Path.GetFullPath(destinationDirectory);
        if (string.Equals(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar), destinationDirectory.TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("The memory check must not use the source profile as its destination.");
        if (File.Exists(Path.Combine(destinationDirectory, "data.json")) || File.Exists(Path.Combine(destinationDirectory, "lumi.db")))
            throw new InvalidOperationException("The memory check requires a fresh destination; existing profile data is never overwritten.");
        var sourceData = Path.Combine(sourceDirectory, "data.json");
        var sourceDatabase = Path.Combine(sourceDirectory, "lumi.db");
        if (!File.Exists(sourceData) || !File.Exists(sourceDatabase))
            throw new FileNotFoundException("The source must be a Lumi profile containing data.json and lumi.db. Legacy profiles must be migrated first.");
        var data = JsonSerializer.Deserialize(File.ReadAllText(sourceData), AppDataJsonContext.Default.AppData)
            ?? throw new InvalidDataException("The source profile has no app data.");
        Sanitize(data);
        Directory.CreateDirectory(destinationDirectory);
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourceDatabase,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(destinationDirectory, "lumi.db"),
            Pooling = false,
        }.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
        using var check = destination.CreateCommand();
        check.CommandText = "PRAGMA quick_check";
        if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
            throw new InvalidDataException("The copied chat database did not pass quick_check.");
        File.WriteAllText(Path.Combine(destinationDirectory, "data.json"), JsonSerializer.Serialize(data, AppDataJsonContext.Default.AppData));
    }

    internal static void Sanitize(AppData data)
    {
        var settings = data.Settings;
        settings.IsOnboarded = true;
        settings.AutoSaveChats = true;
        settings.StartMinimized = false;
        settings.LaunchAtStartup = false;
        settings.MinimizeToTray = false;
        settings.GlobalHotkey = "";
        settings.NotificationsEnabled = false;
        settings.AutoGenerateTitles = false;
        settings.SuggestFollowUps = false;
        settings.EnableMemoryAutoSave = false;
        settings.EnableMemoryAutoMaintenance = false;
        settings.EnableDailyBriefs = false;
        settings.AllowAppSetupSuggestions = false;
        settings.RemoteAccessEnabled = false;
        settings.PersonalSkillDirectories = [];
        settings.McpRegistryUrls = [];
        settings.ByokEndpoints = [];
        settings.ByokModels = [];
        settings.UseBYOKOnly = true;
        settings.Language = "en";
        settings.FollowSystemTheme = false;
        settings.IsDarkTheme = true;
        settings.IsCompactDensity = false;
        settings.UiScalePercent = 100;
        settings.WorkspacePanelOpen = false;
        settings.WindowWidth = 1000;
        settings.WindowHeight = 820;
        foreach (var job in data.BackgroundJobs)
            job.IsEnabled = false;
        foreach (var project in data.Projects)
            project.AutoSyncMainBranchDaily = false;
    }

#if DEBUG || MEMORY_CHECK
    internal static void Prepare(MemoryCheckOptions options)
    {
        var root = Environment.GetEnvironmentVariable("LUMI_APPDATA_DIR");
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("--memory-check requires a fresh isolated LUMI_APPDATA_DIR; use tools\\memory\\Test-LumiMemory.ps1.");
        var directory = Path.Combine(Path.GetFullPath(root), "Lumi");
        var before = MemoryRuntimeSample.Capture();
        if (options.SnapshotProfile is { } snapshot)
            CopySnapshot(snapshot, directory);
        else if (options.SourceProfile is { } source)
        {
            if (!File.Exists(Path.Combine(source, "data.json")))
                source = Path.Combine(source, "Lumi");
            Copy(source, directory);
            Seal(directory, "private profile copy", "copied-sqlite");
        }
        else
        {
            if (File.Exists(Path.Combine(directory, "data.json")) || File.Exists(Path.Combine(directory, "lumi.db")))
                throw new InvalidOperationException("The memory check must start with a fresh isolated profile.");
            var store = new DataStore();
            try
            {
                Sanitize(store.Data);
                var workloads = new UiWorkloadScenarios(store);
                workloads.Seed(fillerChatCount: 1987);
                PopulateManagement(store.Data);
                foreach (var chat in store.Data.Chats)
                    store.SaveChatAsync(chat).GetAwaiter().GetResult();
                store.SaveAsync().GetAwaiter().GetResult();
                store.ChatStore!.Checkpoint();
            }
            finally { store.CloseChatStorage(); }
            Seal(directory, "synthetic", "ui-workloads-sqlite-v2");
        }
        var after = MemoryRuntimeSample.Capture();
        Console.WriteLine($"[memory-check] Profile preparation allocated {(after.AllocatedBytes - before.AllocatedBytes) / 1048576:0.0} MiB; Gen2 collections {after.Gen2 - before.Gen2}; GC committed {after.CommittedBytes / 1048576:0.0} MiB. Preparation is outside the measured process.");
        Console.WriteLine($"[memory-check] Prepared profile identity: {VerifySnapshot(directory).Fingerprint}");
    }

    internal static MemoryProfileManifest ValidatePrepared(MemoryCheckOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.PreparedFingerprint))
            throw new InvalidOperationException("Prepare the profile in a separate process first; use tools\\memory\\Test-LumiMemory.ps1.");
        var root = Environment.GetEnvironmentVariable("LUMI_APPDATA_DIR");
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("A measured check requires an isolated profile root.");
        var manifest = VerifySnapshot(Path.Combine(root, "Lumi"));
        if (!manifest.Fingerprint.Equals(options.PreparedFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("The prepared profile does not match the requested snapshot identity.");
        return manifest;
    }

    private static void PopulateManagement(AppData data)
    {
        for (var index = data.Skills.Count; index < 40; index++)
            data.Skills.Add(new Skill
            {
                Name = $"Memory fixture skill {index}",
                Description = "A local, inert capability for populated gallery coverage.",
                Content = "This is synthetic memory-check data. No tool or model action is requested.",
            });
        for (var index = data.Agents.Count; index < 8; index++)
            data.Agents.Add(new LumiAgent
            {
                Name = $"Memory fixture agent {index}",
                Description = "An inert persona for gallery coverage.",
                SystemPrompt = "Synthetic memory-check fixture; never run.",
            });
        for (var index = 0; index < 96; index++)
            data.Memories.Add(new Memory
            {
                Key = $"Memory fixture fact {index}",
                Category = index % 2 == 0 ? "Work" : "Preferences",
                Content = "Synthetic factual text used to exercise a populated memories gallery.",
            });
        for (var index = 0; index < 24; index++)
            data.BackgroundJobs.Add(new BackgroundJob
            {
                Name = $"Memory fixture job {index}",
                Prompt = "Synthetic data; do not execute.",
                IsEnabled = false,
                ScheduleType = BackgroundJobScheduleTypes.Daily,
            });
        for (var index = 0; index < 4; index++)
            data.McpServers.Add(new McpServer
            {
                Name = $"Memory fixture connector {index}",
                Description = "Disabled synthetic connector.",
                IsEnabled = false,
                Command = "memory-fixture-never-run",
            });
    }

#endif
}

public sealed record MemoryProfileManifest(int Version, string Kind, string CorpusVersion,
    string MetadataSha256, string DatabaseSha256)
{
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{Version}\n{Kind}\n{CorpusVersion}\n{MetadataSha256}\n{DatabaseSha256}")));
}
