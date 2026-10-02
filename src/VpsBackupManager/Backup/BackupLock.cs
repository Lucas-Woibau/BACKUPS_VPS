namespace VpsBackupManager.Backup;

/// <summary>
/// Global "one backup at a time" lock: an in-process semaphore plus an exclusive OS file lock
/// (flock on Linux) so that even a second process/container sharing the data volume cannot run
/// concurrently.
/// </summary>
public sealed class BackupLock(string lockFile)
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public bool IsHeld => _semaphore.CurrentCount == 0;

    public async Task<IDisposable?> TryAcquireAsync(TimeSpan wait, CancellationToken ct)
    {
        if (!await _semaphore.WaitAsync(wait, ct)) return null;
        try
        {
            var deadline = DateTime.UtcNow + wait;
            while (true)
            {
                try
                {
                    var fs = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return new Handle(this, fs);
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
                catch (IOException)
                {
                    _semaphore.Release();
                    return null;
                }
            }
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
    }

    private sealed class Handle(BackupLock owner, FileStream fs) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            fs.Dispose();
            owner._semaphore.Release();
        }
    }
}
