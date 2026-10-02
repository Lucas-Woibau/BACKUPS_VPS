using System.Security.Cryptography;
using System.Text;
using VpsBackupManager.Config;
using VpsBackupManager.Data;

namespace VpsBackupManager.Security;

/// <summary>
/// First-access flow: while no administrator exists, a random one-time token is written to
/// DATA_DIR/setup_token (mode 600). Creating the first admin requires it; the file is then deleted.
/// The token is never written to logs.
/// </summary>
public sealed class SetupTokenService(Db db, AppOptions options, ILogger<SetupTokenService> logger)
{
    public async Task<bool> HasAdminAsync() =>
        await db.QueryOneAsync<long?>("SELECT id FROM users LIMIT 1") is not null;

    public async Task EnsureAsync()
    {
        var path = options.SetupTokenFile;
        if (await HasAdminAsync())
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        if (!File.Exists(path))
        {
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var fso = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) fso.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var fs = new FileStream(path, fso);
            await fs.WriteAsync(Encoding.UTF8.GetBytes(token + "\n"));
        }
        logger.LogWarning(
            "Nenhum administrador cadastrado. Leia o token de primeiro acesso com: ./scripts/show-setup-token.sh " +
            "(ou: docker compose exec app cat {Path})", path);
    }

    public bool Check(string? provided)
    {
        var path = options.SetupTokenFile;
        if (string.IsNullOrWhiteSpace(provided) || !File.Exists(path)) return false;
        var expected = File.ReadAllText(path).Trim();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(provided.Trim()));
    }

    public void Consume()
    {
        if (File.Exists(options.SetupTokenFile)) File.Delete(options.SetupTokenFile);
    }
}
