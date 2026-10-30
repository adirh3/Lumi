using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

[Trait("Category", "CoreQA")]
public sealed class BackgroundJobsViewModelTests
{
    [Fact]
    public void RefreshFromStore_OpensOnTheOverview_WithJobsGroupedByHowTheyAreDoing()
    {
        var chat = CreateChat("Monitoring");
        var running = CreateJob(chat.Id, "Running watcher");
        running.IsEnabled = true;
        running.IsRunning = true;
        running.LastRunStatus = BackgroundJobRunStatuses.Watching;
        var failing = CreateJob(chat.Id, "Failing digest");
        failing.IsEnabled = true;
        failing.LastRunStatus = BackgroundJobRunStatuses.Failed;
        failing.RunCount = 1;
        var healthy = CreateJob(chat.Id, "Morning plan");
        healthy.IsEnabled = true;
        var pausedFailure = CreateJob(chat.Id, "Old watcher");
        pausedFailure.LastRunStatus = BackgroundJobRunStatuses.Failed;
        pausedFailure.RunCount = 1;
        var data = new AppData { Chats = [chat], BackgroundJobs = [running, failing, healthy, pausedFailure] };
        using var harness = CreateHarness(data);
        var vm = harness.ViewModel;

        Assert.Null(vm.SelectedJob);
        Assert.False(vm.IsEditing);
        Assert.True(vm.ShowOverview);
        Assert.Same(running, Assert.Single(vm.RunningCards).Model);
        Assert.Same(failing, Assert.Single(vm.AttentionCards).Model);
        Assert.Same(healthy, Assert.Single(vm.EnabledCards).Model);
        // A paused job that failed waits with the paused ones rather than asking for attention.
        Assert.Same(pausedFailure, Assert.Single(vm.PausedCards).Model);
        Assert.Equal("Monitoring", vm.EnabledCards[0].ChatTitle);
        Assert.Equal(3, vm.EnabledJobCount);
        Assert.Equal(1, vm.RunningJobCount);
        Assert.Equal(1, vm.AttentionJobCount);
        Assert.Equal(1, vm.PausedJobCount);
        Assert.Equal(4, vm.AllFacet.Count);
        Assert.Equal(2, vm.FailedFacet.Count);
        Assert.True(vm.AllFacet.IsSelected);
    }

    [Fact]
    public void JobCards_MoveBetweenGroupsAsTheirJobsChange_KeepingTheirInstance()
    {
        var chat = CreateChat("Monitoring");
        var job = CreateJob(chat.Id, "Watcher");
        job.IsEnabled = true;
        var data = new AppData { Chats = [chat], BackgroundJobs = [job] };
        using var harness = CreateHarness(data);
        var card = Assert.Single(harness.ViewModel.EnabledCards);

        job.IsRunning = true;
        harness.ViewModel.RefreshFromStore(preserveEditorBuffer: true);
        Assert.Empty(harness.ViewModel.EnabledCards);
        Assert.Same(card, Assert.Single(harness.ViewModel.RunningCards));
        Assert.Equal(Loc.Get("Jobs_LifecycleRunning"), card.StatusText);

        job.IsRunning = false;
        harness.ViewModel.ToggleJobCommand.Execute(job);
        Assert.Empty(harness.ViewModel.RunningCards);
        Assert.Same(card, Assert.Single(harness.ViewModel.PausedCards));
        Assert.False(card.IsEnabled);
    }

    [Fact]
    public void Facets_NarrowTheListToOneState_AndAllJobsClearsThem()
    {
        var chat = CreateChat("Monitoring");
        var enabled = CreateJob(chat.Id, "Enabled");
        enabled.IsEnabled = true;
        var failed = CreateJob(chat.Id, "Failed");
        failed.LastRunStatus = BackgroundJobRunStatuses.Failed;
        failed.RunCount = 1;
        var data = new AppData { Chats = [chat], BackgroundJobs = [enabled, failed] };
        using var harness = CreateHarness(data);
        var vm = harness.ViewModel;

        vm.EnabledFacet.SelectCommand.Execute(null);
        Assert.Equal(1, vm.ActivationFilterIndex);
        Assert.Same(enabled, Assert.Single(vm.Jobs));
        Assert.True(vm.EnabledFacet.IsSelected);
        Assert.False(vm.AllFacet.IsSelected);
        Assert.Empty(vm.PausedCards);

        vm.FailedFacet.SelectCommand.Execute(null);
        Assert.Equal(0, vm.ActivationFilterIndex);
        Assert.Equal(3, vm.LifecycleFilterIndex);
        Assert.Same(failed, Assert.Single(vm.Jobs));
        Assert.Same(failed, Assert.Single(vm.PausedCards).Model);

        vm.AllFacet.SelectCommand.Execute(null);
        Assert.False(vm.HasActiveFilters);
        Assert.Equal(2, vm.Jobs.Count);
    }

