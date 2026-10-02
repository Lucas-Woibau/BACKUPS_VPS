using System.Globalization;
using VpsBackupManager.Services;

namespace VpsBackupManager.Backup;

public sealed record RetentionItem(string Id, DateTimeOffset FinishedAt);

/// <summary>
/// Pure retention logic (no I/O) applied per (connection, database) group.
/// Safety rule: the newest successful backup of a group is never selected for deletion.
/// </summary>
public static class RetentionPolicy
{
    public static IReadOnlyList<string> SelectForDeletion(IReadOnlyList<RetentionItem> items, AppSettings s,
        DateTimeOffset now, TimeZoneInfo tz)
    {
        if (items.Count <= 1 || s.RetentionMode == "none") return [];
        var ordered = items.OrderByDescending(i => i.FinishedAt).ToList();
        var keep = new HashSet<string> { ordered[0].Id };

        switch (s.RetentionMode)
        {
            case "days":
                var limit = now.AddDays(-s.RetentionDays);
                foreach (var i in ordered.Where(i => i.FinishedAt >= limit)) keep.Add(i.Id);
                break;
            case "count":
                foreach (var i in ordered.Take(Math.Max(1, s.RetentionCount))) keep.Add(i.Id);
                break;
            case "gfs":
                KeepNewestPerBucket(ordered, s.GfsDaily, i => Local(i, tz).ToString("yyyy-MM-dd"), keep);
                KeepNewestPerBucket(ordered, s.GfsWeekly, i =>
                {
                    var d = Local(i, tz);
                    return $"{ISOWeek.GetYear(d)}-W{ISOWeek.GetWeekOfYear(d):00}";
                }, keep);
                KeepNewestPerBucket(ordered, s.GfsMonthly, i => Local(i, tz).ToString("yyyy-MM"), keep);
                break;
            default:
                return [];
        }
        return ordered.Where(i => !keep.Contains(i.Id)).Select(i => i.Id).ToList();
    }

    /// <summary>Walks newest → oldest keeping the first item of each bucket, for the newest N buckets.</summary>
    private static void KeepNewestPerBucket(List<RetentionItem> ordered, int buckets, Func<RetentionItem, string> key, HashSet<string> keep)
    {
        if (buckets <= 0) return;
        var seen = new HashSet<string>();
        foreach (var item in ordered)
        {
            var k = key(item);
            if (seen.Contains(k)) continue;
            if (seen.Count >= buckets) break;
            seen.Add(k);
            keep.Add(item.Id);
        }
    }

    private static DateTime Local(RetentionItem i, TimeZoneInfo tz) => TimeZoneInfo.ConvertTime(i.FinishedAt, tz).DateTime;
}
