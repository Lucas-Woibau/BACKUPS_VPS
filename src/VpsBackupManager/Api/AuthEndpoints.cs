using System.Text;
using System.Text.RegularExpressions;
using VpsBackupManager.Config;
using VpsBackupManager.Data;
using VpsBackupManager.Security;
using VpsBackupManager.Services;

namespace VpsBackupManager.Api;

public sealed record LoginRequest(string Username, string Password);
public sealed record SetupRequest(string Token, string Username, string Password);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public static partial class AuthEndpoints
{
    // Used when the user does not exist so the response time does not reveal valid usernames.
    // PublicationOnly: a transient PasswordHasherBusyException must not be cached forever by Lazy.
    private static readonly Lazy<string> DummyHash = new(() => PasswordHasher.Hash(Guid.NewGuid().ToString()),
        LazyThreadSafetyMode.PublicationOnly);

    /// <summary>Header with the current password, base64(UTF-8), required for sensitive changes.</summary>
    public const string ReauthHeader = "X-Current-Password";

    /// <summary>
    /// Re-checks the logged-in user's password before a sensitive change (encryption keys, retention,
    /// destination, webhook, Google account), so a stolen session alone cannot sabotage backups.
    /// Returns null when allowed, otherwise the error response. Attempts are throttled per user.
    /// </summary>
    public static async Task<IResult?> RequireReauthAsync(HttpContext ctx, Db db, LoginThrottle throttle, string action)
    {
        var session = ctx.Session();
        if (session is null) return Results.Json(new { error = "Não autenticado." }, statusCode: 401);
        var key = "reauth:" + session.UserId;
        if (throttle.IsBlocked(key)) return Results.Json(new { error = "Muitas tentativas. Aguarde 15 minutos." }, statusCode: 429);

        string? password = null;
        var raw = ctx.Request.Headers[ReauthHeader].ToString();
        if (raw.Length is > 0 and <= 2048)
        {
            try { password = Encoding.UTF8.GetString(Convert.FromBase64String(raw)); }
            catch (FormatException) { /* treated as missing */ }
        }
        if (string.IsNullOrEmpty(password))
            return Results.Json(new { error = $"Confirme sua senha atual para {action}.", reauthRequired = true }, statusCode: 403);

        var user = await db.QueryOneAsync<UserRow>("SELECT * FROM users WHERE id=@id", new { id = session.UserId });
        if (user is null || !PasswordHasher.Verify(user.PasswordHash, password))
        {
            throttle.Fail(key);
            return Results.Json(new { error = "Senha atual incorreta.", reauthRequired = true }, statusCode: 403);
        }
        throttle.Reset(key);
        return null;
    }

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/auth");

        g.MapGet("/status", async (HttpContext ctx, SetupTokenService setup, SettingsService settings) =>
        {
            var s = ctx.Session();
            var appSettings = await settings.GetAsync();
            return Results.Ok(new
            {
                setupRequired = !await setup.HasAdminAsync(),
                authenticated = s is not null,
                username = s?.Username,
                csrfToken = s?.CsrfToken,
                timezone = appSettings.Timezone,
                vpsName = appSettings.VpsName,
            });
        });

        g.MapPost("/setup", async (SetupRequest req, HttpContext ctx, Db db, SetupTokenService setup, SessionService sessions,
            LoginThrottle throttle, AppOptions options) =>
        {
            var ipKey = "setup:" + ctx.ClientIp();
            if (throttle.IsBlocked(ipKey)) return Results.Json(new { error = "Muitas tentativas. Aguarde 15 minutos." }, statusCode: 429);
            if (await setup.HasAdminAsync()) return Results.Conflict(new { error = "Administrador já cadastrado." });
            if (!setup.Check(req.Token))
            {
                throttle.Fail(ipKey);
                return Results.BadRequest(new { error = "Token de primeiro acesso inválido." });
            }
            var errors = ValidateUsername(req.Username);
            errors.AddRange(PasswordHasher.ValidateStrength(req.Password ?? "", req.Username ?? ""));
            if (errors.Count > 0) return Results.BadRequest(new { error = string.Join(" ", errors), errors });

            var id = await db.InsertAsync("INSERT INTO users(username, password_hash, created_at) VALUES (@u, @h, @n)",
                new { u = req.Username!.Trim(), h = PasswordHasher.Hash(req.Password!), n = Clock.NowMs() });
            setup.Consume();
            var (token, csrf) = await sessions.CreateAsync(id, ctx.ClientIp(), ctx.Request.Headers.UserAgent);
            SetCookie(ctx, token, options);
            return Results.Ok(new { csrfToken = csrf, username = req.Username!.Trim() });
        });

