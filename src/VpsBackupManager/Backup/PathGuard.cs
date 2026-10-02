using System.Text.RegularExpressions;

namespace VpsBackupManager.Backup;

/// <summary>
/// Every deletion (local or remote) goes through these checks. Rules:
/// local  — the target must resolve strictly inside an allowed root, must not be a symlink,
///          and run directories must match the exact naming pattern created by the app;
/// remote — the path must be relative, free of traversal, live inside the dated layout created by
///          the app, and the file name must match the backup naming pattern.
/// </summary>
public static partial class PathGuard
{
    public sealed class UnsafePathException(string message) : Exception(message);

    // ---------------------------------------------------------------- local
    public static string EnsureInside(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(candidate);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(Path.TrimEndingDirectorySeparator(full), fullRoot, comparison) ||
            !full.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            throw new UnsafePathException($"Caminho fora da área de backup: {candidate}");
        return full;
    }

    /// <summary>Deletes a run work directory (work/run_&lt;32 hex&gt;) after strict validation.</summary>
    public static bool DeleteRunDirectory(string workRoot, string runDir)
    {
        var full = EnsureInside(workRoot, runDir);
        var name = Path.GetFileName(full);
        if (!RunDirName().IsMatch(name))
            throw new UnsafePathException($"Nome de diretório de execução inesperado: {name}");
        if (!string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetFullPath(workRoot)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnsafePathException("Diretório de execução não é filho direto da área de trabalho.");
        var info = new DirectoryInfo(full);
        if (!info.Exists) return false;
        if (info.LinkTarget is not null)
            throw new UnsafePathException("Diretório de execução é um link simbólico; exclusão recusada.");
        info.Delete(recursive: true);
        return true;
    }

    /// <summary>Deletes one file inside root; refuses directories and symlinks.</summary>
    public static bool DeleteFileInside(string root, string file)
    {
        var full = EnsureInside(root, file);
        var info = new FileInfo(full);
        if (!info.Exists) return false;
        if (info.LinkTarget is not null) throw new UnsafePathException("Arquivo é link simbólico; exclusão recusada.");
        if (!IsBackupArtifactName(info.Name)) throw new UnsafePathException($"Arquivo não reconhecido como backup: {info.Name}");
        info.Delete();
        return true;
    }

    public static string RunDirectoryName(string runId) => "run_" + runId;

    // ---------------------------------------------------------------- remote
    public static bool IsSafeSegment(string segment) =>
        !string.IsNullOrEmpty(segment) && segment.Length <= 128 && segment != "." && segment != ".." &&
        SafeSegment().IsMatch(segment);

    public static bool IsSafeRelativeRemotePath(string path, bool allowEmpty)
    {
        if (string.IsNullOrEmpty(path)) return allowEmpty;
        if (path.Length > 1024 || path.StartsWith('/') || path.EndsWith('/') || path.Contains('\\') || path.Contains(':'))
            return false;
        return path.Split('/').All(IsSafeSegment);
    }

    /// <summary>
    /// Validates a remote path relative to the configured base before deleting it.
    /// Expected layout: [vps/]yyyy/MM/dd/&lt;family&gt;/&lt;connection&gt;/&lt;file&gt;.
    /// </summary>
    public static void EnsureDeletableRemoteBackup(string relativePath)
    {
        if (!IsSafeRelativeRemotePath(relativePath, allowEmpty: false))
            throw new UnsafePathException($"Caminho remoto inseguro: {relativePath}");
        var parts = relativePath.Split('/');
        if (parts.Length < 6)
            throw new UnsafePathException($"Caminho remoto fora da estrutura de backups: {relativePath}");
        var file = parts[^1];
        if (!IsBackupArtifactName(file))
            throw new UnsafePathException($"Nome de arquivo remoto não reconhecido: {file}");
        var (y, m, d) = (parts[^6], parts[^5], parts[^4]);
        if (!Year().IsMatch(y) || !TwoDigits().IsMatch(m) || !TwoDigits().IsMatch(d))
            throw new UnsafePathException($"Caminho remoto sem estrutura de data esperada: {relativePath}");
    }

    public static bool IsBackupArtifactName(string fileName) => BackupNaming.FilePattern().IsMatch(fileName);

    [GeneratedRegex("^run_[0-9a-f]{32}$")]
    private static partial Regex RunDirName();

    [GeneratedRegex(@"^[A-Za-z0-9 _.()\-]+$")]
    private static partial Regex SafeSegment();

    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex Year();

    [GeneratedRegex(@"^\d{2}$")]
    private static partial Regex TwoDigits();
}
