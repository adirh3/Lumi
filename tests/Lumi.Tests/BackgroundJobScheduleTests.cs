using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class BackgroundJobScheduleTests
{
    [Fact]
    public void ComputeNextRun_ScriptTrigger_IsOneShot()
    {
        var now = new DateTimeOffset(2026, 4, 26, 10, 0, 0, TimeSpan.FromHours(3));
        var job = new BackgroundJob
        {
            TriggerType = BackgroundJobTriggerTypes.Script,
            ScriptContent = "Write-Output ready"
        };

        Assert.Equal(now, BackgroundJobSchedule.ComputeNextRun(job, now, afterRun: false));
        Assert.Null(BackgroundJobSchedule.ComputeNextRun(job, now, afterRun: true));
    }

    [Fact]
    public void ComputeNextRun_OnceTrigger_CanBeRearmedWithLaterRunAt()
    {
        var lastRun = new DateTimeOffset(2026, 4, 26, 10, 0, 0, TimeSpan.FromHours(3));
        var nextRun = new DateTimeOffset(2026, 4, 26, 11, 0, 0, TimeSpan.FromHours(3));
        var job = new BackgroundJob
        {
            TriggerType = BackgroundJobTriggerTypes.Time,
            ScheduleType = BackgroundJobScheduleTypes.Once,
            RunAt = nextRun,
            LastRunAt = lastRun
        };

        Assert.Equal(nextRun, BackgroundJobSchedule.ComputeNextRun(job, lastRun, afterRun: false));

        job.LastRunAt = nextRun;
        Assert.Null(BackgroundJobSchedule.ComputeNextRun(job, nextRun, afterRun: false));
    }

    [Fact]
    public void ComputeNextRun_InitialInterval_UsesCreatedOrUpdatedAnchor()
    {
        var createdAt = new DateTimeOffset(2026, 4, 26, 10, 0, 0, TimeSpan.FromHours(3));
        var updatedAt = createdAt.AddMinutes(10);
        var now = createdAt.AddMinutes(30);
        var job = new BackgroundJob
        {
            TriggerType = BackgroundJobTriggerTypes.Time,
            ScheduleType = BackgroundJobScheduleTypes.Interval,
            IntervalMinutes = 60,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt
        };

        Assert.Equal(updatedAt.AddMinutes(60), BackgroundJobSchedule.ComputeNextRun(job, now, afterRun: false));

        job.CreatedAt = createdAt.AddHours(-2);
        job.UpdatedAt = job.CreatedAt;
        job.IntervalMinutes = 30;
        Assert.Equal(now, BackgroundJobSchedule.ComputeNextRun(job, now, afterRun: false));
    }

    [Fact]
    public void ComputeNextRun_Monthly_ClampsShortMonths()
    {
        var now = CreateLocalDateTimeOffset(2026, 1, 31, 9, 0, 0);
        var job = new BackgroundJob
        {
            TriggerType = BackgroundJobTriggerTypes.Time,
            ScheduleType = BackgroundJobScheduleTypes.Monthly,
            MonthlyDay = 31,
            DailyTime = "08:00"
        };

        var expected = CreateLocalDateTimeOffset(2026, 2, 28, 8, 0, 0);
        Assert.Equal(expected, BackgroundJobSchedule.ComputeNextRun(job, now, afterRun: false));
    }

    [Fact]
    public void TryValidateCronExpression_AcceptsAliasesAndRejectsMalformedInput()
    {
        Assert.True(BackgroundJobSchedule.TryValidateCronExpression("0 8 * * Mon-Fri", out var validError));
        Assert.Equal("", validError);

        Assert.False(BackgroundJobSchedule.TryValidateCronExpression("0 8 *", out var invalidError));
        Assert.Contains("five fields", invalidError);
    }

    [Fact]
    public void GetSchedulerDelay_NoNextRun_SleepsUntilRescheduled()
    {
        var now = new DateTimeOffset(2026, 4, 26, 10, 0, 0, TimeSpan.FromHours(3));

        Assert.Null(BackgroundJobService.GetSchedulerDelay(null, now));
    }

    [Fact]
    public void GetSchedulerDelay_DueRun_DoesNotDelay()
    {
        var now = new DateTimeOffset(2026, 4, 26, 10, 0, 0, TimeSpan.FromHours(3));

        Assert.Equal(TimeSpan.Zero, BackgroundJobService.GetSchedulerDelay(now.AddSeconds(-1), now));
    }

    [Fact]
    public void GetSchedulerDelay_FutureRun_UsesExactDelay()
    {
        var now = new DateTimeOffset(2026, 4, 26, 10, 0, 0, TimeSpan.FromHours(3));

        Assert.Equal(TimeSpan.FromHours(6), BackgroundJobService.GetSchedulerDelay(now.AddHours(6), now));
    }

    [Fact]
    public void GetSchedulerDelay_VeryFarRun_CapsSleepForClockChanges()
    {
        var now = new DateTimeOffset(2026, 4, 26, 10, 0, 0, TimeSpan.FromHours(3));

        Assert.Equal(TimeSpan.FromHours(24), BackgroundJobService.GetSchedulerDelay(now.AddDays(7), now));
    }

    private static DateTimeOffset CreateLocalDateTimeOffset(
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second)
    {
        var localDateTime = new DateTime(year, month, day, hour, minute, second);
        return new DateTimeOffset(localDateTime, TimeZoneInfo.Local.GetUtcOffset(localDateTime));
    }
}