        g.MapPost("/login", async (LoginRequest req, HttpContext ctx, Db db, SessionService sessions, LoginThrottle throttle,
            AppOptions options, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("Auth");
            var username = (req.Username ?? "").Trim();
            var keys = new[] { "ip:" + ctx.ClientIp(), "user:" + username.ToLowerInvariant() };
            if (throttle.IsBlocked(keys))
                return Results.Json(new { error = "Muitas tentativas. Aguarde 15 minutos." }, statusCode: 429);

            var user = await db.QueryOneAsync<UserRow>("SELECT * FROM users WHERE username = @username", new { username });
            var ok = PasswordHasher.Verify(user?.PasswordHash ?? DummyHash.Value, req.Password ?? "") && user is not null;
            if (!ok)
            {
                throttle.Fail(keys);
                log.LogWarning("Login falhou para usuário '{User}' de {Ip}", username, ctx.ClientIp());
                return Results.Json(new { error = "Usuário ou senha inválidos." }, statusCode: 401);
            }
            throttle.Reset(keys);
            await db.ExecuteAsync("UPDATE users SET last_login_at=@n WHERE id=@id", new { n = Clock.NowMs(), id = user!.Id });
            var (token, csrf) = await sessions.CreateAsync(user.Id, ctx.ClientIp(), ctx.Request.Headers.UserAgent);
            SetCookie(ctx, token, options);
            log.LogInformation("Login de '{User}' a partir de {Ip}", user.Username, ctx.ClientIp());
            return Results.Ok(new { csrfToken = csrf, username = user.Username });
        });

        g.MapPost("/logout", async (HttpContext ctx, SessionService sessions, AppOptions options) =>
        {
            await sessions.DestroyAsync(ctx.Request.Cookies[SessionService.CookieName]);
            ctx.Response.Cookies.Delete(SessionService.CookieName, CookieOptions(options));
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/change-password", async (ChangePasswordRequest req, HttpContext ctx, Db db, SessionService sessions) =>
        {
            var s = ctx.Session()!;
            var user = await db.QueryOneAsync<UserRow>("SELECT * FROM users WHERE id=@id", new { id = s.UserId });
            if (user is null || !PasswordHasher.Verify(user.PasswordHash, req.CurrentPassword ?? ""))
                return Results.BadRequest(new { error = "Senha atual incorreta." });
            var errors = PasswordHasher.ValidateStrength(req.NewPassword ?? "", user.Username);
            if (errors.Count > 0) return Results.BadRequest(new { error = string.Join(" ", errors), errors });
            await db.ExecuteAsync("UPDATE users SET password_hash=@h WHERE id=@id", new { h = PasswordHasher.Hash(req.NewPassword!), id = user.Id });
            await sessions.DestroyOtherSessionsAsync(user.Id, s.TokenHash);
            return Results.Ok(new { ok = true });
        });
    }

    private static CookieOptions CookieOptions(AppOptions options) => new()
    {
        HttpOnly = true,
        Secure = options.SessionCookieSecure,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
    };

    private static void SetCookie(HttpContext ctx, string token, AppOptions options)
    {
        var o = CookieOptions(options);
        o.MaxAge = TimeSpan.FromHours(options.SessionTtlHours);
        ctx.Response.Cookies.Append(SessionService.CookieName, token, o);
    }

    private static List<string> ValidateUsername(string? username) =>
        UsernameRegex().IsMatch(username?.Trim() ?? "") ? [] : ["Usuário deve ter 3 a 32 caracteres (letras, números, . _ -)."];

    [GeneratedRegex("^[A-Za-z0-9_.-]{3,32}$")]
    private static partial Regex UsernameRegex();
}
