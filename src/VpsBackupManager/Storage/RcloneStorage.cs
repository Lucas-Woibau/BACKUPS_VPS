using System.Text.Json;
using VpsBackupManager.Backup;
using VpsBackupManager.Config;
using VpsBackupManager.Providers;
using VpsBackupManager.Services;

namespace VpsBackupManager.Storage;

/// <summary>
/// Google Drive (or any rclone backend) through the rclone CLI. rclone handles OAuth token refresh
/// on its own and writes the refreshed token back to rclone.conf, so the config volume must be writable.
/// Deletions only use `rclone deletefile` on a single validated file — never purge/rmdirs/sync.
/// </summary>
public sealed class RcloneStorage : IStorageProvider
{
    private readonly string _remote;
    private readonly string _base;
    private readonly string _configPath;

    public RcloneStorage(string remote, string basePath, string configPath)
    {
        if (!SettingsService.RemoteName().IsMatch(remote)) throw new StorageException("Remote rclone inválido.", false);
        basePath = basePath.Trim().Trim('/');
        if (!PathGuard.IsSafeRelativeRemotePath(basePath, allowEmpty: false))
            throw new StorageException("Pasta base remota inválida.", false);
        _remote = remote;
        _base = basePath;
        _configPath = configPath;
    }

    public string Describe(string relativePath = "") =>
        string.IsNullOrEmpty(relativePath) ? $"{_remote}:{_base}" : $"{_remote}:{_base}/{relativePath}";

    private string Target(string relativePath)
    {
        if (!PathGuard.IsSafeRelativeRemotePath(relativePath, allowEmpty: true))
            throw new StorageException($"Caminho remoto inseguro: {relativePath}", false);
        return Describe(relativePath);
    }

    private Task<ProcessResult> Rclone(IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var all = new List<string>(args)
        {
            "--log-level=NOTICE", "--contimeout=60s", "--timeout=300s", "--retries=3", "--low-level-retries=10",
        };
        return ProcessRunner.RunAsync("rclone", all, new Dictionary<string, string> { ["RCLONE_CONFIG"] = _configPath },
            timeout, ct);
    }

