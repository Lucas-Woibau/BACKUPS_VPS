using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VpsBackupManager.Services;

namespace VpsBackupManager.Notifications;

/// <summary>Sha256 lets the receiver keep an out-of-band copy of each backup's hash (the .sha256 sidecar lives next to
/// the file, under the same credential, so it alone cannot detect tampering).</summary>
public sealed record BackupItemSummary(string Connection, string Database, string Status, long? Size, string? RemotePath, string? Error,
    string? Sha256 = null);

public sealed record NotificationEvent(
    string Event,            // "backup.success" | "backup.failure" | "settings.changed" | "test"
    string VpsName,
    string RunId,
    string Status,
    int Total,
    int Succeeded,
    int Warnings,
    int Failed,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<BackupItemSummary> Items,
    string? Message = null);

/// <summary>Notification channel. Future channels: e-mail, Telegram, Discord…</summary>
public interface INotifier
{
    string Name { get; }
    Task NotifyAsync(NotificationEvent evt, CancellationToken ct);
}

/// <summary>
/// Generic webhook: POST JSON. When a secret is configured the body is signed:
/// header X-VBM-Signature: sha256=HEX(HMAC-SHA256(secret, body)).
/// </summary>
public sealed class WebhookNotifier(IHttpClientFactory httpFactory, SettingsService settingsService, ILogger<WebhookNotifier> logger) : INotifier
{
    public string Name => "webhook";

    public async Task NotifyAsync(NotificationEvent evt, CancellationToken ct)
    {
        var s = await settingsService.GetAsync();
        if (!s.WebhookEnabled || string.IsNullOrEmpty(s.WebhookUrl)) return;
        var isFailure = evt.Event == "backup.failure";
        if (evt.Event != "test" && ((isFailure && !s.WebhookOnFailure) || (!isFailure && !s.WebhookOnSuccess))) return;
        await SendAsync(s.WebhookUrl, evt, await settingsService.GetWebhookSecretAsync(), ct);
    }

    public async Task SendAsync(string url, NotificationEvent evt, string? secret, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(evt, SettingsService.Json);
        var client = httpFactory.CreateClient("webhook");
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
                req.Content.Headers.ContentType = new("application/json");
                req.Headers.Add("X-VBM-Event", evt.Event);
                if (!string.IsNullOrEmpty(secret))
                    req.Headers.Add("X-VBM-Signature", "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)));
                using var resp = await client.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode) return;
                last = new HttpRequestException($"HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex;
            }
            await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
        }
        logger.LogWarning("Webhook falhou após 3 tentativas: {Error}", last?.Message);
        throw new InvalidOperationException("Webhook falhou: " + last?.Message);
    }
}

public sealed class NotificationDispatcher(IEnumerable<INotifier> notifiers, ILogger<NotificationDispatcher> logger)
{
    public async Task DispatchAsync(NotificationEvent evt)
    {
        foreach (var n in notifiers)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                await n.NotifyAsync(evt, cts.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Notificação {Channel} falhou: {Error}", n.Name, ex.Message);
            }
        }
    }
}

/// <summary>
/// Connection policy for webhook requests: refuses loopback (the app itself), link-local (cloud metadata
/// 169.254.169.254), unspecified and multicast addresses — checked after DNS resolution and on every
/// connection, so redirects and DNS rebinding cannot bypass it. Private LAN/Docker addresses stay allowed
/// because self-hosted receivers (n8n, Uptime Kuma…) often live there.
/// </summary>
public static class WebhookHttp
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        ConnectCallback = ConnectAsync,
        MaxAutomaticRedirections = 3,
    };

    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal;
        var b = address.GetAddressBytes();
        return b[0] == 0 || b[0] == 127 || (b[0] == 169 && b[1] == 254) || b[0] >= 224;
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var allowed = addresses.Where(a => !IsBlocked(a)).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException($"Destino do webhook bloqueado (loopback/link-local): {context.DnsEndPoint.Host}");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
