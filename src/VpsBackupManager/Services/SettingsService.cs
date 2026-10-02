using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using VpsBackupManager.Config;
using VpsBackupManager.Data;
using VpsBackupManager.Security;

namespace VpsBackupManager.Services;

/// <summary>Runtime settings editable from the panel. Stored as one JSON document in `settings`.</summary>
public sealed record AppSettings
{
    // VPS
    public string VpsName { get; init; } = "VPS-Producao";
    public string? PublicIp { get; init; }
    public string Timezone { get; init; } = "America/Sao_Paulo";
    public int MinFreeDiskMb { get; init; } = 2048;

    // Compression / encryption
    public string Compression { get; init; } = "gzip";      // gzip | zstd | none
    public int CompressionLevel { get; init; } = 6;          // gzip 1-9, zstd 1-19
    public bool EncryptionEnabled { get; init; }
    public List<string> AgeRecipients { get; init; } = [];

    // Remote storage (rclone)
    public string RcloneRemote { get; init; } = "gdrive";
    public string RemoteBasePath { get; init; } = "Backups";
    public bool UseVpsSubfolder { get; init; } = true;
    public string VpsFolderName { get; init; } = "VPS-Producao";
    /// <summary>Gmail da conta conectada (informativo; preenchido pelo fluxo "Conectar Google Drive").</summary>
    public string? DriveAccountEmail { get; init; }

    // Retention (remote)
    public string RetentionMode { get; init; } = "days";     // none | days | count | gfs
    public int RetentionDays { get; init; } = 30;
    public int RetentionCount { get; init; } = 30;
    public int GfsDaily { get; init; } = 7;
    public int GfsWeekly { get; init; } = 4;
    public int GfsMonthly { get; init; } = 12;

    // Local copies
    public bool KeepLocalCopy { get; init; }
    public int KeepLocalDays { get; init; } = 3;

    // Webhook
    public bool WebhookEnabled { get; init; }
    public string? WebhookUrl { get; init; }
    public bool WebhookOnSuccess { get; init; } = true;
    public bool WebhookOnFailure { get; init; } = true;
    /// <summary>Read-only flag for the UI (the secret itself is stored encrypted elsewhere).</summary>
    public bool WebhookSecretSet { get; init; }

    // Reliability
    public int UploadMaxAttempts { get; init; } = 5;
    public int UploadInitialBackoffSeconds { get; init; } = 30;
    public int UploadMaxBackoffSeconds { get; init; } = 600;
    public int DefaultDumpTimeoutMinutes { get; init; } = 360;
    public int ScheduledLockWaitMinutes { get; init; } = 120;
    public int CatchUpHours { get; init; } = 6;
}

public sealed record DriveStatus(bool? Ok, long? CheckedAt, string? Message);

public sealed partial class SettingsService(Db db, SecretBox secretBox, AppOptions options)
{
    private const string Key = "app";
    private const string WebhookSecretKey = "webhook_secret_enc";
    private const string DriveStatusKey = "drive_status";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public async Task<AppSettings> GetAsync()
    {
        var raw = await db.QueryOneAsync<string>("SELECT value FROM settings WHERE key = @Key", new { Key });
        var settings = raw is null
            ? new AppSettings { Timezone = options.DefaultTimezone }
            : JsonSerializer.Deserialize<AppSettings>(raw, Json) ?? new AppSettings();
        var secret = await db.QueryOneAsync<string>("SELECT value FROM settings WHERE key = @k", new { k = WebhookSecretKey });
        return settings with { WebhookSecretSet = !string.IsNullOrEmpty(secret) };
    }

    public async Task<AppSettings> SaveAsync(AppSettings settings)
    {
        var errors = Validate(settings);
        if (errors.Count > 0) throw new ValidationException(errors);
        var normalized = Normalize(settings);
        await Upsert(Key, JsonSerializer.Serialize(normalized with { WebhookSecretSet = false }, Json));
        return await GetAsync();
    }

    private static AppSettings Normalize(AppSettings settings) => settings with
    {
        RemoteBasePath = settings.RemoteBasePath.Trim().Trim('/'),
        VpsFolderName = settings.VpsFolderName.Trim().Trim('/'),
        AgeRecipients = settings.AgeRecipients.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct().ToList(),
        WebhookUrl = string.IsNullOrWhiteSpace(settings.WebhookUrl) ? null : settings.WebhookUrl.Trim(),
    };

    /// <summary>
    /// Groups of settings whose change could silently destroy or redirect backups (an attacker with a session
    /// could swap the age key, shrink retention, change the destination or mute alerts). Changing any of them
    /// requires re-entering the password and is recorded/alerted.
    /// </summary>
    public static List<string> SensitiveChanges(AppSettings before, AppSettings after)
    {
        var a = Normalize(after);
        var b = Normalize(before);
        var changed = new List<string>();
        if (a.EncryptionEnabled != b.EncryptionEnabled || !a.AgeRecipients.SequenceEqual(b.AgeRecipients)) changed.Add("criptografia");
        if (a.RcloneRemote != b.RcloneRemote || a.RemoteBasePath != b.RemoteBasePath || a.UseVpsSubfolder != b.UseVpsSubfolder
            || a.VpsFolderName != b.VpsFolderName) changed.Add("destino dos backups");
        if (a.RetentionMode != b.RetentionMode || a.RetentionDays != b.RetentionDays || a.RetentionCount != b.RetentionCount
            || a.GfsDaily != b.GfsDaily || a.GfsWeekly != b.GfsWeekly || a.GfsMonthly != b.GfsMonthly) changed.Add("retenção");
        if (a.WebhookEnabled != b.WebhookEnabled || a.WebhookUrl != b.WebhookUrl || a.WebhookOnFailure != b.WebhookOnFailure)
            changed.Add("alertas (webhook)");
        return changed;
    }

