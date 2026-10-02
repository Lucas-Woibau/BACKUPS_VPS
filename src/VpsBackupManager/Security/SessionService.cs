using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using VpsBackupManager.Config;
using VpsBackupManager.Data;

namespace VpsBackupManager.Security;

public sealed record SessionInfo(long UserId, string Username, string CsrfToken, string TokenHash);

/// <summary>
/// Server-side sessions: the cookie carries a random token; only its SHA-256 is stored.
/// Sessions have an absolute TTL and an idle timeout and can be revoked (logout, password change).
/// </summary>
public sealed class SessionService(Db db, AppOptions options)
{
    public const string CookieName = "vbm_session";
    public const string CsrfHeader = "X-CSRF-Token";
    /// <summary>Custom header required on every state-changing request; cross-origin pages cannot send it
    /// without a CORS preflight, which this app never allows.</summary>
    public const string AjaxHeader = "X-Requested-With";
    public const string AjaxValue = "vbm";

    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));

    public async Task<(string Token, string Csrf)> CreateAsync(long userId, string? ip, string? userAgent)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var csrf = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = Clock.NowMs();
        await db.ExecuteAsync(
            """
            INSERT INTO sessions(token_hash, user_id, csrf_token, created_at, last_seen_at, expires_at, ip, user_agent)
            VALUES (@h, @u, @c, @n, @n, @e, @ip, @ua)
            """,
            new
            {
                h = HashToken(token), u = userId, c = csrf, n = now,
                e = now + (long)TimeSpan.FromHours(options.SessionTtlHours).TotalMilliseconds,
                ip = Trim(ip, 64), ua = Trim(userAgent, 256),
            });
        return (token, csrf);
    }

    public async Task<SessionInfo?> GetAsync(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128) return null;
        var hash = HashToken(token);
        var row = await db.QueryOneAsync<SessionJoin>(
            """
            SELECT s.user_id, u.username, s.csrf_token, s.last_seen_at, s.expires_at
            FROM sessions s JOIN users u ON u.id = s.user_id WHERE s.token_hash = @hash
            """, new { hash });
        if (row is null) return null;
        var now = Clock.NowMs();
        var idleMs = (long)TimeSpan.FromMinutes(options.SessionIdleMinutes).TotalMilliseconds;
        if (now >= row.ExpiresAt || now - row.LastSeenAt > idleMs)
        {
            await db.ExecuteAsync("DELETE FROM sessions WHERE token_hash = @hash", new { hash });
            return null;
        }
        if (now - row.LastSeenAt > 60_000)
            await db.ExecuteAsync("UPDATE sessions SET last_seen_at = @now WHERE token_hash = @hash", new { now, hash });
        return new SessionInfo(row.UserId, row.Username, row.CsrfToken, hash);
    }

    public Task DestroyAsync(string? token) =>
        string.IsNullOrEmpty(token)
            ? Task.CompletedTask
            : db.ExecuteAsync("DELETE FROM sessions WHERE token_hash = @h", new { h = HashToken(token) });

    public Task DestroyOtherSessionsAsync(long userId, string keepHash) =>
        db.ExecuteAsync("DELETE FROM sessions WHERE user_id = @userId AND token_hash != @keepHash", new { userId, keepHash });

    public Task PurgeExpiredAsync() =>
        db.ExecuteAsync("DELETE FROM sessions WHERE expires_at < @now", new { now = Clock.NowMs() });

    public static bool CsrfValid(SessionInfo session, string? provided) =>
        !string.IsNullOrEmpty(provided) &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(session.CsrfToken), Encoding.ASCII.GetBytes(provided));

    private sealed class SessionJoin
    {
        public long UserId { get; set; }
        public string Username { get; set; } = "";
        public string CsrfToken { get; set; } = "";
        public long LastSeenAt { get; set; }
        public long ExpiresAt { get; set; }
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Trim(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}

/// <summary>Sliding-window login throttle (per IP and per username).</summary>
public sealed class LoginThrottle(int maxFailures = 5, int windowSeconds = 900)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _failures = new();

    public bool IsBlocked(params string[] keys) => keys.Any(k => Count(k) >= maxFailures);

    private const int MaxTrackedKeys = 10_000;

    public void Fail(params string[] keys)
    {
        if (_failures.Count > MaxTrackedKeys) Prune();
        foreach (var k in keys)
        {
            var q = _failures.GetOrAdd(k, _ => new Queue<DateTimeOffset>());
            lock (q) q.Enqueue(Clock.UtcNow());
        }
    }

    public void Reset(params string[] keys)
    {
        foreach (var k in keys) _failures.TryRemove(k, out _);
    }

    /// <summary>Drops keys whose failures all expired, so random usernames cannot grow the map forever.</summary>
    private void Prune()
    {
        foreach (var key in _failures.Keys)
            if (Count(key) == 0) _failures.TryRemove(key, out _);
    }

    private int Count(string key)
    {
        if (!_failures.TryGetValue(key, out var q)) return 0;
        lock (q)
        {
            var limit = Clock.UtcNow().AddSeconds(-windowSeconds);
            while (q.Count > 0 && q.Peek() < limit) q.Dequeue();
            return q.Count;
        }
    }
}
