using VpsBackupManager.Backup;
using VpsBackupManager.Scheduling;
using VpsBackupManager.Services;

namespace VpsBackupManager.Tests;

public class RetentionPolicyTests
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static List<RetentionItem> Daily(int days) =>
        Enumerable.Range(0, days).Select(i => new RetentionItem($"d{i}", Now.AddDays(-i).AddHours(-6))).ToList();

    [Fact]
    public void Days_policy_deletes_older_than_limit()
    {
        var items = Daily(40);
        var del = RetentionPolicy.SelectForDeletion(items, new AppSettings { RetentionMode = "days", RetentionDays = 30 }, Now, Tz);
        Assert.Equal(10, del.Count);
        Assert.All(del, id => Assert.True(int.Parse(id[1..]) >= 30));
    }

    [Fact]
    public void Count_policy_keeps_newest_n()
    {
        var del = RetentionPolicy.SelectForDeletion(Daily(10), new AppSettings { RetentionMode = "count", RetentionCount = 3 }, Now, Tz);
        Assert.Equal(7, del.Count);
        Assert.DoesNotContain("d0", del);
        Assert.DoesNotContain("d2", del);
        Assert.Contains("d3", del);
    }

    [Fact]
    public void Newest_backup_is_never_deleted_even_if_expired()
    {
        var items = new List<RetentionItem> { new("old1", Now.AddDays(-100)), new("old2", Now.AddDays(-90)) };
        var del = RetentionPolicy.SelectForDeletion(items, new AppSettings { RetentionMode = "days", RetentionDays = 7 }, Now, Tz);
        Assert.Equal(["old1"], del);
    }

    [Fact]
    public void None_policy_and_single_item_delete_nothing()
    {
        Assert.Empty(RetentionPolicy.SelectForDeletion(Daily(50), new AppSettings { RetentionMode = "none" }, Now, Tz));
        Assert.Empty(RetentionPolicy.SelectForDeletion(Daily(1), new AppSettings { RetentionMode = "count", RetentionCount = 1 }, Now, Tz));
    }

    [Fact]
    public void Gfs_keeps_daily_weekly_monthly()
    {
        var items = Daily(400);
        var s = new AppSettings { RetentionMode = "gfs", GfsDaily = 7, GfsWeekly = 4, GfsMonthly = 12 };
        var del = RetentionPolicy.SelectForDeletion(items, s, Now, Tz);
        var kept = items.Select(i => i.Id).Except(del).ToList();
        // 7 daily + up to 4 weekly + 12 monthly, with overlaps
        Assert.InRange(kept.Count, 12, 23);
        for (var i = 0; i < 7; i++) Assert.Contains($"d{i}", kept);
        Assert.DoesNotContain("d399", kept);
    }

    [Fact]
    public void Multiple_backups_same_day_keep_only_newest_for_gfs_daily()
    {
        var items = new List<RetentionItem>
        {
            new("a", Now.AddHours(-1)), new("b", Now.AddHours(-2)), new("c", Now.AddDays(-1)),
        };
        var del = RetentionPolicy.SelectForDeletion(items, new AppSettings { RetentionMode = "gfs", GfsDaily = 2, GfsWeekly = 0, GfsMonthly = 0 }, Now, Tz);
        Assert.Equal(["b"], del);
    }
}

public class ScheduleCalculatorTests
{
    private static readonly TimeZoneInfo Sp = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
    private static ScheduleSpec Weekly(int[] days, params string[] times) =>
        new("weekly", days.Select(d => (DayOfWeek)d).ToList(), times.Select(ScheduleSpec.ParseTime).ToList(), null);

    [Fact]
    public void Daily_at_three_in_sao_paulo_is_six_utc()
    {
        var after = new DateTimeOffset(2026, 10, 1, 4, 0, 0, TimeSpan.Zero); // 01:00 local
        var next = ScheduleCalculator.Next(Weekly([0, 1, 2, 3, 4, 5, 6], "03:00"), after, Sp);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 6, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void After_todays_time_moves_to_next_day()
    {
        var after = new DateTimeOffset(2026, 10, 1, 6, 0, 0, TimeSpan.Zero); // exactly 03:00 local -> strictly after
        var next = ScheduleCalculator.Next(Weekly([0, 1, 2, 3, 4, 5, 6], "03:00"), after, Sp);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 6, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Mon_wed_fri_at_0230()
    {
        // 2026-10-01 is a Thursday
        var after = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var spec = Weekly([1, 3, 5], "02:30");
        var next3 = ScheduleCalculator.NextN(spec, after, Sp, 3);
        Assert.Equal(DayOfWeek.Friday, TimeZoneInfo.ConvertTime(next3[0], Sp).DayOfWeek);
        Assert.Equal(DayOfWeek.Monday, TimeZoneInfo.ConvertTime(next3[1], Sp).DayOfWeek);
        Assert.Equal(DayOfWeek.Wednesday, TimeZoneInfo.ConvertTime(next3[2], Sp).DayOfWeek);
        Assert.All(next3, n => Assert.Equal(new TimeOnly(2, 30), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(n, Sp).DateTime)));
    }

    [Fact]
    public void Multiple_times_per_day()
    {
        var after = new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.Zero); // 04:00 local
        var next = ScheduleCalculator.NextN(Weekly([4], "03:00", "15:30"), after, Sp, 2);
        Assert.Equal(new TimeOnly(15, 30), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(next[0], Sp).DateTime));
        var second = TimeZoneInfo.ConvertTime(next[1], Sp);
        Assert.Equal(new DateTime(2026, 10, 8, 3, 0, 0), second.DateTime); // next Thursday 03:00
    }

    [Fact]
    public void Interval_adds_minutes()
    {
        var after = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(after.AddMinutes(90), ScheduleCalculator.Next(new ScheduleSpec("interval", [], [], 90), after, Sp));
    }

    [Fact]
    public void Dst_gap_time_is_moved_forward_not_skipped()
    {
        var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        // 2026-03-08 02:30 does not exist in New York
        var after = new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero);
        var next = ScheduleCalculator.Next(Weekly([0], "02:30"), after, ny);
        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 3, 8), TimeZoneInfo.ConvertTime(next!.Value, ny).Date);
    }

    [Fact]
    public void Validation_rejects_bad_specs()
    {
        Assert.NotEmpty(Weekly([], "03:00").Validate());
        Assert.NotEmpty(Weekly([1]).Validate());
        Assert.NotEmpty(new ScheduleSpec("interval", [], [], 5).Validate());
        Assert.NotEmpty(new ScheduleSpec("cron", [], [], null).Validate());
        Assert.Empty(Weekly([1], "03:00").Validate());
        Assert.Throws<FormatException>(() => ScheduleSpec.ParseTime("25:00"));
    }
}
