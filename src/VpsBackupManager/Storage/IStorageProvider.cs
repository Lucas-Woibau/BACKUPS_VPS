using VpsBackupManager.Providers;
using VpsBackupManager.Services;

namespace VpsBackupManager.Storage;

public sealed record RemoteFileInfo(string RelativePath, long Size, string? Md5);

public sealed class StorageException(string message, bool transient, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>True when retrying later may succeed (network, rate limit). False for config/auth errors.</summary>
    public bool Transient { get; } = transient;
}

/// <summary>
/// Remote destination. Paths are always relative to the configured base folder and are validated
/// before use. Implementations: rclone (Google Drive and any rclone backend), local folder (tests).
/// Future: native S3/B2/OneDrive/Dropbox/MinIO — or simply other rclone remotes.
/// </summary>
public interface IStorageProvider
{
    string Describe(string relativePath = "");
    Task<TestResult> TestConnectionAsync(CancellationToken ct);
    Task UploadAsync(string localPath, string relativePath, CancellationToken ct);
    Task<RemoteFileInfo?> StatAsync(string relativePath, CancellationToken ct);
    async Task<bool> ExistsAsync(string relativePath, CancellationToken ct) => await StatAsync(relativePath, ct) is not null;
    Task DeleteAsync(string relativePath, CancellationToken ct);
    Task<IReadOnlyList<RemoteFileInfo>> ListBackupsAsync(string relativeDirectory, CancellationToken ct);
}

public interface IStorageFactory
{
    IStorageProvider Create(AppSettings settings);
}
