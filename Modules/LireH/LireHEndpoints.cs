using System.Data;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;
using Utils.Colas.Infrastructure;

namespace Utils.Colas.Modules.LireH;

public sealed class LireHEnvironment
{
    public string[] Connections { get; set; } = [];
    public string Schema { get; set; } = string.Empty;
}

// Lire .h (ex Utils.htm / appli-jade03) : extrait include64/<objet>.h du BLOB F98780R.
public static partial class LireHEndpoints
{
    private const int ChunkSize = 2000;
    private const int MaxChunks = 5000;

    [GeneratedRegex("^[A-Za-z0-9_$]{1,10}$")]
    private static partial Regex ObjectNameRegex();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_$]{0,29}$")]
    private static partial Regex SchemaRegex();

    public static Dictionary<string, LireHEnvironment> LoadEnvironments(IConfiguration configuration)
    {
        var envs = new Dictionary<string, LireHEnvironment>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in configuration.GetSection("LireH:Environments").GetChildren())
        {
            var e = new LireHEnvironment();
            s.Bind(e);
            envs[s.Key] = e;
        }
        return envs;
    }

    public static IEndpointRouteBuilder MapLireH(this IEndpointRouteBuilder app)
    {
        var environments = LoadEnvironments(app.ServiceProvider.GetRequiredService<IConfiguration>());
        var group = app.MapGroup("/api/lireh");

        group.MapGet("/environments", (ConnectionStore store) =>
            Results.Ok(environments.Select(e =>
            {
                var conn = e.Value.Connections.FirstOrDefault(store.All.ContainsKey);
                var schema = conn is not null && !string.IsNullOrWhiteSpace(store.All[conn].Schema) ? store.All[conn].Schema : e.Value.Schema;
                var color = conn is not null ? store.All[conn].BannerColor : string.Empty;
                return new { code = e.Key, schema, connection = conn, bannerColor = color, available = conn is not null };
            })));

        // mode=read -> JSON { header } ; mode=download -> fichier .h ; mode=zip -> archive brute
        group.MapGet("/header", async (HttpContext ctx, ConnectionStore store, ILoggerFactory lf, string? env, string? objet, string? mode) =>
        {
            var name = (objet ?? string.Empty).Trim().ToUpperInvariant();
            if (name.EndsWith(".H", StringComparison.Ordinal)) name = name[..^2];
            if (!ObjectNameRegex().IsMatch(name))
                return Results.BadRequest(new { error = "Nom d'objet JDE invalide (1 à 10 caractères alphanumériques)." });

            if (env is null || !environments.TryGetValue(env, out var envInfo))
                return Results.BadRequest(new { error = $"Environnement inconnu ({string.Join(", ", environments.Keys)})." });

            var connName = envInfo.Connections.FirstOrDefault(store.All.ContainsKey);
            if (connName is null || !store.TryGet(connName, out var cfg))
                return Results.BadRequest(new { error = $"Aucune connexion configurée pour {env}." });
            if (!cfg.HasCredentials)
                return Results.BadRequest(new { error = $"UserId / Password manquants pour {connName} (secrets.json)." });

            // Le champ "Schema" de la connexion prévaut sur le schéma par défaut de l'environnement.
            var schema = string.IsNullOrWhiteSpace(cfg.Schema) ? envInfo.Schema : cfg.Schema.Trim();
            if (!SchemaRegex().IsMatch(schema))
                return Results.BadRequest(new { error = $"Schéma invalide : '{schema}'." });
            var sql = $"SELECT RAWTOHEX(DBMS_LOB.SUBSTR(XMOMRBLOB, {ChunkSize}, :pos)) FROM {schema}.F98780R WHERE TRIM(XMOMWOBJID) = :objid AND ROWNUM = 1";

            try
            {
                await using var conn = new OracleConnection(ConnectionStore.ConnectionString(cfg));
                await conn.OpenAsync(ctx.RequestAborted);

                using var zipBuffer = new MemoryStream();
                var position = 1;
                for (var i = 0; i < MaxChunks; i++)
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.BindByName = true;
                    cmd.CommandTimeout = 30;
                    cmd.CommandText = sql;
                    cmd.Parameters.Add(new OracleParameter("pos", OracleDbType.Int32, position, ParameterDirection.Input));
                    cmd.Parameters.Add(new OracleParameter("objid", OracleDbType.Varchar2, name, ParameterDirection.Input));

                    var hex = (await cmd.ExecuteScalarAsync(ctx.RequestAborted)) as string;
                    if (string.IsNullOrEmpty(hex)) break;

                    var bytes = Convert.FromHexString(hex);
                    zipBuffer.Write(bytes);
                    position += bytes.Length;
                    if (bytes.Length < ChunkSize) break;
                }

                if (zipBuffer.Length == 0)
                    return Results.NotFound(new { error = $"Objet {name} introuvable dans {schema}.F98780R." });

                if (string.Equals(mode, "zip", StringComparison.OrdinalIgnoreCase))
                    return Results.File(zipBuffer.ToArray(), "application/zip", $"{name}.zip");

                zipBuffer.Position = 0;
                using var archive = new ZipArchive(zipBuffer, ZipArchiveMode.Read);
                var target = $"include64/{name}.h";
                var entry = archive.Entries.FirstOrDefault(e =>
                    string.Equals(e.FullName.Replace('\\', '/'), target, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                    return Results.NotFound(new { error = $"{target} absent de l'archive.", entries = archive.Entries.Select(e => e.FullName).ToList() });

                string text;
                using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, true))
                    text = await reader.ReadToEndAsync(ctx.RequestAborted);

                if (string.Equals(mode, "download", StringComparison.OrdinalIgnoreCase))
                    return Results.File(new UTF8Encoding(false).GetBytes(text), "text/plain; charset=utf-8", $"{name}.h");

                return Results.Ok(new { env, schema, objectName = name, length = text.Length, header = text });
            }
            catch (InvalidDataException)
            {
                return Results.Json(new { error = "Le BLOB lu n'est pas une archive ZIP valide." }, statusCode: 500);
            }
            catch (OracleException ex)
            {
                lf.CreateLogger("LireH").LogError(ex, "Lecture {Object} sur {Connection} impossible", name, connName);
                return Results.Json(new { error = "Lecture impossible", detail = ex.Message }, statusCode: 500);
            }
        });

        return app;
    }
}
