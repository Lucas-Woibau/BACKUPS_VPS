using System.Data;
using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace VpsBackupManager.Data;

/// <summary>
/// SQLite access (WAL mode) plus a minimal versioned migration runner.
/// Migrations are embedded SQL files named NNNN_description.sql and are applied in order, once.
/// </summary>
public sealed partial class Db
{
    private readonly string _connectionString;

    static Db()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public Db(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 30,
            Pooling = true,
        }.ToString();
    }

    public string Path { get; }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        conn.Execute("PRAGMA busy_timeout = 30000;");
        return conn;
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? param = null)
    {
        await using var conn = Open();
        return (await conn.QueryAsync<T>(sql, param)).AsList();
    }

    public async Task<T?> QueryOneAsync<T>(string sql, object? param = null)
    {
        await using var conn = Open();
        return await conn.QueryFirstOrDefaultAsync<T>(sql, param);
    }

    public async Task<int> ExecuteAsync(string sql, object? param = null)
    {
        await using var conn = Open();
        return await conn.ExecuteAsync(sql, param);
    }

    public async Task<long> InsertAsync(string sql, object? param = null)
    {
        await using var conn = Open();
        return await conn.ExecuteScalarAsync<long>(sql + "; SELECT last_insert_rowid();", param);
    }

    public bool Ping()
    {
        try
        {
            using var conn = Open();
            return conn.ExecuteScalar<long>("SELECT 1") == 1;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<string> Migrate()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var applied = new List<string>();
        using var conn = Open();
        conn.Execute("PRAGMA journal_mode = WAL;");
        conn.Execute("CREATE TABLE IF NOT EXISTS schema_migrations (version TEXT PRIMARY KEY, applied_at INTEGER NOT NULL)");
        var done = conn.Query<string>("SELECT version FROM schema_migrations").ToHashSet();

        var asm = Assembly.GetExecutingAssembly();
        var resources = asm.GetManifestResourceNames()
            .Select(n => (Name: n, Match: MigrationName().Match(n)))
            .Where(x => x.Match.Success)
            .OrderBy(x => x.Match.Groups[1].Value, StringComparer.Ordinal);

        foreach (var (name, match) in resources)
        {
            var version = match.Groups[1].Value;
            if (done.Contains(version)) continue;
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();
            using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
            conn.Execute(sql, transaction: tx);
            conn.Execute("INSERT INTO schema_migrations(version, applied_at) VALUES (@v, @t)",
                new { v = version, t = Clock.NowMs() }, tx);
            tx.Commit();
            applied.Add(name);
        }

        if (!OperatingSystem.IsWindows() && File.Exists(Path))
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return applied;
    }

    [GeneratedRegex(@"\.(\d{4})_[a-z0-9_]+\.sql$")]
    private static partial Regex MigrationName();
}

public static class Clock
{
    /// <summary>Overridable for tests.</summary>
    public static Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    public static long NowMs() => UtcNow().ToUnixTimeMilliseconds();

    public static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    public static string? IsoFromMs(long? ms) => ms is null ? null : FromMs(ms.Value).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
}
