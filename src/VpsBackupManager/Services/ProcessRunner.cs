using System.Diagnostics;
using System.Text;
using VpsBackupManager.Security;

namespace VpsBackupManager.Services;

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

/// <summary>
/// Runs external tools with an explicit argv (ProcessStartInfo.ArgumentList) — never through a shell —
/// and with a minimal environment so secrets of this process (APP_SECRET_KEY…) never leak to children.
/// </summary>
public static class ProcessRunner
{
    private static readonly string[] InheritedVariables = ["PATH", "HOME", "LANG", "LC_ALL", "TZ", "TMPDIR", "SystemRoot", "TEMP", "TMP"];

    public static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var name in InheritedVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null) psi.Environment[name] = value;
        }
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;
        return psi;
    }

    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args,
        IReadOnlyDictionary<string, string>? env = null, TimeSpan? timeout = null, CancellationToken ct = default,
        int maxOutput = 4 * 1024 * 1024)
    {
        using var proc = new Process { StartInfo = CreateStartInfo(fileName, args, env) };
        try
        {
            proc.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new ProcessResult(127, "", $"Executável não encontrado: {fileName} ({ex.Message})", false);
        }
        proc.StandardInput.Close();
        var stdoutTask = ReadLimitedAsync(proc.StandardOutput, maxOutput);
        var stderrTask = ReadLimitedAsync(proc.StandardError, 256 * 1024);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is not null) cts.CancelAfter(timeout.Value);
        var timedOut = false;
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            await proc.WaitForExitAsync(CancellationToken.None);
            if (!timedOut) throw;
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcessResult(timedOut ? -1 : proc.ExitCode, stdout, Redactor.Truncate(stderr, 8000), timedOut);
    }

    public static async Task<string> ReadLimitedAsync(StreamReader reader, int max)
    {
        var sb = new StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            if (sb.Length < max) sb.Append(buffer, 0, Math.Min(read, max - sb.Length));
        }
        return sb.ToString();
    }

    public static bool CommandExists(string name)
    {
        if (Path.IsPathRooted(name)) return File.Exists(name);
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator).Any(dir => !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, name)));
    }
}
