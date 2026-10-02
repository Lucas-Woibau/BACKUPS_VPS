using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace VpsBackupManager.Security;

/// <summary>
/// Removes secrets from any text that may be logged or shown in the panel
/// (dump tool stderr, rclone output, exception messages).
/// </summary>
public static partial class Redactor
{
    private static readonly ConcurrentDictionary<string, byte> Literals = new();

    /// <summary>Register a literal secret (e.g. a decrypted DB password) to be masked everywhere.</summary>
    public static void Register(string? secret)
    {
        if (!string.IsNullOrEmpty(secret) && secret.Length >= 4) Literals.TryAdd(secret, 0);
    }

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        foreach (var secret in Literals.Keys)
            if (text.Contains(secret, StringComparison.Ordinal))
                text = text.Replace(secret, "***", StringComparison.Ordinal);
        text = KeyValue().Replace(text, m => $"{m.Groups[1].Value}{m.Groups[2].Value}***");
        text = UriCredentials().Replace(text, m => $"{m.Groups[1].Value}://{m.Groups[2].Value}:***@");
        text = JsonToken().Replace(text, m => $"{m.Groups[1].Value}\"***\"");
        return text;
    }

    public static string Truncate(string? text, int max = 4000)
    {
        var t = Redact(text);
        return t.Length <= max ? t : t[..max] + "…";
    }

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|secret|token|api[_-]?key|authorization)(\s*[=:]\s*)(""[^""]*""|'[^']*'|[^\s,;&]+)")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"(?i)\b(postgres(?:ql)?|mysql|mariadb|https?)://([^:/\s@]+):([^@\s]+)@")]
    private static partial Regex UriCredentials();

    [GeneratedRegex(@"(?i)(""(?:access_token|refresh_token|client_secret|password|secret)""\s*:\s*)""[^""]*""")]
    private static partial Regex JsonToken();
}
