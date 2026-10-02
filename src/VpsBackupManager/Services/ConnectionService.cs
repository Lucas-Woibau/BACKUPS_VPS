using System.Text.Json;
using VpsBackupManager.Backup;
using VpsBackupManager.Config;
using VpsBackupManager.Data;
using VpsBackupManager.Providers;
using VpsBackupManager.Security;

namespace VpsBackupManager.Services;

public sealed record ConnectionInput(
    string Name,
    string DbType,
    string Host,
    int Port,
    string Username,
    string? Password,
    string SslMode,
    Dictionary<string, JsonElement>? Options,
    bool BackupAll,
    bool IncludeSystem,
    List<string>? SelectedDatabases,
    List<string>? ExcludedDatabases,
    bool Enabled);

public sealed class ConnectionService(Db db, SecretBox secretBox, ProviderRegistry registry, AppOptions options)
{
    private static readonly HashSet<string> AllowedOptions =
    [
        "dump_binary", "single_transaction", "routines", "events", "triggers", "max_allowed_packet",
        "set_gtid_purged_off", "ssl_ca", "timeout_minutes", "maintenance_db", "dump_globals",
        "globals_no_role_passwords", "ssl_root_cert", "lock_wait_timeout_seconds",
        "drive_folder", "server_backup_dir", "local_backup_dir", "verify_backup", "native_compression",
    ];

    public Task<IReadOnlyList<ConnectionRow>> ListAsync() =>
        db.QueryAsync<ConnectionRow>("SELECT * FROM connections ORDER BY name");

    public Task<ConnectionRow?> GetAsync(long id) =>
        db.QueryOneAsync<ConnectionRow>("SELECT * FROM connections WHERE id = @id", new { id });

