using VpsBackupManager.Data;
using VpsBackupManager.Security;

namespace VpsBackupManager.Services;

/// <summary>Per-run event timeline shown in the panel, mirrored to the structured application log.</summary>
public sealed class EventLog(Db db, ILogger<EventLog> logger)
{
    public async Task AddAsync(string? runId, string? backupId, string level, string message)
    {
        message = Redactor.Truncate(message, 4000);
        using (logger.BeginScope(new Dictionary<string, object?> { ["RunId"] = runId, ["BackupId"] = backupId }))
        {
            var lvl = level switch { "error" => LogLevel.Error, "warning" => LogLevel.Warning, _ => LogLevel.Information };
            logger.Log(lvl, "{Message}", message);
        }
        try
        {
            await db.ExecuteAsync(
                "INSERT INTO events(run_id, backup_id, level, message, created_at) VALUES (@runId, @backupId, @level, @message, @now)",
                new { runId, backupId, level, message, now = Clock.NowMs() });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao gravar evento no banco interno");
        }
    }

    public Task Info(string? runId, string? backupId, string message) => AddAsync(runId, backupId, "info", message);
    public Task Warn(string? runId, string? backupId, string message) => AddAsync(runId, backupId, "warning", message);
    public Task Error(string? runId, string? backupId, string message) => AddAsync(runId, backupId, "error", message);

    /// <summary>Keeps the events table bounded (default: 90 days).</summary>
    public Task PruneAsync(int days = 90) =>
        db.ExecuteAsync("DELETE FROM events WHERE created_at < @limit",
            new { limit = Clock.NowMs() - (long)TimeSpan.FromDays(days).TotalMilliseconds });
}
