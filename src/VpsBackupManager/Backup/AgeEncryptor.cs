using VpsBackupManager.Services;

namespace VpsBackupManager.Backup;

public interface IBackupEncryptor
{
    /// <summary>Encrypts <paramref name="input"/> into <paramref name="output"/>.</summary>
    Task EncryptAsync(string input, string output, IReadOnlyList<string> recipients, CancellationToken ct);
}

/// <summary>
/// Encryption with age (https://age-encryption.org, X25519 + ChaCha20-Poly1305) using PUBLIC keys
/// only: the VPS can encrypt but cannot decrypt. The private key stays offline with the administrator.
/// Restore: age -d -i chave.txt arquivo.sql.gz.age &gt; arquivo.sql.gz
/// </summary>
public sealed class AgeEncryptor : IBackupEncryptor
{
    public async Task EncryptAsync(string input, string output, IReadOnlyList<string> recipients, CancellationToken ct)
    {
        if (recipients.Count == 0) throw new InvalidOperationException("Nenhuma chave pública age configurada.");
        var args = new List<string>();
        foreach (var r in recipients) { args.Add("-r"); args.Add(r); }
        args.AddRange(["-o", output, input]);
        var size = new FileInfo(input).Length;
        var result = await ProcessRunner.RunAsync("age", args, timeout: TimeSpan.FromMinutes(10 + size / (100L * 1024 * 1024)), ct: ct);
        if (result.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
            throw new InvalidOperationException($"Falha na criptografia age (código {result.ExitCode}): {result.Stderr}");
    }
}