    public async Task SetWebhookSecretAsync(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
            await db.ExecuteAsync("DELETE FROM settings WHERE key = @k", new { k = WebhookSecretKey });
        else
            await Upsert(WebhookSecretKey, secretBox.Encrypt(secret));
    }

    public async Task<string?> GetWebhookSecretAsync()
    {
        var enc = await db.QueryOneAsync<string>("SELECT value FROM settings WHERE key = @k", new { k = WebhookSecretKey });
        return enc is null ? null : secretBox.Decrypt(enc);
    }

    public async Task<DriveStatus> GetDriveStatusAsync()
    {
        var raw = await db.QueryOneAsync<string>("SELECT value FROM settings WHERE key = @k", new { k = DriveStatusKey });
        return raw is null ? new DriveStatus(null, null, null) : JsonSerializer.Deserialize<DriveStatus>(raw, Json)!;
    }

    public Task SetDriveStatusAsync(bool ok, string message) =>
        Upsert(DriveStatusKey, JsonSerializer.Serialize(new DriveStatus(ok, Clock.NowMs(), Redactor.Truncate(message, 500)), Json));

    private Task Upsert(string key, string value) =>
        db.ExecuteAsync(
            "INSERT INTO settings(key, value, updated_at) VALUES (@key, @value, @now) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
            new { key, value, now = Clock.NowMs() });

    public static TimeZoneInfo ResolveTimeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception) { throw new ValidationException([$"Timezone desconhecida: {id}"]); }
    }

    public static List<string> Validate(AppSettings s)
    {
        var e = new List<string>();
        if (string.IsNullOrWhiteSpace(s.VpsName) || s.VpsName.Length > 80) e.Add("Nome da VPS obrigatório (máx. 80).");
        try { TimeZoneInfo.FindSystemTimeZoneById(s.Timezone); } catch { e.Add($"Timezone inválida: {s.Timezone}"); }
        if (s.MinFreeDiskMb is < 100 or > 10_000_000) e.Add("Espaço mínimo em disco deve estar entre 100 MB e 10 TB.");
        if (s.Compression is not ("gzip" or "zstd" or "none")) e.Add("Compressão deve ser gzip, zstd ou none.");
        if (s.Compression == "gzip" && s.CompressionLevel is < 1 or > 9) e.Add("Nível gzip: 1 a 9.");
        if (s.Compression == "zstd" && s.CompressionLevel is < 1 or > 19) e.Add("Nível zstd: 1 a 19.");
        if (s.EncryptionEnabled)
        {
            if (s.AgeRecipients.Count == 0) e.Add("Criptografia ativa exige ao menos uma chave pública age (age1...).");
            foreach (var r in s.AgeRecipients)
                if (!AgeRecipient().IsMatch(r.Trim())) e.Add($"Chave pública age inválida: {Short(r)}");
        }
        if (!RemoteName().IsMatch(s.RcloneRemote ?? "")) e.Add("Remote rclone inválido (use letras, números, _ e -).");
        if (!Backup.PathGuard.IsSafeRelativeRemotePath(s.RemoteBasePath.Trim().Trim('/'), allowEmpty: false))
            e.Add("Pasta de destino inválida (não use '..', ':', '\\' ou caminhos absolutos).");
        if (s.UseVpsSubfolder && !Backup.PathGuard.IsSafeSegment(s.VpsFolderName.Trim()))
            e.Add("Subpasta da VPS inválida.");
        if (s.RetentionMode is not ("none" or "days" or "count" or "gfs")) e.Add("Modo de retenção inválido.");
        if (s.RetentionDays is < 1 or > 3650) e.Add("Retenção em dias: 1 a 3650.");
        if (s.RetentionCount is < 1 or > 10000) e.Add("Retenção por quantidade: 1 a 10000.");
        if (s.GfsDaily < 0 || s.GfsWeekly < 0 || s.GfsMonthly < 0 || s.GfsDaily + s.GfsWeekly + s.GfsMonthly == 0)
            e.Add("Retenção GFS precisa de ao menos um período.");
        if (s.KeepLocalDays is < 1 or > 365) e.Add("Dias de cópia local: 1 a 365.");
        if (s.WebhookEnabled)
        {
            if (!Uri.TryCreate(s.WebhookUrl, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
                e.Add("URL do webhook inválida.");
        }
        if (s.UploadMaxAttempts is < 1 or > 10) e.Add("Tentativas de upload: 1 a 10.");
        if (s.UploadInitialBackoffSeconds is < 1 or > 3600) e.Add("Intervalo inicial de retry: 1 a 3600 s.");
        if (s.UploadMaxBackoffSeconds < s.UploadInitialBackoffSeconds) e.Add("Intervalo máximo de retry menor que o inicial.");
        if (s.DefaultDumpTimeoutMinutes is < 1 or > 2880) e.Add("Timeout de dump: 1 a 2880 minutos.");
        if (s.ScheduledLockWaitMinutes is < 0 or > 1440) e.Add("Espera por lock: 0 a 1440 minutos.");
        if (s.CatchUpHours is < 0 or > 72) e.Add("Janela de recuperação de agendamento: 0 a 72 horas.");
        return e;
    }

    private static string Short(string s) => s.Length > 16 ? s[..16] + "…" : s;

    [GeneratedRegex("^age1[0-9a-z]{58}$")]
    private static partial Regex AgeRecipient();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    public static partial Regex RemoteName();
}

public sealed class ValidationException(IReadOnlyList<string> errors) : Exception(string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