    public async Task<TestResult> TestConnectionAsync(CancellationToken ct)
    {
        if (!File.Exists(_configPath))
            return new TestResult(false, $"rclone.conf não encontrado em {_configPath}. Configure com scripts/rclone-config.sh.");
        var mk = await Rclone(["mkdir", Target("")], TimeSpan.FromMinutes(2), ct);
        if (mk.ExitCode != 0) return new TestResult(false, Classify(mk).Message);

        // Round-trip test: upload a tiny file, verify it, delete it.
        var tmp = Path.Combine(Path.GetTempPath(), $"vbm-test-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tmp, $"VPS Backup Manager - teste {DateTimeOffset.UtcNow:O}\n", ct);
        var rel = $".vbm-connection-test/test-{Guid.NewGuid():N}.txt";
        try
        {
            var up = await Rclone(["copyto", tmp, Target(rel)], TimeSpan.FromMinutes(5), ct);
            if (up.ExitCode != 0) return new TestResult(false, "Falha no upload de teste: " + Classify(up).Message);
            var stat = await StatAsync(rel, ct);
            if (stat is null) return new TestResult(false, "Upload de teste não foi encontrado no destino.");
            await Rclone(["deletefile", Target(rel)], TimeSpan.FromMinutes(2), ct);
            await Rclone(["rmdir", Target(".vbm-connection-test")], TimeSpan.FromMinutes(2), ct);
            return new TestResult(true, $"Conectado. Upload de teste em {Describe()} realizado e removido.");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    public async Task UploadAsync(string localPath, string relativePath, CancellationToken ct)
    {
        var size = new FileInfo(localPath).Length;
        // generous timeout: 10 min + 1 min per 50 MB
        var timeout = TimeSpan.FromMinutes(10 + size / (50L * 1024 * 1024));
        var r = await Rclone(["copyto", localPath, Target(relativePath), "--drive-chunk-size=64M", "--no-traverse"], timeout, ct);
        if (r.ExitCode != 0) throw Classify(r);
    }

    public async Task<RemoteFileInfo?> StatAsync(string relativePath, CancellationToken ct)
    {
        var r = await Rclone(["lsjson", "--files-only", "--hash", "--no-mimetype", Target(relativePath)], TimeSpan.FromMinutes(2), ct);
        if (r.ExitCode is 3 or 4 || r.Stderr.Contains("not found", StringComparison.OrdinalIgnoreCase)) return null;
        if (r.ExitCode != 0) throw Classify(r);
        var items = ParseList(r.Stdout, Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? "");
        var name = relativePath.Split('/')[^1];
        return items.FirstOrDefault(i => i.RelativePath.Split('/')[^1] == name);
    }

    public async Task DeleteAsync(string relativePath, CancellationToken ct)
    {
        PathGuard.EnsureDeletableRemoteBackup(relativePath);
        var r = await Rclone(["deletefile", Target(relativePath)], TimeSpan.FromMinutes(2), ct);
        if (r.ExitCode != 0 && !r.Stderr.Contains("not found", StringComparison.OrdinalIgnoreCase)) throw Classify(r);
    }

    public async Task<IReadOnlyList<RemoteFileInfo>> ListBackupsAsync(string relativeDirectory, CancellationToken ct)
    {
        var r = await Rclone(["lsjson", "-R", "--files-only", "--no-mimetype", Target(relativeDirectory)], TimeSpan.FromMinutes(10), ct);
        if (r.ExitCode is 3) return [];
        if (r.ExitCode != 0) throw Classify(r);
        return ParseList(r.Stdout, relativeDirectory).Where(f => PathGuard.IsBackupArtifactName(f.RelativePath.Split('/')[^1])).ToList();
    }

    internal static List<RemoteFileInfo> ParseList(string json, string prefix)
    {
        var list = new List<RemoteFileInfo>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        using var doc = JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var path = e.GetProperty("Path").GetString() ?? "";
            var size = e.TryGetProperty("Size", out var s) ? s.GetInt64() : -1;
            string? md5 = null;
            if (e.TryGetProperty("Hashes", out var h) && h.ValueKind == JsonValueKind.Object && h.TryGetProperty("md5", out var m))
                md5 = m.GetString()?.ToLowerInvariant();
            list.Add(new RemoteFileInfo(string.IsNullOrEmpty(prefix) ? path : $"{prefix}/{path}", size, md5));
        }
        return list;
    }

    internal static StorageException Classify(ProcessResult r)
    {
        var err = r.Stderr;
        if (r.TimedOut) return new StorageException("Tempo esgotado na operação do rclone.", true);
        if (r.ExitCode == 127) return new StorageException("rclone não está instalado no container.", false);
        if (err.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase) || err.Contains("token expired", StringComparison.OrdinalIgnoreCase))
            return new StorageException("Token do Google Drive expirado ou revogado. Reautorize o remote com scripts/rclone-config.sh.", false);
        if (err.Contains("didn't find section in config file", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("couldn't find section", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("not found in config", StringComparison.OrdinalIgnoreCase))
            return new StorageException("Remote não existe no rclone.conf. Confira o nome do remote nas configurações.", false);
        if (err.Contains("storageQuotaExceeded", StringComparison.OrdinalIgnoreCase) || err.Contains("quota", StringComparison.OrdinalIgnoreCase))
            return new StorageException("Cota do Google Drive excedida.", false);
        if (err.Contains("insufficientPermissions", StringComparison.OrdinalIgnoreCase) || err.Contains("403", StringComparison.Ordinal))
            return new StorageException("Permissão negada no Google Drive (escopo/pasta). Detalhe: " + Short(err), true);
        if (err.Contains("no such host", StringComparison.OrdinalIgnoreCase) || err.Contains("dial tcp", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("connection refused", StringComparison.OrdinalIgnoreCase) || err.Contains("i/o timeout", StringComparison.OrdinalIgnoreCase))
            return new StorageException("Sem conectividade com o Google Drive (Internet/DNS). Detalhe: " + Short(err), true);
        return new StorageException($"rclone falhou (código {r.ExitCode}): {Short(err)}", true);
    }

    private static string Short(string s) => Security.Redactor.Truncate(s.Trim(), 500);
}

public sealed class StorageFactory(AppOptions options) : IStorageFactory
{
    public IStorageProvider Create(AppSettings settings)
    {
        var basePath = settings.RemoteBasePath.Trim().Trim('/');
        return new RcloneStorage(settings.RcloneRemote, basePath, options.RcloneConfig);
    }
}
