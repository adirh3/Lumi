using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class BackgroundJobsViewModelTests
{
    [Fact]
    public void RefreshFromStore_SelectsFirstJob_WhenJobsExistAndNothingIsSelected()
    {
        var chat = CreateChat("Daily planning");
        var job = CreateJob(chat.Id, "Daily plan");
        var data = new AppData { Chats = [chat], BackgroundJobs = [job] };
        using var harness = CreateHarness(data);

        Assert.Same(job, harness.ViewModel.SelectedJob);
        Assert.True(harness.ViewModel.IsEditing);
        Assert.Equal("Daily plan", harness.ViewModel.EditName);
    }

    [Fact]
    public void RefreshFromStore_SelectsPreferredChatJob_WhenOpeningJobsFromChat()
    {
        var otherChat = CreateChat("Other chat");
        var preferredChat = CreateChat("Hotel search");
        var otherJob = CreateJob(otherChat.Id, "A unrelated job");
        var preferredJob = CreateJob(preferredChat.Id, "Z hotel monitor");
        var data = new AppData
        {
            Chats = [otherChat, preferredChat],
            BackgroundJobs = [otherJob, preferredJob]
        };
        using var harness = CreateHarness(data);

        Assert.Same(otherJob, harness.ViewModel.SelectedJob);

        harness.ViewModel.SetPreferredChat(preferredChat);
        harness.ViewModel.RefreshFromStore();

        Assert.Same(preferredJob, harness.ViewModel.SelectedJob);
        Assert.Equal("Z hotel monitor", harness.ViewModel.EditName);
    }

    [Fact]
    public void DeleteSelectedJob_SelectsRemainingJob()
    {
        var chat = CreateChat("Monitoring");
        var firstJob = CreateJob(chat.Id, "A first");
        var secondJob = CreateJob(chat.Id, "B second");
        var data = new AppData { Chats = [chat], BackgroundJobs = [firstJob, secondJob] };
        using var harness = CreateHarness(data);

        harness.ViewModel.DeleteJobCommand.Execute(firstJob);

        Assert.Same(secondJob, harness.ViewModel.SelectedJob);
        Assert.True(harness.ViewModel.IsEditing);
    }

    [Fact]
    public void ActivationAndLifecycleFilters_CanBeCombined()
    {
        var chat = CreateChat("Monitoring");
        var runningJob = CreateJob(chat.Id, "Running monitor");
        runningJob.IsEnabled = true;
        runningJob.IsRunning = true;
        runningJob.LastRunStatus = BackgroundJobRunStatuses.Running;

        var completedJob = CreateJob(chat.Id, "Completed monitor");
        completedJob.LastRunStatus = BackgroundJobRunStatuses.Completed;
        completedJob.RunCount = 1;

        var failedJob = CreateJob(chat.Id, "Failed monitor");
        failedJob.IsEnabled = true;
        failedJob.LastRunStatus = BackgroundJobRunStatuses.Failed;
        failedJob.RunCount = 1;

        var data = new AppData
        {
            Chats = [chat],
            BackgroundJobs = [runningJob, completedJob, failedJob]
        };
        using var harness = CreateHarness(data);

        harness.ViewModel.ActivationFilterIndex = 1;
        harness.ViewModel.LifecycleFilterIndex = 3;

        Assert.Collection(
            harness.ViewModel.Jobs,
            job => Assert.Same(failedJob, job));
        Assert.Equal(3, harness.ViewModel.TotalJobCount);
        Assert.True(harness.ViewModel.HasActiveFilters);
    }

    [Fact]
    public void SearchQuery_DoesNotMatchUnrelatedLifecycleLabels()
    {
        var chat = CreateChat("Monitoring");
        var dailyJob = CreateJob(chat.Id, "Daily planning");
        var failedJob = CreateJob(chat.Id, "TV monitor");
        failedJob.LastRunStatus = BackgroundJobRunStatuses.Failed;
        failedJob.RunCount = 1;
        var data = new AppData { Chats = [chat], BackgroundJobs = [dailyJob, failedJob] };
        using var harness = CreateHarness(data);

        harness.ViewModel.SearchQuery = "Daily";

        Assert.Collection(
            harness.ViewModel.Jobs,
            job => Assert.Same(dailyJob, job));
    }

    [Fact]
    public void FinishedLifecycleFilter_IncludesCompletedAndSkipped()
    {
        var chat = CreateChat("Monitoring");
        var completedJob = CreateJob(chat.Id, "Completed");
        completedJob.LastRunStatus = BackgroundJobRunStatuses.Completed;
        completedJob.RunCount = 1;
        var skippedJob = CreateJob(chat.Id, "Skipped");
        skippedJob.LastRunStatus = BackgroundJobRunStatuses.Skipped;
        skippedJob.RunCount = 1;
        var failedJob = CreateJob(chat.Id, "Failed");
        failedJob.LastRunStatus = BackgroundJobRunStatuses.Failed;
        failedJob.RunCount = 1;
        var data = new AppData { Chats = [chat], BackgroundJobs = [completedJob, skippedJob, failedJob] };
        using var harness = CreateHarness(data);

        harness.ViewModel.LifecycleFilterIndex = 2;

        Assert.Equal([completedJob, skippedJob], harness.ViewModel.Jobs.OrderBy(job => job.Name));
        Assert.True(completedJob.IsLifecycleCompleted);
        Assert.True(skippedJob.IsLifecycleSkipped);
        Assert.False(skippedJob.IsLifecycleCompleted);
    }

    [Fact]
    public void NotRunLifecycleFilter_ExcludesIdleJobsWithHistory()
    {
        var chat = CreateChat("Monitoring");
        var newJob = CreateJob(chat.Id, "New");
        var idleJob = CreateJob(chat.Id, "Previously run");
        idleJob.RunCount = 1;
        var data = new AppData { Chats = [chat], BackgroundJobs = [newJob, idleJob] };
        using var harness = CreateHarness(data);

        harness.ViewModel.LifecycleFilterIndex = 4;

        Assert.Collection(
            harness.ViewModel.Jobs,
            job => Assert.Same(newJob, job));
    }

    [Theory]
    [InlineData(BackgroundJobRunStatuses.Running)]
    [InlineData(BackgroundJobRunStatuses.Watching)]
    [InlineData(BackgroundJobRunStatuses.Waiting)]
    public void PersistedActiveLifecycle_IsPresentedAsInterrupted(string status)
    {
        var chat = CreateChat("Monitoring");
        var job = CreateJob(chat.Id, "Interrupted");
        job.LastRunStatus = status;
        job.RunCount = 1;
        var data = new AppData { Chats = [chat], BackgroundJobs = [job] };
        using var harness = CreateHarness(data);

        Assert.False(job.IsLifecycleInProgress);
        Assert.True(job.IsLifecycleInterrupted);
        Assert.True(job.IsLifecycleFailed);
        Assert.Equal(Loc.Get("Jobs_LifecycleInterrupted"), job.LifecycleDisplay);
        Assert.Equal(
            Loc.Get("Jobs_LifecycleInterruptedDescription"),
            harness.ViewModel.SelectedLifecycleDescription);
        Assert.Equal(
            Loc.Get("Jobs_LifecycleInterruptedDescription"),
            harness.ViewModel.SelectedLastRunSummary);

        harness.ViewModel.LifecycleFilterIndex = 1;
        Assert.Empty(harness.ViewModel.Jobs);

        harness.ViewModel.LifecycleFilterIndex = 3;
        Assert.Collection(
            harness.ViewModel.Jobs,
            item => Assert.Same(job, item));
    }

    [Theory]
    [InlineData(BackgroundJobRunStatuses.Completed)]
    [InlineData(BackgroundJobRunStatuses.Skipped)]
    [InlineData(BackgroundJobRunStatuses.Failed)]
    public void LiveRun_TakesPrecedenceOverPreviousTerminalLifecycle(string previousStatus)
    {
        var job = CreateJob(Guid.NewGuid(), "Running");
        job.LastRunStatus = previousStatus;
        job.RunCount = 1;
        job.IsRunning = true;

        Assert.True(job.IsLifecycleInProgress);
        Assert.False(job.IsLifecycleCompleted);
        Assert.False(job.IsLifecycleSkipped);
        Assert.False(job.IsLifecycleFailed);
        Assert.Equal(Loc.Get("Jobs_LifecycleRunning"), job.LifecycleDisplay);
    }

    [Fact]
    public void SelectedState_SeparatesActivationFromLifecycle()
    {
        var chat = CreateChat("Monitoring");
        var job = CreateJob(chat.Id, "Completed one-shot");
        job.LastRunStatus = BackgroundJobRunStatuses.Completed;
        job.RunCount = 1;
        var data = new AppData { Chats = [chat], BackgroundJobs = [job] };
        using var harness = CreateHarness(data);

        Assert.Equal(job.ActivationDisplay, harness.ViewModel.SelectedActivationStatus);
        Assert.Equal(job.LifecycleDisplay, harness.ViewModel.SelectedLifecycleStatus);
        Assert.NotEqual(harness.ViewModel.SelectedActivationStatus, harness.ViewModel.SelectedLifecycleStatus);
        Assert.True(harness.ViewModel.IsSelectedJobPaused);
        Assert.True(harness.ViewModel.IsSelectedLifecycleCompleted);
    }

    [Fact]
    public void FiltersThatHideSelection_KeepTheEditorBuffer()
    {
        var chat = CreateChat("Monitoring");
        var selectedJob = CreateJob(chat.Id, "A failed monitor");
        selectedJob.IsEnabled = true;
        selectedJob.LastRunStatus = BackgroundJobRunStatuses.Failed;
        selectedJob.RunCount = 1;
        var completedJob = CreateJob(chat.Id, "B completed monitor");
        completedJob.LastRunStatus = BackgroundJobRunStatuses.Completed;
        completedJob.RunCount = 1;
        var data = new AppData { Chats = [chat], BackgroundJobs = [selectedJob, completedJob] };
        using var harness = CreateHarness(data);
        harness.ViewModel.EditName = "Unsaved monitor name";

        harness.ViewModel.LifecycleFilterIndex = 2;

        Assert.Same(selectedJob, harness.ViewModel.SelectedJob);
        Assert.Null(harness.ViewModel.SelectedVisibleJob);
        Assert.Equal("Unsaved monitor name", harness.ViewModel.EditName);
        Assert.Collection(
            harness.ViewModel.Jobs,
            job => Assert.Same(completedJob, job));

        harness.ViewModel.LifecycleFilterIndex = 0;

        Assert.Same(selectedJob, harness.ViewModel.SelectedVisibleJob);
        Assert.Equal("Unsaved monitor name", harness.ViewModel.EditName);
    }

    [Fact]
    public void ToggleJob_NotifiesSidebarPresentationProperties()
    {
        var chat = CreateChat("Monitoring");
        var job = CreateJob(chat.Id, "Paused monitor");
        var data = new AppData { Chats = [chat], BackgroundJobs = [job] };
        using var harness = CreateHarness(data);
        var changedProperties = new List<string?>();
        job.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        harness.ViewModel.ToggleJobCommand.Execute(job);

        Assert.Contains(nameof(BackgroundJob.IsEnabled), changedProperties);
        Assert.Contains(nameof(BackgroundJob.ActivationDisplay), changedProperties);
        Assert.Contains(nameof(BackgroundJob.UpcomingRunDisplay), changedProperties);
    }

    [Fact]
    public void RuntimeRefresh_PreservesEditorBufferAndUpdatesRuntimeState()
    {
        var originalChat = CreateChat("Monitoring");
        var replacementChat = CreateChat("Replacement");
        var job = CreateJob(originalChat.Id, "Monitor");
        job.IsEnabled = true;
        var data = new AppData { Chats = [originalChat, replacementChat], BackgroundJobs = [job] };
        using var harness = CreateHarness(data);
        harness.ViewModel.EditName = "Unsaved name";
        harness.ViewModel.EditDescription = "Unsaved description";
        harness.ViewModel.EditChat = replacementChat;
        harness.ViewModel.ValidationMessage = "Keep this message";

        job.IsEnabled = false;
        job.LastRunStatus = BackgroundJobRunStatuses.Completed;
        job.RunCount = 1;
        harness.ViewModel.RefreshFromStore(preserveEditorBuffer: true);

        Assert.Equal("Unsaved name", harness.ViewModel.EditName);
        Assert.Equal("Unsaved description", harness.ViewModel.EditDescription);
        Assert.Same(replacementChat, harness.ViewModel.EditChat);
        Assert.Equal("Keep this message", harness.ViewModel.ValidationMessage);
        Assert.False(harness.ViewModel.EditIsEnabled);
        Assert.Equal(job.LifecycleDisplay, harness.ViewModel.SelectedLifecycleStatus);
    }

    [Fact]
    public void ChatEventJob_RoundTripsSourceAndFiltersThroughEditor()
    {
        var targetChat = CreateChat("Lead");
        var sourceChat = CreateChat("Worker");
        var job = CreateJob(targetChat.Id, "Worker completion");
        job.TriggerType = BackgroundJobTriggerTypes.ChatEvent;
        job.SourceChatId = sourceChat.Id;
        job.ChatEventTypes = [ChatLifecycleEventTypes.Idle];
        var data = new AppData { Chats = [targetChat, sourceChat], BackgroundJobs = [job] };
        using var harness = CreateHarness(data);

        Assert.True(harness.ViewModel.IsChatEventTrigger);
        Assert.Same(sourceChat, harness.ViewModel.EditSourceChat);
        Assert.Equal(ChatLifecycleEventTypes.Idle, harness.ViewModel.EditChatEventTypes);

        harness.ViewModel.EditChatEventTypes = "turn_started, turn-ended";
        harness.ViewModel.SaveJobCommand.Execute(null);

        Assert.Equal(
            [ChatLifecycleEventTypes.TurnStart, ChatLifecycleEventTypes.TurnEnd],
            job.ChatEventTypes);
        Assert.Null(job.NextRunAt);
    }

    [Fact]
    public void OrphanedJobActions_AreBlockedWithFeedbackAndPreserveDraft()
    {
        var job = CreateJob(Guid.NewGuid(), "Orphaned");
        var data = new AppData { BackgroundJobs = [job] };
        using var harness = CreateHarness(data);
        var openedChat = false;
        harness.ViewModel.OpenChatRequested += _ => openedChat = true;
        harness.ViewModel.EditName = "Unsaved orphan name";

        harness.ViewModel.ToggleJobCommand.Execute(job);
        harness.ViewModel.RunNowCommand.Execute(job);
        harness.ViewModel.OpenChatCommand.Execute(job);

        Assert.False(job.IsEnabled);
        Assert.False(openedChat);
        Assert.Equal("Unsaved orphan name", harness.ViewModel.EditName);
        Assert.Equal(Loc.Get("Jobs_LinkedChatMissing"), harness.ViewModel.ValidationMessage);
    }

    private static TestHarness CreateHarness(AppData data)
    {
        var store = new DataStore(data);
        var chatViewModel = new ChatViewModel(store, TestCopilot.Shared);
        var jobService = new BackgroundJobService(store, chatViewModel);
        return new TestHarness(new BackgroundJobsViewModel(store, jobService), jobService);
    }

    private static Chat CreateChat(string title)
    {
        return new Chat
        {
            Id = Guid.NewGuid(),
            Title = title,
            UpdatedAt = DateTimeOffset.Now
        };
    }

    private static BackgroundJob CreateJob(Guid chatId, string name)
    {
        return new BackgroundJob
        {
            Id = Guid.NewGuid(),
            ChatId = chatId,
            Name = name,
            Prompt = "Check something.",
            TriggerType = BackgroundJobTriggerTypes.Time,
            ScheduleType = BackgroundJobScheduleTypes.Interval,
            IsEnabled = false
        };
    }

    private sealed record TestHarness(BackgroundJobsViewModel ViewModel, BackgroundJobService JobService) : IDisposable
    {
        public void Dispose()
        {
            JobService.Dispose();
        }
    }
}