    [Fact]
    public void Facets_CountTheJobsTheSearchLeaves()
    {
        var chat = CreateChat("Monitoring");
        var hotel = CreateJob(chat.Id, "Hotel prices");
        hotel.IsEnabled = true;
        var digest = CreateJob(chat.Id, "Morning digest");
        var data = new AppData { Chats = [chat], BackgroundJobs = [hotel, digest] };
        using var harness = CreateHarness(data);

        harness.ViewModel.SearchQuery = "hotel";

        Assert.Equal(1, harness.ViewModel.AllFacet.Count);
        Assert.Equal(1, harness.ViewModel.EnabledFacet.Count);
        Assert.Equal(0, harness.ViewModel.PausedFacet.Count);
        Assert.False(harness.ViewModel.PausedFacet.HasItems);
    }

    [Fact]
    public void PreferredChat_KeepsTheOverview_AndNewJobsTargetIt()
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

        harness.ViewModel.SetPreferredChat(preferredChat);
        harness.ViewModel.RefreshFromStore();
        Assert.Null(harness.ViewModel.SelectedJob);
        Assert.True(harness.ViewModel.ShowOverview);

        harness.ViewModel.NewJobCommand.Execute(null);
        Assert.Same(preferredChat, harness.ViewModel.EditChat);
        Assert.True(harness.ViewModel.IsNewJob);
    }

    [Fact]
    public void RefreshFromStore_OpensTheJobAWakeNamed_Once()
    {
        var chat = CreateChat("Pull requests");
        var firstJob = CreateJob(chat.Id, "A first watcher");
        var wokenJob = CreateJob(chat.Id, "B second watcher");
        var data = new AppData { Chats = [chat], BackgroundJobs = [firstJob, wokenJob] };
        using var harness = CreateHarness(data);

        harness.ViewModel.SetPreferredChat(chat, wokenJob.Id);
        harness.ViewModel.RefreshFromStore();
        Assert.Same(wokenJob, harness.ViewModel.SelectedJob);
        Assert.True(harness.ViewModel.IsEditing);

        // The named job is chosen once; reopening from the chat later keeps what is open.
        harness.ViewModel.SetPreferredChat(chat);
        harness.ViewModel.RefreshFromStore();
        Assert.Same(wokenJob, harness.ViewModel.SelectedJob);
    }

    [Fact]
    public void RefreshFromStore_ShowsTheJobAWakeNamed_EvenWhenFiltersHideIt()
    {
        var chat = CreateChat("Pull requests");
        var visibleJob = CreateJob(chat.Id, "A paused watcher");
        var wokenJob = CreateJob(chat.Id, "B running watcher");
        wokenJob.IsEnabled = true;
        var data = new AppData { Chats = [chat], BackgroundJobs = [visibleJob, wokenJob] };
        using var harness = CreateHarness(data);
        harness.ViewModel.SearchQuery = "paused";
        Assert.DoesNotContain(wokenJob, harness.ViewModel.Jobs);

        harness.ViewModel.SetPreferredChat(chat, wokenJob.Id);
        harness.ViewModel.RefreshFromStore();

        Assert.Same(wokenJob, harness.ViewModel.SelectedJob);
        Assert.Equal("", harness.ViewModel.SearchQuery);
        Assert.Contains(wokenJob, harness.ViewModel.Jobs);
    }

    [Fact]
    public void DeletingTheOpenJob_AsksFirst_ThenReturnsToTheOverview()
    {
        var chat = CreateChat("Monitoring");
        var firstJob = CreateJob(chat.Id, "A first");
        var secondJob = CreateJob(chat.Id, "B second");
        var data = new AppData { Chats = [chat], BackgroundJobs = [firstJob, secondJob] };
        using var harness = CreateHarness(data);
        var vm = harness.ViewModel;
        vm.SelectedJob = firstJob;

        vm.RequestDeleteCommand.Execute(null);
        Assert.True(vm.IsConfirmingDelete);
        Assert.Contains(firstJob, data.BackgroundJobs);
        vm.CancelDeleteCommand.Execute(null);
        Assert.False(vm.IsConfirmingDelete);
        Assert.Contains(firstJob, data.BackgroundJobs);

        vm.RequestDeleteCommand.Execute(null);
        vm.ConfirmDeleteCommand.Execute(null);

        Assert.DoesNotContain(firstJob, data.BackgroundJobs);
        Assert.Null(vm.SelectedJob);
        Assert.False(vm.IsEditing);
        Assert.True(vm.ShowOverview);
        Assert.Same(secondJob, Assert.Single(vm.Jobs));
    }

    [Fact]
    public void DeletingAnotherJob_LeavesTheOpenOneAndItsEdits()
    {
        var chat = CreateChat("Monitoring");
        var openJob = CreateJob(chat.Id, "A open");
        var otherJob = CreateJob(chat.Id, "B other");
        var data = new AppData { Chats = [chat], BackgroundJobs = [openJob, otherJob] };
        using var harness = CreateHarness(data);
        harness.ViewModel.SelectedJob = openJob;
        harness.ViewModel.EditName = "Unsaved name";

        harness.ViewModel.DeleteJobCommand.Execute(otherJob);

        Assert.Same(openJob, harness.ViewModel.SelectedJob);
        Assert.True(harness.ViewModel.IsEditing);
        Assert.Equal("Unsaved name", harness.ViewModel.EditName);
    }

    [Fact]
    public void Edits_ShowTheSaveBar_AndDiscardRestoresTheStoredJob()
    {
        var chat = CreateChat("Monitoring");
        var job = CreateJob(chat.Id, "Monitor");
        var data = new AppData { Chats = [chat], BackgroundJobs = [job] };
        using var harness = CreateHarness(data);
        var vm = harness.ViewModel;
        vm.SelectedJob = job;
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.ShowSaveBar);

        vm.EditName = "Renamed monitor";
        Assert.True(vm.HasUnsavedChanges);
        Assert.True(vm.ShowSaveBar);
        Assert.Equal("Renamed monitor", vm.DetailTitle);

        vm.EditName = "Monitor";
        Assert.False(vm.HasUnsavedChanges);

        vm.EditScheduleTypeIndex = 2;
        Assert.True(vm.ShowSaveBar);
        vm.DiscardChangesCommand.Execute(null);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(0, vm.EditScheduleTypeIndex);
        Assert.Equal("interval", job.ScheduleType);
    }

    [Fact]
    public void Saving_KeepsTheJobOpenWithNothingLeftUnsaved()
    {
        var chat = CreateChat("Monitoring");
        var job = CreateJob(chat.Id, "Monitor");
        var data = new AppData { Chats = [chat], BackgroundJobs = [job] };
        using var harness = CreateHarness(data);
        var vm = harness.ViewModel;
        vm.SelectedJob = job;

        vm.EditName = "  Renamed monitor  ";
        vm.SaveJobCommand.Execute(null);

        Assert.Equal("Renamed monitor", job.Name);
        Assert.Same(job, vm.SelectedJob);
        Assert.True(vm.IsEditing);
        Assert.Equal("Renamed monitor", vm.EditName);
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.ShowSaveBar);
        Assert.True(vm.ShowSavedToast);
    }

    [Fact]
    public void ANewJob_KeepsTheSaveBarUntilCreated_AndCancelGoesBackToTheOverview()
    {
        var chat = CreateChat("Monitoring");
        var data = new AppData { Chats = [chat] };
        using var harness = CreateHarness(data);
        var vm = harness.ViewModel;
        Assert.True(vm.ShowEmptyState);

        vm.NewJobCommand.Execute(null);
        Assert.True(vm.IsNewJob);
        Assert.True(vm.ShowSaveBar);
        Assert.Equal(Loc.Jobs_Create, vm.SaveButtonText);
        Assert.Equal(Loc.Jobs_New, vm.DetailTitle);

        vm.DiscardChangesCommand.Execute(null);
        Assert.False(vm.IsEditing);
        Assert.True(vm.ShowOverview);
        Assert.Empty(data.BackgroundJobs);

        vm.NewJobCommand.Execute(null);
        vm.SaveJobCommand.Execute(null);
        Assert.Equal(Loc.Jobs_ValidationName, vm.ValidationMessage);
        Assert.True(vm.HasValidationMessage);
        vm.EditName = "Morning plan";
        vm.EditPrompt = "Plan my morning.";
        vm.SaveJobCommand.Execute(null);

        var created = Assert.Single(data.BackgroundJobs);
        Assert.Same(created, vm.SelectedJob);
        Assert.False(vm.IsNewJob);
        Assert.False(vm.ShowSaveBar);
        Assert.False(vm.HasValidationMessage);
    }

    [Fact]
    public void ARefresh_NeverThrowsAwayEditsInProgress()
    {
        var chat = CreateChat("Monitoring");
        var job = CreateJob(chat.Id, "Monitor");
        var data = new AppData { Chats = [chat], BackgroundJobs = [job] };
        using var harness = CreateHarness(data);
        harness.ViewModel.SelectedJob = job;
        harness.ViewModel.EditPrompt = "An unsaved instruction.";

        harness.ViewModel.RefreshFromStore();

        Assert.Equal("An unsaved instruction.", harness.ViewModel.EditPrompt);
        Assert.True(harness.ViewModel.HasUnsavedChanges);
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
        harness.ViewModel.SelectedJob = job;

        Assert.False(job.IsLifecycleInProgress);
        Assert.True(job.IsLifecycleInterrupted);
        Assert.True(job.IsLifecycleFailed);
        Assert.Equal(Loc.Get("Jobs_LifecycleInterrupted"), job.LifecycleDisplay);
        Assert.Equal(
            Loc.Get("Jobs_LifecycleInterruptedDescription"),
            harness.ViewModel.SelectedLifecycleDescription);
        Assert.True(harness.ViewModel.IsSelectedFailing);

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
        harness.ViewModel.SelectedJob = job;

        Assert.Equal(job.ActivationDisplay, harness.ViewModel.SelectedActivationStatus);
        Assert.Equal(job.LifecycleDisplay, harness.ViewModel.SelectedLifecycleStatus);
        Assert.NotEqual(harness.ViewModel.SelectedActivationStatus, harness.ViewModel.SelectedLifecycleStatus);
        Assert.True(harness.ViewModel.IsSelectedJobPaused);
        Assert.True(harness.ViewModel.IsSelectedLifecycleCompleted);
        Assert.Equal("Monitoring", harness.ViewModel.SelectedChatTitle);
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
        harness.ViewModel.SelectedJob = selectedJob;
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
        Assert.Contains(nameof(BackgroundJob.ScheduleDisplay), changedProperties);
    }

    [Fact]
    public void ToggleOfTheOpenJob_IsNotAnUnsavedEdit()
    {
        var chat = CreateChat("Monitoring");
        var job = CreateJob(chat.Id, "Monitor");
        var data = new AppData { Chats = [chat], BackgroundJobs = [job] };
        using var harness = CreateHarness(data);
        harness.ViewModel.SelectedJob = job;

        harness.ViewModel.ToggleJobCommand.Execute(job);

        Assert.True(job.IsEnabled);
        Assert.True(harness.ViewModel.EditIsEnabled);
        Assert.False(harness.ViewModel.HasUnsavedChanges);
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
        harness.ViewModel.SelectedJob = job;
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
        harness.ViewModel.SelectedJob = job;

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
    public void ChatEventChips_ShowTheStoredEvents_AndWriteTheChosenOnes()
    {
        var targetChat = CreateChat("Lead");
        var sourceChat = CreateChat("Worker");
        var job = CreateJob(targetChat.Id, "Worker completion");
        job.TriggerType = BackgroundJobTriggerTypes.ChatEvent;
        job.SourceChatId = sourceChat.Id;
        job.ChatEventTypes = [ChatLifecycleEventTypes.Idle];
        var data = new AppData { Chats = [targetChat, sourceChat], BackgroundJobs = [job] };
        using var harness = CreateHarness(data);
        var vm = harness.ViewModel;

        // "idle" is also the editor's initial value, so nothing changes when the job opens.
        vm.SelectedJob = job;
        var idle = vm.ChatEventOptions.Single(option => option.Token == ChatLifecycleEventTypes.Idle);
        var turnEnd = vm.ChatEventOptions.Single(option => option.Token == ChatLifecycleEventTypes.TurnEnd);
        Assert.True(idle.IsSelected);
        Assert.Single(vm.ChatEventOptions, option => option.IsSelected);

        turnEnd.ToggleCommand.Execute(null);
        Assert.True(turnEnd.IsSelected);
        Assert.Equal("turn_end, idle", vm.EditChatEventTypes);
        Assert.True(vm.HasUnsavedChanges);

        idle.ToggleCommand.Execute(null);
        turnEnd.ToggleCommand.Execute(null);
        Assert.True(turnEnd.IsSelected);
        Assert.Equal(ChatLifecycleEventTypes.TurnEnd, vm.EditChatEventTypes);

        vm.SaveJobCommand.Execute(null);
        Assert.Equal([ChatLifecycleEventTypes.TurnEnd], job.ChatEventTypes);
    }

    [Fact]
    public void WeekdayChips_ShowTheStoredDays_AndWriteTheChosenOnes()
    {
        var chat = CreateChat("Planning");
        var job = CreateJob(chat.Id, "Weekday plan");
        job.ScheduleType = BackgroundJobScheduleTypes.Weekly;
        // The editor starts with "weekdays" too, so opening the job changes nothing.
        job.DaysOfWeek = "weekdays";
        job.DailyTime = "08:30";
        var data = new AppData { Chats = [chat], BackgroundJobs = [job] };
        using var harness = CreateHarness(data);
        var vm = harness.ViewModel;
        vm.SelectedJob = job;

        DayOfWeek[] weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
        Assert.Equal(weekdays.Order(), vm.WeekDays.Where(day => day.IsSelected).Select(day => day.Day).Order());
        Assert.Equal(7, vm.WeekDays.Count);

        vm.WeekDays.Single(day => day.Day == DayOfWeek.Saturday).ToggleCommand.Execute(null);
        Assert.Equal("Mon,Tue,Wed,Thu,Fri,Sat", vm.EditDaysOfWeek);
        Assert.True(vm.HasUnsavedChanges);

        vm.WeekDays.Single(day => day.Day == DayOfWeek.Saturday).ToggleCommand.Execute(null);
        Assert.False(vm.HasUnsavedChanges);

        foreach (var day in vm.WeekDays.Where(day => day.Day != DayOfWeek.Monday))
        {
            if (day.IsSelected)
                day.ToggleCommand.Execute(null);
        }
        vm.WeekDays.Single(day => day.Day == DayOfWeek.Monday).ToggleCommand.Execute(null);
        Assert.Equal("Mon", vm.EditDaysOfWeek);

        vm.SaveJobCommand.Execute(null);
        Assert.Equal("Mon", job.DaysOfWeek);
    }

    [Fact]
    public void SchedulePreview_ReadsTheEditedScheduleBack_FromTheJobsLastRun()
    {
        var chat = CreateChat("Prices");
        var job = CreateJob(chat.Id, "Price watch");
        job.IsEnabled = true;
        job.ScheduleType = BackgroundJobScheduleTypes.Interval;
        job.IntervalMinutes = 720;
        job.LastRunAt = DateTimeOffset.Now.AddHours(-1);
        job.NextRunAt = job.LastRunAt.Value.AddMinutes(720);
        var data = new AppData { Chats = [chat], BackgroundJobs = [job] };
        using var harness = CreateHarness(data);
        var vm = harness.ViewModel;

        vm.SelectedJob = job;

        Assert.Equal(BackgroundJobSchedule.Describe(new BackgroundJob { IntervalMinutes = 720 }), vm.EditScheduleSummary);
        Assert.Equal(NextRunText(job.NextRunAt.Value), vm.EditNextRunText);

        // An edited interval counts from the last run, as saving will.
        vm.EditIntervalMinutes = 360;
        Assert.Equal(NextRunText(job.LastRunAt.Value.AddMinutes(360)), vm.EditNextRunText);

        vm.EditScheduleTypeIndex = 1;
        vm.EditDailyTime = "25:99";
        Assert.False(vm.HasEditSchedulePreview);

        vm.EditDailyTime = "07:45";
        Assert.True(vm.HasEditSchedulePreview);
        Assert.Contains("07:45", vm.EditScheduleSummary);
    }

    [Fact]
    public void SchedulePreview_AgreesWithTheJobsStatus_UntilItsScheduleOrSwitchChanges()
    {
        var chat = CreateChat("Builds");
        var job = CreateJob(chat.Id, "Build check");
        job.IsEnabled = true;
        job.IntervalMinutes = 720;
        job.CreatedAt = job.UpdatedAt = DateTimeOffset.Now.AddHours(-5);
        job.NextRunAt = job.UpdatedAt.AddMinutes(720);
        var data = new AppData { Chats = [chat], BackgroundJobs = [job] };
        using var harness = CreateHarness(data);
        var vm = harness.ViewModel;

        // A job that hasn't run yet keeps the run it was stored with; saving now would move it.
        vm.SelectedJob = job;
        var stored = NextRunText(job.NextRunAt.Value);
        Assert.Equal(stored, vm.EditNextRunText);

        vm.EditIntervalMinutes = 360;
        Assert.NotEqual(stored, vm.EditNextRunText);
        vm.EditIntervalMinutes = 720;
        Assert.Equal(stored, vm.EditNextRunText);

        // A paused job doesn't run, whether the switch was turned off here or the job was paused.
        vm.EditIsEnabled = false;
        Assert.False(vm.HasEditNextRun);
        vm.EditIsEnabled = true;
        Assert.Equal(stored, vm.EditNextRunText);

        vm.ToggleJobCommand.Execute(job);
        Assert.False(job.IsEnabled);
        Assert.False(vm.HasEditNextRun);
        Assert.True(vm.HasEditSchedulePreview);

        vm.ToggleJobCommand.Execute(job);
        Assert.Equal(NextRunText(job.NextRunAt!.Value), vm.EditNextRunText);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void OrphanedJobActions_AreBlockedWithFeedbackAndPreserveDraft()
    {
        var job = CreateJob(Guid.NewGuid(), "Orphaned");
        var data = new AppData { BackgroundJobs = [job] };
        using var harness = CreateHarness(data);
        var openedChat = false;
        harness.ViewModel.OpenChatRequested += _ => openedChat = true;
        harness.ViewModel.SelectedJob = job;
        harness.ViewModel.EditName = "Unsaved orphan name";

        harness.ViewModel.ToggleJobCommand.Execute(job);
        harness.ViewModel.RunNowCommand.Execute(job);
        harness.ViewModel.OpenChatCommand.Execute(job);

        Assert.False(job.IsEnabled);
        Assert.False(openedChat);
        Assert.Equal("Unsaved orphan name", harness.ViewModel.EditName);
        Assert.Equal(Loc.Get("Jobs_LinkedChatMissing"), harness.ViewModel.ValidationMessage);

        harness.ViewModel.DismissValidationCommand.Execute(null);
        Assert.False(harness.ViewModel.HasValidationMessage);
    }

    [Fact]
    public void ScheduleDisplay_DescribesACopy_LeavingTheStoredJobAsItIs()
    {
        var job = new BackgroundJob
        {
            ScheduleType = "Daily",
            DailyTime = "not a time",
            DaysOfWeek = "",
            ChatEventTypes = []
        };

        Assert.Equal(BackgroundJobSchedule.Describe(new BackgroundJob { ScheduleType = BackgroundJobScheduleTypes.Daily }), job.ScheduleDisplay);
        Assert.Equal("Daily", job.ScheduleType);
        Assert.Equal("not a time", job.DailyTime);
        Assert.Equal("", job.DaysOfWeek);
    }

    private static TestHarness CreateHarness(AppData data)
    {
        var store = new DataStore(data);
        var chatViewModel = new ChatViewModel(store, TestCopilot.Shared);
        var jobService = new BackgroundJobService(store, chatViewModel);
        return new TestHarness(new BackgroundJobsViewModel(store, jobService), jobService);
    }

    private static string NextRunText(DateTimeOffset at) =>
        Loc.Get("Jobs_NextRunAt", LaunchpadPlanner.FormatRunTime(at, DateTimeOffset.Now));

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
