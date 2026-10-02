using System.Diagnostics;
using System.Security.Cryptography;
using VpsBackupManager.Security;
using VpsBackupManager.Services;

namespace VpsBackupManager.Providers;

/// <summary>
/// Runs a dump tool and streams stdout straight into the compressor — the raw dump never touches the
/// disk uncompressed. Captures size, SHA-256, head/tail bytes (for format validation) and stderr.
/// </summary>
public sealed class ProcessDumpExecutor : IDumpExecutor
{
    private const int HeadSize = 64;
    private const int TailSize = 8192;

    public async Task<DumpOutcome> ExecuteAsync(DumpCommand command, Stream sink, CancellationToken ct)
    {
        using var proc = new Process { StartInfo = ProcessRunner.CreateStartInfo(command.FileName, command.Args, command.Env) };
        try
        {
            proc.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ProviderException($"Ferramenta de dump não encontrada: {command.FileName} ({ex.Message})");
        }
        proc.StandardInput.Close();

        var stderrTask = ProcessRunner.ReadLimitedAsync(proc.StandardError, 64 * 1024);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (command.Timeout > TimeSpan.Zero) cts.CancelAfter(command.Timeout);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var head = new MemoryStream();
        var tail = new byte[TailSize];
        var tailLen = 0;
        long total = 0;
        var timedOut = false;
        var buffer = new byte[1 << 20];
        try
        {
            var stdout = proc.StandardOutput.BaseStream;
            int read;
            while ((read = await stdout.ReadAsync(buffer, cts.Token)) > 0)
            {
                await sink.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                hash.AppendData(buffer, 0, read);
                if (head.Length < HeadSize) head.Write(buffer, 0, (int)Math.Min(read, HeadSize - head.Length));
                AppendTail(tail, ref tailLen, buffer.AsSpan(0, read));
                total += read;
            }
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
            await proc.WaitForExitAsync(CancellationToken.None);
            if (!timedOut) throw;
        }
        catch
        {
            // e.g. disk full while writing the compressed file: stop the dump tool before propagating.
            try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw;
        }

        var stderr = Redactor.Truncate(await stderrTask, 8000);
        return new DumpOutcome(timedOut ? -1 : proc.ExitCode, total, Convert.ToHexStringLower(hash.GetHashAndReset()),
            head.ToArray(), tail[..tailLen], stderr, timedOut);
    }

    private static void AppendTail(byte[] tail, ref int tailLen, ReadOnlySpan<byte> data)
    {
        if (data.Length >= tail.Length)
        {
            data[^tail.Length..].CopyTo(tail);
            tailLen = tail.Length;
            return;
        }
        var keep = Math.Min(tailLen, tail.Length - data.Length);
        if (keep > 0) Buffer.BlockCopy(tail, tailLen - keep, tail, 0, keep);
        data.CopyTo(tail.AsSpan(keep));
        tailLen = keep + data.Length;
    }
}
