using System.Collections.Concurrent;
using VpsBackupManager.Backup;
using VpsBackupManager.Data;
using VpsBackupManager.Security;
using VpsBackupManager.Services;

namespace VpsBackupManager.Scheduling;

/// <summary>
/// In-process scheduler (BackgroundService). Every 20 s it compares each schedule's next fire time
/// (computed from last_triggered_at stored in SQLite) with now.
///
/// Why not cron/systemd timers: the app runs in a container and schedules are edited in the panel;
/// keeping them in the app DB means one source of truth, restarts lose nothing, and missed runs
/// (VPS down at 03:00) are recovered within <c>CatchUpHours</c>.
/// </summary>
public sealed class SchedulerService(
    IServiceProvider services,
    BackupOrchestrator orchestrator,
    ILogger<SchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(20);
    private readonly ConcurrentDictionary<long, byte> _inFlight = new();
    private DateTimeOffset _lastMaintenance = DateTimeOffset.MinValue;

    public DateTimeOffset LastTick { get; private set; } = DateTimeOffset.MinValue;

    public bool Healthy => Clock.UtcNow() - LastTick < TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        orchestrator.AttachShutdownToken(stoppingToken);
        logger.LogInformation("Scheduler iniciado");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
                if (Clock.UtcNow() - _lastMaintenance > TimeSpan.FromHours(1)) await MaintenanceAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Erro no ciclo do scheduler");
            }
            LastTick = Clock.UtcNow();
            try { await Task.Delay(TickInterval, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var schedules = scope.ServiceProvider.GetRequiredService<ScheduleService>();
        var events = scope.ServiceProvider.GetRequiredService<EventLog>();
        var settings = await settingsService.GetAsync();
        var tz = SettingsService.ResolveTimeZone(settings.Timezone);
        var now = Clock.UtcNow();

        foreach (var row in await schedules.ListAsync())
        {
            if (!row.Enabled) continue;
            DateTimeOffset? due;
            try { due = ScheduleService.NextRun(row, tz); }
            catch (Exception ex) { logger.LogWarning("Agenda {Id} inválida: {Error}", row.Id, ex.Message); continue; }
            if (due is null || due > now) continue;

            if (!_inFlight.TryAdd(row.Id, 0))
            {
                // Still running the previous occurrence: coalesce (skip this one).
                await schedules.MarkTriggeredAsync(row.Id, now.ToUnixTimeMilliseconds());
                await events.Warn(null, null, $"Agenda '{row.Name}': ocorrência {due:u} ignorada, execução anterior ainda em andamento.");
                continue;
            }

            await schedules.MarkTriggeredAsync(row.Id, now.ToUnixTimeMilliseconds());
            var late = now - due.Value;
            if (late > TimeSpan.FromHours(settings.CatchUpHours) && late > TimeSpan.FromMinutes(2))
            {
                _inFlight.TryRemove(row.Id, out _);
                await events.Warn(null, null,
                    $"Agenda '{row.Name}': ocorrência de {due:u} perdida há {late.TotalHours:0.#} h (fora da janela de recuperação de {settings.CatchUpHours} h).");
                continue;
            }

            var trigger = late > TimeSpan.FromMinutes(2) ? "catchup" : "schedule";
            var target = new RunTarget(row.TargetType, row.TargetConnectionId, row.TargetDatabase);
            logger.LogInformation("Disparando agenda {Name} ({Trigger})", row.Name, trigger);
            _ = Task.Run(async () =>
            {
                try { await orchestrator.RunScheduledAsync(row.Id, trigger, target); }
                catch (Exception ex) { logger.LogError(ex, "Falha ao executar agenda {Name}", row.Name); }
                finally { _inFlight.TryRemove(row.Id, out _); }
            }, CancellationToken.None);
        }
    }

    private async Task MaintenanceAsync()
    {
        _lastMaintenance = Clock.UtcNow();
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SessionService>().PurgeExpiredAsync();
        await scope.ServiceProvider.GetRequiredService<EventLog>().PruneAsync();
        var settings = await scope.ServiceProvider.GetRequiredService<SettingsService>().GetAsync();
        if (orchestrator.CurrentRunId is null) await orchestrator.CleanupLocalCopiesAsync(null, settings);
    }
}
