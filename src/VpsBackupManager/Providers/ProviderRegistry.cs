namespace VpsBackupManager.Providers;

/// <summary>Maps a connection type ("mysql", "postgresql"…) to its provider.</summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<string, IDatabaseBackupProvider> _byType = new(StringComparer.OrdinalIgnoreCase);

    public ProviderRegistry(IEnumerable<IDatabaseBackupProvider> providers)
    {
        foreach (var p in providers)
            foreach (var t in p.Types)
                _byType[t] = p;
    }

    public static ProviderRegistry CreateDefault() =>
        new([new SqlServerProvider(), new MySqlProvider("mysql"), new MySqlProvider("mariadb"), new PostgreSqlProvider()]);

    public IReadOnlyCollection<string> SupportedTypes => _byType.Keys;

    public IDatabaseBackupProvider Get(string type) =>
        _byType.TryGetValue(type, out var p) ? p : throw new ProviderException($"Tipo de banco não suportado: {type}");

    public bool IsSupported(string type) => _byType.ContainsKey(type);
}
