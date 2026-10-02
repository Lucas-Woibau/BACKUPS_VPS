using System.IO.Compression;
using System.Security.Cryptography;
using ZstdSharp;

namespace VpsBackupManager.Backup;

/// <summary>
/// Streaming compression. Default = gzip (universal: `gunzip` exists on any Linux/Windows tool),
/// zstd is offered for faster/smaller archives (`zstd -d` needed on restore).
/// </summary>
public static class Compression
{
    public static Stream OpenWriter(string compression, int level, Stream destination) => compression switch
    {
        "gzip" => new GZipStream(destination, GzipLevel(level), leaveOpen: false),
        "zstd" => new CompressionStream(destination, Math.Clamp(level, 1, 19), leaveOpen: false),
        "none" => destination,
        _ => throw new ArgumentException($"Compressão desconhecida: {compression}"),
    };

    public static Stream OpenReader(string compression, Stream source) => compression switch
    {
        "gzip" => new GZipStream(source, CompressionMode.Decompress, leaveOpen: false),
        "zstd" => new DecompressionStream(source, leaveOpen: false),
        "none" => source,
        _ => throw new ArgumentException($"Compressão desconhecida: {compression}"),
    };

    private static CompressionLevel GzipLevel(int level) => level switch
    {
        <= 3 => CompressionLevel.Fastest,
        <= 6 => CompressionLevel.Optimal,
        _ => CompressionLevel.SmallestSize,
    };

    /// <summary>
    /// Decompresses the whole file and returns (size, sha256) of the content. Detects truncated or
    /// corrupted archives before anything is uploaded.
    /// </summary>
    public static async Task<(long Size, string Sha256)> VerifyAsync(string path, string compression, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        await using var reader = OpenReader(compression, file);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long total = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            total += read;
        }
        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
}

public static class FileHashes
{
    public static async Task<(string Sha256, string Md5, long Size)> ComputeAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5); // only for comparing with Drive's md5Checksum
        var buffer = new byte[1 << 20];
        long total = 0;
        int read;
        while ((read = await file.ReadAsync(buffer, ct)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            md5.AppendData(buffer, 0, read);
            total += read;
        }
        return (Convert.ToHexStringLower(sha.GetHashAndReset()), Convert.ToHexStringLower(md5.GetHashAndReset()), total);
    }
}
