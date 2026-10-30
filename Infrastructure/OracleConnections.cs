using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Oracle.ManagedDataAccess.Client;

namespace Utils.Colas.Infrastructure;

// Modèle unifié : reprend les champs de Lire .h (Schema) et de JDE Compare (SystemSchema, DefaultSchema, BannerColor...).
public sealed class OracleConnectionConfig
{
    public string Name { get; set; } = string.Empty;
    public string TnsAdmin { get; set; } = string.Empty;
    public string DataSource { get; set; } = string.Empty;
    public string SysDataSource { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string WebEnvironment { get; set; } = string.Empty;
    public string Schema { get; set; } = string.Empty;
    public string SystemSchema { get; set; } = string.Empty;
    public string DefaultSchema { get; set; } = string.Empty;
    public string CtlSchema { get; set; } = string.Empty;
    public string BannerColor { get; set; } = string.Empty;
    // Rang d'affichage dans les listes d'environnements (la configuration .NET trie les clés par ordre alphabétique).
    public int Order { get; set; } = int.MaxValue;
    public bool UseEncryption { get; set; }

    // Schéma JDE de l'environnement (F98780R pour Lire .h, specs pour JDE Compare).
    public string EnvironmentSchema => string.IsNullOrWhiteSpace(Schema) ? Name : Schema.Trim();

    public bool HasCredentials => !string.IsNullOrWhiteSpace(UserId) && !string.IsNullOrWhiteSpace(Password);
}

public sealed class ConnectionStore
{
    // Entropie DPAPI historique (PackTool / appli-jade03) : les valeurs ENC: existantes restent lisibles.
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("PackTool.OracleConnection.v1");
    private const string LegacyProtectorPurpose = "NERcompare.OracleConnection";

    private readonly Dictionary<string, OracleConnectionConfig> _connections;
    private readonly IDataProtector _protector;

    public ConnectionStore(IConfiguration configuration, IDataProtectionProvider dataProtection, ILogger<ConnectionStore> logger)
    {
        _connections = new(StringComparer.OrdinalIgnoreCase);
        var protector = _protector = dataProtection.CreateProtector(LegacyProtectorPurpose);

        foreach (var section in configuration.GetSection("OracleConnections").GetChildren())
        {
            var c = new OracleConnectionConfig();
            section.Bind(c);
            c.Name = section.Key;
            if (string.IsNullOrWhiteSpace(c.DataSource))
            {
                logger.LogWarning("Connexion Oracle {Connection} ignorée : DataSource absent (clé orpheline dans secrets.json / appsettings.Production.json ?).", c.Name);
                continue;
            }
            if (c.UseEncryption)
            {
                c.UserId = Decrypt(c.UserId, protector, section.Key);
                c.Password = Decrypt(c.Password, protector, section.Key);
            }
            if (!c.HasCredentials)
                logger.LogWarning("Connexion Oracle {Connection} sans UserId/Password (renseigner secrets.json).", c.Name);
            logger.LogInformation("Connexion Oracle {Connection} : Data Source = {DataSource}", c.Name, c.DataSource);
            _connections[c.Name] = c;
        }
        logger.LogInformation("{Count} connexion(s) Oracle chargée(s) : {Names}", _connections.Count, string.Join(", ", _connections.Keys));
        ApplyTnsAdmin(logger);
    }

    // TNS_ADMIN est global au processus pour ODP.NET : appliqué une seule fois, au démarrage.
    private void ApplyTnsAdmin(ILogger logger)
    {
        var dirs = _connections.Values.Select(c => c.TnsAdmin).Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (dirs.Count == 0) return;
        if (dirs.Count > 1)
            logger.LogWarning("TnsAdmin différents selon les connexions ({Dirs}) : seul {Used} est utilisé.", string.Join(", ", dirs), dirs[0]);
        if (!File.Exists(Path.Combine(dirs[0], "tnsnames.ora")))
            logger.LogWarning("TnsAdmin {Dir} : tnsnames.ora introuvable, les alias TNS ne seront pas résolus (ORA-12154).", dirs[0]);
        OracleConfiguration.TnsAdmin = dirs[0];
    }

    public IReadOnlyDictionary<string, OracleConnectionConfig> All => _connections;

    // Liste unique des environnements affichés par tous les utilitaires (ordre : Order puis nom).
    public IReadOnlyList<OracleConnectionConfig> Ordered =>
        _connections.Values.OrderBy(c => c.Order).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public bool TryGet(string? name, out OracleConnectionConfig config)
    {
        if (!string.IsNullOrWhiteSpace(name) && _connections.TryGetValue(name, out var c))
        {
            config = c;
            return true;
        }
        config = null!;
        return false;
    }

    public static string ConnectionString(OracleConnectionConfig c, bool useSystemDataSource = false)
    {
        var ds = useSystemDataSource && !string.IsNullOrWhiteSpace(c.SysDataSource) ? c.SysDataSource : c.DataSource;
        return $"User Id={c.UserId};Password={c.Password};Data Source={ds};Connection Timeout=30";
    }

    // Secret quelconque (ex. clé d'API) : valeur en clair ou ENC:, même format que les identifiants Oracle.
    public string Unprotect(string value, string label) => Decrypt(value, _protector, label);

    // ENC:<base64> : DPAPI (format Lire .h / PackTool), sinon ASP.NET Data Protection (format NERcompare).
    private static string Decrypt(string value, IDataProtector protector, string connection)
    {
        if (!value.StartsWith("ENC:", StringComparison.Ordinal)) return value;
        var payload = value[4..];
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(payload), DpapiEntropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // Pas un blob DPAPI : on tente le format Data Protection.
            }
        }
        try
        {
            return protector.Unprotect(payload);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException($"Impossible de déchiffrer le secret '{connection}' (identité / clés différentes ?).", ex);
        }
    }
}
