using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using MySqlConnector;
using VpsBackupManager.Services;

namespace VpsBackupManager.Providers;

/// <summary>
/// MySQL and MariaDB. Dumps with mariadb-dump/mysqldump using --single-transaction (consistent
/// snapshot for InnoDB without locking). The password is passed through a 0600
/// --defaults-extra-file, never on the command line.
/// </summary>
public sealed partial class MySqlProvider(string flavor) : IDatabaseBackupProvider
{
    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase)
        { "information_schema", "performance_schema", "mysql", "sys" };
    private static readonly HashSet<string> NeverDump = new(StringComparer.OrdinalIgnoreCase)
        { "information_schema", "performance_schema" };
    private static readonly ConcurrentDictionary<string, bool> IsOracleBinaryCache = new();

    public IReadOnlyList<string> Types => [flavor];
    public string Family => flavor;
    public int DefaultPort => 3306;

    internal static MySqlConnectionStringBuilder BuildConnectionString(DbConnectionInfo conn) => new()
    {
        Server = ProviderHelpers.ValidateHost(conn.Host),
        Port = (uint)conn.Port,
        UserID = conn.Username,
        Password = conn.Password,
        ConnectionTimeout = 10,
        DefaultCommandTimeout = 30,
        Pooling = false,
        SslMode = conn.SslMode switch
        {
            "preferred" => MySqlSslMode.Preferred,
            "required" => MySqlSslMode.Required,
            "verify_ca" => MySqlSslMode.VerifyCA,
            "verify_identity" => MySqlSslMode.VerifyFull,
            _ => MySqlSslMode.None,
        },
        SslCa = conn.OptString("ssl_ca") ?? "",
        // caching_sha2_password over a non-TLS link needs the server RSA key. Only enabled when TLS is off,
        // which should only happen on loopback / private Docker networks.
        AllowPublicKeyRetrieval = conn.SslMode is "disabled" or "preferred",
    };

    public async Task<TestResult> TestConnectionAsync(DbConnectionInfo conn, CancellationToken ct)
    {
        try
        {
            await using var c = new MySqlConnection(BuildConnectionString(conn).ConnectionString);
            await c.OpenAsync(ct);
            await using var cmd = new MySqlCommand("SELECT VERSION()", c);
            var version = Convert.ToString(await cmd.ExecuteScalarAsync(ct));
            return new TestResult(true, "Conexão realizada com sucesso.", version);
        }
        catch (MySqlException ex)
        {
            return new TestResult(false, Friendly(ex));
        }
        catch (ProviderException ex)
        {
            return new TestResult(false, ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnectionInfo conn, bool includeSystem, CancellationToken ct)
    {
        try
        {
            await using var c = new MySqlConnection(BuildConnectionString(conn).ConnectionString);
            await c.OpenAsync(ct);
            await using var cmd = new MySqlCommand("SHOW DATABASES", c);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var result = new List<string>();
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                if (NeverDump.Contains(name)) continue;
                if (!includeSystem && SystemDatabases.Contains(name)) continue;
                result.Add(name);
            }
            return result.OrderBy(n => n, StringComparer.Ordinal).ToList();
        }
        catch (MySqlException ex)
        {
            throw new ProviderException(Friendly(ex), ex);
        }
    }

    public DumpCommand BuildDumpCommand(DbConnectionInfo conn, string database, string workDir)
    {
        ProviderHelpers.ValidateDatabaseName(database);
        if (NeverDump.Contains(database)) throw new ProviderException($"O schema {database} não pode ser exportado.");
        var host = ProviderHelpers.ValidateHost(conn.Host);
        var binary = ResolveBinary(conn);
        var oracle = IsOracleMysqldump(binary);

        var defaultsFile = ProviderHelpers.UniqueSecretPath(workDir, "my");
        ProviderHelpers.WriteSecretFile(defaultsFile, BuildOptionFile(host, conn.Port, conn.Username, conn.Password));

        var args = new List<string>
        {
            $"--defaults-extra-file={defaultsFile}", // must be the first argument
            "--protocol=TCP",
            "--quick",
            "--hex-blob",
            "--default-character-set=utf8mb4",
            "--no-tablespaces",
        };
        if (conn.OptBool("single_transaction", true)) args.Add("--single-transaction");
        if (conn.OptBool("routines", true)) args.Add("--routines");
        if (conn.OptBool("events", true)) args.Add("--events");
        args.Add(conn.OptBool("triggers", true) ? "--triggers" : "--skip-triggers");
        var packet = conn.OptString("max_allowed_packet") ?? "512M";
        if (!PacketSize().IsMatch(packet)) throw new ProviderException("max_allowed_packet inválido (ex.: 512M).");
        args.Add($"--max-allowed-packet={packet}");

        if (oracle)
        {
            args.Add("--column-statistics=0");
            if (conn.OptBool("set_gtid_purged_off", true)) args.Add("--set-gtid-purged=OFF");
        }
        args.AddRange(SslArgs(conn, oracle));
        args.Add(database);

        return new DumpCommand(binary, args, new Dictionary<string, string>(), "sql", [defaultsFile],
            TimeSpan.FromMinutes(conn.OptInt("timeout_minutes", 0)));
    }

    public ValidationResult ValidateBackup(byte[] head, byte[] tail, string stderr)
    {
        if (head.Length == 0) return new(false, "Dump vazio.");
        var tailText = Encoding.UTF8.GetString(tail);
        if (!tailText.Contains("-- Dump completed", StringComparison.Ordinal))
            return new(false, "Dump incompleto: marcador final '-- Dump completed' ausente (dump interrompido?).");
        var warning = string.IsNullOrWhiteSpace(stderr) ? null : stderr.Trim();
        return new(true, "", warning);
    }

    internal static string BuildOptionFile(string host, int port, string user, string password)
    {
        if (password.IndexOfAny(['\n', '\r', '\0']) >= 0 || user.IndexOfAny(['\n', '\r', '\0']) >= 0)
            throw new ProviderException("Usuário/senha contém caracteres de controle não suportados.");
        static string Q(string v) => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        return $"[client]\nhost={Q(host)}\nport={port}\nuser={Q(user)}\npassword={Q(password)}\n";
    }

    private static IEnumerable<string> SslArgs(DbConnectionInfo conn, bool oracle)
    {
        var ca = conn.OptString("ssl_ca");
        if (oracle)
        {
            yield return "--ssl-mode=" + conn.SslMode switch
            {
                "preferred" => "PREFERRED",
                "required" => "REQUIRED",
                "verify_ca" => "VERIFY_CA",
                "verify_identity" => "VERIFY_IDENTITY",
                _ => "DISABLED",
            };
        }
        else
        {
            switch (conn.SslMode)
            {
                case "disabled": yield return "--skip-ssl"; break;
                case "required": yield return "--ssl"; break;
                case "verify_ca":
                case "verify_identity":
                    yield return "--ssl";
                    yield return "--ssl-verify-server-cert";
                    break;
            }
        }
        if (!string.IsNullOrEmpty(ca) && conn.SslMode is not "disabled") yield return $"--ssl-ca={ca}";
    }

    private static string ResolveBinary(DbConnectionInfo conn)
    {
        var configured = conn.OptString("dump_binary");
        if (!string.IsNullOrEmpty(configured) && configured != "auto")
        {
            if (configured is not ("mariadb-dump" or "mysqldump"))
                throw new ProviderException("dump_binary deve ser auto, mariadb-dump ou mysqldump.");
            return configured;
        }
        return ProcessRunner.CommandExists("mariadb-dump") ? "mariadb-dump" : "mysqldump";
    }

    private static bool IsOracleMysqldump(string binary)
    {
        if (binary == "mariadb-dump") return false;
        return IsOracleBinaryCache.GetOrAdd(binary, b =>
        {
            try
            {
                var r = ProcessRunner.RunAsync(b, ["--version"], timeout: TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                return r.ExitCode == 0 && !r.Stdout.Contains("MariaDB", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        });
    }

    private static string Friendly(MySqlException ex) => ex.ErrorCode switch
    {
        MySqlErrorCode.AccessDenied => "Acesso negado: usuário ou senha incorretos (ou host não autorizado para este usuário).",
        MySqlErrorCode.UnableToConnectToHost => "Não foi possível conectar ao servidor (host/porta inacessível ou serviço parado).",
        _ => $"Erro MySQL/MariaDB: {Security.Redactor.Truncate(ex.Message, 300)}",
    };

    [GeneratedRegex(@"^\d{1,10}[KMG]?$")]
    private static partial Regex PacketSize();
}
