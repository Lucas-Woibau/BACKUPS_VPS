using System.Text.Json;
using VpsBackupManager.Data;
using VpsBackupManager.Providers;
using VpsBackupManager.Services;

namespace VpsBackupManager.Scheduling;

public sealed record ScheduleInput(
    string Name,
    bool Enabled,
    string Kind,
    List<int>? DaysOfWeek,
    List<string>? Times,
    int? IntervalMinutes,
    string TargetType,
    long? TargetConnectionId,
    string? TargetDatabase);

/// <summary>CRUD for schedules. Schedules live in SQLite, so restarts never lose them.</summary>
public sealed class ScheduleService(Db db, ConnectionService connections)
{
    public Task<IReadOnlyList<ScheduleRow>> ListAsync() => db.QueryAsync<ScheduleRow>("SELECT * FROM schedules ORDER BY name");

    public Task<ScheduleRow?> GetAsync(long id) => db.QueryOneAsync<ScheduleRow>("SELECT * FROM schedules WHERE id=@id", new { id });

    public async Task<long> CreateAsync(ScheduleInput input)
    {
        var (days, times) = await ValidateAsync(input);
        var now = Clock.NowMs();
        return await db.InsertAsync(
            """
            INSERT INTO schedules(name, enabled, kind, days_of_week, times, interval_minutes, target_type, target_connection_id,
              target_database, last_triggered_at, created_at, updated_at)
            VALUES (@Name, @Enabled, @Kind, @days, @times, @IntervalMinutes, @TargetType, @TargetConnectionId, @TargetDatabase, @now, @now, @now)
            """,
            new
            {
                Name = input.Name.Trim(), input.Enabled, input.Kind, days, times, input.IntervalMinutes, input.TargetType,
                TargetConnectionId = input.TargetType == "all" ? null : input.TargetConnectionId,
                TargetDatabase = input.TargetType == "database" ? input.TargetDatabase : null, now,
            });
    }

    public async Task UpdateAsync(long id, ScheduleInput input)
    {
        var (days, times) = await ValidateAsync(input);
        _ = await GetAsync(id) ?? throw new KeyNotFoundException();
        var now = Clock.NowMs();
        // last_triggered_at = now: an edited schedule starts counting from the edit (no surprise immediate run).
        await db.ExecuteAsync(
            """
            UPDATE schedules SET name=@Name, enabled=@Enabled, kind=@Kind, days_of_week=@days, times=@times,
              interval_minutes=@IntervalMinutes, target_type=@TargetType, target_connection_id=@TargetConnectionId,
              target_database=@TargetDatabase, last_triggered_at=@now, updated_at=@now
            WHERE id=@id
            """,
            new
            {
                id, Name = input.Name.Trim(), input.Enabled, input.Kind, days, times, input.IntervalMinutes, input.TargetType,
                TargetConnectionId = input.TargetType == "all" ? null : input.TargetConnectionId,
                TargetDatabase = input.TargetType == "database" ? input.TargetDatabase : null, now,
            });
    }

    public Task DeleteAsync(long id) => db.ExecuteAsync("DELETE FROM schedules WHERE id=@id", new { id });

    public Task MarkTriggeredAsync(long id, long at) =>
        db.ExecuteAsync("UPDATE schedules SET last_triggered_at=@at WHERE id=@id", new { id, at });

    public static DateTimeOffset? NextRun(ScheduleRow row, TimeZoneInfo tz)
    {
        if (!row.Enabled) return null;
        var baseline = Clock.FromMs(row.LastTriggeredAt ?? row.CreatedAt);
        return ScheduleCalculator.Next(ScheduleSpec.FromRow(row), baseline, tz);
    }

    public static ScheduleSpec ToSpec(ScheduleInput input) => new(
        input.Kind,
        (input.DaysOfWeek ?? []).Select(d => (DayOfWeek)d).ToList(),
        (input.Times ?? []).Select(ScheduleSpec.ParseTime).ToList(),
        input.IntervalMinutes);

    private async Task<(string Days, string Times)> ValidateAsync(ScheduleInput input)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 80) errors.Add("Nome obrigatório (máx. 80).");
        if ((input.DaysOfWeek ?? []).Any(d => d is < 0 or > 6)) errors.Add("Dia da semana inválido.");
        List<string> times = [];
        try
        {
            times = (input.Times ?? []).Select(t => ScheduleSpec.ParseTime(t).ToString("HH:mm")).Distinct().Order().ToList();
        }
        catch (FormatException)
        {
            errors.Add("Horário inválido (use HH:mm).");
        }
        if (errors.Count == 0) errors.AddRange(ToSpec(input).Validate());
        if (input.TargetType is not ("all" or "connection" or "database")) errors.Add("Alvo inválido.");
        if (input.TargetType is "connection" or "database")
        {
            if (input.TargetConnectionId is null || await connections.GetAsync(input.TargetConnectionId.Value) is null)
                errors.Add("Conexão alvo não encontrada.");
        }
        if (input.TargetType == "database")
        {
            try { ProviderHelpers.ValidateDatabaseName(input.TargetDatabase ?? ""); }
            catch (ProviderException ex) { errors.Add(ex.Message); }
        }
        if (errors.Count > 0) throw new ValidationException(errors);
        var days = input.Kind == "weekly" ? (input.DaysOfWeek ?? []).Distinct().Order().ToList() : [];
        return (JsonSerializer.Serialize(days), JsonSerializer.Serialize(input.Kind == "weekly" ? times : []));
    }
}
