using Microsoft.Extensions.Logging.Abstractions;
using VpsBackupManager.Backup;
using VpsBackupManager.Config;
using VpsBackupManager.Data;
using VpsBackupManager.Notifications;
using VpsBackupManager.Providers;
using VpsBackupManager.Security;
using VpsBackupManager.Services;
using VpsBackupManager.Storage;

namespace VpsBackupManager.Tests;

/// <summary>End-to-end backup flow with fake DB provider/executor and a local-folder "Google Drive".</summary>
public sealed class OrchestratorTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly AppOptions _options;
    private readonly Db _db;
    private readonly SettingsService _settings;
    private readonly ConnectionService _connections;
    private readonly FakeProvider _provider = new();
    private readonly FakeDumpExecutor _executor = new();
    private readonly string _remoteRoot;

    public OrchestratorTests()
    {
        _options = new AppOptions
        {
            SecretKey = new string('s', 48), DataDir = _tmp.Sub("data"), BackupRoot = _tmp.Sub("backups"),
            LogDir = _tmp.Sub("logs"), RcloneConfig = Path.Combine(_tmp.Path, "rclone.conf"),
        };
        _options.EnsureDirectories();
        _db = new Db(_options.DbPath);
        _db.Migrate();
        var box = new SecretBox(_options.SecretKey);
        _settings = new SettingsService(_db, box, _options);
        var registry = new ProviderRegistry([_provider]);
        _connections = new ConnectionService(_db, box, registry, _options);
        _remoteRoot = _tmp.Sub("gdrive");
    }

    private BackupOrchestrator Create(IStorageProvider storage) =>
        new(_db, _options, _settings, _connections, new ProviderRegistry([_provider]), _executor, new FixedStorageFactory(storage),
            new AgeEncryptor(), new NotificationDispatcher([], NullLogger<NotificationDispatcher>.Instance),
            new EventLog(_db, NullLogger<EventLog>.Instance), new BackupLock(_options.LockFile), NullLogger<BackupOrchestrator>.Instance);

    private async Task<long> AddConnection(string name = "Principal", bool backupAll = true, List<string>? selected = null) =>
        await _connections.CreateAsync(new ConnectionInput(name, "fake", "localhost", 1, "backup", "secret-pass-123", "disabled",
            null, backupAll, false, selected, null, true));

    private async Task SaveSettings(Func<AppSettings, AppSettings> change) =>
        await _settings.SaveAsync(change(await _settings.GetAsync() with
        {
            RemoteBasePath = "Backups", UploadInitialBackoffSeconds = 1, UploadMaxBackoffSeconds = 1, MinFreeDiskMb = 100,
        }));

    [Fact]
    public async Task Successful_run_uploads_compressed_files_with_checksum_and_cleans_temp()
    {
        await SaveSettings(s => s);
        await AddConnection();
        var storage = new LocalFolderStorage(_remoteRoot);
        var runId = await Create(storage).RunScheduledAsync(0, "schedule", RunTarget.All);

        var run = await _db.QueryOneAsync<RunRow>("SELECT * FROM runs WHERE id=@runId", new { runId });
        Assert.Equal(BackupStatus.Success, run!.Status);
        Assert.Equal(2, run.Total);

        var items = await _db.QueryAsync<BackupRow>("SELECT * FROM backups WHERE run_id=@runId", new { runId });
        Assert.All(items, b =>
        {
            Assert.Equal(BackupStatus.Success, b.Status);
            Assert.EndsWith(".sql.gz", b.FileName);
            Assert.True(b.RawSize > b.FinalSize);
            Assert.Equal(64, b.ChecksumSha256!.Length);
            var rel = b.RemotePath![(_remoteRoot.Length + 1)..].Replace('\\', '/');
            Assert.Matches(@"^VPS-Producao/\d{4}/\d{2}/\d{2}/fake/Principal/(app|shop)_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}\.sql\.gz$", rel);
            var local = Path.Combine(_remoteRoot, rel);
            Assert.True(File.Exists(local));
            Assert.True(File.Exists(local + ".sha256"));
            Assert.StartsWith(b.ChecksumSha256, File.ReadAllText(local + ".sha256"));
        });

        Assert.Empty(Directory.EnumerateFileSystemEntries(_options.WorkDir));
        Assert.Null(items[0].LocalPath);
    }

    [Fact]
    public async Task One_failing_database_does_not_stop_others_and_secret_is_redacted()
    {
        await SaveSettings(s => s);
        _provider.Databases = ["app", "broken_db", "shop"];
        await AddConnection();
        var runId = await Create(new LocalFolderStorage(_remoteRoot)).RunScheduledAsync(0, "schedule", RunTarget.All);

        var items = await _db.QueryAsync<BackupRow>("SELECT * FROM backups WHERE run_id=@runId ORDER BY database_name", new { runId });
        Assert.Equal([BackupStatus.Success, BackupStatus.Error, BackupStatus.Success], items.Select(i => i.Status));
        Assert.Contains("código 2", items[1].Error);
        Assert.DoesNotContain("hunter2", items[1].Error);

        var run = await _db.QueryOneAsync<RunRow>("SELECT * FROM runs WHERE id=@runId", new { runId });
        Assert.Equal(BackupStatus.Error, run!.Status);
        Assert.Contains("Parcial", run.Message);
    }

    [Fact]
    public async Task Connection_failure_is_recorded_as_error_item()
    {
        await SaveSettings(s => s);
        _provider.ConnectionOk = false;
        await AddConnection();
        var runId = await Create(new LocalFolderStorage(_remoteRoot)).RunScheduledAsync(0, "schedule", RunTarget.All);
        var item = Assert.Single(await _db.QueryAsync<BackupRow>("SELECT * FROM backups WHERE run_id=@runId", new { runId }));
        Assert.Equal(BackupStatus.Error, item.Status);
        Assert.Contains("Acesso negado", item.Error);
        Assert.Equal(0, _executor.Calls);
    }

    [Fact]
    public async Task Transient_upload_failures_are_retried_with_limit()
    {
        await SaveSettings(s => s with { UploadMaxAttempts = 3 });
        _provider.Databases = ["app"];
        await AddConnection();
        var flaky = new FlakyStorage(new LocalFolderStorage(_remoteRoot), failures: 2);
        var runId = await Create(flaky).RunScheduledAsync(0, "schedule", RunTarget.All);
        var item = Assert.Single(await _db.QueryAsync<BackupRow>("SELECT * FROM backups WHERE run_id=@runId", new { runId }));
        Assert.Equal(BackupStatus.Success, item.Status);
        Assert.Equal(3, item.UploadAttempts);

        var failing = new FlakyStorage(new LocalFolderStorage(_remoteRoot), failures: 100);
        var run2 = await Create(failing).RunScheduledAsync(0, "schedule", RunTarget.All);
        var item2 = Assert.Single(await _db.QueryAsync<BackupRow>("SELECT * FROM backups WHERE run_id=@run2", new { run2 }));
        Assert.Equal(BackupStatus.Error, item2.Status);
        Assert.Contains("após 3 tentativa", item2.Error);
        Assert.True(failing.UploadCalls <= 3, "não deve tentar indefinidamente");
    }

    [Fact]
    public async Task Retention_count_deletes_old_remote_files_but_keeps_newest()
    {
        await SaveSettings(s => s with { RetentionMode = "count", RetentionCount = 1 });
        _provider.Databases = ["app"];
        await AddConnection();
        var storage = new LocalFolderStorage(_remoteRoot);
        var orchestrator = Create(storage);
        await orchestrator.RunScheduledAsync(0, "schedule", RunTarget.All);
        await Task.Delay(1100); // distinct timestamp in file name
        await orchestrator.RunScheduledAsync(0, "schedule", RunTarget.All);

        var rows = await _db.QueryAsync<BackupRow>("SELECT * FROM backups ORDER BY finished_at");
        Assert.Equal(2, rows.Count);
        Assert.NotNull(rows[0].RemoteDeletedAt);
        Assert.Null(rows[1].RemoteDeletedAt);
        var files = Directory.EnumerateFiles(_remoteRoot, "*.gz", SearchOption.AllDirectories).ToList();
        Assert.Single(files);
    }

    [Fact]
    public async Task Selected_databases_only_and_missing_selected_reported()
    {
        await SaveSettings(s => s);
        await AddConnection(backupAll: false, selected: ["shop", "gone"]);
        var runId = await Create(new LocalFolderStorage(_remoteRoot)).RunScheduledAsync(0, "schedule", RunTarget.All);
        var items = await _db.QueryAsync<BackupRow>("SELECT * FROM backups WHERE run_id=@runId ORDER BY database_name", new { runId });
        Assert.Equal(["gone", "shop"], items.Select(i => i.DatabaseName));
        Assert.Equal(BackupStatus.Error, items[0].Status);
        Assert.Equal(BackupStatus.Success, items[1].Status);
    }

    [Fact]
    public async Task Keep_local_copy_moves_file_to_local_area()
    {
        await SaveSettings(s => s with { KeepLocalCopy = true, KeepLocalDays = 3 });
        _provider.Databases = ["app"];
        await AddConnection();
        var runId = await Create(new LocalFolderStorage(_remoteRoot)).RunScheduledAsync(0, "schedule", RunTarget.All);
        var item = Assert.Single(await _db.QueryAsync<BackupRow>("SELECT * FROM backups WHERE run_id=@runId", new { runId }));
        Assert.NotNull(item.LocalPath);
        Assert.True(File.Exists(item.LocalPath));
        Assert.StartsWith(Path.GetFullPath(_options.LocalCopiesDir), Path.GetFullPath(item.LocalPath!));
    }

    [Fact]
    public async Task Manual_run_is_rejected_while_another_runs()
    {
        await SaveSettings(s => s);
        await AddConnection();
        var lockObj = new BackupLock(_options.LockFile);
        var orchestrator = new BackupOrchestrator(_db, _options, _settings, _connections, new ProviderRegistry([_provider]), _executor,
            new FixedStorageFactory(new LocalFolderStorage(_remoteRoot)), new AgeEncryptor(),
            new NotificationDispatcher([], NullLogger<NotificationDispatcher>.Instance), new EventLog(_db, NullLogger<EventLog>.Instance),
            lockObj, NullLogger<BackupOrchestrator>.Instance);
        using var held = await lockObj.TryAcquireAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(held);
        await Assert.ThrowsAsync<BackupBusyException>(() => orchestrator.StartManualAsync(RunTarget.All));
    }

    [Fact]
    public async Task Recovery_marks_interrupted_runs_and_cleans_work_dirs()
    {
        await _db.ExecuteAsync("INSERT INTO runs(id, trigger, target_type, status, started_at) VALUES ('r1','manual','all','running',1)");
        await _db.ExecuteAsync("INSERT INTO backups(id, run_id, connection_name, db_type, database_name, status, created_at) VALUES ('b1','r1','c','fake','db','running',1)");
        var runDir = Path.Combine(_options.WorkDir, "run_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDir);
        File.WriteAllText(Path.Combine(runDir, "partial.sql.gz"), "x");

        await Create(new LocalFolderStorage(_remoteRoot)).RecoverInterruptedAsync();

        Assert.Equal(BackupStatus.Error, await _db.QueryOneAsync<string>("SELECT status FROM backups WHERE id='b1'"));
        Assert.Contains("reiniciado", await _db.QueryOneAsync<string>("SELECT message FROM runs WHERE id='r1'"));
        Assert.False(Directory.Exists(runDir));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _tmp.Dispose();
    }
}
