using System.Text;
using System.Text.Json;
using VpsBackupManager.Backup;
using VpsBackupManager.Providers;
using VpsBackupManager.Security;
using VpsBackupManager.Services;

namespace VpsBackupManager.Tests;

public class SecurityTests
{
    [Fact]
    public void SecretBox_roundtrip_and_tamper_detection()
    {
        var box = new SecretBox(new string('k', 48));
        var enc = box.Encrypt("s3nh@ forte");
        Assert.DoesNotContain("s3nh@", enc);
        Assert.Equal("s3nh@ forte", box.Decrypt(enc));
        Assert.NotEqual(enc, box.Encrypt("s3nh@ forte")); // random nonce

        var other = new SecretBox(new string('x', 48));
        Assert.Throws<InvalidOperationException>(() => other.Decrypt(enc));
        var tampered = enc[..^4] + (enc[^4] == 'A' ? "B" : "A") + enc[^3..];
        Assert.Throws<InvalidOperationException>(() => box.Decrypt(tampered));
    }

    [Fact]
    public void Argon2id_hash_verifies_and_rejects()
    {
        var hash = PasswordHasher.Hash("Correct-Horse-9");
        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=2$", hash);
        Assert.True(PasswordHasher.Verify(hash, "Correct-Horse-9"));
        Assert.False(PasswordHasher.Verify(hash, "correct-horse-9"));
        Assert.False(PasswordHasher.Verify("garbage", "x"));
    }

    [Fact]
    public void Password_strength_rules()
    {
        Assert.NotEmpty(PasswordHasher.ValidateStrength("short", "admin"));
        Assert.NotEmpty(PasswordHasher.ValidateStrength("onlylowercaseletters", "admin"));
        Assert.NotEmpty(PasswordHasher.ValidateStrength("Admin-Password-1", "admin"));
        Assert.Empty(PasswordHasher.ValidateStrength("Tr0ub4dor&Horse", "admin"));
    }

    [Fact]
    public void Redactor_masks_known_patterns_and_registered_literals()
    {
        Redactor.Register("SuperSecretValue123");
        var text = Redactor.Redact("password=abc123 token: xyz connecting mysql://bob:pw@host failed SuperSecretValue123 \"refresh_token\":\"r-1\"");
        Assert.DoesNotContain("abc123", text);
        Assert.DoesNotContain("xyz", text);
        Assert.DoesNotContain(":pw@", text);
        Assert.DoesNotContain("SuperSecretValue123", text);
        Assert.DoesNotContain("r-1", text);
    }

    [Fact]
    public void LoginThrottle_blocks_after_max_failures()
    {
        var t = new LoginThrottle(maxFailures: 3, windowSeconds: 60);
        Assert.False(t.IsBlocked("ip:1"));
        t.Fail("ip:1"); t.Fail("ip:1"); t.Fail("ip:1");
        Assert.True(t.IsBlocked("ip:1", "user:x"));
        t.Reset("ip:1");
        Assert.False(t.IsBlocked("ip:1"));
    }
}

public class ProviderTests
{
    private static DbConnectionInfo Conn(string type, string password = "p\"a\\ss:w$rd`$(rm -rf /)", Dictionary<string, JsonElement>? options = null) => new()
    {
        Name = "main", DbType = type, Host = "host.docker.internal", Port = type == "postgresql" ? 5432 : 3306,
        Username = "backup_user", Password = password, Options = options ?? new(),
    };

