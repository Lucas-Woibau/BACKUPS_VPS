using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VpsBackupManager.Config;
using VpsBackupManager.Providers;
using VpsBackupManager.Services;

namespace VpsBackupManager.Storage;

public sealed record DriveConnectStart(string AuthUrl, string Instructions);
public sealed record DriveConnectResult(bool Ok, string Message, string? Email);

/// <summary>
/// "Conectar Google Drive" from the panel, using only the administrator's Gmail.
///
/// Google never allows access with just an e-mail address: the account owner must approve once on Google's
/// consent screen. This service makes that a two-click flow without creating a Google Cloud project:
///   1. runs <c>rclone authorize drive</c> inside the container (rclone's own verified OAuth app);
///   2. returns Google's consent URL with <c>login_hint=&lt;gmail&gt;</c> so the right account is pre-selected;
///   3. after approval the browser is redirected to http://127.0.0.1:53682/?code=… (rclone's loopback address,
///      which only exists inside the container) — the page fails to load on the PC, so the administrator pastes
///      that address back in the panel and the app delivers it to rclone locally;
///   4. the resulting token is written to rclone.conf (0600) and the account e-mail is verified.
/// rclone refreshes the token by itself afterwards; the PC is no longer needed.
/// </summary>
public sealed partial class DriveAuthService(
    AppOptions options,
    SettingsService settingsService,
    IStorageFactory storageFactory,
    ILogger<DriveAuthService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private TaskCompletionSource<string>? _token;
    private string? _expectedEmail;
    private CancellationTokenSource? _expiry;

    public async Task<DriveConnectStart> StartAsync(string email, CancellationToken ct)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        if (!EmailPattern().IsMatch(email)) throw new ValidationException(["Informe um e-mail válido (ex.: voce@gmail.com)."]);
        if (!ProcessRunner.CommandExists("rclone")) throw new StorageException("rclone não está instalado no container.", false);

        await _gate.WaitAsync(ct);
        try
        {
            Stop();
            _expectedEmail = email;
            _token = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var link = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            // {"scope":"drive.file"}: rclone only sees files it created (least privilege).
            var configBlob = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"scope\":\"drive.file\"}"));
            var psi = ProcessRunner.CreateStartInfo("rclone", ["authorize", "drive", configBlob, "--auth-no-open-browser"],
                new Dictionary<string, string> { ["RCLONE_CONFIG"] = options.RcloneConfig });
            _process = Process.Start(psi) ?? throw new StorageException("Falha ao iniciar rclone authorize.", false);
            _process.StandardInput.Close();
            _ = PumpAsync(_process.StandardOutput, link, _token);
            _ = PumpAsync(_process.StandardError, link, _token);

            _expiry = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            _expiry.Token.Register(Stop);

            var localLink = await link.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            var googleUrl = await ResolveGoogleUrlAsync(localLink, ct);
            googleUrl += (googleUrl.Contains('?') ? "&" : "?") + "login_hint=" + Uri.EscapeDataString(email);
            return new DriveConnectStart(googleUrl,
                "Entre com a sua conta Google e clique em Permitir. No final o navegador mostrará uma página que não abre " +
                "(endereço começando com http://127.0.0.1:53682). Copie esse endereço inteiro da barra do navegador e cole no painel.");
        }
        catch (TimeoutException)
        {
            Stop();
            throw new StorageException("rclone não respondeu ao iniciar a autorização. Tente novamente.", true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DriveConnectResult> CompleteAsync(string pastedUrl, CancellationToken ct)
    {
        if (_process is null || _token is null)
            return new DriveConnectResult(false, "Nenhuma autorização em andamento. Clique em 'Conectar Google Drive' novamente.", null);
        if (!Uri.TryCreate((pastedUrl ?? "").Trim(), UriKind.Absolute, out var uri) ||
            uri.Port != 53682 || uri.Host is not ("127.0.0.1" or "localhost"))
            return new DriveConnectResult(false, "Cole o endereço completo que começa com http://127.0.0.1:53682/ (da barra do navegador).", null);

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        if (!string.IsNullOrEmpty(query["error"]))
            return new DriveConnectResult(false, $"O Google recusou a autorização: {query["error"]}.", null);
        if (string.IsNullOrEmpty(query["code"]) || string.IsNullOrEmpty(query["state"]))
            return new DriveConnectResult(false, "Endereço incompleto: faltam os parâmetros 'code' e 'state'.", null);

        // Deliver to rclone's listener inside the container. Only the query string is reused; host/path are fixed.
        using (var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) })
        {
            try { await http.GetAsync("http://127.0.0.1:53682/" + uri.Query, ct); }
            catch (HttpRequestException ex)
            {
                Stop();
                return new DriveConnectResult(false, "A autorização expirou (rclone não está mais aguardando). Recomece: " + ex.Message, null);
            }
        }

        string tokenJson;
        try { tokenJson = await _token.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); }
        catch (TimeoutException) { Stop(); return new DriveConnectResult(false, "rclone não devolveu o token. Recomece a conexão.", null); }
        finally { Stop(); }

        var settings = await settingsService.GetAsync();
        RcloneConfigFile.WriteDriveRemote(options.RcloneConfig, settings.RcloneRemote, tokenJson);
        logger.LogInformation("Remote {Remote} do Google Drive configurado pelo painel", settings.RcloneRemote);

        var email = await ReadAccountEmailAsync(settings.RcloneRemote, ct);
        await settingsService.SaveAsync(settings with { DriveAccountEmail = email ?? _expectedEmail });

        TestResult test;
        try { test = await storageFactory.Create(settings).TestConnectionAsync(ct); }
        catch (StorageException ex) { test = new TestResult(false, ex.Message); }
        await settingsService.SetDriveStatusAsync(test.Ok, test.Message);

        var mismatch = email is not null && _expectedEmail is not null && !string.Equals(email, _expectedEmail, StringComparison.OrdinalIgnoreCase)
            ? $" Atenção: a conta autorizada foi {email}, diferente de {_expectedEmail}." : "";
        return new DriveConnectResult(test.Ok,
            (test.Ok ? $"Google Drive conectado ({email ?? _expectedEmail}). Pasta '{settings.RemoteBasePath}' pronta." : "Token salvo, mas o teste falhou: " + test.Message) + mismatch,
            email);
    }

    public async Task<string?> ReadAccountEmailAsync(string remote, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync("rclone", ["config", "userinfo", remote + ":", "--json"],
            new Dictionary<string, string> { ["RCLONE_CONFIG"] = options.RcloneConfig }, TimeSpan.FromSeconds(30), ct);
        if (r.ExitCode != 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(r.Stdout);
            return doc.RootElement.TryGetProperty("EmailAddress", out var e) ? e.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static async Task<string> ResolveGoogleUrlAsync(string localLink, CancellationToken ct)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        using var resp = await http.GetAsync(localLink, ct);
        var location = resp.Headers.Location?.ToString();
        if (location is null || !location.StartsWith("https://accounts.google.com/", StringComparison.Ordinal))
            throw new StorageException("Não foi possível obter o endereço de autorização do Google a partir do rclone.", true);
        return location;
    }

    private static async Task PumpAsync(StreamReader reader, TaskCompletionSource<string> link, TaskCompletionSource<string> token)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                var m = LocalAuthLink().Match(line);
                if (m.Success) link.TrySetResult(m.Value);
                var t = line.Trim();
                if (t.StartsWith('{') && t.Contains("\"access_token\"", StringComparison.Ordinal)) token.TrySetResult(t);
            }
        }
        catch { /* process ended */ }
    }

    private void Stop()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { /* gone */ }
        _process?.Dispose();
        _process = null;
        _expiry?.Dispose();
        _expiry = null;
    }

    [GeneratedRegex(@"http://127\.0\.0\.1:53682/auth\?state=[A-Za-z0-9_\-]+")]
    private static partial Regex LocalAuthLink();

    [GeneratedRegex(@"^[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,}$")]
    private static partial Regex EmailPattern();
}

