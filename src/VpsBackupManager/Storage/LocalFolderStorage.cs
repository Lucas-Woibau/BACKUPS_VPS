using System.Security.Cryptography;
using VpsBackupManager.Backup;
using VpsBackupManager.Providers;

namespace VpsBackupManager.Storage;

/// <summary>
/// Storage backed by a local/mounted folder. Used by automated tests and usable for an extra copy on
/// a mounted disk. Applies the same path validations as the rclone implementation.
/// </summary>
public sealed class LocalFolderStorage(string root) : IStorageProvider
{
    public string Describe(string relativePath = "") =>
        string.IsNullOrEmpty(relativePath) ? root : root.TrimEnd('/', '\\') + "/" + relativePath;

    private string Resolve(string relativePath)
    {
        if (!PathGuard.IsSafeRelativeRemotePath(relativePath, allowEmpty: true))
            throw new StorageException($"Caminho inseguro: {relativePath}", false);
        return string.IsNullOrEmpty(relativePath) ? root : PathGuard.EnsureInside(root, Path.Combine(root, relativePath));
    }

    public Task<TestResult> TestConnectionAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        return Task.FromResult(new TestResult(true, "Pasta local acessível."));
    }

    public async Task UploadAsync(string localPath, string relativePath, CancellationToken ct)
    {
        var target = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var src = File.OpenRead(localPath);
        await using var dst = File.Create(target);
        await src.CopyToAsync(dst, ct);
    }

    public async Task<RemoteFileInfo?> StatAsync(string relativePath, CancellationToken ct)
    {
        var path = Resolve(relativePath);
        if (!File.Exists(path)) return null;
        await using var fs = File.OpenRead(path);
        var md5 = Convert.ToHexStringLower(await MD5.HashDataAsync(fs, ct));
        return new RemoteFileInfo(relativePath, fs.Length, md5);
    }

    public Task DeleteAsync(string relativePath, CancellationToken ct)
    {
        PathGuard.EnsureDeletableRemoteBackup(relativePath);
        var path = Resolve(relativePath);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RemoteFileInfo>> ListBackupsAsync(string relativeDirectory, CancellationToken ct)
    {
        var dir = Resolve(relativeDirectory);
        if (!Directory.Exists(dir)) return Task.FromResult<IReadOnlyList<RemoteFileInfo>>([]);
        IReadOnlyList<RemoteFileInfo> list = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => PathGuard.IsBackupArtifactName(Path.GetFileName(f)))
            .Select(f => new RemoteFileInfo(Path.GetRelativePath(root, f).Replace('\\', '/'), new FileInfo(f).Length, null))
            .ToList();
        return Task.FromResult(list);
    }
}