    [Fact]
    public void MySql_password_goes_to_0600_option_file_not_argv()
    {
        using var tmp = new TempDir();
        var conn = Conn("mysql", options: new() { ["dump_binary"] = JsonDocument.Parse("\"mariadb-dump\"").RootElement });
        var cmd = new MySqlProvider("mysql").BuildDumpCommand(conn, "shop", tmp.Path);
        Assert.Equal("mariadb-dump", cmd.FileName);
        Assert.DoesNotContain(cmd.Args, a => a.Contains(conn.Password));
        Assert.StartsWith("--defaults-extra-file=", cmd.Args[0]);
        Assert.Equal("shop", cmd.Args[^1]);
        Assert.Contains("--single-transaction", cmd.Args);
        Assert.Contains("--skip-ssl", cmd.Args);

        var file = Assert.Single(cmd.SecretFiles);
        var content = File.ReadAllText(file);
        Assert.Contains("password=\"p\\\"a\\\\ss:w$rd`$(rm -rf /)\"", content);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        ProviderHelpers.DeleteSecretFiles(cmd.SecretFiles);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void MySql_rejects_option_injection_in_database_name()
    {
        using var tmp = new TempDir();
        Assert.Throws<ProviderException>(() => new MySqlProvider("mysql").BuildDumpCommand(Conn("mysql"), "--result-file=/etc/passwd", tmp.Path));
        Assert.Throws<ProviderException>(() => new MySqlProvider("mysql").BuildDumpCommand(Conn("mysql"), "information_schema", tmp.Path));
        var evilHost = new DbConnectionInfo { Name = "x", DbType = "mysql", Host = "-oProxyCommand=x", Port = 1, Username = "u", Password = "p" };
        Assert.Throws<ProviderException>(() => new MySqlProvider("mysql").BuildDumpCommand(evilHost, "db", tmp.Path));
    }

    [Fact]
    public void MySql_validation_requires_completion_marker()
    {
        var p = new MySqlProvider("mysql");
        Assert.True(p.ValidateBackup("-- MySQL dump"u8.ToArray(), "\n-- Dump completed on 2026-10-01\n"u8.ToArray(), "").Ok);
        Assert.False(p.ValidateBackup("-- MySQL dump"u8.ToArray(), "INSERT INTO x VALUES (1"u8.ToArray(), "").Ok);
        Assert.False(p.ValidateBackup([], [], "").Ok);
        Assert.Equal("Warning: x", p.ValidateBackup("a"u8.ToArray(), "-- Dump completed"u8.ToArray(), "Warning: x").Warning);
    }

    [Fact]
    public void Postgres_uses_env_and_pgpass_and_custom_format()
    {
        using var tmp = new TempDir();
        var conn = Conn("postgresql");
        var cmd = new PostgreSqlProvider().BuildDumpCommand(conn, "crm", tmp.Path);
        Assert.Equal("pg_dump", cmd.FileName);
        Assert.Equal("dump", cmd.Extension);
        Assert.Contains("--format=custom", cmd.Args);
        Assert.DoesNotContain(cmd.Args, a => a.Contains("crm") || a.Contains(conn.Password));
        Assert.Equal("crm", cmd.Env["PGDATABASE"]);
        Assert.Equal("disable", cmd.Env["PGSSLMODE"]);
        Assert.DoesNotContain(cmd.Env.Values, v => v.Contains(conn.Password));
        var pgpass = File.ReadAllText(cmd.Env["PGPASSFILE"]);
        Assert.Equal("*:*:*:backup_user:p\"a\\\\ss\\:w$rd`$(rm -rf /)\n", pgpass);
    }

    [Fact]
    public void Postgres_globals_only_when_enabled_and_validation()
    {
        using var tmp = new TempDir();
        var p = new PostgreSqlProvider();
        Assert.Null(p.BuildGlobalsCommand(Conn("postgresql"), tmp.Path));
        var withGlobals = Conn("postgresql", options: new() { ["dump_globals"] = JsonDocument.Parse("true").RootElement });
        var cmd = p.BuildGlobalsCommand(withGlobals, tmp.Path)!;
        Assert.Equal("pg_dumpall", cmd.FileName);
        Assert.Contains("--globals-only", cmd.Args);
        Assert.Contains("--no-role-passwords", cmd.Args);

        Assert.True(p.ValidateBackup("PGDMP\x01\x0e"u8.ToArray(), [], "").Ok);
        Assert.False(p.ValidateBackup("-- SQL"u8.ToArray(), [], "").Ok);
    }

    [Fact]
    public void Registry_resolves_types()
    {
        var r = ProviderRegistry.CreateDefault();
        Assert.Equal("mysql", r.Get("mysql").Family);
        Assert.Equal("mariadb", r.Get("MariaDB").Family);
        Assert.Equal("postgresql", r.Get("postgresql").Family);
        Assert.Throws<ProviderException>(() => r.Get("mongodb"));
    }
}

public class CompressionAndSettingsTests
{
    [Theory]
    [InlineData("gzip", 6)]
    [InlineData("zstd", 3)]
    [InlineData("none", 1)]
    public async Task Compression_roundtrip_verifies_content(string kind, int level)
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "x.bin");
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("INSERT INTO t VALUES (1,'abc');\n", 5000)));
        await using (var fs = File.Create(path))
        await using (var w = Compression.OpenWriter(kind, level, fs))
            await w.WriteAsync(data);
        var (size, sha) = await Compression.VerifyAsync(path, kind, CancellationToken.None);
        Assert.Equal(data.Length, size);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data)), sha);
        if (kind != "none") Assert.True(new FileInfo(path).Length < data.Length / 5);
    }

    [Fact]
    public async Task Truncated_gzip_is_detected()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "x.gz");
        await using (var fs = File.Create(path))
        await using (var w = Compression.OpenWriter("gzip", 6, fs))
            await w.WriteAsync(System.Security.Cryptography.RandomNumberGenerator.GetBytes(200_000));
        var bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, bytes[..(bytes.Length / 2)]);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var (size, _) = await Compression.VerifyAsync(path, "gzip", CancellationToken.None);
            if (size != 200_000) throw new InvalidDataException("truncated");
        });
    }

    [Fact]
    public void Settings_validation()
    {
        Assert.Empty(SettingsService.Validate(new AppSettings()));
        Assert.NotEmpty(SettingsService.Validate(new AppSettings { Timezone = "Mars/Olympus" }));
        Assert.NotEmpty(SettingsService.Validate(new AppSettings { RemoteBasePath = "../x" }));
        Assert.NotEmpty(SettingsService.Validate(new AppSettings { RemoteBasePath = "" }));
        Assert.NotEmpty(SettingsService.Validate(new AppSettings { RcloneRemote = "gdrive:evil" }));
        Assert.NotEmpty(SettingsService.Validate(new AppSettings { EncryptionEnabled = true }));
        Assert.NotEmpty(SettingsService.Validate(new AppSettings { EncryptionEnabled = true, AgeRecipients = ["not-a-key"] }));
        Assert.Empty(SettingsService.Validate(new AppSettings
        {
            EncryptionEnabled = true, AgeRecipients = ["age1ql3z7hjy54pw3hyww5ayyfg7zqgvc7w3j2elw8zmrj2kg5sfn9aqmcac8p"],
        }));
        Assert.NotEmpty(SettingsService.Validate(new AppSettings { WebhookEnabled = true, WebhookUrl = "ftp://x" }));
        Assert.NotEmpty(SettingsService.Validate(new AppSettings { Compression = "gzip", CompressionLevel = 15 }));
    }
}
