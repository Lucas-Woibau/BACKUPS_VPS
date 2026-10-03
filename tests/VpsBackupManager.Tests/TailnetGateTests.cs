using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using VpsBackupManager.Api;
using VpsBackupManager.Config;

namespace VpsBackupManager.Tests;

public class TailnetGateTests
{
    private const string Proxy = "10.213.77.3";
    private const string Allowed = "lucaswoibau7@gmail.com";

    private static AppOptions Options(string? proxy = Proxy, string[]? logins = null) => new()
    {
        SecretKey = new string('t', 48), DataDir = "d", BackupRoot = "b", LogDir = "l", RcloneConfig = "r",
        TailnetPort = 8081, TailnetProxyIp = proxy, TailnetAllowedLogins = logins ?? [Allowed],
    };

    private static async Task<(int Status, bool Passed, HttpContext Ctx)> Run(AppOptions options, int port, string remote,
        string? login, string? forwardedFor = null)
    {
        var passed = false;
        var ctx = new DefaultHttpContext();
        ctx.Connection.LocalPort = port;
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        if (login is not null) ctx.Request.Headers[TailnetGateMiddleware.LoginHeader] = login;
        if (forwardedFor is not null) ctx.Request.Headers["X-Forwarded-For"] = forwardedFor;
        ctx.Response.Body = new MemoryStream();
        var mw = new TailnetGateMiddleware(_ => { passed = true; return Task.CompletedTask; }, options,
            NullLogger<TailnetGateMiddleware>.Instance);
        await mw.InvokeAsync(ctx);
        return (ctx.Response.StatusCode, passed, ctx);
    }

    [Fact]
    public async Task Allows_sidecar_with_allowed_login_and_uses_client_ip()
    {
        var (_, passed, ctx) = await Run(Options(), 8081, Proxy, "LucasWoibau7@gmail.com", "100.101.102.103");
        Assert.True(passed);
        Assert.True(ctx.ViaTailnet());
        Assert.Equal("100.101.102.103", ctx.ClientIp());
    }

    [Theory]
    [InlineData("172.20.0.5", Allowed)]          // another container reaching the port directly
    [InlineData(Proxy, "intruso@gmail.com")]       // tailnet user not on the allow-list
    [InlineData(Proxy, null)]                      // tagged device / no identity header
    [InlineData(Proxy, "")]
    public async Task Refuses_wrong_origin_or_login(string remote, string? login)
    {
        var (status, passed, _) = await Run(Options(), 8081, remote, login);
        Assert.False(passed);
        Assert.Equal(403, status);
    }

    [Fact]
    public async Task Fails_closed_when_half_configured()
    {
        Assert.False((await Run(Options(proxy: null), 8081, Proxy, Allowed)).Passed);
        Assert.False((await Run(Options(logins: []), 8081, Proxy, Allowed)).Passed);
    }

    [Fact]
    public async Task Other_ports_are_untouched()
    {
        var (_, passed, ctx) = await Run(Options(), 8080, "172.17.0.1", null);
        Assert.True(passed);
        Assert.False(ctx.ViaTailnet());
    }
}
