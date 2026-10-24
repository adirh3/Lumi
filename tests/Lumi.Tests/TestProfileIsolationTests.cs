using System;
using System.IO;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

/// <summary>Pins <see cref="TestProfile"/>: nothing a test does may reach the real Lumi profile.</summary>
[Trait("Category", "CoreQA")]
public sealed class TestProfileIsolationTests
{
    private static readonly string RealProfile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumi");

    [Fact]
    public void EveryLumiFolderBelongsToTheTestRun()
    {
        string[] folders =
        [
            DataStore.AppDirectory,
            DataStore.ChatsDir,
            DataStore.SkillsDir,
            DataStore.CopilotConfigDir,
            new DataStore(new AppData()).SkillsDirectory,
            InstalledAppWorkingDirectory.GetSafeWorkingDirectory(),
            Environment.GetEnvironmentVariable("LUMI_MOBILE_DATA_DIR")!,
#if DEBUG
            CopilotService.RuntimeHomeDirectory,
#endif
        ];

        Assert.All(folders, folder =>
        {
            Assert.StartsWith(TestProfile.Root, Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);
            Assert.False(Path.GetFullPath(folder).StartsWith(RealProfile, StringComparison.OrdinalIgnoreCase));
        });
        Assert.True(DataStore.UsesIsolatedProfile);
    }

    [Fact]
    public async Task AnInMemoryStoreNeverDeletesSavedChats()
    {
        Directory.CreateDirectory(DataStore.ChatsDir);
        var saved = Path.Combine(DataStore.ChatsDir, $"{Guid.NewGuid()}.json");
        File.WriteAllText(saved, "{}");
        try
        {
            var store = new DataStore(new AppData());
            var resets = 0;
            store.ChatsContentReset += () => resets++;

            await store.DeleteAllChatFilesAsync();

            Assert.True(File.Exists(saved));
            Assert.Equal(1, resets);
        }
        finally
        {
            File.Delete(saved);
        }
    }
}