    public DbConnectionInfo ToInfo(ConnectionRow row)
    {
        var password = secretBox.Decrypt(row.PasswordEnc);
        Redactor.Register(password);
        return new DbConnectionInfo
        {
            Id = row.Id, Name = row.Name, DbType = row.DbType, Host = row.Host, Port = (int)row.Port,
            Username = row.Username, Password = password, SslMode = row.SslMode,
            Options = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.Options) ?? new(),
        };
    }

    /// <summary>Builds a DbConnectionInfo from an unsaved form (test before saving).
    /// If the password is omitted and an id is given, the stored password is reused.</summary>
    public async Task<DbConnectionInfo> FromInputAsync(ConnectionInput input, long? existingId)
    {
        Validate(input, requirePassword: existingId is null);
        var password = input.Password;
        if (string.IsNullOrEmpty(password) && existingId is not null)
        {
            var row = await GetAsync(existingId.Value) ?? throw new KeyNotFoundException();
            password = secretBox.Decrypt(row.PasswordEnc);
        }
        Redactor.Register(password);
        return new DbConnectionInfo
        {
            Id = existingId, Name = input.Name.Trim(), DbType = input.DbType, Host = input.Host.Trim(), Port = input.Port,
            Username = input.Username.Trim(), Password = password ?? "", SslMode = input.SslMode, Options = input.Options ?? new(),
        };
    }

    public async Task<long> CreateAsync(ConnectionInput input)
    {
        Validate(input, requirePassword: true);
        var now = Clock.NowMs();
        return await db.InsertAsync(
            """
            INSERT INTO connections(name, db_type, host, port, username, password_enc, ssl_mode, options, backup_all,
                include_system, selected_databases, excluded_databases, enabled, created_at, updated_at)
            VALUES (@Name, @DbType, @Host, @Port, @Username, @PasswordEnc, @SslMode, @Options, @BackupAll,
                @IncludeSystem, @Selected, @Excluded, @Enabled, @now, @now)
            """,
            Params(input, secretBox.Encrypt(input.Password!), now));
    }

    public async Task UpdateAsync(long id, ConnectionInput input)
    {
        Validate(input, requirePassword: false);
        var existing = await GetAsync(id) ?? throw new KeyNotFoundException();
        var enc = string.IsNullOrEmpty(input.Password) ? existing.PasswordEnc : secretBox.Encrypt(input.Password);
        await db.ExecuteAsync(
            """
            UPDATE connections SET name=@Name, db_type=@DbType, host=@Host, port=@Port, username=@Username,
                password_enc=@PasswordEnc, ssl_mode=@SslMode, options=@Options, backup_all=@BackupAll,
                include_system=@IncludeSystem, selected_databases=@Selected, excluded_databases=@Excluded,
                enabled=@Enabled, updated_at=@now
            WHERE id=@id
            """,
            WithId(Params(input, enc, Clock.NowMs()), id));
    }

    public Task DeleteAsync(long id) => db.ExecuteAsync("DELETE FROM connections WHERE id = @id", new { id });

    public Task SaveTestResultAsync(long id, bool ok, string message) =>
        db.ExecuteAsync("UPDATE connections SET last_test_ok=@ok, last_test_at=@now, last_test_message=@message WHERE id=@id",
            new { id, ok, now = Clock.NowMs(), message = Redactor.Truncate(message, 500) });

    public Task SaveDiscoveredAsync(long id, IEnumerable<string> databases) =>
        db.ExecuteAsync("UPDATE connections SET discovered_databases=@d WHERE id=@id",
            new { id, d = JsonSerializer.Serialize(databases) });

    /// <summary>Databases that should be backed up for this connection right now.</summary>
    public static IReadOnlyList<string> ResolveTargets(ConnectionRow row, IReadOnlyList<string> onServer)
    {
        if (row.BackupAll)
        {
            var excluded = JsonSerializer.Deserialize<List<string>>(row.ExcludedDatabases) ?? [];
            return onServer.Where(d => !excluded.Contains(d, StringComparer.Ordinal)).ToList();
        }
        var selected = JsonSerializer.Deserialize<List<string>>(row.SelectedDatabases) ?? [];
        return selected.Where(d => onServer.Contains(d, StringComparer.Ordinal)).ToList();
    }

    public static IReadOnlyList<string> MissingSelected(ConnectionRow row, IReadOnlyList<string> onServer)
    {
        if (row.BackupAll) return [];
        var selected = JsonSerializer.Deserialize<List<string>>(row.SelectedDatabases) ?? [];
        return selected.Where(d => !onServer.Contains(d, StringComparer.Ordinal)).ToList();
    }

    public static int ProtectedCount(ConnectionRow row)
    {
        if (!row.Enabled) return 0;
        if (!row.BackupAll) return (JsonSerializer.Deserialize<List<string>>(row.SelectedDatabases) ?? []).Count;
        var discovered = JsonSerializer.Deserialize<List<string>>(row.DiscoveredDatabases) ?? [];
        return ResolveTargets(row, discovered).Count;
    }

    private void Validate(ConnectionInput i, bool requirePassword)
    {
        var e = new List<string>();
        if (string.IsNullOrWhiteSpace(i.Name) || i.Name.Trim().Length > 80) e.Add("Nome obrigatório (máx. 80).");
        if (!registry.IsSupported(i.DbType)) e.Add($"Tipo não suportado. Use: {string.Join(", ", registry.SupportedTypes)}.");
        try { ProviderHelpers.ValidateHost(i.Host); } catch (ProviderException ex) { e.Add(ex.Message); }
        if (i.Port is < 1 or > 65535) e.Add("Porta inválida.");
        if (string.IsNullOrWhiteSpace(i.Username) || i.Username.Length > 128) e.Add("Usuário obrigatório.");
        if (requirePassword && string.IsNullOrEmpty(i.Password)) e.Add("Senha obrigatória.");
        if (i.Password is { Length: > 512 }) e.Add("Senha longa demais.");
        if (!ProviderHelpers.SslModes.Contains(i.SslMode)) e.Add("Modo SSL inválido.");
        foreach (var (key, value) in i.Options ?? new())
        {
            if (!AllowedOptions.Contains(key)) { e.Add($"Opção desconhecida: {key}"); continue; }
            if (key == "drive_folder" && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                && !PathGuard.IsSafeRelativeRemotePath(value.GetString()!.Trim().Trim('/'), allowEmpty: false))
                e.Add("Pasta no Google Drive inválida (use nomes simples separados por '/', sem '..' ou ':').");
            if (key == "local_backup_dir" && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                && (!value.GetString()!.StartsWith('/') || value.GetString()!.Contains("..")))
                e.Add("Pasta compartilhada do SQL Server no backup manager deve ser absoluta (ex.: /mssql-backups).");
            if (key is "ssl_ca" or "ssl_root_cert" && value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString()))
            {
                try { PathGuard.EnsureInside(Path.Combine(options.DataDir, "certs"), value.GetString()!); }
                catch (PathGuard.UnsafePathException) { e.Add($"{key} deve ficar dentro de {Path.Combine(options.DataDir, "certs")}."); }
            }
        }
        foreach (var d in (i.SelectedDatabases ?? []).Concat(i.ExcludedDatabases ?? []))
        {
            try { ProviderHelpers.ValidateDatabaseName(d); } catch (ProviderException ex) { e.Add(ex.Message); }
        }
        if (!i.BackupAll && (i.SelectedDatabases is null || i.SelectedDatabases.Count == 0))
            e.Add("Selecione ao menos um banco ou marque 'todos os bancos'.");
        if (e.Count > 0) throw new ValidationException(e);
    }

    private static Dictionary<string, object?> Params(ConnectionInput i, string passwordEnc, long now) => new()
    {
        ["Name"] = i.Name.Trim(), ["DbType"] = i.DbType, ["Host"] = i.Host.Trim(), ["Port"] = i.Port,
        ["Username"] = i.Username.Trim(), ["PasswordEnc"] = passwordEnc, ["SslMode"] = i.SslMode,
        ["Options"] = JsonSerializer.Serialize(i.Options ?? new()), ["BackupAll"] = i.BackupAll,
        ["IncludeSystem"] = i.IncludeSystem,
        ["Selected"] = JsonSerializer.Serialize((i.SelectedDatabases ?? []).Distinct().ToList()),
        ["Excluded"] = JsonSerializer.Serialize((i.ExcludedDatabases ?? []).Distinct().ToList()),
        ["Enabled"] = i.Enabled, ["now"] = now,
    };

    private static Dictionary<string, object?> WithId(Dictionary<string, object?> values, long id)
    {
        values["id"] = id;
        return values;
    }
}
