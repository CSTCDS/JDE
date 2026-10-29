using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;
using Utils.Colas.Infrastructure;

namespace Utils.Colas.Modules.LireH;

// Lire .h (ex Utils.htm / appli-jade03) : affiche un fichier du package F98780R (include64/<objet>.h en priorité).
// Environnements = connexions de OracleConnections (aucune liste séparée).
public static partial class LireHEndpoints
{
    [GeneratedRegex("^[A-Za-z0-9_$]{1,10}$")]
    private static partial Regex ObjectNameRegex();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_$]{0,29}$")]
    private static partial Regex SchemaRegex();

    public static IEndpointRouteBuilder MapLireH(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/lireh");

        group.MapGet("/environments", (ConnectionStore store) =>
            Results.Ok(store.Ordered.Select(c => new
            {
                code = c.Name, schema = c.EnvironmentSchema, connection = c.Name, bannerColor = c.BannerColor, available = c.HasCredentials
            })));

        // mode=read -> JSON { content, files } ; mode=download -> fichier affiché ; mode=zip -> archive brute.
        // path=<chemin dans l'archive> force le fichier ; sinon recherche automatique (JdePackage.FindMain).
        group.MapGet("/header", async (HttpContext ctx, ConnectionStore store, ILoggerFactory lf, string? env, string? objet, string? mode, string? path) =>
        {
            var name = (objet ?? string.Empty).Trim().ToUpperInvariant();
            if (name.EndsWith(".H", StringComparison.Ordinal)) name = name[..^2];
            if (!ObjectNameRegex().IsMatch(name))
                return Results.BadRequest(new { error = "Nom d'objet JDE invalide (1 à 10 caractères alphanumériques)." });

            if (env is null || !store.TryGet(env, out var cfg))
                return Results.BadRequest(new { error = $"Environnement inconnu ({string.Join(", ", store.Ordered.Select(c => c.Name))})." });
            var connName = cfg.Name;
            if (!cfg.HasCredentials)
                return Results.BadRequest(new { error = $"UserId / Password manquants pour {connName} (secrets.json)." });

            var schema = cfg.EnvironmentSchema;
            if (!SchemaRegex().IsMatch(schema))
                return Results.BadRequest(new { error = $"Schéma invalide : '{schema}'." });

            try
            {
                byte[]? zip;
                await using (var conn = await OracleErrors.OpenAsync(cfg, ctx))
                    zip = await JdePackage.ReadBlobAsync(conn, schema, name, 60, ctx.RequestAborted);

                if (zip is null)
                    return Results.NotFound(new { error = $"Objet {name} introuvable dans {schema}.F98780R." });

                if (string.Equals(mode, "zip", StringComparison.OrdinalIgnoreCase))
                    return Results.File(zip, "application/zip", $"{name}.zip");

                var files = JdePackage.Open(zip);
                var tree = files.Select(f => new { path = f.Path, size = f.Size }).ToList();

                PackageMatch? match;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var chosen = JdePackage.Find(files, path);
                    if (chosen is null)
                        return Results.NotFound(new { error = $"{path} absent de l'archive.", files = tree });
                    match = new(chosen, "choix");
                }
                else match = JdePackage.FindMain(files, name);

                if (match is null)
                    return string.Equals(mode, "download", StringComparison.OrdinalIgnoreCase)
                        ? Results.NotFound(new { error = "Aucun fichier .h / .c ni spec de l'objet : choisir un fichier dans l'arborescence.", files = tree })
                        : Results.Ok(new { env, schema, objectName = name, path = (string?)null, files = tree,
                            message = "Ni include64, ni fichier .h / .c, ni spec de l'objet dans specs.zip : choisir un fichier dans l'arborescence." });

                var file = match.File;
                if (string.Equals(mode, "download", StringComparison.OrdinalIgnoreCase))
                    return Results.File(file.Content, JdePackage.IsText(file.Content) ? "text/plain; charset=utf-8" : "application/octet-stream", file.Name);

                var binary = !JdePackage.IsText(file.Content);
                var text = binary ? JdePackage.HexDump(file.Content) : JdePackage.Decode(file.Content);
                return Results.Ok(new
                {
                    env, schema, objectName = name, path = file.Path, rule = match.Rule, binary,
                    size = file.Size, length = text.Length, header = text, files = tree
                });
            }
            catch (InvalidDataException)
            {
                return Results.Json(new { error = "Le BLOB lu n'est pas une archive ZIP valide." }, statusCode: 500);
            }
            catch (PackageLimitException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 413);
            }
            catch (OracleException ex)
            {
                lf.CreateLogger("LireH").LogError(ex, "Lecture {Object} sur {Connection} impossible", name, connName);
                return Results.Json(new { error = "Lecture impossible", detail = OracleErrors.Describe(ex, connName) }, statusCode: 502);
            }
        });

        return app;
    }
}
