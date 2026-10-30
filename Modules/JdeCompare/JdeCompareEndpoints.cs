using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;
using Utils.Colas.Infrastructure;

namespace Utils.Colas.Modules.JdeCompare;

public sealed class JdeCompareOptions
{
    public string ObjectLibrarianSchema { get; set; } = "OL920";
    public int MaxObjects { get; set; } = 500;
    public int CommandTimeoutSeconds { get; set; } = 60;
    // Analyse IA proposée au-delà de ce nombre de lignes de diff affichées (une modification = 2 lignes).
    public int AiDiffLineThreshold { get; set; } = 2;
    // Taille maximale du texte « Copier pour Copilot » (limite de saisie des applications de chat).
    public int CopilotMaxChars { get; set; } = 12000;
}

public sealed record CompareRequest(string? Source, string? Target, string[]? Objects);
public sealed record PackageDiffRequest(string? Object, string? Source, string? Target, string? Path);

// JDE Compare (ex NERcompare) : comparaison de specs d'objets JDE entre deux environnements.
public static partial class JdeCompareEndpoints
{
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_$]{0,29}$")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex("^[A-Za-z0-9_$]{1,20}$")]
    private static partial Regex ObjectNameRegex();

    public static IEndpointRouteBuilder MapJdeCompare(this IEndpointRouteBuilder app)
    {
        var configuration = app.ServiceProvider.GetRequiredService<IConfiguration>();
        var opt = new JdeCompareOptions();
        configuration.GetSection("JdeCompare").Bind(opt);
        var logger = app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("JdeCompare");

        // Environnements = connexions de OracleConnections (nom affiché / schéma specs = Schema), dans l'ordre Order.
        var envList = new List<(string Name, string Connection)>();
        var environments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in app.ServiceProvider.GetRequiredService<ConnectionStore>().Ordered)
        {
            var name = c.EnvironmentSchema;
            if (!environments.TryAdd(name, c.Name))
            {
                logger.LogWarning("Connexion {Connection} ignorée par JDE Compare : schéma {Schema} déjà fourni par {Other}.", c.Name, name, environments[name]);
                continue;
            }
            envList.Add((name, c.Name));
        }

        // Fail fast : ces valeurs sont interpolées dans le SQL.
        foreach (var id in environments.Keys.Append(opt.ObjectLibrarianSchema))
            if (!IdentifierRegex().IsMatch(id))
                throw new InvalidOperationException($"JdeCompare : identifiant de schéma invalide '{id}'.");

        var ol = opt.ObjectLibrarianSchema;
        var group = app.MapGroup("/api/jdecompare");
        group.AddEndpointFilter(async (ctx, next) =>
        {
            try
            {
                return await next(ctx);
            }
            catch (OracleException ex)
            {
                logger.LogError(ex, "Erreur Oracle sur {Path}", ctx.HttpContext.Request.Path);
                var detail = OracleErrors.Describe(ex, OracleErrors.FailedConnection(ctx.HttpContext));
                return Results.Problem(title: "Erreur Oracle", detail: detail, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        OracleCommand Cmd(OracleConnection conn, string sql)
        {
            var c = conn.CreateCommand();
            c.BindByName = true;
            c.CommandTimeout = opt.CommandTimeoutSeconds;
            c.CommandText = sql;
            return c;
        }

        static OracleParameter P(string name, object value, OracleDbType type = OracleDbType.Varchar2)
            => new(name, type, value, ParameterDirection.Input);

        async Task<IReadOnlyList<PackageFile>?> LoadPackage(OracleConnection conn, string schema, string objectName, CancellationToken ct)
        {
            var blob = await JdePackage.ReadBlobAsync(conn, schema, objectName, opt.CommandTimeoutSeconds, ct);
            return blob is null ? null : JdePackage.Open(blob);
        }

        // Une erreur de lecture du package n'invalide pas la comparaison des specs.
        async Task<PackageComparison> ComparePackages(OracleConnection srcConn, string srcSchema, OracleConnection tgtConn, string tgtSchema, string objectName, CancellationToken ct)
        {
            try
            {
                return JdePackage.Compare(await LoadPackage(srcConn, srcSchema, objectName, ct), await LoadPackage(tgtConn, tgtSchema, objectName, ct));
            }
            catch (Exception ex) when (ex is OracleException or InvalidDataException or PackageLimitException)
            {
                var msg = ex is OracleException oex ? OracleErrors.Describe(oex, null) : ex.Message;
                logger.LogWarning(ex, "Package F98780R de {Object} illisible", objectName);
                return new("erreur", $"Package illisible : {msg}", 0, []);
            }
        }

        IResult? Reference(HttpContext ctx, ConnectionStore store, out OracleConnectionConfig cfg)
        {
            var name = ResolveConnection(ctx.Request, store, configuration["DefaultConnection"]);
            if (!store.TryGet(name, out cfg))
                return Results.BadRequest(new { error = $"Connexion '{name}' introuvable." });
            if (!cfg.HasCredentials)
                return Results.BadRequest(new { error = $"UserId / Password manquants pour {name} (secrets.json)." });
            if (!IdentifierRegex().IsMatch(SystemSchema(cfg)))
                return Results.BadRequest(new { error = $"Schéma système invalide pour {name}." });
            return null;
        }

        group.MapGet("/environments", (ConnectionStore store) =>
            Results.Ok(envList.Select(e => new { environment = e.Name, connection = e.Connection, available = store.All[e.Connection].HasCredentials })));

        // Autocomplétion objets F9860 (schéma Object Librarian)
        group.MapGet("/objects", async (HttpContext ctx, ConnectionStore store, string? q, int? limit) =>
        {
            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 3) return Results.Ok(Array.Empty<object>());
            if (Reference(ctx, store, out var cfg) is { } err) return err;

            await using var conn = await OracleErrors.OpenAsync(cfg, ctx);
            await using var cmd = Cmd(conn, $@"SELECT SIOBNM, SIFUNO, SIMD FROM {ol}.F9860
WHERE UPPER(SIOBNM) LIKE '%' || UPPER(:q) || '%' ORDER BY SIOBNM FETCH FIRST :lim ROWS ONLY");
            cmd.Parameters.Add(P("q", q.Trim()));
            cmd.Parameters.Add(P("lim", Math.Clamp(limit ?? 20, 1, 50), OracleDbType.Int32));
            var rows = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ctx.RequestAborted);
            while (await r.ReadAsync(ctx.RequestAborted))
                rows.Add(new { objectName = Str(r, 0), objectType = Str(r, 1), description = Str(r, 2) });
            return Results.Ok(rows);
        });

        // Autocomplétion projets F98220 (schéma système)
        group.MapGet("/projects", async (HttpContext ctx, ConnectionStore store, string? q, int? limit) =>
        {
            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 3) return Results.Ok(Array.Empty<object>());
            if (Reference(ctx, store, out var cfg) is { } err) return err;

            await using var conn = await OracleErrors.OpenAsync(cfg, ctx);
            await using var cmd = Cmd(conn, $@"SELECT PMOMWPRJID, PMOMWPS, PMOMWDESC FROM {SystemSchema(cfg)}.F98220
WHERE UPPER(PMOMWPRJID) LIKE '%' || UPPER(:q) || '%' ORDER BY PMOMWPRJID FETCH FIRST :lim ROWS ONLY");
            cmd.Parameters.Add(P("q", q.Trim()));
            cmd.Parameters.Add(P("lim", Math.Clamp(limit ?? 20, 1, 50), OracleDbType.Int32));
            var rows = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ctx.RequestAborted);
            while (await r.ReadAsync(ctx.RequestAborted))
                rows.Add(new { projectId = Str(r, 0), status = Str(r, 1), description = Str(r, 2) });
            return Results.Ok(rows);
        });

        // Statuts distincts F98220.PMOMWPS
        group.MapGet("/statuses", async (HttpContext ctx, ConnectionStore store) =>
        {
            if (Reference(ctx, store, out var cfg) is { } err) return err;

            await using var conn = await OracleErrors.OpenAsync(cfg, ctx);
            await using var cmd = Cmd(conn, $@"SELECT DISTINCT PMOMWPS FROM {SystemSchema(cfg)}.F98220
WHERE PMOMWPS IS NOT NULL AND PMUPMJ > 125001 ORDER BY PMOMWPS");
            var statuses = new List<string>();
            await using var r = await cmd.ExecuteReaderAsync(ctx.RequestAborted);
            while (await r.ReadAsync(ctx.RequestAborted))
                if (!r.IsDBNull(0)) statuses.Add(Str(r, 0));
            return Results.Ok(statuses);
        });

        group.MapGet("/projects-by-status", async (HttpContext ctx, ConnectionStore store, string? status) =>
        {
            if (string.IsNullOrWhiteSpace(status)) return Results.BadRequest(new { error = "Paramètre status requis." });
            if (Reference(ctx, store, out var cfg) is { } err) return err;

            await using var conn = await OracleErrors.OpenAsync(cfg, ctx);
            await using var cmd = Cmd(conn, $@"SELECT PMOMWPRJID, PMOMWPS, PMOMWDESC FROM {SystemSchema(cfg)}.F98220
WHERE TRIM(PMOMWPS) = TRIM(:st) ORDER BY PMOMWPRJID");
            cmd.Parameters.Add(P("st", status.Trim()));
            var rows = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ctx.RequestAborted);
            while (await r.ReadAsync(ctx.RequestAborted))
                rows.Add(new { projectId = Str(r, 0), status = Str(r, 1), description = Str(r, 2) });
            return Results.Ok(new { count = rows.Count, data = rows });
        });

        // Objets d'un projet via F98222
        group.MapGet("/project-objects", async (HttpContext ctx, ConnectionStore store, string? prj) =>
        {
            if (string.IsNullOrWhiteSpace(prj)) return Results.BadRequest(new { error = "Paramètre prj requis." });
            if (Reference(ctx, store, out var cfg) is { } err) return err;

            await using var conn = await OracleErrors.OpenAsync(cfg, ctx);
            await using var cmd = Cmd(conn, $@"SELECT f222.POOMWOBJID, f222.POOMWOT, f860.SIMD
FROM {SystemSchema(cfg)}.F98222 f222
LEFT JOIN {ol}.F9860 f860 ON TRIM(f860.SIOBNM) = TRIM(f222.POOMWOBJID)
WHERE TRIM(f222.POOMWPRJID) = TRIM(:prj) ORDER BY f222.POOMWOBJID");
            cmd.Parameters.Add(P("prj", prj.Trim()));
            var rows = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ctx.RequestAborted);
            while (await r.ReadAsync(ctx.RequestAborted))
                rows.Add(new { objectName = Str(r, 0), objectType = Str(r, 1), description = Str(r, 2) });
            return Results.Ok(new { project = prj.Trim(), count = rows.Count, objects = rows });
        });

        group.MapPost("/compare", async (HttpContext ctx, ConnectionStore store, CompareRequest? body) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Source) || string.IsNullOrWhiteSpace(body.Target))
                return Results.BadRequest(new { error = "Source et cible requis." });
            var objects = (body.Objects ?? []).Select(o => o?.Trim() ?? "").Where(o => o.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (objects.Length == 0)
                return Results.BadRequest(new { error = "Au moins un objet requis." });
            if (objects.Length > opt.MaxObjects)
                return Results.BadRequest(new { error = $"Trop d'objets ({objects.Length} > {opt.MaxObjects})." });
            if (objects.FirstOrDefault(o => !ObjectNameRegex().IsMatch(o)) is { } bad)
                return Results.BadRequest(new { error = $"Nom d'objet invalide : '{bad}'." });

            if (Reference(ctx, store, out var refCfg) is { } err) return err;
            if (!environments.TryGetValue(body.Source, out var srcConnName) || !store.TryGet(srcConnName, out var srcCfg))
                return Results.BadRequest(new { error = $"Environnement source '{body.Source}' introuvable." });
            if (!environments.TryGetValue(body.Target, out var tgtConnName) || !store.TryGet(tgtConnName, out var tgtCfg))
                return Results.BadRequest(new { error = $"Environnement cible '{body.Target}' introuvable." });

            // Clés validées au démarrage : utilisables comme schéma SQL.
            var srcSchema = environments.Keys.First(k => k.Equals(body.Source, StringComparison.OrdinalIgnoreCase));
            var tgtSchema = environments.Keys.First(k => k.Equals(body.Target, StringComparison.OrdinalIgnoreCase));
            var specs = new SpecReader(Cmd, ol);

            await using var refConn = await OracleErrors.OpenAsync(refCfg, ctx);
            await using var srcConn = await OracleErrors.OpenAsync(srcCfg, ctx);
            await using var tgtConn = await OracleErrors.OpenAsync(tgtCfg, ctx);

            var results = new List<object>();
            var codes = new List<string>();
            foreach (var name in objects)
            {
                ctx.RequestAborted.ThrowIfCancellationRequested();
                try
                {
                    string objectType = "", objectDesc = "";
                    await using (var cmd = Cmd(refConn, $"SELECT SIFUNO, SIMD FROM {ol}.F9860 WHERE TRIM(SIOBNM) = TRIM(:obj)"))
                    {
                        cmd.Parameters.Add(P("obj", name));
                        await using var r = await cmd.ExecuteReaderAsync(ctx.RequestAborted);
                        if (await r.ReadAsync(ctx.RequestAborted)) { objectType = Str(r, 0); objectDesc = Str(r, 1); }
                    }

                    var srcInfo = await specs.GetF9861Info(refConn, name, srcSchema);
                    var tgtInfo = await specs.GetF9861Info(refConn, name, tgtSchema);
                    var srcSpec = await specs.GetSpecInfo(srcConn, srcSchema, name, objectType);
                    var tgtSpec = await specs.GetSpecInfo(tgtConn, tgtSchema, name, objectType);
                    var cmp = SpecReader.CompareSpecs(srcSpec, tgtSpec, srcInfo, tgtInfo, objectType);

                    // Package F98780R (même extraction que Lire .h) : différences fichier par fichier.
                    var pkg = await ComparePackages(srcConn, srcSchema, tgtConn, tgtSchema, name, ctx.RequestAborted);
                    cmp = SpecReader.MergePackage(cmp, pkg);

                    codes.Add(cmp.Code);
                    results.Add(new
                    {
                        objectName = name, objectType, objectDesc,
                        result = cmp.Code, resultLabel = cmp.Label,
                        source = new { environment = srcSchema, user = srcInfo.User, date = srcInfo.Date, time = srcInfo.Time, recordCount = srcSpec.RecordCount, blobSize = srcSpec.BlobSize },
                        target = new { environment = tgtSchema, user = tgtInfo.User, date = tgtInfo.Date, time = tgtInfo.Time, recordCount = tgtSpec.RecordCount, blobSize = tgtSpec.BlobSize },
                        differences = cmp.Summary,
                        package = new { status = pkg.Status, label = pkg.Label, identical = pkg.Identical, files = pkg.Files }
                    });
                }
                catch (Exception objEx) when (objEx is not OperationCanceledException)
                {
                    codes.Add("!");
                    results.Add(new
                    {
                        objectName = name, objectType = "", objectDesc = "",
                        result = "!", resultLabel = "Erreur",
                        source = new { environment = srcSchema, user = "", date = "", time = "", recordCount = 0, blobSize = 0L },
                        target = new { environment = tgtSchema, user = "", date = "", time = "", recordCount = 0, blobSize = 0L },
                        differences = objEx.Message,
                        package = (object?)null
                    });
                }
            }

            int Count(string code) => codes.Count(c => c == code);
            return Results.Ok(new
            {
                source = srcSchema, target = tgtSchema,
                totalObjects = results.Count,
                identical = Count("="), different = Count("≠"), moreRecent = Count("↑"), missing = Count("?"),
                notApplicable = Count("-"), conforme = Count("✓"), errors = Count("!"),
                data = results
            });
        });

        // Chargement commun à /package-diff et /ai-analysis : fichier `path` du package F98780R côté source et cible.
        async Task<(IResult? Error, FileDiff? Diff)> LoadFileDiff(HttpContext ctx, ConnectionStore store, PackageDiffRequest? body)
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Object) || string.IsNullOrWhiteSpace(body.Source) || string.IsNullOrWhiteSpace(body.Target) || string.IsNullOrWhiteSpace(body.Path))
                return (Results.BadRequest(new { error = "Paramètres object, source, target et path requis." }), null);
            var objName = body.Object.Trim();
            if (!ObjectNameRegex().IsMatch(objName))
                return (Results.BadRequest(new { error = "Nom d'objet invalide." }), null);
            if (!environments.TryGetValue(body.Source, out var srcConnName) || !store.TryGet(srcConnName, out var srcCfg) ||
                !environments.TryGetValue(body.Target, out var tgtConnName) || !store.TryGet(tgtConnName, out var tgtCfg))
                return (Results.BadRequest(new { error = "Environnement source ou cible inconnu." }), null);
            if (!srcCfg.HasCredentials || !tgtCfg.HasCredentials)
                return (Results.BadRequest(new { error = "UserId / Password manquants (secrets.json)." }), null);
            var srcSchema = environments.Keys.First(k => k.Equals(body.Source, StringComparison.OrdinalIgnoreCase));
            var tgtSchema = environments.Keys.First(k => k.Equals(body.Target, StringComparison.OrdinalIgnoreCase));

            try
            {
                IReadOnlyList<PackageFile>? src, tgt;
                await using (var c = await OracleErrors.OpenAsync(srcCfg, ctx)) src = await LoadPackage(c, srcSchema, objName, ctx.RequestAborted);
                await using (var c = await OracleErrors.OpenAsync(tgtCfg, ctx)) tgt = await LoadPackage(c, tgtSchema, objName, ctx.RequestAborted);
                var s = src is null ? null : JdePackage.Find(src, body.Path);
                var t = tgt is null ? null : JdePackage.Find(tgt, body.Path);
                if (s is null && t is null)
                    return (Results.NotFound(new { error = $"{body.Path} absent des deux packages." }), null);

                var binary = (s is not null && !JdePackage.IsText(s.Content)) || (t is not null && !JdePackage.IsText(t.Content));
                var srcText = s is null || binary ? "" : JdePackage.Decode(s.Content);
                var tgtText = t is null || binary ? "" : JdePackage.Decode(t.Content);
                return (null, new FileDiff(objName, srcSchema, tgtSchema, body.Path, s, t, binary, srcText, tgtText,
                    binary ? [] : LineDiff.Generate(srcText, tgtText)));
            }
            catch (InvalidDataException)
            {
                return (Results.Json(new { error = "Le BLOB lu n'est pas une archive ZIP valide." }, statusCode: 500), null);
            }
            catch (PackageLimitException ex)
            {
                return (Results.Json(new { error = ex.Message }, statusCode: 413), null);
            }
        }

        // Diff d'un fichier du package F98780R entre source et cible (détail à la demande).
        group.MapPost("/package-diff", async (HttpContext ctx, ConnectionStore store, AzureOpenAiClient ai, PackageDiffRequest? body) =>
        {
            var (error, d) = await LoadFileDiff(ctx, store, body);
            if (error is not null) return error;
            var screenLines = LineDiff.ScreenLines(d!.Lines);
            var bigGap = !d.Binary && screenLines > opt.AiDiffLineThreshold;
            var textDiff = !d.Binary && d.Lines.Count > 0;
            return Results.Ok(new
            {
                path = d.Path, binary = d.Binary,
                identical = d.Binary ? d.Src?.Sha256 == d.Tgt?.Sha256 : d.SrcText == d.TgtText,
                srcSize = d.Src?.Size ?? 0, tgtSize = d.Tgt?.Size ?? 0,
                srcLineCount = d.Src is null || d.Binary ? 0 : d.SrcText.Split('\n').Length,
                tgtLineCount = d.Tgt is null || d.Binary ? 0 : d.TgtText.Split('\n').Length,
                diff = d.Lines,
                screenLines,
                // Analyse IA proposée quand l'écart dépasse le seuil (en lignes affichées).
                aiAvailable = ai.Enabled && bigGap,
                // Texte à copier/coller dans Copilot (application Windows) : dès qu'il y a un écart, sans seuil.
                copilotPrompt = textDiff ? AiPrompt.ForChat(d, opt.CopilotMaxChars) : null
            });
        });

        // Analyse IA d'un écart : le diff est recalculé côté serveur (aucun texte libre transmis par le navigateur).
        group.MapPost("/ai-analysis", async (HttpContext ctx, ConnectionStore store, AzureOpenAiClient ai, PackageDiffRequest? body) =>
        {
            if (!ai.Enabled)
                return Results.Json(new { error = "Analyse IA non configurée (section AzureOpenAI)." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            var (error, d) = await LoadFileDiff(ctx, store, body);
            if (error is not null) return error;
            if (d!.Binary)
                return Results.BadRequest(new { error = "Fichier binaire : analyse IA impossible." });
            var screenLines = LineDiff.ScreenLines(d.Lines);
            if (screenLines <= opt.AiDiffLineThreshold)
                return Results.BadRequest(new { error = $"Écart trop faible pour une analyse IA ({screenLines} ligne(s))." });

            logger.LogInformation("Analyse IA demandée par {User} : {Object} {Path} {Source}->{Target} ({Lines} lignes de diff).",
                ctx.User.Identity?.Name, d.Object, d.Path, d.Source, d.Target, screenLines);
            try
            {
                var analysis = await ai.ChatAsync(AiPrompt.System, AiPrompt.User(d, ai.Options.MaxInputChars), ctx.RequestAborted);
                return Results.Ok(new { analysis });
            }
            catch (AiException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
            }
        });

        return app;
    }

    // En-tête X-JDE-Connection, puis cookie JDE-Connection, puis ?connection=, puis DefaultConnection.
    public static string ResolveConnection(HttpRequest req, ConnectionStore store, string? defaultConnection)
    {
        if (req.Headers.TryGetValue("X-JDE-Connection", out var header) && !string.IsNullOrWhiteSpace(header))
            return header.ToString();
        if (req.Cookies.TryGetValue("JDE-Connection", out var cookie) && !string.IsNullOrWhiteSpace(cookie) && store.All.ContainsKey(cookie))
            return cookie;
        if (req.Query.TryGetValue("connection", out var query) && !string.IsNullOrWhiteSpace(query))
            return query.ToString();
        if (!string.IsNullOrWhiteSpace(defaultConnection) && store.All.ContainsKey(defaultConnection))
            return defaultConnection;
        return store.All.Keys.FirstOrDefault() ?? string.Empty;
    }

    private static string SystemSchema(OracleConnectionConfig cfg)
        => string.IsNullOrWhiteSpace(cfg.SystemSchema) ? cfg.DefaultSchema : cfg.SystemSchema;

    private static string Str(OracleDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetValue(i).ToString()!.Trim();
}
