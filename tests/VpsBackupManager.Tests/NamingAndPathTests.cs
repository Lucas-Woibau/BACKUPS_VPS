using VpsBackupManager.Backup;
using VpsBackupManager.Services;

namespace VpsBackupManager.Tests;

public class BackupNamingTests
{
    private static readonly DateTime T = new(2026, 10, 1, 3, 0, 0);

    [Fact]
    public void FileName_contains_database_date_and_time()
    {
        Assert.Equal("ecommerce_2026-10-01_03-00-00.sql.gz", BackupNaming.FileName("ecommerce", T, "sql", "gzip", false));
        Assert.Equal("crm_2026-10-01_03-00-00.dump.zst.age", BackupNaming.FileName("crm", T, "dump", "zstd", true));
        Assert.Equal("crm_2026-10-01_03-00-00_ab12cd34.dump", BackupNaming.FileName("crm", T, "dump", "none", false, "ab12cd34"));
    }

    [Theory]
    [InlineData("Sistema Financeiro", "Sistema_Financeiro")]
    [InlineData("ação/../../etc", "acao_.._.._etc")]
    [InlineData("../", "db")]
    [InlineData("wp-site.prod", "wp-site.prod")]
    public void Slug_is_filesystem_and_path_safe(string input, string expected)
    {
        var slug = BackupNaming.Slug(input);
        Assert.Equal(expected, slug);
        Assert.DoesNotContain('/', slug);
        Assert.True(PathGuard.IsSafeSegment(slug));
    }

    [Fact]
    public void RemoteDirectory_follows_dated_layout()
    {
        var s = new AppSettings { UseVpsSubfolder = true, VpsFolderName = "VPS-Producao" };
        Assert.Equal("VPS-Producao/2026/10/01/mysql/MySQL_principal", BackupNaming.RemoteDirectory(s, "mysql", "MySQL principal", T));
        Assert.Equal("2026/10/01/postgresql/pg", BackupNaming.RemoteDirectory(s with { UseVpsSubfolder = false }, "postgresql", "pg", T));
    }

    [Fact]
    public void Generated_names_match_the_deletion_pattern()
    {
        foreach (var name in new[]
                 {
                     BackupNaming.FileName("db", T, "sql", "gzip", false),
                     BackupNaming.FileName("db", T, "dump", "zstd", true, "deadbeef"),
                     BackupNaming.SidecarName(BackupNaming.FileName("db", T, "sql", "none", false)),
                 })
            Assert.True(PathGuard.IsBackupArtifactName(name), name);
    }

    [Fact]
    public void ToLocal_uses_configured_timezone()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        var local = BackupNaming.ToLocal(new DateTimeOffset(2026, 10, 1, 6, 0, 0, TimeSpan.Zero), tz);
        Assert.Equal(new DateTime(2026, 10, 1, 3, 0, 0), local);
    }
}

public class PathGuardTests
{
    [Theory]
    [InlineData("Backups")]
    [InlineData("Backups/VPS-Producao")]
    [InlineData("Meus Backups/2026")]
    public void Safe_remote_paths_are_accepted(string path) => Assert.True(PathGuard.IsSafeRelativeRemotePath(path, false));

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/etc")]
    [InlineData("../x")]
    [InlineData("a/../../b")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    [InlineData("other:remote")]
    [InlineData("a/./b")]
    [InlineData("a/b/")]
    public void Unsafe_remote_paths_are_rejected(string path) => Assert.False(PathGuard.IsSafeRelativeRemotePath(path, false));

    [Fact]
    public void Remote_delete_requires_dated_layout_and_backup_name()
    {
        PathGuard.EnsureDeletableRemoteBackup("VPS/2026/10/01/mysql/main/shop_2026-10-01_03-00-00.sql.gz");
        PathGuard.EnsureDeletableRemoteBackup("2026/10/01/mysql/main/shop_2026-10-01_03-00-00.sql.gz.sha256");

        Assert.Throws<PathGuard.UnsafePathException>(() => PathGuard.EnsureDeletableRemoteBackup("shop_2026-10-01_03-00-00.sql.gz"));
        Assert.Throws<PathGuard.UnsafePathException>(() => PathGuard.EnsureDeletableRemoteBackup("VPS/2026/10/01/mysql/main/important.docx"));
        Assert.Throws<PathGuard.UnsafePathException>(() => PathGuard.EnsureDeletableRemoteBackup("VPS/notes/10/01/mysql/main/shop_2026-10-01_03-00-00.sql.gz"));
        Assert.Throws<PathGuard.UnsafePathException>(() => PathGuard.EnsureDeletableRemoteBackup("../2026/10/01/mysql/main/shop_2026-10-01_03-00-00.sql.gz"));
        Assert.Throws<PathGuard.UnsafePathException>(() => PathGuard.EnsureDeletableRemoteBackup("VPS/2026/10/01/mysql/main/"));
    }

    [Fact]
    public void EnsureInside_blocks_traversal_and_root_itself()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("work");
        Assert.EndsWith("x", PathGuard.EnsureInside(root, Path.Combine(root, "x")));
        Assert.Throws<PathGuard.UnsafePathException>(() => PathGuard.EnsureInside(root, root));
        Assert.Throws<PathGuard.UnsafePathException>(() => PathGuard.EnsureInside(root, Path.Combine(root, "..", "other")));
        Assert.Throws<PathGuard.UnsafePathException>(() => PathGuard.EnsureInside(root, root + "-sibling"));
    }

    [Fact]
    public void DeleteRunDirectory_only_deletes_run_dirs_inside_work_root()
    {
        using var tmp = new TempDir();
        var work = tmp.Sub("work");
        var run = Path.Combine(work, "run_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, "f.sql.gz"), "x");
        Assert.True(PathGuard.DeleteRunDirectory(work, run));
        Assert.False(Directory.Exists(run));

        var other = tmp.Sub("work/important");
        Assert.Throws<PathGuard.UnsafePathException>(() => PathGuard.DeleteRunDirectory(work, other));
        Assert.True(Directory.Exists(other));
        Assert.Throws<PathGuard.UnsafePathException>(() => PathGuard.DeleteRunDirectory(work, work));
        Assert.Throws<PathGuard.UnsafePathException>(() => PathGuard.DeleteRunDirectory(work, tmp.Path));
    }

    [Fact]
    public void DeleteFileInside_refuses_non_backup_files()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("local");
        var keep = Path.Combine(root, "notes.txt");
        File.WriteAllText(keep, "x");
        Assert.Throws<PathGuard.UnsafePathException>(() => PathGuard.DeleteFileInside(root, keep));
        Assert.True(File.Exists(keep));
        var backup = Path.Combine(root, "db_2026-10-01_03-00-00.sql.gz");
        File.WriteAllText(backup, "x");
        Assert.True(PathGuard.DeleteFileInside(root, backup));
    }
}
