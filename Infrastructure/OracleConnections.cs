using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

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
    public bool UseEncryption { get; set; }

    public bool HasCredentials => !string.IsNullOrWhiteSpace(UserId) && !string.IsNullOrWhiteSpace(Password);
}

public sealed class ConnectionStore
{
    // Entropie DPAPI historique (PackTool / appli-jade03) : les valeurs ENC: existantes restent lisibles.
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("PackTool.OracleConnection.v1");
    private const string LegacyProtectorPurpose = "NERcompare.OracleConnection";

    private readonly Dictionary<string, OracleConnectionConfig> _connections;

    public ConnectionStore(IConfiguration configuration, IDataProtectionProvider dataProtection, ILogger<ConnectionStore> logger)
    {
        _connections = new(StringComparer.OrdinalIgnoreCase);
        var protector = dataProtection.CreateProtector(LegacyProtectorPurpose);

        foreach (var section in configuration.GetSection("OracleConnections").GetChildren())
        {
            var c = new OracleConnectionConfig();
            section.Bind(c);
            c.Name = section.Key;
            if (c.UseEncryption)
            {
                c.UserId = Decrypt(c.UserId, protector, section.Key);
                c.Password = Decrypt(c.Password, protector, section.Key);
            }
            if (!c.HasCredentials)
                logger.LogWarning("Connexion Oracle {Connection} sans UserId/Password (renseigner secrets.json).", c.Name);
            _connections[c.Name] = c;
        }
        logger.LogInformation("{Count} connexion(s) Oracle chargée(s) : {Names}", _connections.Count, string.Join(", ", _connections.Keys));
    }

    public IReadOnlyDictionary<string, OracleConnectionConfig> All => _connections;

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
        if (!string.IsNullOrWhiteSpace(c.TnsAdmin))
            System.Environment.SetEnvironmentVariable("TNS_ADMIN", c.TnsAdmin);
        var ds = useSystemDataSource && !string.IsNullOrWhiteSpace(c.SysDataSource) ? c.SysDataSource : c.DataSource;
        return $"User Id={c.UserId};Password={c.Password};Data Source={ds};Connection Timeout=30";
    }

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
            throw new InvalidOperationException($"Impossible de déchiffrer les identifiants de la connexion '{connection}' (identité / clés différentes ?).", ex);
        }
    }
}
