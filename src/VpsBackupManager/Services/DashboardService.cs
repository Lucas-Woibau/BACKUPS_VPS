using System.Net;
using System.Net.Sockets;
using VpsBackupManager.Backup;
using VpsBackupManager.Config;
using VpsBackupManager.Data;
using VpsBackupManager.Scheduling;

namespace VpsBackupManager.Services;

public sealed class DashboardService(
    Db db,
    AppOptions options,
    SettingsService settingsService,
    ConnectionService connections,
    ScheduleService schedules,
    BackupOrchestrator orchestrator,
    SchedulerService scheduler)
{
    public async Task<object> GetAsync()
    {
        var settings = await settingsService.GetAsync();
        var tz = SettingsService.ResolveTimeZone(settings.Timezone);
        var since24h = Clock.NowMs() - (long)TimeSpan.FromHours(24).TotalMilliseconds;

        var lastAttempt = await db.QueryOneAsync<RunRow>("SELECT * FROM runs ORDER BY started_at DESC LIMIT 1");
        var lastSuccess = await db.QueryOneAsync<BackupRow>(
            "SELECT * FROM backups WHERE status IN ('success','warning') ORDER BY finished_at DESC LIMIT 1");
        var lastUpload = await db.QueryOneAsync<long?>(
            "SELECT MAX(finished_at) FROM backups WHERE remote_path IS NOT NULL AND status IN ('success','warning')");
        var counts = await db.QueryAsync<(string Status, long Count)>(
            "SELECT status, COUNT(*) FROM backups WHERE created_at >= @since24h GROUP BY status", new { since24h });
        long Count(string s) => counts.Where(c => c.Status == s).Sum(c => c.Count);

        // Consecutive failures since the last success (per database) — the key "is something broken?" signal.
        var failingSinceLastSuccess = await db.QueryAsync<(string ConnectionName, string DatabaseName, long Failures)>(
            """
            SELECT b.connection_name, b.database_name, COUNT(*) FROM backups b
            WHERE b.status = 'error' AND b.created_at > COALESCE(
              (SELECT MAX(s.finished_at) FROM backups s WHERE s.connection_id IS b.connection_id
                 AND s.database_name = b.database_name AND s.status IN ('success','warning')), 0)
            GROUP BY b.connection_id, b.database_name ORDER BY 3 DESC LIMIT 20
            """);

        var connectionRows = await connections.ListAsync();
        var nextRuns = (await schedules.ListAsync())
            .Select(s => (Schedule: s, Next: ScheduleService.NextRun(s, tz)))
            .Where(x => x.Next is not null).OrderBy(x => x.Next).ToList();

        var drive = await settingsService.GetDriveStatusAsync();
        var disk = DiskInfo(options.BackupRoot);
        var lastSuccessAgeHours = lastSuccess?.FinishedAt is { } f ? (Clock.NowMs() - f) / 3_600_000.0 : (double?)null;

        var overall = "online";
        if (failingSinceLastSuccess.Count > 0 || lastAttempt?.Status == "error") overall = "atencao";
        if (lastSuccessAgeHours is null or > 48 && connectionRows.Any(c => c.Enabled)) overall = "atencao";
        if (!scheduler.Healthy) overall = "degradado";

        return new
        {
            status = overall,
            vpsName = settings.VpsName,
            timezone = settings.Timezone,
            running = orchestrator.CurrentRunId,
            lastAttempt = lastAttempt is null ? null : new
            {
                lastAttempt.Id, lastAttempt.Status, lastAttempt.Message, lastAttempt.Trigger,
                startedAt = Clock.IsoFromMs(lastAttempt.StartedAt), finishedAt = Clock.IsoFromMs(lastAttempt.FinishedAt),
            },
            lastSuccess = lastSuccess is null ? null : new
            {
                lastSuccess.ConnectionName, lastSuccess.DatabaseName, finishedAt = Clock.IsoFromMs(lastSuccess.FinishedAt),
                lastSuccess.RemotePath, ageHours = lastSuccessAgeHours,
            },
            nextRun = nextRuns.Count == 0 ? null : new
            {
                at = nextRuns[0].Next!.Value.ToString("O"), schedule = nextRuns[0].Schedule.Name,
            },
            protectedDatabases = connectionRows.Sum(ConnectionService.ProtectedCount),
            connections = connectionRows.Count(c => c.Enabled),
            last24h = new
            {
                total = counts.Sum(c => c.Count),
                success = Count(BackupStatus.Success),
                warning = Count(BackupStatus.Warning),
                error = Count(BackupStatus.Error),
            },
            failing = failingSinceLastSuccess.Select(f => new { f.ConnectionName, f.DatabaseName, f.Failures }),
            disk,
            drive = new
            {
                ok = drive.Ok, checkedAt = Clock.IsoFromMs(drive.CheckedAt), message = drive.Message,
                lastUpload = Clock.IsoFromMs(lastUpload), destination = $"{settings.RcloneRemote}:{settings.RemoteBasePath}",
            },
            schedulerHealthy = scheduler.Healthy,
            encryptionEnabled = settings.EncryptionEnabled,
        };
    }

    public static object DiskInfo(string path)
    {
        try
        {
            var d = new DriveInfo(Path.GetFullPath(path));
            return new { path, totalBytes = d.TotalSize, freeBytes = d.AvailableFreeSpace, usedPercent = 100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize };
        }
        catch (Exception ex)
        {
            return new { path, error = ex.Message };
        }
    }

    public static async Task<object> SystemInfoAsync(AppOptions options)
    {
        var tools = new Dictionary<string, string?>();
        foreach (var (name, args) in new[]
                 {
                     ("mariadb-dump", "--version"), ("mysqldump", "--version"), ("pg_dump", "--version"),
                     ("pg_dumpall", "--version"), ("rclone", "version"), ("age", "--version"),
                 })
        {
            if (!ProcessRunner.CommandExists(name)) { tools[name] = null; continue; }
            var r = await ProcessRunner.RunAsync(name, [args], timeout: TimeSpan.FromSeconds(10));
            tools[name] = (r.Stdout + r.Stderr).Split('\n').FirstOrDefault()?.Trim();
        }
        string? ip = null;
        try
        {
            ip = (await Dns.GetHostAddressesAsync(Dns.GetHostName()))
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        }
        catch { /* informative only */ }
        return new
        {
            hostname = Environment.GetEnvironmentVariable("VPS_HOSTNAME") ?? Environment.MachineName,
            containerIp = ip,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            dotnet = Environment.Version.ToString(),
            appVersion = typeof(DashboardService).Assembly.GetName().Version?.ToString(),
            backupRoot = options.BackupRoot,
            rcloneConfigPresent = File.Exists(options.RcloneConfig),
            dockerDiscovery = !string.IsNullOrEmpty(options.DockerDiscoveryUrl),
            tools,
            disk = DiskInfo(options.BackupRoot),
        };
    }
}
