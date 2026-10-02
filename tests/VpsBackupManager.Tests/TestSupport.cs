using System.Text;
using VpsBackupManager.Providers;
using VpsBackupManager.Storage;

namespace VpsBackupManager.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vbm-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Sub(string name)
    {
        var p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* best effort */ }
    }
}

/// <summary>Provider that never touches a real database. Databases named "broken*" fail to dump.</summary>
public sealed class FakeProvider : IDatabaseBackupProvider
{
    public List<string> Databases { get; set; } = ["app", "shop"];
    public bool ConnectionOk { get; set; } = true;

    public IReadOnlyList<string> Types => ["fake"];
    public string Family => "fake";
    public int DefaultPort => 1;

    public Task<TestResult> TestConnectionAsync(DbConnectionInfo conn, CancellationToken ct) =>
        Task.FromResult(new TestResult(ConnectionOk, ConnectionOk ? "ok" : "Acesso negado"));

    public Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnectionInfo conn, bool includeSystem, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Databases);

    public DumpCommand BuildDumpCommand(DbConnectionInfo conn, string database, string workDir)
    {
        var secret = ProviderHelpers.WriteSecretFile(ProviderHelpers.UniqueSecretPath(workDir, "fake"), conn.Password);
        return new DumpCommand("fake-dump", [database], new Dictionary<string, string>(), "sql", [secret], TimeSpan.Zero);
    }

    public ValidationResult ValidateBackup(byte[] head, byte[] tail, string stderr) =>
        Encoding.UTF8.GetString(tail).Contains("-- Dump completed") ? new(true) : new(false, "marcador ausente");
}

/// <summary>Writes a deterministic SQL "dump"; simulates failures for databases named broken*.</summary>
public sealed class FakeDumpExecutor : IDumpExecutor
{
    public int Calls;

    public async Task<DumpOutcome> ExecuteAsync(DumpCommand command, Stream sink, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        var db = command.Args[0];
        if (db.StartsWith("broken"))
            return new DumpOutcome(2, 0, "", [], [], "mysqldump: Got error: 1045: Access denied (password=hunter2)", false);
        var sb = new StringBuilder();
        sb.AppendLine($"-- fake dump of {db}");
        for (var i = 0; i < 2000; i++) sb.AppendLine($"INSERT INTO t VALUES ({i}, 'linha {i}');");
        sb.AppendLine("-- Dump completed on 2026-10-01");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        await sink.WriteAsync(bytes, ct);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        return new DumpOutcome(0, bytes.Length, sha, bytes[..64], bytes[^Math.Min(8192, bytes.Length)..], "", false);
    }
}

/// <summary>Local storage that fails the first N uploads with a transient error.</summary>
public sealed class FlakyStorage(IStorageProvider inner, int failures) : IStorageProvider
{
    private int _remaining = failures;
    public int UploadCalls;

    public string Describe(string relativePath = "") => inner.Describe(relativePath);
    public Task<TestResult> TestConnectionAsync(CancellationToken ct) => inner.TestConnectionAsync(ct);

    public Task UploadAsync(string localPath, string relativePath, CancellationToken ct)
    {
        Interlocked.Increment(ref UploadCalls);
        if (Interlocked.Decrement(ref _remaining) >= 0) throw new StorageException("Sem conectividade (simulado)", true);
        return inner.UploadAsync(localPath, relativePath, ct);
    }

    public Task<RemoteFileInfo?> StatAsync(string relativePath, CancellationToken ct) => inner.StatAsync(relativePath, ct);
    public Task DeleteAsync(string relativePath, CancellationToken ct) => inner.DeleteAsync(relativePath, ct);
    public Task<IReadOnlyList<RemoteFileInfo>> ListBackupsAsync(string relativeDirectory, CancellationToken ct) => inner.ListBackupsAsync(relativeDirectory, ct);
}

public sealed class FixedStorageFactory(IStorageProvider storage) : IStorageFactory
{
    public IStorageProvider Create(Services.AppSettings settings) => storage;
}
