using System.Text.Json;
using VpsBackupManager.Backup;
using VpsBackupManager.Config;
using VpsBackupManager.Data;
using VpsBackupManager.Notifications;
using VpsBackupManager.Providers;
using VpsBackupManager.Scheduling;
using VpsBackupManager.Services;
using VpsBackupManager.Storage;

namespace VpsBackupManager.Api;

public sealed record RunRequest(string? TargetType, long? ConnectionId, string? Database);
public sealed record WebhookSecretRequest(string? Secret);
public sealed record DriveConnectRequest(string? Email);
public sealed record DriveCompleteRequest(string? Url);
public sealed record SchedulePreviewRequest(string Kind, List<int>? DaysOfWeek, List<string>? Times, int? IntervalMinutes);

public static class ApiEndpoints
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        // ------------------------------------------------------------------ health (public)
        api.MapGet("/health", (Db db, SchedulerService scheduler, AppOptions options) =>
        {
            var workDirOk = false;
            try
            {
                var probe = Path.Combine(options.WorkDir, ".health");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                workDirOk = true;
            }
            catch { /* reported below */ }
            var checks = new { app = true, database = db.Ping(), scheduler = scheduler.Healthy, workDir = workDirOk };
            var ok = checks.database && checks.scheduler && checks.workDir;
            return Results.Json(new { status = ok ? "ok" : "fail", checks }, statusCode: ok ? 200 : 503);
        });

        // ------------------------------------------------------------------ dashboard / system
        api.MapGet("/dashboard", (DashboardService d) => d.GetAsync());
        api.MapGet("/system", (AppOptions o) => DashboardService.SystemInfoAsync(o));
        api.MapGet("/discovery", (DiscoveryService d, CancellationToken ct) => d.DiscoverAsync(ct));
        api.MapGet("/logs", (AppOptions o, int? lines) => Results.Ok(LogReader.Tail(o.LogDir, lines ?? 200)));
        api.MapGet("/events", async (Db db, int? limit, string? level) =>
        {
            var rows = await db.QueryAsync<EventRow>(
                "SELECT * FROM events WHERE (@level IS NULL OR level=@level) ORDER BY id DESC LIMIT @limit",
                new { level, limit = Math.Clamp(limit ?? 200, 1, 2000) });
            return rows.Select(EventDto);
        });

        // ------------------------------------------------------------------ connections
        api.MapGet("/connections/types", (ProviderRegistry r) => r.SupportedTypes.Select(t => new
        {
            type = t, defaultPort = r.Get(t).DefaultPort, family = r.Get(t).Family,
        }));
        api.MapGet("/connections", async (ConnectionService s) => (await s.ListAsync()).Select(ConnectionDto));
        api.MapGet("/connections/{id:long}", async (long id, ConnectionService s) =>
            await s.GetAsync(id) is { } row ? Results.Ok(ConnectionDto(row)) : Results.NotFound());
        api.MapPost("/connections", async (ConnectionInput input, ConnectionService s) =>
        {
            var id = await s.CreateAsync(input);
            return Results.Created($"/api/connections/{id}", ConnectionDto((await s.GetAsync(id))!));
        });
        api.MapPut("/connections/{id:long}", async (long id, ConnectionInput input, ConnectionService s) =>
        {
            await s.UpdateAsync(id, input);
            return Results.Ok(ConnectionDto((await s.GetAsync(id))!));
        });
        api.MapDelete("/connections/{id:long}", async (long id, ConnectionService s) =>
        {
            await s.DeleteAsync(id);
            return Results.NoContent();
        });

        // Test a form before saving (password optional when editing an existing connection).
        api.MapPost("/connections/test", async (ConnectionInput input, long? id, ConnectionService s, ProviderRegistry r, CancellationToken ct) =>
        {
            var info = await s.FromInputAsync(input, id);
            var result = await r.Get(info.DbType).TestConnectionAsync(info, ct);
            IReadOnlyList<string>? databases = null;
            if (result.Ok)
            {
                try { databases = await r.Get(info.DbType).ListDatabasesAsync(info, input.IncludeSystem, ct); }
                catch (ProviderException ex) { result = result with { Message = result.Message + " (listagem falhou: " + ex.Message + ")" }; }
            }
            return Results.Ok(new { result.Ok, result.Message, result.ServerVersion, databases });
        });
        api.MapPost("/connections/{id:long}/test", async (long id, ConnectionService s, ProviderRegistry r, CancellationToken ct) =>
        {
            var row = await s.GetAsync(id) ?? throw new KeyNotFoundException();
            var result = await r.Get(row.DbType).TestConnectionAsync(s.ToInfo(row), ct);
            await s.SaveTestResultAsync(id, result.Ok, result.Message);
            return Results.Ok(result);
        });
        api.MapGet("/connections/{id:long}/databases", async (long id, ConnectionService s, ProviderRegistry r, CancellationToken ct) =>
        {
            var row = await s.GetAsync(id) ?? throw new KeyNotFoundException();
            var onServer = await r.Get(row.DbType).ListDatabasesAsync(s.ToInfo(row), row.IncludeSystem, ct);
            await s.SaveDiscoveredAsync(id, onServer);
            var targets = ConnectionService.ResolveTargets(row, onServer);
            return Results.Ok(onServer.Select(name => new { name, selected = targets.Contains(name) }));
        });

        // ------------------------------------------------------------------ schedules
        api.MapGet("/schedules", async (ScheduleService s, SettingsService settings) =>
        {
            var tz = SettingsService.ResolveTimeZone((await settings.GetAsync()).Timezone);
            return (await s.ListAsync()).Select(row => ScheduleDto(row, tz));
        });
        api.MapPost("/schedules", async (ScheduleInput input, ScheduleService s) =>
        {
            var id = await s.CreateAsync(input);
            return Results.Created($"/api/schedules/{id}", new { id });
        });
        api.MapPut("/schedules/{id:long}", async (long id, ScheduleInput input, ScheduleService s) =>
        {
            await s.UpdateAsync(id, input);
            return Results.Ok(new { id });
        });
        api.MapDelete("/schedules/{id:long}", async (long id, ScheduleService s) =>
        {
            await s.DeleteAsync(id);
            return Results.NoContent();
        });
        api.MapPost("/schedules/preview", async (SchedulePreviewRequest req, SettingsService settings) =>
        {
            var tz = SettingsService.ResolveTimeZone((await settings.GetAsync()).Timezone);
            var spec = ScheduleService.ToSpec(new ScheduleInput("preview", true, req.Kind, req.DaysOfWeek, req.Times, req.IntervalMinutes, "all", null, null));
            var errors = spec.Validate();
            if (errors.Count > 0) throw new ValidationException(errors);
            return Results.Ok(ScheduleCalculator.NextN(spec, Clock.UtcNow(), tz, 5).Select(d => d.ToString("O")));
        });

        // ------------------------------------------------------------------ backups / runs
        api.MapPost("/backups/run", async (RunRequest req, BackupOrchestrator o) =>
        {
            var target = new RunTarget(req.TargetType ?? "all", req.ConnectionId, req.Database);
            var runId = await o.StartManualAsync(target);
            return Results.Accepted($"/api/runs/{runId}", new { runId });
        });
        api.MapGet("/backups", async (Db db, string? status, long? connectionId, string? q, int? limit, int? offset) =>
        {
            var rows = await db.QueryAsync<BackupRow>(
                """
                SELECT * FROM backups
                WHERE (@status IS NULL OR status=@status) AND (@connectionId IS NULL OR connection_id=@connectionId)
                  AND (@q IS NULL OR database_name LIKE '%' || @q || '%' OR connection_name LIKE '%' || @q || '%')
                ORDER BY created_at DESC LIMIT @limit OFFSET @offset
                """,
                new { status, connectionId, q = string.IsNullOrWhiteSpace(q) ? null : q, limit = Math.Clamp(limit ?? 50, 1, 500), offset = Math.Max(0, offset ?? 0) });
            return rows.Select(BackupDto);
        });
        api.MapGet("/backups/{id}", async (string id, Db db) =>
        {
            var row = await db.QueryOneAsync<BackupRow>("SELECT * FROM backups WHERE id=@id", new { id }) ?? throw new KeyNotFoundException();
            var events = await db.QueryAsync<EventRow>("SELECT * FROM events WHERE backup_id=@id ORDER BY id", new { id });
            return Results.Ok(new { backup = BackupDto(row), events = events.Select(EventDto) });
        });
        api.MapGet("/runs", async (Db db, int? limit) =>
            (await db.QueryAsync<RunRow>("SELECT * FROM runs ORDER BY started_at DESC LIMIT @l", new { l = Math.Clamp(limit ?? 30, 1, 500) }))
            .Select(RunDto));
        api.MapGet("/runs/current", (BackupOrchestrator o) => Results.Ok(new { runId = o.CurrentRunId }));
        api.MapGet("/runs/{id}", async (string id, Db db) =>
        {
            var run = await db.QueryOneAsync<RunRow>("SELECT * FROM runs WHERE id=@id", new { id }) ?? throw new KeyNotFoundException();
            var items = await db.QueryAsync<BackupRow>("SELECT * FROM backups WHERE run_id=@id ORDER BY created_at", new { id });
            var events = await db.QueryAsync<EventRow>("SELECT * FROM events WHERE run_id=@id ORDER BY id", new { id });
            return Results.Ok(new { run = RunDto(run), items = items.Select(BackupDto), events = events.Select(EventDto) });
        });

        // ------------------------------------------------------------------ settings
        api.MapGet("/settings", (SettingsService s) => s.GetAsync());
        api.MapPut("/settings", (AppSettings input, SettingsService s) => s.SaveAsync(input));
        api.MapPost("/settings/webhook-secret", async (WebhookSecretRequest req, SettingsService s) =>
        {
            if (req.Secret is { Length: > 256 }) throw new ValidationException(["Segredo longo demais."]);
            await s.SetWebhookSecretAsync(req.Secret);
            return Results.Ok(new { ok = true });
        });
        api.MapPost("/settings/storage/test", async (SettingsService s, IStorageFactory f, CancellationToken ct) =>
        {
            var settings = await s.GetAsync();
            TestResult result;
            try { result = await f.Create(settings).TestConnectionAsync(ct); }
            catch (StorageException ex) { result = new TestResult(false, ex.Message); }
            await s.SetDriveStatusAsync(result.Ok, result.Message);
            return Results.Ok(result);
        });
        api.MapPost("/settings/webhook/test", async (SettingsService s, WebhookNotifier webhook, CancellationToken ct) =>
        {
            var settings = await s.GetAsync();
            if (string.IsNullOrEmpty(settings.WebhookUrl)) throw new ValidationException(["Configure a URL do webhook primeiro."]);
            var now = Clock.UtcNow();
            try
            {
                await webhook.SendAsync(settings.WebhookUrl, new NotificationEvent("test", settings.VpsName, "test", "success", 0, 0, 0, 0, now, now, []),
                    await s.GetWebhookSecretAsync(), ct);
                return Results.Ok(new { ok = true, message = "Webhook enviado com sucesso." });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = false, message = ex.Message });
            }
        });
        // ------------------------------------------------------------------ Google Drive (Gmail)
        api.MapPost("/drive/connect", async (DriveConnectRequest req, DriveAuthService auth, CancellationToken ct) =>
            Results.Ok(await auth.StartAsync(req.Email ?? "", ct)));
        api.MapPost("/drive/complete", async (DriveCompleteRequest req, DriveAuthService auth, CancellationToken ct) =>
            Results.Ok(await auth.CompleteAsync(req.Url ?? "", ct)));
        api.MapGet("/drive/status", async (SettingsService s, AppOptions o) =>
        {
            var settings = await s.GetAsync();
            var status = await s.GetDriveStatusAsync();
            return Results.Ok(new
            {
                configured = RcloneConfigFile.HasRemote(o.RcloneConfig, settings.RcloneRemote),
                email = settings.DriveAccountEmail, remote = settings.RcloneRemote, basePath = settings.RemoteBasePath,
                status.Ok, checkedAt = Clock.IsoFromMs(status.CheckedAt), status.Message,
            });
        });

        api.MapGet("/settings/timezones", () =>
            TimeZoneInfo.GetSystemTimeZones()
                .Select(t => t.HasIanaId ? t.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(t.Id, out var iana) ? iana : null)
                .Where(id => id is not null && id.Contains('/')).Distinct().Order());
    }

    // ---------------------------------------------------------------------- DTOs
    private static object ConnectionDto(ConnectionRow r) => new
    {
        r.Id, r.Name, r.DbType, r.Host, r.Port, r.Username, hasPassword = r.PasswordEnc.Length > 0, r.SslMode,
        options = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(r.Options),
        r.BackupAll, r.IncludeSystem,
        selectedDatabases = JsonSerializer.Deserialize<List<string>>(r.SelectedDatabases),
        excludedDatabases = JsonSerializer.Deserialize<List<string>>(r.ExcludedDatabases),
        discoveredDatabases = JsonSerializer.Deserialize<List<string>>(r.DiscoveredDatabases),
        protectedCount = ConnectionService.ProtectedCount(r),
        r.Enabled, r.LastTestOk, lastTestAt = Clock.IsoFromMs(r.LastTestAt), r.LastTestMessage,
    };

    private static object ScheduleDto(ScheduleRow r, TimeZoneInfo tz) => new
    {
        r.Id, r.Name, r.Enabled, r.Kind,
        daysOfWeek = JsonSerializer.Deserialize<List<int>>(r.DaysOfWeek),
        times = JsonSerializer.Deserialize<List<string>>(r.Times),
        r.IntervalMinutes, r.TargetType, r.TargetConnectionId, r.TargetDatabase,
        lastTriggeredAt = Clock.IsoFromMs(r.LastTriggeredAt),
        nextRunAt = ScheduleService.NextRun(r, tz)?.ToString("O"),
    };

    private static object RunDto(RunRow r) => new
    {
        r.Id, r.Trigger, r.ScheduleId, r.TargetType, r.TargetConnectionId, r.TargetDatabase, r.Status,
        r.Total, r.Succeeded, r.Warnings, r.Failed, r.Message,
        startedAt = Clock.IsoFromMs(r.StartedAt), finishedAt = Clock.IsoFromMs(r.FinishedAt),
    };

    private static object BackupDto(BackupRow b) => new
    {
        b.Id, b.RunId, b.ConnectionId, b.ConnectionName, b.DbType, b.DatabaseName, b.Status,
        startedAt = Clock.IsoFromMs(b.StartedAt), finishedAt = Clock.IsoFromMs(b.FinishedAt), b.DurationMs,
        b.RawSize, b.CompressedSize, b.FinalSize, b.Compression, b.Encrypted, b.RawSha256, b.ChecksumSha256,
        b.FileName, b.LocalPath, b.RemotePath, b.UploadAttempts, b.Warning, b.Error,
        localDeletedAt = Clock.IsoFromMs(b.LocalDeletedAt), remoteDeletedAt = Clock.IsoFromMs(b.RemoteDeletedAt),
    };

    private static object EventDto(EventRow e) => new
    {
        e.Id, e.RunId, e.BackupId, e.Level, e.Message, createdAt = Clock.IsoFromMs(e.CreatedAt),
    };
}

/// <summary>Reads the tail of the newest rolling log file (compact JSON lines).</summary>
public static class LogReader
{
    public static IReadOnlyList<JsonElement> Tail(string logDir, int lines)
    {
        lines = Math.Clamp(lines, 1, 2000);
        if (!Directory.Exists(logDir)) return [];
        var file = new DirectoryInfo(logDir).EnumerateFiles("app*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (file is null) return [];
        using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, fs.Length - 512 * 1024);
        fs.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        var all = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<JsonElement>();
        foreach (var line in all.TakeLast(lines))
        {
            try { result.Add(JsonDocument.Parse(line).RootElement.Clone()); }
            catch (JsonException) { /* partial first line */ }
        }
        return result;
    }
}
