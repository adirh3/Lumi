using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Lumi.Models;

namespace Lumi.Services;

public static class BackgroundJobSchedule
{
    public static void Normalize(BackgroundJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        job.TriggerType = NormalizeTriggerType(job.TriggerType);
        job.ScheduleType = NormalizeScheduleType(job.ScheduleType);
        job.IntervalMinutes = Math.Clamp(job.IntervalMinutes, 1, 525_600);
        job.MonthlyDay = Math.Clamp(job.MonthlyDay, 1, 31);

        if (string.IsNullOrWhiteSpace(job.DailyTime) || !TryParseDailyTime(job.DailyTime, out _))
            job.DailyTime = "08:00";

        if (string.IsNullOrWhiteSpace(job.DaysOfWeek) || !TryParseDaysOfWeek(job.DaysOfWeek, out _))
            job.DaysOfWeek = "Mon,Tue,Wed,Thu,Fri";

        if (string.IsNullOrWhiteSpace(job.CronExpression))
            job.CronExpression = "0 8 * * *";

        job.ScriptLanguage = NormalizeScriptLanguage(job.ScriptLanguage);
    }

    public static string NormalizeTriggerType(string? value)
    {
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "script" or "poll" or "polling" => BackgroundJobTriggerTypes.Script,
            _ => BackgroundJobTriggerTypes.Time
        };
    }

    public static string NormalizeScheduleType(string? value)
    {
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "daily" or "day" => BackgroundJobScheduleTypes.Daily,
            "weekly" or "week" => BackgroundJobScheduleTypes.Weekly,
            "monthly" or "month" => BackgroundJobScheduleTypes.Monthly,
            "once" or "one-time" or "one_time" => BackgroundJobScheduleTypes.Once,
            "cron" or "advanced" or "expression" => BackgroundJobScheduleTypes.Cron,
            _ => BackgroundJobScheduleTypes.Interval
        };
    }

    public static string NormalizeScriptLanguage(string? value)
    {
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "pwsh" or "powershell" or "ps1" => BackgroundJobScriptLanguages.PowerShell,
            "python" or "py" or "python3" => BackgroundJobScriptLanguages.Python,
            "node" or "nodejs" or "javascript" or "js" => BackgroundJobScriptLanguages.Node,
            "cmd" or "bat" or "batch" or "command" or "shell" or "sh" => BackgroundJobScriptLanguages.Command,
            _ => BackgroundJobScriptLanguages.DefaultForCurrentOs()
        };
    }

    public static DateTimeOffset? EnsureNextRun(BackgroundJob job, DateTimeOffset now)
    {
        Normalize(job);

        if (!job.IsEnabled)
        {
            job.NextRunAt = null;
            return null;
        }

        job.NextRunAt ??= ComputeNextRun(job, now, afterRun: false);
        return job.NextRunAt;
    }

    public static DateTimeOffset? ComputeNextRun(BackgroundJob job, DateTimeOffset now, bool afterRun)
    {
        Normalize(job);

        if (!job.IsEnabled)
            return null;

        if (job.TriggerType == BackgroundJobTriggerTypes.Script)
        {
            return afterRun ? null : now;
        }

        return job.ScheduleType switch
        {
            BackgroundJobScheduleTypes.Daily => ComputeDailyRun(job.DailyTime, now),
            BackgroundJobScheduleTypes.Weekly => ComputeWeeklyRun(job.DaysOfWeek, job.DailyTime, now),
            BackgroundJobScheduleTypes.Monthly => ComputeMonthlyRun(job.MonthlyDay, job.DailyTime, now),
            BackgroundJobScheduleTypes.Once => ComputeOnceRun(job, now),
            BackgroundJobScheduleTypes.Cron => ComputeCronRun(job.CronExpression, now),
            _ => afterRun
                ? now.AddMinutes(Math.Clamp(job.IntervalMinutes, 1, 525_600))
                : ComputeInitialIntervalRun(job, now, Math.Clamp(job.IntervalMinutes, 1, 525_600))
        };
    }

    public static string Describe(BackgroundJob job)
    {
        Normalize(job);

        if (job.TriggerType == BackgroundJobTriggerTypes.Script)
            return "Wake script: runs once, then wakes Lumi when it exits";

        return job.ScheduleType switch
        {
            BackgroundJobScheduleTypes.Daily => $"Every day at {job.DailyTime}",
            BackgroundJobScheduleTypes.Weekly => $"Every {FormatDays(job.DaysOfWeek)} at {job.DailyTime}",
            BackgroundJobScheduleTypes.Monthly => $"Monthly on day {job.MonthlyDay} at {job.DailyTime}",
            BackgroundJobScheduleTypes.Once => job.RunAt.HasValue
                ? $"Once at {job.RunAt.Value.LocalDateTime:g}"
                : "Once when enabled",
            BackgroundJobScheduleTypes.Cron => $"Advanced schedule: {job.CronExpression}",
            _ => $"Every {FormatInterval(job.IntervalMinutes)}"
        };
    }

    public static bool TryValidateCronExpression(string value, out string error)
    {
        error = "";
        if (TryParseCron(value, out _, out error))
            return true;

        if (string.IsNullOrWhiteSpace(error))
            error = "Cron expression must use five fields: minute hour day-of-month month day-of-week.";
        return false;
    }

    public static bool TryValidateDaysOfWeek(string value, out string error)
    {
        error = "";
        if (TryParseDaysOfWeek(value, out _))
            return true;

        error = "Enter weekdays like Mon,Wed,Fri or weekdays.";
        return false;
    }

    public static bool TryValidateDailyTime(string value, out string error)
    {
        error = "";
        if (TryParseDailyTime(value, out _))
            return true;

        error = "Enter a local time like 08:00.";
        return false;
    }

    private static DateTimeOffset ComputeInitialIntervalRun(BackgroundJob job, DateTimeOffset now, int intervalMinutes)
    {
        var anchor = job.LastRunAt ?? (job.UpdatedAt > job.CreatedAt ? job.UpdatedAt : job.CreatedAt);
        var next = anchor.AddMinutes(intervalMinutes);
        return next <= now ? now : next;
    }

    private static DateTimeOffset? ComputeOnceRun(BackgroundJob job, DateTimeOffset now)
    {
        if (job.RunAt is not { } runAt)
            return job.LastRunAt.HasValue ? null : now;

        return job.LastRunAt.HasValue && job.LastRunAt.Value >= runAt
            ? null
            : runAt;
    }

    private static DateTimeOffset ComputeDailyRun(string dailyTime, DateTimeOffset now)
    {
        if (!TryParseDailyTime(dailyTime, out var time))
            time = new TimeSpan(8, 0, 0);

        var localNow = now.LocalDateTime;
        var candidateLocal = localNow.Date.Add(time);
        if (candidateLocal <= localNow)
            candidateLocal = candidateLocal.AddDays(1);

        return new DateTimeOffset(candidateLocal, TimeZoneInfo.Local.GetUtcOffset(candidateLocal));
    }

    private static DateTimeOffset ComputeWeeklyRun(string daysOfWeek, string dailyTime, DateTimeOffset now)
    {
        if (!TryParseDaysOfWeek(daysOfWeek, out var days) || days.Count == 0)
            days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
        if (!TryParseDailyTime(dailyTime, out var time))
            time = new TimeSpan(8, 0, 0);

        var daySet = days.ToHashSet();
        var localNow = now.LocalDateTime;
        for (var offset = 0; offset <= 7; offset++)
        {
            var candidateDate = localNow.Date.AddDays(offset);
            if (!daySet.Contains(candidateDate.DayOfWeek))
                continue;

            var candidateLocal = candidateDate.Add(time);
            if (candidateLocal > localNow)
                return new DateTimeOffset(candidateLocal, TimeZoneInfo.Local.GetUtcOffset(candidateLocal));
        }

        var fallback = localNow.Date.AddDays(7).Add(time);
        return new DateTimeOffset(fallback, TimeZoneInfo.Local.GetUtcOffset(fallback));
    }

    private static DateTimeOffset ComputeMonthlyRun(int monthlyDay, string dailyTime, DateTimeOffset now)
    {
        if (!TryParseDailyTime(dailyTime, out var time))
            time = new TimeSpan(8, 0, 0);

        var localNow = now.LocalDateTime;
        var candidateLocal = CreateMonthlyCandidate(localNow.Year, localNow.Month, monthlyDay, time);
        if (candidateLocal <= localNow)
        {
            var nextMonth = localNow.AddMonths(1);
            candidateLocal = CreateMonthlyCandidate(nextMonth.Year, nextMonth.Month, monthlyDay, time);
        }

        return new DateTimeOffset(candidateLocal, TimeZoneInfo.Local.GetUtcOffset(candidateLocal));
    }

    private static DateTime CreateMonthlyCandidate(int year, int month, int day, TimeSpan time)
    {
        var safeDay = Math.Clamp(day, 1, DateTime.DaysInMonth(year, month));
        return new DateTime(year, month, safeDay).Add(time);
    }

    private static DateTimeOffset? ComputeCronRun(string expression, DateTimeOffset now)
    {
        if (!TryParseCron(expression, out var cron, out _))
            return null;

        var candidate = now.LocalDateTime.AddMinutes(1);
        candidate = new DateTime(candidate.Year, candidate.Month, candidate.Day, candidate.Hour, candidate.Minute, 0);

        for (var i = 0; i < 1_052_640; i++)
        {
            if (cron.Matches(candidate))
                return new DateTimeOffset(candidate, TimeZoneInfo.Local.GetUtcOffset(candidate));
            candidate = candidate.AddMinutes(1);
        }

        return null;
    }

    private static bool TryParseDailyTime(string value, out TimeSpan time)
    {
        if (TimeSpan.TryParseExact(value.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out time))
            return true;

        return TimeSpan.TryParse(value.Trim(), CultureInfo.CurrentCulture, out time);
    }

    private static bool TryParseDaysOfWeek(string value, out IReadOnlyList<DayOfWeek> days)
    {
        days = [];
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim();
        if (normalized.Equals("weekdays", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("weekday", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("business days", StringComparison.OrdinalIgnoreCase))
        {
            days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
            return true;
        }

        if (normalized.Equals("weekends", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("weekend", StringComparison.OrdinalIgnoreCase))
        {
            days = [DayOfWeek.Saturday, DayOfWeek.Sunday];
            return true;
        }

        if (normalized.Equals("daily", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("everyday", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            days = Enum.GetValues<DayOfWeek>();
            return true;
        }

        var parsed = new List<DayOfWeek>();
        var seen = new HashSet<DayOfWeek>();
        foreach (var token in normalized.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseDayOfWeek(token, out var day) || !seen.Add(day))
                return false;
            parsed.Add(day);
        }

        days = parsed;
        return parsed.Count > 0;
    }

    private static bool TryParseDayOfWeek(string token, out DayOfWeek day)
    {
        day = DayOfWeek.Monday;
        return token.Trim().ToLowerInvariant() switch
        {
            "sun" or "sunday" or "0" or "7" => SetDay(DayOfWeek.Sunday, out day),
            "mon" or "monday" or "1" => SetDay(DayOfWeek.Monday, out day),
            "tue" or "tues" or "tuesday" or "2" => SetDay(DayOfWeek.Tuesday, out day),
            "wed" or "wednesday" or "3" => SetDay(DayOfWeek.Wednesday, out day),
            "thu" or "thur" or "thurs" or "thursday" or "4" => SetDay(DayOfWeek.Thursday, out day),
            "fri" or "friday" or "5" => SetDay(DayOfWeek.Friday, out day),
            "sat" or "saturday" or "6" => SetDay(DayOfWeek.Saturday, out day),
            _ => false
        };
    }

    private static bool SetDay(DayOfWeek value, out DayOfWeek day)
    {
        day = value;
        return true;
    }

    private static string FormatDays(string daysOfWeek)
    {
        if (!TryParseDaysOfWeek(daysOfWeek, out var days) || days.Count == 0)
            return "weekdays";

        var set = days.ToHashSet();
        if (set.SetEquals([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]))
            return "weekdays";
        if (set.SetEquals([DayOfWeek.Saturday, DayOfWeek.Sunday]))
            return "weekends";
        if (set.Count == 7)
            return "day";

        return string.Join(", ", days.Select(static day => day.ToString()[..3]));
    }

    private static bool TryParseCron(string expression, out CronSchedule cron, out string error)
    {
        cron = default;
        error = "";

        var fields = (expression ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5)
        {
            error = "Cron expression must use five fields: minute hour day-of-month month day-of-week.";
            return false;
        }

        if (!TryParseCronField(fields[0], 0, 59, null, normalizeSevenToZero: false, out var minute, out error)
            || !TryParseCronField(fields[1], 0, 23, null, normalizeSevenToZero: false, out var hour, out error)
            || !TryParseCronField(fields[2], 1, 31, null, normalizeSevenToZero: false, out var dayOfMonth, out error)
            || !TryParseCronField(fields[3], 1, 12, MonthAliases, normalizeSevenToZero: false, out var month, out error)
            || !TryParseCronField(fields[4], 0, 7, DayAliases, normalizeSevenToZero: true, out var dayOfWeek, out error))
        {
            return false;
        }

        cron = new CronSchedule(minute, hour, dayOfMonth, month, dayOfWeek);
        return true;
    }

    private static bool TryParseCronField(
        string field,
        int min,
        int max,
        IReadOnlyDictionary<string, int>? aliases,
        bool normalizeSevenToZero,
        out CronField result,
        out string error)
    {
        result = default;
        error = "";
        var values = new HashSet<int>();
        var wildcard = false;

        foreach (var rawPart in field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart;
            var step = 1;
            var slashIndex = part.IndexOf('/');
            if (slashIndex >= 0)
            {
                if (!int.TryParse(part[(slashIndex + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1)
                {
                    error = $"Invalid cron step in '{field}'.";
                    return false;
                }

                part = part[..slashIndex];
            }

            int start;
            int end;
            if (part == "*")
            {
                wildcard = true;
                start = min;
                end = max;
            }
            else if (part.Contains('-'))
            {
                var range = part.Split('-', StringSplitOptions.TrimEntries);
                if (range.Length != 2
                    || !TryParseCronValue(range[0], aliases, out start)
                    || !TryParseCronValue(range[1], aliases, out end))
                {
                    error = $"Invalid cron range '{part}'.";
                    return false;
                }
            }
            else if (TryParseCronValue(part, aliases, out start))
            {
                end = start;
            }
            else
            {
                error = $"Invalid cron value '{part}'.";
                return false;
            }

            if (normalizeSevenToZero && start == 7)
                start = 0;
            if (normalizeSevenToZero && end == 7)
                end = 0;

            if (start < min || start > max || end < min || end > max)
            {
                error = $"Cron value '{part}' is outside {min}-{max}.";
                return false;
            }

            if (start > end && !(normalizeSevenToZero && start > 0 && end == 0))
            {
                error = $"Cron range '{part}' is reversed.";
                return false;
            }

            if (normalizeSevenToZero && start > end)
            {
                AddCronValues(values, start, max, step, normalizeSevenToZero);
                AddCronValues(values, min, end, step, normalizeSevenToZero);
            }
            else
            {
                AddCronValues(values, start, end, step, normalizeSevenToZero);
            }
        }

        result = new CronField(values, wildcard);
        return values.Count > 0;
    }

    private static void AddCronValues(HashSet<int> values, int start, int end, int step, bool normalizeSevenToZero)
    {
        for (var value = start; value <= end; value += step)
            values.Add(normalizeSevenToZero && value == 7 ? 0 : value);
    }

    private static bool TryParseCronValue(string value, IReadOnlyDictionary<string, int>? aliases, out int result)
    {
        if (aliases?.TryGetValue(value.Trim().ToLowerInvariant(), out result) == true)
            return true;

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }

    private static string FormatInterval(int minutes)
    {
        if (minutes % (24 * 60) == 0)
        {
            var days = minutes / (24 * 60);
            return days == 1 ? "day" : $"{days} days";
        }

        if (minutes % 60 == 0)
        {
            var hours = minutes / 60;
            return hours == 1 ? "hour" : $"{hours} hours";
        }

        return minutes == 1 ? "minute" : $"{minutes} minutes";
    }

    private static readonly IReadOnlyDictionary<string, int> MonthAliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["feb"] = 2, ["mar"] = 3, ["apr"] = 4, ["may"] = 5, ["jun"] = 6,
        ["jul"] = 7, ["aug"] = 8, ["sep"] = 9, ["oct"] = 10, ["nov"] = 11, ["dec"] = 12
    };

    private static readonly IReadOnlyDictionary<string, int> DayAliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["sun"] = 0, ["mon"] = 1, ["tue"] = 2, ["wed"] = 3, ["thu"] = 4, ["fri"] = 5, ["sat"] = 6
    };

    private readonly record struct CronField(HashSet<int> Values, bool IsWildcard)
    {
        public bool Matches(int value) => Values.Contains(value);
    }

    private readonly record struct CronSchedule(
        CronField Minute,
        CronField Hour,
        CronField DayOfMonth,
        CronField Month,
        CronField DayOfWeek)
    {
        public bool Matches(DateTime local)
        {
            if (!Minute.Matches(local.Minute) || !Hour.Matches(local.Hour) || !Month.Matches(local.Month))
                return false;

            var dayOfMonthMatches = DayOfMonth.Matches(local.Day);
            var dayOfWeekMatches = DayOfWeek.Matches((int)local.DayOfWeek);
            var dayMatches = !DayOfMonth.IsWildcard && !DayOfWeek.IsWildcard
                ? dayOfMonthMatches || dayOfWeekMatches
                : dayOfMonthMatches && dayOfWeekMatches;

            return dayMatches;
        }
    }
}
