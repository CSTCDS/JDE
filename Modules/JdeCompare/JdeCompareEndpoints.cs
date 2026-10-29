using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;
using Utils.Colas.Infrastructure;

namespace Utils.Colas.Modules.JdeCompare;

public sealed class JdeCompareOptions
{
    public string ObjectLibrarianSchema { get; set; } = "OL920";
    public string ObjectBrowserPath { get; set; } = @"Tools\ObjectBrowser\ObjectBrowser.exe";
    public int MaxObjects { get; set; } = 500;
    public int CommandTimeoutSeconds { get; set; } = 60;
    public int ObjectBrowserTimeoutSeconds { get; set; } = 120;
}

public sealed record CompareRequest(string? Source, string? Target, string[]? Objects);
public sealed record OBCompareRequest(string? Object, string? Source, string? Target);
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
        var env = app.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
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
        var obExePath = Path.IsPathRooted(opt.ObjectBrowserPath) ? opt.ObjectBrowserPath : Path.Combine(env.ContentRootPath, opt.ObjectBrowserPath);
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

        // Diff d'un fichier du package F98780R entre source et cible (détail à la demande).
        group.MapPost("/package-diff", async (HttpContext ctx, ConnectionStore store, PackageDiffRequest? body) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Object) || string.IsNullOrWhiteSpace(body.Source) || string.IsNullOrWhiteSpace(body.Target) || string.IsNullOrWhiteSpace(body.Path))
                return Results.BadRequest(new { error = "Paramètres object, source, target et path requis." });
            var objName = body.Object.Trim();
            if (!ObjectNameRegex().IsMatch(objName))
                return Results.BadRequest(new { error = "Nom d'objet invalide." });
            if (!environments.TryGetValue(body.Source, out var srcConnName) || !store.TryGet(srcConnName, out var srcCfg) ||
                !environments.TryGetValue(body.Target, out var tgtConnName) || !store.TryGet(tgtConnName, out var tgtCfg))
                return Results.BadRequest(new { error = "Environnement source ou cible inconnu." });
            if (!srcCfg.HasCredentials || !tgtCfg.HasCredentials)
                return Results.BadRequest(new { error = "UserId / Password manquants (secrets.json)." });
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
                    return Results.NotFound(new { error = $"{body.Path} absent des deux packages." });

                var binary = (s is not null && !JdePackage.IsText(s.Content)) || (t is not null && !JdePackage.IsText(t.Content));
                if (binary)
                    return Results.Ok(new
                    {
                        path = body.Path, binary, identical = s?.Sha256 == t?.Sha256,
                        srcSize = s?.Size ?? 0, tgtSize = t?.Size ?? 0, diff = Array.Empty<object>()
                    });
                var srcText = s is null ? "" : JdePackage.Decode(s.Content);
                var tgtText = t is null ? "" : JdePackage.Decode(t.Content);
                return Results.Ok(new
                {
                    path = body.Path, binary, identical = srcText == tgtText,
                    srcSize = s?.Size ?? 0, tgtSize = t?.Size ?? 0,
                    srcLineCount = s is null ? 0 : srcText.Split('\n').Length,
                    tgtLineCount = t is null ? 0 : tgtText.Split('\n').Length,
                    diff = LineDiff.Generate(srcText, tgtText)
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
        });

        // Comparaison Event Rules via ObjectBrowser.exe (export texte source / cible puis diff)
        group.MapPost("/ob-compare", async (HttpContext ctx, OBCompareRequest? body) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Object) || string.IsNullOrWhiteSpace(body.Source) || string.IsNullOrWhiteSpace(body.Target))
                return Results.BadRequest(new { error = "Paramètres object, source et target requis." });
            var objName = body.Object.Trim();
            if (!ObjectNameRegex().IsMatch(objName))
                return Results.BadRequest(new { error = "Nom d'objet invalide." });
            var src = environments.Keys.FirstOrDefault(k => k.Equals(body.Source, StringComparison.OrdinalIgnoreCase));
            var tgt = environments.Keys.FirstOrDefault(k => k.Equals(body.Target, StringComparison.OrdinalIgnoreCase));
            if (src is null || tgt is null)
                return Results.BadRequest(new { error = "Environnement source ou cible inconnu." });
            if (!File.Exists(obExePath))
                return Results.Ok(new { success = false, error = $"ObjectBrowser.exe introuvable ({obExePath})." });

            var tempDir = Path.Combine(Path.GetTempPath(), "Utils.Colas_OB", Guid.NewGuid().ToString("N"));
            var srcDir = Path.Combine(tempDir, "source");
            var tgtDir = Path.Combine(tempDir, "target");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(tgtDir);
            try
            {
                var timeout = TimeSpan.FromSeconds(opt.ObjectBrowserTimeoutSeconds);
                var srcResult = await RunObjectBrowser(obExePath, objName, src, srcDir, timeout, ctx.RequestAborted);
                var tgtResult = await RunObjectBrowser(obExePath, objName, tgt, tgtDir, timeout, ctx.RequestAborted);
                if (!string.IsNullOrWhiteSpace(srcResult) && !string.IsNullOrWhiteSpace(tgtResult))
                    return Results.Ok(new { success = false, error = $"Source: {srcResult} | Cible: {tgtResult}" });

                var srcFiles = Directory.GetFiles(srcDir, "*.txt", SearchOption.AllDirectories);
                var tgtFiles = Directory.GetFiles(tgtDir, "*.txt", SearchOption.AllDirectories);
                var srcContent = srcFiles.Length > 0 ? await File.ReadAllTextAsync(srcFiles[0], ctx.RequestAborted) : "";
                var tgtContent = tgtFiles.Length > 0 ? await File.ReadAllTextAsync(tgtFiles[0], ctx.RequestAborted) : "";

                return Results.Ok(new
                {
                    success = true, objectName = objName, source = src, target = tgt,
                    srcFile = srcFiles.Length > 0 ? Path.GetFileName(srcFiles[0]) : "",
                    tgtFile = tgtFiles.Length > 0 ? Path.GetFileName(tgtFiles[0]) : "",
                    srcLineCount = srcContent.Split('\n').Length,
                    tgtLineCount = tgtContent.Split('\n').Length,
                    diff = LineDiff.Generate(srcContent, tgtContent)
                });
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        });

        return app;
    }

    // En-tête X-JDE-Connection, puis cookie JDE-Connection, puis ?connection=, puis DefaultConnection.
    public static string ResolveConnection(HttpRequest req, ConnectionStore store, string? defaultConnection)
    {
        if (req.Headers.TryGetValue("X-JDE-Connection", out var header) && !string.IsNullOrWhiteSpace(header))
            return header.ToString();
        if (req.Cookies.TryGetValue("JDE-Connection", out var cookie) && !string.IsNullOrWhiteSpace(cookie))
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

    private static async Task<string> RunObjectBrowser(string exePath, string objectName, string environment, string outputDir, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in new[] { $"-o{objectName}", $"-c{environment}", "-lC", $"-d{outputDir}\\", "-b1", "-mT", "-n", "-i" })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi);
        if (proc is null) return "Impossible de démarrer ObjectBrowser.exe";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = proc.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch (InvalidOperationException) { }
            return $"ObjectBrowser interrompu (délai {timeout.TotalSeconds:0}s dépassé ou requête annulée)";
        }
        await stdout;
        var error = await stderr;
        if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(error)) error = $"Exit code: {proc.ExitCode}";
        return error;
    }
}