/// <summary>Minimal editor for rclone.conf: replaces one [section] and keeps the rest. Writes atomically with 0600.</summary>
public static class RcloneConfigFile
{
    public static void WriteDriveRemote(string path, string remote, string tokenJson)
    {
        if (!SettingsService.RemoteName().IsMatch(remote)) throw new StorageException("Nome de remote inválido.", false);
        if (tokenJson.Contains('\n') || !tokenJson.TrimStart().StartsWith('{')) throw new StorageException("Token inválido.", false);
        JsonDocument.Parse(tokenJson).Dispose(); // must be valid JSON

        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var output = new List<string>();
        var skipping = false;
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith('[') && t.EndsWith(']')) skipping = t[1..^1] == remote;
            if (!skipping) output.Add(line);
        }
        while (output.Count > 0 && string.IsNullOrWhiteSpace(output[^1])) output.RemoveAt(output.Count - 1);
        if (output.Count > 0) output.Add("");
        output.AddRange([$"[{remote}]", "type = drive", "scope = drive.file", $"token = {tokenJson}", ""]);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        var fso = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) fso.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(tmp, fso))
        using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
            w.Write(string.Join('\n', output));
        File.Move(tmp, path, overwrite: true);
    }

    public static bool HasRemote(string path, string remote) =>
        File.Exists(path) && File.ReadLines(path).Any(l => l.Trim() == $"[{remote}]");
}
