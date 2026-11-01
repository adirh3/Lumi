using System.Text.Json;
using Lumi.MemoryDiagnostics;
using Lumi.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lumi.Tests;

public sealed class MemoryCheckProfileTests
{
    [Fact]
    public void CopyReadsLiveWalDataWithoutChangingTheSourceAndDisablesAutomationOnlyInTheCopy()
    {
        var root = Path.Combine(TestProfile.Root, "memory-check-copy", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoGenerateTitles = true,
                SuggestFollowUps = true,
                EnableMemoryAutoSave = true,
                EnableDailyBriefs = true,
                LaunchAtStartup = true,
                RemoteAccessEnabled = true,
            },
        };
        data.Projects.Add(new Project { Name = "My project", Instructions = "Keep canonical instructions.", AutoSyncMainBranchDaily = true });
        data.BackgroundJobs.Add(new BackgroundJob { Name = "My job", IsEnabled = true });
        var metadata = JsonSerializer.Serialize(data, AppDataJsonContext.Default.AppData);
        File.WriteAllText(Path.Combine(source, "data.json"), metadata);
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(source, "lumi.db"),
            Pooling = false,
        }.ToString());
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE evidence(value TEXT); INSERT INTO evidence VALUES ('uncheckpointed text');";
        command.ExecuteNonQuery();
        Assert.True(File.Exists(Path.Combine(source, "lumi.db-wal")));

        MemoryCheckProfile.Copy(source, destination);

        Assert.Equal(metadata, File.ReadAllText(Path.Combine(source, "data.json")));
        using var copiedDatabase = new SqliteConnection($"Data Source={Path.Combine(destination, "lumi.db")};Pooling=False");
        copiedDatabase.Open();
        using var copiedCommand = copiedDatabase.CreateCommand();
        copiedCommand.CommandText = "SELECT value FROM evidence";
        Assert.Equal("uncheckpointed text", copiedCommand.ExecuteScalar());
        var copied = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(destination, "data.json")), AppDataJsonContext.Default.AppData)!;
        Assert.False(copied.Settings.AutoGenerateTitles);
        Assert.False(copied.Settings.SuggestFollowUps);
        Assert.False(copied.Settings.EnableMemoryAutoSave);
        Assert.False(copied.Settings.EnableDailyBriefs);
        Assert.False(copied.Settings.LaunchAtStartup);
        Assert.False(copied.Settings.RemoteAccessEnabled);
        Assert.False(copied.BackgroundJobs.Single().IsEnabled);
        Assert.False(copied.Projects.Single().AutoSyncMainBranchDaily);
        Assert.Equal("Keep canonical instructions.", copied.Projects.Single().Instructions);
        Assert.True(data.Settings.EnableMemoryAutoSave);
        Assert.True(data.BackgroundJobs.Single().IsEnabled);
    }

    [Fact]
    public void CopyRefusesToOverwriteAProfile()
    {
        var directory = Path.Combine(TestProfile.Root, "memory-check-overwrite", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dataPath = Path.Combine(directory, "data.json");
        File.WriteAllText(dataPath, "existing profile");
        Assert.Throws<InvalidOperationException>(() => MemoryCheckProfile.Copy(Path.Combine(directory, "source"), directory));
        Assert.Equal("existing profile", File.ReadAllText(dataPath));
    }

    [Fact]
    public void CopyRejectsTheSameSourceAndDestination()
    {
        var directory = Path.Combine(TestProfile.Root, "memory-check-same");
        Assert.Throws<ArgumentException>(() => MemoryCheckProfile.Copy(directory, directory));
    }

    [Fact]
    public void FrozenSnapshotCopiesIdenticalBytesAndRejectsMutation()
    {
        var root = Path.Combine(TestProfile.Root, "frozen-memory", Guid.NewGuid().ToString("N"));
        var snapshot = Path.Combine(root, "snapshot");
        Directory.CreateDirectory(snapshot);
        File.WriteAllText(Path.Combine(snapshot, "data.json"), "fixed metadata");
        File.WriteAllText(Path.Combine(snapshot, "lumi.db"), "fixed database bytes");
        var identity = MemoryCheckProfile.Seal(snapshot, "synthetic", "fixture-test");
        var copy = Path.Combine(root, "copy");
        MemoryCheckProfile.CopySnapshot(snapshot, copy);
        Assert.Equal(identity, MemoryCheckProfile.VerifySnapshot(copy));
        File.AppendAllText(Path.Combine(snapshot, "data.json"), "changed");
        Assert.Throws<InvalidDataException>(() => MemoryCheckProfile.CopySnapshot(snapshot, Path.Combine(root, "bad")));
        Assert.False(Directory.Exists(Path.Combine(root, "bad")));
    }
}
