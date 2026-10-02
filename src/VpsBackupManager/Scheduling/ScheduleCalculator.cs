using System.Globalization;
using System.Text.Json;
using VpsBackupManager.Data;

namespace VpsBackupManager.Scheduling;

public sealed record ScheduleSpec(string Kind, IReadOnlyList<DayOfWeek> Days, IReadOnlyList<TimeOnly> Times, int? IntervalMinutes)
{
    public static ScheduleSpec FromRow(ScheduleRow row) => new(
        row.Kind,
        JsonSerializer.Deserialize<List<int>>(row.DaysOfWeek)!.Select(d => (DayOfWeek)d).ToList(),
        JsonSerializer.Deserialize<List<string>>(row.Times)!.Select(ParseTime).ToList(),
        (int?)row.IntervalMinutes);

    public static TimeOnly ParseTime(string s) =>
        TimeOnly.ParseExact(s.Trim(), "HH:mm", CultureInfo.InvariantCulture);

    public List<string> Validate()
    {
        var e = new List<string>();
        if (Kind == "weekly")
        {
            if (Days.Count == 0) e.Add("Selecione ao menos um dia da semana.");
            if (Times.Count == 0) e.Add("Informe ao menos um horário.");
            if (Times.Count > 24) e.Add("Máximo de 24 horários por agenda.");
        }
        else if (Kind == "interval")
        {
            if (IntervalMinutes is null or < 15 or > 60 * 24 * 31) e.Add("Intervalo deve estar entre 15 minutos e 31 dias.");
        }
        else e.Add("Tipo de agenda inválido (weekly | interval).");
        return e;
    }
}

/// <summary>
/// Computes fire times in the configured timezone (DST-aware). Pure functions — unit tested.
/// </summary>
public static class ScheduleCalculator
{
    /// <summary>First fire time strictly after <paramref name="afterUtc"/>.</summary>
    public static DateTimeOffset? Next(ScheduleSpec spec, DateTimeOffset afterUtc, TimeZoneInfo tz)
    {
        if (spec.Kind == "interval")
            return spec.IntervalMinutes is > 0 ? afterUtc.AddMinutes(spec.IntervalMinutes.Value) : null;

        if (spec.Days.Count == 0 || spec.Times.Count == 0) return null;
        var localAfter = TimeZoneInfo.ConvertTime(afterUtc, tz);
        var times = spec.Times.Distinct().OrderBy(t => t).ToList();
        for (var dayOffset = 0; dayOffset <= 8; dayOffset++)
        {
            var date = DateOnly.FromDateTime(localAfter.Date).AddDays(dayOffset);
            if (!spec.Days.Contains(date.DayOfWeek)) continue;
            foreach (var t in times)
            {
                var candidate = ToUtc(date.ToDateTime(t), tz);
                if (candidate > afterUtc) return candidate;
            }
        }
        return null;
    }

    public static IReadOnlyList<DateTimeOffset> NextN(ScheduleSpec spec, DateTimeOffset afterUtc, TimeZoneInfo tz, int n)
    {
        var list = new List<DateTimeOffset>();
        var cursor = afterUtc;
        for (var i = 0; i < n; i++)
        {
            var next = Next(spec, cursor, tz);
            if (next is null) break;
            list.Add(next.Value);
            cursor = next.Value;
        }
        return list;
    }

    /// <summary>Local wall-clock → UTC. Nonexistent times (DST gap) move forward; ambiguous take the first.</summary>
    public static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo tz)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (tz.IsInvalidTime(local)) local = local.AddMinutes(30);
        var offset = tz.IsAmbiguousTime(local) ? tz.GetAmbiguousTimeOffsets(local).Max() : tz.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
