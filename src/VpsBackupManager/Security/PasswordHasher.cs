using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace VpsBackupManager.Security;

/// <summary>Argon2id password hashing, encoded in the standard PHC string format.</summary>
public static class PasswordHasher
{
    public const int MinLength = 12;
    private const int MemoryKb = 65536; // 64 MiB
    private const int Iterations = 3;
    private const int Parallelism = 2;
    private const int HashLength = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Compute(password, salt, MemoryKb, Iterations, Parallelism, HashLength);
        return $"$argon2id$v=19$m={MemoryKb},t={Iterations},p={Parallelism}${B64(salt)}${B64(hash)}";
    }

    public static bool Verify(string encoded, string password)
    {
        try
        {
            // $argon2id$v=19$m=65536,t=3,p=2$salt$hash
            var parts = encoded.Split('$');
            if (parts.Length != 6 || parts[1] != "argon2id") return false;
            var p = parts[3].Split(',').Select(kv => kv.Split('=')).ToDictionary(kv => kv[0], kv => int.Parse(kv[1]));
            var salt = FromB64(parts[4]);
            var expected = FromB64(parts[5]);
            var actual = Compute(password, salt, p["m"], p["t"], p["p"], expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    public static List<string> ValidateStrength(string password, string username)
    {
        var problems = new List<string>();
        if (password.Length < MinLength) problems.Add($"A senha deve ter pelo menos {MinLength} caracteres.");
        if (password.Length > 256) problems.Add("A senha é longa demais (máx. 256).");
        if (!string.IsNullOrEmpty(username) && password.Contains(username, StringComparison.OrdinalIgnoreCase))
            problems.Add("A senha não pode conter o nome de usuário.");
        var classes = new[]
        {
            password.Any(char.IsLower), password.Any(char.IsUpper), password.Any(char.IsDigit),
            password.Any(c => !char.IsLetterOrDigit(c)),
        }.Count(x => x);
        if (classes < 3) problems.Add("Use ao menos 3 tipos de caracteres: minúsculas, maiúsculas, números, símbolos.");
        return problems;
    }

    private static byte[] Compute(string password, byte[] salt, int memKb, int iterations, int parallelism, int len)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt, MemorySize = memKb, Iterations = iterations, DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(len);
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=');

    private static byte[] FromB64(string s) => Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
}
