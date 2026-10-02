using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace VpsBackupManager.Providers;

/// <summary>
/// Microsoft SQL Server (Linux, Docker or Windows host reachable over TCP).
///
/// SQL Server writes backups server-side, so the flow is:
///   1. <c>BACKUP DATABASE @db TO DISK = @file WITH COPY_ONLY, CHECKSUM, INIT, FORMAT</c>
///      into a folder shared between SQL Server and this app (bind mount);
///   2. optional <c>RESTORE VERIFYONLY ... WITH CHECKSUM</c>;
///   3. the .bak is streamed from the shared folder into the compressor (gzip/zstd) and deleted.
/// COPY_ONLY keeps any existing backup chain (log shipping, other jobs) untouched.
///
/// Options:
///   server_backup_dir  folder as SQL Server sees it      (default /var/opt/mssql/backup)
///   local_backup_dir   same folder inside this container (default /mssql-backups)
///   verify_backup      run RESTORE VERIFYONLY             (default true)
///   native_compression BACKUP ... WITH COMPRESSION        (default false; not available on Express)
/// </summary>
public sealed partial class SqlServerProvider : IDatabaseBackupProvider
{
    public const string DefaultServerDir = "/var/opt/mssql/backup";
    public const string DefaultLocalDir = "/mssql-backups";

    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase) { "master", "model", "msdb" };

    public IReadOnlyList<string> Types => ["sqlserver"];
    public string Family => "sqlserver";
    public int DefaultPort => 1433;

    internal static SqlConnectionStringBuilder BuildConnectionString(DbConnectionInfo conn, string database = "master") => new()
    {
        DataSource = $"tcp:{ProviderHelpers.ValidateHost(conn.Host)},{conn.Port}",
        UserID = conn.Username,
        Password = conn.Password,
        InitialCatalog = database,
        ConnectTimeout = 15,
        Pooling = false,
        ApplicationName = "vps-backup-manager",
        // SQL Server in Docker uses a self-signed certificate: "disabled/preferred/required" trust it,
        // "verify_*" require a valid certificate chain.
        Encrypt = conn.SslMode == "disabled" ? SqlConnectionEncryptOption.Optional : SqlConnectionEncryptOption.Mandatory,
        TrustServerCertificate = conn.SslMode is not ("verify_ca" or "verify_identity"),
    };

    public async Task<TestResult> TestConnectionAsync(DbConnectionInfo conn, CancellationToken ct)
    {
        try
        {
            await using var c = new SqlConnection(BuildConnectionString(conn).ConnectionString);
            await c.OpenAsync(ct);
            await using var cmd = new SqlCommand("SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)) + ' ' + CAST(SERVERPROPERTY('Edition') AS nvarchar(128))", c);
            var version = Convert.ToString(await cmd.ExecuteScalarAsync(ct));
            var shared = CheckSharedFolder(conn);
            return new TestResult(true, "Conexão realizada com sucesso." + (shared is null ? "" : " Atenção: " + shared), version);
        }
        catch (SqlException ex)
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
            await using var c = new SqlConnection(BuildConnectionString(conn).ConnectionString);
            await c.OpenAsync(ct);
            // ONLINE databases only; tempdb (id 2) can never be backed up.
            await using var cmd = new SqlCommand("SELECT name FROM sys.databases WHERE state = 0 AND database_id <> 2 ORDER BY name", c);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var result = new List<string>();
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                if (!includeSystem && SystemDatabases.Contains(name)) continue;
                result.Add(name);
            }
            return result;
        }
        catch (SqlException ex)
        {
            throw new ProviderException(Friendly(ex), ex);
        }
    }

    /// <summary>Describes the backup (no external process). Executed by <see cref="ExecuteDumpAsync"/>.</summary>
    public DumpCommand BuildDumpCommand(DbConnectionInfo conn, string database, string workDir)
    {
        ProviderHelpers.ValidateDatabaseName(database);
        var (serverDir, localDir) = Folders(conn);
        var fileName = TempBackupFileName(database);
        return new DumpCommand("sqlserver:BACKUP DATABASE", [ServerPath(serverDir, fileName), Path.Combine(localDir, fileName)],
            new Dictionary<string, string>(), "bak", [], TimeSpan.FromMinutes(conn.OptInt("timeout_minutes", 0)));
    }

    public async Task<DumpOutcome> ExecuteDumpAsync(DumpCommand cmd, DbConnectionInfo conn, string database, Stream sink,
        IDumpExecutor executor, CancellationToken ct)
    {
        var serverFile = cmd.Args[0];
        var localFile = cmd.Args[1];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (cmd.Timeout > TimeSpan.Zero) cts.CancelAfter(cmd.Timeout);
        var timeoutSeconds = cmd.Timeout > TimeSpan.Zero ? (int)Math.Min(int.MaxValue, cmd.Timeout.TotalSeconds) : 0;
        var warnings = new StringBuilder();

        var shared = CheckSharedFolder(conn);
        if (shared is not null) throw new ProviderException(shared);

        try
        {
            await using (var c = new SqlConnection(BuildConnectionString(conn).ConnectionString))
            {
                c.InfoMessage += (_, e) => { if (e.Errors.Cast<SqlError>().Any(x => x.Class > 10)) warnings.AppendLine(e.Message); };
                await c.OpenAsync(cts.Token);

                var withOptions = "COPY_ONLY, CHECKSUM, INIT, FORMAT" + (conn.OptBool("native_compression", false) ? ", COMPRESSION" : "");
                await using (var backup = new SqlCommand($"BACKUP DATABASE @db TO DISK = @file WITH {withOptions}", c))
                {
                    backup.CommandTimeout = timeoutSeconds;
                    backup.Parameters.Add(new SqlParameter("@db", System.Data.SqlDbType.NVarChar, 128) { Value = database });
                    backup.Parameters.Add(new SqlParameter("@file", System.Data.SqlDbType.NVarChar, 1024) { Value = serverFile });
                    await backup.ExecuteNonQueryAsync(cts.Token);
                }

                if (conn.OptBool("verify_backup", true))
                {
                    try
                    {
                        await using var verify = new SqlCommand("RESTORE VERIFYONLY FROM DISK = @file WITH CHECKSUM", c);
                        verify.CommandTimeout = timeoutSeconds;
                        verify.Parameters.Add(new SqlParameter("@file", System.Data.SqlDbType.NVarChar, 1024) { Value = serverFile });
                        await verify.ExecuteNonQueryAsync(cts.Token);
                    }
                    catch (SqlException ex) when (ex.Number is 262 or 229 or 3110 or 15247)
                    {
                        warnings.AppendLine("RESTORE VERIFYONLY não executado (usuário sem permissão CREATE DATABASE).");
                    }
                }
            }

            if (!File.Exists(localFile))
                throw new ProviderException(
                    $"O SQL Server gravou o backup em {serverFile}, mas o arquivo não apareceu em {localFile}. " +
                    "A pasta precisa ser compartilhada entre o SQL Server e o backup manager (docs/04-bancos.md).");

            return await StreamFileAsync(localFile, sink, warnings.ToString().Trim(), cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DumpOutcome(-1, 0, "", [], [], "Tempo limite excedido.", true);
        }
        catch (SqlException ex)
        {
            return new DumpOutcome(ex.Number == 0 ? 1 : ex.Number, 0, "", [], [], Friendly(ex), false);
        }
        finally
        {
            try
            {
                if (File.Exists(localFile) && TempFilePattern().IsMatch(Path.GetFileName(localFile))) File.Delete(localFile);
            }
            catch { /* reported by the next run if it persists */ }
        }
    }

    public ValidationResult ValidateBackup(byte[] head, byte[] tail, string stderr)
    {
        // Microsoft Tape Format: every SQL Server .bak starts with the "TAPE" descriptor block.
        if (head.Length < 4 || Encoding.ASCII.GetString(head, 0, 4) != "TAPE")
            return new(false, "Arquivo .bak sem cabeçalho MTF 'TAPE' (backup inválido ou corrompido).");
        return new(true, "", string.IsNullOrWhiteSpace(stderr) ? null : stderr.Trim());
    }

    // ------------------------------------------------------------------------------------- helpers
    private static async Task<DumpOutcome> StreamFileAsync(string path, Stream sink, string warnings, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        var head = new MemoryStream();
        long total = 0;
        int read;
        while ((read = await file.ReadAsync(buffer, ct)) > 0)
        {
            await sink.WriteAsync(buffer.AsMemory(0, read), ct);
            hash.AppendData(buffer, 0, read);
            if (head.Length < 64) head.Write(buffer, 0, (int)Math.Min(read, 64 - head.Length));
            total += read;
        }
        return new DumpOutcome(0, total, Convert.ToHexStringLower(hash.GetHashAndReset()), head.ToArray(), [], warnings, false);
    }

    internal static (string ServerDir, string LocalDir) Folders(DbConnectionInfo conn)
    {
        var serverDir = (conn.OptString("server_backup_dir") ?? DefaultServerDir).Trim();
        var localDir = (conn.OptString("local_backup_dir") ?? DefaultLocalDir).Trim();
        if (serverDir.Length == 0 || serverDir.Contains("..") || serverDir.IndexOfAny(['\0', '\n', '\r', '\'']) >= 0)
            throw new ProviderException("Pasta de backup do SQL Server inválida.");
        if (!Path.IsPathRooted(localDir) || localDir.Contains(".."))
            throw new ProviderException("Pasta compartilhada no backup manager deve ser um caminho absoluto.");
        return (serverDir, localDir);
    }

    /// <summary>Joins using the separator style of the SQL Server path (Linux "/" or Windows "\").</summary>
    internal static string ServerPath(string serverDir, string fileName)
    {
        var sep = serverDir.Contains('\\') && !serverDir.Contains('/') ? '\\' : '/';
        return serverDir.TrimEnd('/', '\\') + sep + fileName;
    }

    internal static string TempBackupFileName(string database) =>
        $"vbm_{Backup.BackupNaming.Slug(database)}_{Guid.NewGuid():N}.bak";

    /// <summary>Returns a problem description when the shared folder is not usable, null when OK.</summary>
    private static string? CheckSharedFolder(DbConnectionInfo conn)
    {
        var (_, localDir) = Folders(conn);
        if (!Directory.Exists(localDir))
            return $"pasta compartilhada {localDir} não existe no backup manager. Monte a mesma pasta do SQL Server (docs/04-bancos.md).";
        return null;
    }

    private static string Friendly(SqlException ex) => ex.Number switch
    {
        18456 => "Login falhou: usuário ou senha incorretos.",
        4060 => "Banco não existe ou o usuário não tem acesso a ele.",
        262 or 3110 or 229 => "Permissão negada: o usuário precisa do papel db_backupoperator no banco.",
        3201 => "SQL Server não conseguiu gravar o arquivo de backup: verifique a pasta e as permissões. " + Security.Redactor.Truncate(ex.Message, 300),
        -2 => "Tempo esgotado na operação do SQL Server.",
        2 or 26 or 40 or 53 or 111 or 113 or 1225 or 10054 or 10060 or 10061 or 11001 or 35 => "Não foi possível conectar ao servidor (host/porta inacessível ou serviço parado).",
        _ => $"Erro SQL Server ({ex.Number}): {Security.Redactor.Truncate(ex.Message, 300)}",
    };

    [GeneratedRegex("^vbm_[A-Za-z0-9_.-]+_[0-9a-f]{32}\\.bak$")]
    private static partial Regex TempFilePattern();
}
