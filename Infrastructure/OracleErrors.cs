using Oracle.ManagedDataAccess.Client;

namespace Utils.Colas.Infrastructure;

// Traduction des erreurs Oracle courantes en messages exploitables par l'utilisateur.
// Ne contient jamais d'identifiant ni de chaîne de connexion : seulement le nom logique (ex. DV920) et le code ORA.
public static class OracleErrors
{
    public static string Describe(OracleException ex, string? connection) => Describe(ex.Number, ex.Message, connection);

    private const string FailedConnectionKey = "Utils.Colas.FailedOracleConnection";

    // Ouvre la connexion et, en cas d'échec, mémorise son nom pour le message d'erreur (plusieurs connexions par requête).
    public static async Task<OracleConnection> OpenAsync(OracleConnectionConfig cfg, HttpContext http)
    {
        var conn = new OracleConnection(ConnectionStore.ConnectionString(cfg));
        try
        {
            await conn.OpenAsync(http.RequestAborted);
            return conn;
        }
        catch (OracleException)
        {
            http.Items[FailedConnectionKey] = cfg.Name;
            await conn.DisposeAsync();
            throw;
        }
    }

    public static string? FailedConnection(HttpContext http) => http.Items[FailedConnectionKey] as string;

    public static string Describe(int number, string oracleMessage, string? connection)
    {
        var on = string.IsNullOrWhiteSpace(connection) ? "" : $" (connexion {connection})";
        var code = $"ORA-{number:D5}";
        var hint = number switch
        {
            12154 or 12262 => "Identifiant de connexion Oracle introuvable : vérifier DataSource (host:port/service) ou TnsAdmin/tnsnames.ora dans appsettings.Production.json.",
            12541 => "Aucun listener Oracle à l'adresse configurée : vérifier l'hôte et le port de DataSource.",
            12514 or 12505 => "Service Oracle inconnu du listener : vérifier le nom de service après le « / » dans DataSource.",
            12545 => "Hôte Oracle introuvable (DNS) : vérifier le nom d'hôte de DataSource depuis le serveur.",
            12170 or 12543 or 12535 => "Serveur Oracle injoignable ou délai de connexion dépassé (réseau / pare-feu).",
            50000 when oracleMessage.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                => "Délai de connexion Oracle dépassé.",
            1017 => "Utilisateur ou mot de passe Oracle refusé : vérifier secrets.json.",
            28000 => "Compte Oracle verrouillé.",
            28001 => "Mot de passe Oracle expiré : le renouveler puis mettre à jour secrets.json.",
            942 => "Table ou vue inaccessible : droits insuffisants ou schéma incorrect.",
            _ => null,
        };
        return hint is null ? $"{oracleMessage}{on}" : $"{hint}{on} [{code}]";
    }
}
