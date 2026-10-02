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
    private static readonly Lazy<string> DummyHash = new(() => PasswordHasher.Hash(Guid.NewGuid().ToString()));

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
