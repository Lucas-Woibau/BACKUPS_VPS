using System.Security.Cryptography;
using System.Text;

namespace VpsBackupManager.Security;

/// <summary>
/// Encrypts credentials at rest (database passwords, webhook secret) with AES-256-GCM.
/// Key = HKDF-SHA256(APP_SECRET_KEY). Losing APP_SECRET_KEY means stored passwords must be
/// re-entered — the backup files themselves do NOT depend on this key.
/// </summary>
public sealed class SecretBox
{
    private const string Prefix = "v1:";
    private readonly byte[] _key;

    public SecretBox(string masterSecret)
    {
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(masterSecret), 32,
            Encoding.ASCII.GetBytes("vps-backup-manager/v1"), Encoding.ASCII.GetBytes("credential-store"));
    }

    public string Encrypt(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        var blob = new byte[12 + cipher.Length + 16];
        nonce.CopyTo(blob, 0);
        cipher.CopyTo(blob, 12);
        tag.CopyTo(blob, 12 + cipher.Length);
        return Prefix + Convert.ToBase64String(blob);
    }

    public string Decrypt(string token)
    {
        try
        {
            if (!token.StartsWith(Prefix, StringComparison.Ordinal)) throw new FormatException();
            var blob = Convert.FromBase64String(token[Prefix.Length..]);
            if (blob.Length < 28) throw new FormatException();
            var nonce = blob.AsSpan(0, 12);
            var cipher = blob.AsSpan(12, blob.Length - 28);
            var tag = blob.AsSpan(blob.Length - 16);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new InvalidOperationException(
                "Não foi possível descriptografar a credencial armazenada. A APP_SECRET_KEY foi alterada? " +
                "Recadastre a senha da conexão.", ex);
        }
    }
}
