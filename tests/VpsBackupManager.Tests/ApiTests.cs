using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace VpsBackupManager.Tests;

/// <summary>Boots the real app (in-memory server) to test first access, auth gate and CSRF.</summary>
public sealed class ApiTests : IClassFixture<ApiTests.AppFactory>
{
    public sealed class AppFactory : WebApplicationFactory<Program>
    {
        public TempDir Temp { get; } = new();

        public AppFactory()
        {
            Environment.SetEnvironmentVariable("APP_SECRET_KEY", new string('t', 48));
            Environment.SetEnvironmentVariable("DATA_DIR", Temp.Sub("data"));
            Environment.SetEnvironmentVariable("BACKUP_ROOT", Temp.Sub("backups"));
            Environment.SetEnvironmentVariable("RCLONE_CONFIG", Path.Combine(Temp.Path, "rclone.conf"));
            Environment.SetEnvironmentVariable("HOST_GATEWAY_NAME", "127.0.0.1");
        }

        public string DataDir => Path.Combine(Temp.Path, "data");

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Temp.Dispose();
        }
    }

    private readonly AppFactory _factory;

    public ApiTests(AppFactory factory) => _factory = factory;

    private HttpClient Client() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static HttpRequestMessage Req(HttpMethod m, string url, object? body = null, string? csrf = null, bool ajax = true)
    {
        var r = new HttpRequestMessage(m, url);
        if (body is not null) r.Content = JsonContent.Create(body);
        if (ajax) r.Headers.Add("X-Requested-With", "vbm");
        if (csrf is not null) r.Headers.Add("X-CSRF-Token", csrf);
        return r;
    }

    [Fact]
    public async Task Full_first_access_and_security_flow()
    {
        var c = Client();

        // public endpoints
        var status = await c.GetFromJsonAsync<JsonElement>("/api/auth/status");
        Assert.True(status.GetProperty("setupRequired").GetBoolean());
        var health = await c.GetAsync("/api/health");
        Assert.Contains(health.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable });
        var healthBody = await health.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret", healthBody, StringComparison.OrdinalIgnoreCase);

        // protected without session
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/dashboard")).StatusCode);
        var page = await c.GetAsync("/connections.html");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("/setup.html", page.Headers.Location!.ToString());
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").First());

        // setup requires anti-CSRF header and valid token
        Assert.Equal(HttpStatusCode.Forbidden,
            (await c.SendAsync(Req(HttpMethod.Post, "/api/auth/setup", new { token = "x", username = "admin", password = "Very-Strong-Pass-1" }, ajax: false))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.SendAsync(Req(HttpMethod.Post, "/api/auth/setup", new { token = "wrong", username = "admin", password = "Very-Strong-Pass-1" }))).StatusCode);

        var token = (await File.ReadAllTextAsync(Path.Combine(_factory.DataDir, "setup_token"))).Trim();
        var weak = await c.SendAsync(Req(HttpMethod.Post, "/api/auth/setup", new { token, username = "admin", password = "admin" }));
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        var setup = await c.SendAsync(Req(HttpMethod.Post, "/api/auth/setup", new { token, username = "admin", password = "Very-Strong-Pass-1" }));
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var cookie = setup.Headers.GetValues("Set-Cookie").First();
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        var csrf = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("csrfToken").GetString();
        Assert.False(File.Exists(Path.Combine(_factory.DataDir, "setup_token")), "token deve ser invalidado");

        // second setup refused
        Assert.Equal(HttpStatusCode.Conflict,
            (await c.SendAsync(Req(HttpMethod.Post, "/api/auth/setup", new { token, username = "x2", password = "Very-Strong-Pass-1" }, csrf))).StatusCode);

        // authenticated reads
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/connections.html")).StatusCode);
        var root = await c.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Contains("/js/dashboard.js", await root.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/nao-existe")).StatusCode);

        // mutations need CSRF token
        var conn = new
        {
            name = "Principal", dbType = "mysql", host = "127.0.0.1", port = 3306, username = "backup_user", password = "pw-123456",
            sslMode = "disabled", options = new { }, backupAll = true, includeSystem = false, enabled = true,
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Req(HttpMethod.Post, "/api/connections", conn))).StatusCode);
        var created = await c.SendAsync(Req(HttpMethod.Post, "/api/connections", conn, csrf));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadAsStringAsync();
        Assert.DoesNotContain("pw-123456", body);
        Assert.DoesNotContain("passwordEnc", body);

        // validation errors are 400 with messages
        var bad = await c.SendAsync(Req(HttpMethod.Post, "/api/connections", new
        {
            name = "", dbType = "oracle", host = "-x", port = 0, username = "", password = "", sslMode = "?", backupAll = true, includeSystem = false, enabled = true,
        }, csrf));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // settings roundtrip + path traversal rejected
        var settings = await c.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal("gdrive", settings.GetProperty("rcloneRemote").GetString());
        var traversal = JsonSerializer.Deserialize<Dictionary<string, object>>(settings.GetRawText())!;
        traversal["remoteBasePath"] = "../../etc";
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Put, "/api/settings", traversal, csrf))).StatusCode);

        // schedules
        var sched = await c.SendAsync(Req(HttpMethod.Post, "/api/schedules", new
        {
            name = "Diário", enabled = true, kind = "weekly", daysOfWeek = new[] { 0, 1, 2, 3, 4, 5, 6 }, times = new[] { "03:00" },
            targetType = "all",
        }, csrf));
        Assert.Equal(HttpStatusCode.Created, sched.StatusCode);
        var schedules = await c.GetFromJsonAsync<JsonElement>("/api/schedules");
        Assert.NotNull(schedules[0].GetProperty("nextRunAt").GetString());

        // logout invalidates session
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Post, "/api/auth/logout", null, csrf))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/dashboard")).StatusCode);

        // login: wrong password, then right
        var c2 = Client();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await c2.SendAsync(Req(HttpMethod.Post, "/api/auth/login", new { username = "admin", password = "nope" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await c2.SendAsync(Req(HttpMethod.Post, "/api/auth/login", new { username = "admin", password = "Very-Strong-Pass-1" }))).StatusCode);
    }
}
