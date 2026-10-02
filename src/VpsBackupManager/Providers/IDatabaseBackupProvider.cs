using System.Text.Json;
using System.Text.RegularExpressions;

namespace VpsBackupManager.Providers;

/// <summary>Expected, user-facing failure (wrong password, host unreachable, …).</summary>
public sealed class ProviderException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class DbConnectionInfo
{
    public long? Id { get; init; }
    public required string Name { get; init; }
    public required string DbType { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }
    public string SslMode { get; init; } = "disabled";
    public Dictionary<string, JsonElement> Options { get; init; } = new();

    public bool OptBool(string key, bool fallback) =>
        Options.TryGetValue(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;

    public int OptInt(string key, int fallback) =>
        Options.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;

    public string? OptString(string key) =>
        Options.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public override string ToString() => $"{Name} ({DbType} {Host}:{Port})"; // never includes the password
}

public sealed record TestResult(bool Ok, string Message, string? ServerVersion = null);

public sealed record DumpCommand(
    string FileName,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env,
    string Extension,
    IReadOnlyList<string> SecretFiles,
    TimeSpan Timeout);

public sealed record ValidationResult(bool Ok, string Message = "", string? Warning = null);

public sealed record DumpOutcome(int ExitCode, long RawSize, string RawSha256, byte[] Head, byte[] Tail, string Stderr, bool TimedOut);

/// <summary>Streams a dump command's stdout into a sink (the compressor).</summary>
public interface IDumpExecutor
{
    Task<DumpOutcome> ExecuteAsync(DumpCommand command, Stream sink, CancellationToken ct);
}

/// <summary>
/// One implementation per database engine. New engines (MongoDB, Redis, SQL Server, SQLite…) are added
/// by implementing this interface and registering it in <see cref="ProviderRegistry"/>.
/// </summary>
public interface IDatabaseBackupProvider
{
    /// <summary>Accepted values for DbConnectionInfo.DbType (e.g. "mysql", "mariadb").</summary>
    IReadOnlyList<string> Types { get; }
    /// <summary>Folder name in the remote layout (e.g. "mysql", "postgresql").</summary>
    string Family { get; }
    int DefaultPort { get; }

    Task<TestResult> TestConnectionAsync(DbConnectionInfo conn, CancellationToken ct);
    Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnectionInfo conn, bool includeSystem, CancellationToken ct);
    DumpCommand BuildDumpCommand(DbConnectionInfo conn, string database, string workDir);
    ValidationResult ValidateBackup(byte[] head, byte[] tail, string stderr);

    /// <summary>Optional server-level dump (roles/tablespaces). Null when unsupported or disabled.</summary>
    DumpCommand? BuildGlobalsCommand(DbConnectionInfo conn, string workDir) => null;
    bool WantsGlobals(DbConnectionInfo conn) => false;
    ValidationResult ValidateGlobals(byte[] head, byte[] tail, string stderr) => new(head.Length > 0, "Saída vazia");

    /// <summary>Builds the command and streams the dump into <paramref name="sink"/>.</summary>
    async Task<DumpOutcome> CreateBackupAsync(DbConnectionInfo conn, string database, string workDir, Stream sink,
        IDumpExecutor executor, CancellationToken ct)
    {
        var cmd = BuildDumpCommand(conn, database, workDir);
        try
        {
            return await ExecuteDumpAsync(cmd, conn, database, sink, executor, ct);
        }
        finally
        {
            ProviderHelpers.DeleteSecretFiles(cmd.SecretFiles);
        }
    }

    /// <summary>
    /// Runs a command built by <see cref="BuildDumpCommand"/>. Default: external tool streaming to stdout.
    /// Engines whose backup is produced server-side (SQL Server BACKUP DATABASE) override this.
    /// </summary>
    Task<DumpOutcome> ExecuteDumpAsync(DumpCommand cmd, DbConnectionInfo conn, string database, Stream sink,
        IDumpExecutor executor, CancellationToken ct) => executor.ExecuteAsync(cmd, sink, ct);
}

public static partial class ProviderHelpers
{
    public static readonly string[] SslModes = ["disabled", "preferred", "required", "verify_ca", "verify_identity"];

    public static string ValidateHost(string host)
    {
        host = (host ?? "").Trim();
        if (!HostPattern().IsMatch(host) || host.StartsWith('-')) throw new ProviderException("Host inválido.");
        return host;
    }

    /// <summary>Database names come from the server but are still checked before reaching argv/env.</summary>
    public static string ValidateDatabaseName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128 || name.StartsWith('-') ||
            name.IndexOfAny(['\0', '\n', '\r']) >= 0)
            throw new ProviderException($"Nome de banco não suportado: {name}");
        return name;
    }

    /// <summary>Creates a credentials file readable only by the app user (O_EXCL + 0600).</summary>
    public static string WriteSecretFile(string path, string content)
    {
        var fso = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) fso.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var fs = new FileStream(path, fso);
        using var w = new StreamWriter(fs);
        w.Write(content);
        return path;
    }

    public static void DeleteSecretFiles(IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best effort; run dir is removed later */ }
        }
    }

    public static string UniqueSecretPath(string workDir, string prefix) =>
        Path.Combine(workDir, $".{prefix}-{Guid.NewGuid():N}");

    [GeneratedRegex(@"^[A-Za-z0-9._:\-\[\]]{1,253}$")]
    private static partial Regex HostPattern();
}
