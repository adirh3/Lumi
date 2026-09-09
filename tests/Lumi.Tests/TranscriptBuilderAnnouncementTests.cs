using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Microsoft.Extensions.AI;
using Xunit;
using ChatMessage = Lumi.Models.ChatMessage;

namespace Lumi.Tests;

public sealed class TranscriptBuilderAnnouncementTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"lumi-announcements-{Guid.NewGuid():N}");
    private readonly string _file;

    public TranscriptBuilderAnnouncementTests()
    {
        Directory.CreateDirectory(_directory);
        _file = Path.Combine(_directory, "deliverable.cs");
        File.WriteAllText(_file, "original");
    }

    [Fact]
    public void ExplicitAnnouncements_DeduplicateNormalizedPathsWithinUserTurn()
    {
        var builder = CreateBuilder();
        var turns = builder.Rebuild([
            User(), Announce(_file), Announce(Path.Combine(_directory, ".", "deliverable.cs")), Assistant(),
            Edit(_file), Announce(_file), Assistant(),
        ]);

        var chip = Assert.Single(Chips(turns));
        Assert.Equal(_file, chip.FilePath);
        Assert.True(chip.IsPreviewable);
        Assert.False(chip.IsEdited);
    }

    [Fact]
    public void ExplicitAnnouncement_CanShowSameFileInSubsequentUserTurn()
    {
        var builder = CreateBuilder();
        var turns = builder.Rebuild([
            User(), Announce(_file), Assistant(),
            User(), Announce(_file), Assistant(),
        ]);

        Assert.Equal(2, Chips(turns).Count);
        Assert.All(Chips(turns), chip => Assert.False(chip.IsEdited));
    }

    [Fact]
    public void SubsequentEdits_ShowOneEditedChipPerUserTurn()
    {
        var builder = CreateBuilder();
        var turns = builder.Rebuild([
            User(), Announce(_file), Assistant(),
            User(), Edit(_file), Edit(_file), WorkspaceChange(_file), Assistant(),
            User(), Edit(_file), Assistant(),
        ]);

        var chips = Chips(turns);
        Assert.Equal(3, chips.Count);
        Assert.False(chips[0].IsEdited);
        Assert.True(chips[1].IsEdited);
        Assert.True(chips[2].IsEdited);
        Assert.All(chips, chip => Assert.True(chip.IsPreviewable));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LiveEdit_WaitsForSuccessEvenWhenArgumentsArriveLate(bool showTools)
    {
        var builder = CreateBuilder(showTools);
        var turns = builder.Rebuild([User(), Announce(_file), Assistant()]);
        builder.ProcessMessageToTranscript(User());
        var edit = Tool("edit", "", "InProgress");
        builder.ProcessMessageToTranscript(edit);
        builder.ProcessMessageToTranscript(Assistant());
        builder.AppendModelLabel("gpt-5.5");
        Assert.Single(Chips(turns));

        edit.Message.Content = Edit(_file).Content;
        edit.NotifyContentChanged();
        Assert.Single(Chips(turns));
        edit.Message.ToolStatus = "Completed";
        edit.NotifyToolStatusChanged();

        var chip = Assert.Single(Chips(turns).Where(c => c.IsEdited));
        Assert.True(chip.IsPreviewable);
        Assert.Empty(builder.PendingToolFileChips);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitAnnouncement_SupersedesAutomaticChipWithoutDuplication(bool alreadyMounted)
    {
        var builder = CreateBuilder();
        var turns = builder.Rebuild([User(), Announce(_file), Assistant()]);
        builder.ProcessMessageToTranscript(User());
        builder.ProcessMessageToTranscript(Edit(_file));
        var automatic = Assert.Single(builder.PendingToolFileChips);
        Assert.True(automatic.IsEdited);
        if (alreadyMounted)
            builder.ProcessMessageToTranscript(Assistant());
        var changed = new List<string?>();
        automatic.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        builder.ProcessMessageToTranscript(Announce(_file));
        builder.ProcessMessageToTranscript(Edit(_file));
        builder.ProcessMessageToTranscript(Assistant());

        Assert.Equal(2, Chips(turns).Count);
        Assert.Same(automatic, Chips(turns)[1]);
        Assert.False(automatic.IsEdited);
        Assert.True(automatic.IsPreviewable);
        Assert.Contains(nameof(FileAttachmentItem.IsEdited), changed);
    }

    [Fact]
    public void ExplicitAnnouncementBeforeEdits_SuppressesEditedIndicatorForThatTurn()
    {
        var turns = CreateBuilder().Rebuild([
            User(), Announce(_file), Assistant(),
            User(), Announce(_file), Edit(_file), WorkspaceChange(_file), Assistant(),
        ]);

        Assert.Equal(2, Chips(turns).Count);
        Assert.All(Chips(turns), chip => Assert.False(chip.IsEdited));
    }

    [Theory]
    [InlineData("Failed", true)]
    [InlineData("Failed", false)]
    [InlineData("Stopped", true)]
    public void FailedOrStoppedEdits_DoNotShowEditedChip(string status, bool showTools)
    {
        var builder = CreateBuilder(showTools);
        var turns = builder.Rebuild([User(), Announce(_file), Assistant()]);
        builder.ProcessMessageToTranscript(User());
        var edit = Edit(_file, "InProgress");
        builder.ProcessMessageToTranscript(edit);
        edit.Message.ToolStatus = status;
        edit.NotifyToolStatusChanged();
        builder.ProcessMessageToTranscript(WorkspaceChange(_file, status));
        builder.ProcessMessageToTranscript(Assistant());
        builder.FlushPendingFileEdits();

        Assert.Single(Chips(turns));
        Assert.Empty(builder.PendingToolFileChips);
    }

    [Fact]
    public void UnannouncedAndFailedAnnouncementEdits_DoNotCreateChips()
    {
        var turns = CreateBuilder().Rebuild([
            User(), Announce(_file, "Failed"), Assistant(),
            User(), Edit(_file), WorkspaceChange(_file), Assistant(),
        ]);
        Assert.Empty(Chips(turns));
    }

    [Fact]
    public void FailedExplicitAnnouncement_DoesNotSupersedeSuccessfulAutomaticEdit()
    {
        var turns = CreateBuilder().Rebuild([
            User(), Announce(_file), Assistant(),
            User(), Edit(_file), Announce(_file, "Failed"), Assistant(),
        ]);
        Assert.True(Chips(turns)[1].IsEdited);
        Assert.Equal(2, Chips(turns).Count);
    }

    [Fact]
    public void Rebuild_RetainsMissingHistoricalAnnouncementForLaterRecreation()
    {
        var builder = CreateBuilder();
        var history = new[] { User(), Announce(_file), Assistant(), User(), Assistant() };
        File.Delete(_file);
        var turns = builder.Rebuild(history);
        Assert.Empty(Chips(turns));

        File.WriteAllText(_file, "restored");
        builder.ProcessMessageToTranscript(WorkspaceChange(_file));

        Assert.True(Assert.Single(Chips(turns)).IsEdited);
    }

    [Fact]
    public void Rebuild_DoesNotCarryAnnouncedPathsAcrossChats()
    {
        var builder = CreateBuilder();
        builder.Rebuild([User(), Announce(_file), Assistant()]);
        var otherChat = builder.Rebuild([User(), Edit(_file), Assistant()]);
        Assert.Empty(Chips(otherChat));
    }

    [Fact]
    public void Rebuild_RestoresAnnouncementsOlderThanMountedPagesAndRelativeEditPaths()
    {
        var builder = CreateBuilder(resolveFilePath: path => Path.GetFullPath(path, _directory));
        var history = new List<ChatMessageViewModel> { User(), Announce(_file), Assistant() };
        for (var i = 0; i < 40; i++)
        {
            history.Add(User());
            history.Add(Assistant());
        }
        history.Add(User());
        history.Add(Edit("./deliverable.cs"));
        history.Add(Assistant());

        var turns = builder.Rebuild(history);
        var window = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxTurnsPerPage = 2,
            MaxPageWeight = 8,
            MinInitialPages = 1,
            MaxMountedPages = 2,
        });
        window.BindTranscript(turns, "announcement-replay");
        window.ResetToLatest(200, "announcement-replay");

        Assert.DoesNotContain(turns[0], window.MountedTurns);
        var edited = Assert.Single(Chips(window.MountedTurns));
        Assert.True(edited.IsEdited);
        Assert.Equal(_file, edited.FilePath);

        builder.ProcessMessageToTranscript(Edit(_file));
        builder.ProcessMessageToTranscript(WorkspaceChange(_file));
        Assert.Equal(2, Chips(turns).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateWorkspaceEvent_AttachesImmediatelyToCompletedAssistant(bool reload)
    {
        var builder = CreateBuilder();
        var history = new[] { User(), Announce(_file), Assistant(), User(), Assistant() };
        ObservableCollection<TranscriptTurn> turns;
        if (reload)
            turns = builder.Rebuild(history);
        else
        {
            turns = [];
            builder.SetLiveTarget(turns);
            foreach (var message in history)
                builder.ProcessMessageToTranscript(message);
            builder.AppendModelLabel("gpt-5.5");
        }
        var lastAssistant = turns.SelectMany(t => t.Items).OfType<AssistantMessageItem>().Last();

        builder.ProcessMessageToTranscript(WorkspaceChange(_file));
        builder.ProcessMessageToTranscript(WorkspaceChange(_file));

        var chip = Assert.Single(lastAssistant.FileAttachments);
        Assert.True(chip.IsEdited);
        Assert.Empty(builder.PendingToolFileChips);
        Assert.Equal(2, Chips(turns).Count);
    }

    [Fact]
    public void Rebuild_InProgressAnnouncementStillObservesCompletion()
    {
        var builder = CreateBuilder();
        var announcement = Announce(_file, "InProgress");
        var turns = builder.Rebuild([User(), announcement, Assistant()]);
        Assert.Empty(Chips(turns));

        announcement.Message.ToolStatus = "Completed";
        announcement.NotifyToolStatusChanged();

        Assert.True(Assert.Single(Chips(turns)).IsPreviewable);
    }

    [Fact]
    public void ToolOnlyTurn_FlushesVisibleAttachmentWithoutLeakingIntoNextTurn()
    {
        var builder = CreateBuilder();
        var turns = builder.Rebuild([User(), Announce(_file), User(), Assistant()]);
        var assistants = turns.SelectMany(t => t.Items).OfType<AssistantMessageItem>().ToArray();

        Assert.Equal(2, assistants.Length);
        Assert.True(assistants[0].IsItemVisible);
        Assert.Single(assistants[0].FileAttachments);
        Assert.Empty(assistants[1].FileAttachments);
        Assert.Empty(builder.PendingToolFileChips);
    }

    [Fact]
    public void LatePreviousTurnCompletion_DoesNotAttachToNewUserOrAssistant()
    {
        var builder = CreateBuilder();
        var turns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(turns);
        builder.ProcessMessageToTranscript(User());
        var announcement = Announce(_file, "InProgress");
        builder.ProcessMessageToTranscript(announcement);
        builder.ProcessMessageToTranscript(User());
        builder.ProcessMessageToTranscript(Assistant());

        announcement.Message.ToolStatus = "Completed";
        announcement.NotifyToolStatusChanged();

        Assert.Single(Assert.IsType<AssistantMessageItem>(Assert.Single(turns[1].Items)).FileAttachments);
        Assert.IsType<UserMessageItem>(Assert.Single(turns[2].Items));
        Assert.Empty(turns[^1].Items.OfType<AssistantMessageItem>().Single().FileAttachments);
    }

    [Fact]
    public void NewFileDiscovery_PreservesFilteringAndExplicitlyUpgradesSameTurnChip()
    {
        var file = Path.Combine(_directory, "deliverable.pdf");
        File.WriteAllText(file, "document");
        var script = Path.Combine(_directory, "intermediate.py");
        File.WriteAllText(script, "print('helper')");
        var builder = CreateBuilder();
        var turns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(turns);
        builder.ProcessMessageToTranscript(User());
        builder.AddDetectedFileChip(script);
        Assert.Empty(builder.PendingToolFileChips);
        builder.AddDetectedFileChip(file);
        builder.AddDetectedFileChip(Path.Combine(_directory, ".", "deliverable.pdf"));
        var chip = Assert.Single(builder.PendingToolFileChips);
        Assert.False(chip.IsPreviewable);
        builder.ProcessMessageToTranscript(Assistant());
        builder.ProcessMessageToTranscript(Announce(file));
        builder.ProcessMessageToTranscript(User());
        builder.AddDetectedFileChip(file);
        builder.ProcessMessageToTranscript(Assistant());

        Assert.Same(chip, Assert.Single(Chips(turns)));
        Assert.True(chip.IsPreviewable);
        Assert.False(chip.IsEdited);
        Assert.False(new FileAttachmentItem(_file).IsPreviewable);
        Assert.False(new FileAttachmentItem(_file).IsEdited);
    }

    [Fact]
    public void ExplicitAnnouncement_AllowsCodeExcludedByAutomaticDiscovery()
    {
        var script = Path.Combine(_directory, "deliverable.py");
        File.WriteAllText(script, "print('deliverable')");
        var turns = CreateBuilder().Rebuild([User(), Announce(script), Assistant()]);

        Assert.True(Assert.Single(Chips(turns)).IsPreviewable);
        Assert.Equal(script, ChatViewModel.ValidateAnnouncedFilePath(script));
    }

    [Fact]
    public void PathComparison_MatchesHostFilesystemConvention()
    {
        var alternate = Path.Combine(_directory, "DELIVERABLE.cs");
        if (!OperatingSystem.IsWindows())
            File.WriteAllText(alternate, "different file");
        var turns = CreateBuilder().Rebuild([
            User(), Announce(_file), Assistant(), User(), Edit(alternate), Assistant(),
        ]);

        Assert.Equal(OperatingSystem.IsWindows() ? 2 : 1, Chips(turns).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative.txt")]
    public void AnnounceValidation_RejectsNonAbsolutePaths(string path)
        => Assert.Throws<ArgumentException>(() => ChatViewModel.ValidateAnnouncedFilePath(path));

    [Fact]
    public void AnnounceValidation_RejectsMissingFilesAndDirectories()
    {
        Assert.Throws<FileNotFoundException>(() => ChatViewModel.ValidateAnnouncedFilePath(Path.Combine(_directory, "missing")));
        Assert.Throws<FileNotFoundException>(() => ChatViewModel.ValidateAnnouncedFilePath(_directory));
    }

    [Fact]
    public void AnnounceValidation_AcceptsEmptyCodeAndNormalizesPath()
    {
        File.WriteAllText(_file, "");
        Assert.Equal(_file, ChatViewModel.ValidateAnnouncedFilePath(Path.Combine(_directory, ".", "deliverable.cs")));
    }

    [Fact]
    public void AnnounceValidation_RejectsUnreadableFile()
    {
        if (!OperatingSystem.IsWindows())
            return; // FileShare.None is enforced by the Windows filesystem.
        using var locked = new FileStream(_file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.ThrowsAny<IOException>(() => ChatViewModel.ValidateAnnouncedFilePath(_file));
    }

    [Fact]
    public async Task AnnounceTool_DefaultPreviewIsOptionalAndDoesNotNeedUiState()
    {
        // The false/default path must only validate the file; it must not mutate a builder
        // or dispatch UI work before the persisted successful tool message arrives.
        var vm = (ChatViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ChatViewModel));
        var tool = (AIFunction)typeof(ChatViewModel)
            .GetMethod("BuildAnnounceFileTool", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [Guid.NewGuid()])!;

        var schema = tool.JsonSchema;
        Assert.False(schema.GetProperty("properties").GetProperty("preview").GetProperty("default").GetBoolean());
        Assert.DoesNotContain(schema.GetProperty("required").EnumerateArray(), element => element.GetString() == "preview");
        var result = await tool.InvokeAsync(new AIFunctionArguments { ["filePath"] = _file });
        Assert.Contains(_file, result?.ToString());
        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await tool.InvokeAsync(new AIFunctionArguments { ["filePath"] = Path.Combine(_directory, "missing") }));
    }

    private static TranscriptBuilder CreateBuilder(bool showTools = true, Func<string, string>? resolveFilePath = null)
    {
        var store = (DataStore)RuntimeHelpers.GetUninitializedObject(typeof(DataStore));
        var data = new AppData();
        data.Settings.ShowToolCalls = showTools;
        typeof(DataStore).GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(store, data);
        return new TranscriptBuilder(
            store, _ => { }, (_, _) => { }, _ => { }, (_, _) => Task.CompletedTask,
            () => "gpt-5.5", resolveFilePath: resolveFilePath);
    }

    private static List<FileAttachmentItem> Chips(IEnumerable<TranscriptTurn> turns)
        => turns.SelectMany(t => t.Items).OfType<AssistantMessageItem>().SelectMany(a => a.FileAttachments).ToList();

    private static ChatMessageViewModel User()
        => new(new ChatMessage { Role = "user", Content = "Please update the deliverable." });

    private static ChatMessageViewModel Assistant()
        => new(new ChatMessage { Role = "assistant", Author = "Lumi", Content = "Done." });

    private static ChatMessageViewModel Announce(string path, string status = "Completed")
        => Tool("announce_file", JsonSerializer.Serialize(new { filePath = path }), status);

    private static ChatMessageViewModel Edit(string path, string status = "Completed")
        => Tool("edit", JsonSerializer.Serialize(new { filePath = path, oldString = "original", newString = "edited" }), status);

    private static ChatMessageViewModel WorkspaceChange(string path, string status = "Completed")
        => Tool(ToolDisplayHelper.WorkspaceFileChangedToolName,
            JsonSerializer.Serialize(new { filePath = path, operation = "Modify" }), status);

    private static ChatMessageViewModel Tool(string name, string content, string status)
        => new(new ChatMessage
        {
            Role = "tool", ToolName = name, ToolCallId = Guid.NewGuid().ToString(),
            ToolStatus = status, Content = content,
        });

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
