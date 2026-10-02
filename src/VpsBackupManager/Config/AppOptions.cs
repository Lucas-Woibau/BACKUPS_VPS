namespace VpsBackupManager.Config;

/// <summary>
/// Infrastructure configuration read from environment variables at startup.
/// Everything the administrator can change at runtime lives in <see cref="Services.AppSettings"/>
/// (stored in SQLite and edited through the panel).
/// </summary>
public sealed class AppOptions
{
    /// <summary>Key used only by launchSettings.json / scripts/dev-run.ps1 (public). Never valid for production.</summary>
    public const string DevOnlySecretKey = "dev-only-secret-key-change-me-0123456789abcdef";

    public required string SecretKey { get; init; }
    public required string DataDir { get; init; }
    public required string BackupRoot { get; init; }
    public required string LogDir { get; init; }
    public required string RcloneConfig { get; init; }
    public bool SessionCookieSecure { get; init; }
    public int SessionTtlHours { get; init; } = 12;
    public int SessionIdleMinutes { get; init; } = 60;
    public bool TrustProxyHeaders { get; init; }
    public string TrustedProxyNetworks { get; init; } = "127.0.0.1/32,172.16.0.0/12";
    public string? DockerDiscoveryUrl { get; init; }
    public string HostGateway { get; init; } = "host.docker.internal";
    public string DefaultTimezone { get; init; } = "America/Sao_Paulo";
    public string LogLevel { get; init; } = "Information";

    public string DbPath => Path.Combine(DataDir, "app.db");
    public string WorkDir => Path.Combine(BackupRoot, "work");
    public string LocalCopiesDir => Path.Combine(BackupRoot, "local");
    public string LockFile => Path.Combine(DataDir, "backup.lock");
    public string SetupTokenFile => Path.Combine(DataDir, "setup_token");

    public static AppOptions FromEnvironment()
    {
        var secret = ReadSecret("APP_SECRET_KEY");
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32)
            throw new InvalidOperationException(
                "APP_SECRET_KEY ausente ou curta (mínimo 32 caracteres). Gere com: openssl rand -base64 48");

        var dataDir = Env("DATA_DIR", "/data")!;
        return new AppOptions
        {
            SecretKey = secret,
            DataDir = dataDir,
            BackupRoot = Env("BACKUP_ROOT", "/backups")!,
            LogDir = Env("LOG_DIR", Path.Combine(dataDir, "logs"))!,
            RcloneConfig = Env("RCLONE_CONFIG", "/config/rclone/rclone.conf")!,
            SessionCookieSecure = EnvBool("SESSION_COOKIE_SECURE", false),
            SessionTtlHours = EnvInt("SESSION_TTL_HOURS", 12),
            SessionIdleMinutes = EnvInt("SESSION_IDLE_MINUTES", 60),
            TrustProxyHeaders = EnvBool("TRUST_PROXY_HEADERS", false),
            TrustedProxyNetworks = Env("TRUSTED_PROXY_NETWORKS", "127.0.0.1/32,172.16.0.0/12")!,
            DockerDiscoveryUrl = Env("DOCKER_DISCOVERY_URL", null),
            HostGateway = Env("HOST_GATEWAY_NAME", "host.docker.internal")!,
            DefaultTimezone = Env("TZ_DEFAULT", "America/Sao_Paulo")!,
            LogLevel = Env("LOG_LEVEL", "Information")!,
        };
    }

    public void EnsureDirectories()
    {
        foreach (var dir in new[] { DataDir, BackupRoot, WorkDir, LocalCopiesDir, LogDir })
        {
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static string? Env(string name, string? fallback)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();
    }

    private static bool EnvBool(string name, bool fallback)
    {
        var v = Env(name, null);
        return v is null ? fallback : v.ToLowerInvariant() is "1" or "true" or "yes" or "on";
    }

    private static int EnvInt(string name, int fallback)
    {
        var v = Env(name, null);
        if (v is null) return fallback;
        return int.TryParse(v, out var i) ? i : throw new InvalidOperationException($"{name} deve ser inteiro");
    }

    /// <summary>Reads NAME or NAME_FILE (Docker secrets convention).</summary>
    private static string? ReadSecret(string name)
    {
        var file = Env(name + "_FILE", null);
        if (file is not null)
        {
            if (!File.Exists(file)) throw new InvalidOperationException($"Arquivo de segredo {file} não encontrado");
            return File.ReadAllText(file).Trim();
        }
        return Env(name, null);
    }
}
