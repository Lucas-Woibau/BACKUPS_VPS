using System.Text;
using System.Text.Json;
using VpsBackupManager.Backup;
using VpsBackupManager.Providers;
using VpsBackupManager.Services;
using VpsBackupManager.Storage;

namespace VpsBackupManager.Tests;

public class SqlServerProviderTests
{
    private static DbConnectionInfo Conn(Dictionary<string, JsonElement>? options = null, string ssl = "preferred") => new()
    {
        Name = "VPS - Banco 1", DbType = "sqlserver", Host = "mssql", Port = 1433, Username = "backup_user",
        Password = "S3nha;Forte'", SslMode = ssl, Options = options ?? new(),
    };

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Connection_string_trusts_self_signed_unless_verify()
    {
        var cs = SqlServerProvider.BuildConnectionString(Conn());
        Assert.Equal("tcp:mssql,1433", cs.DataSource);
        Assert.True(cs.TrustServerCertificate);
        Assert.False(SqlServerProvider.BuildConnectionString(Conn(ssl: "verify_identity")).TrustServerCertificate);
        Assert.Equal("S3nha;Forte'", cs.Password); // builder escapes; never concatenated by hand
    }

    [Fact]
    public void Dump_command_points_to_shared_folder_with_unique_bak_name()
    {
        var cmd = new SqlServerProvider().BuildDumpCommand(Conn(), "Loja Produção", "/tmp");
        Assert.Equal("bak", cmd.Extension);
        Assert.StartsWith("/var/opt/mssql/backup/vbm_Loja_Producao_", cmd.Args[0]);
        Assert.EndsWith(".bak", cmd.Args[0]);
        Assert.Equal(Path.GetFileName(cmd.Args[0]), Path.GetFileName(cmd.Args[1]));
        Assert.Empty(cmd.SecretFiles);
        Assert.DoesNotContain(cmd.Args, a => a.Contains("S3nha"));
    }

