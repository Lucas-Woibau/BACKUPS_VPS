using System.Text;
using Npgsql;

namespace VpsBackupManager.Providers;

/// <summary>
/// PostgreSQL. Each database is dumped with `pg_dump --format=custom --compress=0` (the app compresses
/// the stream; custom format allows selective/parallel pg_restore). Optional `pg_dumpall --globals-only`
/// exports roles and tablespaces. The password is passed via a 0600 PGPASSFILE; connection parameters
/// via PG* environment variables (no connection string parsing of user data in argv).
/// </summary>
public sealed class PostgreSqlProvider : IDatabaseBackupProvider
{
    public const string GlobalsName = "pg-globals";

    public IReadOnlyList<string> Types => ["postgresql"];
    public string Family => "postgresql";
    public int DefaultPort => 5432;

    internal static NpgsqlConnectionStringBuilder BuildConnectionString(DbConnectionInfo conn) => new()
    {
        Host = ProviderHelpers.ValidateHost(conn.Host),
        Port = conn.Port,
        Username = conn.Username,
        Password = conn.Password,
        Database = conn.OptString("maintenance_db") ?? "postgres",
        Timeout = 10,
        CommandTimeout = 30,
        Pooling = false,
        ApplicationName = "vps-backup-manager",
        SslMode = conn.SslMode switch
        {
            "preferred" => SslMode.Prefer,
            "required" => SslMode.Require,
            "verify_ca" => SslMode.VerifyCA,
            "verify_identity" => SslMode.VerifyFull,
            _ => SslMode.Disable,
        },
        RootCertificate = conn.OptString("ssl_root_cert"),
    };

    public async Task<TestResult> TestConnectionAsync(DbConnectionInfo conn, CancellationToken ct)
    {
        try
        {
            await using var c = new NpgsqlConnection(BuildConnectionString(conn).ConnectionString);
            await c.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SHOW server_version", c);
            var version = Convert.ToString(await cmd.ExecuteScalarAsync(ct));
            return new TestResult(true, "Conexão realizada com sucesso.", version);
        }
        catch (Exception ex) when (ex is NpgsqlException or PostgresException or ProviderException)
        {
            return new TestResult(false, Friendly(ex));
        }
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnectionInfo conn, bool includeSystem, CancellationToken ct)
    {
        try
        {
            await using var c = new NpgsqlConnection(BuildConnectionString(conn).ConnectionString);
            await c.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT datname FROM pg_database WHERE datallowconn AND NOT datistemplate ORDER BY datname", c);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var result = new List<string>();
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                if (!includeSystem && name == "postgres") continue;
                result.Add(name);
            }
            return result;
        }
        catch (Exception ex) when (ex is NpgsqlException or PostgresException)
        {
            throw new ProviderException(Friendly(ex), ex);
        }
    }

    public DumpCommand BuildDumpCommand(DbConnectionInfo conn, string database, string workDir)
    {
        ProviderHelpers.ValidateDatabaseName(database);
        var (env, passFile) = BuildEnv(conn, workDir, database);
        var args = new List<string> { "--format=custom", "--compress=0", "--no-password" };
        var lockWait = conn.OptInt("lock_wait_timeout_seconds", 0);
        if (lockWait > 0) args.Add($"--lock-wait-timeout={lockWait}s");
        return new DumpCommand("pg_dump", args, env, "dump", [passFile], TimeSpan.FromMinutes(conn.OptInt("timeout_minutes", 0)));
    }

    public bool WantsGlobals(DbConnectionInfo conn) => conn.OptBool("dump_globals", false);

    public DumpCommand? BuildGlobalsCommand(DbConnectionInfo conn, string workDir)
    {
        if (!WantsGlobals(conn)) return null;
        var maintenance = ProviderHelpers.ValidateDatabaseName(conn.OptString("maintenance_db") ?? "postgres");
        var (env, passFile) = BuildEnv(conn, workDir, maintenance);
        var args = new List<string> { "--globals-only", "--no-password", "-l", maintenance };
        if (conn.OptBool("globals_no_role_passwords", true)) args.Add("--no-role-passwords");
        return new DumpCommand("pg_dumpall", args, env, "sql", [passFile], TimeSpan.FromMinutes(30));
    }

    public ValidationResult ValidateBackup(byte[] head, byte[] tail, string stderr)
    {
        if (head.Length < 5 || Encoding.ASCII.GetString(head, 0, 5) != "PGDMP")
            return new(false, "Arquivo não possui cabeçalho PGDMP de pg_dump (formato custom).");
        return new(true, "", string.IsNullOrWhiteSpace(stderr) ? null : stderr.Trim());
    }

    public ValidationResult ValidateGlobals(byte[] head, byte[] tail, string stderr)
    {
        var text = Encoding.UTF8.GetString(tail);
        return text.Contains("PostgreSQL database cluster dump complete", StringComparison.Ordinal)
            ? new(true, "", string.IsNullOrWhiteSpace(stderr) ? null : stderr.Trim())
            : new(false, "pg_dumpall incompleto (marcador final ausente).");
    }

    private static (Dictionary<string, string> Env, string PassFile) BuildEnv(DbConnectionInfo conn, string workDir, string database)
    {
        var host = ProviderHelpers.ValidateHost(conn.Host);
        var passFile = ProviderHelpers.UniqueSecretPath(workDir, "pgpass");
        ProviderHelpers.WriteSecretFile(passFile, BuildPgPassLine(conn.Username, conn.Password));
        var env = new Dictionary<string, string>
        {
            ["PGHOST"] = host,
            ["PGPORT"] = conn.Port.ToString(),
            ["PGUSER"] = conn.Username,
            ["PGDATABASE"] = database,
            ["PGPASSFILE"] = passFile,
            ["PGCONNECT_TIMEOUT"] = "15",
            ["PGAPPNAME"] = "vps-backup-manager",
            ["PGSSLMODE"] = conn.SslMode switch
            {
                "preferred" => "prefer",
                "required" => "require",
                "verify_ca" => "verify-ca",
                "verify_identity" => "verify-full",
                _ => "disable",
            },
        };
        var rootCert = conn.OptString("ssl_root_cert");
        if (!string.IsNullOrEmpty(rootCert)) env["PGSSLROOTCERT"] = rootCert;
        return (env, passFile);
    }

    internal static string BuildPgPassLine(string user, string password)
    {
        if (password.IndexOfAny(['\n', '\r', '\0']) >= 0 || user.IndexOfAny(['\n', '\r', '\0']) >= 0)
            throw new ProviderException("Usuário/senha contém caracteres de controle não suportados.");
        static string E(string v) => v.Replace("\\", "\\\\").Replace(":", "\\:");
        return $"*:*:*:{E(user)}:{E(password)}\n";
    }

    private static string Friendly(Exception ex) => ex switch
    {
        PostgresException { SqlState: "28P01" } => "Autenticação falhou: usuário ou senha incorretos.",
        PostgresException { SqlState: "28000" } => "Acesso negado pelo pg_hba.conf para este host/usuário.",
        PostgresException { SqlState: "3D000" } => "Banco de manutenção não existe (ajuste 'maintenance_db').",
        NpgsqlException { InnerException: System.Net.Sockets.SocketException } =>
            "Não foi possível conectar ao servidor (host/porta inacessível ou serviço parado).",
        TimeoutException or NpgsqlException { InnerException: TimeoutException } => "Tempo esgotado ao conectar.",
        _ => $"Erro PostgreSQL: {Security.Redactor.Truncate(ex.Message, 300)}",
    };
}
