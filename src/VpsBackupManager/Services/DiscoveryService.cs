using System.Net.Sockets;
using System.Text.Json;
using VpsBackupManager.Config;

namespace VpsBackupManager.Services;

public sealed record DiscoveredService(string Source, string Type, string Name, string SuggestedHost, int SuggestedPort, string Detail);

/// <summary>
/// Safe discovery. Never scans networks and never reads credentials:
///  - host probe: TCP connect only to the Docker host gateway on the default ports (1433/3306/3307/5432/5433);
///  - Docker (optional): lists running containers through a READ-ONLY docker-socket-proxy
///    (GET /containers/json only — that endpoint does not return container environment variables).
/// </summary>
public sealed class DiscoveryService(AppOptions options, IHttpClientFactory httpFactory, ILogger<DiscoveryService> logger)
{
    private static readonly (int Port, string Type)[] KnownPorts = [(1433, "sqlserver"), (3306, "mysql"), (3307, "mariadb"), (5432, "postgresql"), (5433, "postgresql")];

    public async Task<object> DiscoverAsync(CancellationToken ct)
    {
        var found = new List<DiscoveredService>();
        found.AddRange(await ProbeHostAsync(ct));
        string? dockerMessage;
        if (string.IsNullOrEmpty(options.DockerDiscoveryUrl))
        {
            dockerMessage = "Descoberta Docker desativada (opcional; veja docs/04-bancos.md).";
        }
        else
        {
            try
            {
                found.AddRange(await DiscoverDockerAsync(ct));
                dockerMessage = "Containers listados via docker-socket-proxy (somente leitura).";
            }
            catch (Exception ex)
            {
                logger.LogWarning("Descoberta Docker falhou: {Error}", ex.Message);
                dockerMessage = "Falha ao consultar o docker-socket-proxy: " + ex.Message;
            }
        }
        return new { services = found, docker = dockerMessage, hostGateway = options.HostGateway };
    }

    private async Task<List<DiscoveredService>> ProbeHostAsync(CancellationToken ct)
    {
        var tasks = KnownPorts.Select(async p =>
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(1500));
            try
            {
                await client.ConnectAsync(options.HostGateway, p.Port, cts.Token);
                return new DiscoveredService("host", p.Type, $"Serviço no host :{p.Port}", options.HostGateway, p.Port,
                    "Porta aberta no host (tipo presumido pela porta; confirme ao testar a conexão).");
            }
            catch
            {
                return null;
            }
        });
        return (await Task.WhenAll(tasks)).Where(x => x is not null).Cast<DiscoveredService>().ToList();
    }

    private async Task<List<DiscoveredService>> DiscoverDockerAsync(CancellationToken ct)
    {
        var client = httpFactory.CreateClient("docker");
        var url = options.DockerDiscoveryUrl!.TrimEnd('/') + "/containers/json";
        using var doc = JsonDocument.Parse(await client.GetStringAsync(url, ct));
        var result = new List<DiscoveredService>();
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            var image = c.GetProperty("Image").GetString() ?? "";
            var type = image.ToLowerInvariant() switch
            {
                var i when i.Contains("mssql") || i.Contains("sql-server") || i.Contains("sqlserver") || i.Contains("azure-sql-edge") => "sqlserver",
                var i when i.Contains("mariadb") => "mariadb",
                var i when i.Contains("mysql") || i.Contains("percona") => "mysql",
                var i when i.Contains("postgres") || i.Contains("postgis") || i.Contains("timescale") => "postgresql",
                _ => null,
            };
            if (type is null) continue;
            var name = c.GetProperty("Names").EnumerateArray().Select(n => n.GetString()!.TrimStart('/')).FirstOrDefault() ?? "?";
            var internalPort = type switch { "postgresql" => 5432, "sqlserver" => 1433, _ => 3306 };
            var published = c.GetProperty("Ports").EnumerateArray()
                .Where(p => p.TryGetProperty("PublicPort", out _) && p.GetProperty("PrivatePort").GetInt32() == internalPort)
                .Select(p => p.GetProperty("PublicPort").GetInt32()).FirstOrDefault();
            var networks = c.TryGetProperty("NetworkSettings", out var ns) && ns.TryGetProperty("Networks", out var nets)
                ? string.Join(", ", nets.EnumerateObject().Select(n => n.Name)) : "";
            result.Add(new DiscoveredService("docker", type, name, name, internalPort,
                $"Imagem {image}; redes: {networks}" + (published > 0 ? $"; publicado no host em :{published}" : "") +
                ". Para usar o nome do container, conecte o backup manager à mesma rede (docker-compose.override.yml)."));
        }
        return result;
    }
}