    [Fact]
    public void Windows_style_server_folder_is_supported()
    {
        Assert.Equal(@"D:\Backups\x.bak", SqlServerProvider.ServerPath(@"D:\Backups\", "x.bak"));
        Assert.Equal("/var/opt/mssql/backup/x.bak", SqlServerProvider.ServerPath("/var/opt/mssql/backup/", "x.bak"));
    }

    [Fact]
    public void Folder_options_are_validated()
    {
        Assert.Throws<ProviderException>(() => SqlServerProvider.Folders(Conn(new() { ["server_backup_dir"] = J("\"/var/../etc\"") })));
        Assert.Throws<ProviderException>(() => SqlServerProvider.Folders(Conn(new() { ["local_backup_dir"] = J("\"relative/dir\"") })));
        Assert.Throws<ProviderException>(() => SqlServerProvider.Folders(Conn(new() { ["server_backup_dir"] = J("\"/x'; DROP--\"") })));
        Assert.Throws<ProviderException>(() => new SqlServerProvider().BuildDumpCommand(Conn(), "-evil", "/tmp"));
    }

    [Fact]
    public void Bak_validation_requires_mtf_header()
    {
        var p = new SqlServerProvider();
        Assert.True(p.ValidateBackup(Encoding.ASCII.GetBytes("TAPE\0\0\0\0"), [], "").Ok);
        Assert.False(p.ValidateBackup(Encoding.ASCII.GetBytes("PGDMP"), [], "").Ok);
        Assert.False(p.ValidateBackup([], [], "").Ok);
    }

    [Fact]
    public async Task Missing_shared_folder_fails_with_clear_message()
    {
        var conn = Conn(new() { ["local_backup_dir"] = J("\"/definitely/not/here-vbm\"") });
        var p = new SqlServerProvider();
        var cmd = p.BuildDumpCommand(conn, "loja", "/tmp");
        var ex = await Assert.ThrowsAsync<ProviderException>(() => p.ExecuteDumpAsync(cmd, conn, "loja", Stream.Null, null!, CancellationToken.None));
        Assert.Contains("compartilhada", ex.Message);
    }

    [Fact]
    public void Registry_includes_sqlserver()
    {
        var r = ProviderRegistry.CreateDefault();
        Assert.Equal("sqlserver", r.Get("sqlserver").Family);
        Assert.Equal(1433, r.Get("sqlserver").DefaultPort);
    }
}

public class DriveFolderAndRcloneConfigTests
{
    private static readonly DateTime T = new(2026, 10, 1, 3, 0, 0);

    [Fact]
    public void Connection_drive_folder_replaces_vps_folder_and_keeps_order()
    {
        var s = new AppSettings { UseVpsSubfolder = true, VpsFolderName = "VPS-Producao" };
        Assert.Equal("VPS/Banco1/2026/10/01/sqlserver/VPS_-_Banco_1",
            BackupNaming.RemoteDirectory(s, "sqlserver", "VPS - Banco 1", T, "/VPS/Banco1/"));
        Assert.Equal("VPS-Producao/2026/10/01/sqlserver/x", BackupNaming.RemoteDirectory(s, "sqlserver", "x", T, ""));
        Assert.Throws<PathGuard.UnsafePathException>(() => BackupNaming.RemoteDirectory(s, "sqlserver", "x", T, "../fora"));
    }

    [Fact]
    public void Deep_custom_folder_is_still_deletable_by_retention_and_bak_matches()
    {
        PathGuard.EnsureDeletableRemoteBackup("Clientes/VPS/Banco1/2026/10/01/sqlserver/VPS_-_Banco_1/loja_2026-10-01_03-00-00.bak.gz");
        Assert.True(PathGuard.IsBackupArtifactName("loja_2026-10-01_03-00-00.bak.gz.age.sha256"));
    }

    [Fact]
    public void Rclone_config_writer_replaces_only_target_section()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "rclone.conf");
        File.WriteAllText(path, "[s3]\ntype = s3\nprovider = AWS\n\n[gdrive]\ntype = drive\ntoken = {\"old\":1}\n");
        RcloneConfigFile.WriteDriveRemote(path, "gdrive", "{\"access_token\":\"a\",\"refresh_token\":\"r\",\"expiry\":\"2026-10-01T00:00:00Z\"}");
        var text = File.ReadAllText(path);
        Assert.Contains("[s3]\ntype = s3", text);
        Assert.DoesNotContain("\"old\"", text);
        Assert.Contains("scope = drive.file", text);
        Assert.Contains("\"refresh_token\":\"r\"", text);
        Assert.Single(text.Split('\n'), l => l == "[gdrive]");
        Assert.True(RcloneConfigFile.HasRemote(path, "gdrive"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Rclone_config_writer_rejects_bad_input()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "rclone.conf");
        Assert.Throws<StorageException>(() => RcloneConfigFile.WriteDriveRemote(path, "g drive]", "{}"));
        Assert.Throws<StorageException>(() => RcloneConfigFile.WriteDriveRemote(path, "gdrive", "{\"a\":1}\n[evil]"));
        Assert.ThrowsAny<JsonException>(() => RcloneConfigFile.WriteDriveRemote(path, "gdrive", "{not json"));
    }
}

public class DriveTokenParsingTests
{
    private const string Token = "{\"access_token\":\"ya29.x\",\"token_type\":\"Bearer\",\"refresh_token\":\"1//r\",\"expiry\":\"2026-10-02T12:00:00Z\"}";

    [Fact]
    public void Raw_json_token_is_accepted() =>
        Assert.Contains("ya29.x", DriveAuthService.TryExtractToken(Token));

    [Fact]
    public void Base64_config_blob_token_is_decoded()
    {
        var blob = JsonSerializer.Serialize(new Dictionary<string, string> { ["token"] = Token });
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(blob)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var json = DriveAuthService.TryExtractToken(b64);
        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.Equal("1//r", doc.RootElement.GetProperty("refresh_token").GetString());
        Assert.DoesNotContain('\n', json!);
    }

    [Theory]
    [InlineData("Paste the following into your remote machine --->")]
    [InlineData("<---End paste")]
    [InlineData("2026/10/02 NOTICE: Got code")]
    public void Other_lines_are_ignored(string line) => Assert.Null(DriveAuthService.TryExtractToken(line));
}
