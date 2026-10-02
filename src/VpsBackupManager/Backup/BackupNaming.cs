using System.Text;
using System.Text.RegularExpressions;
using VpsBackupManager.Services;

namespace VpsBackupManager.Backup;

/// <summary>
/// File and folder naming. Example:
/// Backups/VPS-Producao/2026/10/01/mysql/mysql-principal/ecommerce_2026-10-01_03-00-00.sql.gz
/// Timestamps use the configured timezone (local wall clock of the administrator).
/// </summary>
public static partial class BackupNaming
{
    public static string Slug(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Normalize(NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_');
        }
        var slug = MultiUnderscore().Replace(sb.ToString(), "_").Trim('_', '.', '-');
        if (slug.Length == 0) slug = "db";
        return slug.Length > 80 ? slug[..80] : slug;
    }

    public static string CompressionExtension(string compression) => compression switch
    {
        "gzip" => ".gz",
        "zstd" => ".zst",
        _ => "",
    };

    public static string FileName(string database, DateTime localTime, string dumpExtension, string compression,
        bool encrypted, string? suffix = null)
    {
        var name = $"{Slug(database)}_{localTime:yyyy-MM-dd_HH-mm-ss}";
        if (!string.IsNullOrEmpty(suffix)) name += "_" + suffix;
        name += "." + dumpExtension + CompressionExtension(compression);
        if (encrypted) name += ".age";
        return name;
    }

    public static string SidecarName(string fileName) => fileName + ".sha256";

    /// <summary>
    /// Path relative to "remote:base": [vps | pasta-da-conexão]/yyyy/MM/dd/family/connection.
    /// A per-connection Drive folder (e.g. "VPS-Producao/Banco1") replaces the global VPS subfolder.
    /// </summary>
    public static string RemoteDirectory(AppSettings settings, string family, string connectionName, DateTime localTime,
        string? connectionFolder = null)
    {
        var parts = new List<string>();
        var custom = connectionFolder?.Trim().Trim('/');
        if (!string.IsNullOrEmpty(custom))
        {
            if (!PathGuard.IsSafeRelativeRemotePath(custom, allowEmpty: false))
                throw new PathGuard.UnsafePathException($"Pasta no Drive inválida: {custom}");
            parts.Add(custom);
        }
        else if (settings.UseVpsSubfolder) parts.Add(settings.VpsFolderName.Trim().Trim('/'));
        parts.Add(localTime.ToString("yyyy"));
        parts.Add(localTime.ToString("MM"));
        parts.Add(localTime.ToString("dd"));
        parts.Add(family);
        parts.Add(Slug(connectionName));
        return string.Join('/', parts);
    }

    public static DateTime ToLocal(DateTimeOffset utc, TimeZoneInfo tz) => TimeZoneInfo.ConvertTime(utc, tz).DateTime;

    /// <summary>Matches every artifact this app creates (and nothing else). Used to guard deletions.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}(_[a-z0-9]{1,12})?\.(sql|dump|bak)(\.gz|\.zst)?(\.age)?(\.sha256)?$")]
    public static partial Regex FilePattern();

    [GeneratedRegex("_{2,}")]
    private static partial Regex MultiUnderscore();
}
