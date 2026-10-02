using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using VpsBackupManager.Api;
using VpsBackupManager.Backup;
using VpsBackupManager.Config;
using VpsBackupManager.Data;
using VpsBackupManager.Notifications;
using VpsBackupManager.Providers;
using VpsBackupManager.Scheduling;
using VpsBackupManager.Security;
using VpsBackupManager.Services;
using VpsBackupManager.Storage;

var options = AppOptions.FromEnvironment();
options.EnsureDirectories();

// Recuperação de acesso: dotnet VpsBackupManager.dll reset-admin-password [usuario]
// (na VPS: docker exec -it vps_backup_manager dotnet VpsBackupManager.dll reset-admin-password admin)
if (args.Length > 0 && args[0] == "reset-admin-password")
{
    var resetDb = new Db(options.DbPath);
    resetDb.Migrate();
    var username = args.Length > 1 ? args[1] : "admin";
    var newPassword = "Vbm-" + Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12))
        .TrimEnd('=').Replace('+', 'x').Replace('/', 'y') + "-9a";
    var userId = await resetDb.QueryOneAsync<long?>("SELECT id FROM users WHERE username = @username", new { username });
    if (userId is null)
    {
        var existing = await resetDb.QueryAsync<string>("SELECT username FROM users ORDER BY id");
        Console.Error.WriteLine($"Usuário '{username}' não existe.");
        Console.Error.WriteLine(existing.Count == 0
            ? "Nenhum administrador cadastrado ainda: faça o primeiro acesso (token em /data/setup_token)."
            : "Usuários existentes: " + string.Join(", ", existing));
        return 1;
    }
    await resetDb.ExecuteAsync("UPDATE users SET password_hash = @h WHERE id = @id", new { h = PasswordHasher.Hash(newPassword), id = userId });
    await resetDb.ExecuteAsync("DELETE FROM sessions WHERE user_id = @id", new { id = userId });
    Console.WriteLine($"Nova senha de '{username}': {newPassword}");
    Console.WriteLine("Troque-a em Configurações → Trocar minha senha.");
    return 0;
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(Enum.TryParse<LogEventLevel>(options.LogLevel, true, out var lvl) ? lvl : LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter())
    .WriteTo.File(new CompactJsonFormatter(), Path.Combine(options.LogDir, "app-.log"),
        rollingInterval: RollingInterval.Day, fileSizeLimitBytes: 20 * 1024 * 1024, rollOnFileSizeLimit: true,
        retainedFileCountLimit: 30, shared: false)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, WebRootPath = "wwwroot" });
    builder.Host.UseSerilog();
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.AddServerHeader = false;
        k.Limits.MaxRequestBodySize = 1024 * 1024;
    });

    var services = builder.Services;
    services.AddSingleton(options);
    services.AddSingleton(new Db(options.DbPath));
    services.AddSingleton(new SecretBox(options.SecretKey));
    services.AddSingleton<SettingsService>();
    services.AddSingleton<SessionService>();
    services.AddSingleton<SetupTokenService>();
    services.AddSingleton(new LoginThrottle());
    services.AddSingleton(ProviderRegistry.CreateDefault());
    services.AddSingleton<IDumpExecutor, ProcessDumpExecutor>();
    services.AddSingleton<IStorageFactory, StorageFactory>();
    services.AddSingleton<IBackupEncryptor, AgeEncryptor>();
    services.AddSingleton<ConnectionService>();
    services.AddSingleton<ScheduleService>();
    services.AddSingleton<EventLog>();
    services.AddSingleton(new BackupLock(options.LockFile));
    services.AddSingleton<WebhookNotifier>();
    services.AddSingleton<INotifier>(sp => sp.GetRequiredService<WebhookNotifier>());
    services.AddSingleton<NotificationDispatcher>();
    services.AddSingleton<BackupOrchestrator>();
    services.AddSingleton<SchedulerService>();
    services.AddHostedService(sp => sp.GetRequiredService<SchedulerService>());
    services.AddSingleton<DashboardService>();
    services.AddSingleton<DiscoveryService>();
    services.AddSingleton<DriveAuthService>();
    services.AddHttpClient("webhook", c => c.Timeout = TimeSpan.FromSeconds(10));
    services.AddHttpClient("docker", c => c.Timeout = TimeSpan.FromSeconds(5));
    services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

    if (options.TrustProxyHeaders)
    {
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            foreach (var cidr in options.TrustedProxyNetworks.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
        });
    }

    var app = builder.Build();

    // ---- startup: migrations, crash recovery, first-access token
    var db = app.Services.GetRequiredService<Db>();
    foreach (var m in db.Migrate()) Log.Information("Migration aplicada: {Migration}", m);
    await app.Services.GetRequiredService<BackupOrchestrator>().RecoverInterruptedAsync();
    await app.Services.GetRequiredService<SetupTokenService>().EnsureAsync();

    if (options.TrustProxyHeaders) app.UseForwardedHeaders();
    app.UseMiddleware<SecurityHeadersMiddleware>();
    app.UseMiddleware<ErrorHandlingMiddleware>();
    app.UseMiddleware<AuthGateMiddleware>();
    app.UseDefaultFiles();
    app.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
    });

    AuthEndpoints.Map(app);
    ApiEndpoints.Map(app);
    app.MapFallback("/api/{**path}", ctx =>
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return ctx.Response.WriteAsJsonAsync(new { error = "Não encontrado." });
    });

    Log.Information("VPS Backup Manager iniciado. Dados: {DataDir}, backups: {BackupRoot}", options.DataDir, options.BackupRoot);
    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Falha fatal na inicialização");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
return 0;

public partial class Program;
