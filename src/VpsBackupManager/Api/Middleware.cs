using VpsBackupManager.Backup;
using VpsBackupManager.Providers;
using VpsBackupManager.Security;
using VpsBackupManager.Services;
using VpsBackupManager.Storage;

namespace VpsBackupManager.Api;

public static class HttpContextExtensions
{
    public static SessionInfo? Session(this HttpContext ctx) => ctx.Items["session"] as SessionInfo;

    public static string ClientIp(this HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

/// <summary>Security headers for every response. CSP forbids inline scripts/styles: all JS lives in /js.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
            "font-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        if (ctx.Request.Path.StartsWithSegments("/api")) h["Cache-Control"] = "no-store";
        return next(ctx);
    }
}

/// <summary>
/// Authentication + CSRF gate:
///  - every state-changing request needs header X-Requested-With: vbm (blocks cross-site form posts);
///  - authenticated state-changing requests also need X-CSRF-Token bound to the session;
///  - pages other than login/setup redirect to /login.html when there is no session.
/// </summary>
public sealed class AuthGateMiddleware(RequestDelegate next)
{
    private static readonly string[] PublicApi = ["/api/health", "/api/auth/status", "/api/auth/login", "/api/auth/setup"];
    private static readonly string[] PublicPrefixes = ["/css/", "/js/", "/img/"];
    private static readonly string[] PublicPages = ["/login.html", "/setup.html", "/favicon.svg"];

    public async Task InvokeAsync(HttpContext ctx, SessionService sessions, SetupTokenService setup)
    {
        var path = ctx.Request.Path.Value ?? "/";
        var isApi = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || path == "/api";
        var mutating = !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !HttpMethods.IsOptions(ctx.Request.Method);

        if (mutating && ctx.Request.Headers[SessionService.AjaxHeader] != SessionService.AjaxValue)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { error = "Requisição recusada (cabeçalho anti-CSRF ausente)." });
            return;
        }

        var session = await sessions.GetAsync(ctx.Request.Cookies[SessionService.CookieName]);
        ctx.Items["session"] = session;

        var isPublic = PublicApi.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase))
                       || PublicPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                       || PublicPages.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase));

        if (!isPublic && session is null)
        {
            if (isApi)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "Não autenticado." });
            }
            else
            {
                ctx.Response.Redirect(await setup.HasAdminAsync() ? "/login.html" : "/setup.html");
            }
            return;
        }

        if (mutating && session is not null && !SessionService.CsrfValid(session, ctx.Request.Headers[SessionService.CsrfHeader]))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { error = "Token CSRF inválido. Recarregue a página." });
            return;
        }

        await next(ctx);
    }
}

/// <summary>Maps known exceptions to JSON errors; never leaks stack traces.</summary>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (Exception ex) when (!ctx.Response.HasStarted)
        {
            var (status, body) = ex switch
            {
                ValidationException v => (400, (object)new { error = string.Join(" ", v.Errors), errors = v.Errors }),
                ProviderException or StorageException or PathGuard.UnsafePathException => (400, new { error = Redactor.Redact(ex.Message) }),
                KeyNotFoundException => (404, new { error = "Não encontrado." }),
                BackupBusyException => (409, new { error = ex.Message }),
                BadHttpRequestException or System.Text.Json.JsonException => (400, new { error = "Requisição inválida." }),
                _ => (500, new { error = "Erro interno. Consulte os logs." }),
            };
            if (status == 500) logger.LogError(ex, "Erro não tratado em {Path}", ctx.Request.Path);
            ctx.Response.StatusCode = status;
            await ctx.Response.WriteAsJsonAsync(body);
        }
    }
}
