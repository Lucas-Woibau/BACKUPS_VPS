using System.Text;
using Dapper;
using VpsBackupManager.Config;
using VpsBackupManager.Data;
using VpsBackupManager.Notifications;
using VpsBackupManager.Providers;
using VpsBackupManager.Security;
using VpsBackupManager.Services;
using VpsBackupManager.Storage;

namespace VpsBackupManager.Backup;

public sealed record RunTarget(string Type, long? ConnectionId, string? Database)
{
    public static readonly RunTarget All = new("all", null, null);

    public void Validate()
    {
        if (Type is not ("all" or "connection" or "database")) throw new ValidationException(["Alvo inválido."]);
        if (Type != "all" && ConnectionId is null) throw new ValidationException(["Informe a conexão."]);
        if (Type == "database") ProviderHelpers.ValidateDatabaseName(Database ?? "");
    }
}

public sealed class BackupBusyException() : Exception("Já existe um backup em execução. Aguarde a conclusão.");

/// <summary>A failure in one step of one database backup, with a user-facing message.</summary>
public sealed class BackupStepException(string message) : Exception(message);

/// <summary>
/// Runs the backup flow:
/// validate → disk check → connection check → per-run temp dir → dump (streamed into compressor) →
/// exit code / size / format checks → decompression verify → SHA-256 → optional age encryption →
/// upload with retries + remote size/MD5 check → history → retention → local cleanup → notify.
/// One database failing never stops the others.
/// </summary>
public sealed class BackupOrchestrator(
    Db db,
    AppOptions options,
    SettingsService settingsService,
    ConnectionService connections,
    ProviderRegistry registry,
    IDumpExecutor dumpExecutor,
    IStorageFactory storageFactory,
    IBackupEncryptor encryptor,
    NotificationDispatcher notifications,
    EventLog events,
    BackupLock backupLock,
    ILogger<BackupOrchestrator> logger)
{
    private volatile string? _currentRunId;
    private CancellationToken _stopping = CancellationToken.None;

    public string? CurrentRunId => _currentRunId;

    /// <summary>Set by the host so in-flight runs see shutdown.</summary>
    public void AttachShutdownToken(CancellationToken token) => _stopping = token;

    /// <summary>Starts a manual run in the background. Throws <see cref="BackupBusyException"/> if one is running.</summary>
    public async Task<string> StartManualAsync(RunTarget target)
    {
        target.Validate();
        var handle = await backupLock.TryAcquireAsync(TimeSpan.Zero, CancellationToken.None) ?? throw new BackupBusyException();
        string runId;
        try
        {
            runId = await CreateRunAsync("manual", null, target);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
        _ = Task.Run(() => ExecuteRunAsync(runId, target, handle));
        return runId;
    }

    /// <summary>Runs a scheduled backup, waiting for the lock if another run is in progress.</summary>
    public async Task<string?> RunScheduledAsync(long scheduleId, string trigger, RunTarget target)
    {
        var settings = await settingsService.GetAsync();
        var handle = await backupLock.TryAcquireAsync(TimeSpan.FromMinutes(settings.ScheduledLockWaitMinutes), _stopping);
        if (handle is null)
        {
            var skipped = await CreateRunAsync(trigger, scheduleId, target);
            await FinishRunAsync(skipped, BackupStatus.Error, 0, 0, 0, 0,
                $"Agendamento ignorado: outro backup ocupou o lock por mais de {settings.ScheduledLockWaitMinutes} min.");
            await events.Error(skipped, null, "Agendamento ignorado: lock ocupado.");
            return skipped;
        }
        var runId = await CreateRunAsync(trigger, scheduleId, target);
        await ExecuteRunAsync(runId, target, handle);
        return runId;
    }

    /// <summary>Called at startup: anything left "running" was interrupted by a restart.</summary>
    public async Task RecoverInterruptedAsync()
    {
        var now = Clock.NowMs();
        const string msg = "Interrompido: a aplicação/contêiner foi reiniciado durante o backup.";
        var runs = await db.ExecuteAsync(
            "UPDATE runs SET status='error', message=@msg, finished_at=@now WHERE status IN ('pending','running')", new { msg, now });
        var items = await db.ExecuteAsync(
            "UPDATE backups SET status='error', error=@msg, finished_at=@now WHERE status IN ('pending','running')", new { msg, now });
        if (runs + items > 0) logger.LogWarning("Recuperação: {Runs} execuções e {Items} backups marcados como interrompidos", runs, items);

        if (Directory.Exists(options.WorkDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(options.WorkDir, "run_*"))
            {
                try { PathGuard.DeleteRunDirectory(options.WorkDir, dir); }
                catch (Exception ex) { logger.LogWarning("Não foi possível limpar {Dir}: {Error}", dir, ex.Message); }
            }
        }
    }

    // ===================================================================================== run
    private sealed class PlannedItem
    {
        public required BackupRow Row { get; init; }
        public required DbConnectionInfo Info { get; init; }
        public required IDatabaseBackupProvider Provider { get; init; }
        public bool IsGlobals { get; init; }
    }

    private async Task ExecuteRunAsync(string runId, RunTarget target, IDisposable lockHandle)
    {
        _currentRunId = runId;
        var runDir = Path.Combine(options.WorkDir, PathGuard.RunDirectoryName(runId));
        var results = new List<PlannedItem>();
        var startedAt = Clock.UtcNow();
        try
        {
            await db.ExecuteAsync("UPDATE runs SET status='running' WHERE id=@runId", new { runId });
            var settings = await settingsService.GetAsync();
            var tz = SettingsService.ResolveTimeZone(settings.Timezone);
            var storage = storageFactory.Create(settings);
            Directory.CreateDirectory(runDir);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(runDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            await events.Info(runId, null, $"Execução iniciada (alvo: {Describe(target)}). Destino: {storage.Describe()}");
            var plan = await PlanAsync(runId, target);
            await db.ExecuteAsync("UPDATE runs SET total=@t WHERE id=@runId",
                new { t = await db.QueryOneAsync<long>("SELECT COUNT(*) FROM backups WHERE run_id=@runId", new { runId }), runId });

            foreach (var item in plan)
            {
                if (_stopping.IsCancellationRequested) break;
                await ProcessItemAsync(runId, item, settings, tz, storage, runDir);
                results.Add(item);
            }

            await ApplyRetentionAsync(runId, results.Where(r => r.Row.Status is BackupStatus.Success or BackupStatus.Warning), settings, tz, storage);
            await CleanupLocalCopiesAsync(runId, settings);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha geral na execução {RunId}", runId);
            await events.Error(runId, null, "Falha geral: " + ex.Message);
            await db.ExecuteAsync("UPDATE runs SET message=@m WHERE id=@runId", new { m = Redactor.Truncate(ex.Message, 1000), runId });
        }
        finally
        {
            try { if (Directory.Exists(runDir)) PathGuard.DeleteRunDirectory(options.WorkDir, runDir); }
            catch (Exception ex) { logger.LogWarning("Falha ao remover diretório temporário: {Error}", ex.Message); }

            RunSummary? summary = null;
            try
            {
                summary = await SummarizeAsync(runId);
                await FinishRunAsync(runId, summary.Status, summary.Total, summary.Ok, summary.Warn, summary.Err, summary.Message);
                await events.AddAsync(runId, null, summary.Status == BackupStatus.Error ? "error" : "info", "Execução finalizada: " + summary.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha ao finalizar a execução {RunId}", runId);
            }
            finally
            {
                // Always release the lock, even if the internal DB is unavailable.
                _currentRunId = null;
                lockHandle.Dispose();
            }
            if (summary is not null) await NotifyAsync(runId, summary, startedAt);
        }
    }

    private async Task<List<PlannedItem>> PlanAsync(string runId, RunTarget target)
    {
        var rows = target.Type == "all"
            ? (await connections.ListAsync()).Where(c => c.Enabled).ToList()
            : [await connections.GetAsync(target.ConnectionId!.Value) ?? throw new BackupStepException("Conexão não encontrada.")];
        if (rows.Count == 0) await events.Warn(runId, null, "Nenhuma conexão habilitada.");

        var plan = new List<PlannedItem>();
        foreach (var row in rows)
        {
            IDatabaseBackupProvider provider;
            DbConnectionInfo info;
            try
            {
                provider = registry.Get(row.DbType);
                info = connections.ToInfo(row);
            }
            catch (Exception ex)
            {
                await InsertFailedItemAsync(runId, row, "*", ex.Message);
                continue;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            cts.CancelAfter(TimeSpan.FromMinutes(2));
            var test = await provider.TestConnectionAsync(info, cts.Token);
            await connections.SaveTestResultAsync(row.Id, test.Ok, test.Message);
            if (!test.Ok)
            {
                await InsertFailedItemAsync(runId, row, "*", "Conexão falhou: " + test.Message);
                continue;
            }

            IReadOnlyList<string> onServer;
            try
            {
                onServer = await provider.ListDatabasesAsync(info, row.IncludeSystem, cts.Token);
                await connections.SaveDiscoveredAsync(row.Id, onServer);
            }
            catch (Exception ex)
            {
                await InsertFailedItemAsync(runId, row, "*", "Falha ao listar bancos: " + ex.Message);
                continue;
            }

            IReadOnlyList<string> dbs;
            if (target.Type == "database")
            {
                if (!onServer.Contains(target.Database!, StringComparer.Ordinal))
                {
                    await InsertFailedItemAsync(runId, row, target.Database!, "Banco não encontrado no servidor.");
                    continue;
                }
                dbs = [target.Database!];
            }
            else
            {
                dbs = ConnectionService.ResolveTargets(row, onServer);
                foreach (var missing in ConnectionService.MissingSelected(row, onServer))
                    await InsertFailedItemAsync(runId, row, missing, "Banco selecionado não existe mais no servidor.");
            }
            if (dbs.Count == 0) await events.Warn(runId, null, $"Conexão '{row.Name}': nenhum banco a copiar.");

            foreach (var name in dbs)
                plan.Add(new PlannedItem { Row = await InsertItemAsync(runId, row, name), Info = info, Provider = provider });

            if (target.Type != "database" && provider.WantsGlobals(info))
            {
                plan.Add(new PlannedItem
                {
                    Row = await InsertItemAsync(runId, row, PostgreSqlProvider.GlobalsName), Info = info, Provider = provider, IsGlobals = true,
                });
            }
        }
        return plan;
    }

    // ============================================================================ single item
    private async Task ProcessItemAsync(string runId, PlannedItem item, AppSettings settings, TimeZoneInfo tz,
        IStorageProvider storage, string runDir)
    {
        var row = item.Row;
        var started = Clock.UtcNow();
        row.Status = BackupStatus.Running;
        row.StartedAt = started.ToUnixTimeMilliseconds();
        await db.ExecuteAsync("UPDATE backups SET status=@Status, started_at=@StartedAt WHERE id=@Id", row);
        await events.Info(runId, row.Id, $"[{row.ConnectionName}/{row.DatabaseName}] iniciando backup");

        var created = new List<string>();
        try
        {
            CheckDiskSpace(settings);

            var localTime = BackupNaming.ToLocal(started, tz);
            var cmd = item.IsGlobals
                ? item.Provider.BuildGlobalsCommand(item.Info, runDir)!
                : item.Provider.BuildDumpCommand(item.Info, row.DatabaseName, runDir);
            if (cmd.Timeout <= TimeSpan.Zero) cmd = cmd with { Timeout = TimeSpan.FromMinutes(settings.DefaultDumpTimeoutMinutes) };

            var remoteDir = BackupNaming.RemoteDirectory(settings, item.Provider.Family, row.ConnectionName, localTime,
                item.Info.OptString("drive_folder"));
            var fileName = BackupNaming.FileName(row.DatabaseName, localTime, cmd.Extension, settings.Compression, settings.EncryptionEnabled);
            if (await storage.ExistsAsync($"{remoteDir}/{fileName}", CancellationToken.None))
                fileName = BackupNaming.FileName(row.DatabaseName, localTime, cmd.Extension, settings.Compression,
                    settings.EncryptionEnabled, row.Id[..8]);
            var compressedName = settings.EncryptionEnabled ? fileName[..^".age".Length] : fileName;
            var compressedPath = Path.Combine(runDir, compressedName);
            created.Add(compressedPath);

            // ---- dump → compressor → file
            DumpOutcome outcome;
            var fso = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, BufferSize = 1 << 16 };
            if (!OperatingSystem.IsWindows()) fso.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            try
            {
                await using var file = new FileStream(compressedPath, fso);
                await using var compressor = Compression.OpenWriter(settings.Compression, settings.CompressionLevel, file);
                outcome = item.IsGlobals
                    ? await dumpExecutor.ExecuteAsync(cmd, compressor, _stopping)
                    : await item.Provider.ExecuteDumpAsync(cmd, item.Info, row.DatabaseName, compressor, dumpExecutor, _stopping);
            }
            catch (IOException ex) when (IsDiskFull(ex))
            {
                throw new BackupStepException("Disco cheio durante o dump.");
            }
            finally
            {
                ProviderHelpers.DeleteSecretFiles(cmd.SecretFiles);
            }

            if (outcome.TimedOut) throw new BackupStepException($"Tempo limite do dump excedido ({cmd.Timeout.TotalMinutes:0} min).");
            if (outcome.ExitCode != 0) throw new BackupStepException($"{cmd.FileName} terminou com código {outcome.ExitCode}: {Short(outcome.Stderr)}");
            if (outcome.RawSize == 0) throw new BackupStepException("Dump gerou 0 bytes.");
            var validation = item.IsGlobals
                ? item.Provider.ValidateGlobals(outcome.Head, outcome.Tail, outcome.Stderr)
                : item.Provider.ValidateBackup(outcome.Head, outcome.Tail, outcome.Stderr);
            if (!validation.Ok) throw new BackupStepException("Validação do dump falhou: " + validation.Message);

            var compressedSize = new FileInfo(compressedPath).Length;
            if (compressedSize == 0) throw new BackupStepException("Arquivo compactado com 0 bytes.");
            var (checkSize, checkSha) = await Compression.VerifyAsync(compressedPath, settings.Compression, _stopping);
            if (checkSize != outcome.RawSize || checkSha != outcome.RawSha256)
                throw new BackupStepException("Verificação da compressão falhou (conteúdo descompactado difere do dump).");

            // ---- optional encryption
            var finalPath = compressedPath;
            if (settings.EncryptionEnabled)
            {
                finalPath = compressedPath + ".age";
                created.Add(finalPath);
                await encryptor.EncryptAsync(compressedPath, finalPath, settings.AgeRecipients, _stopping);
                File.Delete(compressedPath);
            }

            var (sha256, md5, finalSize) = await FileHashes.ComputeAsync(finalPath, _stopping);
            var sidecarPath = finalPath + ".sha256";
            created.Add(sidecarPath);
            await File.WriteAllTextAsync(sidecarPath, $"{sha256}  {fileName}\n", Encoding.ASCII);

            row.RawSize = outcome.RawSize;
            row.RawSha256 = outcome.RawSha256;
            row.CompressedSize = compressedSize;
            row.FinalSize = finalSize;
            row.Compression = settings.Compression;
            row.Encrypted = settings.EncryptionEnabled;
            row.ChecksumSha256 = sha256;
            row.FileName = fileName;
            await db.ExecuteAsync(
                """
                UPDATE backups SET raw_size=@RawSize, raw_sha256=@RawSha256, compressed_size=@CompressedSize, final_size=@FinalSize,
                  compression=@Compression, encrypted=@Encrypted, checksum_sha256=@ChecksumSha256, file_name=@FileName WHERE id=@Id
                """, row);
            await events.Info(runId, row.Id, $"Dump OK: {Fmt(outcome.RawSize)} → {Fmt(finalSize)} (sha256 {sha256[..16]}…)");

            // ---- upload (+ verification)
            var remoteRel = $"{remoteDir}/{fileName}";
            row.UploadAttempts = await UploadWithRetryAsync(runId, row.Id, storage, finalPath, remoteRel, md5, finalSize, settings);
            row.RemotePath = storage.Describe(remoteRel);
            await db.ExecuteAsync("UPDATE backups SET remote_path=@RemotePath, upload_attempts=@UploadAttempts WHERE id=@Id", row);

            var warnings = new List<string>();
            if (validation.Warning is not null) warnings.Add("Aviso da ferramenta de dump: " + Short(validation.Warning));
            try
            {
                await UploadWithRetryAsync(runId, row.Id, storage, sidecarPath, remoteRel + ".sha256", null, new FileInfo(sidecarPath).Length, settings);
            }
            catch (Exception ex)
            {
                warnings.Add("Arquivo .sha256 não enviado: " + ex.Message);
            }

            // ---- local copy policy
            if (settings.KeepLocalCopy)
            {
                var localDir = PathGuard.EnsureInside(options.LocalCopiesDir, Path.Combine(options.LocalCopiesDir, remoteDir));
                Directory.CreateDirectory(localDir);
                var localPath = Path.Combine(localDir, fileName);
                File.Move(finalPath, localPath, overwrite: false);
                File.Move(sidecarPath, localPath + ".sha256", overwrite: false);
                row.LocalPath = localPath;
            }

            row.Warning = warnings.Count > 0 ? string.Join(" | ", warnings) : null;
            row.Status = warnings.Count > 0 ? BackupStatus.Warning : BackupStatus.Success;
            await events.Info(runId, row.Id, $"Backup concluído: {row.RemotePath}");
        }
        catch (Exception ex)
        {
            row.Status = BackupStatus.Error;
            row.Error = ex switch
            {
                OperationCanceledException => "Interrompido: aplicação encerrada durante o backup.",
                BackupStepException or StorageException or ProviderException or PathGuard.UnsafePathException => ex.Message,
                _ => "Erro inesperado: " + ex.Message,
            };
            row.Error = Redactor.Truncate(row.Error, 2000);
            await events.Error(runId, row.Id, $"[{row.ConnectionName}/{row.DatabaseName}] {row.Error}");
            if (ex is not (BackupStepException or StorageException or ProviderException or OperationCanceledException))
                logger.LogError(ex, "Erro inesperado no backup {BackupId}", row.Id);
        }
        finally
        {
            foreach (var f in created)
            {
                try
                {
                    if (File.Exists(f)) File.Delete(PathGuard.EnsureInside(options.WorkDir, f));
                }
                catch (Exception ex) { logger.LogWarning("Falha ao remover temporário {File}: {Error}", f, ex.Message); }
            }
            var finished = Clock.UtcNow();
            row.FinishedAt = finished.ToUnixTimeMilliseconds();
            row.DurationMs = (long)(finished - started).TotalMilliseconds;
            await db.ExecuteAsync(
                "UPDATE backups SET status=@Status, finished_at=@FinishedAt, duration_ms=@DurationMs, warning=@Warning, error=@Error, local_path=@LocalPath WHERE id=@Id",
                row);
        }
    }

    private async Task<int> UploadWithRetryAsync(string runId, string backupId, IStorageProvider storage, string localPath,
        string remoteRel, string? expectedMd5, long expectedSize, AppSettings settings)
    {
        var max = Math.Max(1, settings.UploadMaxAttempts);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await storage.UploadAsync(localPath, remoteRel, _stopping);
                var stat = await storage.StatAsync(remoteRel, _stopping)
                           ?? throw new StorageException("Upload concluído mas o arquivo não foi encontrado no destino.", true);
                if (stat.Size >= 0 && stat.Size != expectedSize)
                    throw new StorageException($"Tamanho remoto ({stat.Size}) difere do local ({expectedSize}).", true);
                if (expectedMd5 is not null && stat.Md5 is not null && !string.Equals(stat.Md5, expectedMd5, StringComparison.OrdinalIgnoreCase))
                    throw new StorageException("MD5 remoto difere do arquivo local (upload corrompido).", true);
                if (expectedMd5 is not null && stat.Md5 is null)
                    await events.Warn(runId, backupId, "Destino não informou hash; upload verificado apenas pelo tamanho.");
                return attempt;
            }
            catch (StorageException ex) when (ex.Transient && attempt < max && !_stopping.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(settings.UploadMaxBackoffSeconds,
                    settings.UploadInitialBackoffSeconds * Math.Pow(2, attempt - 1)) * (0.8 + Random.Shared.NextDouble() * 0.4));
                await events.Warn(runId, backupId, $"Upload falhou (tentativa {attempt}/{max}): {ex.Message}. Nova tentativa em {delay.TotalSeconds:0}s.");
                await Task.Delay(delay, _stopping);
            }
            catch (StorageException ex)
            {
                throw new StorageException($"Upload falhou após {attempt} tentativa(s): {ex.Message}", false, ex);
            }
        }
    }

    // ============================================================================== retention
    private async Task ApplyRetentionAsync(string runId, IEnumerable<PlannedItem> successful, AppSettings settings,
        TimeZoneInfo tz, IStorageProvider storage)
    {
        if (settings.RetentionMode == "none") return;
        var prefix = storage.Describe() + "/";
        var groups = successful.Select(i => (i.Row.ConnectionId, i.Row.DatabaseName)).Distinct();
        foreach (var (connectionId, database) in groups)
        {
            var rows = await db.QueryAsync<BackupRow>(
                """
                SELECT * FROM backups WHERE connection_id=@connectionId AND database_name=@database
                  AND status IN ('success','warning') AND remote_path IS NOT NULL AND remote_deleted_at IS NULL
                """, new { connectionId, database });
            var items = rows.Where(r => r.FinishedAt is not null)
                .Select(r => new RetentionItem(r.Id, Clock.FromMs(r.FinishedAt!.Value))).ToList();
            var toDelete = RetentionPolicy.SelectForDeletion(items, settings, Clock.UtcNow(), tz);
            foreach (var id in toDelete)
            {
                var r = rows.First(x => x.Id == id);
                try
                {
                    if (!r.RemotePath!.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        await events.Warn(runId, id, $"Retenção ignorada: {r.RemotePath} está fora do destino atual ({storage.Describe()}).");
                        continue;
                    }
                    var rel = r.RemotePath[prefix.Length..];
                    PathGuard.EnsureDeletableRemoteBackup(rel);
                    await storage.DeleteAsync(rel, CancellationToken.None);
                    try { await storage.DeleteAsync(rel + ".sha256", CancellationToken.None); } catch { /* sidecar is optional */ }
                    await db.ExecuteAsync("UPDATE backups SET remote_deleted_at=@now WHERE id=@id", new { now = Clock.NowMs(), id });
                    await events.Info(runId, id, $"Retenção: removido {r.RemotePath}");
                }
                catch (Exception ex)
                {
                    await events.Warn(runId, id, $"Retenção: falha ao remover {r.RemotePath}: {ex.Message}");
                }
            }
        }
    }

    public async Task CleanupLocalCopiesAsync(string? runId, AppSettings settings)
    {
        var limit = settings.KeepLocalCopy ? Clock.NowMs() - (long)TimeSpan.FromDays(settings.KeepLocalDays).TotalMilliseconds : long.MaxValue;
        var rows = await db.QueryAsync<BackupRow>(
            "SELECT * FROM backups WHERE local_path IS NOT NULL AND local_deleted_at IS NULL AND finished_at < @limit", new { limit });
        foreach (var r in rows)
        {
            try
            {
                PathGuard.DeleteFileInside(options.LocalCopiesDir, r.LocalPath!);
                PathGuard.DeleteFileInside(options.LocalCopiesDir, r.LocalPath! + ".sha256");
                await db.ExecuteAsync("UPDATE backups SET local_deleted_at=@now WHERE id=@id", new { now = Clock.NowMs(), id = r.Id });
            }
            catch (Exception ex)
            {
                await events.Warn(runId, r.Id, $"Falha ao remover cópia local {r.LocalPath}: {ex.Message}");
            }
        }
    }

    // ================================================================================ helpers
    private void CheckDiskSpace(AppSettings settings)
    {
        var free = new DriveInfo(Path.GetFullPath(options.BackupRoot)).AvailableFreeSpace;
        var min = settings.MinFreeDiskMb * 1024L * 1024L;
        if (free < min)
            throw new BackupStepException($"Espaço em disco insuficiente: {Fmt(free)} livres, mínimo configurado {Fmt(min)}.");
    }

    private static bool IsDiskFull(IOException ex) =>
        ex.HResult == 28 || ex.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase) || ex.HResult == unchecked((int)0x80070070);

    private async Task<string> CreateRunAsync(string trigger, long? scheduleId, RunTarget target)
    {
        var id = Guid.NewGuid().ToString("N");
        await db.ExecuteAsync(
            """
            INSERT INTO runs(id, trigger, schedule_id, target_type, target_connection_id, target_database, status, started_at)
            VALUES (@id, @trigger, @scheduleId, @type, @conn, @database, 'pending', @now)
            """,
            new { id, trigger, scheduleId, type = target.Type, conn = target.ConnectionId, database = target.Database, now = Clock.NowMs() });
        return id;
    }

    private async Task<BackupRow> InsertItemAsync(string runId, ConnectionRow conn, string database, string status = BackupStatus.Pending, string? error = null)
    {
        var row = new BackupRow
        {
            Id = Guid.NewGuid().ToString("N"), RunId = runId, ConnectionId = conn.Id, ConnectionName = conn.Name,
            DbType = conn.DbType, DatabaseName = database, Status = status, Error = error, CreatedAt = Clock.NowMs(),
        };
        if (status == BackupStatus.Error) { row.StartedAt = row.CreatedAt; row.FinishedAt = row.CreatedAt; row.DurationMs = 0; }
        await db.ExecuteAsync(
            """
            INSERT INTO backups(id, run_id, connection_id, connection_name, db_type, database_name, status, error, started_at, finished_at, duration_ms, created_at)
            VALUES (@Id, @RunId, @ConnectionId, @ConnectionName, @DbType, @DatabaseName, @Status, @Error, @StartedAt, @FinishedAt, @DurationMs, @CreatedAt)
            """, row);
        return row;
    }

    private async Task InsertFailedItemAsync(string runId, ConnectionRow conn, string database, string error)
    {
        var row = await InsertItemAsync(runId, conn, database, BackupStatus.Error, Redactor.Truncate(error, 2000));
        await events.Error(runId, row.Id, $"[{conn.Name}/{database}] {error}");
    }

    private sealed record RunSummary(string Status, int Total, int Ok, int Warn, int Err, string Message);

    private async Task<RunSummary> SummarizeAsync(string runId)
    {
        var statuses = await db.QueryAsync<string>("SELECT status FROM backups WHERE run_id=@runId", new { runId });
        var ok = statuses.Count(s => s == BackupStatus.Success);
        var warn = statuses.Count(s => s == BackupStatus.Warning);
        var err = statuses.Count(s => s is BackupStatus.Error or BackupStatus.Pending or BackupStatus.Running);
        var total = statuses.Count;
        var runMessage = await db.QueryOneAsync<string?>("SELECT message FROM runs WHERE id=@runId", new { runId });
        if (runMessage is not null) return new(BackupStatus.Error, total, ok, warn, Math.Max(err, 1), runMessage);
        if (total == 0) return new(BackupStatus.Warning, 0, 0, 0, 0, "Nenhum banco foi copiado (nada configurado).");
        if (err > 0)
            return new(BackupStatus.Error, total, ok, warn, err,
                ok + warn > 0 ? $"Parcial: {err} de {total} backup(s) com erro." : $"Todos os {total} backup(s) falharam.");
        if (warn > 0) return new(BackupStatus.Warning, total, ok, warn, 0, $"{total} backup(s) concluído(s), {warn} com aviso.");
        return new(BackupStatus.Success, total, ok, 0, 0, $"{total} backup(s) concluído(s) com sucesso.");
    }

    private Task FinishRunAsync(string runId, string status, int total, int ok, int warn, int err, string message) =>
        db.ExecuteAsync(
            "UPDATE runs SET status=@status, total=@total, succeeded=@ok, warnings=@warn, failed=@err, message=@message, finished_at=@now WHERE id=@runId",
            new { runId, status, total, ok, warn, err, message, now = Clock.NowMs() });

    private async Task NotifyAsync(string runId, RunSummary summary, DateTimeOffset startedAt)
    {
        try
        {
            var settings = await settingsService.GetAsync();
            var items = await db.QueryAsync<BackupRow>("SELECT * FROM backups WHERE run_id=@runId", new { runId });
            await notifications.DispatchAsync(new NotificationEvent(
                summary.Err > 0 ? "backup.failure" : "backup.success", settings.VpsName, runId, summary.Status,
                summary.Total, summary.Ok, summary.Warn, summary.Err, startedAt, Clock.UtcNow(),
                items.Select(i => new BackupItemSummary(i.ConnectionName, i.DatabaseName, i.Status, i.FinalSize, i.RemotePath, i.Error, i.ChecksumSha256)).ToList()));
        }
        catch (Exception ex)
        {
            logger.LogWarning("Falha ao notificar: {Error}", ex.Message);
        }
    }

    private static string Describe(RunTarget t) => t.Type switch
    {
        "connection" => $"conexão #{t.ConnectionId}",
        "database" => $"banco {t.Database} (conexão #{t.ConnectionId})",
        _ => "todas as conexões",
    };

    private static string Short(string s) => Redactor.Truncate(s.Trim(), 1500);

    public static string Fmt(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.#} {units[u]}";
    }
}
